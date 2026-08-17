# Direction — where esgee wants to go

Captured 2026-08-12 from a working session with Ben. This is product intent,
not a spec: the next design session should pressure-test all of it. The
constraint that overrides everything below: **keep the current aesthetic and
the simplified nature of the app.** Everything ships fast, feels tight, runs
local-first. Any feature that bogs that down is wrong even if it's useful.

## Context: how the app is actually used today

- Ben uses esgee on every Windows machine he owns, daily, heavily.
- Primary workflow: screenshots feed agents. An estimated 30–40% of
  agent/team collaboration is passing screenshots back and forth.
- The OCR is "surprisingly accurate" and used constantly — both search and
  the new copy-text path.
- Peers/pull/push between his own machines is the feature that made it
  stick: capture on one box, pull it on another.
- Log reporting for troubleshooting exists (`esgee --doctor`, local-only,
  opt-in paste) and that model — user opts in, nothing phones home — is the
  template for anything diagnostic.

## Goal 1 — a real Mac version (primary)

The surprise: even though esgee mimics several macOS-native capture
behaviors, the shelf + drag-out + OCR-archive loop is *better* than the
native experience, and Ben now wants it on his MacBook. Direction:

- A **fully Mac-native** app, not a port with a cross-platform toolkit —
  native is judged necessary to keep the speed and tightness that make the
  Windows app loved. (Swift/AppKit territory; Apple's Vision framework has
  on-device OCR; the archive/index format and the peer protocol are the
  contract both apps share.)
- Same soul: hotkey → shelf card → drag anywhere; everything auto-saved and
  OCR-indexed; archive window with search; peers.
- The point is linking ALL machines — Windows and Mac in one peer mesh.
  The wire protocol (plain HTTP + JSON over the tailnet, shared token,
  /ping /recent /search /meta /file /thumb /ingest /pair) is deliberately
  simple enough to reimplement anywhere.

Open questions: minimum macOS version; hotkey conventions on Mac (the
system owns Cmd+Shift+3/4/5 — pick chords that coexist); how much of the
capture pipeline is per-OS vs shared spec; CI for a second platform.

## Goal 2 — team shares (riding on goal 1)

The tension to design around: **solo mode wants everything shared
everywhere** (all my machines, all my captures, zero friction — this is
today's peers and it's correct), but **team mode must not default-share a
personal capture stream.** Nobody wants their whole shelf broadcast to
coworkers forever.

Sketch of the shape (name TBD — "team share" / "team store"):

- A team share is a **separate, explicit destination**. Your machines stay
  private-meshed as today; a capture reaches the team only by a deliberate
  push ("push to team share") — never by default.
- Multiple shares should probably exist (per team / per project).
- Everyone on the team sees the same share on their own machine, inside
  the same archive UI (the machine switcher naturally generalizes: This PC,
  my other machines, then team shares).
- The bar for the UX: sharing to the team must be *less* effort than
  copy-paste into Slack, or it loses to Slack.

Once shared captures exist without their author present, they need
traceability — the secondary UX layer:

- comments on a capture
- markup / annotation (arrows, boxes, redaction)
- notes attached to a capture
- tagging people

Design questions to resolve before building: where does a team share live
(one member's always-on box? a headless `esgee --serve` node the team runs?
a hosted relay?); identity (today's model is machine-pairing, teams need
person-level identity); retention and deletion semantics; whether Tailscale
(shared tailnets / Tailscale identities) is the substrate or just one
transport. A hosted version or hosted connection point is explicitly on the
table as an option to explore, weighed against the local-first ethos.

## Goal 3 — workflows on top of shares (tertiary, exploratory)

If team shares land well, shared+annotated captures become **triggers**:

- **Feedback loop for this app itself (dogfood first):** hit an issue in
  the wild → snapshot it → attach it to a support/feedback destination
  (idea sketch: a monitored inbox, e.g. a Resend account, feeding an
  internal queue) → an agent team picks it up: diagnose, design, build,
  test, ship — then message the reporter "this is fixed." A self-improving
  feedback-loop-driven workflow where the screenshot IS the bug report.
- **Power-user / agent integration:** an MCP server for esgee, so agents
  can take screenshots, search the archive, read screen text, annotate,
  and pass captures to teammates. The CLI already serves agents; MCP is
  the natural next interface. This is a standalone deliverable that's
  valuable even if teams never ship.

## Sequencing instinct

1. Mac app (the mesh getting cross-platform is the foundation everything
   else stands on)
2. Team shares + annotation/traceability
3. Workflow triggers + MCP server (MCP could jump the queue — it's small,
   independent, and serves the primary agent-feeding workflow today)

## Non-negotiables carried from the current app

- Local-first; nothing leaves the user's machines without an explicit act.
- No accounts for solo use, ever. Pairing stays PIN-simple.
- The glass aesthetic and the quiet, small-surface UI.
- Fast: capture-to-shelf latency and search-as-you-type must survive every
  addition.
