# esgee — design notes & Windows quirks

The README stays short; the reasoning and the sharp edges live here.

## Why it exists

Nothing on Windows ships the macOS-style floating-thumbnail drag-out — take a
screenshot, drag the little preview straight into another app. ShareX declined
exactly this ([ShareX#6991](https://github.com/ShareX/ShareX/issues/6991)).
The shelf is that feature, and the rest of esgee grew around it: if a capture
is worth taking it's worth saving, and if it's saved it should be findable —
hence the always-on archive and OCR index.

The design discipline: every feature is "another way to put a card on the
shelf." Capture sources (clipboard watcher, region overlay, recorder) feed one
pipeline — save → shelf → clipboard → index — and new sources can be added
without touching anything downstream.

## Architecture

Three projects since the Core/Node split (docs/SHARES.md "The node is a
Linux binary"): everything desktop-free lives in `Esgee.Core`, the WPF app
and the headless node both reference it. Build both
`src/Esgee/Esgee.csproj` and `src/Esgee.Node/Esgee.Node.csproj` to know a
Core change broke neither consumer.

```
src/Esgee.Core/          net10.0, no WPF anywhere in its closure
  Store/     PNG/MP4 on disk + SQLite/FTS5 index (thread-safe; WAL)
  Peers/     Opt-in machine-to-machine layer over Tailscale: PeerServer (the
             HTTP API), PeerClient (+ discovery + peer cache), SyncQueue
             (background push), Tailscale (NIC scan + CLI wrapper),
             PeerProtocol (DTOs), Http (shared request parser/response
             writer/bind policy for every esgee server)
  Shares/    ShareClient (browse/join/push wire half), SharePusher,
             ShareProtocol (DTOs)
  Settings / Log / Cli — shared config, the audit log, headless query verbs

src/Esgee/               the WPF app (references Core)
  Capture/   ClipboardWatcher + HotkeyManager + OverlayWindow + CaptureController
             + WindowFinder + CountdownWindow — the capture front ends
             + ScreenRecorder + RecordController + RecordingIndicatorWindow —
               the ffmpeg-backed recording front end
             + FfmpegSetup — pinned, hash-verified first-run ffmpeg download
  Ocr/       Background Windows.Media.Ocr indexer
  Ui/        ShelfWindow, ShotCard, ArchiveWindow, ShareJoinWindow, theme
  Interop/   Win32 + the OLE drag source
  Program.cs / UpdateService.cs — Velopack bootstrap + GitHub Releases self-update

src/Esgee.Node/          esgee-node, the headless peer/share server
  --serve (peer archive) / --serve-share + --share-invite (the team share
  node, docs/SHARES.md); ShareStore/ShareServer, ImageSharp thumb encoder;
  published self-contained linux-x64 (runs on Windows too for local tests)
```

### The search pattern

OCR runs once, at capture time, on a background queue (Windows' built-in
`Windows.Media.Ocr` — local, no network, ~100–300 ms/image). Text lands in a
SQLite **FTS5** index. Query time is therefore just an inverted-index lookup:
single-digit milliseconds at any archive size. Items that miss OCR (app was
closed, engine unavailable) are picked up by a backlog sweep on startup;
failures are marked done so a bad file can't wedge the queue.

### Recording internals

Capture is ffmpeg `gdigrab` → libx264 yuv420p 30fps `+faststart`. Stop is a
graceful `q` on ffmpeg's stdin — killing the process corrupts the MP4 (no
moov atom). Clips ≤ `GifMaxSeconds` also get a GIF via
`palettegen`/`paletteuse`. When a GIF exists it's the drag/clipboard payload
(a GIF is the thing you paste into a chat); the MP4 sits beside it. The
recording pill is excluded from capture via
`SetWindowDisplayAffinity(WDA_EXCLUDEFROMCAPTURE)` — visible to you, absent
from the recording.

ffmpeg is deliberately not bundled in the app package (it would triple every
update). First press of the record hotkey offers a one-time download of a
pinned gyan.dev build, verified against a pinned SHA-256, into
`%LOCALAPPDATA%\esgee\bin\`. A copy from `winget install Gyan.FFmpeg` dropped
in that folder works too.

### Peers: protocol & security model

Everything peer-shaped is opt-in and additive. With `PeersEnabled: false`
(the default) and no `SyncTargetPeer`, esgee opens **zero sockets** and the
peer code never runs — behavior is identical to pre-peer releases.

**Security model in two sentences:** reachability is tailnet membership —
the server binds exclusively to this machine's Tailscale IPv4 (read off the
Tailscale adapter itself — the 100.64/10 address on the NIC that identifies
as Tailscale's — with `tailscale ip -4` as the fallback for userspace-
networking setups), never `0.0.0.0`, so only devices already admitted to
your WireGuard-encrypted tailnet can even connect. Authorization is a shared
secret: every request must carry the `X-Esgee-Token` header matching
`PeerToken` (generated on first enable, compared in constant time), so a
stray tailnet device without the token gets nothing but 401s.

HTTPS is deliberately absent: the tailnet link is already
WireGuard-encrypted end to end, so TLS would add certificate management for
zero additional confidentiality. If no Tailscale IPv4 can be found — the
adapter is down or absent AND the CLI can't answer (not installed, logged
out) — the server simply doesn't start; it never falls back to a wider bind.

**Pairing (how the token travels):** machines exchange the token
Bluetooth-style, from the tray, so nobody edits settings.json. "Pair a new
machine…" opens a window showing a 6-digit PIN; while that window is open —
and only then — the server answers `POST /pair`, the single route that
authenticates by PIN instead of token (getting the token is its whole
point). The PIN is `RandomNumberGenerator`-uniform, compared in constant
time, single-use (the first success consumes it), dies with the window or at
the 2-minute mark, and 5 wrong guesses close the window in a
"too many attempts" state — so a guesser gets 5 tries against a keyspace of
10⁶ per human-supervised session, on a network that already requires tailnet
membership. "Pair with another machine…" discovers candidates (online
tailnet nodes + manual `Peers` entries), POSTs the PIN to each, adopts the
token from whoever accepts, saves settings, and brings the peer layer up
in-process — no restart. First use of "Pair a new machine…" on a fresh
machine auto-enables peers and mints the token. Disabling peers from the
tray closes every socket but keeps the token, so re-pairing is instant.
Neither PIN nor token values are ever logged; only outcomes are.

**The API** (HTTP/1.1 + JSON, snake_case, `proto: 2` in /ping). This section
describes what ships; [docs/PROTOCOL.md](PROTOCOL.md) is the normative
contract a second implementation writes against:

```
GET  /ping          {app, version, proto, machine, captures, capabilities}
                    Windows answers capabilities: ["peer", "record"]; a peer
                    that omits the field is proto 1 and implicitly ["peer"]
GET  /recent?n=     newest captures (metadata list)
GET  /search?q=     FTS5 search, same quoting rules as the archive window
GET  /meta/{id}     one capture, including ocr_text + ocr_engine_version
GET  /thumb/{id}    pre-scaled JPEG for grid tiles
GET  /file/{id}     the original PNG/MP4; ?alt=gif / ?alt=thumb fetch a
                    recording's sibling GIF / preview frame
POST /ingest        multipart/form-data: "meta" JSON sidecar + "file" bytes
                    (+ optional "gif"/"thumb" parts for recordings)
POST /pair          {pin, machine} → {token, machine}. PIN-authenticated (the
                    one tokenless route); answers only while a pairing window
                    is open on the serving machine. With NO window open the
                    route is indistinguishable from one that never existed —
                    the generic 401 "missing or wrong token" without a token,
                    the generic 404 with one — so an esgee server can't be
                    fingerprinted pre-pairing. Clients tell a missed PIN from
                    no-window by the 401 BODY ("wrong pin" appears only while
                    a window is open); PROTOCOL.md "Pairing" has the
                    normative outcome table
```

**Why a hand-rolled TcpListener responder** rather than HttpListener or
Kestrel: `HttpListener` on a non-localhost prefix requires a netsh URL ACL —
an admin step, unacceptable for a per-user app. Embedding Kestrel
(`FrameworkReference: Microsoft.AspNetCore.App`) means shipping the ASP.NET
Core framework inside every self-contained build, ballooning each full
update by tens of MB — the same reason ffmpeg isn't bundled. Eight fixed
routes serving one trusted client don't need a framework; the whole
responder (incl. a minimal multipart parser) is one file. Gotcha learned en
route: .NET's `MultipartFormDataContent` emits *unquoted* part names
(`name=meta`) while curl quotes them — the parser accepts both.

**Ingest & the versioned sidecar:** the receiver dedupes by sha256 (global,
not time-windowed — a retried push or a pull-then-sync must land exactly
once), writes the file into its own `yyyy\MM` tree, and **imports the OCR
text from the sidecar instead of re-OCRing**. `ocr_engine_version` (e.g.
`winocr/10.0.26200.0` — Windows.Media.Ocr has no version of its own, the OS
build is the honest proxy) is stored per row, so a future, better engine can
re-OCR selectively — only rows produced by older engines — instead of
blindly. A sidecar with `ocr_text: null` on an image leaves `ocr_done=0`, so
the receiver's own backlog sweep fills the hole. Rows gained two additive
columns (`origin`, `ocr_engine_version`); sync bookkeeping is a new
`sync_pushed` table — older app versions sharing the DB ignore all three.

**Push sync (SyncQueue):** the capture pipeline's only contribution is a
non-blocking channel write, so the hotkey → shelf path is untouched in every
configuration. The worker waits briefly for local OCR (so sidecars carry
text), pushes with exponential backoff when the target is offline, and a
startup sweep enqueues anything `sync_pushed` doesn't know about — captures
taken while the target was down, or before sync was enabled, catch up on
their own. Shots that *originated* at the target are skipped (no echo
loops).

**Remote browsing:** the archive window's machine switcher discovers peers
by probing `tailscale status --json` nodes for `/ping` with the token
(manual `Peers` entries — `name=host:port` or full `name=http(s)://…` URLs —
as fallback). A peer is a name plus an opaque base URL; routes are appended,
never rebuilt from host/port parts. A remote tile's thumbnail streams from
`/thumb` off the UI thread; drag-out **must** hand CF_HDROP a real file, so
remote files are materialized into `%LOCALAPPDATA%\esgee\peercache\<peer>\`
— prefetched on mouse-down and on preview, so the drag is usually instant; a
cold drag downloads inline (slower, never broken). "Pull to this PC" turns a
remote capture into a first-class local row via the same ingest path,
`origin` set to the source machine.

**Testing with one machine:** the self-peer loopback is a supported
configuration — the machine's own tailnet IP serves its real archive and
shows up in its own machine switcher. `esgee --serve --archive-root <dir>
--port <p> [--token <t>]` runs a headless receiver against a second archive
root, `esgee --check-peer [host[:port]|name]` exercises ping → recent →
cache download → CF_HDROP round-trip, and `--archive-root` works on every
CLI verb.

### Why Velopack (not MSIX/AppInstaller)

GitHub Releases is the entire update feed, delta updates come free, and —
decisive — **no package signing**. MSIX/AppInstaller requires a signed
package, which for a personal tool means installing a self-signed certificate
on every machine. Velopack needs nothing per-machine.

Two install caveats, verified the hard way: **re-running Setup.exe over an
existing install** and **uninstalling** both reset `%LOCALAPPDATA%\esgee`
entirely (settings.json and the downloaded ffmpeg included). Normal
self-updates only swap `current\` and are safe. The capture archive in
`%USERPROFILE%\esgee` survives everything.

### Why the archive isn't in Pictures

`Pictures` is frequently OneDrive-redirected, and thousands of PNGs a day
through sync is its own outage. `%USERPROFILE%\esgee` is flat, local, and
still trivially findable.

## Windows quirks

### PrintScreen routing

Windows 11 routes PrtScn to Snipping Tool by default, at shell level — below
`RegisterHotKey`, so a hotkey registration *succeeds* but never fires. To hand
PrtScn to esgee:

```powershell
Set-ItemProperty "HKCU:\Control Panel\Keyboard" PrintScreenKeyForSnippingEnabled 0
```

(same as unchecking Settings → Accessibility → Keyboard → "Use the Print
screen key to open screen capture"). The shell only re-reads this at sign-in,
so it takes effect after the next sign-out. If PrtScn ever seems dead, check
this setting first.

### Hotkey collisions

Chords are global `RegisterHotKey` registrations, so availability depends on
what the shell and other resident apps already own. `Win+Shift+S` is the
shell's own snip (its captures still reach the shelf via the clipboard
watcher). On some machines many `Win+Shift+<letter>` chords are taken; a
failed registration is logged, and any chord can be changed in settings. A
quick probe loop over `RegisterHotKey` tells you what's free on a given
machine.

### Remote desktop

Remote-control clients (TeamViewer et al.) don't forward Win-key chords by
default — enable the client's "send key combinations" option or Win+Shift
hotkeys act on the local machine instead. Ctrl-based chords pass through
regardless.

## Known gaps

- Shelf/overlay anchor to the primary display work area; multi-monitor
  mixed-DPI is untested (developed on a single-monitor machine).
- No annotation (arrows/blur/crop-after-capture).
- No audio in recordings.
- Binaries are unsigned; SmartScreen asks for "More info → Run anyway" once.
- WPF's `InvariantGlobalization` incompatibility is documented in the csproj —
  don't re-add it.
