import AppKit

/// Borderless glass card that can still take key status — the PIN box needs a
/// field editor, and a window that can't become key never gets one.
private final class JoinCardWindow: NSWindow {
    var onEscape: (() -> Void)?
    override var canBecomeKey: Bool { true }
    override func keyDown(with event: NSEvent) {
        if event.keyCode == 53 { onEscape?(); return } // Esc
        super.keyDown(with: event)
    }
    // Esc inside the text field surfaces as cancelOperation, not keyDown.
    override func cancelOperation(_ sender: Any?) { onEscape?() }
}

/// Joining side of pairing: type the 6-digit PIN shown on the other machine.
/// Submit POSTs the PIN to every reachable candidate in parallel (online
/// tailnet nodes + manual Peers entries, 4 s each); the machine with an open
/// pairing window answers with the PeerToken, which onPaired persists — the
/// running app is fully paired with no restart and no settings editing.
/// Mirrors src/Esgee/Ui/PairingEnterWindow.cs.
@MainActor
final class PairJoinWindowController: NSObject {
    var onClosed: (() -> Void)?

    private let settings: Settings // snapshot; pairing is a one-shot flow
    private let onPaired: @MainActor (PairResult) -> Void
    private let window: JoinCardWindow
    private let pinField: NSTextField
    private let pairButton: NSButton
    private let status: NSTextField
    private var busy = false
    private var closed = false

    init(settings: Settings, onPaired: @escaping @MainActor (PairResult) -> Void) {
        self.settings = settings
        self.onPaired = onPaired

        let size = NSSize(width: 360, height: 320)
        let card = JoinCardWindow(contentRect: NSRect(origin: .zero, size: size),
                                  styleMask: .borderless, backing: .buffered, defer: false)
        window = card

        let field = NSTextField(string: "")
        field.font = .systemFont(ofSize: 30, weight: .bold)
        field.alignment = .center
        field.textColor = Theme.ink
        field.drawsBackground = false
        field.isBordered = false
        field.focusRingType = .none
        pinField = field

        let button = NSButton(title: "Pair", target: nil, action: nil)
        button.isBordered = false
        button.wantsLayer = true
        button.layer?.backgroundColor = Theme.accent.cgColor
        button.layer?.cornerRadius = 8
        button.attributedTitle = NSAttributedString(
            string: "Pair",
            attributes: [.foregroundColor: NSColor.white,
                         .font: NSFont.systemFont(ofSize: 13, weight: .semibold)])
        pairButton = button

        status = Self.label("", size: 12, color: Theme.inkMuted, wraps: true)

        super.init()

        card.isReleasedWhenClosed = false
        card.isOpaque = false
        card.backgroundColor = .clear
        card.hasShadow = true
        card.level = .floating
        card.isMovableByWindowBackground = true
        card.appearance = NSAppearance(named: .darkAqua)
        card.title = "esgee — pair with another machine"
        card.onEscape = { [weak self] in self?.close() }

        let content = NSView(frame: NSRect(origin: .zero, size: size))
        content.wantsLayer = true
        content.layer?.backgroundColor = Theme.surface.cgColor
        content.layer?.cornerRadius = 14
        content.layer?.borderWidth = 1
        content.layer?.borderColor = Theme.hairline.cgColor

        let headline = Self.label("Pair with another machine", size: 14,
                                  weight: .semibold, color: Theme.ink)
        headline.frame = NSRect(x: 24, y: size.height - 40, width: 260, height: 20)
        content.addSubview(headline)

        let closeButton = NSButton(title: "✕", target: self, action: #selector(closePressed))
        closeButton.isBordered = false
        closeButton.font = .systemFont(ofSize: 13)
        closeButton.contentTintColor = Theme.inkMuted
        closeButton.frame = NSRect(x: size.width - 24 - 22, y: size.height - 42, width: 22, height: 22)
        content.addSubview(closeButton)

        let hint = Self.label("On the other machine: the esgee menu bar icon → Peers → " +
                              "“Pair a new machine…”, then type the PIN it shows here.",
                              size: 12, color: Theme.inkMuted, wraps: true)
        hint.alignment = .center
        hint.frame = NSRect(x: 30, y: 214, width: 300, height: 56)
        content.addSubview(hint)

        // The box chrome carries the visual weight; the field itself is bare
        // so its focus ring and bezel don't fight the glass.
        let boxChrome = NSView(frame: NSRect(x: 85, y: 156, width: 190, height: 48))
        boxChrome.wantsLayer = true
        boxChrome.layer?.backgroundColor = Theme.surfaceHover.cgColor
        boxChrome.layer?.cornerRadius = 8
        boxChrome.layer?.borderWidth = 1
        boxChrome.layer?.borderColor = Theme.hairline.cgColor
        content.addSubview(boxChrome)

        field.frame = NSRect(x: 10, y: 6, width: 170, height: 36)
        field.delegate = self
        field.target = self
        field.action = #selector(submitPressed) // Return submits
        boxChrome.addSubview(field)

        button.frame = NSRect(x: (size.width - 96) / 2, y: 108, width: 96, height: 32)
        button.target = self
        button.action = #selector(submitPressed)
        content.addSubview(button)

        status.alignment = .center
        status.frame = NSRect(x: 30, y: 22, width: 300, height: 72)
        content.addSubview(status)

        card.contentView = content
    }

    func show() {
        window.center()
        window.makeKeyAndOrderFront(nil)
        NSApp.activate()
        window.makeFirstResponder(pinField)
    }

    func close() {
        guard !closed else { return }
        closed = true
        window.orderOut(nil)
        onClosed?()
    }

    @objc private func closePressed() { close() }

    // ---- submit ---------------------------------------------------------------

    @objc private func submitPressed() { submit() }

    private func submit() {
        guard !busy, !closed else { return }

        let pin = String(pinField.stringValue.filter(\.isNumber))
        guard pin.count == 6 else {
            status.stringValue = "Enter the 6-digit PIN shown on the other machine."
            return
        }

        busy = true
        pinField.isEnabled = false
        pairButton.isEnabled = false
        status.stringValue = "Looking for machines on your tailnet…"

        let snapshot = settings
        Task { [weak self] in
            let attempt = await Self.pairWithAny(pin: pin, settings: snapshot)
            self?.finish(attempt)
        }
    }

    private func finish(_ attempt: PeerClient.PairAttempt?) {
        guard !closed else { return }

        if let attempt, attempt.outcome == .paired, let paired = attempt.result {
            // Token and PIN values never reach the log — names and outcomes only.
            Log.info("peers: paired with '\(paired.machine)' at " +
                     "\(attempt.peer.baseURL.absoluteString) — token adopted")
            onPaired(paired)
            status.stringValue = "Paired with \(paired.machine) — peers are on."
            Task { [weak self] in
                try? await Task.sleep(nanoseconds: 2_000_000_000)
                self?.close()
            }
            return // stays busy: the window is about to close
        }

        status.stringValue = attempt?.outcome == .wrongPin
            ? "That PIN wasn’t accepted — double-check the digits and try again."
            : "No machine is offering a PIN right now. On the other machine: " +
              "menu bar → Peers → “Pair a new machine…”, then retry."

        busy = false
        pinField.isEnabled = true
        pairButton.isEnabled = true
        window.makeFirstResponder(pinField)
        pinField.currentEditor()?.selectAll(nil)
    }

    /// Posts the PIN to every candidate in parallel. At most one can accept
    /// (only one machine has a pairing window open); a "wrong pin" anywhere
    /// means a window WAS open and the PIN missed — precedence
    /// paired > wrongPin > noPairing. Candidate discovery shells out to
    /// tailscale, so it runs detached, never on the main actor.
    private nonisolated static func pairWithAny(pin: String, settings: Settings)
        async -> PeerClient.PairAttempt?
    {
        let candidates = await Task.detached {
            PeerClient.candidatePeers(settings: settings)
        }.value
        Log.info("peers: pairing — probing \(candidates.count) candidate(s)")
        if candidates.isEmpty { return nil }

        var attempts: [PeerClient.PairAttempt] = []
        await withTaskGroup(of: PeerClient.PairAttempt.self) { group in
            for candidate in candidates {
                group.addTask {
                    await PeerClient.tryPair(peer: candidate, pin: pin, timeout: 4)
                }
            }
            for await attempt in group { attempts.append(attempt) }
        }

        return attempts.first { $0.outcome == .paired }
            ?? attempts.first { $0.outcome == .wrongPin }
            ?? attempts.first
    }

    // ---- helpers ------------------------------------------------------------------

    private static func label(_ text: String, size: CGFloat,
                              weight: NSFont.Weight = .regular,
                              color: NSColor, wraps: Bool = false) -> NSTextField {
        let field = wraps ? NSTextField(wrappingLabelWithString: text)
                          : NSTextField(labelWithString: text)
        field.font = .systemFont(ofSize: size, weight: weight)
        field.textColor = color
        field.isSelectable = false
        return field
    }
}

// @preconcurrency: the SDK's isolation annotation on control delegates has
// moved between releases; the callback only ever runs on the main thread.
extension PairJoinWindowController: @preconcurrency NSTextFieldDelegate {
    /// Digits only, capped at 6 — enforced live so what's in the box is
    /// always exactly what submit will send.
    func controlTextDidChange(_ obj: Notification) {
        let digits = String(pinField.stringValue.filter(\.isNumber).prefix(6))
        if digits != pinField.stringValue {
            pinField.stringValue = digits
        }
    }
}
