using System.IO;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Esgee.Peers;
using Esgee.Store;

namespace Esgee.Shares;

/// <summary>How a join attempt ended. NeedDisplayName and NameTaken are
/// retryable — the server left the invite unconsumed; BadInvite is final for
/// that code; Unreachable says nothing about the invite at all.</summary>
public enum ShareJoinStatus { Joined, NeedDisplayName, NameTaken, BadInvite, Unreachable }

/// <summary>Result of ShareClient.JoinAsync. Result is non-null exactly when
/// Status is Joined.</summary>
public sealed record ShareJoinAttempt(ShareJoinStatus Status, ShareJoinResult? Result = null);

/// <summary>
/// Client side of the share routes — how the Windows app (and the Mac app)
/// consumes a share. Same addressing rule as PeerClient: the base URL is
/// opaque and routes are appended to it, so a tailnet node
/// (http://100.x.y.z:43118) and a hypothetical hosted relay
/// (https://relay.example/s/team) differ only in a string.
///
/// One instance per share, authenticated with THIS member's token — never a
/// shared secret. All calls are async and safe from any thread.
/// </summary>
public sealed class ShareClient : IDisposable
{
    private readonly HttpClient _http;

    public string BaseUrl { get; }

    /// <summary>Display name for logs, captions, and the cache directory —
    /// the settings entry's Name. Falls back to the URL pre-join, when no
    /// name is known yet.</summary>
    public string Name { get; }

    public ShareClient(string baseUrl, string memberToken, string name = "")
    {
        BaseUrl = baseUrl.TrimEnd('/');
        Name = name.Length > 0 ? name : BaseUrl;
        _http = new HttpClient
        {
            // Trailing slash + relative route paths = true concatenation, so a
            // base URL that carries a path (a hosted relay) keeps working.
            BaseAddress = new Uri(BaseUrl + "/"),
            Timeout = TimeSpan.FromMinutes(5), // big MP4 pulls over a relay link
        };
        _http.DefaultRequestHeaders.Add(PeerProtocol.TokenHeader, memberToken);
    }

    // ---- queries ------------------------------------------------------------

    public async Task<PingDto?> PingAsync(TimeSpan timeout)
    {
        using var cts = new CancellationTokenSource(timeout);
        return await _http.GetFromJsonAsync<PingDto>("ping", PeerProtocol.Json, cts.Token);
    }

    public Task<ShareInfoDto?> InfoAsync()
        => _http.GetFromJsonAsync<ShareInfoDto?>("share", PeerProtocol.Json);

    public async Task<List<ShareMemberDto>> MembersAsync()
        => await _http.GetFromJsonAsync<List<ShareMemberDto>>(
               "share/members", PeerProtocol.Json) ?? [];

    /// <summary>Items newest first plus tombstones. <paramref name="since"/> is
    /// a timestamp a previous response returned (shared_at / latest_comment_at
    /// / deleted_at) — the "anything new?" poll behind the notification dot.
    /// A since page arrives oldest activity first; when Truncated is true,
    /// advance since to the newest timestamp received and poll again.</summary>
    public async Task<ShareItemsPage> ItemsAsync(string? since = null, int n = 200)
    {
        var route = $"share/items?n={n}" +
                    (since is null ? "" : $"&since={Uri.EscapeDataString(since)}");
        var page = await _http.GetFromJsonAsync<ShareItemsPage>(route, PeerProtocol.Json)
                   ?? new ShareItemsPage([], []);
        DropInvalidIds(page);
        Log.Info($"share {BaseUrl}: /share/items -> {page.Items.Count} items, {page.Deleted.Count} tombstones");
        return page;
    }

    public async Task<List<ShareItemDto>> SearchAsync(string query)
    {
        var list = await _http.GetFromJsonAsync<List<ShareItemDto>>(
            $"share/search?q={Uri.EscapeDataString(query)}", PeerProtocol.Json) ?? [];
        var dropped = list.RemoveAll(i => !IsValidItemId(i.Item));
        if (dropped > 0)
            Log.Warn($"share {BaseUrl}: dropped {dropped} search result(s) with malformed item ids");
        Log.Info($"share {BaseUrl}: /share/search \"{query}\" -> {list.Count} items");
        return list;
    }

    /// <summary>The full item: metadata, OCR text, comments. Null on 404 —
    /// deleted items vanish from this route and surface as tombstones instead.</summary>
    public async Task<ShareItemDto?> ItemAsync(string item)
    {
        if (!IsValidItemId(item)) return null;
        using var resp = await _http.GetAsync($"share/items/{Uri.EscapeDataString(item)}");
        if (!resp.IsSuccessStatusCode) return null;
        var dto = await resp.Content.ReadFromJsonAsync<ShareItemDto>(PeerProtocol.Json);
        return dto is not null && IsValidItemId(dto.Item) ? dto : null;
    }

    /// <summary>Item ids become LOCAL FILE NAMES (CachePathFor), so their
    /// shape is enforced at the trust boundary, not assumed: "itm_" plus
    /// ASCII alphanumerics, the shape the node mints (docs/PROTOCOL.md "Item
    /// identity" — "itm_" + 10 url-safe characters; a modest length range is
    /// tolerated so a future longer id doesn't brick older clients). Anything
    /// else — separators, dots, a rooted path like C:\…\Startup\x — is a
    /// hostile or corrupt server; Path.Combine would happily treat such an
    /// "id" as an escape from the cache directory, handing the server an
    /// arbitrary file write on whoever browses the share.</summary>
    public static bool IsValidItemId(string? id)
    {
        if (id is null || id.Length is < 5 or > 64 ||
            !id.StartsWith("itm_", StringComparison.Ordinal)) return false;
        for (var i = 4; i < id.Length; i++)
            if (!char.IsAsciiLetterOrDigit(id[i])) return false;
        return true;
    }

    /// <summary>Malformed ids never leave the wire layer: an item with one
    /// can't be rendered, cached, or (critically) turned into a path.</summary>
    private void DropInvalidIds(ShareItemsPage page)
    {
        var dropped = page.Items.RemoveAll(i => !IsValidItemId(i.Item)) +
                      page.Deleted.RemoveAll(t => !IsValidItemId(t.Item));
        if (dropped > 0)
            Log.Warn($"share {BaseUrl}: dropped {dropped} entr(ies) with malformed item ids " +
                     "(not the id shape an esgee share node mints — hostile or corrupt server?)");
    }

    public Task<byte[]> ThumbAsync(string item)
        => _http.GetByteArrayAsync($"share/items/{Uri.EscapeDataString(item)}/thumb");

    /// <summary>Downloads the item's original bytes (?alt=gif|thumb for a
    /// recording's siblings) to <paramref name="destPath"/>. Atomic: lands as
    /// .part first so a torn transfer never looks like a finished file.</summary>
    public async Task DownloadAsync(string item, string destPath, string? alt = null)
    {
        var route = $"share/items/{Uri.EscapeDataString(item)}/file" +
                    (alt is null ? "" : $"?alt={alt}");
        // Temp name unique per ATTEMPT, not per destination: the same item can
        // be racing down twice (a refresh replaced the Entry mid-prefetch, or
        // two archive processes browse the same share), and a shared temp name
        // makes the loser throw a sharing violation and its cleanup delete the
        // winner's half-written file. The last finished move wins; identical
        // bytes make that harmless.
        var tmp = destPath + "." + Guid.NewGuid().ToString("N")[..8] + ".part";
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(destPath)!);
            using var response = await _http.GetAsync(route, HttpCompletionOption.ResponseHeadersRead);
            response.EnsureSuccessStatusCode();
            await using (var fs = File.Create(tmp))
                await response.Content.CopyToAsync(fs);
            try
            {
                File.Move(tmp, destPath, overwrite: true);
            }
            catch (Exception) when (File.Exists(destPath))
            {
                // Two racing moves can collide on the destination itself; the
                // bytes are identical, so whoever landed is right.
            }
        }
        finally
        {
            try { if (File.Exists(tmp)) File.Delete(tmp); } catch { }
        }
    }

    // ---- local materialization ------------------------------------------------
    // Same shape as PeerClient's: anything OS-facing (drag, clipboard, video
    // playback, ingest) gets a REAL local file out of the cache first.

    /// <summary>Cache path this share item will occupy locally, under
    /// peercache\share_&lt;name&gt;. A share item deliberately has no
    /// client-visible file NAME (docs/PROTOCOL.md "Item identity"), so the
    /// share-assigned id names the cache file; file_ext supplies the stored
    /// extension so a JPEG a teammate shared isn't dragged out (or pulled in)
    /// labeled .png. SafeExtension re-checks the server's value and falls
    /// back to the kind's default — also what a pre-file_ext node gets.
    /// The id is wire data used as a path component, so it is re-validated
    /// here even though every fetch path already filters: defense in depth
    /// against a server-chosen string escaping the cache directory.</summary>
    public string CachePathFor(ShareItemDto item)
    {
        if (!IsValidItemId(item.Item))
            throw new InvalidOperationException(
                $"share item id '{item.Item}' is not an esgee item id; refusing to use it as a file name");
        return Path.Combine(PeerClient.CacheRoot, "share_" + Sanitize(Name),
            item.Item + ShotStore.SafeExtension(item.FileExt, item.Kind));
    }

    /// <summary>Downloads the item (and, for recordings, the GIF + preview
    /// frame siblings) into the cache, then returns a Shot pointing at the
    /// LOCAL copy — from there the ordinary DragSource/preview/ingest paths
    /// work unchanged. No-op when already cached.</summary>
    public async Task<Shot> EnsureLocalAsync(ShareItemDto item)
    {
        var dest = CachePathFor(item);
        if (!File.Exists(dest))
        {
            await DownloadAsync(item.Item, dest);
            if (item.Kind == "video")
            {
                if (item.HasGif)
                    await DownloadAsync(item.Item, Path.ChangeExtension(dest, ".gif"), "gif");
                try { await DownloadAsync(item.Item, dest + ".png", "thumb"); }
                catch { /* preview frame is optional — the MP4 is the item */ }
            }

            Log.Info($"share {Name}: cached item {item.Item} -> {dest} " +
                     $"({new FileInfo(dest).Length / 1024} KB)");
        }

        return ToLocalShot(item, dest);
    }

    /// <summary>A Shot over the (eventual) cache copy — the single shape the
    /// archive grid consumes. Origin carries the share's name; the Id is
    /// synthetic (share ids are strings the share assigned), stable per item
    /// so preview navigation can match entries by it.</summary>
    public Shot ToLocalShot(ShareItemDto item, string localPath) => new(
        SyntheticId(item.Item), localPath, DateTimeOffset.Parse(item.TakenAt),
        item.Width, item.Height, item.Sha256, item.Kind, item.DurationMs, Name);

    private static long SyntheticId(string item)
        => BitConverter.ToInt64(
            System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(item)));

    private static string Sanitize(string name)
        => string.Concat(name.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c));

    // ---- writes ---------------------------------------------------------------

    /// <summary>POST /share/items — the explicit per-capture act that is the
    /// ONLY way anything enters a share. The response is the item (the share's
    /// id for it, not any local id); Duplicate=true means the sha256 was
    /// already shared and the existing item came back — success either way.</summary>
    public async Task<ShareItemDto> PostItemAsync(
        SharePostMeta meta, string filePath, string? gifPath = null, string? thumbPath = null)
    {
        using var form = new MultipartFormDataContent();
        form.Add(new StringContent(JsonSerializer.Serialize(meta, PeerProtocol.Json)), "meta");
        form.Add(new ByteArrayContent(await File.ReadAllBytesAsync(filePath)),
            "file", Path.GetFileName(filePath));
        if (gifPath is not null && File.Exists(gifPath))
            form.Add(new ByteArrayContent(await File.ReadAllBytesAsync(gifPath)),
                "gif", Path.GetFileName(gifPath));
        if (thumbPath is not null && File.Exists(thumbPath))
            form.Add(new ByteArrayContent(await File.ReadAllBytesAsync(thumbPath)),
                "thumb", Path.GetFileName(thumbPath));

        using var response = await _http.PostAsync("share/items", form);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<ShareItemDto>(PeerProtocol.Json)
               ?? throw new HttpRequestException("empty share post response");
    }

    /// <summary>DELETE /share/items/{item}. Throws on 403 (not the author, not
    /// the operator) and 404 — the server decides authorship from the token.</summary>
    public async Task DeleteItemAsync(string item)
    {
        using var resp = await _http.DeleteAsync($"share/items/{Uri.EscapeDataString(item)}");
        resp.EnsureSuccessStatusCode();
    }

    public async Task<ShareCommentDto> CommentAsync(string item, string body)
    {
        using var resp = await _http.PostAsync(
            $"share/items/{Uri.EscapeDataString(item)}/comments",
            JsonBody(new ShareCommentRequest(body)));
        resp.EnsureSuccessStatusCode();
        return await resp.Content.ReadFromJsonAsync<ShareCommentDto>(PeerProtocol.Json)
               ?? throw new HttpRequestException("empty comment response");
    }

    // ---- joining --------------------------------------------------------------

    /// <summary>POST /share/join — redeems a single-use invite for THIS
    /// member's own token. The one share call made without a token. The
    /// status keeps the retryable answers apart from the fatal one: the
    /// server does NOT consume the invite on NeedDisplayName or NameTaken
    /// (docs/PROTOCOL.md "Identity and tokens"), so a join UI must offer a
    /// retry with a (different) name instead of reporting a dead code.
    /// BadInvite folds spent, expired, and wrong together — the server
    /// answers all three identically on purpose.</summary>
    public static async Task<ShareJoinAttempt> JoinAsync(
        string baseUrl, string invite, string displayName, TimeSpan? timeout = null)
    {
        using var http = new HttpClient { Timeout = timeout ?? TimeSpan.FromSeconds(10) };
        try
        {
            using var resp = await http.PostAsync(
                baseUrl.TrimEnd('/') + "/share/join",
                JsonBody(new ShareJoinRequest(invite, displayName)));
            if (resp.IsSuccessStatusCode)
            {
                var result = await resp.Content.ReadFromJsonAsync<ShareJoinResult>(PeerProtocol.Json);
                return result is { Token.Length: > 0 }
                    ? new ShareJoinAttempt(ShareJoinStatus.Joined, result)
                    : new ShareJoinAttempt(ShareJoinStatus.Unreachable); // a 200 without a token is not an esgee share
            }

            return await ReadErrorAsync(resp) switch
            {
                "display_name required" => new ShareJoinAttempt(ShareJoinStatus.NeedDisplayName),
                "display_name taken" => new ShareJoinAttempt(ShareJoinStatus.NameTaken),
                _ => new ShareJoinAttempt(ShareJoinStatus.BadInvite),
            };
        }
        catch
        {
            return new ShareJoinAttempt(ShareJoinStatus.Unreachable);
        }
    }

    /// <summary>The error string of a non-success share response — the bodies
    /// are normative (docs/PROTOCOL.md), so they are safe to branch on.</summary>
    private static async Task<string?> ReadErrorAsync(HttpResponseMessage resp)
    {
        try
        {
            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
            return doc.RootElement.TryGetProperty("error", out var e) ? e.GetString() : null;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Parses an invite URL as minted by `esgee-node --share-invite`:
    /// esgee-share://&lt;host&gt;:&lt;port&gt;#&lt;code&gt; (the part before the
    /// fragment is the base URL with its scheme swapped for http — tailnet
    /// endpoints are plain HTTP inside WireGuard).</summary>
    public static (string BaseUrl, string Code)? ParseInviteUrl(string url)
    {
        const string scheme = "esgee-share://";
        url = url.Trim();
        if (!url.StartsWith(scheme, StringComparison.OrdinalIgnoreCase)) return null;

        var hash = url.IndexOf('#');
        if (hash < 0) return null;

        var authority = url[scheme.Length..hash].TrimEnd('/');
        var code = url[(hash + 1)..].Trim();
        if (authority.Length == 0 || code.Length == 0) return null;

        var baseUrl = "http://" + authority;
        return Uri.TryCreate(baseUrl + "/", UriKind.Absolute, out _)
            ? (baseUrl, code) : null;
    }

    private static StringContent JsonBody(object body) => new(
        JsonSerializer.Serialize(body, body.GetType(), PeerProtocol.Json),
        Encoding.UTF8, "application/json");

    public void Dispose() => _http.Dispose();
}
