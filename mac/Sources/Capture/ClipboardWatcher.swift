import AppKit
import CryptoKit

/// Rides whatever already puts an image on the pasteboard (⌘⇧⌃4 above all) —
/// zero new muscle memory, and everything downstream (save, shelf, drag-out)
/// is identical to what esgee's own hotkeys feed. NSPasteboard has no change
/// notification, so this polls changeCount every 500 ms; the compare is an
/// Int read and effectively free.
@MainActor
final class ClipboardWatcher {
    /// Fired on the main actor with encoded PNG bytes, the pixel size, and
    /// the moment the pasteboard changed.
    var onImage: (@MainActor (Data, PixelSize, Date) -> Void)?

    private var timer: Timer?
    private var lastChangeCount: Int
    private var ignoreUntil = Date.distantPast
    private var lastHash: String?
    private var lastAt = Date.distantPast

    init() {
        // Whatever is on the pasteboard at launch predates us; capturing it
        // would resurrect something the user copied hours ago.
        lastChangeCount = NSPasteboard.general.changeCount

        let timer = Timer(timeInterval: 0.5, repeats: true) { [weak self] _ in
            // The timer is scheduled on the main run loop, so the block runs
            // on the main thread; assumeIsolated states that, it doesn't hop.
            MainActor.assumeIsolated { self?.poll() }
        }
        timer.tolerance = 0.1
        // .common so polling keeps running while a menu or drag is tracking.
        RunLoop.main.add(timer, forMode: .common)
        self.timer = timer
    }

    /// Call immediately BEFORE esgee writes the pasteboard. Window-based
    /// (750 ms), deliberately not consumed on first hit: one write bumps
    /// changeCount more than once (clearContents and writeObjects each
    /// count), and a bare consume-once flag turned the second bump into a
    /// phantom duplicate capture on Windows. If the write throws and no
    /// change ever lands, the window just expires instead of swallowing the
    /// user's next real capture.
    func ignoreNextChange() {
        ignoreUntil = Date().addingTimeInterval(0.75)
    }

    func stop() {
        timer?.invalidate()
        timer = nil
    }

    private func poll() {
        let pasteboard = NSPasteboard.general
        let count = pasteboard.changeCount
        guard count != lastChangeCount else { return }
        lastChangeCount = count

        if Date() < ignoreUntil { return }

        guard let (png, size) = Self.readImage(pasteboard) else { return }

        // A single copy can bump changeCount several times as the source app
        // publishes each format. Collapse those by content.
        let hash = SHA256.hash(data: png).map { String(format: "%02X", $0) }.joined()
        let now = Date()
        if hash == lastHash && now.timeIntervalSince(lastAt) < 3 { return }
        lastHash = hash
        lastAt = now

        onImage?(png, size, now)
    }

    /// Prefers real PNG bytes — no re-encode, no color-space drift. TIFF is
    /// the fallback every AppKit image producer writes.
    private static func readImage(_ pasteboard: NSPasteboard) -> (Data, PixelSize)? {
        if let png = pasteboard.data(forType: .png), !png.isEmpty,
           let rep = NSBitmapImageRep(data: png) {
            return (png, PixelSize(width: rep.pixelsWide, height: rep.pixelsHigh))
        }
        if let tiff = pasteboard.data(forType: .tiff),
           let rep = NSBitmapImageRep(data: tiff),
           let png = rep.representation(using: .png, properties: [:]) {
            return (png, PixelSize(width: rep.pixelsWide, height: rep.pixelsHigh))
        }
        return nil // The pasteboard holds something, but not an image.
    }
}
