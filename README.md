# esgee

A screenshot tool for Windows with its own capture overlay, a drag-out shelf,
and an OCR-searchable archive. Built for one workflow: taking screenshots
constantly and handing them to agents.

## Install

Download and run the latest installer — one command on a fresh machine:

```powershell
irm https://github.com/beswift/esgee/releases/latest/download/esgee-win-Setup.exe -OutFile "$env:TEMP\esgee-setup.exe"; & "$env:TEMP\esgee-setup.exe"
```

(Or grab `esgee-win-Setup.exe` from the
[releases page](https://github.com/beswift/esgee/releases) and double-click it.)

The app installs per-user to `%LOCALAPPDATA%\esgee\current\` — no admin, no
certificate warnings beyond the usual unsigned-binary SmartScreen click-through.
It **updates itself**: the resident tray app checks GitHub Releases shortly
after startup and every 12 hours, downloads quietly, and applies on the next
restart. The tray menu's **Check for updates** shows the running version and
can restart into a new one immediately.

First-run checklist:

- Tray menu → **Start with Windows** (a hotkey tool that isn't resident is a
  paperweight).
- Optional, to make plain **PrtScn** open esgee's overlay: see
  [PrintScreen note](#printscreen-note) below.
- Recording: the first time you press the record hotkey on a machine without
  FFmpeg, esgee offers a one-time, hash-verified download (~105 MB) — see
  [Recording](#recording).
- Remote machines via TeamViewer: enable **Send key combinations** or the
  Win/PrtScn chords stay on the local side.

Updates and reinstalls never touch your data: settings live at
`%LOCALAPPDATA%\esgee\settings.json` and captures at `%USERPROFILE%\esgee\`,
both outside the replaced `current\` directory. (A full *uninstall* removes
`%LOCALAPPDATA%\esgee` including settings — the capture archive still survives.)

### Why Velopack (and not MSIX/AppInstaller)

Self-updating installs are [Velopack](https://velopack.io) (the successor to
Squirrel.Windows): GitHub Releases is the entire update feed, delta updates
come free, and — decisive here — **no package signing**. MSIX/AppInstaller
wants a signed package, which for a personal tool means installing a
self-signed certificate on every machine first. Velopack needs nothing
per-machine: run Setup.exe, done.

## Capture

| Hotkey | Action |
|---|---|
| **Win+Shift+C** (also **Ctrl+Shift+S**, **PrintScreen**) | Region overlay — **C**rop |
| **Win+Shift+F** | **F**ull screen, instantly — no overlay |
| **Win+Shift+L** | **L**ast region re-shot instantly (persists across restarts) |
| **Win+Shift+D** | **D**elayed: countdown pill, then the region overlay on a re-frozen frame |
| **Win+Shift+G** | Recordin**g** — press to start, again to stop (MP4, + GIF when short) |

All are `settings.json` keys (`RegionHotkey`, `FullscreenHotkey`,
`LastRegionHotkey`, `TimerHotkey`, `TimerSeconds`). These are *global*
registrations, so what's actually bindable depends on what the shell and other
resident apps on a given machine already own: `Win+Shift+S` is the shell's own
snip (those captures reach the shelf via the clipboard watcher anyway), and
chords like `Win+Shift+P`/`T`/`A`/`M`/`R`/`V`/`W` are often taken. If a chord
doesn't register, `esgee.log` says so — pick another in settings.

Inside the region overlay:

| Input | Result |
|---|---|
| **Drag** | Capture that region (live pixel-dimension badge) |
| **Click a window** | Capture that window (DWM frame bounds, hover-highlighted) |
| **Enter / Space** | Capture the full screen |
| **1–9** | Delay: overlay hides, counts down N seconds top-center, then re-freezes — for menus/tooltips you have to arm |
| **Esc / right-click** | Cancel |

No Snipping Tool anywhere in the loop — nothing to wedge. `Win+Shift+S` still
works too: esgee watches the clipboard, so captures from *any* tool flow into
the same pipeline.

Every capture is simultaneously: **saved** to the archive, **copied** to the
clipboard (file + PNG + bitmap formats at once), **shown** on the shelf, and
**queued** for OCR.

## Recording

**Win+Shift+G** toggles a screen recording of the last selected region (the
same rect Win+Shift+L would re-shoot), or the full screen if none exists.
While recording, a small pill floats just outside the recorded region — red
dot, elapsed clock, stop square. Click it (or the hotkey again) to stop. The
pill is excluded from capture (`WDA_EXCLUDEFROMCAPTURE`), so it never appears
in the recording itself.

Output is an **MP4** (H.264 yuv420p, 30fps, faststart) in the same dated
archive tree as the PNGs. Recordings of **≤ 15s also get a GIF** (12fps,
max 960px wide, palette-optimized) — and when a GIF exists, *it* is what the
shelf card drags out and what lands on the clipboard, because a GIF is the
thing you paste into a chat. The MP4 is always there next to it (folder
button on the card). Recordings appear on the shelf and in the archive like
screenshots — thumbnail is a mid-clip frame — but skip OCR.

Engine: a static **ffmpeg** at `%LOCALAPPDATA%\esgee\bin\ffmpeg.exe` (plus
`ffprobe.exe`), so esgee never depends on PATH. It is *not* part of the app
package (that would triple every update): on a machine without it, the first
press of the record hotkey offers a one-time download of a pinned gyan.dev
build, verified against a pinned SHA-256 before install. Offline or declined,
recording stays off and the log says why; screenshots are unaffected. A copy
from `winget install Gyan.FFmpeg` dropped into that folder works too. Capture
is gdigrab; stop is a graceful `q` on ffmpeg's stdin — killing the process
would corrupt the MP4.

Settings: `RecordHotkey` (Win+Shift+G), `RecordFps` (30), `GifMaxSeconds`
(15, 0 disables GIFs), `GifFps` (12), `GifMaxWidth` (960).

## The shelf

Captures stack as cards in the bottom-right corner instead of fighting over the
clipboard's single slot:

| Gesture | Result |
|---|---|
| **Drag a card** | Drops as a real `.png` into any app |
| **Click** | Re-copies to clipboard |
| **Hover** | Pauses the fade, reveals copy / folder / pin / dismiss |
| 📌 | Keeps it indefinitely |
| Ignore | Fades after 8s — the file is already on disk |

Nothing on Windows ships the macOS floating-thumbnail drag-out; ShareX declined
exactly this ([#6991](https://github.com/ShareX/ShareX/issues/6991)). It's the
whole reason esgee exists.

## The archive

Windows' built-in OCR reads every capture (locally — nothing leaves the
machine) into a SQLite FTS5 index.

- **GUI**: double-click the tray icon, or `esgee --archive` — search-as-you-type
  over screen text, drag any tile out as a file, double-click to copy.
- **CLI**: `esgee --search "connection refused"`, `esgee --recent 20` — lets an
  agent find a screenshot from weeks ago by what was on screen.
- **Diagnostics**: `esgee --check-drag` round-trips the drag payload through the
  OS clipboard and reports surviving formats.

Files land in `%USERPROFILE%\esgee\yyyy\MM\` — deliberately not `Pictures`,
which is often OneDrive-redirected, and thousands of PNGs through sync is its
own outage.

## Building from source

```powershell
dotnet publish src/Esgee/Esgee.csproj -c Release -o "$env:LOCALAPPDATA\esgee\app"
```

Requires the .NET 10 SDK. A from-source copy runs identically but reports
v0.0.0 and does not self-update (no Velopack `Update.exe` beside it). Logs:
`%LOCALAPPDATA%\esgee\esgee.log`.

### Cutting a release

CI does everything on a version tag:

```bash
git tag v0.2.0 && git push origin v0.2.0
```

`.github/workflows/release.yml` publishes a self-contained build stamped with
that version, runs `vpk pack`, and uploads Setup.exe + full/delta packages to
a GitHub Release. Installed machines pick it up within 12 hours (or
immediately via tray → Check for updates). Pushes to `main` get a plain build
check (`build.yml`).

### PrintScreen note

Windows 11 routes PrtScn to Snipping Tool by default (shell-level, below
`RegisterHotKey`). To hand it to esgee instead:

```powershell
Set-ItemProperty "HKCU:\Control Panel\Keyboard" PrintScreenKeyForSnippingEnabled 0
```

(same as unchecking Settings → Accessibility → Keyboard → "Use the Print
screen key to open screen capture") — but the shell only re-reads it at
sign-in, so PrtScn comes alive after the next sign-out. Until then use the
region chord. If PrtScn ever seems dead, check that setting first.

## Layout

```
Capture/   ClipboardWatcher + HotkeyManager + OverlayWindow + CaptureController
           + WindowFinder + CountdownWindow — the capture front ends
           + ScreenRecorder + RecordController + RecordingIndicatorWindow —
             the ffmpeg-backed recording front end
           + FfmpegSetup — pinned, hash-verified first-run ffmpeg download
Store/     PNG/MP4 on disk + SQLite/FTS5 index (thread-safe; WAL)
Ocr/       Background Windows.Media.Ocr indexer
Ui/        ShelfWindow, ShotCard, ArchiveWindow, theme
Interop/   Win32 + the OLE drag source
Program.cs / UpdateService.cs — Velopack bootstrap + GitHub Releases self-update
```

Two capture sources feed one pipeline; more can be added without touching
anything downstream.

## Known gaps

- **Shelf/overlay anchor to the primary display work area**; multi-monitor
  mixed-DPI is untested (developed on a single-monitor machine).
- **No annotation** (arrows/blur/crop-after-capture).
- **No audio in recordings** (screen only; no mic/system audio).
- WPF's `InvariantGlobalization` incompatibility is documented in the csproj —
  don't re-add it.
- Binaries are unsigned; SmartScreen may ask for a "More info → Run anyway" on
  first install.
