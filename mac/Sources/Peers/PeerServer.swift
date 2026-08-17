import Foundation
import Network
import CryptoKit
import ImageIO
import UniformTypeIdentifiers

/// The peer API: a deliberately tiny HTTP/1.1 responder on an NWListener,
/// bound ONLY to this machine's Tailscale address. Why hand-rolled: eight
/// fixed routes serving one trusted client, and pulling in SwiftNIO to serve
/// them is the Kestrel mistake in a different language (docs/MAC.md).
///
/// Security model: reachability = tailnet membership (WireGuard-encrypted,
/// invite-only), authorization = the shared PeerToken on every request.
/// Never binds 0.0.0.0; if the Tailscale IP can't be determined the server
/// simply doesn't start.
final class PeerServer: @unchecked Sendable {
    let boundAddress: String // "100.x.y.z:43117"

    private let listener: NWListener
    private let store: ShotStore
    private let token: String
    // Hashed once so every auth check compares digests, never token bytes —
    // constant time with respect to the supplied value, same effect as
    // FixedTimeEquals on Windows.
    private let tokenDigest: SHA256.Digest

    // NWConnection callbacks land on one queue while blocking request workers
    // wait on another — sharing a queue would deadlock the semaphore handshake.
    private let callbackQueue = DispatchQueue(label: "esgee.peers.io", attributes: .concurrent)
    private let workerQueue = DispatchQueue(label: "esgee.peers.work", attributes: .concurrent)

    // Non-nil only while a pairing window is open — the sole time POST /pair
    // is routed at all (closed, it falls through the token gate like any
    // unknown route). Set on the main actor, read on workers.
    private let stateLock = NSLock()
    private var pairing: PairingSession?
    private var stopped = false

    private init(listener: NWListener, store: ShotStore, token: String, bound: String) {
        self.listener = listener
        self.store = store
        self.token = token
        self.tokenDigest = SHA256.hash(data: Data(token.utf8))
        self.boundAddress = bound
    }

    /// Starts the server on the machine's Tailscale IPv4. Blocking (address
    /// discovery + bind) — never call on the main actor. Returns nil (and
    /// logs why) when tailscale is unavailable or the port is taken — never
    /// falls back to a wider bind.
    static func tryStart(store: ShotStore, token: String, port: Int) -> PeerServer? {
        if token.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty {
            Log.warn("peers: no PeerToken set; server not started")
            return nil
        }

        guard let ip = Tailnet.selfIPv4() else {
            Log.warn("peers: no Tailscale IPv4 found (is tailscale running?); server not started")
            return nil
        }

        guard port > 0, port <= 65535, let nwPort = NWEndpoint.Port(rawValue: UInt16(port)) else {
            Log.error("peers: failed to bind \(ip):\(port): invalid port")
            return nil
        }

        let params = NWParameters.tcp
        // Restart-after-crash must not wait out TIME_WAIT — the same behavior
        // TcpListener gives the Windows build for free.
        params.allowLocalEndpointReuse = true
        params.requiredLocalEndpoint = NWEndpoint.hostPort(host: NWEndpoint.Host(ip), port: nwPort)

        let listener: NWListener
        do {
            listener = try NWListener(using: params)
        } catch {
            Log.error("peers: failed to bind \(ip):\(port): \(error.localizedDescription)")
            return nil
        }

        let server = PeerServer(listener: listener, store: store, token: token,
                                bound: "\(ip):\(port)")
        if let failure = server.awaitReady() {
            listener.cancel()
            Log.error("peers: failed to bind \(ip):\(port): \(failure)")
            return nil
        }

        Log.info("peers: serving archive on http://\(server.boundAddress) (tailscale interface only)")
        return server
    }

    /// Opens the /pair route for this session's lifetime. Called by the
    /// pairing window on open; the session's own expiry/lockout/consumed
    /// state still gates every attempt, so a stale registration can't leak
    /// anything.
    func beginPairing(_ session: PairingSession) {
        stateLock.lock()
        pairing = session
        stateLock.unlock()

        let clock = DateFormatter()
        clock.locale = Locale(identifier: "en_US_POSIX")
        clock.dateFormat = "HH:mm:ss"
        Log.info("peers: pairing open — /pair answering until \(clock.string(from: session.expiresAt))")
    }

    /// Closes the /pair route (window closed, expired, or locked out).
    /// No-op unless the registered session is the one being ended.
    func endPairing(_ session: PairingSession) {
        stateLock.lock()
        let matched = pairing === session
        if matched { pairing = nil }
        stateLock.unlock()

        if matched { Log.info("peers: pairing closed — /pair disabled") }
    }

    func stop() {
        stateLock.lock()
        let already = stopped
        stopped = true
        stateLock.unlock()
        if already { return }

        listener.cancel()
        Log.info("peers: server stopped")
    }

    // ---- listener lifecycle -------------------------------------------------

    /// Waits for the bind to resolve. nil = ready; otherwise the reason, for
    /// the caller's error log. NWListener reports port collisions through the
    /// .failed state after start, not the initializer — hence the gate.
    private func awaitReady() -> String? {
        let gate = StartGate()
        listener.stateUpdateHandler = { state in
            switch state {
            case .ready: gate.resolve(nil)
            case .failed(let error): gate.resolve(error.localizedDescription)
            case .cancelled: gate.resolve("listener cancelled")
            default: break
            }
        }
        listener.newConnectionHandler = { [weak self] connection in
            self?.accept(connection)
        }
        listener.start(queue: callbackQueue)
        return gate.wait(seconds: 5)
    }

    private func accept(_ connection: NWConnection) {
        connection.start(queue: callbackQueue)
        workerQueue.async { [weak self] in
            self?.handle(connection)
        }
    }

    // ---- request handling -----------------------------------------------------

    private func handle(_ connection: NWConnection) {
        let remote = Self.describe(connection.endpoint)
        defer { connection.cancel() }

        guard let request = HttpRequest.read(using: { self.receiveChunk(connection) }) else {
            return
        }

        // Bodies are Content-Length framed only (docs/PROTOCOL.md
        // "Transport"). A chunked body would silently parse as empty and
        // fail downstream as a baffling 400 — refuse it by name instead.
        if let te = request.header("Transfer-Encoding"), te.lowercased().contains("chunked") {
            Log.warn("peers: 411 \(request.method) \(request.rawPath) from \(remote) (chunked body)")
            writeJson(connection, status: 411, ErrorDto(error: "content-length required"))
            return
        }

        // /pair is the one PIN-authenticated route — the caller doesn't have
        // the token yet; getting it is the point. Only while a window is
        // open, though: closed, the route must be indistinguishable from one
        // that doesn't exist (401 without a token, 404 with one), or any host
        // that can reach the port could fingerprint an esgee server and its
        // pairing state without holding a token.
        stateLock.lock()
        let pairingOpen = pairing?.active ?? false
        stateLock.unlock()
        if request.method == "POST" && request.path == "/pair" && pairingOpen {
            handlePair(connection, request, remote)
            return
        }

        guard authorized(request) else {
            Log.warn("peers: 401 \(request.method) \(request.rawPath) from \(remote)")
            writeJson(connection, status: 401, ErrorDto(error: "missing or wrong token"))
            return
        }

        do {
            try route(connection, request, remote)
        } catch {
            Log.warn("peers: connection from \(remote) failed: \(error.localizedDescription)")
        }
    }

    private func authorized(_ req: HttpRequest) -> Bool {
        guard let supplied = req.header(PeerProtocol.tokenHeader) else { return false }
        return SHA256.hash(data: Data(supplied.utf8)) == tokenDigest
    }

    private func route(_ connection: NWConnection, _ req: HttpRequest, _ remote: String) throws {
        let path = req.path

        if req.method == "GET" && path == "/ping" {
            Log.info("peers: /ping from \(remote)")
            writeJson(connection, status: 200, PingDto(
                app: "esgee", version: AppInfo.version, proto: PeerProtocol.proto,
                machine: AppInfo.machineName, captures: store.count(),
                capabilities: PeerCapability.advertised))
            return
        }

        if req.method == "GET" && path == "/recent" {
            let n = min(max(req.queryInt("n") ?? 200, 1), 1000)
            let shots = store.recent(limit: n)
            Log.info("peers: /recent n=\(n) -> \(shots.count) from \(remote)")
            writeJson(connection, status: 200, shots.map(Self.toDto))
            return
        }

        if req.method == "GET" && path == "/search" {
            let q = req.query("q") ?? ""
            var shots: [Shot]
            if q.trimmingCharacters(in: .whitespaces).isEmpty {
                shots = store.recent(limit: 200)
            } else {
                do {
                    shots = try store.search(matching: ShotStore.ftsQuery(q), limit: 200)
                } catch {
                    // Unbalanced quotes and other FTS syntax hiccups are the
                    // caller's typo, not a server fault.
                    Log.warn("peers: /search \"\(q)\" failed: \(error.localizedDescription)")
                    shots = []
                }
            }
            Log.info("peers: /search \"\(q)\" -> \(shots.count) from \(remote)")
            writeJson(connection, status: 200, shots.map(Self.toDto))
            return
        }

        if req.method == "GET", let metaId = Self.parseId(path, prefix: "/meta/") {
            guard let shot = store.byId(metaId) else {
                writeJson(connection, status: 404, ErrorDto(error: "no such shot"))
                return
            }
            let state = store.ocrState(id: shot.id)
            Log.info("peers: /meta/\(metaId) from \(remote)")
            var dto = Self.toDto(shot)
            dto.ocrText = state.text
            dto.ocrEngineVersion = state.engineVersion
            writeJson(connection, status: 200, dto)
            return
        }

        if req.method == "GET", let thumbId = Self.parseId(path, prefix: "/thumb/") {
            guard let shot = store.byId(thumbId),
                  FileManager.default.fileExists(atPath: shot.thumbPath) else {
                writeJson(connection, status: 404, ErrorDto(error: "no thumbnail"))
                return
            }
            let jpeg: Data
            do {
                // Decoded scaled-down (never the full bitmap) on this worker,
                // far from the main actor.
                jpeg = try Self.encodeThumb(path: shot.thumbPath)
            } catch {
                Log.warn("peers: thumb \(thumbId) failed: \(error.localizedDescription)")
                writeJson(connection, status: 500, ErrorDto(error: "thumbnail failed"))
                return
            }
            writeBytes(connection, status: 200, contentType: "image/jpeg", body: jpeg)
            return
        }

        if req.method == "GET", let fileId = Self.parseId(path, prefix: "/file/") {
            guard let shot = store.byId(fileId) else {
                writeJson(connection, status: 404, ErrorDto(error: "no such shot"))
                return
            }

            // ?alt=gif → the sibling GIF of a recording; ?alt=thumb → the
            // extracted preview frame beside an MP4. Both exist so a pulling
            // peer can reconstruct the full on-disk shape of a recording.
            let alt = req.query("alt")
            let filePath: String?
            if alt == "gif" {
                filePath = shot.gifPath
            } else if alt == "thumb" {
                filePath = shot.isVideo && FileManager.default.fileExists(atPath: shot.thumbPath)
                    ? shot.thumbPath : nil
            } else {
                filePath = shot.path
            }
            guard let filePath, FileManager.default.fileExists(atPath: filePath) else {
                writeJson(connection, status: 404, ErrorDto(error: "file missing"))
                return
            }

            let attrs = try? FileManager.default.attributesOfItem(atPath: filePath)
            let size = (attrs?[.size] as? UInt64) ?? 0
            let altSuffix = alt.map { "?alt=\($0)" } ?? ""
            Log.info("peers: /file/\(fileId)\(altSuffix) (\(size / 1024) KB) from \(remote)")
            writeFile(connection, path: filePath)
            return
        }

        if req.method == "POST" && path == "/ingest" {
            try handleIngest(connection, req, remote)
            return
        }

        // Unknown routes answer 404 with the error shape — never 5xx for
        // routing (docs/PROTOCOL.md "Versioning rules").
        writeJson(connection, status: 404, ErrorDto(error: "no such endpoint"))
    }

    /// POST /pair: redeem the on-screen PIN for the PeerToken. Only reached
    /// while a pairing window is open — closed, handle() lets the route fall
    /// through the ordinary token gate so it looks exactly like a route that
    /// doesn't exist. 401 "wrong pin" on a miss, 200 with the token exactly
    /// once. PIN and token values never reach the log — only outcomes do.
    private func handlePair(_ connection: NWConnection, _ req: HttpRequest, _ remote: String) {
        stateLock.lock()
        let session = pairing
        stateLock.unlock()

        guard let session, session.active else {
            // The window closed between handle()'s gate and here: answer
            // exactly like any other tokenless request so the race can't
            // leak the route's existence.
            Log.info("peers: /pair from \(remote) rejected — no pairing in progress")
            writeJson(connection, status: 401, ErrorDto(error: "missing or wrong token"))
            return
        }

        let pair = try? PeerProtocol.makeDecoder().decode(PairRequest.self, from: Data(req.body))
        guard let pair, !pair.pin.isEmpty else {
            writeJson(connection, status: 400, ErrorDto(error: "bad pair request"))
            return
        }

        let trimmed = pair.machine.trimmingCharacters(in: .whitespacesAndNewlines)
        let machine = trimmed.isEmpty ? remote : trimmed

        switch session.tryRedeem(pin: pair.pin, peerMachine: machine) {
        case .accepted:
            Log.info("peers: /pair from \(remote) ('\(machine)') accepted — PIN consumed, token issued")
            writeJson(connection, status: 200,
                      PairResult(token: token, machine: AppInfo.machineName))
        case .wrongPin:
            Log.warn("peers: /pair from \(remote) wrong PIN " +
                     "(\(session.failuresSoFar)/\(PairingSession.maxAttempts))")
            writeJson(connection, status: 401, ErrorDto(error: "wrong pin"))
        case .notActive:
            // Spent mid-request (consumed, expired, or locked out) — same
            // shape as the closed-window answer, for the same reason.
            Log.info("peers: /pair from \(remote) rejected — no pairing in progress")
            writeJson(connection, status: 401, ErrorDto(error: "missing or wrong token"))
        }
    }

    private func handleIngest(_ connection: NWConnection, _ req: HttpRequest, _ remote: String) throws {
        guard let parts = Multipart.parse(req) else {
            writeJson(connection, status: 400, ErrorDto(error: "expected multipart/form-data"))
            return
        }

        let metaPart = parts.first { $0.name == "meta" }
        let filePart = parts.first { $0.name == "file" }
        guard let metaPart, let filePart else {
            let got = parts.map(\.name).joined(separator: ", ")
            Log.warn("peers: ingest from \(remote) missing parts (got: \(got))")
            writeJson(connection, status: 400, ErrorDto(error: "need 'meta' and 'file' parts"))
            return
        }

        let meta: IngestMeta
        do {
            meta = try PeerProtocol.makeDecoder().decode(IngestMeta.self, from: Data(metaPart.body))
        } catch {
            writeJson(connection, status: 400,
                      ErrorDto(error: "bad meta json: \(error.localizedDescription)"))
            return
        }
        guard let takenAt = IsoStamp.parse(meta.takenAt) else {
            writeJson(connection, status: 400, ErrorDto(error: "bad meta"))
            return
        }

        let fileBytes = Data(filePart.body)
        let sha = Self.sha256Hex(fileBytes)
        guard sha.caseInsensitiveCompare(meta.sha256) == .orderedSame else {
            writeJson(connection, status: 400, ErrorDto(error: "sha256 mismatch"))
            return
        }

        // The extension is client-supplied wire data: validated down to a
        // short alphanumeric tail or replaced with the kind's default —
        // NSString.pathExtension happily returns spaces, quotes, and
        // arbitrarily long tails (docs/PROTOCOL.md; same rule as Windows).
        let rawExt = ((meta.fileName ?? "") as NSString).pathExtension
        let ext = ShotStore.safeExtension("." + rawExt, kind: meta.kind)

        // File before row, same as a local capture — drag-out hands paths to
        // the OS and the index must never point at bytes that aren't there.
        // Filed by the SENDER's wall clock (the offset inside taken_at), so
        // this tree and the sender's stay the same artifact across zones.
        let dest = store.planIngestPath(
            takenAt: takenAt,
            timeZone: IsoStamp.embeddedTimeZone(of: meta.takenAt) ?? .current,
            ext: ext)
        try fileBytes.write(to: URL(fileURLWithPath: dest))

        // Recordings arrive with their sidecar files so the local archive gets
        // the same on-disk shape a native recording has (thumb for the grid,
        // GIF as the paste artifact).
        let gifSibling = (dest as NSString).deletingPathExtension + ".gif"
        if let gif = parts.first(where: { $0.name == "gif" }) {
            try Data(gif.body).write(to: URL(fileURLWithPath: gifSibling))
        }
        if let thumb = parts.first(where: { $0.name == "thumb" }) {
            try Data(thumb.body).write(to: URL(fileURLWithPath: dest + ".png"))
        }

        // takenAtRaw travels verbatim — this machine did not mint it and must
        // not re-format another machine's UTC offset. A DB failure throws out
        // of this handler: no 200 is written and the connection drops, so the
        // sender's backoff loop retries instead of marking the shot pushed
        // (same shape as the C# HandleIngestAsync).
        let (shot, duplicate) = try store.ingest(
            path: dest, sha256: sha, takenAtRaw: meta.takenAt,
            width: meta.width, height: meta.height,
            kind: meta.kind, durationMs: meta.durationMs,
            ocrText: meta.ocrText, ocrEngineVersion: meta.ocrEngineVersion ?? "",
            origin: meta.origin ?? "")

        if duplicate {
            // Lost the race (or a retry of an already-landed push): keep the
            // original row, discard the fresh copy.
            try? FileManager.default.removeItem(atPath: dest)
            try? FileManager.default.removeItem(atPath: gifSibling)
            try? FileManager.default.removeItem(atPath: dest + ".png")
            Log.info("peers: ingest from \(remote) deduplicated (sha match, shot \(shot.id))")
        } else {
            let ocrNote = meta.ocrText == nil
                ? "pending"
                : "imported from sidecar [\(meta.ocrEngineVersion ?? "")]"
            Log.info("peers: ingested \(meta.kind) \(shot.width)x\(shot.height) from " +
                     "\(meta.origin ?? "?") -> \(shot.path) (id \(shot.id), ocr \(ocrNote))")
        }

        writeJson(connection, status: 200, IngestResult(id: shot.id, duplicate: duplicate))
    }

    // ---- wire helpers ---------------------------------------------------------

    private static func toDto(_ s: Shot) -> ShotDto {
        ShotDto(id: s.id, fileName: s.fileName, takenAt: s.takenAtRaw,
                width: s.width, height: s.height, sha256: s.sha256,
                kind: s.kind, durationMs: s.durationMs, origin: s.origin,
                hasGif: s.gifPath != nil)
    }

    private static func parseId(_ path: String, prefix: String) -> Int64? {
        guard path.hasPrefix(prefix) else { return nil }
        return Int64(path.dropFirst(prefix.count))
    }

    private static func sha256Hex(_ data: Data) -> String {
        SHA256.hash(data: data).map { String(format: "%02X", $0) }.joined()
    }

    private static func describe(_ endpoint: NWEndpoint) -> String {
        if case .hostPort(let host, let port) = endpoint {
            return "\(host):\(port)"
        }
        return "?"
    }

    /// Small JPEG for grid tiles. kCGImageSourceThumbnailMaxPixelSize caps the
    /// LONGER side, but the contract is 448 px WIDE (matching the Windows
    /// DecodePixelWidth) — so a portrait capture scales the cap up to keep the
    /// width at 448.
    private static func encodeThumb(path: String) throws -> Data {
        guard let source = CGImageSourceCreateWithURL(URL(fileURLWithPath: path) as CFURL, nil) else {
            throw ThumbError("could not open \(path)")
        }

        var maxPixel = 448.0
        if let props = CGImageSourceCopyPropertiesAtIndex(source, 0, nil) as? [CFString: Any],
           let w = (props[kCGImagePropertyPixelWidth] as? NSNumber)?.doubleValue,
           let h = (props[kCGImagePropertyPixelHeight] as? NSNumber)?.doubleValue,
           w > 0, h > w {
            maxPixel = (448.0 * h / w).rounded()
        }

        let options: [CFString: Any] = [
            kCGImageSourceCreateThumbnailFromImageAlways: true,
            kCGImageSourceCreateThumbnailWithTransform: true,
            kCGImageSourceThumbnailMaxPixelSize: maxPixel,
            kCGImageSourceShouldCacheImmediately: true,
        ]
        guard let image = CGImageSourceCreateThumbnailAtIndex(source, 0, options as CFDictionary) else {
            throw ThumbError("thumbnail decode failed for \(path)")
        }

        let out = NSMutableData()
        guard let dest = CGImageDestinationCreateWithData(out as CFMutableData,
                                                          UTType.jpeg.identifier as CFString, 1, nil) else {
            throw ThumbError("jpeg destination failed")
        }
        let jpegOptions: [CFString: Any] = [kCGImageDestinationLossyCompressionQuality: 0.8]
        CGImageDestinationAddImage(dest, image, jpegOptions as CFDictionary)
        guard CGImageDestinationFinalize(dest) else {
            throw ThumbError("jpeg encode failed for \(path)")
        }
        return out as Data
    }

    // ---- HTTP plumbing ----------------------------------------------------------

    /// One blocking read off the connection. nil = EOF, error, or the 30 s
    /// receive guard firing — HttpRequest.read treats all three as end of
    /// stream, and the guard also cancels so a stalled sender can't pin a
    /// worker forever.
    private func receiveChunk(_ connection: NWConnection, timeout: TimeInterval = 30) -> Data? {
        let gate = Handoff<Data?>()
        connection.receive(minimumIncompleteLength: 1, maximumLength: 64 * 1024) { data, _, _, _ in
            if let data, !data.isEmpty {
                gate.resolve(data)
            } else {
                gate.resolve(nil)
            }
        }
        guard let outcome = gate.wait(seconds: timeout) else {
            connection.cancel()
            return nil
        }
        return outcome
    }

    /// One blocking send, 60 s guard. False = the connection is dead; callers
    /// abandon the response — every response already says Connection: close.
    private func send(_ connection: NWConnection, _ data: Data, timeout: TimeInterval = 60) -> Bool {
        let gate = Handoff<Bool>()
        connection.send(content: data, completion: .contentProcessed { error in
            gate.resolve(error == nil)
        })
        guard let ok = gate.wait(seconds: timeout), ok else {
            connection.cancel()
            return false
        }
        return true
    }

    private func writeJson<T: Encodable>(_ connection: NWConnection, status: Int, _ body: T) {
        let data: Data
        do {
            data = try PeerProtocol.makeEncoder().encode(body)
        } catch {
            Log.error("peers: response encode failed: \(error.localizedDescription)")
            return
        }
        writeBytes(connection, status: status,
                   contentType: "application/json; charset=utf-8", body: data)
    }

    private func writeBytes(_ connection: NWConnection, status: Int,
                            contentType: String, body: Data) {
        var payload = Data(head(status: status, contentType: contentType,
                                length: Int64(body.count)).utf8)
        payload.append(body)
        _ = send(connection, payload)
    }

    /// Streams a file in 256 KB chunks — a full-screen recording must never
    /// be buffered whole just to cross the tailnet.
    private func writeFile(_ connection: NWConnection, path: String) {
        guard let handle = FileHandle(forReadingAtPath: path) else {
            writeJson(connection, status: 404, ErrorDto(error: "file missing"))
            return
        }
        defer { try? handle.close() }

        let attrs = try? FileManager.default.attributesOfItem(atPath: path)
        let size = (attrs?[.size] as? UInt64) ?? 0
        let header = head(status: 200, contentType: Self.contentType(forPath: path),
                          length: Int64(size))
        guard send(connection, Data(header.utf8)) else { return }

        while true {
            guard let chunk = try? handle.read(upToCount: 256 * 1024), !chunk.isEmpty else {
                break
            }
            if !send(connection, chunk) { return }
        }
    }

    private func head(status: Int, contentType: String, length: Int64) -> String {
        "HTTP/1.1 \(status) \(Self.reason(status))\r\n" +
        "Content-Type: \(contentType)\r\n" +
        "Content-Length: \(length)\r\n" +
        "Connection: close\r\n\r\n"
    }

    private static func contentType(forPath path: String) -> String {
        switch (path as NSString).pathExtension.lowercased() {
        case "png": return "image/png"
        case "gif": return "image/gif"
        case "mp4": return "video/mp4"
        case "jpg", "jpeg": return "image/jpeg"
        default: return "application/octet-stream"
        }
    }

    private static func reason(_ status: Int) -> String {
        switch status {
        case 200: return "OK"
        case 400: return "Bad Request"
        case 401: return "Unauthorized"
        case 404: return "Not Found"
        case 411: return "Length Required"
        default: return "Error"
        }
    }
}

/// Thumbnail failures carry their reason into the warn log; LocalizedError so
/// `localizedDescription` prints the message, not a generic Foundation shrug.
private struct ThumbError: LocalizedError {
    let message: String
    init(_ message: String) { self.message = message }
    var errorDescription: String? { message }
}

// ---- synchronization helpers ------------------------------------------------

/// Bridges one Network.framework callback to a blocking worker: resolve once
/// from the callback queue, wait (with deadline) from the worker. First
/// resolution wins; late callbacks after a timeout are dropped instead of
/// signaling a semaphore nobody holds.
private final class Handoff<Value>: @unchecked Sendable {
    private let sem = DispatchSemaphore(value: 0)
    private let lock = NSLock()
    private var value: Value?
    private var resolved = false

    func resolve(_ v: Value) {
        lock.lock()
        if !resolved {
            resolved = true
            value = v
            sem.signal()
        }
        lock.unlock()
    }

    /// nil = deadline passed with no resolution.
    func wait(seconds: TimeInterval) -> Value? {
        guard sem.wait(timeout: .now() + seconds) == .success else { return nil }
        lock.lock()
        defer { lock.unlock() }
        return value
    }
}

/// The bind handshake: NWListener only reveals port collisions through its
/// state stream, and tryStart's contract is synchronous success-or-nil.
private final class StartGate: @unchecked Sendable {
    private let sem = DispatchSemaphore(value: 0)
    private let lock = NSLock()
    private var error: String?
    private var resolved = false

    /// nil = ready; anything else is the failure reason.
    func resolve(_ error: String?) {
        lock.lock()
        if !resolved {
            resolved = true
            self.error = error
            sem.signal()
        }
        lock.unlock()
    }

    func wait(seconds: TimeInterval) -> String? {
        guard sem.wait(timeout: .now() + seconds) == .success else {
            return "timed out waiting for bind"
        }
        lock.lock()
        defer { lock.unlock() }
        return error
    }
}
