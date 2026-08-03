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
