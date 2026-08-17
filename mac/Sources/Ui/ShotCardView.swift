import AppKit
import ImageIO
import QuartzCore

/// ~2x the card's 184 pt display width — enough for a crisp thumbnail on a
/// HiDPI panel, nowhere near the cost of decoding a full ultrawide grab.
/// File-scope because the decode runs off the main actor and must not touch
/// the card's main-actor statics.
private let thumbDecodeWidth = 392

/// Overlay pieces that must never win hit testing: the copy flash, the video
/// badge, and the timer track all sit above the thumbnail, and any of them
/// swallowing a click would break "click the card = copy".
private final class StaticView: NSView {
    override func hitTest(_ point: NSPoint) -> NSView? { nil }
}

/// One shelf card. Mirrors src/Esgee/Ui/ShotCard.xaml(.cs): linger countdown,
/// hover chrome (copy / reveal / pin / dismiss), click-to-copy with a flash,
/// drag-out past 4 pt, right-click dismiss. The card's height is fixed at
/// init from the shot's known pixel dimensions so the shelf can lay out
/// before the thumbnail has decoded.
@MainActor
final class ShotCardView: NSView {
    // Contract numbers (SPEC.md "Numbers that are contract").
    static let outerWidth: CGFloat = 196          // content 184 + 6 pt padding each side
    private static let contentWidth: CGFloat = 184
    private static let padding: CGFloat = 6
    private static let dragThreshold: CGFloat = 4
    private static let tickInterval: TimeInterval = 1.0 / 30.0

    private let shot: Shot
    private let linger: TimeInterval
    private let beforePasteboardWrite: @MainActor () -> Void

    /// Fixed for the card's whole life; the shelf sums these for layout.
    let cardHeight: CGFloat

    /// Fires exactly once, on the main actor, when the leave animation has
    /// finished and the card can be removed from the shelf.
    var onGone: ((ShotCardView) -> Void)?

    /// Internal to ShelfUI: fires at the *start* of leave so the shelf can
    /// glide the neighbouring cards into the vacated space in the same
    /// animation beat. Without this the stack would snap after the fade.
    var onLeaveStarted: ((ShotCardView) -> Void)?

    private(set) var isLeaving = false

    private let thumbView = StaticView()
    private let chrome = NSView()
    private let timerTrack = StaticView()
    private let flash = StaticView()
    private var pinButton: NSButton!

    /// Kept for the drag image; the layer holds the CGImage for display.
    private var thumbImage: NSImage?

    private var pressed = false
    private var pressAt = NSPoint.zero
    private var hovering = false
    private var draggingOut = false
    private var pinned = false

    private var countdownTimer: Timer?
    private var countdownRemaining: TimeInterval = 0
    private var countdownTotal: TimeInterval = 1

    init(shot: Shot, linger: TimeInterval,
         beforePasteboardWrite: @escaping @MainActor () -> Void) {
        self.shot = shot
        self.linger = max(1, linger)
        self.beforePasteboardWrite = beforePasteboardWrite

        // Height from the shot's known dimensions, not the decoded bitmap —
        // the card must claim its final slot before the async decode lands,
        // or the stack would reflow under the user's cursor.
        let aspectHeight = shot.width > 0
            ? Self.contentWidth * CGFloat(shot.height) / CGFloat(shot.width)
            : 128
        let thumbHeight = (min(128, max(52, aspectHeight))).rounded()
        let height = thumbHeight + Self.padding * 2
        cardHeight = height

        super.init(frame: NSRect(x: 0, y: 0, width: Self.outerWidth, height: height))
        wantsLayer = true

        // Soft lift, matching the WPF CardLift: a hard shadow reads as cheap
        // against a busy desktop. The panel itself has no shadow — each card
        // carries its own so gaps between cards stay clean.
        layer?.masksToBounds = false
        layer?.shadowColor = NSColor.black.cgColor
        layer?.shadowOpacity = 0.55
        layer?.shadowRadius = 14
        layer?.shadowOffset = CGSize(width: 0, height: -6)

        buildSubviews(thumbHeight: thumbHeight)
        startCountdown(self.linger)
        decodeThumbAsync()
    }

    @available(*, unavailable)
    required init?(coder: NSCoder) { fatalError("ShotCardView is code-built only") }

    // ---- construction --------------------------------------------------------

    private func buildSubviews(thumbHeight: CGFloat) {
        // Real glass: behind-window blur under the near-opaque surface tint.
        // WPF fakes this with a solid #1B1B1F because it has no cheap blur.
        let glass = NSVisualEffectView(frame: bounds)
        glass.material = .hudWindow
        glass.blendingMode = .behindWindow
        glass.state = .active // the panel never becomes key; without .active the blur would grey out
        glass.wantsLayer = true
        glass.layer?.cornerRadius = 12
        glass.layer?.masksToBounds = true
        glass.layer?.borderWidth = 1
        glass.layer?.borderColor = Theme.hairline.cgColor
        glass.autoresizingMask = [.width, .height]
        addSubview(glass)

        let tint = NSView(frame: glass.bounds)
        tint.wantsLayer = true
        tint.layer?.backgroundColor = Theme.surface.cgColor
        tint.autoresizingMask = [.width, .height]
        glass.addSubview(tint)

        let thumbFrame = NSRect(x: Self.padding, y: Self.padding,
                                width: Self.contentWidth, height: thumbHeight)

        thumbView.frame = thumbFrame
        thumbView.wantsLayer = true
        thumbView.layer?.cornerRadius = 7
        thumbView.layer?.masksToBounds = true
        thumbView.layer?.contentsGravity = .resizeAspect
        addSubview(thumbView)

        if shot.isVideo {
            addVideoBadge(above: thumbFrame)
        }

        // Hover chrome. alpha 0, not hidden: it fades, and by the time anyone
        // can click a button the pointer is inside and the fade-in has run.
        chrome.frame = NSRect(x: Self.padding, y: Self.padding,
                              width: Self.contentWidth, height: 34)
        chrome.wantsLayer = true
        chrome.layer?.backgroundColor = NSColor.black.withAlphaComponent(0.7).cgColor
        chrome.layer?.cornerRadius = 7
        chrome.layer?.maskedCorners = [.layerMinXMinYCorner, .layerMaxXMinYCorner]
        chrome.alphaValue = 0
        addSubview(chrome)

        let copyButton = chromeButton(symbol: "doc.on.doc", tooltip: "Copy to clipboard",
                                      action: #selector(copyClicked))
        let revealButton = chromeButton(symbol: "folder", tooltip: "Show in Finder",
                                        action: #selector(revealClicked))
        pinButton = chromeButton(symbol: "pin", tooltip: "Keep on shelf",
                                 action: #selector(pinClicked))
        let dismissButton = chromeButton(symbol: "xmark", tooltip: "Dismiss",
                                         action: #selector(dismissClicked))

        let buttons = [copyButton, revealButton, pinButton!, dismissButton]
        let side: CGFloat = 26
        let spacing: CGFloat = 6
        let total = side * CGFloat(buttons.count) + spacing * CGFloat(buttons.count - 1)
        var x = (Self.contentWidth - total) / 2
        for button in buttons {
            button.frame = NSRect(x: x, y: (34 - side) / 2, width: side, height: side)
            chrome.addSubview(button)
            x += side + spacing
        }

        // Time-to-dismiss: a thin bar reads at a glance without demanding
        // attention the way a countdown number would. Sits above the chrome
        // scrim, same as the XAML z-order.
        timerTrack.frame = NSRect(x: Self.padding, y: Self.padding,
                                  width: Self.contentWidth, height: 2)
        timerTrack.wantsLayer = true
        timerTrack.layer?.backgroundColor = Theme.accent.cgColor
        timerTrack.layer?.cornerRadius = 1
        addSubview(timerTrack)

        flash.frame = thumbFrame
        flash.wantsLayer = true
        flash.layer?.backgroundColor = NSColor.white.cgColor
        flash.layer?.cornerRadius = 7
        flash.alphaValue = 0
        addSubview(flash)
    }

    private func addVideoBadge(above thumbFrame: NSRect) {
        let text = "▶ \(shot.durationText)\(shot.gifPath != nil ? "  GIF" : "")"
        let label = NSTextField(labelWithString: text)
        label.font = .systemFont(ofSize: 11)
        label.textColor = Theme.ink
        label.sizeToFit()

        let badge = StaticView()
        badge.wantsLayer = true
        badge.layer?.backgroundColor = NSColor.black.withAlphaComponent(0.7).cgColor
        badge.layer?.cornerRadius = 5
        let size = NSSize(width: label.frame.width + 13, height: label.frame.height + 5)
        badge.frame = NSRect(x: thumbFrame.minX + 6,
                             y: thumbFrame.maxY - size.height - 6,
                             width: size.width, height: size.height)
        label.setFrameOrigin(NSPoint(x: 6, y: 3))
        badge.addSubview(label)
        addSubview(badge)
    }

    private func chromeButton(symbol: String, tooltip: String, action: Selector) -> NSButton {
        let image = NSImage(systemSymbolName: symbol, accessibilityDescription: tooltip)
            ?? NSImage()
        let button = NSButton(image: image, target: self, action: action)
        button.isBordered = false
        button.setButtonType(.momentaryChange)
        button.imageScaling = .scaleProportionallyDown
        button.contentTintColor = Theme.ink
        button.toolTip = tooltip
        return button
    }

    // ---- thumbnail -----------------------------------------------------------

    /// CGImage crosses the actor boundary in a box: it is immutable in
    /// practice, but the SDK's Sendable annotation on it has moved between
    /// releases and this must compile first try.
    private struct ThumbBox: @unchecked Sendable { let image: CGImage }

    /// Decodes off the main actor at ~2x display width — an ultrawide
    /// fullscreen PNG takes long enough to drop the slide-in's frames.
    private func decodeThumbAsync() {
        let path = shot.thumbPath
        // The outer Task inherits main-actor isolation, so capturing self is
        // legal; only the Sendable path string enters the detached closure.
        Task { [weak self] in
            let box = await Task.detached(priority: .userInitiated) {
                Self.decodeThumb(path: path)
            }.value
            guard let box, let self else { return }
            self.applyThumb(box)
        }
    }

    nonisolated private static func decodeThumb(path: String) -> ThumbBox? {
        guard let source = CGImageSourceCreateWithURL(
            URL(fileURLWithPath: path) as CFURL, nil) else {
            Log.warn("thumbnail decode failed for \(path): file unreadable")
            return nil
        }
        let options: [CFString: Any] = [
            kCGImageSourceCreateThumbnailFromImageAlways: true,
            kCGImageSourceCreateThumbnailWithTransform: true,
            kCGImageSourceShouldCacheImmediately: true,
            kCGImageSourceThumbnailMaxPixelSize: thumbDecodeWidth,
        ]
        guard let image = CGImageSourceCreateThumbnailAtIndex(
            source, 0, options as CFDictionary) else {
            Log.warn("thumbnail decode failed for \(path): no decodable image")
            return nil
        }
        return ThumbBox(image: image)
    }

    private func applyThumb(_ box: ThumbBox) {
        guard !isLeaving else { return }
        thumbView.layer?.contents = box.image
        thumbImage = NSImage(cgImage: box.image, size: .zero)
    }

    // ---- hover ---------------------------------------------------------------

    override func updateTrackingAreas() {
        super.updateTrackingAreas()
        for area in trackingAreas { removeTrackingArea(area) }
        addTrackingArea(NSTrackingArea(
            rect: .zero,
            options: [.mouseEnteredAndExited, .activeAlways, .inVisibleRect],
            owner: self, userInfo: nil))
    }

    override func mouseEntered(with event: NSEvent) {
        hovering = true // pauses the countdown via the tick guard
        fadeChrome(to: 1, duration: 0.12)
    }

    override func mouseExited(with event: NSEvent) {
        hovering = false
        fadeChrome(to: 0, duration: 0.16)
    }

    private func fadeChrome(to alpha: CGFloat, duration: TimeInterval) {
        NSAnimationContext.runAnimationGroup { context in
            context.duration = duration
            chrome.animator().alphaValue = alpha
        }
    }

    // ---- pointer -------------------------------------------------------------
    // Chrome buttons consume their own mouseDown, so any press that reaches
    // the card is a card press — same effect as the WPF IsChrome walk.

    override func mouseDown(with event: NSEvent) {
        guard !isLeaving else { return }
        pressed = true
        pressAt = convert(event.locationInWindow, from: nil)
    }

    override func mouseDragged(with event: NSEvent) {
        guard pressed else { return }
        let now = convert(event.locationInWindow, from: nil)
        guard abs(now.x - pressAt.x) >= Self.dragThreshold ||
              abs(now.y - pressAt.y) >= Self.dragThreshold else { return }
        pressed = false
        beginDragOut(with: event)
    }

    override func mouseUp(with event: NSEvent) {
        guard pressed else { return }
        pressed = false
        copyShot() // a plain click means "put it back on the pasteboard"
    }

    override func rightMouseUp(with event: NSEvent) {
        leave()
    }

    // ---- drag-out ------------------------------------------------------------

    private func beginDragOut(with event: NSEvent) {
        draggingOut = true // the countdown must not expire under an in-flight drag
        let item = NSDraggingItem(
            pasteboardWriter: ShotPasteboard.pasteboardItem(for: shot))
        item.setDraggingFrame(thumbView.frame, contents: thumbImage)
        _ = beginDraggingSession(with: [item], event: event, source: self)
    }

    // ---- actions -------------------------------------------------------------

    @objc private func copyClicked() { copyShot() }

    @objc private func revealClicked() {
        NSWorkspace.shared.activateFileViewerSelecting(
            [URL(fileURLWithPath: shot.path)])
    }

    @objc private func pinClicked() {
        pinned.toggle()
        if pinned {
            countdownTimer?.invalidate()
            countdownTimer = nil
            timerTrack.isHidden = true
            pinButton.contentTintColor = Theme.accent
        } else {
            pinButton.contentTintColor = Theme.ink
            startCountdown(linger) // unpin restarts at the full linger
        }
    }

    @objc private func dismissClicked() { leave() }

    private func copyShot() {
        beforePasteboardWrite() // suppress our own clipboard echo
        ShotPasteboard.copy(shot)
        flashOnce()
    }

    private func flashOnce() {
        flash.alphaValue = 0.55
        NSAnimationContext.runAnimationGroup { context in
            context.duration = 0.34
            context.timingFunction = CAMediaTimingFunction(name: .linear)
            flash.animator().alphaValue = 0
        }
    }

    // ---- countdown -----------------------------------------------------------
    // A 30 Hz main-runloop timer rather than a Core Animation width animation:
    // the countdown pauses and resumes for hover, drag, and pin, and pausable
    // layer time (speed/timeOffset juggling) is exactly the kind of cleverness
    // that breaks when a frame is animated by the shelf at the same moment.

    private func startCountdown(_ duration: TimeInterval) {
        countdownTimer?.invalidate()
        countdownRemaining = duration
        countdownTotal = duration
        timerTrack.isHidden = false
        layoutTimerTrack()
        countdownTimer = Timer.scheduledTimer(
            withTimeInterval: Self.tickInterval, repeats: true) { [weak self] _ in
            // The timer is scheduled on the main runloop; assumeIsolated is a
            // statement of that fact, not a hop.
            MainActor.assumeIsolated { self?.tick() }
        }
    }

    private func tick() {
        if isLeaving {
            countdownTimer?.invalidate()
            countdownTimer = nil
            return
        }
        guard !hovering, !draggingOut, !pinned else { return }
        countdownRemaining -= Self.tickInterval
        if countdownRemaining <= 0 {
            countdownTimer?.invalidate()
            countdownTimer = nil
            leave()
        } else {
            layoutTimerTrack()
        }
    }

    private func layoutTimerTrack() {
        let fraction = countdownTotal > 0
            ? max(0, countdownRemaining / countdownTotal) : 0
        timerTrack.frame = NSRect(x: Self.padding, y: Self.padding,
                                  width: Self.contentWidth * fraction, height: 2)
    }

    // ---- leave ---------------------------------------------------------------

    /// Fades out while the shelf collapses this card's slot, so the stack
    /// glides instead of snapping. Fires onGone exactly once.
    func leave() {
        guard !isLeaving else { return }
        isLeaving = true

        countdownTimer?.invalidate()
        countdownTimer = nil

        onLeaveStarted?(self)

        NSAnimationContext.runAnimationGroup({ context in
            context.duration = 0.2
            context.timingFunction = CAMediaTimingFunction(name: .easeOut)
            animator().alphaValue = 0
        }, completionHandler: { [weak self] in
            MainActor.assumeIsolated {
                guard let self else { return }
                self.onGone?(self)
            }
        })
    }
}

// @preconcurrency: the SDK's isolation annotation on NSDraggingSource differs
// across releases; the methods only ever run on the main thread either way.
extension ShotCardView: @preconcurrency NSDraggingSource {
    func draggingSession(_ session: NSDraggingSession,
                         sourceOperationMaskFor context: NSDraggingContext) -> NSDragOperation {
        .copy
    }

    func draggingSession(_ session: NSDraggingSession,
                         endedAt screenPoint: NSPoint,
                         operation: NSDragOperation) {
        draggingOut = false
        // Dropped somewhere real — its job is done, get it off the shelf.
        // A cancelled drag resumes the countdown via the flag alone.
        if operation != [] { leave() }
    }
}
