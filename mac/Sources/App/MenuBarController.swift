import AppKit
import CoreGraphics
import ServiceManagement
import Sparkle

/// The real menu bar item: every capture verb the hotkeys own, the archive,
/// the peers lifecycle, and the app chrome (linger, login item, updates,
/// quit). Mirrors the Windows tray item for item — minus recording, which
/// Mac v1 deliberately does not ship — so nobody swapping platforms has to
/// re-learn where anything lives.
@MainActor
final class MenuBarController: NSObject, @preconcurrency NSMenuDelegate {
    private weak var app: AppDelegate?
    private let settings: SettingsStore
    private let hotkeys: HotkeyManager

    private let statusItem: NSStatusItem
    private let menu = NSMenu()

    // Rows the delegate refreshes on every open. Autoenable is off — the
    // Windows tray manages enabled/checked state by hand and this mirrors it.
    private var warningItem: NSMenuItem!
    private var warningSeparator: NSMenuItem!
    private var peersStateItem: NSMenuItem!
    private var peersDetailItem: NSMenuItem!
    private var peersDisableItem: NSMenuItem!
    private var lingerItem: NSMenuItem!
    private var loginItem: NSMenuItem!

    private var screenAccess: Bool
    // Reachable-archive count from a throttled background discovery, so
    // opening the menu never blocks on the network (same 20 s throttle as
    // Windows). -1 = never counted.
    private var lastPeerCount = -1
    private var lastPeerCountAt = Date.distantPast

    /// Sparkle 2, the documented wiring — but only STARTED when the build
    /// carries a signing key (CI injects SUPublicEDKey; local builds have
    /// none). Starting the updater keyless doesn't just log — it puts an
    /// "can't check for updates" alert in the user's face at launch. Same
    /// policy as Windows: "update checks off: not a managed install".
    private static var hasUpdateFeed: Bool {
        (Bundle.main.object(forInfoDictionaryKey: "SUPublicEDKey") as? String)?.isEmpty == false
    }
    private let updater = SPUStandardUpdaterController(startingUpdater: hasUpdateFeed,
                                                       updaterDelegate: nil,
                                                       userDriverDelegate: nil)

    init(app: AppDelegate, settings: SettingsStore, hotkeys: HotkeyManager,
         screenAccess: Bool) {
        self.app = app
        self.settings = settings
        self.hotkeys = hotkeys
        self.screenAccess = screenAccess
        self.statusItem = NSStatusBar.system.statusItem(withLength: NSStatusItem.squareLength)
        super.init()

        menu.delegate = self
        menu.autoenablesItems = false
        buildMenu()
        statusItem.menu = menu
        applyIcon()

        statusItem.button?.toolTip =
            hotkeys.bound.first(where: { $0.action == .region }).map {
                "esgee — \($0.chord) to capture"
            } ?? "esgee — screenshots land here"
    }

    // ---- construction ---------------------------------------------------------

    private func buildMenu() {
        // Unmistakable, per the design: never fail silently on the first
        // hotkey press (docs/MAC.md "Screen Recording permission").
        warningItem = NSMenuItem(title: "esgee can't see your screen — open settings…",
                                 action: #selector(openScreenSettings), keyEquivalent: "")
        warningItem.target = self
        menu.addItem(warningItem)
        warningSeparator = NSMenuItem.separator()
        menu.addItem(warningSeparator)

        addAction(withChord("Capture region", .region), #selector(doRegion))
        addAction(withChord("Capture screen", .screen), #selector(doScreen))
        addAction(withChord("Repeat last region", .last), #selector(doLast))
        let fuse = min(max(settings.current.timerSeconds, 1), 60)
        addAction(withChord("Timed capture (\(fuse)s)", .timer), #selector(doTimed))
        menu.addItem(.separator())

        addAction(withChord("Search archive…", .archive), #selector(doArchive))
        addAction("Open archive folder", #selector(doArchiveFolder))
        menu.addItem(buildPeersItem())
        addAction("Clear shelf", #selector(doClearShelf))
        menu.addItem(.separator())

        lingerItem = NSMenuItem(title: "Cards linger for", action: nil, keyEquivalent: "")
        let lingerMenu = NSMenu()
        lingerMenu.autoenablesItems = false
        for seconds in [4, 8, 15, 30] {
            let choice = NSMenuItem(title: "\(seconds)s",
                                    action: #selector(lingerPicked(_:)), keyEquivalent: "")
            choice.target = self
            choice.tag = seconds
            lingerMenu.addItem(choice)
        }
        lingerItem.submenu = lingerMenu
        menu.addItem(lingerItem)

        loginItem = NSMenuItem(title: "Start at login",
                               action: #selector(toggleLogin), keyEquivalent: "")
        loginItem.target = self
        menu.addItem(loginItem)

        addAction("Edit settings", #selector(doEditSettings))

        // Dev builds show the version but can't check — a nil action renders
        // the row disabled, mirroring the Windows "not a managed install" line.
        let update = NSMenuItem(title: "Check for updates  (v\(AppInfo.version))",
                                action: Self.hasUpdateFeed
                                    ? #selector(SPUStandardUpdaterController.checkForUpdates(_:))
                                    : nil,
                                keyEquivalent: "")
        update.target = Self.hasUpdateFeed ? updater : nil
        menu.addItem(update)
        menu.addItem(.separator())

        addAction("Quit esgee", #selector(doQuit))
    }

    /// The Peers submenu: live on/off state, the serving/sync detail line,
    /// both halves of PIN pairing, and the off switch.
    private func buildPeersItem() -> NSMenuItem {
        let root = NSMenuItem(title: "Peers", action: nil, keyEquivalent: "")
        let sub = NSMenu()
        sub.autoenablesItems = false

        peersStateItem = NSMenuItem(title: "Peers: off", action: nil, keyEquivalent: "")
        peersStateItem.isEnabled = false
        sub.addItem(peersStateItem)

        peersDetailItem = NSMenuItem(title: "", action: nil, keyEquivalent: "")
        peersDetailItem.isEnabled = false
        peersDetailItem.isHidden = true
        sub.addItem(peersDetailItem)

        sub.addItem(.separator())

        let host = NSMenuItem(title: "Pair a new machine…",
                              action: #selector(doPairHost), keyEquivalent: "")
        host.target = self
        sub.addItem(host)

        let join = NSMenuItem(title: "Pair with another machine…",
                              action: #selector(doPairJoin), keyEquivalent: "")
        join.target = self
        sub.addItem(join)

        sub.addItem(.separator())

        peersDisableItem = NSMenuItem(title: "Disable peers",
                                      action: #selector(doDisablePeers), keyEquivalent: "")
        peersDisableItem.target = self
        sub.addItem(peersDisableItem)

        root.submenu = sub
        return root
    }

    /// Chord suffix, same shape as the Windows tray: "Capture region  (Ctrl+Shift+S)".
    /// Only chords that actually registered earn a label — a claimed-elsewhere
    /// chord in the menu would be a lie.
    private func withChord(_ label: String, _ action: HotkeyAction) -> String {
        if let chord = hotkeys.bound.first(where: { $0.action == action })?.chord {
            return "\(label)  (\(chord))"
        }
        return label
    }

    private func addAction(_ title: String, _ selector: Selector) {
        let item = NSMenuItem(title: title, action: selector, keyEquivalent: "")
        item.target = self
        menu.addItem(item)
    }

    // ---- live state -------------------------------------------------------------

    /// Peers were enabled/disabled or a pairing landed: forget the stale
    /// machine count so the next menu open recounts immediately.
    func peersChanged() {
        lastPeerCount = -1
        lastPeerCountAt = .distantPast
        refreshPeersStatus()
    }

    func menuNeedsUpdate(_ menu: NSMenu) {
        guard menu === self.menu else { return }

        // TCC can flip while we run (System Settings toggle); re-probe each
        // open so the warning clears without a relaunch when macOS allows it.
        let access = CGPreflightScreenCaptureAccess()
        if access != screenAccess {
            screenAccess = access
            applyIcon()
        }
        warningItem.isHidden = screenAccess
        warningSeparator.isHidden = screenAccess

        refreshPeersStatus()
        kickPeerCount()

        for item in lingerItem.submenu?.items ?? [] {
            item.state = item.tag == settings.current.lingerSeconds ? .on : .off
        }
        loginItem.state = SMAppService.mainApp.status == .enabled ? .on : .off
    }

    private func refreshPeersStatus() {
        let current = settings.current

        if !current.peersEnabled {
            peersStateItem.title = "Peers: off"
        } else if lastPeerCount < 0 {
            peersStateItem.title = "Peers: on"
        } else if lastPeerCount == 1 {
            peersStateItem.title = "Peers: on (1 machine)"
        } else {
            peersStateItem.title = "Peers: on (\(lastPeerCount) machines)"
        }
        peersDisableItem.isEnabled = current.peersEnabled

        var parts: [String] = []
        if let address = app?.peerServerAddress {
            parts.append("serving on \(address)")
        }
        if let sync = app?.syncStatus {
            let state = sync.offline ? "offline, \(sync.pending) queued"
                : sync.pending > 0 ? "\(sync.pending) pending"
                : "up to date"
            parts.append("sync to \(sync.target): \(state)")
        }
        let text = parts.joined(separator: "  ·  ")
        peersDetailItem.title = text
        peersDetailItem.isHidden = text.isEmpty
    }

    /// Count reachable archives in the background (throttled — discovery
    /// probes the tailnet). The label updates in place when it lands.
    private func kickPeerCount() {
        let snapshot = settings.current
        guard snapshot.peersEnabled, !snapshot.peerToken.isEmpty,
              Date().timeIntervalSince(lastPeerCountAt) > 20 else { return }
        lastPeerCountAt = Date()

        Task.detached { [weak self] in
            let found = await PeerClient.discover(settings: snapshot)
            await MainActor.run { [weak self] in
                guard let self else { return }
                self.lastPeerCount = found.count
                self.refreshPeersStatus()
            }
        }
    }

    private func applyIcon() {
        let symbol = screenAccess ? "camera.viewfinder" : "exclamationmark.triangle.fill"
        let description = screenAccess ? "esgee" : "esgee — no screen access"
        statusItem.button?.image = NSImage(systemSymbolName: symbol,
                                           accessibilityDescription: description)
    }

    // ---- actions ------------------------------------------------------------------

    @objc private func doRegion() { app?.captureRegion() }
    @objc private func doScreen() { app?.captureScreen() }
    @objc private func doLast() { app?.captureLast() }
    @objc private func doTimed() { app?.captureTimed() }
    @objc private func doArchive() { app?.openArchiveWindow() }
    @objc private func doArchiveFolder() { app?.openArchiveFolder() }
    @objc private func doClearShelf() { app?.clearShelf() }
    @objc private func doPairHost() { app?.pairNewMachine() }
    @objc private func doPairJoin() { app?.pairWithMachine() }
    @objc private func doDisablePeers() { app?.disablePeers() }
    @objc private func doEditSettings() { app?.openSettingsFile() }
    @objc private func openScreenSettings() { app?.openScreenRecordingSettings() }
    @objc private func doQuit() { NSApp.terminate(nil) }

    @objc private func lingerPicked(_ sender: NSMenuItem) {
        // The shelf reads lingerSeconds at each push, so persisting is the
        // whole job — no live object to poke.
        settings.update { $0.lingerSeconds = sender.tag }
    }

    @objc private func toggleLogin() {
        do {
            if SMAppService.mainApp.status == .enabled {
                try SMAppService.mainApp.unregister()
            } else {
                try SMAppService.mainApp.register()
            }
        } catch {
            // Ad-hoc-signed dev builds can be refused; the checkmark simply
            // stays where the system says it is.
            Log.warn("launch at login toggle failed: \(error.localizedDescription)")
        }
        loginItem.state = SMAppService.mainApp.status == .enabled ? .on : .off
    }
}
