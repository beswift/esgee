import Foundation

/// The wire shapes every esgee implementation shares. docs/PROTOCOL.md is
/// normative; where a comment here disagrees with it, the document wins.
/// Plain HTTP/1.1 + JSON inside the tailnet — WireGuard already encrypts the
/// link, so TLS would add certificate management for zero additional
/// confidentiality. Field names are snake_case on the wire, spelled out in
/// CodingKeys so the Swift names can stay Swift.
enum PeerProtocol {
    /// Proto 2 = proto 1 plus base-URL addressing and the capabilities array.
    /// `proto` bumps only for an incompatible change to an existing route's
    /// shape; new routes ride on capability strings instead.
    static let proto = 2

    /// Every request authenticates with this header carrying the peer token,
    /// compared in constant time. POST /pair is the sole exception — the
    /// caller has no token yet, and obtaining one is the point.
    static let tokenHeader = "X-Esgee-Token"

    /// Coders are cheap and not Sendable: make one per use site rather than
    /// sharing. Optional fields encode as *absent*, never null — a missing
    /// ocr_text means "sender hadn't OCR'd it yet", and receivers must be able
    /// to rely on absence carrying that meaning.
    static func makeEncoder() -> JSONEncoder { JSONEncoder() }
    static func makeDecoder() -> JSONDecoder { JSONDecoder() }
}

/// Capabilities, not the proto integer, gate features — so a Windows box, a
/// Mac, and a headless share node can each implement a different subset
/// without anyone version-sniffing (docs/PROTOCOL.md "Capability negotiation").
enum PeerCapability {
    /// Serves the peer routes over its own archive.
    static let peer = "peer"
    /// Serves the share routes; enforces per-member identity.
    static let share = "share"
    /// Accepts annotation and comment writes.
    static let annotate = "annotate"
    /// Archive may contain kind "video" items with GIF siblings.
    static let record = "record"

    /// What this build advertises on /ping. `record` is defined by ARCHIVE
    /// CONTENTS, not by what this endpoint produces (docs/PROTOCOL.md:
    /// "Archive may contain kind 'video' items with GIF siblings") — no
    /// recorder ships in Mac v1, but pushed/pulled recordings land here as
    /// first-class video items served with their GIF/thumb siblings, so a
    /// client written to the doc's table must wire its video affordances.
    /// Same static answer as the Windows build, which advertises
    /// ["peer", "record"] before any recording exists.
    static let advertised: [String] = [peer, record]
}

/// GET /ping — the handshake and health number.
struct PingDto: Codable, Sendable {
    let app: String
    let version: String
    let proto: Int
    let machine: String
    let captures: Int64
    /// Absent from proto-1 peers. Never read this directly for gating —
    /// use `effectiveCapabilities`, which encodes the compatibility rule.
    let capabilities: [String]?

    /// A peer that predates capabilities is, by definition, a peer.
    var effectiveCapabilities: [String] { capabilities ?? [PeerCapability.peer] }

    enum CodingKeys: String, CodingKey {
        case app, version, proto, machine, captures, capabilities
    }
}

/// One capture, as listed by /recent, /search, and /meta. Lists omit
/// `ocrText`; /meta/{id} includes it — that plus the engine version IS the
/// sync sidecar for a pull.
struct ShotDto: Codable, Sendable, Identifiable {
    let id: Int64
    let fileName: String
    /// ISO 8601 with offset, passed through verbatim — never parsed and
    /// re-formatted for the wire (see IsoStamp for the round-trip rule).
    let takenAt: String
    let width: Int
    let height: Int
    let sha256: String
    /// "image" or "video". A String, not an enum: unknown kinds from newer
    /// peers must survive decode (additive fields and values are always
    /// allowed, docs/PROTOCOL.md "Versioning rules").
    let kind: String
    let durationMs: Int64
    /// "" for captures taken on the serving machine, else the machine name
    /// the capture originally came from.
    let origin: String
    let hasGif: Bool
    var ocrText: String? = nil
    var ocrEngineVersion: String? = nil

    enum CodingKeys: String, CodingKey {
        case id
        case fileName = "file_name"
        case takenAt = "taken_at"
        case width, height, sha256, kind
        case durationMs = "duration_ms"
        case origin
        case hasGif = "has_gif"
        case ocrText = "ocr_text"
        case ocrEngineVersion = "ocr_engine_version"
    }
}

/// The JSON sidecar in a POST /ingest multipart body. `ocrText` nil on an
/// image means "sender hadn't OCR'd it yet" — the receiver leaves the row
/// pending so its own backlog sweep fills the hole. Senders must not
/// fabricate empty strings.
struct IngestMeta: Codable, Sendable {
    let sha256: String
    let takenAt: String
    let width: Int
    let height: Int
    let kind: String
    let durationMs: Int64
    var ocrText: String? = nil
    var ocrEngineVersion: String? = nil
    var origin: String? = nil
    var fileName: String? = nil

    enum CodingKeys: String, CodingKey {
        case sha256
        case takenAt = "taken_at"
        case width, height, kind
        case durationMs = "duration_ms"
        case ocrText = "ocr_text"
        case ocrEngineVersion = "ocr_engine_version"
        case origin
        case fileName = "file_name"
    }
}

/// POST /ingest response. A duplicate is a success, not an error — dedupe is
/// global by sha256, so a retried push lands exactly once and the caller may
/// mark the item pushed and move on.
struct IngestResult: Codable, Sendable {
    let id: Int64
    let duplicate: Bool

    enum CodingKeys: String, CodingKey { case id, duplicate }
}

/// POST /pair body: the PIN currently on the target machine's screen plus the
/// requesting machine's name (so the pairing window can say who joined).
/// This is the one route that authenticates by PIN, not token.
struct PairRequest: Codable, Sendable {
    let pin: String
    let machine: String

    enum CodingKeys: String, CodingKey { case pin, machine }
}

/// Successful /pair response: the real peer token plus the issuing machine's
/// name. The only route that ever transmits the token.
struct PairResult: Codable, Sendable {
    let token: String
    let machine: String

    enum CodingKeys: String, CodingKey { case token, machine }
}

/// Every non-2xx body on the wire: {"error": "..."}. The client side reads it
/// to tell "wrong pin" from "no pairing in progress"; the server side must
/// never answer an unknown route with anything but 404 + this shape.
struct ErrorDto: Codable, Sendable {
    let error: String

    enum CodingKeys: String, CodingKey { case error }
}
