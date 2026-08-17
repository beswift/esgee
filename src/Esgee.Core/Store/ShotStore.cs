using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using Microsoft.Data.Sqlite;

namespace Esgee.Store;

/// <summary>
/// Everything durable: the PNG on disk plus a searchable index beside it.
/// One folder holds both so the whole archive is portable/syncable as a unit.
/// </summary>
public sealed class ShotStore : IDisposable
{
    private readonly SqliteConnection _db;

    // Captures arrive on the UI thread while the OCR indexer writes from its own
    // pump thread. SqliteConnection is not thread-safe, so every statement goes
    // through this. Contention is negligible — these are millisecond writes.
    private readonly Lock _gate = new();

    public string Root { get; }

    public ShotStore(string? root = null)
    {
        Root = root ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), "esgee");
        Directory.CreateDirectory(Root);

        _db = new SqliteConnection($"Data Source={Path.Combine(Root, "index.db")}");
        _db.Open();
        Migrate();
    }

    private void Migrate()
    {
        using var cmd = _db.CreateCommand();
        cmd.CommandText = """
            PRAGMA journal_mode=WAL;

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

            -- Full-text over OCR'd screen text. This is the thing that turns a
            -- pile of thousands of PNGs into something you can actually find in.
            CREATE VIRTUAL TABLE IF NOT EXISTS shots_fts
                USING fts5(ocr_text, content='shots', content_rowid='id');
            """;
        cmd.ExecuteNonQuery();

        // Additive migrations for pre-existing databases. ALTER TABLE ADD COLUMN
        // throws "duplicate column name" once applied — that's the idempotence.
        TryExec("ALTER TABLE shots ADD COLUMN kind TEXT NOT NULL DEFAULT 'image'");
        TryExec("ALTER TABLE shots ADD COLUMN duration_ms INTEGER NOT NULL DEFAULT 0");
        // Peer sync: where a capture originally came from ("" = this machine),
        // and which OCR engine produced ocr_text (the versioned-sidecar pattern —
        // lets a future engine upgrade re-OCR selectively instead of blindly).
        TryExec("ALTER TABLE shots ADD COLUMN origin TEXT NOT NULL DEFAULT ''");
        TryExec("ALTER TABLE shots ADD COLUMN ocr_engine_version TEXT NOT NULL DEFAULT ''");
        // Which shots have been pushed to which sync target. New table = additive;
        // older app versions never touch it.
        TryExec("""
            CREATE TABLE IF NOT EXISTS sync_pushed (
                shot_id   INTEGER NOT NULL,
                target    TEXT NOT NULL,
                pushed_at TEXT NOT NULL,
                PRIMARY KEY (shot_id, target)
            )
            """);
    }

    /// <summary>Quotes each term so user text can't hit FTS5 operator syntax
    /// (AND/OR/NEAR, dashes, colons) by accident. Shared by the archive window
    /// and the peer API so a search means the same thing locally and remotely.</summary>
    public static string FtsQuery(string query)
        => string.Join(" ", query
            .Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Select(t => $"\"{t.Replace("\"", "\"\"")}\"*"));

    private void TryExec(string sql)
    {
        try
        {
            using var cmd = _db.CreateCommand();
            cmd.CommandText = sql;
            cmd.ExecuteNonQuery();
        }
        catch (SqliteException)
        {
            // Migration already applied.
        }
    }

    /// <summary>Writes the PNG and records it. Returns once the file is on disk —
    /// callers depend on that, because drag-out hands out a real file path.
    /// Identical bytes arriving within a short window return the EXISTING shot:
    /// the clipboard echo of esgee's own capture can slip past the watcher's
    /// time-window guard on a slow machine, and content identity is the one
    /// dedup signal that can't mistime.</summary>
    public Shot Add(byte[] png, int width, int height, DateTimeOffset takenAt)
    {
        var sha = Convert.ToHexString(SHA256.HashData(png));

        var dir = MonthDir(takenAt);
        Directory.CreateDirectory(dir);

        lock (_gate)
        {
            if (FindRecentBySha(sha, takenAt, windowSeconds: 10) is { } existing)
            {
                Log.Info($"deduplicated identical capture (echo of shot {existing.Id})");
                return existing;
            }

            var path = Unique(Path.Combine(dir, StampName(takenAt) + ".png"));
            File.WriteAllBytes(path, png);

            using var cmd = _db.CreateCommand();
            cmd.CommandText = """
                INSERT INTO shots (path, taken_at, width, height, sha256)
                VALUES ($p, $t, $w, $h, $s);
                SELECT last_insert_rowid();
                """;
            cmd.Parameters.AddWithValue("$p", path);
            cmd.Parameters.AddWithValue("$t", takenAt.ToString("o"));
            cmd.Parameters.AddWithValue("$w", width);
            cmd.Parameters.AddWithValue("$h", height);
            cmd.Parameters.AddWithValue("$s", sha);
            var id = (long)(cmd.ExecuteScalar() ?? 0L);

            return new Shot(id, path, takenAt, width, height, sha);
        }
    }

    /// <summary>
    /// Records a file that is already on disk — recordings, which ffmpeg writes
    /// straight into the archive tree. Inserted with ocr_done=1 so the OCR queue
    /// never sees a video.
    /// </summary>
    public Shot AddFile(string path, int width, int height, DateTimeOffset takenAt,
        string kind, long durationMs)
    {
        string sha;
        using (var fs = File.OpenRead(path))
            sha = Convert.ToHexString(SHA256.HashData(fs));

        lock (_gate)
        {
            using var cmd = _db.CreateCommand();
            cmd.CommandText = """
                INSERT INTO shots (path, taken_at, width, height, sha256, kind, duration_ms, ocr_done)
                VALUES ($p, $t, $w, $h, $s, $k, $d, 1);
                SELECT last_insert_rowid();
                """;
            cmd.Parameters.AddWithValue("$p", path);
            cmd.Parameters.AddWithValue("$t", takenAt.ToString("o"));
            cmd.Parameters.AddWithValue("$w", width);
            cmd.Parameters.AddWithValue("$h", height);
            cmd.Parameters.AddWithValue("$s", sha);
            cmd.Parameters.AddWithValue("$k", kind);
            cmd.Parameters.AddWithValue("$d", durationMs);
            var id = (long)(cmd.ExecuteScalar() ?? 0L);

            return new Shot(id, path, takenAt, width, height, sha, kind, durationMs);
        }
    }

    /// <summary>Shots still awaiting OCR, oldest first.</summary>
    public List<Shot> PendingOcr(int limit = 25)
    {
        lock (_gate)
        {
            using var cmd = _db.CreateCommand();
            cmd.CommandText = """
                SELECT id, path, taken_at, width, height, sha256, kind, duration_ms, origin
                FROM shots WHERE ocr_done = 0 ORDER BY id LIMIT $n;
                """;
            cmd.Parameters.AddWithValue("$n", limit);
            return ReadShots(cmd);
        }
    }

    public void SetOcr(long id, string text, string engineVersion = "")
    {
        lock (_gate)
        {
            using var tx = _db.BeginTransaction();

            using (var cmd = _db.CreateCommand())
            {
                cmd.Transaction = tx;
                cmd.CommandText =
                    "UPDATE shots SET ocr_text = $x, ocr_done = 1, ocr_engine_version = $v WHERE id = $id;";
                cmd.Parameters.AddWithValue("$x", text);
                cmd.Parameters.AddWithValue("$v", engineVersion);
                cmd.Parameters.AddWithValue("$id", id);
                cmd.ExecuteNonQuery();
            }

            using (var cmd = _db.CreateCommand())
            {
                cmd.Transaction = tx;
                // External-content FTS: push the row in explicitly.
                cmd.CommandText = "INSERT INTO shots_fts(rowid, ocr_text) VALUES ($id, $x);";
                cmd.Parameters.AddWithValue("$id", id);
                cmd.Parameters.AddWithValue("$x", text);
                cmd.ExecuteNonQuery();
            }

            tx.Commit();
        }
    }

    public List<Shot> Search(string query, int limit = 100)
    {
        lock (_gate)
        {
            using var cmd = _db.CreateCommand();
            cmd.CommandText = """
                SELECT s.id, s.path, s.taken_at, s.width, s.height, s.sha256, s.kind, s.duration_ms, s.origin
                FROM shots_fts f JOIN shots s ON s.id = f.rowid
                WHERE shots_fts MATCH $q
                ORDER BY rank LIMIT $n;
                """;
            cmd.Parameters.AddWithValue("$q", query);
            cmd.Parameters.AddWithValue("$n", limit);
            return ReadShots(cmd);
        }
    }

    /// <summary>Most recent captures, newest first — the archive browser's default view.</summary>
    public List<Shot> Recent(int limit = 100)
    {
        lock (_gate)
        {
            using var cmd = _db.CreateCommand();
            cmd.CommandText = """
                SELECT id, path, taken_at, width, height, sha256, kind, duration_ms, origin
                FROM shots ORDER BY id DESC LIMIT $n;
                """;
            cmd.Parameters.AddWithValue("$n", limit);
            return ReadShots(cmd);
        }
    }

    /// <summary>Newest shot with this hash inside the window, if any. Caller
    /// holds the gate.</summary>
    private Shot? FindRecentBySha(string sha, DateTimeOffset now, int windowSeconds)
    {
        using var cmd = _db.CreateCommand();
        cmd.CommandText = """
            SELECT id, path, taken_at, width, height, sha256, kind, duration_ms, origin
            FROM shots WHERE sha256 = $s ORDER BY id DESC LIMIT 1;
            """;
        cmd.Parameters.AddWithValue("$s", sha);

        using var r = cmd.ExecuteReader();
        if (!r.Read()) return null;

        var takenAt = DateTimeOffset.Parse(r.GetString(2));
        if ((now - takenAt).Duration() > TimeSpan.FromSeconds(windowSeconds)) return null;

        return new Shot(
            r.GetInt64(0), r.GetString(1), takenAt,
            r.GetInt32(3), r.GetInt32(4), r.GetString(5),
            r.GetString(6), r.GetInt64(7), r.GetString(8));
    }

    /// <summary>Archive health for `esgee --doctor`: totals, OCR backlog, and
    /// identical-content duplicate groups (the double-shot signature).</summary>
    public (long Total, long Videos, long OcrPending, List<string> DupGroups) Doctor()
    {
        lock (_gate)
        {
            long total, videos, pending;
            using (var cmd = _db.CreateCommand())
            {
                cmd.CommandText = """
                    SELECT COUNT(*),
                           COALESCE(SUM(kind = 'video'), 0),
                           COALESCE(SUM(ocr_done = 0), 0)
                    FROM shots;
                    """;
                using var r = cmd.ExecuteReader();
                r.Read();
                (total, videos, pending) = (r.GetInt64(0), r.GetInt64(1), r.GetInt64(2));
            }

            var dups = new List<string>();
            using (var cmd = _db.CreateCommand())
            {
                cmd.CommandText = """
                    SELECT sha256, COUNT(*) AS n, MIN(taken_at), GROUP_CONCAT(path, ' | ')
                    FROM shots GROUP BY sha256 HAVING n > 1
                    ORDER BY MIN(id) DESC LIMIT 20;
                    """;
                using var r = cmd.ExecuteReader();
                while (r.Read())
                    dups.Add($"{r.GetInt64(1)}x  {r.GetString(2)}  {r.GetString(3)}");
            }

            return (total, videos, pending, dups);
        }
    }

    /// <summary>One row by id, or null. The peer API's lookup primitive.</summary>
    public Shot? GetById(long id)
    {
        lock (_gate)
        {
            using var cmd = _db.CreateCommand();
            cmd.CommandText = """
                SELECT id, path, taken_at, width, height, sha256, kind, duration_ms, origin
                FROM shots WHERE id = $id;
                """;
            cmd.Parameters.AddWithValue("$id", id);
            return ReadShots(cmd).FirstOrDefault();
        }
    }

    /// <summary>Total captures — the /ping health number.</summary>
    public long Count()
    {
        lock (_gate)
        {
            using var cmd = _db.CreateCommand();
            cmd.CommandText = "SELECT COUNT(*) FROM shots;";
            return (long)(cmd.ExecuteScalar() ?? 0L);
        }
    }

    /// <summary>OCR state for one shot: done flag, text, and the engine version
    /// that produced it — the payload of a sync sidecar.</summary>
    public (bool Done, string? Text, string EngineVersion) GetOcr(long id)
    {
        lock (_gate)
        {
            using var cmd = _db.CreateCommand();
            cmd.CommandText =
                "SELECT ocr_done, ocr_text, ocr_engine_version FROM shots WHERE id = $id;";
            cmd.Parameters.AddWithValue("$id", id);
            using var r = cmd.ExecuteReader();
            if (!r.Read()) return (false, null, "");
            return (r.GetInt64(0) != 0, r.IsDBNull(1) ? null : r.GetString(1),
                    r.IsDBNull(2) ? "" : r.GetString(2));
        }
    }

    /// <summary>
    /// Files a capture that arrived from another machine (push sync or a manual
    /// "pull to this PC"). The file is already at <paramref name="path"/> inside
    /// this archive's tree. OCR text comes from the sender's sidecar — it is
    /// imported, never re-run here; a sidecar with no text on an image leaves
    /// ocr_done=0 so the local backlog sweep fills the hole. Dedupe is global by
    /// content hash: the same capture pushed twice (retry, or pull-then-sync)
    /// lands exactly once.
    /// </summary>
    public (Shot Shot, bool Duplicate) Ingest(string path, string sha256,
        DateTimeOffset takenAt, int width, int height, string kind, long durationMs,
        string? ocrText, string ocrEngineVersion, string origin)
    {
        lock (_gate)
        {
            using (var find = _db.CreateCommand())
            {
                find.CommandText = """
                    SELECT id, path, taken_at, width, height, sha256, kind, duration_ms, origin
                    FROM shots WHERE sha256 = $s ORDER BY id DESC LIMIT 1;
                    """;
                find.Parameters.AddWithValue("$s", sha256);
                if (ReadShots(find).FirstOrDefault() is { } existing)
                    return (existing, true);
            }

            var ocrDone = ocrText is not null || kind != "image";

            using var tx = _db.BeginTransaction();
            long id;
            using (var cmd = _db.CreateCommand())
            {
                cmd.Transaction = tx;
                cmd.CommandText = """
                    INSERT INTO shots (path, taken_at, width, height, sha256, kind,
                                       duration_ms, ocr_text, ocr_done, ocr_engine_version, origin)
                    VALUES ($p, $t, $w, $h, $s, $k, $d, $x, $done, $v, $o);
                    SELECT last_insert_rowid();
                    """;
                cmd.Parameters.AddWithValue("$p", path);
                cmd.Parameters.AddWithValue("$t", takenAt.ToString("o"));
                cmd.Parameters.AddWithValue("$w", width);
                cmd.Parameters.AddWithValue("$h", height);
                cmd.Parameters.AddWithValue("$s", sha256);
                cmd.Parameters.AddWithValue("$k", kind);
                cmd.Parameters.AddWithValue("$d", durationMs);
                cmd.Parameters.AddWithValue("$x", (object?)ocrText ?? DBNull.Value);
                cmd.Parameters.AddWithValue("$done", ocrDone ? 1 : 0);
                cmd.Parameters.AddWithValue("$v", ocrEngineVersion);
                cmd.Parameters.AddWithValue("$o", origin);
                id = (long)(cmd.ExecuteScalar() ?? 0L);
            }

            if (!string.IsNullOrEmpty(ocrText))
            {
                using var fts = _db.CreateCommand();
                fts.Transaction = tx;
                fts.CommandText = "INSERT INTO shots_fts(rowid, ocr_text) VALUES ($id, $x);";
                fts.Parameters.AddWithValue("$id", id);
                fts.Parameters.AddWithValue("$x", ocrText);
                fts.ExecuteNonQuery();
            }

            tx.Commit();
            return (new Shot(id, path, takenAt, width, height, sha256, kind, durationMs, origin), false);
        }
    }

    /// <summary>Ingest destinations take their extension from a client-supplied
    /// file name. Anything but a short alphanumeric extension — quotes, control
    /// characters, a hundred-char "extension" — would make the file write throw
    /// after the route can no longer answer 400, dropping the connection with
    /// no response. Those fall back to the kind's default instead.</summary>
    public static string SafeExtension(string? extension, string kind)
    {
        var fallback = kind == "video" ? ".mp4" : ".png";
        if (string.IsNullOrEmpty(extension) ||
            extension.Length is < 2 or > 10 || extension[0] != '.') return fallback;
        for (var i = 1; i < extension.Length; i++)
            if (!char.IsAsciiLetterOrDigit(extension[i])) return fallback;
        return extension;
    }

    /// <summary>Picks a destination path inside this archive's yyyy/MM tree for
    /// an incoming file, creating the month folder. Caller writes the bytes.</summary>
    public string PlanIngestPath(DateTimeOffset takenAt, string extension)
    {
        var dir = MonthDir(takenAt);
        Directory.CreateDirectory(dir);
        lock (_gate)
        {
            return Unique(Path.Combine(dir, StampName(takenAt) + extension));
        }
    }

    /// <summary>The archive's yyyy/MM month folder for a timestamp — formatted
    /// under the INVARIANT culture always. The current culture's default
    /// calendar leaks into bare ToString ("2569/08" under th-TH's Buddhist
    /// era, a different year AND month under ar-SA), and on Linux the node's
    /// culture rides in on LANG — a locale-carrying service environment would
    /// silently fork one archive into two divergent trees.</summary>
    private string MonthDir(DateTimeOffset takenAt) => Path.Combine(Root,
        takenAt.ToString("yyyy", CultureInfo.InvariantCulture),
        takenAt.ToString("MM", CultureInfo.InvariantCulture));

    /// <summary>File-name stem for a capture — same invariant-culture rule as
    /// MonthDir, and the documented cross-platform naming contract
    /// (docs/MAC.md "Store": same yyyy/MM tree, same yyyy-MM-dd_HH-mm-ss names).</summary>
    private static string StampName(DateTimeOffset takenAt)
        => takenAt.ToString("yyyy-MM-dd_HH-mm-ss", CultureInfo.InvariantCulture);

    /// <summary>Shots never pushed to <paramref name="target"/>, oldest first —
    /// the startup backlog sweep. Excludes shots that ORIGINATED at the target
    /// (pushing those back would just bounce off its sha dedupe).</summary>
    public List<Shot> NotPushed(string target, string targetMachine, int limit = 500)
    {
        lock (_gate)
        {
            using var cmd = _db.CreateCommand();
            cmd.CommandText = """
                SELECT id, path, taken_at, width, height, sha256, kind, duration_ms, origin
                FROM shots
                WHERE id NOT IN (SELECT shot_id FROM sync_pushed WHERE target = $t)
                  AND origin != $m
                ORDER BY id LIMIT $n;
                """;
            cmd.Parameters.AddWithValue("$t", target);
            cmd.Parameters.AddWithValue("$m", targetMachine);
            cmd.Parameters.AddWithValue("$n", limit);
            return ReadShots(cmd);
        }
    }

    public void MarkPushed(long shotId, string target)
    {
        lock (_gate)
        {
            using var cmd = _db.CreateCommand();
            cmd.CommandText = """
                INSERT OR REPLACE INTO sync_pushed (shot_id, target, pushed_at)
                VALUES ($id, $t, $now);
                """;
            cmd.Parameters.AddWithValue("$id", shotId);
            cmd.Parameters.AddWithValue("$t", target);
            cmd.Parameters.AddWithValue("$now", DateTimeOffset.Now.ToString("o"));
            cmd.ExecuteNonQuery();
        }
    }

    /// <summary>Cheap change token for live views: moves when rows are added,
    /// removed, or OCR completes. One scalar WAL read — safe to poll.</summary>
    public string ChangeToken()
    {
        lock (_gate)
        {
            using var cmd = _db.CreateCommand();
            cmd.CommandText =
                "SELECT COALESCE(MAX(id),0) || ':' || COUNT(*) || ':' || COALESCE(SUM(ocr_done),0) FROM shots;";
            return (string)(cmd.ExecuteScalar() ?? "0:0:0");
        }
    }

    private static List<Shot> ReadShots(SqliteCommand cmd)
    {
        var list = new List<Shot>();
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            list.Add(new Shot(
                r.GetInt64(0), r.GetString(1),
                DateTimeOffset.Parse(r.GetString(2)),
                r.GetInt32(3), r.GetInt32(4), r.GetString(5),
                r.GetString(6), r.GetInt64(7), r.GetString(8)));
        }
        return list;
    }

    private static string Unique(string path)
    {
        if (!File.Exists(path)) return path;

        var dir = Path.GetDirectoryName(path)!;
        var stem = Path.GetFileNameWithoutExtension(path);
        var ext = Path.GetExtension(path);
        for (var i = 2; ; i++)
        {
            var candidate = Path.Combine(dir, $"{stem}_{i}{ext}");
            if (!File.Exists(candidate)) return candidate;
        }
    }

    public void Dispose() => _db.Dispose();
}
