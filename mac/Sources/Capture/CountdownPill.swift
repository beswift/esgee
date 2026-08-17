import AppKit

/// The delay-capture countdown: a small click-through pill, top-center of the
/// main screen, so the user watches the fuse while arming the menu/hover
/// state they're trying to photograph. Non-activating and mouse-transparent —
/// interacting with the pill would disturb the very state being armed.
@MainActor
final class CountdownPill {
    private let panel: NSPanel
    private let label: NSTextField

    init() {
        let size = NSSize(width: 92, height: 58)
        let panel = NSPanel(contentRect: NSRect(origin: .zero, size: size),
                            styleMask: [.nonactivatingPanel, .borderless],
                            backing: .buffered, defer: false)
        panel.isReleasedWhenClosed = false
        panel.level = .floating
        panel.ignoresMouseEvents = true
        panel.isOpaque = false
        panel.backgroundColor = .clear
        panel.hasShadow = false
        panel.hidesOnDeactivate = false
        // The fuse must stay visible when the user flips into a full-screen
        // app to arm the state they want captured.
        panel.collectionBehavior = [.canJoinAllSpaces, .fullScreenAuxiliary, .stationary]

        let back = NSView(frame: NSRect(origin: .zero, size: size))
        back.wantsLayer = true
        back.layer?.backgroundColor = NSColor(white: 0.06, alpha: 0.92).cgColor
        back.layer?.cornerRadius = 14
        back.layer?.borderWidth = 1
        back.layer?.borderColor = NSColor(white: 1.0, alpha: 0.12).cgColor

        let label = NSTextField(labelWithString: "")
        label.font = .systemFont(ofSize: 34, weight: .semibold)
        label.textColor = NSColor(white: 1.0, alpha: 0.92)
        label.alignment = .center
        label.frame = NSRect(x: 0, y: 7, width: size.width, height: 44)
        label.autoresizingMask = [.width]
        back.addSubview(label)

        panel.contentView = back
        self.panel = panel
        self.label = label
    }

    func show() {
        if let screen = NSScreen.main {
            // 24 pt below the top of the visible frame — under the menu bar,
            // out of the way of what's being photographed.
            let visible = screen.visibleFrame
            let frame = panel.frame
            panel.setFrameOrigin(NSPoint(x: visible.midX - frame.width / 2,
                                         y: visible.maxY - 24 - frame.height))
        }
        panel.orderFrontRegardless()
    }

    func setRemaining(_ seconds: Int) {
        label.stringValue = String(seconds)
    }

    func close() {
        panel.orderOut(nil)
    }
}
