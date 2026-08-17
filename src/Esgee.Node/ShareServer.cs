using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text.Json;
using Esgee.Peers;
using Esgee.Shares;
using Esgee.Store;

namespace Esgee.Node;

/// <summary>
/// The share routes (docs/PROTOCOL.md) over one share archive — the same
/// hand-rolled TcpListener responder as PeerServer, because the no-framework
/// rationale holds even better on a headless node. Lives in the node on
/// purpose: the WPF app never serves shares, it only consumes them through
/// ShareClient.
///
/// Security model differs from the peer server in exactly one way: tokens are
/// per-MEMBER, not per-mesh. Every request resolves its token to a member row
/// (constant-time, against stored hashes) and all authorship — shared_by,
/// comment author, delete rights — derives from that member, never from a
/// client-supplied field. POST /share/join is the only tokenless route; the
/// invite code is the credential there, and redeeming it is what mints a token.
/// </summary>
public sealed class ShareServer : IDisposable
{
    private readonly TcpListener _listener;
    private readonly ShareStore _store;
    private readonly IThumbEncoder _thumbs;
    private readonly string _name;
    private readonly int _retentionDays;
    private readonly CancellationTokenSource _cts = new();
    private readonly Timer? _retention;

    // Two simultaneous posts of the same capture must not race past the sha
    // probe and mint two item ids for one sha256 — item identity is the anchor
    // comments hang off. One post at a time; posts are milliseconds.
    private readonly SemaphoreSlim _postGate = new(1, 1);

    // This is the one esgee server deliberately reachable by devices the
    // owner does not control (Tailscale device sharing), so pre-auth exposure
    // is bounded three ways: bodies without a member token stay tiny, every
    // request read has a hard deadline (Socket.ReceiveTimeout governs only
    // synchronous reads — it never fires for the async reads used here), and
    // concurrent connections are capped instead of queued into the heap.
    private const long PreAuthMaxBody = 256 * 1024;
    private static readonly TimeSpan RequestReadDeadline = TimeSpan.FromMinutes(5);
    private const int MaxConcurrentConnections = 32;
    private int _connections;

    // Comments are stored for the item's whole life and replayed to every
    // member on each detail fetch — a cap keeps one cheap authenticated write
    // from taxing every reader forever. Display names get 64; notes get more.
    private const int MaxCommentChars = 4096;

    // What this implementation serves (docs/PROTOCOL.md "Capability
    // negotiation"): the share routes including comment writes. The
    // annotation-layer route is the one annotate surface still designed-only.
    private static readonly string[] Capabilities = ["share", "annotate"];

    public string BoundAddress { get; }

    private ShareServer(TcpListener listener, ShareStore store, IThumbEncoder thumbs,
        string name, int retentionDays, string bound)
    {
        _listener = listener;
        _store = store;
        _thumbs = thumbs;
        _name = name;
        _retentionDays = retentionDays;
        BoundAddress = bound;
        _ = Task.Run(AcceptLoopAsync);

        // Retention runs on startup and hourly — cheap enough that precision
        // would buy nothing. 0 = unlimited, no timer at all.
        if (retentionDays > 0)
            _retention = new Timer(_ => Sweep(), null, TimeSpan.Zero, TimeSpan.FromHours(1));
    }

    /// <summary>Same bind contract as the peer server: an explicit --bind names
    /// a specific interface (loopback for tests), otherwise the machine's own
    /// Tailscale IPv4; 0.0.0.0 is refused, and no start without it.</summary>
    public static ShareServer? TryStart(ShareStore store, string shareName, int retentionDays,
        int port, IThumbEncoder thumbs, string? bindIp = null)
    {
        var ip = HttpIo.ResolveBindAddress(bindIp, "share");
        if (ip is null) return null;

        try
        {
            var listener = new TcpListener(IPAddress.Parse(ip), port);
            listener.Start();
            var bound = $"{ip}:{port}";
            Log.Info($"share: serving \"{shareName}\" ({store.Shots.Root}) on http://{bound} " +
                     (bindIp is null ? "(tailscale interface only)" : "(explicit --bind)") +
                     (retentionDays > 0 ? $", retention {retentionDays}d" : ", retention unlimited"));
            return new ShareServer(listener, store, thumbs, shareName, retentionDays, bound);
        }
        catch (Exception ex)
        {
            Log.Error($"share: failed to bind {ip}:{port}: {ex.Message}");
            return null;
        }
    }

    private void Sweep()
    {
        try
        {
            var removed = _store.SweepRetention(_retentionDays);
            if (removed.Count > 0)
                Log.Info($"share: retention removed {removed.Count} item(s) older than " +
                         $"{_retentionDays}d ({string.Join(", ", removed)}) — tombstones kept");
        }
        catch (Exception ex)
        {
            Log.Warn($"share: retention sweep failed: {ex.Message}");
        }
    }

    private async Task AcceptLoopAsync()
    {
        while (!_cts.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await _listener.AcceptTcpClientAsync(_cts.Token);
            }
            catch (OperationCanceledException) { break; }
            catch (ObjectDisposedException) { break; }
            catch (Exception ex)
            {
                Log.Warn($"share: accept failed: {ex.Message}");
                continue;
            }

            _ = Task.Run(() => HandleConnectionAsync(client));
        }
    }

    private async Task HandleConnectionAsync(TcpClient client)
    {
        var remote = client.Client.RemoteEndPoint?.ToString() ?? "?";
        if (Interlocked.Increment(ref _connections) > MaxConcurrentConnections)
        {
            Interlocked.Decrement(ref _connections);
            Log.Warn($"share: connection from {remote} dropped ({MaxConcurrentConnections} already open)");
            client.Dispose();
            return;
        }
        try
        {
            client.SendTimeout = 60_000;
            using var stream = client.GetStream();

            // The deadline covers the whole request read: a client that sends
            // headers and goes silent must release its buffer, not hold it
            // until server shutdown. Five minutes matches ShareClient's own
            // timeout — room for a recording upload, not for a stall.
            using var readCts = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token);
            readCts.CancelAfter(RequestReadDeadline);
            var request = await HttpRequest.ReadAsync(stream, readCts.Token, BodyLimit);
            if (request is null) return;

            // Bodies are Content-Length framed only (docs/PROTOCOL.md
            // "Transport") — same refusal, by name, as the peer server.
            if (request.Headers.TryGetValue("Transfer-Encoding", out var te) &&
                te.Contains("chunked", StringComparison.OrdinalIgnoreCase))
            {
                Log.Warn($"share: 411 {request.Method} {request.RawPath} from {remote} (chunked body)");
                await HttpIo.WriteJsonAsync(stream, 411, new { error = "content-length required" });
                return;
            }

            // The one tokenless route: the caller has no token yet — redeeming
            // the invite is how they get one.
            if (request.Method == "POST" && request.Path == "/share/join")
            {
                await HandleJoinAsync(stream, request, remote);
                return;
            }

            var member = Authorize(request);
            if (member is null)
            {
                Log.Warn($"share: 401 {request.Method} {request.RawPath} from {remote}");
                await HttpIo.WriteJsonAsync(stream, 401, new { error = "missing or wrong token" });
                return;
            }

            await RouteAsync(stream, request, member, remote);
        }
        catch (Exception ex)
        {
            Log.Warn($"share: connection from {remote} failed: {ex.Message}");
        }
        finally
        {
            Interlocked.Decrement(ref _connections);
            client.Dispose();
        }
    }

    /// <summary>Body cap for a request whose headers just arrived, consulted
    /// BEFORE any body allocation. Only an authenticated member posting a
    /// capture gets the 1 GB upload ceiling; every other body on this server
    /// is small JSON, so a stranger's Content-Length buys 256 KB at most.</summary>
    private long BodyLimit(HttpRequest req)
        => req.Method == "POST" && req.Path == "/share/items" && Authorize(req) is not null
            ? HttpRequest.MaxBody
            : PreAuthMaxBody;

    private ShareMember? Authorize(HttpRequest req)
        => req.Headers.TryGetValue(PeerProtocol.TokenHeader, out var token)
            ? _store.ResolveToken(token)
            : null;

    private async Task RouteAsync(NetworkStream stream, HttpRequest req,
        ShareMember member, string remote)
    {
        var path = req.Path;

        if (req.Method == "GET" && path == "/ping")
        {
            Log.Info($"share: /ping from {remote} ({member.MemberId})");
            var (items, _) = _store.Counts();
            await HttpIo.WriteJsonAsync(stream, 200, new PingDto(
                "esgee", AppVersion.Current, PeerProtocol.Version,
                Environment.MachineName, items, Capabilities));
            return;
        }

        if (req.Method == "GET" && path == "/share")
        {
            var (items, members) = _store.Counts();
            await HttpIo.WriteJsonAsync(stream, 200, new ShareInfoDto(
                _store.ShareId, _name, (int)members, items, _retentionDays));
            return;
        }

        if (req.Method == "GET" && path == "/share/members")
        {
            await HttpIo.WriteJsonAsync(stream, 200, _store.Members()
                .Select(m => new ShareMemberDto(m.MemberId, m.DisplayName, m.JoinedAt, m.Role))
                .ToList());
            return;
        }

        if (req.Method == "GET" && path == "/share/items")
        {
            await HandleListAsync(stream, req, remote);
            return;
        }

        if (req.Method == "GET" && path == "/share/search")
        {
            var q = req.Query("q") ?? "";
            List<ShareItemRow> rows;
            try
            {
                // FtsQuery, not raw text: PROTOCOL.md promises the same
                // quoting rules as the peer /search, everywhere.
                rows = q.Trim().Length == 0
                    ? _store.LiveItems(200)
                    : _store.SearchItems(ShotStore.FtsQuery(q), 200);
            }
            catch (Exception ex)
            {
                Log.Warn($"share: /share/search \"{q}\" failed: {ex.Message}");
                rows = [];
            }
            Log.Info($"share: /share/search \"{q}\" -> {rows.Count} from {remote}");
            await HttpIo.WriteJsonAsync(stream, 200, rows.Select(r => ToDto(r)).ToList());
            return;
        }

        if (req.Method == "POST" && path == "/share/items")
        {
            await HandlePostItemAsync(stream, req, member, remote);
            return;
        }

        if (TryItemRoute(path, out var itemId, out var sub))
        {
            switch (sub)
            {
                case null when req.Method == "GET":
                    await HandleItemDetailAsync(stream, itemId);
                    return;
                case null when req.Method == "DELETE":
                    await HandleDeleteAsync(stream, itemId, member, remote);
                    return;
                case "thumb" when req.Method == "GET":
                    await HandleThumbAsync(stream, itemId);
                    return;
                case "file" when req.Method == "GET":
                    await HandleFileAsync(stream, req, itemId, remote);
                    return;
                case "comments" when req.Method == "POST":
                    await HandleCommentAsync(stream, req, itemId, member, remote);
                    return;
            }
        }

        await HttpIo.WriteJsonAsync(stream, 404, new { error = "no such endpoint" });
    }

    /// <summary>GET /share/items?since=&amp;n= — live items newest first plus
    /// tombstones. ?since= keeps only what changed after it: new items, new
    /// deletions, and items whose comments moved (the notification-dot poll).</summary>
    private async Task HandleListAsync(NetworkStream stream, HttpRequest req, string remote)
    {
        var n = Math.Clamp(req.QueryInt("n") ?? 200, 1, 1000);
        var sinceRaw = req.Query("since");

        DateTimeOffset? since = null;
        if (!string.IsNullOrEmpty(sinceRaw))
        {
            if (!DateTimeOffset.TryParse(sinceRaw, out var parsed))
            {
                await HttpIo.WriteJsonAsync(stream, 400, new { error = "bad since" });
                return;
            }
            since = parsed;
        }

        // Timestamps compare parsed, not lexically — clients echo back server
        // timestamps, but nothing forces every writer to one offset format.
        List<ShareItemRow> rows;
        bool? truncated = null;
        if (since is { } cut)
        {
            // A delta poll pages OLDEST activity first: when more than n items
            // changed, the client's advanced cursor still lies before every
            // row held back, so the next poll resumes instead of skipping —
            // newest-first would silently drop exactly the old items a fresh
            // comment resurfaced. truncated:true says another poll is due.
            var changed = _store.LiveItems()
                .Where(r => After(r.SharedAt, cut) || After(r.LatestCommentAt, cut))
                .OrderBy(ActivityAt).ToList();
            if (changed.Count > n) truncated = true;
            rows = changed.Take(n).ToList();
        }
        else
            rows = _store.LiveItems(n);

        var stones = _store.Tombstones()
            .Where(t => since is not { } c || After(t.DeletedAt, c))
            .Select(t => new ShareTombstoneDto(t.ItemId, t.DeletedAt))
            .ToList();

        Log.Info($"share: /share/items{(since is null ? "" : " since=…")} " +
                 $"-> {rows.Count} items, {stones.Count} tombstones" +
                 $"{(truncated is true ? " (truncated)" : "")} from {remote}");
        await HttpIo.WriteJsonAsync(stream, 200,
            new ShareItemsPage(rows.Select(r => ToDto(r)).ToList(), stones, truncated));
    }

    private static bool After(string? timestamp, DateTimeOffset cut)
        => timestamp is not null &&
           DateTimeOffset.TryParse(timestamp, out var at) && at > cut;

    /// <summary>When an item last moved: its newest comment, else its share
    /// stamp — the value a since-poll cursor advances over.</summary>
    private static DateTimeOffset ActivityAt(ShareItemRow r)
    {
        var shared = DateTimeOffset.TryParse(r.SharedAt, out var s) ? s : DateTimeOffset.MinValue;
        return r.LatestCommentAt is not null &&
               DateTimeOffset.TryParse(r.LatestCommentAt, out var c) && c > shared ? c : shared;
    }

    private async Task HandleItemDetailAsync(NetworkStream stream, string itemId)
    {
        if (_store.GetItem(itemId) is not { } row)
        {
            await HttpIo.WriteJsonAsync(stream, 404, new { error = "no such item" });
            return;
        }

        var (_, text, engine) = _store.Shots.GetOcr(row.Shot.Id);
        var comments = _store.Comments(itemId)
            .Select(c => new ShareCommentDto(c.Id, c.MemberId, c.DisplayName, c.CreatedAt, c.Body))
            .ToList();

        await HttpIo.WriteJsonAsync(stream, 200, ToDto(row) with
        {
            OcrText = text,
            OcrEngineVersion = engine,
            Comments = comments,
        });
    }

    private async Task HandleThumbAsync(NetworkStream stream, string itemId)
    {
        if (_store.GetItem(itemId) is not { } row || !System.IO.File.Exists(row.Shot.ThumbPath))
        {
            await HttpIo.WriteJsonAsync(stream, 404, new { error = "no thumbnail" });
            return;
        }
        byte[] jpeg;
        try
        {
            jpeg = _thumbs.EncodeThumb(row.Shot.ThumbPath);
        }
        catch (Exception ex)
        {
            Log.Warn($"share: thumb {itemId} failed: {ex.Message}");
            await HttpIo.WriteJsonAsync(stream, 500, new { error = "thumbnail failed" });
            return;
        }
        await HttpIo.WriteBytesAsync(stream, 200, "image/jpeg", jpeg);
    }

    private async Task HandleFileAsync(NetworkStream stream, HttpRequest req,
        string itemId, string remote)
    {
        if (_store.GetItem(itemId) is not { } row)
        {
            await HttpIo.WriteJsonAsync(stream, 404, new { error = "no such item" });
            return;
        }

        // ?alt=gif / ?alt=thumb — a recording's siblings, same contract as the
        // peer /file route.
        var alt = req.Query("alt");
        var shot = row.Shot;
        var filePath = alt switch
        {
            "gif" => shot.GifPath,
            "thumb" => shot.IsVideo && System.IO.File.Exists(shot.ThumbPath) ? shot.ThumbPath : null,
            _ => shot.Path,
        };
        if (filePath is null || !System.IO.File.Exists(filePath))
        {
            await HttpIo.WriteJsonAsync(stream, 404, new { error = "file missing" });
            return;
        }

        Log.Info($"share: /share/items/{itemId}/file{(alt is null ? "" : $"?alt={alt}")} " +
                 $"({new System.IO.FileInfo(filePath).Length / 1024} KB) from {remote}");
        await HttpIo.WriteFileAsync(stream, filePath);
    }

    /// <summary>POST /share/items — the explicit per-capture act. Dedupe is by
    /// sha256 against LIVE items: a re-share answers with the existing item and
    /// duplicate:true, so every member keeps one name for one capture.</summary>
    private async Task HandlePostItemAsync(NetworkStream stream, HttpRequest req,
        ShareMember member, string remote)
    {
        await _postGate.WaitAsync(_cts.Token);
        try
        {
            var parts = Multipart.Parse(req);
            if (parts is null)
            {
                await HttpIo.WriteJsonAsync(stream, 400, new { error = "expected multipart/form-data" });
                return;
            }

            var metaPart = parts.FirstOrDefault(p => p.Name == "meta");
            var filePart = parts.FirstOrDefault(p => p.Name == "file");
            if (metaPart is null || filePart is null)
            {
                await HttpIo.WriteJsonAsync(stream, 400, new { error = "need 'meta' and 'file' parts" });
                return;
            }

            SharePostMeta? meta;
            try
            {
                meta = JsonSerializer.Deserialize<SharePostMeta>(metaPart.Body, PeerProtocol.Json);
            }
            catch (Exception ex)
            {
                await HttpIo.WriteJsonAsync(stream, 400, new { error = $"bad meta json: {ex.Message}" });
                return;
            }
            if (meta is null || !DateTimeOffset.TryParse(meta.TakenAt, out var takenAt))
            {
                await HttpIo.WriteJsonAsync(stream, 400, new { error = "bad meta" });
                return;
            }

            var sha = Convert.ToHexString(SHA256.HashData(filePart.Body));
            if (!sha.Equals(meta.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                await HttpIo.WriteJsonAsync(stream, 400, new { error = "sha256 mismatch" });
                return;
            }

            if (_store.FindLiveBySha(sha) is { } existing)
            {
                Log.Info($"share: post from {member.MemberId} deduplicated (sha match, {existing.ItemId})");
                await HttpIo.WriteJsonAsync(stream, 200, ToDto(existing, duplicate: true));
                return;
            }

            var kind = string.IsNullOrEmpty(meta.Kind) ? "image" : meta.Kind;
            var ext = ShotStore.SafeExtension(
                System.IO.Path.GetExtension(meta.FileName ?? filePart.FileName ?? ""), kind);

            var dest = _store.Shots.PlanIngestPath(takenAt, ext);
            await System.IO.File.WriteAllBytesAsync(dest, filePart.Body, _cts.Token);
            if (parts.FirstOrDefault(p => p.Name == "gif") is { } gif)
                await System.IO.File.WriteAllBytesAsync(
                    System.IO.Path.ChangeExtension(dest, ".gif"), gif.Body, _cts.Token);
            if (parts.FirstOrDefault(p => p.Name == "thumb") is { } thumb)
                await System.IO.File.WriteAllBytesAsync(dest + ".png", thumb.Body, _cts.Token);

            // Origin is deliberately "" — a share item never records which
            // machine a capture came from (docs/SHARES.md "The invariant").
            var (shot, dupShots) = _store.Shots.Ingest(dest, sha, takenAt,
                meta.Width, meta.Height, kind, meta.DurationMs,
                meta.OcrText, meta.OcrEngineVersion ?? "", origin: "");

            if (dupShots)
            {
                // A shots row without a live item (an interrupted earlier post):
                // keep the original file, drop the fresh copy, reuse the row.
                try { System.IO.File.Delete(dest); } catch { }
                try { System.IO.File.Delete(System.IO.Path.ChangeExtension(dest, ".gif")); } catch { }
                try { System.IO.File.Delete(dest + ".png"); } catch { }
            }

            var item = _store.AddItem(shot, member.MemberId);
            Log.Info($"share: {item.ItemId} shared by {member.MemberId} ('{member.DisplayName}') — " +
                     $"{kind} {shot.Width}x{shot.Height}, " +
                     $"ocr {(meta.OcrText is null ? "absent" : $"sidecar [{meta.OcrEngineVersion}]")}, " +
                     $"from {remote}");
            await HttpIo.WriteJsonAsync(stream, 200, ToDto(item, duplicate: false));
        }
        finally
        {
            _postGate.Release();
        }
    }

    private async Task HandleDeleteAsync(NetworkStream stream, string itemId,
        ShareMember member, string remote)
    {
        var outcome = _store.DeleteItem(itemId, member.MemberId, member.IsOperator, out _);
        switch (outcome)
        {
            case ShareDeleteOutcome.Deleted:
                Log.Info($"share: {itemId} deleted by {member.MemberId} " +
                         $"({(member.IsOperator ? "operator" : "author")}) — tombstoned, files removed");
                await HttpIo.WriteJsonAsync(stream, 200, new { item = itemId, deleted = true });
                return;
            case ShareDeleteOutcome.Forbidden:
                Log.Warn($"share: 403 delete {itemId} by {member.MemberId} from {remote} (not author)");
                await HttpIo.WriteJsonAsync(stream, 403, new { error = "not your item" });
                return;
            default:
                await HttpIo.WriteJsonAsync(stream, 404, new { error = "no such item" });
                return;
        }
    }

    private async Task HandleCommentAsync(NetworkStream stream, HttpRequest req,
        string itemId, ShareMember member, string remote)
    {
        ShareCommentRequest? body = null;
        try { body = JsonSerializer.Deserialize<ShareCommentRequest>(req.Body, PeerProtocol.Json); }
        catch { /* falls through to 400 */ }

        var text = body?.Body?.Trim();
        if (string.IsNullOrEmpty(text))
        {
            await HttpIo.WriteJsonAsync(stream, 400, new { error = "empty comment" });
            return;
        }
        if (text.Length > MaxCommentChars)
        {
            await HttpIo.WriteJsonAsync(stream, 400, new { error = "comment too long" });
            return;
        }

        if (_store.AddComment(itemId, member.MemberId, text) is not { } added)
        {
            await HttpIo.WriteJsonAsync(stream, 404, new { error = "no such item" });
            return;
        }

        Log.Info($"share: comment {added.Id} on {itemId} by {member.MemberId} " +
                 $"('{member.DisplayName}', {text.Length} chars) from {remote}");
        await HttpIo.WriteJsonAsync(stream, 200, new ShareCommentDto(
            added.Id, member.MemberId, member.DisplayName, added.CreatedAt, text));
    }

    /// <summary>POST /share/join — invite in, token out. Spent, expired, and
    /// unknown invites are indistinguishable (401 "bad invite") so the route
    /// can't be used to probe the invite table. Codes are never logged.</summary>
    private async Task HandleJoinAsync(NetworkStream stream, HttpRequest req, string remote)
    {
        ShareJoinRequest? join = null;
        try { join = JsonSerializer.Deserialize<ShareJoinRequest>(req.Body, PeerProtocol.Json); }
        catch { /* falls through to 400 */ }
        if (join is null || string.IsNullOrWhiteSpace(join.Invite))
        {
            await HttpIo.WriteJsonAsync(stream, 400, new { error = "bad join request" });
            return;
        }

        var (outcome, memberId, token) = _store.RedeemInvite(join.Invite.Trim(), join.DisplayName);
        switch (outcome)
        {
            case ShareJoinOutcome.Joined:
                Log.Info($"share: /share/join from {remote} — invite redeemed, {memberId} joined");
                await HttpIo.WriteJsonAsync(stream, 200, new ShareJoinResult(token!, memberId!));
                return;
            case ShareJoinOutcome.NeedName:
                await HttpIo.WriteJsonAsync(stream, 400, new { error = "display_name required" });
                return;
            case ShareJoinOutcome.NameTaken:
                // The invite survives — the joiner retries under another name.
                Log.Warn($"share: /share/join from {remote} rejected (display name taken)");
                await HttpIo.WriteJsonAsync(stream, 400, new { error = "display_name taken" });
                return;
            default:
                Log.Warn($"share: /share/join from {remote} rejected (bad invite)");
                await HttpIo.WriteJsonAsync(stream, 401, new { error = "bad invite" });
                return;
        }
    }

    /// <summary>/share/items/{item}[/thumb|/file|/comments] — anything deeper
    /// or different falls through to 404.</summary>
    private static bool TryItemRoute(string path, out string itemId, out string? sub)
    {
        itemId = "";
        sub = null;
        const string prefix = "/share/items/";
        if (!path.StartsWith(prefix, StringComparison.Ordinal)) return false;

        var rest = path[prefix.Length..];
        var slash = rest.IndexOf('/');
        if (slash < 0) itemId = rest;
        else { itemId = rest[..slash]; sub = rest[(slash + 1)..]; }

        itemId = Uri.UnescapeDataString(itemId);
        return itemId.Length > 0 && sub is null or "thumb" or "file" or "comments";
    }

    /// <summary>The share item shape — docs/PROTOCOL.md is normative about
    /// what is NOT here: no machine name, no local row id, no path, no origin.
    /// FileExt is the stored extension alone (the sharer's file_name already
    /// contributed it at post time) so clients don't relabel a JPEG ".png".</summary>
    private static ShareItemDto ToDto(ShareItemRow r, bool? duplicate = null) => new(
        r.ItemId, r.Shot.Sha256, r.SharedBy, r.SharedAt, r.Shot.TakenAt.ToString("o"),
        r.Shot.Width, r.Shot.Height, r.Shot.Kind, r.Shot.DurationMs,
        r.Shot.GifPath is not null, r.CommentCount,
        HasAnnotations: false, LatestCommentAt: r.LatestCommentAt, Duplicate: duplicate,
        FileExt: System.IO.Path.GetExtension(r.Shot.Path));

    public void Dispose()
    {
        _cts.Cancel();
        _retention?.Dispose();
        try { _listener.Stop(); } catch { }
        _cts.Dispose();
        Log.Info("share: server stopped");
    }
}
