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
    }

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
    /// callers depend on that, because drag-out hands out a real file path.</summary>
    public Shot Add(byte[] png, int width, int height, DateTimeOffset takenAt)
    {
        var sha = Convert.ToHexString(SHA256.HashData(png));

        var dir = Path.Combine(Root, takenAt.ToString("yyyy"), takenAt.ToString("MM"));
        Directory.CreateDirectory(dir);

        lock (_gate)
        {
            var path = Unique(Path.Combine(dir, $"{takenAt:yyyy-MM-dd_HH-mm-ss}.png"));
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
                SELECT id, path, taken_at, width, height, sha256, kind, duration_ms
                FROM shots WHERE ocr_done = 0 ORDER BY id LIMIT $n;
                """;
            cmd.Parameters.AddWithValue("$n", limit);
            return ReadShots(cmd);
        }
    }

    public void SetOcr(long id, string text)
    {
        lock (_gate)
        {
            using var tx = _db.BeginTransaction();

            using (var cmd = _db.CreateCommand())
            {
                cmd.Transaction = tx;
                cmd.CommandText = "UPDATE shots SET ocr_text = $x, ocr_done = 1 WHERE id = $id;";
                cmd.Parameters.AddWithValue("$x", text);
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
                SELECT s.id, s.path, s.taken_at, s.width, s.height, s.sha256, s.kind, s.duration_ms
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
                SELECT id, path, taken_at, width, height, sha256, kind, duration_ms
                FROM shots ORDER BY id DESC LIMIT $n;
                """;
            cmd.Parameters.AddWithValue("$n", limit);
            return ReadShots(cmd);
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
                r.GetString(6), r.GetInt64(7)));
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
