using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;

namespace Esgee.Peers;

/// <summary>The HTTP plumbing every esgee server shares — the peer server here
/// in Core and the node's share server. One request parser, one response
/// writer, one bind policy, so the two servers can't drift apart on transport
/// behavior (docs/PROTOCOL.md "Transport and security model" is normative for
/// both). Internal on purpose: the wire contract is the protocol document,
/// not these types.</summary>
internal static class HttpIo
{
    public static Task WriteJsonAsync(NetworkStream stream, int status, object body)
        => WriteBytesAsync(stream, status,
            "application/json; charset=utf-8",
            JsonSerializer.SerializeToUtf8Bytes(body, body.GetType(), PeerProtocol.Json));

    public static async Task WriteBytesAsync(
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

    public static async Task WriteFileAsync(NetworkStream stream, string path)
    {
        // FileShare.Delete matters on Windows: a delete issued while this
        // download streams must succeed (delete-pending until the handle
        // closes), or the tombstoned item's pixels outlive their row.
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read,
            FileShare.Read | FileShare.Delete);
        var head = Encoding.ASCII.GetBytes(
            $"HTTP/1.1 200 OK\r\n" +
            $"Content-Type: {ContentType(path)}\r\n" +
            $"Content-Length: {fs.Length}\r\n" +
            "Connection: close\r\n\r\n");
        await stream.WriteAsync(head);
        await fs.CopyToAsync(stream);
    }

    public static string ContentType(string path) =>
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
        403 => "Forbidden", 404 => "Not Found", 411 => "Length Required",
        500 => "Internal Server Error", _ => "Error",
    };

    /// <summary>Resolves the address a server may bind: an explicit --bind that
    /// names a specific interface (loopback for local testing, a Tailscale
    /// address for real use — 0.0.0.0 is refused, never narrowed), or the
    /// machine's own Tailscale IPv4 when no bind is given. Null means "do not
    /// start"; the reason is already logged under <paramref name="logPrefix"/>.</summary>
    public static string? ResolveBindAddress(string? bindIp, string logPrefix)
    {
        if (bindIp is null)
        {
            var self = Tailscale.SelfIPv4();
            if (self is null)
                Log.Warn($"{logPrefix}: no Tailscale IPv4 found (is tailscale running?); server not started");
            return self;
        }

        // "Reachability = tailnet membership" only holds on a specific
        // interface — a wildcard bind would open the API to every network
        // the machine touches, so it is refused rather than narrowed.
        if (!IPAddress.TryParse(bindIp, out var parsed) ||
            parsed.Equals(IPAddress.Any) || parsed.Equals(IPAddress.IPv6Any))
        {
            Log.Error($"{logPrefix}: refusing to bind '{bindIp}' — a specific interface address is required, never 0.0.0.0");
            return null;
        }

        // Loopback (local testing) and CGNAT 100.64/10 (a Tailscale
        // address) are the sanctioned binds. Anything else serves the
        // archive as plaintext HTTP off-tailnet — WireGuard is no longer
        // doing the encryption the security model assumes, so say so
        // loudly instead of starting in silence.
        if (!IPAddress.IsLoopback(parsed) && !IsTailnetAddress(parsed))
        {
            var warning =
                $"{logPrefix}: --bind {parsed} is neither loopback nor a Tailscale (100.64/10) address — " +
                "the archive will be served as PLAINTEXT HTTP outside the tailnet, " +
                "guarded by the token alone (docs/PROTOCOL.md forbids this endpoint class)";
            Log.Warn(warning);
            // "Loudly" must mean where the operator is looking: an interactive
            // node start prints its success line to stdout, and a warning that
            // lands only in esgee.log is silence on that path.
            Console.Error.WriteLine(warning);
        }
        return parsed.ToString();
    }

    /// <summary>Tailscale hands out IPv4 from the CGNAT block 100.64.0.0/10 —
    /// the only non-loopback range an explicit --bind can name without leaving
    /// the tailnet's encryption.</summary>
    private static bool IsTailnetAddress(IPAddress ip)
    {
        if (ip.AddressFamily != AddressFamily.InterNetwork) return false;
        var b = ip.GetAddressBytes();
        return b[0] == 100 && b[1] >= 64 && b[1] <= 127;
    }
}

/// <summary>A parsed HTTP request: line, headers, and (for POST) the body.</summary>
internal sealed class HttpRequest
{
    // A recording plus its GIF can be large, but this is a private API on a
    // private network — cap the body so a bug can't balloon memory forever.
    internal const long MaxBody = 1L << 30; // 1 GB

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

    /// <summary>Reads one request. <paramref name="bodyLimit"/>, when given, is
    /// consulted after the request line and headers are parsed but BEFORE any
    /// body buffer is allocated — the body cap can therefore depend on who is
    /// asking (a share node keeps unauthenticated bodies tiny while an
    /// authenticated capture upload gets the full ceiling). Content-Length
    /// alone must never be allowed to size an allocation for a stranger.</summary>
    public static async Task<HttpRequest?> ReadAsync(NetworkStream stream, CancellationToken ct,
        Func<HttpRequest, long>? bodyLimit = null)
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
            if (len > Math.Min(MaxBody, bodyLimit?.Invoke(req) ?? MaxBody)) return null;

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

/// <summary>Just enough multipart/form-data parsing for the upload routes — a
/// handful of named parts, binary-safe, no nesting.</summary>
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
