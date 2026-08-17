# esgee for macOS — design

Goal 1 from [DIRECTION.md](DIRECTION.md): a fully Mac-native app, same soul,
joining the same peer mesh as the Windows machines. This is the design, not a
task list.

The bar is not "esgee runs on a Mac." The bar is that the shelf + drag-out +
OCR-archive loop feels *better* than macOS's own capture experience — which is
the reason this is being built at all.

## Decisions locked

| | |
|---|---|
| **Language / UI** | Swift 6, AppKit shell, SwiftUI for the archive grid only |
| **v1 scope** | Region + fullscreen + timer capture, shelf, drag-out, Vision OCR, archive window, pairing, peer browse/pull/push. **No recording.** |
| **Distribution** | Developer ID signed, notarized, stapled DMG; Sparkle 2 self-update |
| **Sandbox** | No App Sandbox. Hardened runtime, yes. |
| **Archive** | `~/esgee/yyyy/MM/`, same SQLite/FTS5 schema, byte-identical `index.db` layout |
| **Protocol** | [docs/PROTOCOL.md](PROTOCOL.md), unchanged from Windows |

Recording is deferred deliberately. It is the largest chunk of the Windows
app (ffmpeg bootstrap, gdigrab pipeline, graceful-`q` stop, GIF palette pass,
the excluded-from-capture pill) and it is secondary to the screenshot loop
that the app is loved for. Deferring it gets the mesh cross-platform months
sooner. The protocol already carries `kind: "video"` and the `record`
capability, so a Mac with no recorder still browses, pulls, and drags out
recordings made on Windows — it just can't make one.

## Why native, restated as a constraint

DIRECTION says native is "judged necessary." The specific things a
cross-platform toolkit would cost:

- **Drag-out fidelity.** The whole app exists for this. `NSDraggingSource`
  with multiple pasteboard representations and asynchronous file promises is
  not something a wrapper reproduces faithfully.
- **Window behavior.** The shelf must be a non-activating panel that floats
  over full-screen apps on every Space and never steals focus. That is four
  specific AppKit properties and no abstraction layer exposes them.
- **Capture latency.** ScreenCaptureKit direct, no marshalling.
- **Binary size and cold start.** The Windows app's identity is that it's
  instant. An Electron shell forfeits that on arrival.

## Minimum macOS

**macOS 14 Sonoma.** Reasons, in order of weight:

- `SCScreenshotManager.captureImage` (14.0) is the supported one-shot capture
  path. The `CGWindowListCreateImage` / `CGDisplayCreateImage` family it
  replaces is deprecated as of 14 and increasingly hostile.
- Vision text recognition revision 3.
- Swift 6 language mode and modern concurrency without back-deployment
  contortions.

Verify what the target MacBook actually runs before committing; if it's newer,
raising the floor only removes work.

## Module map

Deliberately mirrors `src/Esgee/` so the two codebases stay readable side by
side and NOTES.md's architecture rule ("capture sources feed one pipeline")
transfers verbatim.

```
Capture/   HotkeyManager (Carbon RegisterEventHotKey)
           OverlayController + OverlayWindow (frozen-frame region select,
             one window per NSScreen)
           CaptureController — the single pipeline: save → shelf →
             pasteboard → index
           CountdownWindow (timed capture)
           ClipboardWatcher (NSPasteboard changeCount poll)
Store/     ShotStore — thin wrapper over system libsqlite3, identical schema
Ocr/       OcrIndexer — Vision, background queue, backlog sweep on launch
Peers/     PeerServer (NWListener), PeerClient, Pairing, SyncQueue,
           Tailnet (address discovery), Protocol (Codable DTOs)
Ui/        ShelfPanel, ShotCardView, ArchiveWindow, Theme
App/       AppDelegate, MenuBarController, Settings, Log, Updater (Sparkle)
```

## Capture

### Region select

Same model as Windows and for the same reason: **grab the frame first, then
show the overlay over the frozen image.** Selecting a region over live,
animating content is miserable; freezing makes the selection exact.

1. `SCShareableContent.excludingDesktopWindows(false, onScreenWindowsOnly: true)`
2. `SCScreenshotManager.captureImage` per display, at backing scale
3. One borderless `NSWindow` per `NSScreen`, `level = .screenSaver` (above
   the menu bar and Dock), frozen image as the layer contents, crosshair
   cursor, dimmed outside the selection
4. Escape or right-click cancels; drag commits; a bare click commits the
   whole display

**Multi-display is in scope for v1.** It's a known gap on Windows (the shelf
and overlay anchor to the primary work area). Doing it correctly on Mac is
cheap because `NSScreen` enumeration and per-screen windows are the natural
shape, and retrofitting it later is not cheap. Selection may cross displays;
the committed rect is composited from the per-display frames.

Mixed backing scale factors are the sharp edge: capture at each display's
native scale, composite in points, and write the PNG at the *highest* scale
involved so a Retina region never gets downsampled by a 1x neighbour.

### Screen Recording permission (TCC)

ScreenCaptureKit requires the Screen Recording permission. This is the single
worst first-run experience on macOS and deserves explicit handling:

- Call `CGPreflightScreenCaptureAccess()` at launch. If not granted, the
  menu bar shows an unmistakable "esgee can't see your screen" state rather
  than failing silently on the first hotkey press.
- `CGRequestScreenCaptureAccess()` triggers the prompt once. macOS will not
  re-prompt after a denial — the fallback is a button that opens
  `x-apple.systempreferences:com.apple.preference.security?Privacy_ScreenCapture`.
- **This is the payoff for code signing.** The permission is bound to the
  binary's code signature. A signed, notarized app keeps its grant across
  every Sparkle update. An unsigned or ad-hoc-signed one is treated as a new
  application on each update and re-prompts forever.

### Hotkeys

Registered with Carbon `RegisterEventHotKey` — it is still the correct API,
it is system-wide, and crucially it needs **no Accessibility permission**.
`NSEvent.addGlobalMonitorForEvents` would require one, which is a second TCC
prompt for no benefit.

The system owns ⌘⇧3/4/5/6, so those are off the table. Proposal — **⌃⇧
(Control-Shift) + the same letters the Windows app uses**, because
`Ctrl+Shift+S` already means "region capture" in esgee on Windows and muscle
memory should transfer:

| Action | macOS | Windows |
|---|---|---|
| Region select | ⌃⇧S | Win+Shift+C *(and Ctrl+Shift+S)* |
| Fullscreen | ⌃⇧F | Win+Shift+F |
| Repeat last region | ⌃⇧L | Win+Shift+L |
| Timed capture | ⌃⇧D | Win+Shift+D |
| Open archive | ⌃⇧A | *(tray)* |

⌃⇧+letter chords are rarely claimed globally, and a `RegisterEventHotKey`
registration intercepts ahead of the focused app regardless. There is no
PrintScreen key to fight over — the entire Windows PrintScreen-routing quirk
simply does not exist here. Every chord stays user-configurable; a failed
registration logs and does not abort startup.

## Shelf and drag-out

The shelf is an `NSPanel`:

```swift
styleMask   = [.nonactivatingPanel, .borderless]
level       = .floating
collectionBehavior = [.canJoinAllSpaces, .fullScreenAuxiliary, .stationary]
isFloatingPanel = true
hidesOnDeactivate = false
```

`.nonactivatingPanel` + `.canJoinAllSpaces` + `.fullScreenAuxiliary` is the
combination that makes the shelf appear over a full-screen app without
yanking you out of it. Mac users live in full-screen apps far more than
Windows users do, so this is not a nicety — a shelf that forces a Space
switch is a broken shelf.

Cards carry the same hover chrome as Windows (copy / reveal in Finder / pin /
dismiss), the same linger timer, the same max-card push-off.

### Drag-out — better than the Windows implementation

Windows must hand `CF_HDROP` a real file on disk, which is why remote peer
captures are materialized into a `peercache` and prefetched on mouse-down.
AppKit does not have that constraint:

- **Local capture** → `NSPasteboardItem` carrying the `fileURL`, plus PNG and
  TIFF representations so drop targets that want image bytes (web textareas,
  some editors) get them without a temp file round trip. The Windows version
  offers one representation; offering several is strictly better.
- **Remote peer capture** → `NSFilePromiseProvider`. The promise resolves
  asynchronously *after* the drop, streaming from the peer straight to the
  destination. A cold drag from a peer works with no prefetch and no stall.

That last point is worth stating plainly: the peercache prefetch-on-mousedown
machinery is a Windows workaround, not a design. On Mac it is optional, and
the cache exists only to avoid re-downloading, not to make the drag possible.

## OCR

Vision's `VNRecognizeTextRequest`, `.accurate`, `usesLanguageCorrection =
true`, on a background queue with the same shape as the Windows indexer: run
once at capture time, write into FTS5, sweep the backlog at launch, mark
failures done so a bad file cannot wedge the queue.

Engine version string: `vision/<revision>+<osbuild>`, e.g.
`vision/3+25A354` — same honest-proxy convention as `winocr/10.0.26200.0`,
where the OS build stands in for an engine that doesn't version itself.

Two consequences worth naming:

- Vision is materially more accurate than `Windows.Media.Ocr`, especially on
  small UI text. Because the protocol imports OCR text rather than re-running
  it, a capture taken on the Mac keeps Vision's text when it lands on a
  Windows box. The mesh gets the better engine's output wherever the capture
  originated.
- That asymmetry makes selective re-OCR (re-run only rows whose
  `ocr_engine_version` is older/worse) genuinely useful rather than
  theoretical. Not v1 — but the column is already there for it, which is why
  it was added.

## Store

Identical schema, identical file layout, no translation layer. A `~/esgee`
tree and a `%USERPROFILE%\esgee` tree are the same artifact. Same `yyyy/MM`
partitioning, same `yyyy-MM-dd_HH-mm-ss.png` naming, same `index.db` with
`shots` + `shots_fts` + `sync_pushed` and the same additive-migration
discipline (`ALTER TABLE ADD COLUMN`, ignore "duplicate column name").

Access via a thin wrapper over the system `libsqlite3`. No GRDB, no ORM — the
query surface is roughly a dozen statements, and the Windows implementation's
"eight fixed routes don't need a framework" instinct applies equally here.
`FtsQuery` (quote each term, append `*`) must be ported exactly so a search
means the same thing on both platforms and over the wire.

Two things to confirm early: that the system SQLite build has FTS5 compiled
in (it should), and that WAL files written by one platform read cleanly on
the other if an archive is ever moved.

**`~/esgee`, not `~/Pictures`** — same reasoning as Windows, only more so:
Pictures is a Photos-library and iCloud-sync target, and thousands of PNGs a
day through iCloud is its own outage.

## Peer layer

Same eight routes, same security model, same hand-rolled responder rationale.

**Server**: `NWListener` from Network.framework bound to the Tailscale
address. Hand-rolled HTTP/1.1 parsing for the same reason as Windows — eight
fixed routes serving one trusted client, and pulling in SwiftNIO to serve
them is the Kestrel mistake in a different language.

**Tailnet address discovery** should be done better than on Windows. Today the
C# side shells out to `tailscale ip -4`. On macOS that CLI is a moving target:
the standalone build puts it at `/usr/local/bin/tailscale`, the Mac App Store
build buries it inside `/Applications/Tailscale.app/Contents/MacOS/` and needs
a user-created symlink. Instead:

- **Primary**: `getifaddrs()`, take the first IPv4 in `100.64.0.0/10`. Tailscale
  addresses always live in that CGNAT range. No CLI, no path guessing, no
  subprocess, works with either Tailscale distribution.
- **Fallback, for *discovery* only**: `tailscale status --json` to enumerate
  peer nodes, since interface scanning can find *our* address but not the
  fleet. Manual `Peers` settings entries remain the last resort, as today.

This is worth backporting to Windows — same range, same `GetAdaptersAddresses`
one-liner, and it removes a subprocess from startup.

**Client, pairing, sync**: direct ports. The Mac shows the same 6-digit PIN
window, discovers the same candidates, and adopts the token the same way.
Pairing a Mac to a Windows box must work in both directions on day one — it
is the proof that the mesh is genuinely cross-platform.

## Distribution

| Concern | Windows | macOS |
|---|---|---|
| Installer | Velopack `Setup.exe` | Notarized, stapled DMG |
| Updates | Velopack, GitHub Releases feed | Sparkle 2, `appcast.xml` on the Pages site |
| Signing | none (SmartScreen once) | Developer ID Application + hardened runtime |
| Update signing | — | Sparkle EdDSA key pair |

CI: a `mac.yml` workflow on a `macos` runner, triggered by the same `v*` tag
as the Windows release so both platforms ship one version number.

- Import the Developer ID cert from a base64 `.p12` secret into a temporary
  keychain; delete the keychain in an `always()` step.
- Notarize with `notarytool` using an **App Store Connect API key**, not an
  Apple ID and app-specific password — the key is revocable, doesn't carry a
  human account, and doesn't break when 2FA state changes.
- `stapler staple` the DMG before upload so first launch works offline.
- Sign the appcast with the Sparkle EdDSA private key from secrets.

Version comes from the tag, exactly as on Windows. Local builds report `0.0.0`.

## Repo layout

Same repository. The protocol doc is the shared spine and splitting the repo
would immediately let the two implementations drift.

```
src/Esgee/        the Windows app (unchanged)
mac/project.yml   XcodeGen spec — checked in
mac/Sources/      Swift sources
mac/Resources/    Info.plist, icons, entitlements
docs/PROTOCOL.md  normative for both
```

XcodeGen rather than a checked-in `.xcodeproj`: the project file is generated
from a small YAML spec, so it produces readable diffs, never conflicts, and
an agent can regenerate it headlessly. `.xcodeproj` goes in `.gitignore`.

## Deliberate non-goals for v1

- Screen recording (see above)
- Annotation/markup — that arrives with shares, on both platforms at once
- iOS/iPadOS anything
- Mac App Store distribution (the sandbox fights the archive and the peer
  server for no gain on a tool you install once)

## Open items to settle before code

1. macOS version on the target MacBook — confirms or raises the floor.
2. Apple Developer Program enrollment done, Team ID in hand, and the
   certificate + API key loaded into repo secrets. This blocks CI, not
   development.
3. Whether the Windows `LingerSeconds` / `MaxCards` defaults feel right on a
   laptop screen, or whether the Mac shelf wants different numbers.
4. Whether ⌃⇧S survives contact with the apps actually in use, or whether
   ⌥⇧ chords read better on Mac.
5. Confirm FTS5 in the system SQLite on the target OS version.
