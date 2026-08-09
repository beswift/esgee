using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Windows.Media.Imaging;
using Esgee.Store;

namespace Esgee.Peers;

/// <summary>
/// The peer API: a deliberately tiny HTTP/1.1 responder on a raw TcpListener,
/// bound ONLY to this machine's Tailscale address. Why hand-rolled:
/// HttpListener on a non-localhost prefix needs a netsh URL ACL (admin —
/// unacceptable for a per-user app), and embedding Kestrel adds the ASP.NET
/// Core framework to every self-contained update (~40 MB — the same reason
/// ffmpeg isn't bundled). Seven fixed routes for one trusted client don't
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
    private readonly byte[] _token;
    private readonly CancellationTokenSource _cts = new();

    public string BoundAddress { get; }

    private PeerServer(TcpListener listener, ShotStore store, string token, string bound)
    {
        _listener = listener;
        _store = store;
        _token = Encoding.UTF8.GetBytes(token);
        BoundAddress = bound;
        _ = Task.Run(AcceptLoopAsync);
    }

    /// <summary>Starts the server on the machine's Tailscale IPv4. Returns null
    /// (and logs why) when tailscale is unavailable or the port is taken —
    /// never falls back to a wider bind.</summary>
    public static PeerServer? TryStart(ShotStore store, string token, int port)
    {
        if (string.IsNullOrWhiteSpace(token))
        {
            Log.Warn("peers: no PeerToken set; server not started");
            return null;
        }

        var ip = Tailscale.SelfIPv4();
        if (ip is null)
        {
            Log.Warn("peers: no Tailscale IPv4 found (is tailscale running?); server not started");
            return null;
        }

        try
        {
            var listener = new TcpListener(IPAddress.Parse(ip), port);
            listener.Start();
            var bound = $"{ip}:{port}";
            Log.Info($"peers: serving archive on http://{bound} (tailscale interface only)");
            return new PeerServer(listener, store, token, bound);
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
                "esgee", UpdateService.CurrentVersion, PeerProtocol.Version,
                Environment.MachineName, _store.Count()));
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
                jpeg = EncodeThumb(shot.ThumbPath);
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

        var ext = Path.GetExtension(meta.FileName ?? "");
        if (ext.Length == 0) ext = meta.Kind == "video" ? ".mp4" : ".png";

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

    /// <summary>Small JPEG for grid tiles — decoded scaled-down (never the full
    /// bitmap) on this worker thread, far from the UI dispatcher.</summary>
    private static byte[] EncodeThumb(string sourcePath)
    {
        using var fs = File.OpenRead(sourcePath);
        var bmp = new BitmapImage();
        bmp.BeginInit();
        bmp.StreamSource = fs;
        bmp.DecodePixelWidth = 448;
        bmp.CacheOption = BitmapCacheOption.OnLoad;
        bmp.EndInit();
        bmp.Freeze();

        var encoder = new JpegBitmapEncoder { QualityLevel = 80 };
        encoder.Frames.Add(BitmapFrame.Create(bmp));
        using var ms = new MemoryStream();
        encoder.Save(ms);
        return ms.ToArray();
    }

    // ---- HTTP plumbing ------------------------------------------------------

    private static Task WriteJsonAsync(NetworkStream stream, int status, object body)
        => WriteBytesAsync(stream, status,
            "application/json; charset=utf-8",
            JsonSerializer.SerializeToUtf8Bytes(body, body.GetType(), PeerProtocol.Json));

    private static async Task WriteBytesAsync(
        NetworkStream stream, int status, string contentType, byte[] body)
    {
        var head = Encoding.ASCII.GetBytes(
            $"HTTP/1.1 {status} {Reason(status)}\r\n" +
            $"Content-Type: {contentType}\r\n" +
            $"Content-Length: {body.Length}\r\n" +
            "Connection: close\r\n\r\n");
        await stream.WriteAsync(head);
        await stream.WriteAsync(body);
    }

    private static async Task WriteFileAsync(NetworkStream stream, string path)
    {
        using var fs = File.OpenRead(path);
        var head = Encoding.ASCII.GetBytes(
            $"HTTP/1.1 200 OK\r\n" +
            $"Content-Type: {ContentType(path)}\r\n" +
            $"Content-Length: {fs.Length}\r\n" +
            "Connection: close\r\n\r\n");
        await stream.WriteAsync(head);
        await fs.CopyToAsync(stream);
    }

    private static string ContentType(string path) =>
        Path.GetExtension(path).ToLowerInvariant() switch
        {
            ".png" => "image/png",
            ".gif" => "image/gif",
            ".mp4" => "video/mp4",
            ".jpg" or ".jpeg" => "image/jpeg",
            _ => "application/octet-stream",
        };

    private static string Reason(int status) => status switch
    {
        200 => "OK", 400 => "Bad Request", 401 => "Unauthorized",
        404 => "Not Found", _ => "Error",
    };

    public void Dispose()
    {
        _cts.Cancel();
        try { _listener.Stop(); } catch { }
        _cts.Dispose();
        Log.Info("peers: server stopped");
    }
}

/// <summary>A parsed HTTP request: line, headers, and (for POST) the body.</summary>
internal sealed class HttpRequest
{
    // A recording plus its GIF can be large, but this is a private API on a
    // private network — cap the body so a bug can't balloon memory forever.
    private const long MaxBody = 1L << 30; // 1 GB

    public string Method = "";
    public string RawPath = "";
    public string Path = "";
    public Dictionary<string, string> Headers = new(StringComparer.OrdinalIgnoreCase);
    public byte[] Body = [];

    private Dictionary<string, string>? _query;

    public string? Query(string key)
    {
        if (_query is null)
        {
            _query = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var qIdx = RawPath.IndexOf('?');
            if (qIdx >= 0)
            {
                foreach (var pair in RawPath[(qIdx + 1)..].Split('&', StringSplitOptions.RemoveEmptyEntries))
                {
                    var eq = pair.IndexOf('=');
                    if (eq < 0) _query[Uri.UnescapeDataString(pair)] = "";
                    else _query[Uri.UnescapeDataString(pair[..eq])] =
                        Uri.UnescapeDataString(pair[(eq + 1)..].Replace('+', ' '));
                }
            }
        }
        return _query.TryGetValue(key, out var v) ? v : null;
    }

    public int? QueryInt(string key)
        => int.TryParse(Query(key), out var n) ? n : null;

    public static async Task<HttpRequest?> ReadAsync(NetworkStream stream, CancellationToken ct)
    {
        // Read until the blank line ending the headers; anything after it in the
        // same reads is the start of the body.
        var buffer = new MemoryStream();
        var chunk = new byte[8192];
        int headerEnd;
        while (true)
        {
            headerEnd = FindDoubleCrlf(buffer.GetBuffer(), (int)buffer.Length);
            if (headerEnd >= 0) break;
            if (buffer.Length > 64 * 1024) return null; // header flood

            var n = await stream.ReadAsync(chunk, ct);
            if (n == 0) return null;
            buffer.Write(chunk, 0, n);
        }

        var headerText = Encoding.ASCII.GetString(buffer.GetBuffer(), 0, headerEnd);
        var lines = headerText.Split("\r\n");
        var requestLine = lines[0].Split(' ');
        if (requestLine.Length < 2) return null;

        var req = new HttpRequest { Method = requestLine[0], RawPath = requestLine[1] };
        var q = req.RawPath.IndexOf('?');
        req.Path = q < 0 ? req.RawPath : req.RawPath[..q];

        foreach (var line in lines.Skip(1))
        {
            var colon = line.IndexOf(':');
            if (colon > 0) req.Headers[line[..colon].Trim()] = line[(colon + 1)..].Trim();
        }

        if (req.Headers.TryGetValue("Content-Length", out var lenText) &&
            long.TryParse(lenText, out var len) && len > 0)
        {
            if (len > MaxBody) return null;

            var body = new byte[len];
            var already = Math.Min((int)buffer.Length - (headerEnd + 4), (int)len);
            Array.Copy(buffer.GetBuffer(), headerEnd + 4, body, 0, already);

            var offset = already;
            while (offset < len)
            {
                var n = await stream.ReadAsync(body.AsMemory(offset), ct);
                if (n == 0) return null; // truncated
                offset += n;
            }
            req.Body = body;
        }

        return req;
    }

    private static int FindDoubleCrlf(byte[] data, int length)
    {
        for (var i = 0; i + 3 < length; i++)
            if (data[i] == '\r' && data[i + 1] == '\n' && data[i + 2] == '\r' && data[i + 3] == '\n')
                return i;
        return -1;
    }
}

/// <summary>One part of a multipart/form-data body.</summary>
internal sealed record MultipartPart(string Name, string? FileName, byte[] Body);

/// <summary>Just enough multipart/form-data parsing for /ingest — a handful of
/// named parts, binary-safe, no nesting.</summary>
internal static class Multipart
{
    public static List<MultipartPart>? Parse(HttpRequest req)
    {
        if (!req.Headers.TryGetValue("Content-Type", out var contentType))
            return null;
        var marker = "boundary=";
        var idx = contentType.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
        if (!contentType.Contains("multipart/", StringComparison.OrdinalIgnoreCase) || idx < 0)
            return null;

        var boundary = contentType[(idx + marker.Length)..].Trim().Trim('"');
        var delimiter = Encoding.ASCII.GetBytes("--" + boundary);
        var body = req.Body;

        var parts = new List<MultipartPart>();
        var pos = IndexOf(body, delimiter, 0);
        while (pos >= 0)
        {
            pos += delimiter.Length;
            // "--" right after the delimiter = closing boundary.
            if (pos + 1 < body.Length && body[pos] == '-' && body[pos + 1] == '-') break;
            pos += 2; // CRLF after the delimiter

            var headerEnd = IndexOf(body, "\r\n\r\n"u8.ToArray(), pos);
            if (headerEnd < 0) break;

            var headers = Encoding.UTF8.GetString(body, pos, headerEnd - pos);
            var contentStart = headerEnd + 4;

            var next = IndexOf(body, delimiter, contentStart);
            if (next < 0) break;
            var contentEnd = next - 2; // strip the CRLF before the boundary

            var name = HeaderValue(headers, "name");
            if (name is not null)
            {
                var content = new byte[contentEnd - contentStart];
                Array.Copy(body, contentStart, content, 0, content.Length);
                parts.Add(new MultipartPart(name, HeaderValue(headers, "filename"), content));
            }

            pos = next;
        }

        return parts;
    }

    /// <summary>Content-Disposition attribute value, quoted or bare — curl
    /// sends name="meta", .NET's MultipartFormDataContent sends name=meta.</summary>
    private static string? HeaderValue(string headers, string attr)
    {
        // "; name=" (or the line start) so `name=` can't match inside `filename=`.
        foreach (var marker in new[] { "; " + attr + "=", ";" + attr + "=" })
        {
            var idx = headers.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
            if (idx < 0) continue;
            var start = idx + marker.Length;
            if (start < headers.Length && headers[start] == '"')
            {
                var end = headers.IndexOf('"', start + 1);
                return end < 0 ? null : headers[(start + 1)..end];
            }
            var stop = headers.IndexOfAny([';', '\r', '\n'], start);
            return (stop < 0 ? headers[start..] : headers[start..stop]).Trim();
        }
        return null;
    }

    private static int IndexOf(byte[] haystack, byte[] needle, int start)
    {
        for (var i = start; i + needle.Length <= haystack.Length; i++)
        {
            var match = true;
            for (var j = 0; j < needle.Length; j++)
                if (haystack[i + j] != needle[j]) { match = false; break; }
            if (match) return i;
        }
        return -1;
    }
}
