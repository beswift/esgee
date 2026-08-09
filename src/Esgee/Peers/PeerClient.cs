using System.IO;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using Esgee.Store;

namespace Esgee.Peers;

/// <summary>A peer we can talk to: display name + where its API lives.</summary>
public sealed record PeerInfo(string Name, string Host, int Port)
{
    public string BaseUrl => $"http://{Host}:{Port}";
    public override string ToString() => Name;
}

/// <summary>
/// Client side of the peer API. One instance per peer; all calls are async and
/// safe from any thread — the archive window keeps its dispatcher clean by
/// doing every network hop through Task-returning methods here.
///
/// Files fetched from a peer land in a local cache
/// (%LOCALAPPDATA%\esgee\peercache\&lt;peer&gt;\) so drag-out can hand
/// CF_HDROP a REAL local file — a drop target can't stream from a tailnet.
/// </summary>
public sealed class PeerClient : IDisposable
{
    private readonly HttpClient _http;

    public PeerInfo Peer { get; }

    public PeerClient(PeerInfo peer, string token)
    {
        Peer = peer;
        _http = new HttpClient
        {
            BaseAddress = new Uri(peer.BaseUrl),
            Timeout = TimeSpan.FromMinutes(5), // big MP4 pulls over a relay link
        };
        _http.DefaultRequestHeaders.Add(PeerProtocol.TokenHeader, token);
    }

    public static string CacheRoot => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "esgee", "peercache");

    // ---- queries ------------------------------------------------------------

    public async Task<PingDto?> PingAsync(TimeSpan timeout)
    {
        using var cts = new CancellationTokenSource(timeout);
        return await _http.GetFromJsonAsync<PingDto>("/ping", PeerProtocol.Json, cts.Token);
    }

    public async Task<List<ShotDto>> RecentAsync(int n)
    {
        var list = await _http.GetFromJsonAsync<List<ShotDto>>(
            $"/recent?n={n}", PeerProtocol.Json) ?? [];
        Log.Info($"peer {Peer.Name}: /recent n={n} -> {list.Count} items");
        return list;
    }

    public async Task<List<ShotDto>> SearchAsync(string query)
    {
        var list = await _http.GetFromJsonAsync<List<ShotDto>>(
            $"/search?q={Uri.EscapeDataString(query)}", PeerProtocol.Json) ?? [];
        Log.Info($"peer {Peer.Name}: /search \"{query}\" -> {list.Count} items");
        return list;
    }

    public Task<ShotDto?> MetaAsync(long id)
        => _http.GetFromJsonAsync<ShotDto?>($"/meta/{id}", PeerProtocol.Json);

    public Task<byte[]> ThumbAsync(long id)
        => _http.GetByteArrayAsync($"/thumb/{id}");

    // ---- local materialization ----------------------------------------------

    /// <summary>Cache path this peer's shot will occupy locally. Prefixed with
    /// the id so two same-named captures from different days can't collide.</summary>
    public string CachePathFor(ShotDto shot)
        => Path.Combine(CacheRoot, Sanitize(Peer.Name), $"{shot.Id}_{shot.FileName}");

    public bool IsCached(ShotDto shot) => File.Exists(CachePathFor(shot));

    /// <summary>
    /// Downloads the shot's file (and, for recordings, the GIF + preview-frame
    /// siblings) into the peer cache, then returns a Shot pointing at the LOCAL
    /// copy — from there the ordinary DragSource/preview code paths work
    /// unchanged. No-op when already cached.
    /// </summary>
    public async Task<Shot> EnsureLocalAsync(ShotDto shot)
    {
        var dest = CachePathFor(shot);
        if (!File.Exists(dest))
        {
            Directory.CreateDirectory(Path.GetDirectoryName(dest)!);

            await DownloadAsync($"/file/{shot.Id}", dest);
            if (shot.Kind == "video")
            {
                if (shot.HasGif)
                    await DownloadAsync($"/file/{shot.Id}?alt=gif", Path.ChangeExtension(dest, ".gif"));
                await DownloadAsync($"/file/{shot.Id}?alt=thumb", dest + ".png", optional: true);
            }

            Log.Info($"peer {Peer.Name}: cached shot {shot.Id} -> {dest} " +
                     $"({new FileInfo(dest).Length / 1024} KB)");
        }

        return ToLocalShot(shot, dest);
    }

    public Shot ToLocalShot(ShotDto shot, string localPath) => new(
        shot.Id, localPath, DateTimeOffset.Parse(shot.TakenAt),
        shot.Width, shot.Height, shot.Sha256, shot.Kind, shot.DurationMs,
        shot.Origin.Length > 0 ? shot.Origin : Peer.Name);

    private async Task DownloadAsync(string route, string dest, bool optional = false)
    {
        var tmp = dest + ".part";
        try
        {
            using var response = await _http.GetAsync(route, HttpCompletionOption.ResponseHeadersRead);
            if (!response.IsSuccessStatusCode)
            {
                if (optional) return;
                throw new HttpRequestException($"{route}: {(int)response.StatusCode}");
            }
            await using (var fs = File.Create(tmp))
                await response.Content.CopyToAsync(fs);
            File.Move(tmp, dest, overwrite: true);
        }
        finally
        {
            try { if (File.Exists(tmp)) File.Delete(tmp); } catch { }
        }
    }

    // ---- push ---------------------------------------------------------------

    /// <summary>POST /ingest: the media file plus its JSON sidecar (and any
    /// recording siblings). The receiver dedupes by sha256, so retries are safe.</summary>
    public async Task<IngestResult> IngestAsync(
        IngestMeta meta, string filePath, string? gifPath, string? thumbPath)
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

        using var response = await _http.PostAsync("/ingest", form);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<IngestResult>(PeerProtocol.Json)
               ?? throw new HttpRequestException("empty ingest response");
    }

    // ---- discovery ----------------------------------------------------------

    /// <summary>
    /// Peers worth showing in the machine switcher: every tailnet node (from
    /// `tailscale status --json`) that answers /ping with our token, plus any
    /// manual Settings.Peers entries ("name=host:port" or "host:port"). Probes
    /// run in parallel with a short timeout; offline nodes just don't appear.
    /// Includes this machine itself when its own server is up — that self-peer
    /// loopback is the supported way to test the peer layer with one machine.
    /// </summary>
    public static async Task<List<(PeerInfo Info, PingDto Ping)>> DiscoverAsync(Settings settings)
    {
        var probes = CandidatePeers(settings)
            .Select(async info =>
            {
                using var client = new PeerClient(info, settings.PeerToken);
                try
                {
                    var ping = await client.PingAsync(TimeSpan.FromSeconds(2));
                    return ping is { App: "esgee" } ? (info, ping) : ((PeerInfo, PingDto)?)null;
                }
                catch
                {
                    return null; // not running esgee peers, or offline — fine
                }
            })
            .ToList();

        var results = await Task.WhenAll(probes);
        var found = results.Where(r => r is not null).Select(r => r!.Value).ToList();
        Log.Info($"peers: discovery probed {probes.Count} candidates, found {found.Count}");
        return found;
    }

    /// <summary>Everywhere a peer might live: online tailnet nodes (self
    /// included — the loopback config is supported) plus manual Settings.Peers
    /// entries, deduped by address. Shared by discovery and pairing.</summary>
    public static List<PeerInfo> CandidatePeers(Settings settings)
    {
        var candidates = new List<PeerInfo>();

        foreach (var node in Tailscale.Nodes().Where(n => n.Online))
            candidates.Add(new PeerInfo(node.HostName, node.Ip, settings.PeerPort));

        foreach (var entry in settings.Peers)
        {
            var name = entry;
            var addr = entry;
            var eq = entry.IndexOf('=');
            if (eq > 0) { name = entry[..eq]; addr = entry[(eq + 1)..]; }

            var port = settings.PeerPort;
            var colon = addr.LastIndexOf(':');
            if (colon > 0 && int.TryParse(addr[(colon + 1)..], out var p))
            {
                port = p;
                addr = addr[..colon];
            }
            candidates.Add(new PeerInfo(name, addr, port));
        }

        return candidates.DistinctBy(c => $"{c.Host}:{c.Port}").ToList();
    }

    // ---- pairing ------------------------------------------------------------

    public enum PairOutcome
    {
        Paired,
        WrongPin,    // a pairing window IS open over there, but the PIN missed
        NoPairing,   // offline, not esgee, or no pairing window open
    }

    public sealed record PairAttempt(PairOutcome Outcome, PairResult? Result, PeerInfo Peer);

    /// <summary>One POST /pair to one candidate. No token header — the PIN is
    /// the credential; the token is what comes back. Never logs either value.</summary>
    public static async Task<PairAttempt> TryPairAsync(PeerInfo peer, string pin, TimeSpan timeout)
    {
        using var http = new HttpClient { Timeout = timeout };
        try
        {
            var body = new StringContent(
                JsonSerializer.Serialize(new PairRequest(pin, Environment.MachineName), PeerProtocol.Json),
                System.Text.Encoding.UTF8, "application/json");
            using var resp = await http.PostAsync($"{peer.BaseUrl}/pair", body);

            if (resp.StatusCode == System.Net.HttpStatusCode.Unauthorized)
            {
                // Only a live pairing session says "wrong pin". A pre-pairing
                // esgee answers every tokenless request 401 with a different
                // error — that's NoPairing, not a missed PIN.
                var text = await resp.Content.ReadAsStringAsync();
                return new(text.Contains("wrong pin") ? PairOutcome.WrongPin
                    : PairOutcome.NoPairing, null, peer);
            }
            if (!resp.IsSuccessStatusCode)
                return new(PairOutcome.NoPairing, null, peer);

            var result = await resp.Content.ReadFromJsonAsync<PairResult>(PeerProtocol.Json);
            return result is { Token.Length: > 0 }
                ? new(PairOutcome.Paired, result, peer)
                : new(PairOutcome.NoPairing, null, peer);
        }
        catch
        {
            return new(PairOutcome.NoPairing, null, peer); // unreachable — fine
        }
    }

    private static string Sanitize(string name)
        => string.Concat(name.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c));

    public void Dispose() => _http.Dispose();
}
