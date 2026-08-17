# mac/SPEC.md — the module contract

This file is the single source of truth for every type that crosses a module
boundary in the macOS app. Seven module agents work in parallel and **cannot
see each other's code**; what they CAN see is this file, the architect-owned
files already on disk, `docs/MAC.md` (design), `docs/PROTOCOL.md` (wire
contract, normative), and the C# reference under `src/Esgee/`.

**Conform to the signatures below exactly** — names, labels, argument order,
`throws`, actor isolation. If you need something from another module that is
not in this contract, you are holding the problem wrong: re-read your module's
section. Everything compiles into one app target, so "public" below means
*internal visibility, contract-stable*.

## Ground rules

- Swift 6 language mode, macOS 14+, AppKit shell, SwiftUI **only** inside the
  archive grid. No external packages (Sparkle is already declared in
  `project.yml` and is the integrator's concern).
- There is no compiler tonight. Write code that must compile first try:
  well-known APIs, no macros except what SwiftUI itself requires, prefer
  `ObservableObject`/`@Published` over `@Observable`, no exotic generics,
  every file complete — no TODOs, no stubs except items listed under
  **Deferred** at the bottom.
- Comment discipline matches the C# sources: comments state **constraints and
  reasons**, not mechanics. Every workaround names the failure it prevents.
- Errors: log and degrade, never crash. A menu-bar app survives a bad capture.
  `Log.info/warn/error` from any thread.
- Log lines that exist on Windows keep the same wording here (the log is
  designed to read identically on both platforms). The parity list is in
  **Cross-cutting rules**.

## File ownership

Architect-owned, already written — **do not edit, do not duplicate**:

```
mac/project.yml
mac/Sources/Core/Protocol.swift    PeerProtocol, PeerCapability, PingDto, ShotDto,
                                   IngestMeta, IngestResult, PairRequest, PairResult, ErrorDto
mac/Sources/Core/Shot.swift        PixelSize, Shot, IsoStamp
mac/Sources/App/main.swift
mac/Sources/App/AppDelegate.swift  AppDelegate, AppInfo (wiring; placeholder menu)
mac/Sources/App/Settings.swift     Settings, SettingsStore
mac/Sources/App/Log.swift          Log
```

Module-owned. Create **exactly** these files, nothing else, and touch no file
outside your list:

| Module | Files (under `mac/Sources/`) | Types owned |
|---|---|---|
| **Store** | `Store/ShotStore.swift`, `Store/Sqlite.swift` | `ShotStore` (+ private sqlite helpers) |
| **Ocr** | `Ocr/OcrIndexer.swift` | `OcrIndexer` |
| **PeersServer** | `Peers/PeerServer.swift`, `Peers/HttpParts.swift`, `Peers/Pairing.swift`, `Peers/PairHostWindow.swift` | `PeerServer`, `HttpRequest`, `MultipartPart` (internal), `PairingSession`, `PairAttemptResult`, `PairHostWindowController` |
| **PeersClient** | `Peers/PeerClient.swift`, `Peers/Tailnet.swift`, `Peers/SyncQueue.swift`, `Peers/PairJoinWindow.swift` | `PeerInfo`, `PeerClient`, `Tailnet`, `TailnetNode`, `SyncQueue`, `PairJoinWindowController` |
| **Capture** | `Capture/HotkeyManager.swift`, `Capture/ScreenGrabber.swift`, `Capture/OverlayController.swift`, `Capture/CountdownPill.swift`, `Capture/ClipboardWatcher.swift`, `Capture/CaptureController.swift` | `HotkeyAction`, `Chord`, `HotkeyManager`, `FrozenDisplay`, `ScreenGrabber`, `OverlayController`, `CountdownPill`, `ClipboardWatcher`, `CaptureController` |
| **ShelfUI** | `Ui/ShelfPanel.swift`, `Ui/ShotCardView.swift`, `Ui/ShotPasteboard.swift`, `Ui/Theme.swift` | `ShelfPanelController`, `ShotCardView`, `ShotPasteboard`, `Theme` |
| **ArchiveUI** | `Ui/ArchiveWindowController.swift`, `Ui/ArchiveModel.swift`, `Ui/ArchiveView.swift`, `Ui/ArchiveDragHost.swift` | `ArchiveWindowController`, `ArchiveModel`, SwiftUI views, `ArchiveDragHost` |

Reserved for the integrator pass — **do not create**: `App/MenuBarController.swift`,
`App/Updater.swift`.

## Threading model

- `@MainActor` (all AppKit/SwiftUI): `HotkeyManager`, `OverlayController`,
  `CountdownPill`, `ClipboardWatcher`, `CaptureController` (except its
  `nonisolated func save`), `ShelfPanelController`, `ShotCardView`,
  `ShotPasteboard`, `ArchiveWindowController`, `ArchiveModel`,
  `PairHostWindowController`, `PairJoinWindowController`, `SettingsStore`,
  `AppDelegate`. A `@MainActor` class is implicitly `Sendable`; that is how
  references cross into detached tasks legally.
- Any-thread, internally synchronized (`final class … : @unchecked Sendable`
  with an `NSLock` or private serial `DispatchQueue`): `ShotStore`,
  `OcrIndexer`, `SyncQueue`, `PeerServer`, `PairingSession`. `PeerClient` is
  `Sendable` with `async` methods and no shared mutable state beyond the
  URLSession.
- Blocking work never runs on the main actor: `Tailnet.nodes()`,
  `PeerClient.candidatePeers`, `PeerServer.tryStart`, `ShotStore.add/ingest`
  (disk + hash). `ShotStore` *reads* (`recent`, `search`, `byId`,
  `changeToken`, `ocrState`, `count`) are millisecond-fast and MAY be called
  from the main actor, though `ArchiveModel` still hops off for page loads.
- Callback fields (`onSucceeded`, `onStateChanged`, …) fire on **arbitrary**
  threads unless the signature says `@MainActor`. Subscribers hop themselves.
- `Settings` is a value type. The canonical copy lives in `SettingsStore` on
  the main actor. Background components receive a `Settings` **snapshot** at
  init and are rebuilt when the fields they were built from change (that is
  AppDelegate's job, already wired).

## Architect-owned surfaces (recap — already on disk)

```swift
// Core/Protocol.swift
enum PeerProtocol {
    static let proto = 2
    static let tokenHeader = "X-Esgee-Token"
    static func makeEncoder() -> JSONEncoder
    static func makeDecoder() -> JSONDecoder
}
enum PeerCapability {
    static let peer = "peer"; static let share = "share"
    static let annotate = "annotate"; static let record = "record"
    static let advertised: [String]        // == [peer] for Mac v1
}
struct PingDto: Codable, Sendable {        // + effectiveCapabilities (assumes ["peer"] when absent)
    let app: String; let version: String; let proto: Int
    let machine: String; let captures: Int64; let capabilities: [String]?
}
struct ShotDto: Codable, Sendable, Identifiable {
    let id: Int64; let fileName: String; let takenAt: String
    let width: Int; let height: Int; let sha256: String
    let kind: String; let durationMs: Int64; let origin: String
    let hasGif: Bool
    var ocrText: String? = nil; var ocrEngineVersion: String? = nil
}
struct IngestMeta: Codable, Sendable {
    let sha256: String; let takenAt: String; let width: Int; let height: Int
    let kind: String; let durationMs: Int64
    var ocrText: String? = nil; var ocrEngineVersion: String? = nil
    var origin: String? = nil; var fileName: String? = nil
}
struct IngestResult: Codable, Sendable { let id: Int64; let duplicate: Bool }
struct PairRequest: Codable, Sendable { let pin: String; let machine: String }
struct PairResult: Codable, Sendable { let token: String; let machine: String }
struct ErrorDto: Codable, Sendable { let error: String }

// Core/Shot.swift
struct PixelSize: Sendable, Equatable { let width: Int; let height: Int }
struct Shot: Sendable, Equatable, Identifiable {
    let id: Int64; let path: String; let takenAt: Date; let takenAtRaw: String
    let width: Int; let height: Int; let sha256: String
    let kind: String; let durationMs: Int64; let origin: String
    init(id:path:takenAt:takenAtRaw:width:height:sha256:kind:durationMs:origin:)
        // kind defaults "image", durationMs 0, origin ""
    var fileName: String; var isVideo: Bool
    var thumbPath: String                  // video → path + ".png"
    var gifPath: String?                   // checked live on disk
    var durationText: String               // "m:ss" / "h:mm:ss"
}
enum IsoStamp {
    static func format(_ date: Date) -> String   // mint raw taken_at, local offset, 7-digit fraction
    static func parse(_ s: String) -> Date?
    static func fileStem(_ date: Date) -> String // "yyyy-MM-dd_HH-mm-ss" local
    static func yearFolder(_ date: Date) -> String
    static func monthFolder(_ date: Date) -> String
}

// App/Settings.swift
struct Settings: Codable, Sendable {
    var archiveRoot: String; var lingerSeconds: Int; var maxCards: Int; var ocrEnabled: Bool
    var regionHotkey: String; var fullscreenHotkey: String; var lastRegionHotkey: String
    var timerHotkey: String; var archiveHotkey: String; var timerSeconds: Int
    var lastRegion: [Int]?                 // Cocoa global points [x, y, w, h]
    var peersEnabled: Bool; var peerPort: Int; var peerToken: String
    var peers: [String]; var syncTargetPeer: String
    static let fileURL: URL
    static func load() -> Settings
    func save()
}
@MainActor final class SettingsStore {
    private(set) var current: Settings
    init(_ loaded: Settings)
    func update(_ mutate: (inout Settings) -> Void)   // mutates AND saves
}

// App/Log.swift
enum Log {
    static func info(_ msg: String); static func warn(_ msg: String); static func error(_ msg: String)
    static let fileURL: URL
}

// App/AppDelegate.swift
enum AppInfo { static let version: String; static let machineName: String }
```

The **taken_at round-trip rule** (applies to Store, PeersServer, PeersClient):
a raw taken_at string is minted exactly once, by the machine that captured,
via `IsoStamp.format`. The DB column and every wire field carry that string
verbatim forever. `Date` values are derived for display/sorting only. If you
find yourself calling `IsoStamp.format` on a parsed remote date, stop — pass
the raw string through instead.

---

## Module: Store

`ShotStore` is a thin wrapper over the **system libsqlite3** (`import SQLite3`
— no GRDB, no ORM; eight fixed routes don't need a framework). Identical
schema and file layout to Windows: a `~/esgee` tree and a
`%USERPROFILE%\esgee` tree are the same artifact.

```swift
final class ShotStore: @unchecked Sendable {
    let root: URL
    init(root: URL) throws                       // mkdir root, open root/index.db, migrate

    static func ftsQuery(_ raw: String) -> String

    func add(png: Data, size: PixelSize, takenAt: Date) throws -> Shot
    func pendingOcr(limit: Int = 25) -> [Shot]
    func setOcr(id: Int64, text: String, engineVersion: String = "")
    func search(matching ftsQuery: String, limit: Int = 100) throws -> [Shot]
    func recent(limit: Int = 100) -> [Shot]
    func byId(_ id: Int64) -> Shot?
    func count() -> Int64
    func ocrState(id: Int64) -> (done: Bool, text: String?, engineVersion: String)
    func ingest(path: String, sha256: String, takenAtRaw: String,
                width: Int, height: Int, kind: String, durationMs: Int64,
                ocrText: String?, ocrEngineVersion: String, origin: String)
        -> (shot: Shot, duplicate: Bool)
    func planIngestPath(takenAt: Date, ext: String) -> String   // ext includes the dot
    func notPushed(target: String, targetMachine: String, limit: Int = 500) -> [Shot]
    func markPushed(shotId: Int64, target: String)
    func changeToken() -> String
    func close()
}
```

Rules, ported from `ShotStore.cs` — treat that file as the executable spec:

- One `NSLock` gates every statement (captures arrive from a detached task
  while OCR writes from its pump). Prepared statements are made per call.
- Bind text with `SQLITE_TRANSIENT`:
  `let SQLITE_TRANSIENT = unsafeBitCast(-1, to: sqlite3_destructor_type.self)`
  — binding Swift strings with `nil` destructors is the classic
  use-after-free; do not skip this.
- Schema, executed verbatim on open (then `PRAGMA journal_mode=WAL;`):

```sql
CREATE TABLE IF NOT EXISTS shots (
    id       INTEGER PRIMARY KEY AUTOINCREMENT,
    path     TEXT NOT NULL,
    taken_at TEXT NOT NULL,
    width    INTEGER NOT NULL,
    height   INTEGER NOT NULL,
    sha256   TEXT NOT NULL,
    ocr_text TEXT,
    ocr_done INTEGER NOT NULL DEFAULT 0
);
CREATE INDEX IF NOT EXISTS ix_shots_taken_at ON shots(taken_at DESC);
CREATE INDEX IF NOT EXISTS ix_shots_sha      ON shots(sha256);
CREATE INDEX IF NOT EXISTS ix_shots_ocr_todo ON shots(ocr_done) WHERE ocr_done = 0;
CREATE VIRTUAL TABLE IF NOT EXISTS shots_fts
    USING fts5(ocr_text, content='shots', content_rowid='id');
```

- Additive migrations after that, each with errors swallowed ("duplicate
  column name" is the idempotence):
  `ALTER TABLE shots ADD COLUMN kind TEXT NOT NULL DEFAULT 'image'`,
  `… duration_ms INTEGER NOT NULL DEFAULT 0`,
  `… origin TEXT NOT NULL DEFAULT ''`,
  `… ocr_engine_version TEXT NOT NULL DEFAULT ''`, and
  `CREATE TABLE IF NOT EXISTS sync_pushed (shot_id INTEGER NOT NULL, target
  TEXT NOT NULL, pushed_at TEXT NOT NULL, PRIMARY KEY (shot_id, target))`.
- `ftsQuery`: split on single spaces, drop empties, each term becomes
  `"term"*` with embedded `"` doubled, join with spaces. Byte-identical to
  the C# port — a search must mean the same thing locally, remotely, and on
  the wire.
- `add`: sha256 of the PNG as **uppercase hex** (CryptoKit `SHA256`);
  identical bytes within a 10-second window return the EXISTING shot and log
  `deduplicated identical capture (echo of shot N)`. File goes to
  `root/yyyy/MM/yyyy-MM-dd_HH-mm-ss.png` (via `IsoStamp`), `_2`, `_3`…
  suffixes on collision. Write the file BEFORE the row — drag-out hands
  paths to the OS. Mint `takenAtRaw = IsoStamp.format(takenAt)` here and
  store it; this is the only place local raws are minted.
- `ingest`: global sha dedupe (newest row with that sha wins, no time
  window). Stores `takenAtRaw` **verbatim**. `ocr_done = 1` iff
  `ocrText != nil || kind != "image"`; non-empty text also inserts into
  `shots_fts(rowid, ocr_text)`. Both writes in one transaction.
- `setOcr`: UPDATE + FTS insert in one transaction (external-content FTS —
  push the row explicitly).
- `changeToken`:
  `SELECT COALESCE(MAX(id),0) || ':' || COUNT(*) || ':' || COALESCE(SUM(ocr_done),0) FROM shots;`
- `notPushed`: excludes rows already in `sync_pushed` for the target AND rows
  whose `origin == targetMachine` (pushing those back would bounce off the
  receiver's dedupe). Oldest first.
- Row reads: `taken_at` text goes into `Shot.takenAtRaw` untouched;
  `Shot.takenAt = IsoStamp.parse(raw) ?? Date(timeIntervalSince1970: 0)`
  (log a warn on parse failure, never drop the row).
- `search` throws on FTS syntax errors (unbalanced quote mid-keystroke);
  every caller catches and shows empty results.

## Module: Ocr

Vision, background pump, same shape as the Windows indexer: run once at
capture time, sweep the backlog at launch, mark failures done so a bad file
cannot wedge the queue.

```swift
final class OcrIndexer: @unchecked Sendable {
    /// "vision/<max supported VNRecognizeTextRequest revision>+<os build>",
    /// e.g. "vision/3+23F79" — the OS build is the honest proxy for an engine
    /// that doesn't version itself (same convention as winocr/10.0.26200.0).
    static let engineVersion: String
    var available: Bool { get }              // Vision is always present; kept for API parity
    init(store: ShotStore)
    func enqueue(_ shot: Shot)               // any thread; ignores isVideo
    func enqueueBacklog()                    // store.pendingOcr(limit: 500)
    func shutdown()                          // stop the pump; best-effort, returns fast
}
```

- Pump: one serial worker (a private serial `DispatchQueue` or a single
  `Task` draining an `AsyncStream<Shot>` — pick one, keep it boring).
- Per item: read file bytes → `VNImageRequestHandler` →
  `VNRecognizeTextRequest` with `.accurate`, `usesLanguageCorrection = true`.
  Text = observations' `topCandidates(1)` strings joined with `"\n"`.
  Success → `store.setOcr(id:text:engineVersion: Self.engineVersion)`.
- Failure → log `ocr failed for <path>: <error>` then
  `store.setOcr(id: shot.id, text: "", engineVersion: "")` — mark it done
  anyway; a file that can't be read won't start working on the next pass,
  and retrying forever would wedge the queue behind it.
- OS build string via `sysctlbyname("kern.osversion", …)`; revision via
  `VNRecognizeTextRequest.supportedRevisions.last ?? 3`.
- Never re-OCR ingested rows: `ingest` already decides `ocr_done`; this
  module only ever sees rows with `ocr_done = 0`.

## Module: PeersServer

The peer API: a deliberately tiny HTTP/1.1 responder on an `NWListener`
(Network.framework), bound ONLY to this machine's Tailscale address. Eight
fixed routes for one trusted client don't need a framework — SwiftNIO here
would be the Kestrel mistake in a different language (docs/MAC.md).

```swift
final class PeerServer: @unchecked Sendable {
    let boundAddress: String                          // "100.x.y.z:43117"
    static func tryStart(store: ShotStore, token: String, port: Int) -> PeerServer?
    func beginPairing(_ session: PairingSession)      // opens POST /pair
    func endPairing(_ session: PairingSession)        // no-op unless same session
    func stop()
}
```

- `tryStart` (blocking, never on main): empty token → warn
  `peers: no PeerToken set; server not started`, return nil.
  `Tailnet.selfIPv4()` nil → warn
  `peers: no Tailscale IPv4 found (is tailscale running?); server not started`,
  return nil. Bind failure → error log, return nil. **Never binds 0.0.0.0.**
  Success logs
  `peers: serving archive on http://<ip>:<port> (tailscale interface only)`.
- NWListener: `NWParameters.tcp` with
  `requiredLocalEndpoint = NWEndpoint.hostPort(host:port:)`; connections
  handled on a private concurrent queue; per-connection 30 s receive /
  60 s send guard timers; `Connection: close` on every response.
- `HttpRequest` parsing mirrors the C# class: accumulate until CRLFCRLF
  (abort past 64 KB of headers), request line `METHOD PATH`, headers
  case-insensitive, body only when `Content-Length` present, cap 1 GB.
  Query parsing: `&`-split, percent-decode, `+` → space in values.
- Auth: every route except `POST /pair` requires
  `X-Esgee-Token` (`PeerProtocol.tokenHeader`). Compare in constant time:
  SHA256 both sides, compare digests. Wrong/missing → 401
  `{"error": "missing or wrong token"}` + warn log with method/path/remote.
- Routes (JSON via `PeerProtocol.makeEncoder()`, snake_case comes from the
  DTOs' CodingKeys):

| Route | Behavior |
|---|---|
| `GET /ping` | `PingDto(app: "esgee", version: AppInfo.version, proto: PeerProtocol.proto, machine: AppInfo.machineName, captures: store.count(), capabilities: PeerCapability.advertised)` |
| `GET /recent?n=` | n clamped 1…1000, default 200; `[ShotDto]` newest first, no ocr fields |
| `GET /search?q=` | empty q → recent(200); else `store.search(matching: ShotStore.ftsQuery(q), limit: 200)`; search errors → empty list, warn |
| `GET /meta/{id}` | 404 `{"error":"no such shot"}` or ShotDto with `ocrText`/`ocrEngineVersion` from `store.ocrState` |
| `GET /thumb/{id}` | JPEG scaled to 448 px wide, quality 0.8, decoded via ImageIO on the worker; 404 `{"error":"no thumbnail"}` when the file is gone; 500 `{"error":"thumbnail failed"}` on encode error |
| `GET /file/{id}` | streams the original; `?alt=gif` → `shot.gifPath`; `?alt=thumb` → video's `thumbPath`; missing → 404 `{"error":"file missing"}`; Content-Type by extension (png/gif/mp4/jpeg else octet-stream) |
| `POST /ingest` | see below |
| `POST /pair` | see below |
| anything else | 404 `{"error":"no such endpoint"}` — never 5xx for routing |

- `ShotDto` from a `Shot`:
  `ShotDto(id:, fileName:, takenAt: shot.takenAtRaw, width:, height:,
  sha256:, kind:, durationMs:, origin:, hasGif: shot.gifPath != nil)`.
- `/ingest`: multipart/form-data with parts `meta` (JSON `IngestMeta`) and
  `file`, optional `gif` and `thumb`. Missing parts → 400 with the same
  messages as Windows. Verify sha256 of file bytes (uppercase hex) equals
  `meta.sha256` case-insensitively → else 400 `{"error":"sha256 mismatch"}`.
  `IsoStamp.parse(meta.takenAt)` failure → 400 `{"error":"bad meta"}`.
  Extension from `meta.fileName`, else ".mp4"/".png" by kind. Write file at
  `store.planIngestPath`, write `gif` beside it (`.gif` extension swap) and
  `thumb` at `dest + ".png"`, then
  `store.ingest(path: dest, sha256: <computed uppercase>, takenAtRaw: meta.takenAt, …,
  origin: meta.origin ?? "")`. On duplicate: delete the fresh file + siblings,
  log `peers: ingest from <remote> deduplicated (sha match, shot N)`.
  Respond 200 `IngestResult` either way — a duplicate is a success.
- Multipart parser: boundary from Content-Type (quoted or bare), scan
  `--boundary` delimiters, part headers end at CRLFCRLF, content ends 2 bytes
  (CRLF) before the next delimiter, `name=` attribute quoted or bare and must
  not match inside `filename=`. Binary-safe throughout ([UInt8] scanning, no
  string conversion of bodies).
- `POST /pair`: response statuses and bodies are normative in
  docs/PROTOCOL.md "Pairing" — clients classify by status AND body. No
  pairing session (or spent): the route is not routed at all; the request
  falls through the ordinary token gate so a closed `/pair` is
  indistinguishable from a route that doesn't exist (401
  `{"error":"missing or wrong token"}` without a valid token, 404
  `{"error":"no such endpoint"}` with one), logging
  `peers: /pair from <remote> rejected — no pairing in progress`. Malformed
  body/empty pin → 400 `{"error":"bad pair request"}`. Wrong PIN → 401
  `{"error":"wrong pin"}` + warn with running failure count. Accepted → 200
  `PairResult(token: <the real token>, machine: AppInfo.machineName)` and log
  `peers: /pair from <remote> ('<machine>') accepted — PIN consumed, token issued`.
  **PIN and token values never reach the log. Outcomes only.**

```swift
enum PairAttemptResult: Sendable { case accepted, wrongPin, notActive }

/// One Bluetooth-style pairing offer: CSPRNG 6-digit PIN, two-minute life,
/// single-use, five wrong guesses lock it out. Lives only while the host
/// window is open. Callbacks fire on server worker threads — subscribers
/// hop to the main actor themselves.
final class PairingSession: @unchecked Sendable {
    static let maxAttempts = 5
    static let lifetime: TimeInterval = 120
    let pin: String                       // 6 digits; shown on screen, never logged
    let expiresAt: Date
    var failuresSoFar: Int { get }
    var active: Bool { get }
    var onSucceeded: (@Sendable (String) -> Void)?   // redeeming machine's name
    var onWrongGuess: (@Sendable (Int) -> Void)?     // running failure count
    var onLockedOut: (@Sendable () -> Void)?
    init()                                // Int.random(in: 0..<1_000_000) via SystemRandomNumberGenerator, "%06d"
    func close()                          // window gone → /pair goes dark immediately
    func tryRedeem(pin: String, peerMachine: String) -> PairAttemptResult
}

/// Host side of pairing: a small glass card with the large PIN and a
/// countdown ("PIN expires in m:ss", 250 ms tick). Registers the session
/// with server.beginPairing on show; close (any path) calls session.close()
/// and server.endPairing. Self-closes ~2 s after success ("Paired", "✓"),
/// ~3 s after lockout ("Pairing cancelled", "✕"), or on expiry.
@MainActor final class PairHostWindowController {
    init(session: PairingSession, server: PeerServer)
    var onClosed: (() -> Void)?
    func show()
    func close()
}
```

PIN comparison inside `tryRedeem` is constant-time (hash-and-compare, same
trick as the token). Success consumes the session forever; events fire
outside the internal lock.

## Module: PeersClient

```swift
/// A peer we can talk to: display name + the base URL its API lives at.
/// The URL is opaque past construction — routes are appended, never rebuilt
/// from host/port parts (docs/PROTOCOL.md "Addressing", proto 2).
struct PeerInfo: Sendable, Hashable {
    let name: String
    let baseURL: URL
}

struct TailnetNode: Sendable {
    let hostName: String; let ip: String; let online: Bool; let isSelf: Bool
}

/// Address discovery. Better than the Windows build on purpose:
/// self-address comes from getifaddrs, not the CLI (docs/MAC.md "Peer layer").
enum Tailnet {
    /// First IPv4 in 100.64.0.0/10 across interfaces — Tailscale addresses
    /// always live in that CGNAT range. No CLI, no path guessing.
    static func selfIPv4() -> String?
    /// Fleet enumeration still needs `tailscale status --json`. Blocking
    /// (subprocess, 10 s cap) — never call on the main actor. CLI candidates,
    /// first that exists wins: "tailscale" on PATH, /usr/local/bin/tailscale,
    /// /opt/homebrew/bin/tailscale,
    /// /Applications/Tailscale.app/Contents/MacOS/Tailscale.
    static func nodes() -> [TailnetNode]
}

final class PeerClient: Sendable {
    let peer: PeerInfo
    /// ~/Library/Application Support/esgee/peercache — remote files are
    /// materialized here so anything OS-facing gets a real local path, and
    /// so re-drags don't re-download. Layout: <sanitized peer name>/<id>_<fileName>.
    static var cacheRoot: URL { get }

    init(peer: PeerInfo, token: String)   // URLSession, 300 s request timeout,
                                          // token header on every request

    func ping(timeout: TimeInterval) async throws -> PingDto
    func recent(_ n: Int) async throws -> [ShotDto]
    func search(_ query: String) async throws -> [ShotDto]   // percent-encodes q
    func meta(id: Int64) async throws -> ShotDto?             // nil on 404
    func thumb(id: Int64) async throws -> Data
    func cachePath(for dto: ShotDto) -> URL
    func isCached(_ dto: ShotDto) -> Bool
    /// Download file (+ gif/thumb siblings for videos; thumb optional) into
    /// the cache via a ".part" temp + rename. Idempotent. Returns a Shot
    /// whose path points at the LOCAL copy.
    func ensureLocal(_ dto: ShotDto) async throws -> Shot
    /// takenAtRaw = dto.takenAt verbatim; takenAt parsed (epoch fallback);
    /// origin = dto.origin unless empty, then peer.name.
    func toLocalShot(_ dto: ShotDto, localPath: String) -> Shot
    func ingest(meta: IngestMeta, filePath: String,
                gifPath: String?, thumbPath: String?) async throws -> IngestResult

    /// Probes every candidate in parallel, 2 s ping timeout, keeps answers
    /// with app == "esgee". Includes this machine when its own server is up —
    /// the self-peer loopback is the supported single-machine test rig.
    /// Logs "peers: discovery probed N candidates, found M".
    static func discover(settings: Settings) async -> [(info: PeerInfo, ping: PingDto)]
    /// Online tailnet nodes (http://<ip>:<settings.peerPort>) plus manual
    /// Peers entries, deduped by URL. Blocking (Tailnet.nodes) — never on
    /// the main actor. Entry grammar: "name=addr" or "addr", where addr is
    /// a full http(s) URL, "host:port", or bare host (default port).
    static func candidatePeers(settings: Settings) -> [PeerInfo]

    enum PairOutcome: Sendable { case paired, wrongPin, noPairing }
    struct PairAttempt: Sendable { let outcome: PairOutcome; let result: PairResult?; let peer: PeerInfo }
    /// One POST /pair to one candidate. No token header — the PIN is the
    /// credential. 401 whose body contains "wrong pin" → .wrongPin; any
    /// other failure/unreachable → .noPairing. Never logs PIN or token.
    static func tryPair(peer: PeerInfo, pin: String, timeout: TimeInterval) async -> PairAttempt
}

/// Background push of every new capture to SyncTargetPeer. Designed to stay
/// OUT of the capture path: enqueue is a non-blocking write, the single
/// worker owns all network I/O, at-least-once delivery is harmless because
/// the receiver dedupes by sha256.
final class SyncQueue: @unchecked Sendable {
    let target: String                    // SyncTargetPeer verbatim — also the sync_pushed key
    var pending: Int { get }
    var offline: Bool { get }
    var onStateChanged: (@Sendable () -> Void)?
    init(store: ShotStore, settings: Settings)
    func enqueue(_ shotId: Int64)         // any thread, never blocks, never throws
    func enqueueBacklog()                 // store.notPushed sweep; run once at startup, off main
    func shutdown()                       // cancel the worker; returns without waiting
}

/// Joining side of pairing: type the 6-digit PIN shown on the other machine.
/// Submits to every candidate in parallel (4 s timeout each); outcome
/// precedence paired > wrongPin > noPairing. On success calls onPaired (main
/// actor) — AppDelegate persists the token and restarts the server — shows
/// "Paired with <machine> — peers are on." and closes after ~2 s.
@MainActor final class PairJoinWindowController {
    init(settings: Settings, onPaired: @escaping @MainActor (PairResult) -> Void)
    var onClosed: (() -> Void)?
    func show()
    func close()
}
```

SyncQueue behavior, ported from `SyncQueue.cs`:

- Worker drains an unbounded queue; per item, retry loop with backoff
  `[5 s, 15 s, 1 m, 5 m, 15 m]` (cap at last). On first failure log ONE warn
  `sync: push to <target> failed (<error>); retrying with backoff`, set
  `offline = true`; going online again clears it. Target resolution happens
  per connection attempt (the peer's IP may change): full URL → use as-is;
  `host:port` → `http://host:port`; bare name → find tailnet node by
  hostName case-insensitively, else keep retrying.
- Vanished shot or missing file → warn, `markPushed`, move on.
- Images wait up to 30 s (1 s polls of `ocrState`) for OCR so the sidecar
  carries text and the receiver never re-OCRs; a stuck OCR sends without —
  the receiver's sweep fills the hole. `IngestMeta.ocrText` = the text (may
  be ""), `ocrEngineVersion` only when non-empty, `origin` =
  `shot.origin.isEmpty ? AppInfo.machineName : shot.origin`, `takenAt` =
  `shot.takenAtRaw`, `fileName` = `shot.fileName`.
- Videos ship `gifPath` and (when the file exists) `thumbPath` siblings.
- Success: `markPushed`, log
  `sync: pushed shot N to <target> (remote id M[, deduplicated])`.

## Module: Capture

```swift
/// Chord actions. Raw values match the Windows action names so log lines
/// ("hotkey pressed -> region") stay greppable across platforms.
enum HotkeyAction: String, CaseIterable, Sendable {
    case region, screen, last, timer, archive
}

/// "Ctrl+Shift+S" → Carbon (keyCode, modifiers). Modifier words (case-
/// insensitive): ctrl/control → controlKey, shift → shiftKey,
/// alt/option/opt → optionKey, cmd/command/win → cmdKey ("win" so a settings
/// file copied from a Windows machine still parses to something sane).
/// Final token: a–z, 0–9, f1–f12. Unparseable → nil, caller logs and skips.
struct Chord: Sendable, Equatable {
    let keyCode: UInt32
    let carbonModifiers: UInt32
    let display: String                    // the original string, for menus/logs
    static func parse(_ chord: String) -> Chord?
}

/// Carbon RegisterEventHotKey — still the correct API: system-wide and needs
/// NO Accessibility permission (NSEvent global monitors would add a second
/// TCC prompt for no benefit). One InstallEventHandler on
/// GetEventDispatcherTarget for kEventHotKeyPressed; hotkey ids start at
/// 0xE5E0. Duplicate chords keep the first binding. A failed registration
/// logs "hotkey <chord> unavailable (likely claimed by another app)" and
/// never aborts startup; success logs "hotkey registered: <chord> -> <action>".
/// Zero registrations logs the same despair line as Windows.
@MainActor final class HotkeyManager {
    private(set) var bound: [(action: HotkeyAction, chord: String)]
    init(bindings: [(chord: String, action: HotkeyAction)],
         onPress: @escaping @MainActor (HotkeyAction) -> Void)
    func unregisterAll()
}

/// One display's frozen frame. framePoints is the NSScreen frame in Cocoa
/// global points (origin bottom-left); image is at native backing scale.
struct FrozenDisplay {
    let displayID: CGDirectDisplayID
    let framePoints: CGRect
    let scale: CGFloat
    let image: CGImage
}

/// ScreenCaptureKit one-shots. SCScreenshotManager.captureImage is the
/// supported path on 14+ (the CGDisplay/CGWindowList family is deprecated
/// and increasingly hostile).
@MainActor enum ScreenGrabber {
    /// Grab every display at its native scale, with esgee's own windows
    /// excluded via SCContentFilter(display:excludingWindows:) — windows
    /// whose owning bundle id equals ours. No hide/show dance, no compositor
    /// settle delay: exclusion replaces the Windows workaround outright.
    static func freezeAllDisplays() async throws -> [FrozenDisplay]

    /// Composite a global-points rect out of frozen frames. Renders at the
    /// HIGHEST scale among intersected displays so a Retina region never
    /// gets downsampled by a 1x neighbour (docs/MAC.md). Returns PNG bytes
    /// (ImageIO, public.png) and the pixel size.
    /// Pixel mapping per display D (CGImage is top-left origin, Cocoa is
    /// bottom-left — the flip is where off-by-one bugs live):
    ///   local   = rect ∩ D.framePoints
    ///   srcX    = (local.minX − D.framePoints.minX) × D.scale
    ///   srcY    = (D.framePoints.maxY − local.maxY) × D.scale
    ///   srcSize = local.size × D.scale
    static func composite(rectPoints: CGRect, from frames: [FrozenDisplay])
        throws -> (png: Data, size: PixelSize)
}

/// The frozen-frame capture surface: one borderless NSWindow per NSScreen,
/// level .screenSaver (above menu bar and Dock), the frozen image as layer
/// contents, dim mask with a hole punched at the selection, crosshair
/// cursor, size badge. Selection is tracked in global points and may cross
/// displays. Windows must override canBecomeKey to accept Esc.
///   Esc / right-click     cancel
///   drag                  commit the dragged rect
///   bare click            commit the clicked display's full frame
///   Return / Space        commit all displays (union rect)
///   1–9                   cancel into a delayed re-freeze (onDelayRequested)
/// Exactly one terminal callback fires, on the main actor, after all
/// overlay windows close.
@MainActor final class OverlayController {
    init(frames: [FrozenDisplay])
    var onCommit: ((_ rectPoints: CGRect) -> Void)?
    var onDelayRequested: ((_ seconds: Int) -> Void)?
    var onCancelled: (() -> Void)?
    func show()
    func cancel()
}

/// The delay-capture countdown: a small click-through pill, top-center of
/// the main screen (24 pt below the top), so the user watches the fuse while
/// arming the menu/hover state they're trying to photograph. Non-activating,
/// ignoresMouseEvents, level .floating.
@MainActor final class CountdownPill {
    init()
    func show()
    func setRemaining(_ seconds: Int)
    func close()
}

/// Rides whatever already puts an image on the pasteboard (⌘⇧⌃4 above all).
/// NSPasteboard has no change notification — poll changeCount every 500 ms.
/// Reads .png data first, falls back to .tiff re-encoded as PNG. Dedupes by
/// content hash within 3 s (one copy can bump changeCount more than once).
@MainActor final class ClipboardWatcher {
    var onImage: (@MainActor (Data, PixelSize, Date) -> Void)?
    init()                                 // starts polling immediately
    /// Call immediately BEFORE esgee writes the pasteboard. Window-based
    /// (750 ms), deliberately not consumed on first hit: one write can bump
    /// changeCount several times, and a bare flag turned the second bump
    /// into a phantom duplicate capture on Windows.
    func ignoreNextChange()
    func stop()
}

/// esgee's own capture source and the single pipeline every source feeds:
/// save → shelf → pasteboard → index (docs/MAC.md module map). Serialized by
/// a busy flag: a second hotkey mid-flow is ignored, never stacked.
@MainActor final class CaptureController {
    init(store: ShotStore, shelf: ShelfPanelController, settings: SettingsStore,
         beforePasteboardWrite: @escaping @MainActor () -> Void)

    /// Fan-out hook AppDelegate wires to OCR + sync enqueues. Fired on the
    /// main actor for every shot that lands, from any source.
    var onShotSaved: ((Shot) -> Void)?

    func beginRegion()        // freeze → overlay → composite → finish
    func beginFullscreen()    // freeze → composite(union of all displays) → finish
    func beginLastRegion()    // settings.lastRegion rect, re-frozen pixels; empty
                              // intersection falls back to beginRegion with a warn
    func beginTimed()         // pill countdown (timerSeconds clamped 1–60), 160 ms
                              // settle, then the region flow on a fresh freeze

    /// THE pipeline entry. Synchronous and blocking (disk + hash) — call it
    /// off the main actor; the begin* flows do so via Task.detached, the
    /// clipboard watcher's wiring in AppDelegate does the same. Internally:
    /// store.add, then hop to the main actor for shelf.push + onShotSaved.
    /// Logs "captured <w>x<h> -> <path>".
    nonisolated func save(png: Data, size: PixelSize, takenAt: Date) throws -> Shot
}
```

CaptureController behavior:

- `finish` step for the four begin* flows (NOT for watcher captures): after
  `save` returns, on the main actor call `beforePasteboardWrite()` then
  `ShotPasteboard.copy(shot)` — hotkey captures must land on the pasteboard
  so ⌘V muscle memory keeps working; watcher captures are already there.
- Region commit also persists
  `settings.update { $0.lastRegion = [x, y, w, h] }` (global points, ints)
  before compositing.
- Busy rule (mirrors `_timing || _overlay != nil`): overlay open, pill
  burning, or a composite in flight → new begin* calls log
  `capture already in progress; ignoring` and return.
- Screen Recording permission: AppDelegate has already preflighted/prompted.
  If `freezeAllDisplays` throws (denied), log the error once per attempt —
  the menu bar carries the repair link; do not throw UI from here.

## Module: ShelfUI

```swift
/// The corner shelf. Exists so captures stop competing for the pasteboard's
/// single slot. An NSPanel with EXACTLY these properties — this combination
/// is what floats over full-screen Spaces without yanking the user out
/// (docs/MAC.md "Shelf and drag-out"):
///   styleMask          [.nonactivatingPanel, .borderless]
///   level              .floating
///   collectionBehavior [.canJoinAllSpaces, .fullScreenAuxiliary, .stationary]
///   isFloatingPanel    true
///   hidesOnDeactivate  false
///   plus: transparent background, no shadow (cards carry their own),
///   becomesKeyOnlyIfNeeded true.
/// Anchored to the bottom-right of NSScreen.main.visibleFrame, 18 pt gap,
/// re-anchored on every push. Newest card at the bottom. Hidden while empty
/// (an empty transparent panel still costs compositor work).
@MainActor final class ShelfPanelController {
    init(settings: SettingsStore, beforePasteboardWrite: @escaping @MainActor () -> Void)
    func push(_ shot: Shot)      // oldest card .leave()s while count ≥ maxCards
    func clearAll()
    var isEmpty: Bool { get }
}

/// One card. 184 pt content width; thumbnail decoded OFF the main actor at
/// ~2× display width (392 px) via ImageIO downsampling, then hopped back —
/// an ultrawide PNG decode drops slide-in frames otherwise.
/// Events (all main actor):
///   hover           chrome (copy / reveal-in-Finder / pin / dismiss) fades
///                   in 120 ms, out 160 ms; linger countdown pauses on
///                   enter, resumes on exit unless pinned
///   click           copy: beforePasteboardWrite(), ShotPasteboard.copy,
///                   white flash 340 ms
///   drag ≥ 4 pt     NSDraggingSession with ShotPasteboard.pasteboardItem,
///                   thumbnail as drag image, .copy operation; countdown
///                   pauses during the drag; drop somewhere real → leave();
///                   cancelled drag → resume
///   right-click     leave()
///   pin             stops the countdown and hides the timer track; unpin
///                   restarts it at the full linger
///   countdown end   leave()
/// Video badge: "▶ <durationText>" plus "  GIF" when gifPath exists.
/// leave() collapses height while fading so cards below glide up; fires
/// onGone exactly once.
@MainActor final class ShotCardView: NSView {
    init(shot: Shot, linger: TimeInterval, beforePasteboardWrite: @escaping @MainActor () -> Void)
    var onGone: ((ShotCardView) -> Void)?
    func leave()
}

/// The multi-representation pasteboard payload — the reason the app exists.
/// Images: one NSPasteboardItem carrying .fileURL (string), .png (bytes),
/// and .tiff, so Finder-likes take the file while image-first targets take
/// bytes with no temp-file round trip. Videos: .fileURL only, pointing at
/// the GIF when one exists else the MP4 — offering a still frame would make
/// image-first paste targets silently take a frame instead of the clip.
/// Every caller invokes its beforePasteboardWrite hook BEFORE copy().
@MainActor enum ShotPasteboard {
    static func pasteboardItem(for shot: Shot) -> NSPasteboardItem
    static func copy(_ shot: Shot)           // clearContents + writeObjects on .general
}

/// Shared palette — ArchiveUI reads these through Color(nsColor:) so the two
/// surfaces stay one app. Values mirror the Windows glass theme.
enum Theme {
    static let accent: NSColor        // #5B8CFF
    static let ink: NSColor           // white 0.92
    static let inkMuted: NSColor      // white 0.55
    static let hairline: NSColor      // white 0.12
    static let surface: NSColor       // near-black glass, alpha ~0.92
    static let surfaceHover: NSColor
}
```

## Module: ArchiveUI

The payoff of the OCR index: type words that were on screen weeks ago, get
the screenshot back, drag it straight out as a file. AppKit window +
`NSHostingView` root; everything inside is SwiftUI.

```swift
@MainActor final class ArchiveWindowController: NSObject, NSWindowDelegate {
    init(store: ShotStore, settings: SettingsStore,
         beforePasteboardWrite: @escaping @MainActor () -> Void)
    var onClosed: (() -> Void)?
    func showWindow()                     // creates on first call, fronts after
}

/// ObservableObject behind the grid (conservative: @Published, no macros).
/// Public surface is internal to this module; the behaviors are the contract:
@MainActor final class ArchiveModel: ObservableObject { … }
```

Required behaviors, ported from `ArchiveWindow.xaml.cs`:

- Window: titled "esgee archive", ~1000×680, min 640×420, releasedWhenClosed
  false; closing hides and fires `onClosed` (AppDelegate drops its
  reference). On open, log the provenance line:
  `archive window: v<version> from <Bundle.main.bundlePath>, peer token <present|absent>`.
- **Page size 200.** Search-as-you-type debounced 250 ms. Search goes through
  `ShotStore.ftsQuery`; FTS errors → empty results, never a crash
  mid-keystroke.
- **Live poll**: every 1.5 s, local view only, skip while a drag is in
  flight, while the debounce is pending, or while a preview navigation is
  mid-decode. Compare `store.changeToken()`; unchanged → skip. If only the
  ocr_done component moved and the search box is empty, update the token and
  skip the rebuild (tiles don't render OCR state; 200 re-decodes for nothing).
  Log `archive: index changed, auto-refreshing` when it refreshes.
- **Generation counter**: every refresh bumps it; in-flight thumbnail decodes
  and page loads from a superseded refresh must not paint.
- **Machine switcher**: hidden until `settings.current.peerToken` is
  non-empty (default config renders the pre-peers window). Items: "This Mac"
  plus `PeerClient.discover` results labeled `<name>  (<captures>)`,
  discovered off the main actor after open. Switching disposes the old
  client, logs, closes the preview, refreshes. Remote queries that fail log
  a warn and show the empty label
  `No captures on <name> (or it didn't answer).` /
  `Nothing matching "<q>" on <name>.`; local empties use
  `No captures yet — take one with the hotkey.` / `Nothing matching "<q>".`
- **Entries** wrap a `Shot` for both worlds: local = the store row; remote =
  `client.toLocalShot(dto, localPath: client.cachePath(for: dto).path)` plus
  the dto and client. `materialize()` = local: the shot as-is; remote: one
  shared `Task<Shot, Error>` running `client.ensureLocal(dto)` (started once,
  awaited by whoever needs the file).
- **Tiles**: 224 pt wide, thumbnail decoded off-main (local: ImageIO
  downsample to 448 px; remote: `client.thumb(id:)` bytes), caption
  `MMM d, HH:mm   <w>×<h>` plus `▶ durationText` for videos and `⇄ origin`
  for locally-held synced shots. Mouse-down on a remote tile starts
  `materialize()` immediately — by the time a drag crosses the threshold the
  file is usually already local.
- **Drag-out** (`ArchiveDragHost`, an NSViewRepresentable overlay owning
  mouse tracking for each tile — SwiftUI's onDrag cannot express promises):
  4 pt threshold; below it, mouse-up = click = open preview.
  - Local entry: drag `ShotPasteboard.pasteboardItem` — file URL + PNG + TIFF.
  - Remote entry: drag an `NSFilePromiseProvider` (UTType from the dto's
    file extension); `writePromise(to:)` awaits `materialize()` and copies
    the cached file to the destination. The promise resolves AFTER the drop,
    streaming from the peer — a cold drag works with no prefetch and no
    stall. That asymmetry is the whole reason the Mac drag-out is better
    than the Windows one (docs/MAC.md).
  - The model's drag-suspend flag wraps the session so the live poll cannot
    tear tiles out from under an in-flight drag.
- **Context menu**: Copy to clipboard (materialize → beforePasteboardWrite →
  ShotPasteboard.copy); Copy text (stills only — local via `store.ocrState`,
  remote via `client.meta`; flashes `screen text copied` /
  `no text yet — OCR still catching up` / `no text in this capture`);
  remote adds **Pull to this Mac**, local adds **Show in Finder**
  (`NSWorkspace.activateFileViewerSelecting`).
- **Pull** makes a remote capture first-class local: `client.meta` (the OCR
  sidecar) → `ensureLocal` → copy into `store.planIngestPath` (+ gif/thumb
  siblings for videos) → `store.ingest(takenAtRaw: dto.takenAt, …,
  ocrText: meta?.ocrText, ocrEngineVersion: meta?.ocrEngineVersion ?? "",
  origin: shot.origin)` → duplicate deletes the fresh copies. Title-flash
  `pulled to this Mac (<file>)` / `already on this Mac (<file>)` /
  `pull failed — see log`, resetting after 4 s.
- **Preview lightbox**: scrim + centered content; caption
  `MMM d, yyyy  HH:mm   <w>×<h>` (+ `▶ dur`, + `on <peer>`). Snapshot the
  entry list on open so a live-poll refresh can't shift navigation. ←/→
  step (clamped), Esc peels preview first then closes the window, ⌘F
  focuses search. Stills decode full-size off-main with a stale-guard;
  remote preview warms the cache so the next drag is instant. Videos play
  the actual clip — muted, looping (AVQueuePlayer + AVPlayerLooper behind
  AVKit's VideoPlayer), after `materialize()` lands.
- **Screen-text panel**: toggle on the preview (stills only). States:
  `…` while loading, real text (selectable), or verbatim
  `No text yet — OCR is still catching up on this capture.` /
  `No text found in this capture.` / `text unavailable — see log`.
  Copy-all button copies only real text (`screen text copied`).

---

## Cross-cutting rules

- **sha256 is uppercase hex everywhere** (CryptoKit `SHA256`, then
  `map { String(format: "%02X", $0) }.joined()`). Windows stores uppercase;
  a lowercase hash would silently defeat cross-platform dedupe.
- **taken_at**: raw strings travel; `IsoStamp.format` runs only at local
  capture time (see the round-trip rule above).
- **Engine versions**: `vision/<revision>+<osbuild>` here,
  `winocr/<osversion>` on Windows. Receivers import OCR text, never re-run
  it on ingested captures; `ocr_text = nil` on an image means "not OCR'd
  yet", and nobody fabricates empty strings for it.
- **Capabilities**: Mac advertises `["peer"]` (`PeerCapability.advertised`).
  A `/ping` without a capabilities field is a proto-1 peer and means
  `["peer"]` — use `PingDto.effectiveCapabilities`, never the raw field.
- **Security**: peer server binds only the Tailscale address; token and PIN
  compare in constant time; PIN/token values never appear in logs — outcomes
  only; `/pair` exists only while a pairing window is open.
- **Log parity** (keep these shapes verbatim; they are how one pair of eyes
  debugs both platforms): `hotkey registered: <chord> -> <action>`,
  `hotkey pressed -> <action>`, `captured <w>x<h> -> <path>`,
  `deduplicated identical capture (echo of shot N)`,
  `peers: serving archive on http://<addr> (tailscale interface only)`,
  `peers: /ping from <remote>`, `peers: /recent n=<n> -> <count> from <remote>`,
  `peers: ingested <kind> <w>x<h> from <origin> -> <path> (id N, ocr <pending|imported from sidecar [engine]>)`,
  `sync: backlog sweep found N unpushed capture(s)`,
  `sync: pushed shot N to <target> (remote id M[, deduplicated])`,
  `peers: pairing open — /pair answering until <HH:mm:ss>`,
  `peers: pairing closed — /pair disabled`, `esgee down`.
- **UI voice**: sentence case, quiet, concrete ("Pair a new machine…", "No
  text found in this capture."). No exclamation points. Palette through
  `Theme`.
- **Numbers that are contract**: shelf gap 18 pt, card width 184 pt, card
  thumb decode 392 px, grid tile 224 pt, grid thumb 448 px, page size 200,
  debounce 250 ms, live poll 1.5 s, drag threshold 4 pt, clipboard poll
  500 ms, ignore window 750 ms, clipboard dedupe 3 s, store echo window 10 s,
  OCR wait 30 s, ping probe 2 s, pair probe 4 s, PIN life 120 s, 5 PIN
  attempts, thumb JPEG quality 0.8.

## Deferred (do not build, do not stub)

- Screen recording (`record` capability stays off `advertised`).
- Sparkle wiring (`App/Updater.swift`) and the real menu bar
  (`App/MenuBarController.swift`) — integrator pass.
- CLI modes (`--serve`, `--archive`, `--doctor`) and `ShotStore.doctor()`.
- Share routes (proto 2 share namespace), annotations.
- Settings UI beyond "Edit settings" opening the JSON.

## Build (integrator)

```
cd mac && xcodegen generate && xcodebuild -scheme esgee -configuration Release build
```

`.xcodeproj` is gitignored; regenerate freely. Local builds are ad-hoc signed
and report 0.0.0 — CI injects MARKETING_VERSION, the Developer ID identity,
and the Sparkle public key.
