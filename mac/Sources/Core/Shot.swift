import Foundation

/// Capture dimensions in device pixels. Kept integral on purpose — CGSize's
/// doubles invite half-pixel bugs into a pipeline whose whole job is exact
/// crops.
struct PixelSize: Sendable, Equatable {
    let width: Int
    let height: Int
}

/// One capture, already durable on disk by the time this exists. Mirrors
/// src/Esgee/Store/Shot.cs so the two codebases stay readable side by side.
/// `kind` is "image" (PNG) or "video" (MP4 recording — never produced here in
/// v1, but pulled/pushed ones are first-class). `origin` is "" for captures
/// taken on this machine, or the name of the machine a synced/pulled capture
/// originally came from.
struct Shot: Sendable, Equatable, Identifiable {
    let id: Int64
    /// Absolute path as stored in the index — a String, not a URL, because it
    /// is DB text first and an OS handle second.
    let path: String
    let takenAt: Date
    /// The ISO 8601 string exactly as the DB row carries it. The wire and the
    /// index always transport this raw form; `takenAt` is derived for display
    /// and never re-serialized (Date cannot round-trip the sender's UTC
    /// offset, and the offset is part of the artifact).
    let takenAtRaw: String
    let width: Int
    let height: Int
    let sha256: String
    let kind: String
    let durationMs: Int64
    let origin: String

    init(id: Int64, path: String, takenAt: Date, takenAtRaw: String,
         width: Int, height: Int, sha256: String,
         kind: String = "image", durationMs: Int64 = 0, origin: String = "") {
        self.id = id
        self.path = path
        self.takenAt = takenAt
        self.takenAtRaw = takenAtRaw
        self.width = width
        self.height = height
        self.sha256 = sha256
        self.kind = kind
        self.durationMs = durationMs
        self.origin = origin
    }

    var fileName: String { (path as NSString).lastPathComponent }

    var isVideo: Bool { kind == "video" }

    /// What thumbnails should decode. For videos this is the frame extracted
    /// next to the MP4 ("....mp4.png" — a shape no real screenshot filename
    /// can collide with).
    var thumbPath: String { isVideo ? path + ".png" : path }

    /// The sibling GIF, when the recording was short enough to get one.
    /// Checked live because the user may delete either file independently.
    var gifPath: String? {
        guard isVideo else { return nil }
        let gif = (path as NSString).deletingPathExtension + ".gif"
        return FileManager.default.fileExists(atPath: gif) ? gif : nil
    }

    /// "m:ss" under an hour, "h:mm:ss" over — same shape the Windows cards
    /// print, so a badge reads identically on both platforms.
    var durationText: String {
        let total = Int(durationMs / 1000)
        let h = total / 3600
        let m = (total % 3600) / 60
        let s = total % 60
        return h >= 1 ? String(format: "%d:%02d:%02d", h, m, s)
                      : String(format: "%d:%02d", m, s)
    }
}

/// Timestamp formatting shared by the store, the wire, and file naming.
///
/// The round-trip rule: a raw taken_at string is minted exactly once — at
/// capture time, by the machine that took the shot — and from then on is
/// carried verbatim through the DB, /meta, /ingest, and any number of hops.
/// Parsing produces a Date for display and sorting only. Re-formatting a
/// string this machine did not mint is a bug: it would silently rewrite
/// another machine's UTC offset.
enum IsoStamp {
    // DateFormatter is documented thread-safe since macOS 10.9; these are
    // shared to keep the capture path allocation-free.
    nonisolated(unsafe) private static let writer: DateFormatter = {
        // The shape C#'s round-trip "o" format emits: 7 fractional digits and
        // a numeric offset. Local time zone — same convention as Windows,
        // where taken_at carries the capturing machine's wall clock.
        let f = DateFormatter()
        f.locale = Locale(identifier: "en_US_POSIX")
        f.timeZone = TimeZone.current
        f.dateFormat = "yyyy-MM-dd'T'HH:mm:ss.SSSSSSSXXXXX"
        return f
    }()

    // Readers for the shapes that exist in the wild: Windows "o" (7 fractional
    // digits), millisecond fractions, and bare seconds with offset or Z.
    nonisolated(unsafe) private static let readers: [DateFormatter] = {
        ["yyyy-MM-dd'T'HH:mm:ss.SSSSSSSXXXXX",
         "yyyy-MM-dd'T'HH:mm:ss.SSSXXXXX",
         "yyyy-MM-dd'T'HH:mm:ssXXXXX"].map { pattern in
            let f = DateFormatter()
            f.locale = Locale(identifier: "en_US_POSIX")
            f.timeZone = TimeZone(secondsFromGMT: 0)
            f.dateFormat = pattern
            return f
        }
    }()

    nonisolated(unsafe) private static let stem: DateFormatter = {
        let f = DateFormatter()
        f.locale = Locale(identifier: "en_US_POSIX")
        f.timeZone = TimeZone.current
        f.dateFormat = "yyyy-MM-dd_HH-mm-ss"
        return f
    }()

    nonisolated(unsafe) private static let year: DateFormatter = {
        let f = DateFormatter()
        f.locale = Locale(identifier: "en_US_POSIX")
        f.timeZone = TimeZone.current
        f.dateFormat = "yyyy"
        return f
    }()

    nonisolated(unsafe) private static let month: DateFormatter = {
        let f = DateFormatter()
        f.locale = Locale(identifier: "en_US_POSIX")
        f.timeZone = TimeZone.current
        f.dateFormat = "MM"
        return f
    }()

    /// Mint the raw string for a capture taken on THIS machine, now.
    static func format(_ date: Date) -> String { writer.string(from: date) }

    /// nil on garbage. Callers decide the fallback; the store logs and pins
    /// unparseable rows to the epoch rather than dropping them.
    static func parse(_ s: String) -> Date? {
        for reader in readers {
            if let d = reader.date(from: s) { return d }
        }
        return nil
    }

    /// "yyyy-MM-dd_HH-mm-ss" in local time — the archive's file naming, byte
    /// identical to the Windows tree so a moved archive is indistinguishable.
    static func fileStem(_ date: Date) -> String { stem.string(from: date) }

    /// "yyyy" / "MM" partition folders, local time.
    static func yearFolder(_ date: Date) -> String { year.string(from: date) }
    static func monthFolder(_ date: Date) -> String { month.string(from: date) }
}
