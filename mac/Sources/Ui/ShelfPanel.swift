import AppKit
import QuartzCore

/// The shelf panel is larger than its cards (shadow padding, and it keeps its
/// height while a leaving card fades), so the empty regions must never win
/// hit testing — a transparent window that eats clicks meant for the app
/// underneath is worse than no shelf at all.
private final class PassthroughView: NSView {
    override func hitTest(_ point: NSPoint) -> NSView? {
        let hit = super.hitTest(point)
        return hit === self ? nil : hit
    }
}

/// The corner shelf. Exists so captures stop competing for the pasteboard's
/// single slot: several can sit here at once, each independently draggable.
///
/// The NSPanel flag combination is exact and load-bearing (docs/MAC.md "Shelf
/// and drag-out"): .nonactivatingPanel + .canJoinAllSpaces +
/// .fullScreenAuxiliary is what floats the shelf over a full-screen Space
/// without yanking the user out of it, and nothing here may ever take focus —
/// shots land while the user keeps typing.
@MainActor
final class ShelfPanelController {
    // Contract numbers (SPEC.md).
    private static let edgeGap: CGFloat = 18
    private static let cardGap: CGFloat = 10
    /// Room around the cards for their layer shadows; a shadow clipped at the
    /// window edge reads as a rendering bug.
    private static let shadowPad: CGFloat = 28

    private let settings: SettingsStore
    private let beforePasteboardWrite: @MainActor () -> Void
    private let panel: NSPanel
    private let container: PassthroughView

    /// Oldest first; newest renders at the bottom, closest to the corner.
    /// Leaving cards are removed from here at leave *start* — they linger as
    /// subviews only until their fade completes.
    private var cards: [ShotCardView] = []

    var isEmpty: Bool { cards.isEmpty }

    init(settings: SettingsStore, beforePasteboardWrite: @escaping @MainActor () -> Void) {
        self.settings = settings
        self.beforePasteboardWrite = beforePasteboardWrite

        let width = ShotCardView.outerWidth + Self.shadowPad * 2
        let shelf = NSPanel(
            contentRect: NSRect(x: 0, y: 0, width: width, height: Self.shadowPad * 2),
            styleMask: [.nonactivatingPanel, .borderless],
            backing: .buffered,
            defer: true)
        shelf.level = .floating
        shelf.collectionBehavior = [.canJoinAllSpaces, .fullScreenAuxiliary, .stationary]
        shelf.isFloatingPanel = true
        shelf.hidesOnDeactivate = false
        shelf.becomesKeyOnlyIfNeeded = true
        shelf.backgroundColor = .clear
        shelf.isOpaque = false
        shelf.hasShadow = false // cards carry their own
        shelf.isMovable = false
        shelf.isReleasedWhenClosed = false
        shelf.animationBehavior = .none

        let content = PassthroughView()
        content.wantsLayer = true
        shelf.contentView = content

        panel = shelf
        container = content
    }

    /// Lands a fresh capture on the shelf. Re-anchors to the current main
    /// screen every time — displays come and go on laptops, and the shelf
    /// must follow the corner, not the coordinates it was born with.
    func push(_ shot: Shot) {
        // Oldest goes first once the shelf is full — the newest capture is
        // almost always the one being reached for. leave() synchronously
        // removes the card from `cards`; the identity check breaks the loop
        // if that contract is ever violated rather than spinning forever.
        let maxCards = max(1, settings.current.maxCards)
        while cards.count >= maxCards {
            guard let oldest = cards.first else { break }
            oldest.leave()
            if cards.first === oldest { break }
        }

        let linger = TimeInterval(max(1, settings.current.lingerSeconds))
        let card = ShotCardView(shot: shot, linger: linger,
                                beforePasteboardWrite: beforePasteboardWrite)
        card.onLeaveStarted = { [weak self] leaving in self?.beginRemoval(of: leaving) }
        card.onGone = { [weak self] gone in self?.finishRemoval(of: gone) }
        cards.append(card)
        container.addSubview(card)

        anchorPanel()

        let frames = targetFrames()

        // Existing cards slide up to make room in one beat…
        NSAnimationContext.runAnimationGroup { context in
            context.duration = 0.2
            context.timingFunction = CAMediaTimingFunction(name: .easeOut)
            for (existing, frame) in frames where existing !== card {
                existing.animator().frame = frame
            }
        }

        // …while the newcomer slides in from the screen edge.
        if let final = frames.first(where: { $0.0 === card })?.1 {
            card.frame = final.offsetBy(dx: 44, dy: 0)
            card.alphaValue = 0
            NSAnimationContext.runAnimationGroup { context in
                context.duration = 0.26
                context.timingFunction = CAMediaTimingFunction(name: .easeOut)
                card.animator().frame = final
            }
            NSAnimationContext.runAnimationGroup { context in
                context.duration = 0.18
                card.animator().alphaValue = 1
            }
        }

        if !panel.isVisible {
            // Regardless: the shelf must appear even while another app owns
            // the screen — that is its entire reason to exist.
            panel.orderFrontRegardless()
        }
    }

    func clearAll() {
        // leave() mutates `cards` synchronously; iterate a snapshot.
        let snapshot = cards
        for card in snapshot { card.leave() }
    }

    // ---- layout --------------------------------------------------------------
    // Frame-based on purpose. Auto Layout inside a window whose size is
    // animated in lockstep with its subviews fights the animator; explicit
    // frames make every glide a single NSAnimationContext group.

    /// Bottom-anchored slots: newest card at the bottom, older ones stacked
    /// above. (The WPF shelf is top-anchored inside a bottom-anchored window,
    /// so its cards "glide up"; anchoring to the corner the shelf lives in is
    /// the same intent expressed in Cocoa's coordinate space.)
    private func targetFrames() -> [(ShotCardView, NSRect)] {
        var result: [(ShotCardView, NSRect)] = []
        var y = Self.shadowPad
        for card in cards.reversed() {
            result.append((card, NSRect(x: Self.shadowPad, y: y,
                                        width: ShotCardView.outerWidth,
                                        height: card.cardHeight)))
            y += card.cardHeight + Self.cardGap
        }
        return result
    }

    private var contentHeight: CGFloat {
        guard !cards.isEmpty else { return 0 }
        let heights = cards.reduce(CGFloat(0)) { $0 + $1.cardHeight }
        return heights + CGFloat(cards.count - 1) * Self.cardGap
    }

    /// Bottom-right of the visible frame, 18 pt in from each edge, with the
    /// shadow padding hanging past the gap so the *cards* sit at 18 pt. The
    /// panel keeps its height while any card is still fading out — shrinking
    /// early would clip the fade.
    private func anchorPanel() {
        guard let screen = NSScreen.main else { return }
        let visible = screen.visibleFrame

        let width = ShotCardView.outerWidth + Self.shadowPad * 2
        let needed = contentHeight + Self.shadowPad * 2
        let hasLeaving = container.subviews.contains {
            ($0 as? ShotCardView)?.isLeaving == true
        }
        let height = hasLeaving ? max(needed, panel.frame.height) : needed

        let x = visible.maxX - Self.edgeGap - ShotCardView.outerWidth - Self.shadowPad
        let y = visible.minY + Self.edgeGap - Self.shadowPad
        panel.setFrame(NSRect(x: x, y: y, width: width, height: height), display: true)
    }

    // ---- removal -------------------------------------------------------------

    /// Leave has just begun: collapse the leaving card's slot and glide the
    /// survivors into it, in the same 200 ms beat as the card's own fade.
    private func beginRemoval(of card: ShotCardView) {
        guard cards.contains(where: { $0 === card }) else { return }
        cards.removeAll { $0 === card }

        // The frame collapses to zero height; the content must neither squash
        // nor spill over the cards gliding in beneath it.
        card.autoresizesSubviews = false
        card.layer?.masksToBounds = true

        let collapsed = NSRect(x: card.frame.minX + 36, y: card.frame.minY,
                               width: card.frame.width, height: 0)
        let frames = targetFrames()
        NSAnimationContext.runAnimationGroup { context in
            context.duration = 0.2
            context.timingFunction = CAMediaTimingFunction(name: .easeOut)
            card.animator().frame = collapsed
            for (survivor, frame) in frames {
                survivor.animator().frame = frame
            }
        }
    }

    /// The fade has finished: drop the view, and hide the panel once nothing
    /// is left — an empty transparent panel still costs compositor work.
    private func finishRemoval(of card: ShotCardView) {
        card.removeFromSuperview()
        if cards.isEmpty && !container.subviews.contains(where: { $0 is ShotCardView }) {
            panel.orderOut(nil)
        }
        anchorPanel()
    }
}
