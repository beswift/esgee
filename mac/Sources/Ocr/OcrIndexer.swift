import Foundation
import Darwin
import Vision

/// Reads the text out of every capture and files it into FTS. This is what turns
/// a folder of thousands of near-identical PNGs into something you can find in —
/// "that screenshot with the 401 in it" instead of scrubbing thumbnails.
///
/// Uses the OCR engine already built into macOS: no model download, no network,
/// nothing leaves the machine. Mirrors src/Esgee/Ocr/OcrIndexer.cs: run once at
/// capture time, sweep the backlog at launch, mark failures done so a bad file
/// cannot wedge the queue.
final class OcrIndexer: @unchecked Sendable {

    /// "vision/<max supported VNRecognizeTextRequest revision>+<os build>",
    /// e.g. "vision/3+23F79". Recorded per shot and carried in sync sidecars,
    /// so a future better engine can re-OCR only the rows an older engine
    /// produced. Vision doesn't version itself; the request revision plus the
    /// OS build is the honest proxy — same convention as winocr/10.0.26200.0.
    static let engineVersion: String = {
        let revision = VNRecognizeTextRequest.supportedRevisions.max() ?? 3
        return "vision/\(revision)+\(osBuild())"
    }()

    /// Vision ships with the OS — there is no missing-language-pack failure
    /// mode here. Kept so callers read identically to the Windows build.
    var available: Bool { true }

    private let store: ShotStore

    // One serial worker owns all recognition. Captures enqueue from the main
    // actor and the backlog sweep enqueues in bulk; serializing here keeps
    // Vision's memory spike to one image at a time and keeps setOcr writes
    // from ever racing each other.
    private let worker = DispatchQueue(label: "esgee.ocr", qos: .utility)

    // Guarded by `gate`. Once stopped, queued items drain as no-ops — GCD has
    // no cancellation, and shutdown must return without waiting on Vision.
    private let gate = NSLock()
    private var stopped = false

    init(store: ShotStore) {
        self.store = store
    }

    /// Any thread. Videos never OCR — ingest already marks them done, and a
    /// frame grab here would index one arbitrary frame as if it were the clip.
    func enqueue(_ shot: Shot) {
        if shot.isVideo { return }
        gate.lock()
        let dead = stopped
        gate.unlock()
        if dead { return }

        worker.async { [weak self] in
            self?.process(shot)
        }
    }

    /// Picks up anything that was captured while OCR was off or the app was
    /// closed, so the index self-heals instead of developing permanent holes.
    func enqueueBacklog() {
        for shot in store.pendingOcr(limit: 500) {
            enqueue(shot)
        }
    }

    /// Best-effort stop: whatever Vision is chewing on finishes, everything
    /// still queued becomes a no-op. Returns immediately — quit must not wait
    /// on an OCR pass that can take seconds on a large capture.
    func shutdown() {
        gate.lock()
        stopped = true
        gate.unlock()
    }

    private func process(_ shot: Shot) {
        gate.lock()
        let dead = stopped
        gate.unlock()
        if dead { return }

        do {
            let text = try recognize(path: shot.path)
            store.setOcr(id: shot.id, text: text, engineVersion: Self.engineVersion)
        } catch {
            // Mark it done anyway — a file that can't be read won't start
            // working on the next pass, and retrying forever would wedge the
            // queue behind it. Empty engine version: nothing produced this
            // text, and recording one would exempt the row from a future
            // selective re-OCR.
            Log.warn("ocr failed for \(shot.path): \(error.localizedDescription)")
            store.setOcr(id: shot.id, text: "", engineVersion: "")
        }
    }

    private func recognize(path: String) throws -> String {
        // Read the bytes ourselves rather than handing Vision a URL: the file
        // was durable before the Shot existed, and a read failure here becomes
        // the same catchable error as a decode failure instead of a distinct
        // URL-loading path.
        let data = try Data(contentsOf: URL(fileURLWithPath: path))

        let request = VNRecognizeTextRequest()
        request.recognitionLevel = .accurate
        request.usesLanguageCorrection = true

        let handler = VNImageRequestHandler(data: data, options: [:])
        try handler.perform([request])

        let observations = request.results ?? []
        return observations
            .compactMap { $0.topCandidates(1).first?.string }
            .joined(separator: "\n")
    }

    /// "23F79"-style build tag via sysctl — the same string About This Mac
    /// shows in parentheses, and the only part of the OS version that moves
    /// on every update including security patches.
    private static func osBuild() -> String {
        var size = 0
        guard sysctlbyname("kern.osversion", nil, &size, nil, 0) == 0, size > 0 else {
            return "unknown"
        }
        var buffer = [CChar](repeating: 0, count: size)
        guard sysctlbyname("kern.osversion", &buffer, &size, nil, 0) == 0 else {
            return "unknown"
        }
        return String(cString: buffer)
    }
}
