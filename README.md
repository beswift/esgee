# esgee

**[beswift.github.io/esgee](https://beswift.github.io/esgee/)**

Fast screenshots for Windows: hotkey → capture → a draggable card in the
corner. Everything you take is saved automatically and **searchable by the
text that was on screen**.

- **Drag-out shelf** — captures stack as floating cards; drag one into any
  app as a real file, click to re-copy, or just ignore it (it's already saved).
- **OCR search** — every capture is indexed locally (Windows' built-in OCR +
  SQLite FTS5). Find any screenshot by what it said, in milliseconds.
- **The text comes back out** — right-click any capture → **Copy text**, or
  open the **Screen text** panel in the preview to read and select it. A
  screenshot of an error *is* the error message.
- **Recording** — one hotkey records a region to MP4, short clips get a
  paste-ready GIF.
- **Instant repeats** — re-shoot your last region with one key while you
  iterate on a UI.
- **Multi-machine** — pair your PCs with a 6-digit PIN and browse, search,
  pull, and push captures between them — entirely inside your own tailnet.

Local-first: nothing leaves your machine unless you enable
[Peers](#peers--sync) — and then data moves only between your own machines,
inside your own tailnet.

## Install

```powershell
irm https://github.com/beswift/esgee/releases/latest/download/esgee-win-Setup.exe -OutFile "$env:TEMP\esgee-setup.exe"; & "$env:TEMP\esgee-setup.exe"
```

Or grab `esgee-win-Setup.exe` from [releases](https://github.com/beswift/esgee/releases).
Per-user install, no admin. The app keeps itself updated from GitHub Releases.
After installing, enable **Start with Windows** from the tray menu.

## Hotkeys

| Hotkey | Action |
|---|---|
| **Win+Shift+C** (or **Ctrl+Shift+S**) | Region select — drag, or click a window to snap to it |
| **Win+Shift+F** | Full screen, instant |
| **Win+Shift+L** | Re-shoot the last region, instant |
| **Win+Shift+D** | Delayed capture (countdown, then select) |
| **Win+Shift+G** | Record region → MP4 (+GIF ≤15s); press again to stop |

Inside region select: **1–9** adds an N-second delay, **Enter** takes the full
screen, **Esc** cancels. All chords are configurable in
`%LOCALAPPDATA%\esgee\settings.json`; if one can't register (another app owns
it), the log says so — pick another.

Captures from *other* tools (Win+Shift+S included) land on the shelf too, via
the clipboard.

## Search

- **GUI:** double-click the tray icon (or `esgee --archive`) —
  search-as-you-type across the text of every capture; drag any tile out as a
  file. Click a tile to preview it; the **Screen text** button opens a panel
  with the capture's full OCR text — selectable, with one-click copy-all.
  Right-click any tile → **Copy text** skips the preview entirely.
- **CLI:** `esgee --search "connection refused"` / `esgee --recent 20`.
- **Health check:** `esgee --doctor` — archive stats, duplicate detection, and
  a digest of the local log. Runs entirely offline; paste it into bug reports.

Captures live in `%USERPROFILE%\esgee\yyyy\MM\` as ordinary PNG/MP4 files.

## Peers & sync

**Off by default.** When enabled, your machines can browse and copy each
other's archives — data moves only inside your private
[Tailscale](https://tailscale.com) tailnet, authenticated with a shared token,
and never touches any other network. Requires Tailscale running on each
machine; the tailnet link is WireGuard-encrypted, so the API is plain HTTP on
the Tailscale interface (never `0.0.0.0`, never the LAN).

To connect two machines (Bluetooth-style, no settings editing):

1. On one machine: tray → **Peers** → **"Pair a new machine…"**. A window
   shows a 6-digit PIN with a 2-minute fuse. First use turns peers on and
   generates the shared token automatically.
2. On the other machine: tray → **Peers** → **"Pair with another
   machine…"**, type the PIN. It finds the first machine over the tailnet,
   receives the token, and switches peers on — live immediately, no restart.
3. Repeat for each additional machine (pair it with any machine that's
   already in). The Peers menu shows current state ("Peers: on (N
   machines)") and has a **Disable peers** switch; disabling closes every
   socket but keeps the token, so re-pairing is instant.

Once paired, the archive window grows a machine switcher (This PC + every
peer that answers). Browse, search, and preview a peer's captures exactly
like local ones; drag a remote tile out and it downloads first, then drops
as a real file. Right-click → **Pull to this PC** copies a capture into the
local archive for keeps (marked with its origin machine).

<details>
<summary>Manual pairing (headless machines / no tray)</summary>

The PIN flow just automates the shared secret. To do it by hand: on a paired
machine copy `PeerToken` out of `%LOCALAPPDATA%\esgee\settings.json`; on the
new machine paste that exact value in, set `"PeersEnabled": true`, and
restart esgee. This is also the escape hatch for boxes running only
`esgee --serve`.
</details>

Optional push sync: set `"SyncTargetPeer": "<machine-name>"` (a tailnet
hostname, or `host:port`) on a machine and every new capture is pushed to
that peer in the background — queued and retried when it's offline, deduped
by content hash on arrival, with the OCR text shipped alongside so the
receiver never re-OCRs. The tray menu shows sync state.

Settings reference: `PeersEnabled` (default false), `PeerToken` (shared
secret, same on every machine), `PeerPort` (default 43117), `Peers` (manual
`name=host:port` fallback list if tailnet discovery can't see a machine),
`SyncTargetPeer` (empty = no push). Protocol details and the security model
live in [docs/NOTES.md](docs/NOTES.md).

## For agents

esgee doubles as a screenshot memory your coding agents can query — the
archive is plain files plus a CLI:

```
esgee --search "<text that was on screen>"   # returns matching file paths
esgee --recent <n>                           # newest captures
```

Point an agent at these and it can retrieve "that error dialog from last
Tuesday" by content. See [AGENTS.md](AGENTS.md) for repo conventions if
you're pointing an agent at the codebase itself.

## Building from source

```powershell
dotnet publish src/Esgee/Esgee.csproj -c Release
```

Requires the .NET 10 SDK. Releases are cut by pushing a version tag — CI
builds, packages, and publishes; installs self-update within 12 hours.

More detail — architecture, design notes, Windows quirks (PrintScreen
routing, hotkey collisions, remote-desktop key forwarding), and known gaps —
lives in [docs/NOTES.md](docs/NOTES.md).

## License

MIT
