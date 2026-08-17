# Team shares — design

Goal 2 from [DIRECTION.md](DIRECTION.md). The wire contract lives in
[docs/PROTOCOL.md](PROTOCOL.md); this is the model, the topology, and the UX.

## The invariant

> Solo mode wants everything shared everywhere. Team mode must never
> default-share a personal capture stream.

Everything below is downstream of that one sentence, and it is enforced
structurally rather than by UI convention:

**A share can only ever receive a capture through an explicit per-capture
act.** `SyncTargetPeer` — the auto-push setting — may name a peer and must
reject a share. That is a validation rule, not a guideline. There is no
configuration in which captures flow to a team continuously.

The corollary matters just as much: a share never publishes the shape of your
archive. A share item carries the capture, its OCR text, and who shared it. It
does not carry your machine name, your local row id, your file path, or the
`origin` chain that says which of your machines it came from.

## Share is a different noun from peer

Not a peer with a flag. The two have opposite defaults, and collapsing them is
how the invariant gets lost.

| | **Peer** | **Share** |
|---|---|---|
| Whose | mine | ours |
| Membership | machines I own | people I work with |
| Identity | machine name | person, chosen at join |
| Flow | bidirectional, automatic | inbound only, explicit, per capture |
| Auth | one token, whole mesh | one token **per member** |
| Content | my whole archive | the items someone deliberately pushed |
| Lifetime | as long as I own the machine | retention policy, revocable membership |

They share a namespace in exactly one place — the archive window's machine
switcher — because that generalization is natural and DIRECTION calls for it:

```
This Mac
─────────────
alphalfa
workshop
─────────────
Design team          ← shares, visually separated, never mixed in
Project Northwind
```

## Where a share lives

**A headless `esgee --serve-share` node on an always-on box, reached over
Tailscale.** No new infrastructure, no accounts, no hosting bill, and it
reuses the peer server almost verbatim.

Decided 2026-08-17: the node runs on **minimax**, the box already hosting the
team hub/portal (LLM gateway, fleet monitoring). It is always on, already
team-facing, and already on the tailnet — the share is one more service on
the machine whose job is team services.

Hosted is not built. It is also not foreclosed: the three portability
decisions below mean a hosted relay would be a *new server implementing a
documented protocol*, not a refactor of the client. See "Portability, and what
it costs" at the end.

### Tailnet topology — share the node, not the tailnet

The obvious move is to invite teammates as users on your tailnet. Don't. That
puts their devices and your personal machines on one network, with ACLs as the
only thing standing between a coworker and your laptop's peer server — and
the default ACL is allow-all.

Instead, use Tailscale's **device sharing** to share only the share node with
each teammate's own Tailscale account. They stay on their own tailnet. Exactly
one machine is reachable, and it's the one that only ever holds deliberately
shared captures. The invariant then holds at the network layer, not just in
the UI: your personal machines are not merely un-shared, they are
*unreachable*.

Verified against Tailscale docs 2026-08-17: sharing is available on **all
plans including free**; invites go out from the admin console (Machines →
node → Share) by email or link; recipients accept into their own tailnet and
must be Owner/Admin of it (automatic on personal accounts). Shared machines
are **quarantined by default** — recipients can connect *to* the node, but
the node cannot initiate connections into their networks. That default is
correct here; leave it on. Recipients' seats are their own; the sharer's
user count is untouched.

If device sharing doesn't fit, the fallback is inviting users plus an ACL that
grants them the share node's port and nothing else:

```jsonc
// grant teammates the share node's port and nothing else
{
  "src": ["group:team"],
  "dst": ["tag:esgee-share:43118"]
}
```

Confirm current Tailscale plan terms before committing — sharing rules, user
counts, and free-tier limits change, and a three-person team sits right at the
boundary.

### The node itself

Shipped 2026-08-17:

```
esgee-node --serve-share --archive-root /srv/esgee/design-team \
           --share-name "Design team" --token-file /etc/esgee/share-op.token \
           [--port 43118] [--bind <ip>] [--retention 90]
```

It is a peer server with the share routes, a `members` table, and no capture
pipeline. Its archive root is its own — a share's contents are never
commingled with a personal archive, even when the node runs on a machine that
also runs esgee normally (the flag is mandatory for exactly that reason). The
token is the **operator's** bootstrap credential: it registers (or re-keys,
on rotation) the share's operator member at startup, and members never see
it. All share state lives in the share archive's own `index.db` — additive
`members` / `invites` / `share_items` / `comments` tables beside the ordinary
`shots` — so one folder is still the whole share.

### The node is a Linux binary (minimax is Linux)

That makes a WPF-free build a phase-2 requirement, not a nicety. Surveyed
2026-08-17: the dependency boundary is already almost perfect — every
`System.Windows.*` / `System.Drawing` using sits in `Ui/`, `Capture/`,
`App.xaml.cs`, or `Interop/`, except one: `PeerServer.EncodeThumb` decodes
thumbnails with WPF's `BitmapImage`. Store, protocol, client, pairing, sync,
settings, log, and the headless CLI verbs are pure BCL + Microsoft.Data.Sqlite.

The split:

```
src/Esgee.Core/   net10.0        Store/, Peers/, Log, Settings, headless CLI
src/Esgee/        net10.0-windows  the WPF app, references Core — unchanged
src/Esgee.Node/   net10.0        esgee-node console app: --serve /
                                 --serve-share; published self-contained
                                 linux-x64; systemd unit on minimax
```

`EncodeThumb` sits behind the `IThumbEncoder` seam (shipped): WPF keeps
`BitmapImage`, the node uses ImageSharp. The hand-rolled `TcpListener`
responder proved platform-neutral as-is — the share server reuses its
request parser, response writer, and bind policy verbatim — and the
no-Kestrel rationale holds even better on a headless node.

The node never OCRs (no WinRT dependency): items arrive with sidecar text,
and the client-side rule below (don't push OCR-pending images) closes the
only gap that policy leaves.

## Joining — no accounts, still person-level identity

Pairing's PIN dance works because two machines are in front of one human. A
headless share node has nobody standing at it, so the flow inverts: the
operator mints, the member redeems.

```
operator:  esgee-node --share-invite --hint "Ben" --archive-root /srv/esgee/design-team
           → 8fK2q…                                  (the code, single-use, 24h)
           → esgee-share://100.64.0.9:43118#8fK2q…   (host = the node's tailnet
             IP at mint time; rewrite it if members reach the node elsewhere)
member:    Tray → "Join a team share…" → paste → choose display name → in
```

Both halves are shipped: the wire side (invite mint, `POST /share/join`,
per-member tokens, hint fallback for the display name;
`ShareClient.ParseInviteUrl` / `JoinAsync` are the client half) and the tray
join UI (`ShareJoinWindow` — paste, name, retryable outcomes kept apart from
the fatal one). The invite is single-use, expires (24h), and is bound to nothing until
redeemed. Redeeming mints **that member's own token** and records their chosen
display name. From then on the server stamps `shared_by` and comment
authorship from the token — never from a client-supplied field, so a member
cannot post as someone else.

Identity is a display name, deliberately. Not email, not OIDC, not an account.
It is exactly enough to answer "who shared this" and "who is `@ben`," which is
all the traceability layer needs. Revoking someone is deleting one row; nobody
else re-keys. If a hosted version ever exists it can issue those same tokens
after a real login without the client noticing.

## Sharing a capture — the Slack bar

DIRECTION sets the bar: **less effort than copy-pasting into Slack.** Slack is
capture → ⌘V → Enter. Two actions, already cheap. Three paths beat or match it:

1. **From the shelf card.** The hover chrome already carries copy / reveal /
   pin / dismiss. Share becomes a fifth icon — one click, on the card that is
   already in front of you, with no window switch. This is the common case and
   it is one action to Slack's two.
2. **Capture straight to a share.** A hotkey bound to a default share: press,
   select region, done. The capture lands in your archive *and* the share in a
   single gesture. This is strictly fewer actions than any alternative,
   including Slack.
3. **From the archive.** "Push to share ▸" on any tile, for the capture you
   took yesterday.

With more than one share configured, the icon becomes a small menu
(`Design team` / `Project Northwind`) and the last-used share is default.

Confirmation is a quiet badge on the card, not a dialog and not a toast that
steals a corner of the screen. Anything modal in this path loses to Slack on
feel even if it wins on keystrokes.

## Browsing — shares are browsed, not mirrored

A share appears in the machine switcher and behaves exactly like a remote
peer: tiles stream thumbnails from the node, search hits the node's own FTS
index, drag-out materializes on demand, "Pull to this PC" turns an item into a
first-class local row.

Nothing from a share lands in your archive unless you pull it.

This is the single largest simplification available. A mirroring design needs
a sync engine, conflict resolution, retention echo, and a story for "the share
deleted it but I still have it." Live browsing needs none of that, reuses the
remote-browse code path already shipped, and is the correct privacy default in
both directions — your archive doesn't fill with your coworkers' screenshots
any more than theirs fills with yours.

The cost is honest: no offline access to a share, and search-as-you-type over
a share is a network round trip rather than a local index hit. Both are
acceptable; a share holds hundreds of items, not tens of thousands, and the
node is on a tailnet rather than the open internet.

## Traceability

Once a capture exists without its author present, it needs the layer
DIRECTION lists: comments, markup, notes, tagging people.

**Comments** are append-only `(item, member, created_at, body)`. Authorship
comes from the token. `@name` is parsed against the member list — no separate
mention infrastructure. Notes are comments; there is no reason for two nouns.

**Annotations are a layer, not baked pixels.** Shapes stored as JSON in image
coordinates, composited at display time. The original bytes never change, so
sha256 remains a valid dedupe key, the OCR index stays valid, and an
annotation stays editable and attributable to whoever drew it. Two people can
annotate the same capture without forking it.

**Redaction is the exception and must destroy pixels.** A redaction drawn as a
display-time layer is not a redaction — the original is still one `GET` away
for every member. Redacting produces a *new* capture with the pixels
overwritten, which is what gets pushed; the unredacted original never reaches
the share. The protocol forbids `redaction` as a layer shape so this cannot be
gotten wrong by a second implementation.

**Notification** stays minimal: the tray/menu-bar icon carries a dot when a
share has items or comments newer than your last view, driven by
`GET /share/items?since=`. No push service, no badge counts per item, no
email. A dot is enough to make you look, and looking is one click.

## Retention and deletion

The node owns its retention: `--retention 90` (days; `90d` also accepted),
or unlimited by default. Shipped: the sweep runs at startup and hourly,
deletes files and the capture row, and keeps the tombstoned id forever so
members can prune anything they pulled.

- A member may delete an item they shared.
- The operator may delete anything.
- Deleting from a share **never** touches anyone's personal archive. If you
  pulled it, it is yours; the share losing its copy is not a claw-back.

Revoking a member removes their token and their access. Their past items and
comments remain, attributed — a share that silently rewrites history when
someone leaves is worse than useless for the workflows in Goal 3.

## Portability, and what it costs

The temptation is to build a backend abstraction now so a hosted relay can be
dropped in later. That is over-optimizing: an interface with one
implementation gets designed wrong, because it is designed before anyone has
used a share.

The version that actually pays is three decisions in the **protocol**, not the
code. They cost roughly a day now and are expensive to retrofit onto live
content later:

1. **Endpoints are URLs, not `host:port`.** A relay is a path and TLS, not a
   port. Existing peers become `http://100.x.y.z:43117` and no client logic
   changes.
2. **Tokens are per-member, not per-mesh.** This is what makes a comment
   attributable, makes revocation a one-row delete, and — critically — is why
   handing a teammate access to a share does not hand them access to your
   personal machines. It is a security requirement first and a portability
   requirement second.
3. **Shared items are keyed by an id the share assigns**, with sha256 as the
   dedupe key. Everyone must name the same capture identically or comments
   don't anchor.

Make those three choices and the headless node *is* the reference
implementation. A Worker + R2 relay later is a from-scratch server
implementing documented routes — no client refactor, no plugin layer, no
abstraction in the app. Skip them and going hosted means changing addressing,
changing auth semantics, re-keying every shared item, and migrating comments
attributed to machine names, on live content.

Nothing else is built for portability. No storage abstraction, no auth
provider interface, no accounts.

## What this unlocks (Goal 3, for context only)

Shared, annotated, commented captures are exactly the trigger surface Goal 3
wants: a share whose items feed a queue is the "screenshot IS the bug report"
loop, and the same routes an agent needs to read a share are the ones the MCP
server would expose. Neither is designed here. Both get easier if the routes
above are right.

## Sequencing

| Phase | What | Blocks on |
|---|---|---|
| 0 | ~~URL addressing in the Windows client; `capabilities` in `/ping`; PROTOCOL.md as the normative doc~~ **shipped** | nothing — do it now, it's backward compatible |
| 1 | Mac app ([docs/MAC.md](MAC.md)) | phase 0 for addressing |
| 2 | ~~`Esgee.Core`/`Esgee.Node` split; `--serve-share` node (linux-x64 capable), member tokens, invite/join; tray join UI, push from the card and the archive tile, browse in the switcher~~ **shipped 2026-08-17** — still open: deploy on minimax | phase 0 |
| 3 | ~~Comments~~ **shipped on the node**; still open: annotation layer, redaction bake, the notification dot (the `?since=` query it polls is live) | phase 2 |
| — | MCP server | nothing; can jump the queue any time |

Phase 0 is small enough to land alongside ordinary work and it is the only
part that gets harder the longer it waits.

## Open questions

1. ~~Which box runs the share node?~~ **Resolved: minimax** (the team
   hub/portal box — always on, already team infrastructure).
2. ~~Does the plan cover device sharing?~~ **Resolved: yes, all plans
   including free.** Dry-run the invite flow from a second account before
   involving the team.
3. Retention default — 90 days, or unlimited until someone complains?
   (The shipped node defaults to unlimited; `--retention` opts in.)
4. Does a share want its own OCR pass? Items arrive with sidecar text from
   whichever engine took them, which is correct; but a share node with no
   OCR engine cannot fill a hole left by `ocr_text: null`. Simplest answer:
   the sharing client must not push an image whose OCR is still pending —
   wait for it, exactly as `SyncQueue` already does.
5. Is one share per team right, or one per project? The design supports many;
   the UX gets busy past about three.
