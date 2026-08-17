import AppKit

/// Borderless glass card that can still take key status — borderless windows
/// refuse key by default, which would send Esc nowhere.
private final class HostCardWindow: NSWindow {
    var onEscape: (() -> Void)?
    override var canBecomeKey: Bool { true }
    override func keyDown(with event: NSEvent) {
        if event.keyCode == 53 { onEscape?(); return } // Esc
        super.keyDown(with: event)
    }
    override func cancelOperation(_ sender: Any?) { onEscape?() }
}

/// Host side of Bluetooth-style pairing: a small glass card with the large
/// one-time PIN and a countdown. While this window is open — and only then —
/// the peer server answers POST /pair; closing it (any path) takes /pair down
/// with it. Self-closes shortly after success, lockout, or expiry. Mirrors
/// src/Esgee/Ui/PairingWindow.cs so the two hosts read identically.
@MainActor
final class PairHostWindowController: NSObject {
    var onClosed: (() -> Void)?

    private let session: PairingSession
    private let server: PeerServer
    private let window: HostCardWindow
    private let headline: NSTextField
    private let pinLabel: NSTextField
    private let hint: NSTextField
    private let status: NSTextField
    private var tick: Timer?
    // settled = a terminal outcome is on screen and the delayed close is
    // armed; closed = the window is gone. Both guard re-entry because session
    // callbacks arrive from server workers and can race the tick.
    private var settled = false
    private var closed = false

    init(session: PairingSession, server: PeerServer) {
        self.session = session
        self.server = server

        let size = NSSize(width: 360, height: 244)
        let card = HostCardWindow(contentRect: NSRect(origin: .zero, size: size),
                                  styleMask: .borderless, backing: .buffered, defer: false)
        window = card

        headline = Self.label("Pair a new machine", size: 14, weight: .semibold,
                              color: Theme.ink)
        // Split 3+3 the way phones print one-time codes; the PIN is read
        // across a room and typed on another machine.
        pinLabel = Self.label("\(session.pin.prefix(3)) \(session.pin.suffix(3))",
                              size: 46, weight: .bold, color: Theme.ink)
        hint = Self.label("On your other machine, open the esgee menu bar icon → " +
                          "Peers → “Pair with another machine…” and enter this PIN.",
                          size: 12, weight: .regular, color: Theme.inkMuted, wraps: true)
        status = Self.label("", size: 12, weight: .regular, color: Theme.inkMuted)

        super.init()

        card.isReleasedWhenClosed = false
        card.isOpaque = false
        card.backgroundColor = .clear
        card.hasShadow = true
        card.level = .floating
        card.isMovableByWindowBackground = true
        card.appearance = NSAppearance(named: .darkAqua)
        card.title = "esgee — pair a new machine"
        card.onEscape = { [weak self] in self?.close() }

        let content = NSView(frame: NSRect(origin: .zero, size: size))
        content.wantsLayer = true
        content.layer?.backgroundColor = Theme.surface.cgColor
        content.layer?.cornerRadius = 14
        content.layer?.borderWidth = 1
        content.layer?.borderColor = Theme.hairline.cgColor

        headline.frame = NSRect(x: 24, y: size.height - 40, width: 260, height: 20)
        content.addSubview(headline)

        let closeButton = NSButton(title: "✕", target: self, action: #selector(closePressed))
        closeButton.isBordered = false
        closeButton.font = .systemFont(ofSize: 13)
        closeButton.contentTintColor = Theme.inkMuted
        closeButton.frame = NSRect(x: size.width - 24 - 22, y: size.height - 42, width: 22, height: 22)
        content.addSubview(closeButton)

        pinLabel.alignment = .center
        pinLabel.frame = NSRect(x: 0, y: 136, width: size.width, height: 56)
        content.addSubview(pinLabel)

        hint.alignment = .center
        hint.frame = NSRect(x: 30, y: 60, width: 300, height: 64)
        content.addSubview(hint)

        status.alignment = .center
        status.frame = NSRect(x: 30, y: 24, width: 300, height: 18)
        content.addSubview(status)

        card.contentView = content
    }

    /// The window IS the /pair switch: registered on show, deregistered (and
    /// the session killed) the moment it closes.
    func show() {
        server.beginPairing(session)

        // Session events arrive on server worker threads; hop to the main
        // actor before touching any label.
        session.onSucceeded = { [weak self] machine in
            Task { @MainActor in self?.succeeded(machine) }
        }
        session.onWrongGuess = { [weak self] failures in
            Task { @MainActor in self?.wrongGuess(failures) }
        }
        session.onLockedOut = { [weak self] in
            Task { @MainActor in self?.lockedOut() }
        }

        tick = Timer.scheduledTimer(withTimeInterval: 0.25, repeats: true) { [weak self] _ in
            // Scheduled on the main run loop; assumeIsolated states that fact.
            MainActor.assumeIsolated { self?.tickDown() }
        }
        tickDown()

        window.center()
        window.makeKeyAndOrderFront(nil)
        NSApp.activate()
    }

    /// Idempotent; any path to dismissal funnels here so /pair can never
    /// outlive the card.
    func close() {
        guard !closed else { return }
        closed = true
        tick?.invalidate()
        tick = nil
        session.close()
        server.endPairing(session)
        window.orderOut(nil)
        onClosed?()
    }

    @objc private func closePressed() { close() }

    // ---- countdown ------------------------------------------------------------

    private func tickDown() {
        guard !settled, !closed else { return }
        let left = session.expiresAt.timeIntervalSinceNow
        if left <= 0 {
            status.stringValue = "PIN expired — close and try again."
            settle(closeAfter: 2)
            return
        }
        status.stringValue = "PIN expires in \(Self.clock(left))"
    }

    // ---- session outcomes -------------------------------------------------------

    private func succeeded(_ machine: String) {
        guard !settled, !closed else { return }
        headline.stringValue = "Paired"
        pinLabel.stringValue = "✓"
        hint.stringValue = "\(machine) can now browse and sync with this machine's archive."
        status.stringValue = ""
        settle(closeAfter: 2.5)
    }

    private func wrongGuess(_ failures: Int) {
        // The 5th wrong guess arrives as onLockedOut right behind this.
        guard !settled, !closed, failures < PairingSession.maxAttempts else { return }
        let left = max(0, session.expiresAt.timeIntervalSinceNow)
        status.stringValue = "Wrong PIN received (\(failures)/\(PairingSession.maxAttempts)) — " +
                             "PIN expires in \(Self.clock(left))"
    }

    private func lockedOut() {
        guard !settled, !closed else { return }
        headline.stringValue = "Pairing cancelled"
        pinLabel.stringValue = "✕"
        hint.stringValue = "Too many wrong attempts. Open “Pair a new machine…” " +
                           "again for a fresh PIN."
        status.stringValue = ""
        settle(closeAfter: 3)
    }

    /// Terminal state reached: kill the session NOW (so /pair goes dark
    /// immediately), leave the outcome on screen briefly, then close.
    private func settle(closeAfter delay: TimeInterval) {
        settled = true
        session.close()
        server.endPairing(session)
        Task { [weak self] in
            try? await Task.sleep(nanoseconds: UInt64(delay * 1_000_000_000))
            self?.close()
        }
    }

    // ---- helpers -----------------------------------------------------------------

    private static func clock(_ interval: TimeInterval) -> String {
        let total = max(0, Int(interval))
        return String(format: "%d:%02d", total / 60, total % 60)
    }

    private static func label(_ text: String, size: CGFloat, weight: NSFont.Weight,
                              color: NSColor, wraps: Bool = false) -> NSTextField {
        let field = wraps ? NSTextField(wrappingLabelWithString: text)
                          : NSTextField(labelWithString: text)
        field.font = .systemFont(ofSize: size, weight: weight)
        field.textColor = color
        field.isSelectable = false
        return field
    }
}
