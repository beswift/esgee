import AppKit
import SwiftUI
import AVFoundation
import ImageIO

/// NSImage's Sendable conformance is explicitly unavailable, so decode
/// results cross back from Task.detached in a box (same pattern as
/// ShotCardView.ThumbBox — immutable in practice, moved not shared).
private struct DecodedImage: @unchecked Sendable { let img: NSImage? }

/// The payoff of the OCR index: type words that were on screen weeks ago, get
/// the screenshot back, drag it straight out as a file.
///
/// With peers configured, the machine switcher browses another machine's
/// archive over the tailnet through the same grid/search/preview UX. Remote
/// tiles stream the peer's pre-scaled thumbnails; the original bytes move only
/// when a drag, copy, preview, or pull actually needs them.
@MainActor
final class ArchiveWindowController: NSObject, NSWindowDelegate {
    private let store: ShotStore
    private let settings: SettingsStore
    private let model: ArchiveModel
    private var window: ArchiveKeyWindow?

    var onClosed: (() -> Void)?

    init(store: ShotStore, settings: SettingsStore,
         beforePasteboardWrite: @escaping @MainActor () -> Void) {
        self.store = store
        self.settings = settings
        self.model = ArchiveModel(store: store, settings: settings,
                                  beforePasteboardWrite: beforePasteboardWrite)
        super.init()

        // Every window announces its provenance. When a stale window from an
        // old process is mistaken for the current build ("the switcher is
        // gone?"), this line names the binary that actually drew it.
        Log.info("archive window: v\(AppInfo.version) from \(Bundle.main.bundlePath), " +
                 "peer token \(settings.current.peerToken.isEmpty ? "absent" : "present")")
    }

    func showWindow() {
        if window == nil { buildWindow() }
        model.start()
        window?.makeKeyAndOrderFront(nil)
    }

    /// Re-runs machine discovery so an already-open window gains the
    /// switcher the moment a pairing lands — the same promise the Windows
    /// build keeps via ArchiveWindow.RefreshMachineSwitcher().
    func refreshMachineSwitcher() {
        model.refreshMachineSwitcher()
    }

    private func buildWindow() {
        let win = ArchiveKeyWindow(
            contentRect: NSRect(x: 0, y: 0, width: 1000, height: 680),
            styleMask: [.titled, .closable, .miniaturizable, .resizable],
            backing: .buffered, defer: false)
        win.title = "esgee archive"
        win.contentMinSize = NSSize(width: 640, height: 420)
        win.isReleasedWhenClosed = false
        // The glass theme is dark on both platforms; Windows forces a dark
        // title bar for the same reason.
        win.appearance = NSAppearance(named: .darkAqua)
        win.center()
        win.delegate = self
        win.contentView = NSHostingView(rootView: ArchiveView(model: model))
        win.keyHandler = { [weak self] event in self?.handleKey(event) ?? false }
        win.escapeHandler = { [weak self] in self?.handleEscape() }
        window = win

        model.onTitleChanged = { [weak win] title in win?.title = title }
        // Arrow keys must land on the window, not the search box, once a
        // preview is up.
        model.onPreviewOpened = { [weak win] in win?.makeFirstResponder(nil) }
    }

    private func handleKey(_ event: NSEvent) -> Bool {
        let mods = event.modifierFlags.intersection(.deviceIndependentFlagsMask)
        if mods.contains(.command),
           event.charactersIgnoringModifiers?.lowercased() == "f" {
            model.requestSearchFocus()
            return true
        }
        switch event.keyCode {
        case 53: // Esc — the field-editor path arrives via cancelOperation instead
            handleEscape()
            return true
        case 123, 124: // arrows step the preview — unless a caret owns them
            guard model.previewVisible,
                  !(window?.firstResponder is NSTextView) else { return false }
            model.stepPreview(event.keyCode == 123 ? -1 : +1)
            return true
        default:
            return false
        }
    }

    /// Esc peels one layer at a time: preview first, then the window.
    private func handleEscape() {
        if model.previewVisible {
            model.closePreview()
        } else {
            hideWindow()
        }
    }

    /// Closing hides; AppDelegate drops its reference on onClosed and the
    /// whole graph deallocates. releasedWhenClosed stays false so the close
    /// button can't over-release the window out from under us.
    private func hideWindow() {
        model.stop()
        window?.orderOut(nil)
        onClosed?()
    }

    func windowShouldClose(_ sender: NSWindow) -> Bool {
        hideWindow()
        return false
    }
}

/// Esc must peel the preview even while the search field owns the caret, and
/// ⌘F must reach the search box from anywhere. Both are caught here, at the
/// top of the responder chain, because SwiftUI key handling never sees events
/// the AppKit field editor consumes first.
private final class ArchiveKeyWindow: NSWindow {
    var keyHandler: (@MainActor (NSEvent) -> Bool)?
    var escapeHandler: (@MainActor () -> Void)?

    override func keyDown(with event: NSEvent) {
        if keyHandler?(event) == true { return }
        super.keyDown(with: event)
    }

    override func performKeyEquivalent(with event: NSEvent) -> Bool {
        if keyHandler?(event) == true { return true }
        return super.performKeyEquivalent(with: event)
    }

    // Esc inside a text field surfaces as cancelOperation, not keyDown.
    override func cancelOperation(_ sender: Any?) {
        escapeHandler?()
    }
}

/// One switcher choice: This Mac, or a discovered peer.
struct MachineChoice: Identifiable, Hashable, Sendable {
    static let localID = "local"
    static let thisMac = MachineChoice(id: localID, label: "This Mac", peer: nil)

    let id: String
    let label: String
    let peer: PeerInfo?
}

/// ObservableObject behind the grid. Conservative on purpose: @Published and
/// hand-rolled debounce, no macros — this file must compile first try.
@MainActor
final class ArchiveModel: ObservableObject {
    static let pageSize = 200

    private let store: ShotStore
    private let settings: SettingsStore
    private let beforePasteboardWrite: @MainActor () -> Void

    // ---- published surface the views render --------------------------------

    @Published var query: String = "" {
        didSet { if query != oldValue { scheduleDebounce() } }
    }
    @Published private(set) var entries: [ArchiveEntry] = []
    @Published private(set) var emptyText = ""
    @Published private(set) var title = "esgee archive" {
        didSet { onTitleChanged?(title) }
    }
    @Published private(set) var switcherVisible = false
    @Published private(set) var machines: [MachineChoice] = []
    @Published var selectedMachineID: String = MachineChoice.localID {
        didSet { if started && selectedMachineID != oldValue { machineChanged() } }
    }
    @Published private(set) var searchFocusToken = 0

    @Published private(set) var previewVisible = false
    @Published private(set) var previewImage: NSImage?
    @Published private(set) var previewPlayer: AVQueuePlayer?
    @Published private(set) var previewCaption = ""
    @Published private(set) var ocrPanelOpen = false
    @Published private(set) var ocrDisplayText = ""
    @Published private(set) var ocrRealText: String?

    var onTitleChanged: (@MainActor (String) -> Void)?
    var onPreviewOpened: (@MainActor () -> Void)?

    /// Set by the drag host around a dragging session so the live poll cannot
    /// tear tiles out from under an in-flight drag.
    var dragSuspended = false

    // ---- internals ----------------------------------------------------------

    private var remoteClient: PeerClient?
    // Bumped on every refresh so in-flight page loads and decodes from a
    // superseded search can't paint into the new result set.
    private var generation = 0
    private var lastToken = ""
    private var started = false
    private var windowVisible = false
    private var pollTimer: Timer?
    private var debounceTask: Task<Void, Never>?
    private var titleResetTask: Task<Void, Never>?

    // Preview state. previewEntries is a snapshot taken at open so a
    // live-poll refresh can't shift navigation underneath the lightbox.
    private var previewEntries: [ArchiveEntry] = []
    private var previewIndex = -1
    private var previewLooper: AVPlayerLooper?
    private var previewLoadsInFlight = 0
    private var ocrLoadSeq = 0

    init(store: ShotStore, settings: SettingsStore,
         beforePasteboardWrite: @escaping @MainActor () -> Void) {
        self.store = store
        self.settings = settings
        self.beforePasteboardWrite = beforePasteboardWrite
    }

    var previewEntry: ArchiveEntry? {
        guard previewIndex >= 0, previewIndex < previewEntries.count else { return nil }
        return previewEntries[previewIndex]
    }

    // ---- lifecycle -----------------------------------------------------------

    func start() {
        windowVisible = true
        refresh()
        if !started {
            started = true
            loadMachines()
            startPoll()
        }
    }

    func stop() {
        windowVisible = false
        closePreview()
        pollTimer?.invalidate()
        pollTimer = nil
        debounceTask?.cancel()
        debounceTask = nil
        titleResetTask?.cancel()
        started = false
    }

    func requestSearchFocus() {
        searchFocusToken += 1
    }

    // ---- search debounce -------------------------------------------------------

    /// Search-as-you-type, but not query-per-keystroke.
    private func scheduleDebounce() {
        debounceTask?.cancel()
        debounceTask = Task { [weak self] in
            try? await Task.sleep(nanoseconds: 250_000_000)
            guard !Task.isCancelled, let self else { return }
            self.debounceTask = nil
            self.refresh()
        }
    }

    // ---- machine switcher --------------------------------------------------------

    /// Public and re-runnable, mirroring the Windows RefreshMachineSwitcher:
    /// the resident app calls it when a pairing lands while this window is
    /// open, so the switcher tracks settings instead of snapshotting them at
    /// open. Resets the selection to This Mac — an adopted token makes any
    /// existing remoteClient's captured token stale, and the next machine
    /// switch builds a fresh client from settings.current.
    func refreshMachineSwitcher() {
        guard started else { return } // start() runs the first build
        loadMachines()
    }

    /// This Mac plus every peer that answers /ping with our token. Hidden
    /// entirely until a PeerToken exists, so the default configuration
    /// renders the exact pre-peers window.
    private func loadMachines() {
        guard !settings.current.peerToken.isEmpty else { return }
        switcherVisible = true
        machines = [.thisMac]
        selectedMachineID = MachineChoice.localID

        let snapshot = settings.current
        Task.detached { [weak self] in
            let found = await PeerClient.discover(settings: snapshot)
            await MainActor.run { [weak self] in
                guard let self else { return }
                self.machines = [.thisMac] + found.map {
                    MachineChoice(id: $0.info.baseURL.absoluteString,
                                  label: "\($0.info.name)  (\($0.ping.captures))",
                                  peer: $0.info)
                }
            }
        }
    }

    private func machineChanged() {
        if let choice = machines.first(where: { $0.id == selectedMachineID }),
           let peer = choice.peer {
            remoteClient = PeerClient(peer: peer, token: settings.current.peerToken)
            Log.info("archive: switched to peer \(peer.name) (\(peer.baseURL.absoluteString))")
        } else {
            remoteClient = nil
            Log.info("archive: switched to local store")
        }
        closePreview()
        refresh()
    }

    // ---- refresh -----------------------------------------------------------------

    func refresh() {
        let q = query.trimmingCharacters(in: .whitespacesAndNewlines)
        generation += 1
        let gen = generation

        if let client = remoteClient {
            refreshRemote(client, query: q, gen: gen)
            return
        }

        // Any refresh observes the current index state; keep the poll's token
        // in step so it doesn't immediately re-refresh over us.
        lastToken = store.changeToken()

        let store = self.store
        let limit = Self.pageSize
        Task.detached(priority: .userInitiated) { [weak self] in
            var shots: [Shot]
            if q.isEmpty {
                shots = store.recent(limit: limit)
            } else {
                do {
                    shots = try store.search(matching: ShotStore.ftsQuery(q), limit: limit)
                } catch {
                    // An unbalanced quote mid-keystroke throws in FTS; show
                    // empty results rather than crash while the user types.
                    Log.warn("archive query failed: \(error.localizedDescription)")
                    shots = []
                }
            }
            let page = shots
            await MainActor.run { [weak self] in
                guard let self, gen == self.generation, self.remoteClient == nil else { return }
                self.entries = page.map { ArchiveEntry(shot: $0) }
                self.emptyText = q.isEmpty
                    ? "No captures yet — take one with the hotkey."
                    : "Nothing matching \"\(q)\"."
            }
        }
    }

    private func refreshRemote(_ client: PeerClient, query q: String, gen: Int) {
        if entries.isEmpty { emptyText = "Loading from \(client.peer.name)…" }

        Task { [weak self] in
            var dtos: [ShotDto] = []
            do {
                dtos = q.isEmpty
                    ? try await client.recent(Self.pageSize)
                    : try await client.search(q)
            } catch {
                Log.warn("peer \(client.peer.name): query failed: \(error.localizedDescription)")
            }
            guard let self, gen == self.generation, self.remoteClient === client else { return }
            self.entries = dtos.map { ArchiveEntry(dto: $0, client: client) }
            self.emptyText = q.isEmpty
                ? "No captures on \(client.peer.name) (or it didn't answer)."
                : "Nothing matching \"\(q)\" on \(client.peer.name)."
        }
    }

    // ---- live poll -----------------------------------------------------------------

    /// Live refresh. The index changes under this window — captures land,
    /// OCR completes — and no in-memory event is guaranteed to reach it, so
    /// poll the store's cheap change token (WAL read, sub-ms). Local only:
    /// a remote view refreshes on demand, not by polling a peer.
    private func startPoll() {
        pollTimer?.invalidate()
        pollTimer = Timer.scheduledTimer(withTimeInterval: 1.5, repeats: true) { [weak self] _ in
            Task { @MainActor in self?.pollTick() }
        }
    }

    private func pollTick() {
        guard windowVisible, remoteClient == nil, !dragSuspended,
              debounceTask == nil, previewLoadsInFlight == 0 else { return }

        // Never rebuild while the left button is down: a refresh replaces
        // every tile, and a tile destroyed between mouse-down and mouse-up
        // silently eats the click (or a nascent drag). This was the "newest
        // capture won't open" bug on Windows.
        guard NSEvent.pressedMouseButtons & 0x1 == 0 else { return }

        let token = store.changeToken()
        if token == lastToken { return }

        // Tiles don't render OCR state, so if only ocr_done moved and no
        // search is active there is nothing visual to update — skip the
        // rebuild (and its 200 background decodes) entirely.
        if query.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty,
           Self.sameRows(token, lastToken) {
            lastToken = token
            return
        }

        lastToken = token
        Log.info("archive: index changed, auto-refreshing")
        refresh()
    }

    /// True when two "maxid:count:ocrdone" tokens differ only in the
    /// ocr_done component — i.e. no rows were added or removed.
    private static func sameRows(_ a: String, _ b: String) -> Bool {
        let pa = a.split(separator: ":", omittingEmptySubsequences: false)
        let pb = b.split(separator: ":", omittingEmptySubsequences: false)
        return pa.count == 3 && pb.count == 3 && pa[0] == pb[0] && pa[1] == pb[1]
    }

    // ---- preview lightbox --------------------------------------------------------------

    func openPreview(_ entry: ArchiveEntry) {
        previewEntries = entries
        if let idx = previewEntries.firstIndex(where: { $0 === entry }) {
            previewIndex = idx
        } else {
            previewEntries = [entry]
            previewIndex = 0
        }
        previewVisible = true
        onPreviewOpened?()
        showPreview(entry)
    }

    func stepPreview(_ delta: Int) {
        guard previewVisible, !previewEntries.isEmpty else { return }
        let next = min(max(previewIndex + delta, 0), previewEntries.count - 1)
        guard next != previewIndex else { return }
        previewIndex = next
        showPreview(previewEntries[next])
    }

    func closePreview() {
        guard previewVisible else { return }
        teardownPlayer()
        previewImage = nil
        previewVisible = false
        setOcrPanel(false)
        previewIndex = -1
        previewEntries = []
        requestSearchFocus()
    }

    private var currentPreviewShotId: Int64? {
        guard previewVisible, previewIndex >= 0, previewIndex < previewEntries.count else { return nil }
        return previewEntries[previewIndex].shot.id
    }

    private func showPreview(_ entry: ArchiveEntry) {
        let shot = entry.shot
        let when = ArchiveFmt.previewStamp.string(from: shot.takenAt)
        let dims = "\(shot.width)×\(shot.height)"
        let from = entry.isRemote ? "   on \(entry.client?.peer.name ?? "")" : ""
        previewCaption = shot.isVideo
            ? "\(when)   ▶ \(shot.durationText)   \(dims)\(from)"
            : "\(when)   \(dims)\(from)"

        teardownPlayer()
        previewImage = nil

        // Recordings carry no OCR text; the panel only makes sense for stills.
        if shot.isVideo {
            setOcrPanel(false)
        } else if ocrPanelOpen {
            loadOcrPanel(entry)
        }

        let expected = shot.id
        let fetch = entry.materialize()

        if shot.isVideo {
            // Play the actual clip — muted loop; a frozen thumbnail would be
            // a letdown for the one media type whose point is motion. A
            // remote clip lands in the cache first (AVPlayer needs a file).
            Task { [weak self] in
                do {
                    let local = try await fetch.value
                    guard let self, self.currentPreviewShotId == expected else { return }
                    let item = AVPlayerItem(url: URL(fileURLWithPath: local.path))
                    let player = AVQueuePlayer()
                    self.previewLooper = AVPlayerLooper(player: player, templateItem: item)
                    player.isMuted = true
                    self.previewPlayer = player
                    player.play()
                } catch {
                    Log.warn("preview video failed for \(shot.path): \(error.localizedDescription)")
                }
            }
            return
        }

        // Full-quality decode off the main actor, guarded against the user
        // having stepped on before a slow decode (or a remote download)
        // lands. This also doubles as the remote prefetch: previewing warms
        // the cache, so a drag right after is instant. The in-flight counter
        // keeps the live poll from rebuilding tiles mid-navigation.
        previewLoadsInFlight += 1
        Task { [weak self] in
            defer { self?.previewLoadsInFlight -= 1 }
            do {
                let local = try await fetch.value
                let path = local.path
                let img = await Task.detached {
                    DecodedImage(img: ArchiveEntry.decodeImage(path: path, maxPixel: 8192))
                }.value.img
                guard let self, self.currentPreviewShotId == expected, let img else { return }
                self.previewImage = img
            } catch {
                Log.warn("preview decode failed for \(shot.path): \(error.localizedDescription)")
            }
        }
    }

    private func teardownPlayer() {
        previewPlayer?.pause()
        previewLooper = nil
        previewPlayer = nil
    }

    // ---- screen text (OCR) ----------------------------------------------------------------

    func toggleOcrPanel() {
        setOcrPanel(!ocrPanelOpen)
        if ocrPanelOpen, let entry = previewEntry { loadOcrPanel(entry) }
    }

    private func setOcrPanel(_ open: Bool) {
        ocrPanelOpen = open
        if !open {
            ocrDisplayText = ""
            ocrRealText = nil
        }
    }

    private func loadOcrPanel(_ entry: ArchiveEntry) {
        ocrLoadSeq += 1
        let seq = ocrLoadSeq
        ocrRealText = nil
        ocrDisplayText = "…"

        Task { [weak self] in
            guard let self else { return }
            let r = await self.fetchOcr(entry)
            guard seq == self.ocrLoadSeq else { return }
            if let problem = r.problem { self.ocrDisplayText = problem; return }
            if !r.done {
                self.ocrDisplayText = "No text yet — OCR is still catching up on this capture."
                return
            }
            if r.text.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty {
                self.ocrDisplayText = "No text found in this capture."
                return
            }
            self.ocrRealText = r.text
            self.ocrDisplayText = r.text
        }
    }

    func copyAllOcrText() {
        // The panel also holds status messages; only real text copies.
        guard let text = ocrRealText else {
            flashTitle("no text to copy")
            return
        }
        beforePasteboardWrite()
        let pb = NSPasteboard.general
        pb.clearContents()
        pb.setString(text, forType: .string)
        flashTitle("screen text copied")
    }

    /// Tile context menu: straight to the clipboard, no preview needed — the
    /// fast path for handing a screenshot's text to whatever needs it.
    func copyOcrText(_ entry: ArchiveEntry) {
        Task { [weak self] in
            guard let self else { return }
            let r = await self.fetchOcr(entry)
            if let problem = r.problem { self.flashTitle(problem); return }
            guard r.done else { self.flashTitle("no text yet — OCR still catching up"); return }
            guard !r.text.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty else {
                self.flashTitle("no text in this capture")
                return
            }
            self.beforePasteboardWrite()
            let pb = NSPasteboard.general
            pb.clearContents()
            pb.setString(r.text, forType: .string)
            self.flashTitle("screen text copied")
        }
    }

    /// One shot's OCR text, wherever it lives: the local index, or the peer's
    /// /meta (which already carries text for pull sidecars). Never throws —
    /// a peer problem comes back as a message instead.
    private func fetchOcr(_ entry: ArchiveEntry) async -> (done: Bool, text: String, problem: String?) {
        if let client = entry.client, let dto = entry.dto {
            do {
                guard let meta = try await client.meta(id: dto.id) else {
                    return (false, "", "\(client.peer.name) didn't answer for this capture")
                }
                return (meta.ocrText != nil, meta.ocrText ?? "", nil)
            } catch {
                Log.warn("archive: OCR fetch failed for shot \(entry.shot.id): \(error.localizedDescription)")
                return (false, "", "text unavailable — see log")
            }
        }

        let store = self.store
        let id = entry.shot.id
        let state = await Task.detached { store.ocrState(id: id) }.value
        return (state.done, state.text ?? "", nil)
    }

    // ---- copy / pull / reveal ---------------------------------------------------------------

    func copy(_ entry: ArchiveEntry) {
        let fetch = entry.materialize()
        Task { [weak self] in
            do {
                // Remote: download first; the pasteboard write itself happens
                // back on the main actor.
                let local = try await fetch.value
                guard let self else { return }
                self.beforePasteboardWrite()
                ShotPasteboard.copy(local)
            } catch {
                Log.error("archive copy failed: \(error.localizedDescription)")
            }
        }
    }

    func reveal(_ entry: ArchiveEntry) {
        guard !entry.isRemote else { return }
        NSWorkspace.shared.activateFileViewerSelecting([URL(fileURLWithPath: entry.shot.path)])
    }

    /// Makes a remote capture a first-class LOCAL one: /meta (the OCR
    /// sidecar) → cache → copy into the archive tree → ingest with origin
    /// preserved. Content-hash dedupe makes a double-pull a no-op.
    func pull(_ entry: ArchiveEntry) {
        guard let client = entry.client, let dto = entry.dto else { return }
        let fetch = entry.materialize()
        let store = self.store
        let origin = entry.shot.origin

        Task.detached { [weak self] in
            do {
                let meta = try await client.meta(id: dto.id)
                let cached = try await fetch.value

                let ext = (cached.path as NSString).pathExtension
                // Filed by the SENDER's wall clock (the offset in the dto's
                // raw taken_at), matching where the sender's own tree holds
                // this capture — never this Mac's zone.
                let dest = store.planIngestPath(
                    takenAt: cached.takenAt,
                    timeZone: IsoStamp.embeddedTimeZone(of: dto.takenAt) ?? .current,
                    ext: ext.isEmpty ? ".png" : "." + ext)
                let fm = FileManager.default
                try fm.copyItem(atPath: cached.path, toPath: dest)
                if cached.isVideo {
                    let gifSrc = (cached.path as NSString).deletingPathExtension + ".gif"
                    let gifDest = (dest as NSString).deletingPathExtension + ".gif"
                    if fm.fileExists(atPath: gifSrc) {
                        try? fm.removeItem(atPath: gifDest)
                        try? fm.copyItem(atPath: gifSrc, toPath: gifDest)
                    }
                    if fm.fileExists(atPath: cached.path + ".png") {
                        try? fm.removeItem(atPath: dest + ".png")
                        try? fm.copyItem(atPath: cached.path + ".png", toPath: dest + ".png")
                    }
                }

                // takenAtRaw is the dto's string verbatim — re-formatting a
                // parsed date here would rewrite the sender's UTC offset.
                // A DB failure throws into the catch below: "pull failed",
                // never a success flash for a row that didn't land.
                let result = try store.ingest(path: dest, sha256: cached.sha256,
                                              takenAtRaw: dto.takenAt,
                                              width: cached.width, height: cached.height,
                                              kind: cached.kind, durationMs: cached.durationMs,
                                              ocrText: meta?.ocrText,
                                              ocrEngineVersion: meta?.ocrEngineVersion ?? "",
                                              origin: origin)
                if result.duplicate {
                    try? fm.removeItem(atPath: dest)
                    try? fm.removeItem(atPath: (dest as NSString).deletingPathExtension + ".gif")
                    try? fm.removeItem(atPath: dest + ".png")
                    Log.info("archive: pull of remote shot \(dto.id) deduplicated " +
                             "(already local as \(result.shot.id))")
                } else {
                    Log.info("archive: pulled remote shot \(dto.id) from \(client.peer.name) " +
                             "-> \(result.shot.path) (local id \(result.shot.id))")
                }

                let file = (result.shot.path as NSString).lastPathComponent
                let duplicate = result.duplicate
                await MainActor.run { [weak self] in
                    self?.flashTitle(duplicate
                        ? "already on this Mac (\(file))"
                        : "pulled to this Mac (\(file))")
                }
            } catch {
                Log.error("archive: pull failed: \(error.localizedDescription)")
                await MainActor.run { [weak self] in
                    self?.flashTitle("pull failed — see log")
                }
            }
        }
    }

    // ---- title flash ---------------------------------------------------------------------

    private func flashTitle(_ message: String) {
        title = "esgee archive — \(message)"
        titleResetTask?.cancel()
        titleResetTask = Task { [weak self] in
            try? await Task.sleep(nanoseconds: 4_000_000_000)
            guard !Task.isCancelled else { return }
            self?.title = "esgee archive"
        }
    }
}

/// One grid tile / preview subject, local or remote. The wrapped Shot is the
/// single shape everything downstream consumes: for a local capture it IS the
/// store row; for a remote one its path points at the peer-cache location and
/// materialize() makes that path real before drag/copy/preview needs it.
@MainActor
final class ArchiveEntry: ObservableObject, Identifiable {
    nonisolated let shot: Shot
    nonisolated let dto: ShotDto?
    nonisolated let client: PeerClient?

    @Published private(set) var thumbnail: NSImage?

    private var fetch: Task<Shot, Error>?
    private var thumbStarted = false

    nonisolated var id: Int64 { shot.id }
    nonisolated var isRemote: Bool { client != nil }

    init(shot: Shot) {
        self.shot = shot
        self.dto = nil
        self.client = nil
    }

    init(dto: ShotDto, client: PeerClient) {
        self.dto = dto
        self.client = client
        self.shot = client.toLocalShot(dto, localPath: client.cachePath(for: dto).path)
    }

    /// Local: the shot as-is. Remote: one shared download task, started once
    /// and awaited by whoever needs the file — mouse-down starts it so a drag
    /// crossing the threshold usually finds the file already local.
    func materialize() -> Task<Shot, Error> {
        if let fetch { return fetch }
        let t: Task<Shot, Error>
        if let client, let dto {
            t = Task { try await client.ensureLocal(dto) }
        } else {
            let s = shot
            t = Task<Shot, Error> { s }
        }
        fetch = t
        return t
    }

    func loadThumbnail() {
        guard !thumbStarted else { return }
        thumbStarted = true

        if let client, let dto {
            // Remote tiles stream the peer's pre-scaled 448 px JPEG.
            Task { [weak self] in
                do {
                    let data = try await client.thumb(id: dto.id)
                    let img = await Task.detached { DecodedImage(img: NSImage(data: data)) }.value.img
                    if let self, let img { self.thumbnail = img }
                } catch {
                    Log.warn("archive thumb failed for \(dto.fileName): \(error.localizedDescription)")
                }
            }
            return
        }

        // Local: ImageIO downsample on a worker. Decoding ~200 ultrawide
        // PNGs on the main actor froze the whole window on Windows; the same
        // stall would take the shelf and hotkeys down here too.
        let path = shot.thumbPath
        Task { [weak self] in
            let img = await Task.detached {
                DecodedImage(img: ArchiveEntry.decodeImage(path: path, maxPixel: 448))
            }.value.img
            if let self {
                if let img {
                    self.thumbnail = img
                } else {
                    Log.warn("archive thumb failed for \(path): unreadable")
                }
            }
        }
    }

    /// ImageIO downsample — the decode happens here, off the main actor, not
    /// lazily at first draw. The preview path passes a large cap so a
    /// pathological capture can't allocate gigabytes.
    nonisolated static func decodeImage(path: String, maxPixel: Int) -> NSImage? {
        let url = URL(fileURLWithPath: path)
        guard let src = CGImageSourceCreateWithURL(url as CFURL, nil) else { return nil }
        let options: [CFString: Any] = [
            kCGImageSourceCreateThumbnailFromImageAlways: true,
            kCGImageSourceCreateThumbnailWithTransform: true,
            kCGImageSourceThumbnailMaxPixelSize: maxPixel,
        ]
        guard let cg = CGImageSourceCreateThumbnailAtIndex(src, 0, options as CFDictionary) else {
            return nil
        }
        return NSImage(cgImage: cg, size: NSSize(width: CGFloat(cg.width), height: CGFloat(cg.height)))
    }
}

/// Caption formats mirror the Windows tiles so the two archives read
/// identically. POSIX locale keeps month names stable across user locales —
/// the log and the wire are English-shaped everywhere else too.
enum ArchiveFmt {
    nonisolated(unsafe) static let tileStamp: DateFormatter = {
        let f = DateFormatter()
        f.locale = Locale(identifier: "en_US_POSIX")
        f.dateFormat = "MMM d, HH:mm"
        return f
    }()

    nonisolated(unsafe) static let previewStamp: DateFormatter = {
        let f = DateFormatter()
        f.locale = Locale(identifier: "en_US_POSIX")
        f.dateFormat = "MMM d, yyyy  HH:mm"
        return f
    }()
}
