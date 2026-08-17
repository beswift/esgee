import AppKit

/// The frozen-frame capture surface: one borderless window per NSScreen over
/// the already-grabbed image, so what the user aims at can't animate out from
/// under the cursor — the trick that makes every good screenshot tool feel
/// instant. Selection is tracked in Cocoa global points and may cross
/// displays.
///
///   Esc / right-click     cancel
///   drag                  commit the dragged rect
///   bare click            commit the clicked display's full frame
///   Return / Space        commit all displays (union rect)
///   1–9                   cancel into a delayed re-freeze (onDelayRequested)
///
/// Exactly one terminal callback fires, on the main actor, after all overlay
/// windows close.
@MainActor
final class OverlayController {
    var onCommit: ((_ rectPoints: CGRect) -> Void)?
    var onDelayRequested: ((_ seconds: Int) -> Void)?
    var onCancelled: (() -> Void)?

    private let frames: [FrozenDisplay]
    private var windows: [OverlayWindow] = []
    private var chromes: [OverlaySelectionView] = []
    private var finished = false

    // Selection state in global points. dragOrigin non-nil means a drag is
    // live; hover drives the idle crosshair guides only.
    private var dragOrigin: CGPoint?
    private var dragCurrent: CGPoint?
    private var hoverPoint: CGPoint?

    init(frames: [FrozenDisplay]) {
        self.frames = frames
    }

    func show() {
        guard windows.isEmpty, !finished else { return }

        for frame in frames {
            let window = OverlayWindow(contentRect: frame.framePoints,
                                       styleMask: .borderless,
                                       backing: .buffered, defer: false)
            window.isReleasedWhenClosed = false
            // .screenSaver puts the overlay above the menu bar and the Dock —
            // anything lower leaves strips of live UI the user can't select.
            window.level = .screenSaver
            window.isOpaque = true
            window.backgroundColor = .black
            window.hasShadow = false
            window.acceptsMouseMovedEvents = true
            window.collectionBehavior = [.canJoinAllSpaces, .fullScreenAuxiliary, .stationary]

            let container = NSView(frame: NSRect(origin: .zero, size: frame.framePoints.size))

            let imageView = NSImageView(frame: container.bounds)
            imageView.autoresizingMask = [.width, .height]
            imageView.imageScaling = .scaleAxesIndependently
            imageView.image = NSImage(cgImage: frame.image, size: frame.framePoints.size)
            container.addSubview(imageView)

            let chrome = OverlaySelectionView(display: frame, controller: self)
            chrome.frame = container.bounds
            chrome.autoresizingMask = [.width, .height]
            container.addSubview(chrome)

            window.contentView = container
            window.setFrame(frame.framePoints, display: true)
            window.makeFirstResponder(chrome)

            windows.append(window)
            chromes.append(chrome)
        }

        for window in windows { window.orderFrontRegardless() }

        // A menu-bar app isn't active when the hotkey fires; without
        // activation no overlay window can become key and Esc goes nowhere.
        NSApp.activate()
        let mouse = NSEvent.mouseLocation
        (windows.first { $0.frame.contains(mouse) } ?? windows.first)?
            .makeKeyAndOrderFront(nil)

        hoverPoint = mouse
        pushState()
    }

    func cancel() {
        finish(.cancelled)
    }

    // ---- events, forwarded by the per-display chrome views -----------------

    fileprivate func pointerDown(at point: CGPoint) {
        guard !finished else { return }
        dragOrigin = point
        dragCurrent = point
        pushState()
    }

    fileprivate func pointerDragged(to point: CGPoint) {
        guard !finished, dragOrigin != nil else { return }
        dragCurrent = point
        pushState()
    }

    fileprivate func pointerUp(at point: CGPoint) {
        guard !finished, let origin = dragOrigin else { return }
        dragCurrent = point

        // A sub-threshold drag is a click: take the clicked display whole.
        if abs(point.x - origin.x) < 4, abs(point.y - origin.y) < 4 {
            if let display = frames.first(where: { $0.framePoints.contains(point) }) {
                finish(.commit(display.framePoints))
            } else {
                finish(.cancelled)
            }
            return
        }

        if let selection = selectionRect {
            finish(.commit(selection))
        } else {
            finish(.cancelled)
        }
    }

    fileprivate func pointerMoved(to point: CGPoint) {
        guard !finished else { return }
        hoverPoint = point
        if dragOrigin == nil { pushState() }
    }

    fileprivate func key(_ event: NSEvent) {
        guard !finished else { return }
        switch event.keyCode {
        case 53: // Esc
            finish(.cancelled)
        case 36, 76, 49: // Return, keypad Enter, Space
            finish(.commit(unionRect))
        default:
            if let ch = event.charactersIgnoringModifiers?.first,
               let digit = ch.wholeNumberValue, (1...9).contains(digit) {
                finish(.delay(digit))
            }
        }
    }

    // ---- state --------------------------------------------------------------

    private var unionRect: CGRect {
        frames.reduce(CGRect.null) { $0.union($1.framePoints) }
    }

    private var selectionRect: CGRect? {
        guard let a = dragOrigin, let b = dragCurrent else { return nil }
        var rect = CGRect(x: min(a.x, b.x), y: min(a.y, b.y),
                          width: abs(b.x - a.x), height: abs(b.y - a.y))
        // Zero-size rects would make the composite throw for no user benefit.
        rect.size.width = max(1, rect.width)
        rect.size.height = max(1, rect.height)
        return rect
    }

    private func pushState() {
        var state = OverlayRenderState()
        state.selection = selectionRect
        state.hover = dragOrigin == nil ? hoverPoint : nil
        if let selection = state.selection {
            // The badge shows the pixel size the composite will actually
            // produce: points times the highest intersected scale.
            let scale = frames.filter { $0.framePoints.intersects(selection) }
                .map(\.scale).max() ?? 1
            let w = Int((selection.width * scale).rounded())
            let h = Int((selection.height * scale).rounded())
            state.badgeText = "\(w) × \(h)"
            let anchor = dragCurrent ?? selection.origin
            state.badgeDisplayID = frames.first { $0.framePoints.contains(anchor) }?.displayID
        }
        for chrome in chromes { chrome.apply(state) }
    }

    // ---- terminal -----------------------------------------------------------

    private enum Outcome {
        case commit(CGRect)
        case delay(Int)
        case cancelled
    }

    private func finish(_ outcome: Outcome) {
        // Exactly one terminal callback, ever — commit paths can race Esc when
        // both arrive in the same run-loop turn.
        guard !finished else { return }
        finished = true

        for window in windows { window.close() }
        windows.removeAll()
        chromes.removeAll()

        switch outcome {
        case .commit(let rect): onCommit?(rect)
        case .delay(let seconds): onDelayRequested?(seconds)
        case .cancelled: onCancelled?()
        }
    }
}

/// Borderless windows refuse key status by default; without key status the
/// overlay never sees Esc.
private final class OverlayWindow: NSWindow {
    override var canBecomeKey: Bool { true }
}

private struct OverlayRenderState {
    var selection: CGRect?
    var hover: CGPoint?
    var badgeText: String?
    var badgeDisplayID: CGDirectDisplayID?
}

/// The chrome layer over one display's frozen image: dim mask with a hole
/// punched at the selection, idle crosshair guides, size badge. Drawing-only —
/// all selection state lives in the controller because the selection can span
/// displays.
private final class OverlaySelectionView: NSView {
    private let display: FrozenDisplay
    private weak var controller: OverlayController?
    private var state = OverlayRenderState()

    // #5B8CFF — Theme.accent's value as a literal, so Capture doesn't grow a
    // dependency on the ShelfUI module for one stroke color.
    private let accent = NSColor(srgbRed: 91.0 / 255.0, green: 140.0 / 255.0,
                                 blue: 1.0, alpha: 1.0)

    init(display: FrozenDisplay, controller: OverlayController) {
        self.display = display
        self.controller = controller
        super.init(frame: NSRect(origin: .zero, size: display.framePoints.size))
    }

    required init?(coder: NSCoder) {
        fatalError("OverlaySelectionView is never decoded from a nib")
    }

    override var acceptsFirstResponder: Bool { true }

    // The first click on a freshly shown overlay must start the drag, not
    // just hand the window key status.
    override func acceptsFirstMouse(for event: NSEvent?) -> Bool { true }

    func apply(_ newState: OverlayRenderState) {
        state = newState
        needsDisplay = true
    }

    override func resetCursorRects() {
        addCursorRect(bounds, cursor: .crosshair)
    }

    override func updateTrackingAreas() {
        super.updateTrackingAreas()
        for area in trackingAreas { removeTrackingArea(area) }
        addTrackingArea(NSTrackingArea(
            rect: bounds,
            options: [.mouseMoved, .mouseEnteredAndExited, .activeAlways],
            owner: self, userInfo: nil))
    }

    override func mouseEntered(with event: NSEvent) {
        // Key status follows the pointer so Esc works on whichever display
        // the user is actually looking at.
        window?.makeKey()
    }

    override func mouseDown(with event: NSEvent) { controller?.pointerDown(at: global(event)) }
    override func mouseDragged(with event: NSEvent) { controller?.pointerDragged(to: global(event)) }
    override func mouseUp(with event: NSEvent) { controller?.pointerUp(at: global(event)) }
    override func mouseMoved(with event: NSEvent) { controller?.pointerMoved(to: global(event)) }
    override func rightMouseDown(with event: NSEvent) { controller?.cancel() }
    override func keyDown(with event: NSEvent) { controller?.key(event) }

    // ---- geometry -----------------------------------------------------------

    private func global(_ event: NSEvent) -> CGPoint {
        guard let window else { return .zero }
        return window.convertPoint(toScreen: event.locationInWindow)
    }

    private func toLocal(_ globalRect: CGRect) -> NSRect {
        guard let window else { return .zero }
        return convert(window.convertFromScreen(globalRect), from: nil)
    }

    private func toLocalPoint(_ globalPoint: CGPoint) -> NSPoint {
        toLocal(NSRect(origin: globalPoint, size: .zero)).origin
    }

    // ---- drawing ------------------------------------------------------------

    override func draw(_ dirtyRect: NSRect) {
        var hole: NSRect?
        let dim = NSBezierPath(rect: bounds)
        if let selection = state.selection {
            let local = toLocal(selection)
            if local.intersects(bounds) {
                hole = local
                dim.append(NSBezierPath(rect: local))
                dim.windingRule = .evenOdd
            }
        }
        NSColor.black.withAlphaComponent(0.45).setFill()
        dim.fill()

        if let hole {
            accent.setStroke()
            let outline = NSBezierPath(rect: hole.insetBy(dx: -0.5, dy: -0.5))
            outline.lineWidth = 1
            outline.stroke()
        } else if let hover = state.hover, display.framePoints.contains(hover) {
            // Idle crosshair guides, only on the display the pointer is on.
            let p = toLocalPoint(hover)
            NSColor(white: 1.0, alpha: 0.25).setStroke()
            let guides = NSBezierPath()
            guides.move(to: NSPoint(x: p.x + 0.5, y: 0))
            guides.line(to: NSPoint(x: p.x + 0.5, y: bounds.height))
            guides.move(to: NSPoint(x: 0, y: p.y + 0.5))
            guides.line(to: NSPoint(x: bounds.width, y: p.y + 0.5))
            guides.lineWidth = 1
            guides.stroke()
        }

        if let text = state.badgeText, state.badgeDisplayID == display.displayID, let hole {
            drawBadge(text, near: hole)
        }
    }

    private func drawBadge(_ text: String, near hole: NSRect) {
        let attributes: [NSAttributedString.Key: Any] = [
            .font: NSFont.systemFont(ofSize: 12, weight: .medium),
            .foregroundColor: NSColor(white: 1.0, alpha: 0.92),
        ]
        let string = NSAttributedString(string: text, attributes: attributes)
        let textSize = string.size()
        let badgeSize = NSSize(width: textSize.width + 16, height: textSize.height + 8)

        // Above the selection when there's room, tucked inside when there
        // isn't — same rule as the Windows badge.
        var origin = NSPoint(x: max(4, hole.minX), y: hole.maxY + 8)
        if origin.y + badgeSize.height > bounds.maxY - 4 {
            origin.y = max(4, hole.maxY - badgeSize.height - 8)
        }
        if origin.x + badgeSize.width > bounds.maxX - 4 {
            origin.x = bounds.maxX - 4 - badgeSize.width
        }

        let rect = NSRect(origin: origin, size: badgeSize)
        NSColor.black.withAlphaComponent(0.75).setFill()
        NSBezierPath(roundedRect: rect, xRadius: 6, yRadius: 6).fill()
        string.draw(at: NSPoint(x: rect.minX + 8, y: rect.minY + 4))
    }
}
