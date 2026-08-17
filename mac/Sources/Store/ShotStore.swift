import Foundation
import CryptoKit
import SQLite3

/// Local to the store on purpose: callers catch `Error` and show empty
/// results; the message exists for the log line.
private struct StoreError: Error, CustomStringConvertible {
    let message: String
    init(_ message: String) { self.message = message }
    var description: String { message }
}

/// Everything durable: the PNG on disk plus a searchable index beside it.
/// One folder holds both so the whole archive is portable/syncable as a unit —
/// a `~/esgee` tree and a `%USERPROFILE%\esgee` tree are the same artifact
/// (identical schema, identical yyyy/MM layout, same additive migrations).
/// Direct libsqlite3, no ORM: the query surface is a dozen fixed statements
/// and a framework would only add a dependency to a tool that must stay
/// instant.
final class ShotStore: @unchecked Sendable {

    let root: URL

    // Captures arrive from a detached task while the OCR indexer writes from
    // its own pump. One connection handle is not safe under that, so every
    // statement funnels through this serial queue. Contention is negligible —
    // these are millisecond reads and writes.
    private let queue = DispatchQueue(label: "esgee.ShotStore")

    private var db: OpaquePointer?

    init(root: URL) throws {
        self.root = root
        try FileManager.default.createDirectory(at: root, withIntermediateDirectories: true)

        let path = root.appendingPathComponent("index.db").path
        var handle: OpaquePointer?
        let rc = sqlite3_open_v2(
            path, &handle,
            SQLITE_OPEN_READWRITE | SQLITE_OPEN_CREATE | SQLITE_OPEN_FULLMUTEX,
            nil)
        guard rc == SQLITE_OK, let opened = handle else {
            // sqlite allocates a handle even on failure; close it or leak.
            if let opened = handle { sqlite3_close_v2(opened) }
            throw StoreError("cannot open \(path) (sqlite rc \(rc))")
        }
        db = opened

        do {
            try migrate()
        } catch {
            sqlite3_close_v2(opened)
            db = nil
            throw error
        }
    }

    // MARK: - Schema

    private func migrate() throws {
        try exec("PRAGMA journal_mode=WAL;")

        try exec("""
            CREATE TABLE IF NOT EXISTS shots (
                id       INTEGER PRIMARY KEY AUTOINCREMENT,
                path     TEXT NOT NULL,
                taken_at TEXT NOT NULL,
                width    INTEGER NOT NULL,
                height   INTEGER NOT NULL,
                sha256   TEXT NOT NULL,
                ocr_text TEXT,
                ocr_done INTEGER NOT NULL DEFAULT 0
            );

            CREATE INDEX IF NOT EXISTS ix_shots_taken_at ON shots(taken_at DESC);
            CREATE INDEX IF NOT EXISTS ix_shots_sha      ON shots(sha256);
            CREATE INDEX IF NOT EXISTS ix_shots_ocr_todo ON shots(ocr_done) WHERE ocr_done = 0;
            """)

        // Full-text over OCR'd screen text — the thing that turns a pile of
        // thousands of PNGs into something you can actually find in. The
        // system libsqlite3 is expected to ship FTS5; verify rather than
        // assume, because a store that silently cannot search is worse than
        // a loud log line.
        if sqlite3_compileoption_used("ENABLE_FTS5") == 0 {
            Log.error("sqlite reports no FTS5 support — search will be unavailable")
        }
        do {
            try exec("""
                CREATE VIRTUAL TABLE IF NOT EXISTS shots_fts
                    USING fts5(ocr_text, content='shots', content_rowid='id');
                """)
        } catch {
            // Degrade, don't die: captures still land and sync without the
            // index; only search goes dark, and callers already treat search
            // errors as empty results.
            Log.error("shots_fts unavailable: \(error)")
        }

        // Additive migrations for pre-existing databases. ALTER TABLE ADD
        // COLUMN fails with "duplicate column name" once applied — that is
        // the idempotence.
        tryExec("ALTER TABLE shots ADD COLUMN kind TEXT NOT NULL DEFAULT 'image'")
        tryExec("ALTER TABLE shots ADD COLUMN duration_ms INTEGER NOT NULL DEFAULT 0")
        // Peer sync: where a capture originally came from ("" = this machine),
        // and which OCR engine produced ocr_text (the versioned-sidecar
        // pattern — lets a future engine upgrade re-OCR selectively instead
        // of blindly).
        tryExec("ALTER TABLE shots ADD COLUMN origin TEXT NOT NULL DEFAULT ''")
        tryExec("ALTER TABLE shots ADD COLUMN ocr_engine_version TEXT NOT NULL DEFAULT ''")
        // Which shots have been pushed to which sync target. New table =
        // additive; older app versions never touch it.
        tryExec("""
            CREATE TABLE IF NOT EXISTS sync_pushed (
                shot_id   INTEGER NOT NULL,
                target    TEXT NOT NULL,
                pushed_at TEXT NOT NULL,
                PRIMARY KEY (shot_id, target)
            )
            """)
    }

    // MARK: - Queries

    /// Quotes each term so user text can't hit FTS5 operator syntax (AND/OR/
    /// NEAR, dashes, colons) by accident. Byte-identical to the C# port — a
    /// search must mean the same thing locally, remotely, and on the wire.
    static func ftsQuery(_ raw: String) -> String {
        raw.split(separator: " ")
            .map { "\"" + $0.replacingOccurrences(of: "\"", with: "\"\"") + "\"*" }
            .joined(separator: " ")
    }

    /// Writes the PNG and records it. Returns once the file is on disk —
    /// callers depend on that, because drag-out hands out a real file path.
    /// Identical bytes arriving within a short window return the EXISTING
    /// shot: the clipboard echo of esgee's own capture can slip past the
    /// watcher's time-window guard on a slow machine, and content identity is
    /// the one dedup signal that can't mistime.
    func add(png: Data, size: PixelSize, takenAt: Date) throws -> Shot {
        let sha = Self.sha256Hex(png)

        let dir = root.appendingPathComponent(IsoStamp.yearFolder(takenAt))
                      .appendingPathComponent(IsoStamp.monthFolder(takenAt))
        try FileManager.default.createDirectory(at: dir, withIntermediateDirectories: true)

        return try queue.sync { () throws -> Shot in
            if let existing = try findRecentBySha(sha, near: takenAt, windowSeconds: 10) {
                Log.info("deduplicated identical capture (echo of shot \(existing.id))")
                return existing
            }

            let path = Self.uniquePath(
                dir.appendingPathComponent(IsoStamp.fileStem(takenAt) + ".png").path)
            // File before the row — a row pointing at nothing breaks drag-out,
            // a file with no row is just an orphan the index never shows.
            try png.write(to: URL(fileURLWithPath: path))

            // The one place local raws are minted (the taken_at round-trip
            // rule); everything downstream carries this string verbatim.
            let raw = IsoStamp.format(takenAt)

            let stmt = try prepare("""
                INSERT INTO shots (path, taken_at, width, height, sha256)
                VALUES (?, ?, ?, ?, ?);
                """)
            defer { sqlite3_finalize(stmt) }
            bindText(stmt, 1, path)
            bindText(stmt, 2, raw)
            _ = sqlite3_bind_int64(stmt, 3, Int64(size.width))
            _ = sqlite3_bind_int64(stmt, 4, Int64(size.height))
            bindText(stmt, 5, sha)
            try stepDone(stmt)
            let id = sqlite3_last_insert_rowid(db)

            return Shot(id: id, path: path, takenAt: takenAt, takenAtRaw: raw,
                        width: size.width, height: size.height, sha256: sha)
        }
    }

    /// Shots still awaiting OCR, oldest first.
    func pendingOcr(limit: Int = 25) -> [Shot] {
        queue.sync { () -> [Shot] in
            do {
                return try selectShots("""
                    SELECT id, path, taken_at, width, height, sha256, kind, duration_ms, origin
                    FROM shots WHERE ocr_done = 0 ORDER BY id LIMIT ?;
                    """) { stmt in
                    _ = sqlite3_bind_int64(stmt, 1, Int64(limit))
                }
            } catch {
                Log.error("pendingOcr failed: \(error)")
                return []
            }
        }
    }

    func setOcr(id: Int64, text: String, engineVersion: String = "") {
        queue.sync {
            do {
                try exec("BEGIN IMMEDIATE;")
                do {
                    let up = try prepare("""
                        UPDATE shots SET ocr_text = ?, ocr_done = 1, ocr_engine_version = ?
                        WHERE id = ?;
                        """)
                    defer { sqlite3_finalize(up) }
                    bindText(up, 1, text)
                    bindText(up, 2, engineVersion)
                    _ = sqlite3_bind_int64(up, 3, id)
                    try stepDone(up)

                    // External-content FTS: push the row in explicitly.
                    let fts = try prepare("INSERT INTO shots_fts(rowid, ocr_text) VALUES (?, ?);")
                    defer { sqlite3_finalize(fts) }
                    _ = sqlite3_bind_int64(fts, 1, id)
                    bindText(fts, 2, text)
                    try stepDone(fts)

                    try exec("COMMIT;")
                } catch {
                    tryExec("ROLLBACK;")
                    throw error
                }
            } catch {
                Log.error("setOcr(\(id)) failed: \(error)")
            }
        }
    }

    /// Throws on FTS5 syntax errors (an unbalanced quote mid-keystroke);
    /// every caller catches and shows empty results.
    func search(matching ftsQuery: String, limit: Int = 100) throws -> [Shot] {
        try queue.sync { () throws -> [Shot] in
            try selectShots("""
                SELECT s.id, s.path, s.taken_at, s.width, s.height, s.sha256, s.kind, s.duration_ms, s.origin
                FROM shots_fts f JOIN shots s ON s.id = f.rowid
                WHERE shots_fts MATCH ?
                ORDER BY rank LIMIT ?;
                """) { stmt in
                bindText(stmt, 1, ftsQuery)
                _ = sqlite3_bind_int64(stmt, 2, Int64(limit))
            }
        }
    }

    /// Most recent captures, newest first — the archive browser's default view.
    func recent(limit: Int = 100) -> [Shot] {
        queue.sync { () -> [Shot] in
            do {
                return try selectShots("""
                    SELECT id, path, taken_at, width, height, sha256, kind, duration_ms, origin
                    FROM shots ORDER BY id DESC LIMIT ?;
                    """) { stmt in
                    _ = sqlite3_bind_int64(stmt, 1, Int64(limit))
                }
            } catch {
                Log.error("recent failed: \(error)")
                return []
            }
        }
    }

    /// One row by id, or nil. The peer API's lookup primitive.
    func byId(_ id: Int64) -> Shot? {
        queue.sync { () -> Shot? in
            do {
                return try selectShots("""
                    SELECT id, path, taken_at, width, height, sha256, kind, duration_ms, origin
                    FROM shots WHERE id = ?;
                    """) { stmt in
                    _ = sqlite3_bind_int64(stmt, 1, id)
                }.first
            } catch {
                Log.error("byId(\(id)) failed: \(error)")
                return nil
            }
        }
    }

    /// Total captures — the /ping health number.
    func count() -> Int64 {
        queue.sync { () -> Int64 in
            do {
                let stmt = try prepare("SELECT COUNT(*) FROM shots;")
                defer { sqlite3_finalize(stmt) }
                guard sqlite3_step(stmt) == SQLITE_ROW else { return 0 }
                return sqlite3_column_int64(stmt, 0)
            } catch {
                Log.error("count failed: \(error)")
                return 0
            }
        }
    }

    /// OCR state for one shot: done flag, text, and the engine version that
    /// produced it — the payload of a sync sidecar.
    func ocrState(id: Int64) -> (done: Bool, text: String?, engineVersion: String) {
        queue.sync { () -> (done: Bool, text: String?, engineVersion: String) in
            do {
                let stmt = try prepare(
                    "SELECT ocr_done, ocr_text, ocr_engine_version FROM shots WHERE id = ?;")
                defer { sqlite3_finalize(stmt) }
                _ = sqlite3_bind_int64(stmt, 1, id)
                guard sqlite3_step(stmt) == SQLITE_ROW else { return (false, nil, "") }
                return (sqlite3_column_int64(stmt, 0) != 0,
                        columnText(stmt, 1),
                        columnText(stmt, 2) ?? "")
            } catch {
                Log.error("ocrState(\(id)) failed: \(error)")
                return (false, nil, "")
            }
        }
    }

    /// Files a capture that arrived from another machine (push sync or a
    /// manual "pull to this Mac"). The file is already at `path` inside this
    /// archive's tree. OCR text comes from the sender's sidecar — it is
    /// imported, never re-run here; a sidecar with no text on an image leaves
    /// ocr_done = 0 so the local backlog sweep fills the hole. Dedupe is
    /// global by content hash: the same capture pushed twice (retry, or
    /// pull-then-sync) lands exactly once. `takenAtRaw` is stored verbatim —
    /// the capturing machine minted it, and re-formatting would silently
    /// rewrite its UTC offset.
    ///
    /// Throws on a DB failure, exactly like the C# reference: the peer
    /// server must NOT answer 200 for a row that never landed (the sender
    /// would mark the shot pushed and never resend — permanent silent loss),
    /// and the pull path must flash a failure, not "local id 0".
    func ingest(path: String, sha256: String, takenAtRaw: String,
                width: Int, height: Int, kind: String, durationMs: Int64,
                ocrText: String?, ocrEngineVersion: String, origin: String)
        throws -> (shot: Shot, duplicate: Bool)
    {
        try queue.sync { () throws -> (shot: Shot, duplicate: Bool) in
            if let existing = try selectShots("""
                SELECT id, path, taken_at, width, height, sha256, kind, duration_ms, origin
                FROM shots WHERE sha256 = ? ORDER BY id DESC LIMIT 1;
                """, bind: { stmt in bindText(stmt, 1, sha256) }).first {
                return (existing, true)
            }

            // "No text yet" (nil) keeps an image pending; a video never
            // enters the OCR queue at all.
            let ocrDone = ocrText != nil || kind != "image"

            try exec("BEGIN IMMEDIATE;")
            var id: Int64 = 0
            do {
                let ins = try prepare("""
                    INSERT INTO shots (path, taken_at, width, height, sha256, kind,
                                       duration_ms, ocr_text, ocr_done, ocr_engine_version, origin)
                    VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?);
                    """)
                defer { sqlite3_finalize(ins) }
                bindText(ins, 1, path)
                bindText(ins, 2, takenAtRaw)
                _ = sqlite3_bind_int64(ins, 3, Int64(width))
                _ = sqlite3_bind_int64(ins, 4, Int64(height))
                bindText(ins, 5, sha256)
                bindText(ins, 6, kind)
                _ = sqlite3_bind_int64(ins, 7, durationMs)
                if let ocrText {
                    bindText(ins, 8, ocrText)
                } else {
                    _ = sqlite3_bind_null(ins, 8)
                }
                _ = sqlite3_bind_int64(ins, 9, ocrDone ? 1 : 0)
                bindText(ins, 10, ocrEngineVersion)
                bindText(ins, 11, origin)
                try stepDone(ins)
                id = sqlite3_last_insert_rowid(db)

                if let ocrText, !ocrText.isEmpty {
                    let fts = try prepare("INSERT INTO shots_fts(rowid, ocr_text) VALUES (?, ?);")
                    defer { sqlite3_finalize(fts) }
                    _ = sqlite3_bind_int64(fts, 1, id)
                    bindText(fts, 2, ocrText)
                    try stepDone(fts)
                }

                try exec("COMMIT;")
            } catch {
                tryExec("ROLLBACK;")
                throw error
            }

            return (Shot(id: id, path: path,
                         takenAt: parsedOrEpoch(takenAtRaw, id: id),
                         takenAtRaw: takenAtRaw,
                         width: width, height: height, sha256: sha256,
                         kind: kind, durationMs: durationMs, origin: origin), false)
        }
    }

    /// Ingest destinations take their extension from a client-supplied file
    /// name. Anything but a short alphanumeric extension — quotes, control
    /// characters, a hundred-char "extension" — would make the file write
    /// throw after the route can no longer answer 400, dropping the
    /// connection with no response. Those fall back to the kind's default
    /// instead (docs/PROTOCOL.md; byte-identical to the C# SafeExtension).
    static func safeExtension(_ ext: String?, kind: String) -> String {
        let fallback = kind == "video" ? ".mp4" : ".png"
        guard let ext, ext.count >= 2, ext.count <= 10, ext.hasPrefix(".") else {
            return fallback
        }
        for ch in ext.dropFirst() {
            guard ch.isASCII, ch.isLetter || ch.isNumber else { return fallback }
        }
        return ext
    }

    /// Picks a destination path inside this archive's yyyy/MM tree for an
    /// incoming file, creating the month folder. Caller writes the bytes.
    /// `ext` includes the dot. `timeZone` is the SENDER's embedded offset
    /// (IsoStamp.embeddedTimeZone) so the tree files by the capturing
    /// machine's wall clock — the C# reference formats the DateTimeOffset it
    /// parsed from taken_at, and both trees must name the same artifact
    /// identically (docs/MAC.md "Store").
    func planIngestPath(takenAt: Date, timeZone: TimeZone = .current, ext: String) -> String {
        let dir = root.appendingPathComponent(IsoStamp.yearFolder(takenAt, in: timeZone))
                      .appendingPathComponent(IsoStamp.monthFolder(takenAt, in: timeZone))
        try? FileManager.default.createDirectory(at: dir, withIntermediateDirectories: true)
        // The uniqueness probe runs under the gate so two concurrent ingests
        // cannot be handed the same path.
        return queue.sync {
            Self.uniquePath(dir.appendingPathComponent(
                IsoStamp.fileStem(takenAt, in: timeZone) + ext).path)
        }
    }

    /// Shots never pushed to `target`, oldest first — the startup backlog
    /// sweep. Excludes shots that ORIGINATED at the target (pushing those
    /// back would just bounce off its sha dedupe).
    func notPushed(target: String, targetMachine: String, limit: Int = 500) -> [Shot] {
        queue.sync { () -> [Shot] in
            do {
                return try selectShots("""
                    SELECT id, path, taken_at, width, height, sha256, kind, duration_ms, origin
                    FROM shots
                    WHERE id NOT IN (SELECT shot_id FROM sync_pushed WHERE target = ?)
                      AND origin != ?
                    ORDER BY id LIMIT ?;
                    """) { stmt in
                    bindText(stmt, 1, target)
                    bindText(stmt, 2, targetMachine)
                    _ = sqlite3_bind_int64(stmt, 3, Int64(limit))
                }
            } catch {
                Log.error("notPushed(\(target)) failed: \(error)")
                return []
            }
        }
    }

    func markPushed(shotId: Int64, target: String) {
        queue.sync {
            do {
                let stmt = try prepare("""
                    INSERT OR REPLACE INTO sync_pushed (shot_id, target, pushed_at)
                    VALUES (?, ?, ?);
                    """)
                defer { sqlite3_finalize(stmt) }
                _ = sqlite3_bind_int64(stmt, 1, shotId)
                bindText(stmt, 2, target)
                // Bookkeeping stamp, not a taken_at — the mint-once rule does
                // not apply; it just needs to read the same as Windows' "o".
                bindText(stmt, 3, IsoStamp.format(Date()))
                try stepDone(stmt)
            } catch {
                Log.error("markPushed(\(shotId), \(target)) failed: \(error)")
            }
        }
    }

    /// Cheap change token for live views: moves when rows are added, removed,
    /// or OCR completes. One scalar WAL read — safe to poll.
    func changeToken() -> String {
        queue.sync { () -> String in
            do {
                let stmt = try prepare("""
                    SELECT COALESCE(MAX(id),0) || ':' || COUNT(*) || ':' || COALESCE(SUM(ocr_done),0) FROM shots;
                    """)
                defer { sqlite3_finalize(stmt) }
                guard sqlite3_step(stmt) == SQLITE_ROW else { return "0:0:0" }
                return columnText(stmt, 0) ?? "0:0:0"
            } catch {
                Log.error("changeToken failed: \(error)")
                return "0:0:0"
            }
        }
    }

    /// Idempotent. Every call after this degrades to empty results — an app
    /// tearing down must never crash over a late query.
    func close() {
        queue.sync {
            if let db { sqlite3_close_v2(db) }
            db = nil
        }
    }

    // MARK: - Row plumbing (callers hold the queue; init runs pre-concurrency)

    /// Newest shot with this hash inside the window, if any. A row whose
    /// taken_at won't parse pins to the epoch and falls outside any sane
    /// window — degrading to "no echo" rather than failing the capture.
    private func findRecentBySha(_ sha: String, near takenAt: Date,
                                 windowSeconds: TimeInterval) throws -> Shot? {
        guard let candidate = try selectShots("""
            SELECT id, path, taken_at, width, height, sha256, kind, duration_ms, origin
            FROM shots WHERE sha256 = ? ORDER BY id DESC LIMIT 1;
            """, bind: { stmt in bindText(stmt, 1, sha) }).first
        else { return nil }
        return abs(candidate.takenAt.timeIntervalSince(takenAt)) <= windowSeconds
            ? candidate : nil
    }

    private func selectShots(_ sql: String, bind: (OpaquePointer) -> Void) throws -> [Shot] {
        let stmt = try prepare(sql)
        defer { sqlite3_finalize(stmt) }
        bind(stmt)
        var shots: [Shot] = []
        while true {
            switch sqlite3_step(stmt) {
            case SQLITE_ROW: shots.append(rowShot(stmt))
            case SQLITE_DONE: return shots
            default: throw StoreError(currentErrorMessage())
            }
        }
    }

    /// Column order everywhere: id, path, taken_at, width, height, sha256,
    /// kind, duration_ms, origin. The taken_at text lands in `takenAtRaw`
    /// untouched; the Date is derived for display and sorting only.
    private func rowShot(_ stmt: OpaquePointer) -> Shot {
        let id = sqlite3_column_int64(stmt, 0)
        let raw = columnText(stmt, 2) ?? ""
        return Shot(id: id,
                    path: columnText(stmt, 1) ?? "",
                    takenAt: parsedOrEpoch(raw, id: id),
                    takenAtRaw: raw,
                    width: Int(sqlite3_column_int64(stmt, 3)),
                    height: Int(sqlite3_column_int64(stmt, 4)),
                    sha256: columnText(stmt, 5) ?? "",
                    kind: columnText(stmt, 6) ?? "image",
                    durationMs: sqlite3_column_int64(stmt, 7),
                    origin: columnText(stmt, 8) ?? "")
    }

    /// An unparseable row is pinned to the epoch rather than dropped — a
    /// capture you can't sort is still a capture.
    private func parsedOrEpoch(_ raw: String, id: Int64) -> Date {
        if let parsed = IsoStamp.parse(raw) { return parsed }
        Log.warn("shot \(id): unparseable taken_at '\(raw)'; pinned to epoch")
        return Date(timeIntervalSince1970: 0)
    }

    // MARK: - sqlite plumbing

    private func handleOrThrow() throws -> OpaquePointer {
        guard let db else { throw StoreError("store is closed") }
        return db
    }

    private func currentErrorMessage() -> String {
        guard let db else { return "store is closed" }
        return String(cString: sqlite3_errmsg(db))
    }

    private func exec(_ sql: String) throws {
        let db = try handleOrThrow()
        guard sqlite3_exec(db, sql, nil, nil, nil) == SQLITE_OK else {
            throw StoreError(String(cString: sqlite3_errmsg(db)))
        }
    }

    /// Swallows failure on purpose: "duplicate column name" on an applied
    /// migration, ROLLBACK when no transaction survived.
    private func tryExec(_ sql: String) {
        guard let db else { return }
        _ = sqlite3_exec(db, sql, nil, nil, nil)
    }

    private func prepare(_ sql: String) throws -> OpaquePointer {
        let db = try handleOrThrow()
        var stmt: OpaquePointer?
        guard sqlite3_prepare_v2(db, sql, -1, &stmt, nil) == SQLITE_OK, let stmt else {
            throw StoreError(String(cString: sqlite3_errmsg(db)))
        }
        return stmt
    }

    private func stepDone(_ stmt: OpaquePointer) throws {
        while true {
            switch sqlite3_step(stmt) {
            case SQLITE_DONE: return
            case SQLITE_ROW: continue
            default: throw StoreError(currentErrorMessage())
            }
        }
    }

    private func bindText(_ stmt: OpaquePointer, _ idx: Int32, _ value: String) {
        // SQLITE_TRANSIENT: sqlite must copy the buffer before this call
        // returns — the bridged Swift string dies with the call frame, and a
        // nil destructor here is the classic use-after-free.
        let transient = unsafeBitCast(-1, to: sqlite3_destructor_type.self)
        _ = sqlite3_bind_text(stmt, idx, value, -1, transient)
    }

    private func columnText(_ stmt: OpaquePointer, _ idx: Int32) -> String? {
        guard sqlite3_column_type(stmt, idx) != SQLITE_NULL,
              let c = sqlite3_column_text(stmt, idx) else { return nil }
        return String(cString: c)
    }

    /// Uppercase hex everywhere — Windows stores uppercase, and a lowercase
    /// hash would silently defeat cross-platform dedupe.
    private static func sha256Hex(_ data: Data) -> String {
        SHA256.hash(data: data).map { String(format: "%02X", $0) }.joined()
    }

    /// "_2", "_3"… suffixes on collision, same as Windows, so two captures in
    /// the same second both keep the timestamp name.
    private static func uniquePath(_ path: String) -> String {
        let fm = FileManager.default
        if !fm.fileExists(atPath: path) { return path }

        let ns = path as NSString
        let dir = ns.deletingLastPathComponent
        let stem = (ns.lastPathComponent as NSString).deletingPathExtension
        let ext = ns.pathExtension          // no leading dot; "" when none
        var i = 2
        while true {
            let name = ext.isEmpty ? "\(stem)_\(i)" : "\(stem)_\(i).\(ext)"
            let candidate = (dir as NSString).appendingPathComponent(name)
            if !fm.fileExists(atPath: candidate) { return candidate }
            i += 1
        }
    }
}
