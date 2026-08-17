# Running esgee on blackbook — first build

Written overnight 2026-08-17. The Mac app under `mac/` is a complete
implementation of [docs/MAC.md](MAC.md) — 30 Swift files (all under
`mac/Sources/`; `git ls-files 'mac/*.swift' | wc -l` to re-check) written
against `mac/SPEC.md` and cross-checked by an integration pass, but **it has
not been compiled yet** (no Mac was reachable from the build machine overnight; SSH to
blackbook was denied all night). Expect a short first-build fix round, not a
clean first try. The known-risk list is at the bottom — check it before
head-scratching.

## Build it in Xcode

`mac/esgee.xcodeproj` is checked in — a fresh clone opens in Xcode with no
tooling installed. The normal flow, GitHub Desktop or CLI alike:

1. Clone the repo, switch to the branch (`overnight/mesh-and-mac` until it
   merges).
2. Open **`mac/esgee.xcodeproj`** — the project file is inside `mac/`, not
   the repo root.
3. Once, on a new machine: Signing & Capabilities → Team → your team
   (automatic signing).
4. **⌘R**. Approve the **Local Network** and **Screen Recording** prompts;
   after granting Screen Recording, quit and run once more (macOS applies it
   only to new processes). Signed with a real team identity, both grants
   persist across rebuilds.

The project stays *generated*: `mac/project.yml` is the source of truth.
Editing targets/settings means editing project.yml, running
`xcodegen generate` in `mac/`, and committing both — never hand-editing the
pbxproj. Day-to-day Swift work needs none of that; adding/removing source
files doesn't either (the target globs `Sources/`, and regenerating picks
them up).

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
