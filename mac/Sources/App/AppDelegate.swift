import AppKit
import CoreGraphics

/// Identity the peer layer reports on /ping and stamps into `origin` fields.
enum AppInfo {
    /// Local builds report 0.0.0 exactly like the Windows build — a version
    /// is real only when CI stamps it from a release tag.
    static let version: String =
        Bundle.main.infoDictionary?["CFBundleShortVersionString"] as? String ?? "0.0.0"

    /// Hostname sans ".local" — the closest analogue of Windows'
    /// Environment.MachineName, and the shape tailnet node names take.
    static let machineName: String = {
        var name = ProcessInfo.processInfo.hostName
        if name.hasSuffix(".local") {
            name = String(name.dropLast(".local".count))
        }
        return name.isEmpty ? "mac" : name
    }()
}

/// Wiring only: owns the object graph and the hand-offs between modules.
/// The menu bar surface lives in MenuBarController; everything it can do is
/// exposed here as a named method so the menu never reaches into private
/// capture/peer state directly.
@MainActor
final class AppDelegate: NSObject, NSApplicationDelegate {
    private var settings: SettingsStore!
    private var store: ShotStore!
    private var watcher: ClipboardWatcher!
    private var shelf: ShelfPanelController!
    private var capture: CaptureController!
    private var hotkeys: HotkeyManager!
    private var ocr: OcrIndexer?
    private var sync: SyncQueue?
    private var peerServer: PeerServer?
    private var serverTask: Task<PeerServer?, Never>?
    private var archive: ArchiveWindowController?
    private var pairHost: PairHostWindowController?
    private var pairJoin: PairJoinWindowController?
    private var menuBar: MenuBarController?
    private var screenAccess = false

    func applicationDidFinishLaunching(_ notification: Notification) {
        // One resident instance, or two watchers would each save every
        // capture. Same rule as the Windows singleton mutex.
        let bundleId = Bundle.main.bundleIdentifier ?? "com.esgee.mac"
        let others = NSRunningApplication.runningApplications(withBundleIdentifier: bundleId)
            .filter { $0 != NSRunningApplication.current }
        if !others.isEmpty {
            Log.info("another instance is already running; exiting")
            NSApp.terminate(nil)
            return
        }

        // A menu-bar app should survive a bad capture; at minimum the crash
        // must reach the log the way it does on Windows.
        NSSetUncaughtExceptionHandler { exception in
            Log.error("unhandled (objc): \(exception)")
        }

        settings = SettingsStore(Settings.load())

        do {
            store = try ShotStore(root: URL(fileURLWithPath: settings.current.archiveRoot,
                                            isDirectory: true))
        } catch {
            Log.error("archive store failed to open: \(error)")
            let alert = NSAlert()
            alert.messageText = "esgee can't open its archive"
            alert.informativeText =
                "The archive at \(settings.current.archiveRoot) could not be opened. " +
                "See esgee.log for details."
            alert.runModal()
            NSApp.terminate(nil)
            return
        }

        probeScreenRecordingAccess()

        // The ignore hook must exist before anything can write the
        // pasteboard, or a copy would loop back in as a fresh capture.
        watcher = ClipboardWatcher()
        let beforePasteboardWrite: @MainActor () -> Void = { [weak self] in
            self?.watcher?.ignoreNextChange()
        }

        shelf = ShelfPanelController(settings: settings,
                                     beforePasteboardWrite: beforePasteboardWrite)
        capture = CaptureController(store: store, shelf: shelf, settings: settings,
                                    beforePasteboardWrite: beforePasteboardWrite)

        // Fan-out for every capture that lands, whatever the source: OCR and
        // push sync both ride behind non-blocking enqueues.
        capture.onShotSaved = { [weak self] shot in
            self?.ocr?.enqueue(shot)
            self?.sync?.enqueue(shot.id)
        }

        // System-capture ride-along: whatever puts an image on the pasteboard
        // feeds the exact pipeline our own hotkeys feed.
        watcher.onImage = { [weak capture] png, size, takenAt in
            guard let capture else { return }
            Task.detached(priority: .userInitiated) {
                do {
                    _ = try capture.save(png: png, size: size, takenAt: takenAt)
                } catch {
                    Log.error("capture pipeline failed: \(error)")
                }
            }
        }

        hotkeys = HotkeyManager(bindings: [
            (settings.current.regionHotkey, .region),
            (settings.current.fullscreenHotkey, .screen),
            (settings.current.lastRegionHotkey, .last),
            (settings.current.timerHotkey, .timer),
            (settings.current.archiveHotkey, .archive),
        ]) { [weak self] action in
            self?.perform(action)
        }

        if settings.current.ocrEnabled {
            ocr = OcrIndexer(store: store)
            ocr?.enqueueBacklog()
        }

        startPeers()

        menuBar = MenuBarController(app: self, settings: settings,
                                    hotkeys: hotkeys, screenAccess: screenAccess)

        Log.info("esgee v\(AppInfo.version) up; archiving to \(store.root.path)")
    }

    func applicationSupportsSecureRestorableState(_ app: NSApplication) -> Bool { true }

    func applicationWillTerminate(_ notification: Notification) {
        peerServer?.stop()
        sync?.shutdown()
        ocr?.shutdown()
        hotkeys?.unregisterAll()
        watcher?.stop()
        store?.close()
        Log.info("esgee down")
    }

    // ---- actions -------------------------------------------------------------

    private func perform(_ action: HotkeyAction) {
        switch action {
        case .region: capture.beginRegion()
        case .screen: capture.beginFullscreen()
        case .last: capture.beginLastRegion()
        case .timer: capture.beginTimed()
        case .archive: openArchiveWindow()
        }
    }

    func openArchiveWindow() {
        if archive == nil {
            archive = ArchiveWindowController(
                store: store, settings: settings,
                beforePasteboardWrite: { [weak self] in self?.watcher?.ignoreNextChange() })
            archive?.onClosed = { [weak self] in self?.archive = nil }
        }
        archive?.showWindow()
        NSApp.activate()
    }

    // ---- menu surface ----------------------------------------------------------
    // MenuBarController drives these. Thin by design: the menu is a second
    // entry point to the same verbs the hotkeys hit, never a second code path.

    func captureRegion() { capture.beginRegion() }
    func captureScreen() { capture.beginFullscreen() }
    func captureLast() { capture.beginLastRegion() }
    func captureTimed() { capture.beginTimed() }
    func clearShelf() { shelf.clearAll() }

    func pairNewMachine() { Task { await self.openPairNewMachine() } }
    func pairWithMachine() { openPairWithMachine() }

    var peerServerAddress: String? { peerServer?.boundAddress }

    var syncStatus: (target: String, pending: Int, offline: Bool)? {
        guard let sync else { return nil }
        return (sync.target, sync.pending, sync.offline)
    }

    /// Peers off: server (and any open pairing window) down, zero sockets
    /// again. The token is kept so pairing again is instant.
    func disablePeers() {
        settings.update { $0.peersEnabled = false }
        stopServer()
        menuBar?.peersChanged()
        Log.info("peers: disabled from tray (token kept; pair again any time)")
    }

    func openArchiveFolder() {
        _ = NSWorkspace.shared.open(store.root)
    }

    func openSettingsFile() {
        // TextEdit by explicit URL, deliberately: the default .json handler
        // is often an IDE whose cold start reads as "nothing happened" — the
        // same reason Windows opens notepad.
        let textEdit = URL(fileURLWithPath: "/System/Applications/TextEdit.app")
        NSWorkspace.shared.open([Settings.fileURL], withApplicationAt: textEdit,
                                configuration: NSWorkspace.OpenConfiguration()) { _, error in
            if let error {
                Log.warn("open settings failed: \(error.localizedDescription)")
            }
        }
    }

    // ---- Screen Recording (TCC) ------------------------------------------------

    /// The single worst first-run experience on macOS, handled head-on: probe
    /// at launch, prompt once, and after a denial the menu carries a repair
    /// link — macOS will never re-prompt on its own (docs/MAC.md).
    private func probeScreenRecordingAccess() {
        if CGPreflightScreenCaptureAccess() {
            screenAccess = true
            return
        }
        Log.warn("screen recording not granted; requesting (macOS prompts once, ever)")
        screenAccess = CGRequestScreenCaptureAccess()
        Log.info("screen recording after request: \(screenAccess ? "granted" : "denied")")
    }

    func openScreenRecordingSettings() {
        let url = URL(string:
            "x-apple.systempreferences:com.apple.preference.security?Privacy_ScreenCapture")!
        NSWorkspace.shared.open(url)
    }

    // ---- peers ---------------------------------------------------------------

    /// Peer layer, entirely opt-in. With peersEnabled=false and no sync
    /// target this opens zero sockets and starts zero tasks.
    private func startPeers() {
        if settings.current.peersEnabled && settings.current.peerToken.isEmpty {
            settings.update { $0.peerToken = Self.mintToken() }
            Log.info("peers: generated PeerToken (copy it into settings.json on your other machines)")
        }

        if settings.current.peersEnabled {
            Task { _ = await self.ensureServer() }
        }

        if !settings.current.syncTargetPeer.isEmpty {
            guard !settings.current.peerToken.isEmpty else {
                Log.warn("sync: SyncTargetPeer set but no PeerToken; sync disabled")
                return
            }
            let queue = SyncQueue(store: store, settings: settings.current)
            sync = queue
            Task.detached { queue.enqueueBacklog() }
            Log.info("sync: pushing new captures to \(settings.current.syncTargetPeer)")
        }
    }

    /// Starts the peer server exactly once, off the main actor (binding and
    /// address discovery do I/O). A failed start clears the task so a later
    /// attempt — e.g. after the user starts Tailscale — retries cleanly.
    private func ensureServer() async -> PeerServer? {
        if let peerServer { return peerServer }
        if serverTask == nil {
            let store = self.store!
            let token = settings.current.peerToken
            let port = settings.current.peerPort
            serverTask = Task.detached {
                PeerServer.tryStart(store: store, token: token, port: port)
            }
        }
        let server = await serverTask!.value
        if server == nil {
            serverTask = nil // allow retry
        } else if peerServer == nil {
            peerServer = server
        }
        return peerServer
    }

    /// Tears the peer server down (pairing window included). The token always
    /// survives a disable so re-enabling doesn't force a re-pair.
    private func stopServer() {
        pairHost?.close()
        pairHost = nil
        peerServer?.stop()
        peerServer = nil
        serverTask = nil
    }

    /// "Pair a new machine…": this machine shows the PIN. First use is the
    /// enable switch — it mints the token, flips peersEnabled, and brings the
    /// server up without anyone editing settings.json by hand.
    private func openPairNewMachine() async {
        if pairHost != nil { return }

        if settings.current.peerToken.isEmpty {
            settings.update { $0.peerToken = Self.mintToken() }
            Log.info("peers: generated PeerToken (first pairing)")
        }
        if !settings.current.peersEnabled {
            settings.update { $0.peersEnabled = true }
            Log.info("peers: enabled (first pairing)")
        }

        guard let server = await ensureServer() else {
            let alert = NSAlert()
            alert.messageText = "esgee — pairing"
            alert.informativeText =
                "Pairing needs Tailscale running on this Mac — the peer API binds " +
                "only to the Tailscale address.\n\nStart Tailscale and try again; " +
                "esgee.log has details."
            alert.runModal()
            return
        }

        let host = PairHostWindowController(session: PairingSession(), server: server)
        host.onClosed = { [weak self] in self?.pairHost = nil }
        pairHost = host
        host.show()
    }

    /// "Pair with another machine…": this machine types the PIN.
    private func openPairWithMachine() {
        if pairJoin != nil { return }
        let join = PairJoinWindowController(settings: settings.current) { [weak self] pair in
            self?.applyPairedToken(pair)
        }
        join.onClosed = { [weak self] in self?.pairJoin = nil }
        pairJoin = join
        join.show()
    }

    /// A pairing succeeded: persist the received token, flip peers on, and
    /// bring the server up (or bounce it onto the new token) in-process —
    /// fully paired with no restart.
    private func applyPairedToken(_ pair: PairResult) {
        let tokenChanged = settings.current.peerToken != pair.token
        settings.update {
            $0.peerToken = pair.token
            $0.peersEnabled = true
        }
        Log.info("peers: paired with '\(pair.machine)' — peers enabled, token " +
                 (tokenChanged ? "adopted" : "unchanged") + ", settings saved")

        if tokenChanged && (peerServer != nil || serverTask != nil) {
            stopServer() // old token is dead; restart on the adopted one
        }
        Task { _ = await self.ensureServer() }
        menuBar?.peersChanged() // next menu open recounts the fleet
    }

    /// 24 random bytes as uppercase hex — same mint as Windows, so tokens are
    /// interchangeable across the mesh.
    private static func mintToken() -> String {
        var bytes = [UInt8](repeating: 0, count: 24)
        for i in bytes.indices {
            bytes[i] = UInt8.random(in: .min ... .max)
        }
        return bytes.map { String(format: "%02X", $0) }.joined()
    }

}
