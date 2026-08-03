# esgee

**[beswift.github.io/esgee](https://beswift.github.io/esgee/)**

Fast screenshots for Windows: hotkey → capture → a draggable card in the
corner. Everything you take is saved automatically and **searchable by the
text that was on screen**.

- **Drag-out shelf** — captures stack as floating cards; drag one into any
  app as a real file, click to re-copy, or just ignore it (it's already saved).
- **OCR search** — every capture is indexed locally (Windows' built-in OCR +
  SQLite FTS5). Find any screenshot by what it said, in milliseconds.
- **Recording** — one hotkey records a region to MP4, short clips get a
  paste-ready GIF.
- **Instant repeats** — re-shoot your last region with one key while you
  iterate on a UI.

Local-first: nothing leaves your machine.

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
  file.
- **CLI:** `esgee --search "connection refused"` / `esgee --recent 20`.

Captures live in `%USERPROFILE%\esgee\yyyy\MM\` as ordinary PNG/MP4 files.

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
