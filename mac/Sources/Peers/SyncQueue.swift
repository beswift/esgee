import Foundation

/// Background push of every new capture to SyncTargetPeer. Everything about
/// this class is designed to stay OUT of the capture path: enqueue is a
/// non-blocking stream write, the single worker owns all network I/O, and a
/// dead or offline target just means the queue drains later. The receiver
/// dedupes by sha256, so at-least-once delivery (retries, restarts) is
/// harmless.
///
/// Delivery ledger is the sync_pushed table; a startup backlog sweep enqueues
/// anything not yet pushed, so captures taken while the target was offline —
/// or before sync was enabled — catch up automatically.
final class SyncQueue: @unchecked Sendable {
    private static let backoff: [TimeInterval] = [5, 15, 60, 300, 900]

    private let store: ShotStore
    private let settings: Settings // snapshot; AppDelegate rebuilds us on change
    private let gate = NSLock()
    private let continuation: AsyncStream<Int64>.Continuation
    private var worker: Task<Void, Never>?
    private var pendingCount = 0
    private var offlineFlag = false

    /// The SyncTargetPeer setting verbatim — also the sync_pushed key.
    let target: String

    /// Approximate items not yet delivered — status display only.
    var pending: Int {
        gate.lock()
        defer { gate.unlock() }
        return pendingCount
    }

    var offline: Bool {
        gate.lock()
        defer { gate.unlock() }
        return offlineFlag
    }

    /// Fires on arbitrary threads — subscribers hop themselves.
    var onStateChanged: (@Sendable () -> Void)?

    init(store: ShotStore, settings: Settings) {
        self.store = store
        self.settings = settings
        self.target = settings.syncTargetPeer

        let (stream, continuation) = AsyncStream.makeStream(
            of: Int64.self, bufferingPolicy: .unbounded)
        self.continuation = continuation

        // Strong self on purpose: the pump is the object's lifetime, and
        // shutdown() ending the stream is what releases it — same shape as
        // the Windows Task field.
        worker = Task.detached(priority: .utility) { [self] in
            await pump(stream)
        }
    }

    /// Called from the capture pipeline. Non-blocking, never throws.
    func enqueue(_ shotId: Int64) {
        if case .enqueued = continuation.yield(shotId) {
            adjustPending(by: 1)
        }
    }

    /// Enqueue everything not yet pushed — run once at startup, off main
    /// (notPushed hits the DB and target-name parsing is string work only).
    func enqueueBacklog() {
        let backlog = store.notPushed(target: target,
                                      targetMachine: targetMachineName(), limit: 500)
        if backlog.isEmpty { return }
        Log.info("sync: backlog sweep found \(backlog.count) unpushed capture(s)")
        for shot in backlog { enqueue(shot.id) }
    }

    /// Cancel the worker; returns without waiting. In-flight items re-run on
    /// next launch via the backlog sweep — at-least-once is the contract.
    func shutdown() {
        continuation.finish()
        worker?.cancel()
    }

    /// "machine" from "machine", "host:port", or a full URL (the URL's host)
    /// — used to skip pushing a capture back to the machine it came from
    /// (that push would just bounce off the receiver's dedupe).
    private func targetMachineName() -> String {
        if let url = URL(string: target), let scheme = url.scheme,
           scheme == "http" || scheme == "https", let host = url.host {
            return host
        }
        if let colon = target.lastIndex(of: ":"), colon != target.startIndex {
            return String(target[..<colon])
        }
        return target
    }

    // ---- worker ---------------------------------------------------------------

    private enum SyncError: Error, LocalizedError {
        case unresolvable(String)

        var errorDescription: String? {
            switch self {
            case .unresolvable(let target):
                return "cannot resolve sync target '\(target)'"
            }
        }
    }

    private func pump(_ stream: AsyncStream<Int64>) async {
        var client: PeerClient?

        for await id in stream {
            var delivered = false
            var attempt = 0

            while !delivered && !Task.isCancelled {
                do {
                    if client == nil { client = connect() }
                    guard let client else { throw SyncError.unresolvable(target) }

                    delivered = try await pushOne(client, id: id)
                    if offline { setOffline(false) }
                } catch is CancellationError {
                    return
                } catch {
                    client = nil // re-resolve — the peer's IP/port may change

                    let wait = Self.backoff[min(attempt, Self.backoff.count - 1)]
                    if !offline {
                        // One line when we go offline, not one per retry.
                        Log.warn("sync: push to \(target) failed " +
                                 "(\(error.localizedDescription)); retrying with backoff")
                        setOffline(true)
                    }
                    attempt += 1
                    do {
                        try await Task.sleep(nanoseconds: UInt64(wait * 1_000_000_000))
                    } catch {
                        return // cancelled mid-backoff — shutting down
                    }
                }
            }

            adjustPending(by: -1)
            if Task.isCancelled { return }
        }
    }

    /// Resolve the target (tailnet machine name, host[:port], or a full URL —
    /// docs/PROTOCOL.md "Addressing") and build a client. Nil when the name
    /// isn't on the tailnet right now or the entry is malformed.
    private func connect() -> PeerClient? {
        guard let base = PeerClient.resolveTargetURL(target, defaultPort: settings.peerPort)
        else { return nil }
        return PeerClient(peer: PeerInfo(name: targetMachineName(), baseURL: base),
                          token: settings.peerToken)
    }

    /// True = delivered (or permanently skippable). Throws on transient
    /// failure so the caller's backoff loop retries.
    private func pushOne(_ client: PeerClient, id: Int64) async throws -> Bool {
        guard let shot = store.byId(id),
              FileManager.default.fileExists(atPath: shot.path) else {
            Log.warn("sync: shot \(id) vanished before push; skipping")
            store.markPushed(shotId: id, target: target)
            return true
        }

        // Give the local OCR a moment to finish so the sidecar carries text
        // and the receiver never re-OCRs. If it's genuinely stuck, send
        // without — the receiver leaves ocr_done=0 and its own sweep fills
        // the hole.
        var ocrText: String?
        var engine = ""
        if shot.kind == "image" {
            for _ in 0..<30 {
                let state = store.ocrState(id: id)
                if state.done {
                    ocrText = state.text ?? ""
                    engine = state.engineVersion
                    break
                }
                try await Task.sleep(nanoseconds: 1_000_000_000)
            }
        }

        let meta = IngestMeta(
            sha256: shot.sha256,
            takenAt: shot.takenAtRaw, // verbatim — the round-trip rule
            width: shot.width, height: shot.height,
            kind: shot.kind, durationMs: shot.durationMs,
            ocrText: ocrText,
            ocrEngineVersion: engine.isEmpty ? nil : engine,
            origin: shot.origin.isEmpty ? AppInfo.machineName : shot.origin,
            fileName: shot.fileName)

        let result = try await client.ingest(
            meta: meta, filePath: shot.path,
            gifPath: shot.gifPath,
            thumbPath: shot.isVideo && FileManager.default.fileExists(atPath: shot.thumbPath)
                ? shot.thumbPath : nil)

        store.markPushed(shotId: id, target: target)
        Log.info("sync: pushed shot \(id) to \(target) " +
                 "(remote id \(result.id)\(result.duplicate ? ", deduplicated" : ""))")
        return true
    }

    // ---- state ------------------------------------------------------------------

    private func adjustPending(by delta: Int) {
        gate.lock()
        pendingCount += delta
        let callback = onStateChanged
        gate.unlock()
        callback?()
    }

    private func setOffline(_ value: Bool) {
        gate.lock()
        offlineFlag = value
        let callback = onStateChanged
        gate.unlock()
        callback?()
    }
}
