# Running esgee on blackbook — first build

Written overnight 2026-08-17. The Mac app under `mac/` is a complete
implementation of [docs/MAC.md](MAC.md) — 33 Swift files written against
`mac/SPEC.md` and cross-checked by an integration pass, but **it has not been
compiled yet** (no Mac was reachable from the build machine overnight; SSH to
blackbook was denied all night). Expect a short first-build fix round, not a
clean first try. The known-risk list is at the bottom — check it before
head-scratching.

## Build it in Xcode (the path for this morning)

On blackbook:

```bash
# 1. Get the branch
git clone <your repo remote or a copy> esgee && cd esgee
git checkout overnight/mesh-and-mac

# 2. XcodeGen generates the project (project.yml is the source of truth;
#    *.xcodeproj is gitignored)
brew install xcodegen        # once
cd mac && xcodegen generate

# 3. Open and run
open esgee.xcodeproj
```

In Xcode: select the `esgee` scheme → your Team under Signing & Capabilities
(automatic signing is fine for local runs; the Developer ID / notarization
path is CI's job later) → **⌘R**.

Two ways to get the branch onto blackbook:

- **Push it** (from alphalfa): `git push origin overnight/mesh-and-mac`, then
  clone/fetch from GitHub as usual. (Not pushed overnight on purpose —
  nothing leaves the machines without your say-so.)
- **Or pull the staged bundle** — a `git bundle` of the branch is already
  sitting on minimax, refreshed at the end of the overnight run:

  ```bash
  scp ben@minimax:~/esgee-node/esgee-overnight.bundle /tmp/
  git clone /tmp/esgee-overnight.bundle esgee && cd esgee
  git checkout overnight/mesh-and-mac
  ```

## First-run expectations

- **Menu bar app** — no Dock icon (`LSUIElement`). Look for the
  camera-viewfinder icon in the menu bar.
- **Screen Recording permission**: the icon shows a warning state until
  granted. The menu's repair link opens the right Settings pane. After
  granting, relaunch (macOS only applies it to new processes).
- Hotkeys: **⌃⇧S** region, **⌃⇧F** fullscreen, **⌃⇧L** last region, **⌃⇧D**
  timed. Region select shows the frozen-frame overlay; drag, release, card
  lands on the shelf.
- Archive: `~/esgee/yyyy/MM/` + `index.db`, same shapes as Windows.
- **Pairing test (the point of all this)**: menu → Peers → "Pair with another
  machine…" while a Windows box shows a PIN (tray → "Pair a new machine…").
  Both directions should work. After pairing, the Windows machines appear in
  the Mac archive window's machine switcher and vice versa.

## If Sparkle blocks the build

`project.yml` declares Sparkle 2 via SPM; the first `xcodegen`/build needs
network to resolve it. If it fails or you want it gone for the first run:
delete the package entry in `project.yml`, `import Sparkle` +  the `updater`
property + the "Check for updates" menu item in
`mac/Sources/App/MenuBarController.swift`, regenerate. Nothing else touches it.

## Known first-build risks (from the integration pass)

Ranked most-likely-first; all are localized:

1. **Sparkle SPM resolution** — see above; trivially removable.
2. **NWConnection sendability** (`Peers/PeerServer.swift` worker closures) —
   needs the macOS 14+ SDK in Xcode 15/16+; older SDKs error there.
3. **NSImage returned from `Task.detached`** (`Ui/ArchiveWindow.swift`
   preview/tile decode) — relies on Swift 6 region isolation; the project
   pins `SWIFT_VERSION: 6.0`, don't lower it to 5.x.
4. **`@preconcurrency` protocol conformances** (ShotCardView,
   ArchiveGridView, PairJoinWindow, MenuBarController) — at worst a
   "redundant" warning, not an error, on either SDK annotation state.
5. **Carbon hotkey callback** (`Capture/HotkeyManager.swift`) — the C
   callback closure must stay capture-free; if it ever errors, the fix is
   moving state into the `userData` pointer, not capturing.
6. If `CGImageSourceCopyPropertiesAtIndex ... as? [CFString: Any]` balks
   (`Peers/PeerServer.swift` thumb path), insert `as NSDictionary?` first.

Report whatever the first build actually says — the error list, verbatim, is
the fastest way for the next session to finish this.

## What already works without the Mac

Verified live overnight, Windows (alphalfa) ↔ Linux (minimax):

- `esgee-node` (the new WPF-free Linux binary from tonight's
  `Esgee.Core`/`Esgee.Node` split) running on minimax as a systemd user
  service, bound to its tailnet IP only, token-gated (`--token-file`).
- Cross-machine: `/ping` (proto 2 + capabilities), `/ingest` with sha-dedupe
  retry, FTS `/search` over sidecar OCR text, ImageSharp `/thumb`, and a
  byte-identical `/file` roundtrip.

So the Mac app is joining a mesh that is already provably cross-platform.
