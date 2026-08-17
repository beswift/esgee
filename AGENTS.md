# AGENTS.md

Guidance for coding agents working in this repo — and for agents that just
want to *use* esgee as a tool.

## Using esgee from an agent (no code changes)

esgee maintains a local, OCR-indexed archive of every screenshot the user
takes. Query it headlessly:

```
esgee --search "<words that were on screen>"   # matching file paths, newest-ranked
esgee --recent <n>                             # newest n captures
```

Output is one file path per line — feed the paths straight into image-reading
tools. Captures are plain PNG/MP4/GIF under `%USERPROFILE%\esgee\yyyy\MM\`.
The index lives beside them (`index.db`, SQLite/FTS5, WAL — safe to read while
the app runs).

Every verb accepts `--archive-root <path>` to target a different archive.
With peers enabled (see README "Peers & sync"), other machines' archives are
one authenticated HTTP hop away: `GET http://<tailscale-ip>:43117/search?q=…`
with the `X-Esgee-Token` header from settings.json.

## Working on the codebase

- **Stack:** C# / .NET 10, three projects since the Core/Node split
  (docs/SHARES.md "The node is a Linux binary"):
  - `src/Esgee.Core/` — everything desktop-free: `Store/`, `Peers/`,
    `Shares/`, `Settings`, `Log`, the headless CLI verbs. **Portable code
    goes here**, never in the WPF project — the node can't reference
    anything under `src/Esgee/`.
  - `src/Esgee/` — the WPF app (capture, shelf, archive UI, OCR, tray);
    references Core.
  - `src/Esgee.Node/` — `esgee-node`, the headless Linux-capable peer/share
    server; references Core.

  Build **both** `dotnet build src/Esgee/Esgee.csproj` and
  `dotnet build src/Esgee.Node/Esgee.Node.csproj` — a Core change that
  compiles for the app can still break the node. The SDK must be .NET 10+.
  There is also an uncompiled Swift Mac app under `mac/` (docs/MAC.md);
  dotnet builds never touch it.
- **The running app locks its binaries.** If an installed copy is running,
  builds that target its folder fail — stop the `esgee` process first.
  Never hand-publish over `%LOCALAPPDATA%\esgee\current\` — that directory
  belongs to the Velopack updater.
- **Do not add `InvariantGlobalization`** to the csproj. It crashes WPF's font
  cache at render time. The csproj comment explains; this has bitten before.
- **Architecture rule:** capture sources feed one pipeline
  (save → shelf → clipboard → index). Add features as new capture sources or
  new card affordances; don't create side channels around the pipeline.
- **Clipboard code is subtle.** One `SetDataObject` raises several
  `WM_CLIPBOARDUPDATE`s; the watcher's self-echo guard is a time window on
  purpose. Don't "simplify" it to a consume-once flag.
- **UI thread discipline:** never decode images or do I/O on the dispatcher
  thread. Thumbnails decode on `Task.Run` with `StreamSource` + `Freeze()`.
- **Verification bar:** "it compiles" is not done. Drive real behavior —
  global hotkeys can be exercised with injected input (`keybd_event` /
  `mouse_event` from a `powershell -STA` script) and confirmed via
  `%LOCALAPPDATA%\esgee\esgee.log`, which logs every capture path
  explicitly. Beware: a capture appearing in the archive does not prove
  *your* code path ran — the clipboard watcher catches captures from any
  tool. Check for the specific log lines.
- **Peer layer testing needs no second machine.** The self-peer loopback is
  supported: enable peers, and this machine appears in its own archive
  switcher. `esgee --serve --archive-root <dir> --port <p>` runs a headless
  receiver on a scratch archive; `esgee --check-peer` proves the remote-drag
  path end to end (ping → recent → cache download → CF_HDROP round-trip).
  Peer traffic logs as `peers:` (server side) and `peer <name>:` (client
  side) in esgee.log — assert on those lines, not on files appearing.
- **Releases:** push a `v*` tag; `.github/workflows/release.yml` builds,
  packages (Velopack `vpk`), and publishes. Version comes from the tag —
  don't hardcode versions in the csproj (local builds intentionally report
  0.0.0).
- **More context:** [docs/NOTES.md](docs/NOTES.md) records the design
  rationale and Windows quirks (PrintScreen shell routing, hotkey collisions,
  WDA_EXCLUDEFROMCAPTURE behavior in screenshots-of-screenshots).
