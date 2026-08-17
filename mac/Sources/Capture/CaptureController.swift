import AppKit

/// esgee's own capture source and the single pipeline every source feeds:
/// save → shelf → pasteboard → index (docs/MAC.md module map). Serialized by
/// a busy flag: a second hotkey mid-flow is ignored, never stacked.
@MainActor
final class CaptureController {
    // Reachable from the nonisolated save path; ShotStore is internally
    // synchronized, so handing it across is the design, not a leak.
    private nonisolated let store: ShotStore
    private let shelf: ShelfPanelController
    private let settings: SettingsStore
    private let beforePasteboardWrite: @MainActor () -> Void

    /// Fan-out hook AppDelegate wires to OCR + sync enqueues. Fired on the
    /// main actor for every shot that lands, from any source.
    var onShotSaved: ((Shot) -> Void)?

    private var overlay: OverlayController?
    // True from timed-capture countdown start until its overlay opens, so a
    // second hotkey mid-fuse can't stack a parallel capture flow.
    private var timing = false
    // Covers the freeze and composite/save gaps where no overlay exists yet.
    private var inFlight = false

    init(store: ShotStore, shelf: ShelfPanelController, settings: SettingsStore,
         beforePasteboardWrite: @escaping @MainActor () -> Void) {
        self.store = store
        self.shelf = shelf
        self.settings = settings
        self.beforePasteboardWrite = beforePasteboardWrite
    }

    private var busy: Bool { timing || inFlight || overlay != nil }

    // ---- capture flows -------------------------------------------------------

    func beginRegion() {
        Log.info("region capture requested")
        if busy { Log.info("capture already in progress; ignoring"); return }
        openOverlay()
    }

    /// Whole screen, zero ceremony: no overlay, straight to the pipeline.
    func beginFullscreen() {
        Log.info("fullscreen capture requested")
        if busy { Log.info("capture already in progress; ignoring"); return }

        inFlight = true
        Task { @MainActor in
            do {
                let frames = try await ScreenGrabber.freezeAllDisplays()
                let union = frames.reduce(CGRect.null) { $0.union($1.framePoints) }
                let (png, size) = try ScreenGrabber.composite(rectPoints: union, from: frames)
                self.finish(png: png, size: size, takenAt: Date())
            } catch {
                self.inFlight = false
                Log.error("fullscreen capture failed: \(error)")
            }
        }
    }

    /// Re-shoots the last committed selection rect — fresh pixels, same
    /// frame. The ShareX trick for iterating on the same UI area.
    func beginLastRegion() {
        Log.info("last-region capture requested")
        if busy { Log.info("capture already in progress; ignoring"); return }

        guard let last = settings.current.lastRegion, last.count == 4,
              last[2] >= 1, last[3] >= 1 else {
            Log.info("no last region stored; opening overlay instead")
            beginRegion()
            return
        }
        let rect = CGRect(x: CGFloat(last[0]), y: CGFloat(last[1]),
                          width: CGFloat(last[2]), height: CGFloat(last[3]))

        inFlight = true
        Task { @MainActor in
            do {
                let frames = try await ScreenGrabber.freezeAllDisplays()

                // Displays may have been rearranged since the rect was saved.
                let visible = frames.reduce(CGRect.null) { $0.union($1.framePoints) }
                if rect.intersection(visible).isEmpty {
                    self.inFlight = false
                    Log.warn("stored last region is off-screen; opening overlay instead")
                    // The frames are milliseconds old — reuse them rather than
                    // paying for a second freeze via beginRegion.
                    self.presentOverlay(frames: frames)
                    return
                }

                let (png, size) = try ScreenGrabber.composite(rectPoints: rect, from: frames)
                self.finish(png: png, size: size, takenAt: Date())
            } catch {
                self.inFlight = false
                Log.error("last-region capture failed: \(error)")
            }
        }
    }

    /// Hotkey version of the overlay's 1–9 delay: fixed fuse, then the region
    /// overlay opens on a frame frozen at zero.
    func beginTimed() {
        Log.info("timed capture requested")
        if busy { Log.info("capture already in progress; ignoring"); return }
        runDelayed(seconds: min(max(settings.current.timerSeconds, 1), 60))
    }

    // ---- the pipeline entry ----------------------------------------------------

    /// THE pipeline entry. Synchronous and blocking (disk + hash) — call it
    /// off the main actor; the begin* flows do so via Task.detached, the
    /// clipboard watcher's wiring in AppDelegate does the same. Internally:
    /// store.add, then hop to the main actor for shelf.push + onShotSaved.
    nonisolated func save(png: Data, size: PixelSize, takenAt: Date) throws -> Shot {
        let shot = try store.add(png: png, size: size, takenAt: takenAt)
        Log.info("captured \(size.width)x\(size.height) -> \(shot.path)")
        Task { @MainActor in
            self.shelf.push(shot)
            self.onShotSaved?(shot)
        }
        return shot
    }

    // ---- internals -----------------------------------------------------------

    private func openOverlay() {
        // inFlight covers the freeze gap: SCK screenshots are async and a
        // second hotkey must not start a parallel freeze.
        inFlight = true
        Task { @MainActor in
            do {
                let frames = try await ScreenGrabber.freezeAllDisplays()
                self.inFlight = false
                self.presentOverlay(frames: frames)
            } catch {
                // Screen Recording denial lands here. AppDelegate already
                // preflighted and the menu bar carries the repair link — log
                // once per attempt, never throw UI from the capture path.
                self.inFlight = false
                Log.error("capture begin failed: \(error)")
            }
        }
    }

    private func presentOverlay(frames: [FrozenDisplay]) {
        let overlay = OverlayController(frames: frames)
        self.overlay = overlay

        overlay.onCommit = { [weak self] rect in
            guard let self else { return }
            self.overlay = nil

            // Remember the spot for the repeat-last-region hotkey, in global
            // points so it stays valid across sessions.
            let r = rect.standardized
            self.settings.update {
                $0.lastRegion = [Int(r.minX.rounded()), Int(r.minY.rounded()),
                                 Int(r.width.rounded()), Int(r.height.rounded())]
            }

            self.inFlight = true
            do {
                let (png, size) = try ScreenGrabber.composite(rectPoints: r, from: frames)
                self.finish(png: png, size: size, takenAt: Date())
            } catch {
                self.inFlight = false
                Log.error("capture composite failed: \(error)")
            }
        }

        overlay.onCancelled = { [weak self] in
            self?.overlay = nil
        }

        overlay.onDelayRequested = { [weak self] seconds in
            guard let self else { return }
            self.overlay = nil
            self.runDelayed(seconds: seconds)
        }

        overlay.show()
    }

    private func runDelayed(seconds: Int) {
        timing = true
        let pill = CountdownPill()
        pill.setRemaining(seconds)
        pill.show()

        Task { @MainActor in
            var left = seconds
            while left > 0 {
                pill.setRemaining(left)
                try? await Task.sleep(nanoseconds: 1_000_000_000)
                left -= 1
            }
            pill.close()

            // Small settle so the pill is gone from the frame we freeze.
            try? await Task.sleep(nanoseconds: 160_000_000)

            do {
                let frames = try await ScreenGrabber.freezeAllDisplays()
                self.timing = false
                self.presentOverlay(frames: frames)
            } catch {
                self.timing = false
                Log.error("timed capture failed: \(error)")
            }
        }
    }

    /// The finish step for the begin* flows (NOT for watcher captures):
    /// after save, hotkey captures must also land on the pasteboard so ⌘V
    /// muscle memory keeps working — watcher captures are already there.
    /// Expects inFlight to be true on entry; clears it when the pipeline is
    /// done either way.
    private func finish(png: Data, size: PixelSize, takenAt: Date) {
        Task.detached(priority: .userInitiated) { [self] in
            do {
                let shot = try self.save(png: png, size: size, takenAt: takenAt)
                await MainActor.run {
                    self.inFlight = false
                    self.beforePasteboardWrite()
                    ShotPasteboard.copy(shot)
                }
            } catch {
                Log.error("capture pipeline failed: \(error)")
                await MainActor.run { self.inFlight = false }
            }
        }
    }
}
