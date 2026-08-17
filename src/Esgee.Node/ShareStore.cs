using System.Security.Cryptography;
using System.Text;
using Esgee.Store;
using Microsoft.Data.Sqlite;

namespace Esgee.Node;

/// <summary>One share member, resolved from a token. Identity is a display
/// name chosen at join — not an account, not an email (docs/SHARES.md).</summary>
public sealed record ShareMember(string MemberId, string DisplayName, string Role, string JoinedAt)
{
    public bool IsOperator => Role == "operator";
}

/// <summary>One live share item joined to its capture row. LatestCommentAt
/// feeds the ?since= activity filter — a new comment must re-surface an item
/// or the notification dot never fires for it.</summary>
public sealed record ShareItemRow(
    string ItemId, string SharedBy, string SharedAt, Shot Shot,
    int CommentCount, string? LatestCommentAt);

public enum ShareDeleteOutcome { NotFound, Forbidden, Deleted }
public enum ShareJoinOutcome { Joined, BadInvite, NeedName, NameTaken }

/// <summary>
/// Everything durable about one share, all inside the share archive's own
/// index.db — the capture rows via the ordinary ShotStore, plus the additive
/// share tables (members, invites, share_items, comments). One folder is the
/// whole share: portable, backup-able, never commingled with a personal
/// archive.
///
/// Secrets policy: member tokens and invite codes are stored ONLY as SHA-256
/// hashes and verified in constant time. The raw token exists in exactly two
/// places — the join response that minted it, and the member's own machine.
/// </summary>
public sealed class ShareStore : IDisposable
{
    private readonly SqliteConnection _db;

    // Share-table statements come from connection worker threads; SqliteConnection
    // is not thread-safe. Shots writes take ShotStore's own gate — always after
    // this one when both are needed, never the reverse, so the pair can't deadlock.
    private readonly Lock _gate = new();

    private const string IdAlphabet =
        "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789";

    public static readonly TimeSpan InviteLifetime = TimeSpan.FromHours(24);

    public ShotStore Shots { get; }

    /// <summary>Assigned by the share on first serve and stable forever after —
    /// what clients key caches on even if the operator renames the share.</summary>
    public string ShareId { get; }

    public ShareStore(string root)
    {
        Shots = new ShotStore(root);
        _db = new SqliteConnection($"Data Source={System.IO.Path.Combine(Shots.Root, "index.db")}");
        _db.Open();
        Migrate();
        ShareId = EnsureShareId();
    }

    private void Migrate()
    {
        using var cmd = _db.CreateCommand();
        cmd.CommandText = """
            CREATE TABLE IF NOT EXISTS share_meta (
                key   TEXT PRIMARY KEY,
                value TEXT NOT NULL
            );

            CREATE TABLE IF NOT EXISTS members (
                member_id    TEXT PRIMARY KEY,
                display_name TEXT NOT NULL,
                token_hash   TEXT NOT NULL, -- SHA-256 hex; the raw token is never stored
                role         TEXT NOT NULL, -- 'operator' | 'member'
                joined_at    TEXT NOT NULL
            );

            CREATE TABLE IF NOT EXISTS invites (
                code_hash    TEXT PRIMARY KEY, -- SHA-256 hex; the raw code is never stored
                display_hint TEXT,
                minted_at    TEXT NOT NULL,
                expires_at   TEXT NOT NULL,
                redeemed_by  TEXT             -- member_id; single-use is a one-row update
            );

            CREATE TABLE IF NOT EXISTS share_items (
                item_id    TEXT PRIMARY KEY,  -- "itm_" + 10 url-safe chars, assigned here
                shot_id    INTEGER NOT NULL,  -- FK into shots (dangling once tombstoned)
                shared_by  TEXT NOT NULL,     -- member_id, stamped from the token
                shared_at  TEXT NOT NULL,
                deleted_at TEXT               -- tombstone: content gone, id kept for pruning
            );
            CREATE INDEX IF NOT EXISTS ix_share_items_shot ON share_items(shot_id);

            CREATE TABLE IF NOT EXISTS comments (
                id         INTEGER PRIMARY KEY AUTOINCREMENT,
                item_id    TEXT NOT NULL,
                member_id  TEXT NOT NULL,
                created_at TEXT NOT NULL,
                body       TEXT NOT NULL
            );
            CREATE INDEX IF NOT EXISTS ix_comments_item ON comments(item_id);
            """;
        cmd.ExecuteNonQuery();
    }

    private string EnsureShareId()
    {
        lock (_gate)
        {
            using (var get = _db.CreateCommand())
            {
                get.CommandText = "SELECT value FROM share_meta WHERE key = 'share_id';";
                if (get.ExecuteScalar() is string existing) return existing;
            }

            var id = "shr_" + RandomNumberGenerator.GetString(IdAlphabet, 10);
            using var put = _db.CreateCommand();
            put.CommandText = "INSERT INTO share_meta (key, value) VALUES ('share_id', $v);";
            put.Parameters.AddWithValue("$v", id);
            put.ExecuteNonQuery();
            return id;
        }
    }

    // ---- identity -------------------------------------------------------------

    /// <summary>Registers (or re-keys) the operator from the bootstrap token the
    /// node was started with. The token file is the source of truth: rotating
    /// it rotates operator access on the next start, touching nobody else.</summary>
    public string EnsureOperator(string token)
    {
        lock (_gate)
        {
            var hash = HashHex(token);
            using (var find = _db.CreateCommand())
            {
                find.CommandText = "SELECT member_id FROM members WHERE role = 'operator' LIMIT 1;";
                if (find.ExecuteScalar() is string existing)
                {
                    using var rekey = _db.CreateCommand();
                    rekey.CommandText = "UPDATE members SET token_hash = $h WHERE member_id = $m;";
                    rekey.Parameters.AddWithValue("$h", hash);
                    rekey.Parameters.AddWithValue("$m", existing);
                    rekey.ExecuteNonQuery();
                    return existing;
                }
            }

            var id = "mem_" + RandomNumberGenerator.GetString(IdAlphabet, 8);
            using var insert = _db.CreateCommand();
            insert.CommandText = """
                INSERT INTO members (member_id, display_name, token_hash, role, joined_at)
                VALUES ($m, 'operator', $h, 'operator', $t);
                """;
            insert.Parameters.AddWithValue("$m", id);
            insert.Parameters.AddWithValue("$h", hash);
            insert.Parameters.AddWithValue("$t", NowIso());
            insert.ExecuteNonQuery();
            return id;
        }
    }

    /// <summary>Token → member, or null. The ONLY source of request identity:
    /// shared_by and comment authorship are stamped from this, never from a
    /// client-supplied field. Hashes compared in constant time.</summary>
    public ShareMember? ResolveToken(string token)
    {
        var supplied = Encoding.ASCII.GetBytes(HashHex(token));
        lock (_gate)
        {
            using var cmd = _db.CreateCommand();
            cmd.CommandText =
                "SELECT member_id, display_name, role, joined_at, token_hash FROM members;";
            using var r = cmd.ExecuteReader();
            while (r.Read())
            {
                var stored = Encoding.ASCII.GetBytes(r.GetString(4));
                if (stored.Length == supplied.Length &&
                    CryptographicOperations.FixedTimeEquals(supplied, stored))
                    return new ShareMember(r.GetString(0), r.GetString(1), r.GetString(2), r.GetString(3));
            }
            return null;
        }
    }

    public List<ShareMember> Members()
    {
        lock (_gate)
        {
            using var cmd = _db.CreateCommand();
            cmd.CommandText = """
                SELECT member_id, display_name, role, joined_at
                FROM members ORDER BY joined_at, member_id;
                """;
            var list = new List<ShareMember>();
            using var r = cmd.ExecuteReader();
            while (r.Read())
                list.Add(new ShareMember(r.GetString(0), r.GetString(1), r.GetString(2), r.GetString(3)));
            return list;
        }
    }

    // ---- invites ----------------------------------------------------------------

    /// <summary>Mints a single-use invite (24h). Returns the raw code — the
    /// only moment it exists outside the operator's terminal; the table keeps
    /// its hash.</summary>
    public string MintInvite(string? displayHint)
    {
        var code = RandomNumberGenerator.GetString(IdAlphabet, 20);
        lock (_gate)
        {
            using var cmd = _db.CreateCommand();
            cmd.CommandText = """
                INSERT INTO invites (code_hash, display_hint, minted_at, expires_at)
                VALUES ($h, $hint, $now, $exp);
                """;
            cmd.Parameters.AddWithValue("$h", HashHex(code));
            cmd.Parameters.AddWithValue("$hint",
                (object?)Trimmed(displayHint) ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$now", NowIso());
            cmd.Parameters.AddWithValue("$exp",
                (DateTimeOffset.UtcNow + InviteLifetime).ToString("o"));
            cmd.ExecuteNonQuery();
        }
        return code;
    }

    /// <summary>Redeems an invite: spent/expired/unknown are all one answer
    /// (BadInvite) so the route can't be used to probe which codes exist. A
    /// missing display name does NOT consume the invite — the caller retries
    /// with a name — and neither does a name an existing member already goes
    /// by: display names are the layer humans read authorship from, so a
    /// second "Ben" would spoof the first everywhere shared_by, comments, and
    /// @mentions render. Success mints the member's own token.</summary>
    public (ShareJoinOutcome Outcome, string? MemberId, string? Token) RedeemInvite(
        string invite, string? displayName)
    {
        var supplied = Encoding.ASCII.GetBytes(HashHex(invite));
        lock (_gate)
        {
            string? codeHash = null, hint = null;
            using (var cmd = _db.CreateCommand())
            {
                cmd.CommandText = """
                    SELECT code_hash, display_hint, expires_at
                    FROM invites WHERE redeemed_by IS NULL;
                    """;
                using var r = cmd.ExecuteReader();
                while (r.Read())
                {
                    var stored = Encoding.ASCII.GetBytes(r.GetString(0));
                    if (stored.Length != supplied.Length ||
                        !CryptographicOperations.FixedTimeEquals(supplied, stored)) continue;
                    if (!DateTimeOffset.TryParse(r.GetString(2), out var expires) ||
                        DateTimeOffset.UtcNow >= expires) break; // expired = bad
                    codeHash = r.GetString(0);
                    hint = r.IsDBNull(1) ? null : r.GetString(1);
                    break;
                }
            }
            if (codeHash is null) return (ShareJoinOutcome.BadInvite, null, null);

            var name = Trimmed(displayName) ?? Trimmed(hint);
            if (name is null) return (ShareJoinOutcome.NeedName, null, null);
            if (NameTakenLocked(name)) return (ShareJoinOutcome.NameTaken, null, null);

            var memberId = "mem_" + RandomNumberGenerator.GetString(IdAlphabet, 8);
            var token = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));

            using var tx = _db.BeginTransaction();
            using (var insert = _db.CreateCommand())
            {
                insert.Transaction = tx;
                insert.CommandText = """
                    INSERT INTO members (member_id, display_name, token_hash, role, joined_at)
                    VALUES ($m, $n, $h, 'member', $t);
                    """;
                insert.Parameters.AddWithValue("$m", memberId);
                insert.Parameters.AddWithValue("$n", name);
                insert.Parameters.AddWithValue("$h", HashHex(token));
                insert.Parameters.AddWithValue("$t", NowIso());
                insert.ExecuteNonQuery();
            }
            using (var burn = _db.CreateCommand())
            {
                burn.Transaction = tx;
                burn.CommandText =
                    "UPDATE invites SET redeemed_by = $m WHERE code_hash = $h AND redeemed_by IS NULL;";
                burn.Parameters.AddWithValue("$m", memberId);
                burn.Parameters.AddWithValue("$h", codeHash);
                burn.ExecuteNonQuery();
            }
            tx.Commit();

            return (ShareJoinOutcome.Joined, memberId, token);
        }
    }

    /// <summary>Caller holds the gate. Case-insensitive: two members whose
    /// names differ only by case are still one person to everyone reading them.</summary>
    private bool NameTakenLocked(string name)
    {
        using var cmd = _db.CreateCommand();
        cmd.CommandText = "SELECT 1 FROM members WHERE display_name = $n COLLATE NOCASE LIMIT 1;";
        cmd.Parameters.AddWithValue("$n", name);
        return cmd.ExecuteScalar() is not null;
    }

    // ---- items ----------------------------------------------------------------

    private const string ItemSelect = """
        SELECT si.item_id, si.shared_by, si.shared_at,
               s.id, s.path, s.taken_at, s.width, s.height, s.sha256, s.kind, s.duration_ms,
               (SELECT COUNT(*) FROM comments c WHERE c.item_id = si.item_id),
               (SELECT MAX(c2.created_at) FROM comments c2 WHERE c2.item_id = si.item_id)
        FROM share_items si JOIN shots s ON s.id = si.shot_id
        WHERE si.deleted_at IS NULL
        """;

    public List<ShareItemRow> LiveItems(int? limit = null)
    {
        lock (_gate)
        {
            using var cmd = _db.CreateCommand();
            cmd.CommandText = ItemSelect + " ORDER BY si.shared_at DESC, si.rowid DESC" +
                              (limit is null ? ";" : " LIMIT $n;");
            if (limit is not null) cmd.Parameters.AddWithValue("$n", limit);
            return ReadItems(cmd);
        }
    }

    /// <summary>Same FTS semantics as the peer /search: the caller quotes the
    /// user's words with ShotStore.FtsQuery, rank order, live items only.</summary>
    public List<ShareItemRow> SearchItems(string ftsQuery, int limit)
    {
        lock (_gate)
        {
            using var cmd = _db.CreateCommand();
            cmd.CommandText = """
                SELECT si.item_id, si.shared_by, si.shared_at,
                       s.id, s.path, s.taken_at, s.width, s.height, s.sha256, s.kind, s.duration_ms,
                       (SELECT COUNT(*) FROM comments c WHERE c.item_id = si.item_id),
                       (SELECT MAX(c2.created_at) FROM comments c2 WHERE c2.item_id = si.item_id)
                FROM shots_fts f
                JOIN shots s ON s.id = f.rowid
                JOIN share_items si ON si.shot_id = s.id
                WHERE shots_fts MATCH $q AND si.deleted_at IS NULL
                ORDER BY rank LIMIT $n;
                """;
            cmd.Parameters.AddWithValue("$q", ftsQuery);
            cmd.Parameters.AddWithValue("$n", limit);
            return ReadItems(cmd);
        }
    }

    public ShareItemRow? GetItem(string itemId)
    {
        lock (_gate)
        {
            using var cmd = _db.CreateCommand();
            cmd.CommandText = ItemSelect + " AND si.item_id = $id LIMIT 1;";
            cmd.Parameters.AddWithValue("$id", itemId);
            return ReadItems(cmd).FirstOrDefault();
        }
    }

    /// <summary>The dedupe probe: sha256 → the live item that already carries
    /// it, if any. A re-shared capture returns the existing item — everyone
    /// keeps naming the same capture identically, so comments stay anchored.</summary>
    public ShareItemRow? FindLiveBySha(string sha256)
    {
        lock (_gate)
        {
            using var cmd = _db.CreateCommand();
            cmd.CommandText = ItemSelect + " AND s.sha256 = $s ORDER BY si.rowid DESC LIMIT 1;";
            cmd.Parameters.AddWithValue("$s", sha256);
            return ReadItems(cmd).FirstOrDefault();
        }
    }

    /// <summary>Records a share item over an already-ingested shot row.</summary>
    public ShareItemRow AddItem(Shot shot, string memberId)
    {
        var itemId = "itm_" + RandomNumberGenerator.GetString(IdAlphabet, 10);
        string now;
        lock (_gate)
        {
            // Stamped INSIDE the gate, like every other share write: a stamp
            // taken before the lock could commit older than activity a
            // concurrent ?since= poll already returned, and the strict-after
            // filter would then hide this item from every later poll.
            now = NowIso();
            using var cmd = _db.CreateCommand();
            cmd.CommandText = """
                INSERT INTO share_items (item_id, shot_id, shared_by, shared_at)
                VALUES ($i, $s, $m, $t);
                """;
            cmd.Parameters.AddWithValue("$i", itemId);
            cmd.Parameters.AddWithValue("$s", shot.Id);
            cmd.Parameters.AddWithValue("$m", memberId);
            cmd.Parameters.AddWithValue("$t", now);
            cmd.ExecuteNonQuery();
        }
        return new ShareItemRow(itemId, memberId, now, shot, 0, null);
    }

    public List<(string ItemId, string DeletedAt)> Tombstones()
    {
        lock (_gate)
        {
            using var cmd = _db.CreateCommand();
            cmd.CommandText = """
                SELECT item_id, deleted_at FROM share_items
                WHERE deleted_at IS NOT NULL ORDER BY deleted_at DESC;
                """;
            var list = new List<(string, string)>();
            using var r = cmd.ExecuteReader();
            while (r.Read()) list.Add((r.GetString(0), r.GetString(1)));
            return list;
        }
    }

    /// <summary>Tombstones the item and destroys its content: the shots row,
    /// its FTS entry, the files, and the item's comments — the discussion
    /// routinely quotes the capture (a leaked credential, say), so retaining
    /// it at rest would outlive the destruction the protocol promises. The
    /// tombstone row (id + deleted_at) is kept forever so members can prune
    /// copies they pulled. Author-or-operator is enforced HERE, against the
    /// token-derived member — not in the UI.</summary>
    public ShareDeleteOutcome DeleteItem(string itemId, string memberId, bool asOperator,
        out ShareItemRow? removed)
    {
        removed = null;
        lock (_gate)
        {
            var row = GetItemLocked(itemId);
            if (row is null) return ShareDeleteOutcome.NotFound;
            if (!asOperator && row.SharedBy != memberId) return ShareDeleteOutcome.Forbidden;

            string? ocrText = null;
            using (var get = _db.CreateCommand())
            {
                get.CommandText = "SELECT ocr_text FROM shots WHERE id = $id;";
                get.Parameters.AddWithValue("$id", row.Shot.Id);
                if (get.ExecuteScalar() is string t) ocrText = t;
            }

            using (var tx = _db.BeginTransaction())
            {
                using (var stone = _db.CreateCommand())
                {
                    stone.Transaction = tx;
                    stone.CommandText =
                        "UPDATE share_items SET deleted_at = $t WHERE item_id = $i;";
                    stone.Parameters.AddWithValue("$t", NowIso());
                    stone.Parameters.AddWithValue("$i", itemId);
                    stone.ExecuteNonQuery();
                }

                // External-content FTS needs the old text to unindex the row.
                if (!string.IsNullOrEmpty(ocrText))
                {
                    using var fts = _db.CreateCommand();
                    fts.Transaction = tx;
                    fts.CommandText =
                        "INSERT INTO shots_fts(shots_fts, rowid, ocr_text) VALUES ('delete', $id, $x);";
                    fts.Parameters.AddWithValue("$id", row.Shot.Id);
                    fts.Parameters.AddWithValue("$x", ocrText);
                    fts.ExecuteNonQuery();
                }

                using (var drop = _db.CreateCommand())
                {
                    drop.Transaction = tx;
                    drop.CommandText = "DELETE FROM shots WHERE id = $id;";
                    drop.Parameters.AddWithValue("$id", row.Shot.Id);
                    drop.ExecuteNonQuery();
                }

                using (var chatter = _db.CreateCommand())
                {
                    chatter.Transaction = tx;
                    chatter.CommandText = "DELETE FROM comments WHERE item_id = $i;";
                    chatter.Parameters.AddWithValue("$i", itemId);
                    chatter.ExecuteNonQuery();
                }
                tx.Commit();
            }

            // Pixels leave the disk with the row — a tombstoned item must not
            // remain one GET (or one file share browse) away.
            TryDelete(row.Shot.Path);
            TryDelete(System.IO.Path.ChangeExtension(row.Shot.Path, ".gif"));
            TryDelete(row.Shot.Path + ".png");

            removed = row;
            return ShareDeleteOutcome.Deleted;
        }
    }

    /// <summary>Retention: tombstone everything shared more than
    /// <paramref name="days"/> days ago. Returns the removed ids.</summary>
    public List<string> SweepRetention(int days)
    {
        var cutoff = DateTimeOffset.UtcNow.AddDays(-days);
        var expired = LiveItems()
            .Where(r => DateTimeOffset.TryParse(r.SharedAt, out var at) && at < cutoff)
            .Select(r => r.ItemId)
            .ToList();

        var removed = new List<string>();
        foreach (var id in expired)
            if (DeleteItem(id, "", asOperator: true, out _) == ShareDeleteOutcome.Deleted)
                removed.Add(id);
        return removed;
    }

    // ---- comments ---------------------------------------------------------------

    public List<(long Id, string MemberId, string? DisplayName, string CreatedAt, string Body)>
        Comments(string itemId)
    {
        lock (_gate)
        {
            using var cmd = _db.CreateCommand();
            cmd.CommandText = """
                SELECT c.id, c.member_id, m.display_name, c.created_at, c.body
                FROM comments c LEFT JOIN members m ON m.member_id = c.member_id
                WHERE c.item_id = $i ORDER BY c.id;
                """;
            cmd.Parameters.AddWithValue("$i", itemId);
            var list = new List<(long, string, string?, string, string)>();
            using var r = cmd.ExecuteReader();
            while (r.Read())
                list.Add((r.GetInt64(0), r.GetString(1),
                    r.IsDBNull(2) ? null : r.GetString(2), r.GetString(3), r.GetString(4)));
            return list;
        }
    }

    /// <summary>Append-only; null when the item isn't live. Authorship is the
    /// caller's token-derived member id.</summary>
    public (long Id, string CreatedAt)? AddComment(string itemId, string memberId, string body)
    {
        lock (_gate)
        {
            if (GetItemLocked(itemId) is null) return null;

            var now = NowIso();
            using var cmd = _db.CreateCommand();
            cmd.CommandText = """
                INSERT INTO comments (item_id, member_id, created_at, body)
                VALUES ($i, $m, $t, $b);
                SELECT last_insert_rowid();
                """;
            cmd.Parameters.AddWithValue("$i", itemId);
            cmd.Parameters.AddWithValue("$m", memberId);
            cmd.Parameters.AddWithValue("$t", now);
            cmd.Parameters.AddWithValue("$b", body);
            return ((long)(cmd.ExecuteScalar() ?? 0L), now);
        }
    }

    // ---- counts -------------------------------------------------------------------

    public (long Items, long Members) Counts()
    {
        lock (_gate)
        {
            using var cmd = _db.CreateCommand();
            cmd.CommandText = """
                SELECT (SELECT COUNT(*) FROM share_items WHERE deleted_at IS NULL),
                       (SELECT COUNT(*) FROM members);
                """;
            using var r = cmd.ExecuteReader();
            r.Read();
            return (r.GetInt64(0), r.GetInt64(1));
        }
    }

    // ---- plumbing -------------------------------------------------------------------

    /// <summary>Caller holds the gate.</summary>
    private ShareItemRow? GetItemLocked(string itemId)
    {
        using var cmd = _db.CreateCommand();
        cmd.CommandText = ItemSelect + " AND si.item_id = $id LIMIT 1;";
        cmd.Parameters.AddWithValue("$id", itemId);
        return ReadItems(cmd).FirstOrDefault();
    }

    private static List<ShareItemRow> ReadItems(SqliteCommand cmd)
    {
        var list = new List<ShareItemRow>();
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            var shot = new Shot(
                r.GetInt64(3), r.GetString(4), DateTimeOffset.Parse(r.GetString(5)),
                r.GetInt32(6), r.GetInt32(7), r.GetString(8), r.GetString(9), r.GetInt64(10));
            list.Add(new ShareItemRow(
                r.GetString(0), r.GetString(1), r.GetString(2), shot,
                (int)r.GetInt64(11), r.IsDBNull(12) ? null : r.GetString(12)));
        }
        return list;
    }

    private static string HashHex(string secret)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(secret)));

    /// <summary>Share timestamps are stamped UTC round-trip so every stored
    /// value is directly comparable to every other.</summary>
    private static string NowIso() => DateTimeOffset.UtcNow.ToString("o");

    /// <summary>Display names: control characters stripped (an embedded
    /// newline would let a joiner forge lines in esgee.log, the audit trail
    /// every client renders these names from), trimmed, capped, never empty
    /// (null instead).</summary>
    private static string? Trimmed(string? name)
    {
        if (name is null) return null;
        var t = string.Concat(name.Where(c => !char.IsControl(c))).Trim();
        if (t.Length == 0) return null;
        return t.Length <= 64 ? t : t[..64];
    }

    private static void TryDelete(string path)
    {
        try { if (System.IO.File.Exists(path)) System.IO.File.Delete(path); }
        catch (Exception ex)
        {
            // The row is already gone, so a file surviving here is an orphan
            // nothing will ever retry — the log is its only trace.
            Log.Warn($"share: deleted item's file could not be removed ({path}): {ex.Message}");
        }
    }

    public void Dispose()
    {
        _db.Dispose();
        Shots.Dispose();
    }
}
