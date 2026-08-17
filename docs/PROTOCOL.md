# esgee wire protocol

The contract every esgee implementation shares. Windows (`src/Esgee/`) is the
reference implementation of **proto 1**. The Mac app
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

> Proto 1 as shipped models peers as `(host, port)` and builds
> `http://{host}:{port}`. Migrating that field to a base URL is the single
> highest-leverage change in this document and is backward compatible —
> existing `Peers` settings entries (`name=host:port`) expand to the same URL.

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

## Peer routes (proto 1 — shipping)

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
machine — 404 otherwise, so the route is invisible the rest of the time. The
PIN is 6 digits from a CSPRNG, compared in constant time, single-use, expires
with the window or at two minutes, and five wrong guesses close the session.
A successful response is the only time a token crosses the wire.

## Share routes (proto 2 — designed, not built)

Shares are a different noun from peers and get their own namespace. See
[docs/SHARES.md](SHARES.md) for the model and the UX; this section is the
wire contract only.

```
GET    /share                        {id, name, members, item_count, retention_days}
GET    /share/members                [{member_id, display_name, joined_at, role}]
GET    /share/items?since=&n=        item list, newest first
GET    /share/items/{item}           full item: metadata, ocr, comments, annotations
GET    /share/items/{item}/thumb     pre-scaled JPEG
GET    /share/items/{item}/file      original bytes (?alt=gif|thumb as above)
POST   /share/items                  multipart: "meta" + "file" → share an item
DELETE /share/items/{item}           author or operator only
POST   /share/items/{item}/comments  {body} → comment
POST   /share/items/{item}/annotations {shapes:[…]} → annotation layer
POST   /share/join                   {invite, display_name} → {token, member_id}
```

### Item identity

A share item id is assigned by the share and is stable for every member. It
is not anyone's local row id. `sha256` remains the dedupe key: pushing a
capture already present returns the existing item with `duplicate: true`.

```json
{
  "item": "itm_7Kq2",
  "sha256": "A1B2…",
  "shared_by": "mem_ben",
  "shared_at": "2026-08-16T14:23:10Z",
  "taken_at": "2026-08-16T14:22:03Z",
  "width": 2560, "height": 1440,
  "kind": "image",
  "ocr_text": "…",
  "ocr_engine_version": "vision/3+25A354",
  "comment_count": 2,
  "has_annotations": true
}
```

Note what is **absent**: the sharer's machine name, their local row id, their
archive path, their `origin` chain. Sharing a capture publishes the capture,
not the shape of your archive.

### Identity and tokens

Every member holds their own token for the share. The server maps token →
member and stamps `shared_by` / comment authorship from that mapping — never
from a client-supplied field. Revoking one member is deleting one row; it
does not re-key anyone else. This is the concrete reason a share cannot reuse
the mesh's single `PeerToken`.

`POST /share/join` redeems a single-use invite minted by the share operator
and is the only share route that accepts a request without a member token.

### Annotations are a layer

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
