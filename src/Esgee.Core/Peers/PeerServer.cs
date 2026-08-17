using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Esgee.Store;

namespace Esgee.Peers;

/// <summary>
/// The peer API: a deliberately tiny HTTP/1.1 responder on a raw TcpListener,
/// bound ONLY to this machine's Tailscale address. Why hand-rolled:
/// HttpListener on a non-localhost prefix needs a netsh URL ACL (admin —
/// unacceptable for a per-user app), and embedding Kestrel adds the ASP.NET
/// Core framework to every self-contained update (~40 MB — the same reason
/// ffmpeg isn't bundled). Eight fixed routes for one trusted client don't
/// need a framework.
///
/// Security model: reachability = tailnet membership (WireGuard-encrypted,
/// invite-only), authorization = the shared PeerToken on every request.
/// Never binds 0.0.0.0; if the Tailscale IP can't be determined the server
/// simply doesn't start.
/// </summary>
public sealed class PeerServer : IDisposable
{
    private readonly TcpListener _listener;
    private readonly ShotStore _store;
    private readonly IThumbEncoder _thumbs;
    private readonly byte[] _token;
    private readonly string _tokenString;
    private readonly CancellationTokenSource _cts = new();

    // What this implementation serves (docs/PROTOCOL.md "Capability
    // negotiation"): the peer routes, over an archive that may hold
    // recordings. No share or annotate routes here yet.
    private static readonly string[] Capabilities = ["peer", "record"];

    // Non-null only while a pairing window is open — the sole time POST /pair
    // is routed at all (closed, it falls through the token gate like any
    // unknown route). Volatile: set on the UI thread, read on connection
    // worker threads.
    private volatile PairingSession? _pairing;

    public string BoundAddress { get; }

    private PeerServer(TcpListener listener, ShotStore store, string token, string bound,
        IThumbEncoder thumbs)
    {
        _listener = listener;
        _store = store;
        _thumbs = thumbs;
        _token = Encoding.UTF8.GetBytes(token);
        _tokenString = token;
        BoundAddress = bound;
        _ = Task.Run(AcceptLoopAsync);
    }

    /// <summary>Opens the /pair route for this session's lifetime. Called by the
    /// pairing window on open; the session's own expiry/lockout/consumed state
    /// still gates every attempt, so a stale registration can't leak anything.</summary>
    public void BeginPairing(PairingSession session)
    {
        _pairing = session;
        Log.Info($"peers: pairing open — /pair answering until {session.ExpiresAt.ToLocalTime():HH:mm:ss}");
    }

    /// <summary>Closes the /pair route (window closed, expired, or locked out).</summary>
    public void EndPairing(PairingSession session)
    {
        if (ReferenceEquals(_pairing, session))
        {
            _pairing = null;
            Log.Info("peers: pairing closed — /pair disabled");
        }
    }

    /// <summary>Starts the server on the machine's Tailscale IPv4 (or, for the
    /// headless node's --bind, an explicit interface address — loopback is fine
    /// for local testing, the unspecified address never is). Returns null (and
    /// logs why) when tailscale is unavailable or the port is taken — never
    /// falls back to a wider bind.</summary>
    public static PeerServer? TryStart(ShotStore store, string token, int port,
        IThumbEncoder thumbs, string? bindIp = null)
    {
        if (string.IsNullOrWhiteSpace(token))
        {
            Log.Warn("peers: no PeerToken set; server not started");
            return null;
        }

        var ip = HttpIo.ResolveBindAddress(bindIp, "peers");
        if (ip is null) return null;

        try
        {
            var listener = new TcpListener(IPAddress.Parse(ip), port);
            listener.Start();
            var bound = $"{ip}:{port}";
            Log.Info($"peers: serving archive on http://{bound} " +
                     (bindIp is null ? "(tailscale interface only)" : "(explicit --bind)"));
            return new PeerServer(listener, store, token, bound, thumbs);
        }
        catch (Exception ex)
        {
            Log.Error($"peers: failed to bind {ip}:{port}: {ex.Message}");
            return null;
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
                Log.Warn($"peers: accept failed: {ex.Message}");
                continue;
            }

            _ = Task.Run(() => HandleConnectionAsync(client));
        }
    }

    private async Task HandleConnectionAsync(TcpClient client)
    {
        var remote = client.Client.RemoteEndPoint?.ToString() ?? "?";
        try
        {
            client.ReceiveTimeout = 30_000;
            client.SendTimeout = 60_000;
            using var stream = client.GetStream();

            var request = await HttpRequest.ReadAsync(stream, _cts.Token);
            if (request is null) return;

            // Bodies are Content-Length framed only (docs/PROTOCOL.md
            // "Transport"). A chunked body would silently parse as empty and
            // fail downstream as a baffling 400 — refuse it by name instead.
            if (request.Headers.TryGetValue("Transfer-Encoding", out var te) &&
                te.Contains("chunked", StringComparison.OrdinalIgnoreCase))
            {
                Log.Warn($"peers: 411 {request.Method} {request.RawPath} from {remote} (chunked body)");
                await WriteJsonAsync(stream, 411, new { error = "content-length required" });
                return;
            }

            // /pair is the one PIN-authenticated route — the caller doesn't
            // have the token yet; getting it is the point. Only while a window
            // is open, though: closed, the route must be indistinguishable
            // from one that doesn't exist (401 without a token, 404 with one),
            // or any host that can reach the port could fingerprint an esgee
            // server and its pairing state without holding a token.
            if (request.Method == "POST" && request.Path == "/pair" &&
                _pairing is { Active: true })
            {
                await HandlePairAsync(stream, request, remote);
                return;
            }

            if (!Authorized(request))
            {
                Log.Warn($"peers: 401 {request.Method} {request.RawPath} from {remote}");
                await WriteJsonAsync(stream, 401, new { error = "missing or wrong token" });
                return;
            }

            await RouteAsync(stream, request, remote);
        }
        catch (Exception ex)
        {
            Log.Warn($"peers: connection from {remote} failed: {ex.Message}");
        }
        finally
        {
            client.Dispose();
        }
    }

    private bool Authorized(HttpRequest req)
    {
        if (!req.Headers.TryGetValue(PeerProtocol.TokenHeader, out var supplied))
            return false;
        var bytes = Encoding.UTF8.GetBytes(supplied);
        return bytes.Length == _token.Length &&
               CryptographicOperations.FixedTimeEquals(bytes, _token);
    }

    private async Task RouteAsync(NetworkStream stream, HttpRequest req, string remote)
    {
        var path = req.Path;

        if (req.Method == "GET" && path == "/ping")
        {
            Log.Info($"peers: /ping from {remote}");
            await WriteJsonAsync(stream, 200, new PingDto(
                "esgee", AppVersion.Current, PeerProtocol.Version,
                Environment.MachineName, _store.Count(), Capabilities));
            return;
        }

        if (req.Method == "GET" && path == "/recent")
        {
            var n = Math.Clamp(req.QueryInt("n") ?? 200, 1, 1000);
            var shots = _store.Recent(n);
            Log.Info($"peers: /recent n={n} -> {shots.Count} from {remote}");
            await WriteJsonAsync(stream, 200, shots.Select(ToDto).ToList());
            return;
        }

        if (req.Method == "GET" && path == "/search")
        {
            var q = req.Query("q") ?? "";
            List<Shot> shots;
            try
            {
                shots = q.Trim().Length == 0
                    ? _store.Recent(200)
                    : _store.Search(ShotStore.FtsQuery(q), 200);
            }
            catch (Exception ex)
            {
                Log.Warn($"peers: /search \"{q}\" failed: {ex.Message}");
                shots = [];
            }
            Log.Info($"peers: /search \"{q}\" -> {shots.Count} from {remote}");
            await WriteJsonAsync(stream, 200, shots.Select(ToDto).ToList());
            return;
        }

        if (req.Method == "GET" && TryId(path, "/meta/", out var metaId))
        {
            if (_store.GetById(metaId) is not { } shot)
            {
                await WriteJsonAsync(stream, 404, new { error = "no such shot" });
                return;
            }
            var (_, text, engine) = _store.GetOcr(shot.Id);
            Log.Info($"peers: /meta/{metaId} from {remote}");
            await WriteJsonAsync(stream, 200, ToDto(shot) with
            {
                OcrText = text,
                OcrEngineVersion = engine,
            });
            return;
        }

        if (req.Method == "GET" && TryId(path, "/thumb/", out var thumbId))
        {
            if (_store.GetById(thumbId) is not { } shot || !File.Exists(shot.ThumbPath))
            {
                await WriteJsonAsync(stream, 404, new { error = "no thumbnail" });
                return;
            }
            byte[] jpeg;
            try
            {
                jpeg = _thumbs.EncodeThumb(shot.ThumbPath);
            }
            catch (Exception ex)
            {
                Log.Warn($"peers: thumb {thumbId} failed: {ex.Message}");
                await WriteJsonAsync(stream, 500, new { error = "thumbnail failed" });
                return;
            }
            await WriteBytesAsync(stream, 200, "image/jpeg", jpeg);
            return;
        }

        if (req.Method == "GET" && TryId(path, "/file/", out var fileId))
        {
            if (_store.GetById(fileId) is not { } shot)
            {
                await WriteJsonAsync(stream, 404, new { error = "no such shot" });
                return;
            }

            // ?alt=gif → the sibling GIF of a recording; ?alt=thumb → the
            // extracted preview frame beside an MP4. Both exist so a pulling
            // peer can reconstruct the full on-disk shape of a recording.
            var alt = req.Query("alt");
            var filePath = alt switch
            {
                "gif" => shot.GifPath,
                "thumb" => shot.IsVideo && File.Exists(shot.ThumbPath) ? shot.ThumbPath : null,
                _ => shot.Path,
            };
            if (filePath is null || !File.Exists(filePath))
            {
                await WriteJsonAsync(stream, 404, new { error = "file missing" });
                return;
            }

            Log.Info($"peers: /file/{fileId}{(alt is null ? "" : $"?alt={alt}")} " +
                     $"({new FileInfo(filePath).Length / 1024} KB) from {remote}");
            await WriteFileAsync(stream, filePath);
            return;
        }

        if (req.Method == "POST" && path == "/ingest")
        {
            await HandleIngestAsync(stream, req, remote);
            return;
        }

        await WriteJsonAsync(stream, 404, new { error = "no such endpoint" });
    }

    /// <summary>POST /pair: redeem the on-screen PIN for the PeerToken. Only
    /// reached while a pairing window is open — closed, the connection handler
    /// lets the route fall through the ordinary token gate so it looks exactly
    /// like a route that doesn't exist. 401 "wrong pin" on a miss, 200 with the
    /// token exactly once. PIN and token values never reach the log — only
    /// outcomes do.</summary>
    private async Task HandlePairAsync(NetworkStream stream, HttpRequest req, string remote)
    {
        var session = _pairing;
        if (session is null || !session.Active)
        {
            // The window closed between the handler's gate and here: answer
            // exactly like any other tokenless request so the race can't leak
            // the route's existence.
            Log.Info($"peers: /pair from {remote} rejected — no pairing in progress");
            await WriteJsonAsync(stream, 401, new { error = "missing or wrong token" });
            return;
        }

        PairRequest? pair = null;
        try { pair = JsonSerializer.Deserialize<PairRequest>(req.Body, PeerProtocol.Json); }
        catch { /* falls through to 400 */ }
        if (pair is null || string.IsNullOrEmpty(pair.Pin))
        {
            await WriteJsonAsync(stream, 400, new { error = "bad pair request" });
            return;
        }

        var machine = string.IsNullOrWhiteSpace(pair.Machine) ? remote : pair.Machine.Trim();
        switch (session.TryRedeem(pair.Pin, machine))
        {
            case PairAttemptResult.Accepted:
                Log.Info($"peers: /pair from {remote} ('{machine}') accepted — PIN consumed, token issued");
                await WriteJsonAsync(stream, 200, new PairResult(_tokenString, Environment.MachineName));
                return;
            case PairAttemptResult.WrongPin:
                Log.Warn($"peers: /pair from {remote} wrong PIN " +
                         $"({session.FailuresSoFar}/{PairingSession.MaxAttempts})");
                await WriteJsonAsync(stream, 401, new { error = "wrong pin" });
                return;
            default:
                // Spent mid-request (consumed, expired, or locked out) — same
                // shape as the closed-window answer, for the same reason.
                Log.Info($"peers: /pair from {remote} rejected — no pairing in progress");
                await WriteJsonAsync(stream, 401, new { error = "missing or wrong token" });
                return;
        }
    }

    private async Task HandleIngestAsync(NetworkStream stream, HttpRequest req, string remote)
    {
        var parts = Multipart.Parse(req);
        if (parts is null)
        {
            await WriteJsonAsync(stream, 400, new { error = "expected multipart/form-data" });
            return;
        }

        var metaPart = parts.FirstOrDefault(p => p.Name == "meta");
        var filePart = parts.FirstOrDefault(p => p.Name == "file");
        if (metaPart is null || filePart is null)
        {
            Log.Warn($"peers: ingest from {remote} missing parts " +
                     $"(got: {string.Join(", ", parts.Select(p => p.Name))})");
            await WriteJsonAsync(stream, 400, new { error = "need 'meta' and 'file' parts" });
            return;
        }

        IngestMeta? meta;
        try
        {
            meta = JsonSerializer.Deserialize<IngestMeta>(metaPart.Body, PeerProtocol.Json);
        }
        catch (Exception ex)
        {
            await WriteJsonAsync(stream, 400, new { error = $"bad meta json: {ex.Message}" });
            return;
        }
        if (meta is null || !DateTimeOffset.TryParse(meta.TakenAt, out var takenAt))
        {
            await WriteJsonAsync(stream, 400, new { error = "bad meta" });
            return;
        }

        var sha = Convert.ToHexString(SHA256.HashData(filePart.Body));
        if (!sha.Equals(meta.Sha256, StringComparison.OrdinalIgnoreCase))
        {
            await WriteJsonAsync(stream, 400, new { error = "sha256 mismatch" });
            return;
        }

        var ext = ShotStore.SafeExtension(Path.GetExtension(meta.FileName ?? ""), meta.Kind);

        var dest = _store.PlanIngestPath(takenAt, ext);
        await File.WriteAllBytesAsync(dest, filePart.Body, _cts.Token);

        // Recordings arrive with their sidecar files so the local archive gets
        // the same on-disk shape a native recording has (thumb for the grid,
        // GIF as the paste artifact).
        if (parts.FirstOrDefault(p => p.Name == "gif") is { } gif)
            await File.WriteAllBytesAsync(Path.ChangeExtension(dest, ".gif"), gif.Body, _cts.Token);
        if (parts.FirstOrDefault(p => p.Name == "thumb") is { } thumb)
            await File.WriteAllBytesAsync(dest + ".png", thumb.Body, _cts.Token);

        var (shot, duplicate) = _store.Ingest(dest, sha, takenAt, meta.Width, meta.Height,
            meta.Kind, meta.DurationMs, meta.OcrText, meta.OcrEngineVersion ?? "",
            meta.Origin ?? "");

        if (duplicate)
        {
            // Lost the race (or a retry of an already-landed push): keep the
            // original row, discard the fresh copy.
            try { File.Delete(dest); } catch { }
            try { File.Delete(Path.ChangeExtension(dest, ".gif")); } catch { }
            try { File.Delete(dest + ".png"); } catch { }
            Log.Info($"peers: ingest from {remote} deduplicated (sha match, shot {shot.Id})");
        }
        else
        {
            Log.Info($"peers: ingested {meta.Kind} {shot.Width}x{shot.Height} from " +
                     $"{meta.Origin ?? "?"} -> {shot.Path} (id {shot.Id}, " +
                     $"ocr {(meta.OcrText is null ? "pending" : $"imported from sidecar [{meta.OcrEngineVersion}]")})");
        }

        await WriteJsonAsync(stream, 200, new IngestResult(shot.Id, duplicate));
    }

    private static ShotDto ToDto(Shot s) => new(
        s.Id, s.FileName, s.TakenAt.ToString("o"), s.Width, s.Height,
        s.Sha256, s.Kind, s.DurationMs, s.Origin, s.GifPath is not null);

    private static bool TryId(string path, string prefix, out long id)
    {
        id = 0;
        return path.StartsWith(prefix, StringComparison.Ordinal) &&
               long.TryParse(path.AsSpan(prefix.Length), out id);
    }

    // ---- HTTP plumbing (shared with the node's share server — Http.cs) ------

    private static Task WriteJsonAsync(NetworkStream stream, int status, object body)
        => HttpIo.WriteJsonAsync(stream, status, body);

    private static Task WriteBytesAsync(
        NetworkStream stream, int status, string contentType, byte[] body)
        => HttpIo.WriteBytesAsync(stream, status, contentType, body);

    private static Task WriteFileAsync(NetworkStream stream, string path)
        => HttpIo.WriteFileAsync(stream, path);

    public void Dispose()
    {
        _cts.Cancel();
        try { _listener.Stop(); } catch { }
        _cts.Dispose();
        Log.Info("peers: server stopped");
    }
}
