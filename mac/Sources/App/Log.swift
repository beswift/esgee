import Foundation

/// Deliberately tiny. A menu-bar app with no window needs a paper trail.
/// The line format matches the Windows build exactly — "HH:mm:ss.fff LEVEL
/// msg" — so esgee.log reads the same on both platforms and the same eyes
/// can debug either.
enum Log {
    /// Windows appends forever; a Mac that sleeps instead of rebooting
    /// deserves a bound. One rolled sibling keeps yesterday reachable.
    private static let maxBytes: UInt64 = 5 * 1024 * 1024

    // NSLock is Sendable; the formatter is not, but it is documented
    // thread-safe since macOS 10.9 and only ever formats.
    nonisolated(unsafe) private static let gate = NSLock()
    nonisolated(unsafe) private static let clock: DateFormatter = {
        let f = DateFormatter()
        f.locale = Locale(identifier: "en_US_POSIX")
        f.dateFormat = "HH:mm:ss.SSS"
        return f
    }()

    /// Beside settings.json, mirroring the Windows layout: one folder holds
    /// everything that isn't the archive.
    static let fileURL: URL = {
        let dir = FileManager.default
            .urls(for: .applicationSupportDirectory, in: .userDomainMask)[0]
            .appendingPathComponent("esgee", isDirectory: true)
        try? FileManager.default.createDirectory(at: dir, withIntermediateDirectories: true)
        return dir.appendingPathComponent("esgee.log")
    }()

    static func info(_ msg: String) { write("INFO", msg) }
    static func warn(_ msg: String) { write("WARN", msg) }
    // Trailing space, exactly like the C# build, so columns align.
    static func error(_ msg: String) { write("ERR ", msg) }

    // Control characters plus the Unicode line/paragraph separators — the
    // full set a log viewer might treat as "this line ended". Mirrors the
    // C# build's IsLineBreaker (char.IsControl + U+2028/U+2029).
    private static func isLineBreaker(_ scalar: Unicode.Scalar) -> Bool {
        scalar.value < 0x20 || (scalar.value >= 0x7F && scalar.value <= 0x9F)
            || scalar.value == 0x2028 || scalar.value == 0x2029
    }

    private static func write(_ level: String, _ msg: String) {
        gate.lock()
        defer { gate.unlock() }

        // Client-supplied text (display names, search queries) rides inside
        // messages, and this file is what operators and agents reconstruct
        // history from — an embedded newline would forge whole audit lines,
        // so one event is always exactly one line (and control characters
        // can't play terminal tricks either). Same rule as the C# Log.Write.
        var msg = msg
        if msg.unicodeScalars.contains(where: isLineBreaker) {
            let space: Unicode.Scalar = " "
            var scalars = String.UnicodeScalarView()
            for s in msg.unicodeScalars {
                scalars.append(isLineBreaker(s) ? space : s)
            }
            msg = String(scalars)
        }

        guard let data = "\(clock.string(from: Date())) \(level) \(msg)\n"
            .data(using: .utf8) else { return }

        do {
            let fm = FileManager.default
            let path = fileURL.path

            // Roll before the write so a single line never splits files.
            if let attrs = try? fm.attributesOfItem(atPath: path),
               let size = attrs[.size] as? UInt64, size > maxBytes {
                let rolled = fileURL.deletingLastPathComponent()
                    .appendingPathComponent("esgee.log.1")
                try? fm.removeItem(at: rolled)
                try? fm.moveItem(at: fileURL, to: rolled)
            }

            if !fm.fileExists(atPath: path) {
                fm.createFile(atPath: path, contents: nil)
            }

            let handle = try FileHandle(forWritingTo: fileURL)
            defer { try? handle.close() }
            try handle.seekToEnd()
            try handle.write(contentsOf: data)
        } catch {
            // Logging must never take the app down.
        }
    }
}
