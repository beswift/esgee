namespace Esgee.Shares;

/// <summary>
/// The wire shapes of the share routes (docs/PROTOCOL.md "Share routes").
/// A share is a different noun from a peer: per-member tokens, ids the share
/// assigns, and — deliberately — none of the fields that describe the shape
/// of anyone's personal archive. No machine names, no local row ids, no file
/// paths, no origin chains. Serialized with PeerProtocol.Json (snake_case).
/// </summary>

/// <summary>GET /share. <paramref name="Members"/> is a count — the roster
/// itself lives at /share/members. RetentionDays 0 means unlimited.</summary>
public sealed record ShareInfoDto(
    string Id, string Name, int Members, long ItemCount, int RetentionDays);

/// <summary>One row of GET /share/members. Role is "operator" or "member".</summary>
public sealed record ShareMemberDto(
    string MemberId, string DisplayName, string JoinedAt, string Role);

/// <summary>One comment. DisplayName is resolved by the share and may be null
/// if the author's member row is ever gone — clients fall back to MemberId.</summary>
public sealed record ShareCommentDto(
    long Id, string MemberId, string? DisplayName, string CreatedAt, string Body);

/// <summary>A share item. Lists omit OcrText/Comments; GET /share/items/{item}
/// carries both. POST /share/items answers the LIST shape (no OcrText, no
/// Comments — docs/PROTOCOL.md) plus Duplicate — true when the sha256 already
/// names a live item (the existing item is returned; a duplicate is a
/// success, not an error). LatestCommentAt is the newest comment's stamp
/// (null when uncommented): the per-item activity timestamp a ?since= poll
/// cursor advances over. FileExt is the extension the share stored the bytes
/// under (".jpg", never a name or path — item identity stays the share's id),
/// so clients label cached/pulled copies honestly; null from an older node
/// falls back to the kind's default.</summary>
public sealed record ShareItemDto(
    string Item,
    string Sha256,
    string SharedBy,
    string SharedAt,
    string TakenAt,
    int Width,
    int Height,
    string Kind,
    long DurationMs,
    bool HasGif,
    int CommentCount,
    bool HasAnnotations,
    string? LatestCommentAt = null,
    string? OcrText = null,
    string? OcrEngineVersion = null,
    List<ShareCommentDto>? Comments = null,
    bool? Duplicate = null,
    string? FileExt = null);

/// <summary>A deleted item's marker in GET /share/items — retention or a
/// member delete removed the content, and the id is kept so clients can prune
/// anything they pulled.</summary>
public sealed record ShareTombstoneDto(string Item, string DeletedAt);

/// <summary>GET /share/items response: live items newest first, plus the
/// tombstones (both filtered by ?since= when given). A ?since= poll pages
/// oldest activity first instead, and Truncated=true means more than n items
/// changed — advance since to the newest timestamp received and poll again;
/// nothing was skipped, only held back.</summary>
public sealed record ShareItemsPage(
    List<ShareItemDto> Items, List<ShareTombstoneDto> Deleted, bool? Truncated = null);

/// <summary>The "meta" JSON part of POST /share/items. Note what a client
/// cannot even express here: origin, machine, local ids. FileName contributes
/// only its extension to the share's own storage. OcrText null means the
/// sender hadn't OCR'd yet — a share node has no OCR engine to fill the hole,
/// so sharing clients should wait for OCR before pushing (docs/SHARES.md).</summary>
public sealed record SharePostMeta(
    string Sha256,
    string TakenAt,
    int Width,
    int Height,
    string Kind,
    long DurationMs,
    string? OcrText,
    string? OcrEngineVersion,
    string? FileName);

/// <summary>POST /share/join body: a single-use invite code plus the display
/// name this person wants to be known by. The one tokenless share route.</summary>
public sealed record ShareJoinRequest(string Invite, string DisplayName);

/// <summary>Successful join: the member's own token — minted for them alone,
/// revocable without re-keying anyone else — and their id.</summary>
public sealed record ShareJoinResult(string Token, string MemberId);

/// <summary>POST /share/items/{item}/comments body.</summary>
public sealed record ShareCommentRequest(string Body);
