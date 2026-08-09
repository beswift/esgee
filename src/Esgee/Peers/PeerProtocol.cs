using System.Text.Json;
using System.Text.Json.Serialization;

namespace Esgee.Peers;

/// <summary>
/// The wire shapes both ends of the peer API share. Plain HTTP/1.1 + JSON
/// inside the tailnet (WireGuard already encrypts the link; see docs/NOTES.md
/// for the security model). Protocol version bumps go in PingDto.Proto.
/// </summary>
public static class PeerProtocol
{
    public const int Version = 1;

    /// <summary>Requests authenticate with this header carrying PeerToken.</summary>
    public const string TokenHeader = "X-Esgee-Token";

    public static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };
}

public sealed record PingDto(
    string App, string Version, int Proto, string Machine, long Captures);

/// <summary>One capture, as listed by /recent, /search, and /meta. Lists omit
/// OcrText; /meta/{id} includes it (that plus the engine version IS the sync
/// sidecar for a pull).</summary>
public sealed record ShotDto(
    long Id,
    string FileName,
    string TakenAt,
    int Width,
    int Height,
    string Sha256,
    string Kind,
    long DurationMs,
    string Origin,
    bool HasGif,
    string? OcrText = null,
    string? OcrEngineVersion = null);

/// <summary>The JSON sidecar in a POST /ingest multipart body. OcrText null on
/// an image means "sender hadn't OCR'd it yet" — the receiver's own backlog
/// sweep will fill it in rather than the text being lost forever.</summary>
public sealed record IngestMeta(
    string Sha256,
    string TakenAt,
    int Width,
    int Height,
    string Kind,
    long DurationMs,
    string? OcrText,
    string? OcrEngineVersion,
    string? Origin,
    string? FileName);

public sealed record IngestResult(long Id, bool Duplicate);

/// <summary>POST /pair body: the PIN currently on the target machine's screen
/// plus the requesting machine's name (so the pairing window can say who
/// joined). This is the one route that authenticates by PIN, not token.</summary>
public sealed record PairRequest(string Pin, string Machine);

/// <summary>Successful /pair response: the real PeerToken plus the issuing
/// machine's name. The only route that ever transmits the token.</summary>
public sealed record PairResult(string Token, string Machine);
