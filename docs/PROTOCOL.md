# esgee wire protocol

The contract every esgee implementation shares. Windows (`src/Esgee/`) is the
reference implementation of **proto 2**. The Mac app
([docs/MAC.md](MAC.md)) and the share node
([docs/SHARES.md](SHARES.md)) implement this document, not each other.

This file is normative. Where it disagrees with a comment in the code, this
file is what a second implementation is entitled to rely on.

## Design rule

**The protocol is the abstraction.** There is no backend interface in the
client, no plugin layer, no strategy pattern. Anything that speaks these
routes is a valid endpoint — a peer on your tailnet, a headless team share
node, or (later, if it ever earns its place) a hosted relay. Portability is
bought by keeping three things out of the wire format:

1. **No host:port assumptions.** Endpoints are URLs. `http://100.x.y.z:43117`
   and `https://relay.example/s/abc` differ only in a string.
2. **No global shared secret assumptions.** The token header is per-endpoint
   and, for shares, per-member. A server is free to treat one token as one
   identity.
3. **No local-rowid assumptions in shared content.** Peer routes key on the
   serving machine's own row id (fine — you always ask the machine you're
   looking at). Share routes key on an id the share assigns, with sha256 as
   the dedupe key.

Anything else — storage engine, OCR engine, language, hosting — is an
implementation detail by construction.

## Transport and security model

HTTP/1.1 with JSON bodies, `snake_case` field names. No TLS on tailnet
endpoints: the WireGuard link is already encrypted end to end, so TLS would
add certificate management for zero additional confidentiality. Endpoints
reachable outside a tailnet **must** be `https://`.

Request bodies are framed by `Content-Length` only. Servers do not decode
`Transfer-Encoding: chunked` and answer such requests
`411 {"error": "content-length required"}` — clients must buffer uploads and
send an exact length (an HTTP stack given an unknown-length stream, e.g. a Go
`http.Client` with a `Reader` body, will chunk by default and must be told
not to).

A `Content-Length` is a claim, not a purchase order. Servers must not let an
unauthenticated request's declared length size an allocation: the share node
caps any body that does not arrive with a valid member token on an
authenticated capture post at 256 KB (every pre-auth body in this protocol is
small JSON), enforces a hard deadline on reading each request, and bounds
concurrent connections. Clients see nothing of this unless they misbehave —
an over-cap or stalled request is dropped without a response.

Reachability is the first gate: a tailnet server binds exclusively to its own
Tailscale address (never `0.0.0.0`), so only devices admitted to the tailnet
can open a socket at all. Authorization is the second: every request carries

```
X-Esgee-Token: <token>
```

compared in constant time. `POST /pair` is the sole exception — the caller
does not have a token yet, and obtaining one is the point.

Servers must not log token or PIN values. Outcomes only.

## Addressing

An endpoint is a **name plus a base URL**. Everything else is appended:

```
peer       http://100.101.102.103:43117
share      http://100.64.0.9:43118/s/design-team
hosted     https://relay.example/s/design-team     (hypothetical)
```

Clients treat the base URL as opaque and concatenate route paths onto it.
Implementations must not reconstruct addresses from a host and port field.

> Proto 1 modeled peers as `(host, port)` and built `http://{host}:{port}`.
> Proto 2 (shipped on Windows) stores the base URL itself: existing `Peers`
> settings entries (`name=host:port` / `host:port`) expand to the same URL
> they always did, and full-URL entries (`name=http://…` / `https://…`) are
> accepted as-is.

## Capability negotiation

`GET /ping` is the handshake. Proto 2 adds a `capabilities` array; a client
that sees no such field is talking to a proto-1 peer and must assume
`["peer"]`.

```json
{
  "app": "esgee",
  "version": "1.4.2",
  "proto": 2,
  "machine": "alphalfa",
  "captures": 48213,
  "capabilities": ["peer", "share", "annotate"]
}
```

Capabilities, not the version integer, gate features. `proto` exists to
detect an outright incompatible wire format; `capabilities` exists so a
Windows box, a Mac, and a headless share node can each implement a different
subset without anyone version-sniffing.

Defined capabilities:

| Capability | Meaning |
|---|---|
| `peer` | Serves the peer routes below over its own archive |
| `share` | Serves the share routes; enforces per-member identity |
| `annotate` | Accepts annotation and comment writes |
| `record` | Archive may contain `kind: "video"` items with GIF siblings |

The Windows implementation answers `["peer", "record"]`. The share node
answers `["share", "annotate"]` — comment writes shipped first; the
annotation-layer route is still designed-only and answers 404 until it lands,
which additive-tolerant clients must handle anyway.

## Peer routes (shipping)

```
GET  /ping          {app, version, proto, machine, captures[, capabilities]}
GET  /recent?n=     newest captures, metadata only (n clamped 1–1000)
GET  /search?q=     full-text over OCR text, same quoting rules everywhere
GET  /meta/{id}     one capture, including ocr_text + ocr_engine_version
GET  /thumb/{id}    pre-scaled JPEG (448px wide) for grid tiles
GET  /file/{id}     the original PNG/MP4
                    ?alt=gif    a recording's sibling GIF
                    ?alt=thumb  a recording's extracted preview frame
POST /ingest        multipart/form-data: "meta" JSON + "file" bytes
                    (+ optional "gif" / "thumb" parts for recordings)
POST /pair          {pin, machine} → {token, machine}
```

### Shot metadata

Returned by `/recent`, `/search`, `/meta`. Lists omit `ocr_text`; `/meta`
includes it.

```json
{
  "id": 48213,
  "file_name": "2026-08-16_14-22-03.png",
  "taken_at": "2026-08-16T14:22:03.4512345-05:00",
  "width": 2560, "height": 1440,
  "sha256": "A1B2…",
  "kind": "image",
  "duration_ms": 0,
  "origin": "",
  "has_gif": false,
  "ocr_text": "…",
  "ocr_engine_version": "winocr/10.0.26200.0"
}
```

`taken_at` is ISO 8601 round-trip (`o`) with offset. `kind` is `"image"` or
`"video"`. `origin` is `""` for captures taken on the serving machine, else
the name of the machine it came from.

### The versioned OCR sidecar

The rule that makes a heterogeneous mesh work: **a receiver imports OCR text,
it never re-runs OCR on an ingested capture.** `ocr_engine_version` records
which engine produced the text, so a future upgrade can re-OCR *selectively*
rather than blindly.

Engine version strings are `<engine>/<version>`:

| Platform | Engine | Example |
|---|---|---|
| Windows | `Windows.Media.Ocr` (no version of its own — OS build is the honest proxy) | `winocr/10.0.26200.0` |
| macOS | Vision `VNRecognizeTextRequest` | `vision/3+25A354` (request revision + OS build) |

`ocr_text: null` on an image means *the sender had not OCR'd it yet*, not
*this image has no text*. The receiver leaves the row pending so its own
backlog sweep fills the hole. Senders must not fabricate empty strings.

### Ingest semantics

Dedupe is **global by sha256**, not time-windowed: a retried push, or a
pull-then-sync of the same capture, lands exactly once. The response says
which happened:

```json
{ "id": 902, "duplicate": false }
```

A duplicate is a success, not an error. Callers may mark the item pushed and
move on.

### Pairing

`POST /pair` is answered only while a pairing window is open on the serving
machine. The PIN is 6 digits from a CSPRNG, compared in constant time,
single-use, expires with the window or at two minutes, and five wrong guesses
close the session. A successful response is the only time a token crosses
the wire.

Responses are part of this contract — clients classify outcomes by status
**and** body, so the bodies below are normative, not decoration:

| State | Response |
|---|---|
| PIN correct | `200 {"token": "…", "machine": "…"}` |
| PIN wrong, window open | `401 {"error": "wrong pin"}` |
| Malformed body / empty pin, window open | `400 {"error": "bad pair request"}` |
| No window open (or session spent) | as if the route did not exist — see below |

When no window is open, `/pair` must be indistinguishable from a route the
server never had: without a valid token the answer is the ordinary
`401 {"error": "missing or wrong token"}`, and with one it is
`404 {"error": "no such endpoint"}`. Anything more specific (an earlier
revision answered a distinctive 404 body) lets any host that can reach the
port fingerprint an esgee server and its pairing state without holding a
token.

## Share routes (shipping)

Shares are a different noun from peers and get their own namespace. See
[docs/SHARES.md](SHARES.md) for the model and the UX; this section is the
wire contract only. The headless node (`src/Esgee.Node`,
`esgee-node --serve-share`) is the reference implementation; the Windows and
Mac apps consume these routes through a share client and never serve them.

```
GET    /share                        {id, name, members, item_count, retention_days}
GET    /share/members                [{member_id, display_name, joined_at, role}]
GET    /share/items?since=&n=        {items: […], deleted: […]} — see below
GET    /share/search?q=              item list, same FTS semantics as peer /search
GET    /share/items/{item}           full item: metadata, ocr, comments
GET    /share/items/{item}/thumb     pre-scaled JPEG
GET    /share/items/{item}/file      original bytes (?alt=gif|thumb as above)
POST   /share/items                  multipart: "meta" + "file" (+ "gif"/"thumb"
                                     recording siblings) → the item + duplicate
DELETE /share/items/{item}           author or operator only → tombstone
POST   /share/items/{item}/comments  {body} → the created comment
POST   /share/items/{item}/annotations {shapes:[…]} → annotation layer
                                     (designed, not yet served — answers 404)
POST   /share/join                   {invite, display_name} → {token, member_id}
```

`GET /ping` on a share node is the ordinary handshake with
`capabilities: ["share", "annotate"]` and `captures` = live item count.
`GET /share` describes the share: `id` is assigned by the share on first
serve and stable across restarts and renames, `members` is a count (the
roster lives at `/share/members`), and `retention_days` is `0` for
unlimited. Every share route except `POST /share/join` requires a member
token and answers the ordinary `401 {"error": "missing or wrong token"}`
without one.

### Item identity

A share item id is assigned by the share (`"itm_"` + 10 url-safe characters)
and is stable for every member. It is not anyone's local row id. `sha256`
remains the dedupe key: pushing a capture already present returns the
existing item with `duplicate: true`.

```json
{
  "item": "itm_7Kq2mZpXcV",
  "sha256": "A1B2…",
  "shared_by": "mem_OZHhgF1g",
  "shared_at": "2026-08-16T14:23:10.0000000+00:00",
  "taken_at": "2026-08-16T14:22:03.4512345-05:00",
  "width": 2560, "height": 1440,
  "kind": "image",
  "duration_ms": 0,
  "has_gif": false,
  "file_ext": ".png",
  "ocr_text": "…",
  "ocr_engine_version": "vision/3+25A354",
  "comment_count": 2,
  "latest_comment_at": "2026-08-17T09:40:33.0000000+00:00",
  "has_annotations": false
}
```

Note what is **absent**: the sharer's machine name, their local row id, their
archive path, their `origin` chain. Sharing a capture publishes the capture,
not the shape of your archive. Recordings carry `duration_ms` and `has_gif`
so a client knows to fetch the `?alt=gif` sibling; `shared_at` is stamped by
the share in UTC, `taken_at` round-trips as the sharer sent it.
`latest_comment_at` is the newest comment's stamp, omitted while an item is
uncommented — with `shared_at` it is the item's activity timestamp, the value
a `?since=` cursor advances over.

`file_ext` is the extension the share stored the bytes under (the sharer's
`file_name` contributed it at post time) — an extension only, never a name
or path, so clients that cache or pull an item can label the file honestly:
a shared JPEG must not leave a client renamed `.png`. Absent from an older
node's responses; clients then fall back to the kind's default extension.

Item lists omit `ocr_text`; `GET /share/items/{item}` includes it plus the
`comments` array. `POST /share/items` answers the **list** shape — no
`ocr_text`, no `comments` — plus `duplicate`; a duplicate is a success,
exactly as peer ingest. The meta part mirrors the ingest sidecar minus
everything a share must not know:
`{sha256, taken_at, width, height, kind, duration_ms, ocr_text,
ocr_engine_version, file_name}` (file_name contributes only its extension,
and only a short alphanumeric one — anything else falls back to the kind's
default, `.png` / `.mp4`). Any other field a client smuggles in — a
`shared_by`, an `origin` — is ignored. Dedupe is against **live** items only:
re-sharing a capture whose item was deleted mints a fresh item id, and the
old id stays tombstoned.

### Listing, tombstones, and the since= poll

`GET /share/items` answers an envelope, not a bare array:

```json
{
  "items":   [ … newest first, n clamped 1–1000 (default 200) … ],
  "deleted": [ {"item": "itm_iym3t8pXzT", "deleted_at": "2026-08-17T09:42:33Z"} ]
}
```

Deletion — by a member, the operator, or retention — keeps the item id as a
tombstone forever, so members can prune copies they pulled.
`?since=<timestamp>` filters both lists to activity after that instant:
items newly shared, items whose comments moved, and fresh tombstones. Pass a
timestamp a previous response returned (`shared_at`, `latest_comment_at`, or
`deleted_at`); this one query is what drives the tray's notification dot. A
malformed since is `400 {"error": "bad since"}`.

A since-filtered `items` list is ordered **oldest activity first** — the
opposite of the plain listing — and when more than `n` items changed the
envelope carries `"truncated": true` (omitted otherwise; tombstones are never
truncated). The pairing makes the poll lossless: the client advances `since`
to the newest timestamp it received and polls again, and everything held back
still lies after that cursor. Newest-first truncation would silently drop
exactly the old items a fresh comment resurfaced.

### Search

`GET /share/search?q=` matches the peer `/search` contract exactly: the same
quoting rules everywhere (each whitespace-separated term quoted and
prefix-matched, so user text can't hit FTS5 operator syntax), rank order,
and an empty `q` returns the newest 200. Only live items are searched —
tombstoned content leaves the index when it leaves the disk.

### Comments

`POST /share/items/{item}/comments {body}` appends — comments are never
edited or individually deleted; they are destroyed only with their item —
and answers the created comment; the same shape rides along on the
single-item GET:

```json
{"id": 2, "member_id": "mem_5nhIDMhS", "display_name": "Bob",
 "created_at": "2026-08-17T09:40:33.0000000+00:00",
 "body": "@Alice is that total right?"}
```

Authorship comes from the token. `display_name` is resolved by the share
(clients fall back to `member_id` should it ever be null), and `@name` is
parsed against `/share/members` — no separate mention machinery. An empty
body is `400 {"error": "empty comment"}`; a body over 4096 characters is
`400 {"error": "comment too long"}` — comments live as long as their item
and ride along on every detail fetch, so they stay note-sized.

### Deletion and retention

`DELETE /share/items/{item}` succeeds for the item's author or the operator
(`200 {"item": "…", "deleted": true}`); any other member gets
`403 {"error": "not your item"}`. Deletion destroys the content — the files,
the capture row, and the item's comments, which routinely quote the capture
and must not outlive it at rest — and keeps the tombstone. Retention (`retention_days`
in `GET /share`; `0` = unlimited) does exactly the same on the node's own
schedule. Deleting from a share never reaches into anyone's personal
archive: pulled copies are theirs.

### Identity and tokens

Every member holds their own token for the share. The server maps token →
member and stamps `shared_by` / comment authorship from that mapping — never
from a client-supplied field. Revoking one member is deleting one row; it
does not re-key anyone else. This is the concrete reason a share cannot reuse
the mesh's single `PeerToken`.

`POST /share/join` redeems a single-use invite minted by the share operator
and is the only share route that accepts a request without a member token.
The body is `{invite, display_name}`; the display name falls back to the
hint the operator minted the invite with, and when neither exists the answer
is `400 {"error": "display_name required"}` — without consuming the invite.
A name an existing member already goes by (compared case-insensitively) is
`400 {"error": "display_name taken"}`, also without consuming the invite —
display names are the only authorship humans read, so a second "Ben" would
spoof the first in every `shared_by`, comment, and `@mention`. Invites
expire after 24 hours. Spent, expired, and unknown invites are answered
identically — `401 {"error": "bad invite"}` — so the route cannot be used
to probe which codes exist.

Servers store member tokens and invite codes only as SHA-256 hashes,
compared in constant time; a raw token exists nowhere but the join response
that minted it. (The no-logging rule above applies to invite codes too.)

Invites travel as a URL: `esgee-share://<authority>#<code>`, where swapping
the scheme for `http://` yields the share's base URL (tailnet endpoints are
plain HTTP inside WireGuard). The authority is whatever the minting node
knew at mint time — its tailnet IP and port — and the operator may rewrite
it before sending if members reach the node another way.

### Annotations are a layer

Designed, not yet served: the node answers 404 on the annotations route
until the layer lands, and `has_annotations` stays `false`.

An annotation is JSON in image coordinate space, stored beside the original
and composited at display time:

```json
{ "shapes": [
  {"type": "arrow", "from": [120, 300], "to": [480, 310], "color": "#FF3B30"},
  {"type": "box",   "rect": [100, 280, 420, 60], "color": "#FF3B30"},
  {"type": "text",  "at": [120, 260], "body": "this number is wrong"}
]}
```

The original bytes never change, so sha256 stays a valid dedupe key, the OCR
index stays valid, and annotations stay editable and attributable.

**Redaction is the exception and must bake.** A redaction rendered at display
time is not a redaction — the pixels are still in the file any member can
`GET`. A redaction produces a *new* capture with the pixels destroyed, pushed
as its own item; the unredacted original is never shared. Implementations
must not accept `{"type": "redaction"}` as a layer shape.

## Versioning rules

- Additive fields are always allowed and must be ignored when unknown.
- New routes require a new capability string, not a `proto` bump.
- `proto` bumps only for an incompatible change to an existing route's
  request or response shape. There has not been one; `proto 1 → 2` is the
  addressing and capability change described above.
- Servers must answer unknown routes `404` with
  `{"error": "no such endpoint"}` and never 5xx.
