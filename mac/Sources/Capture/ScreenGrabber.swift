import AppKit
import ImageIO
import ScreenCaptureKit

/// One display's frozen frame. `framePoints` is the NSScreen frame in Cocoa
/// global points (origin bottom-left); `image` is at native backing scale so
/// a Retina crop never loses pixels it actually had.
struct FrozenDisplay {
    let displayID: CGDirectDisplayID
    let framePoints: CGRect
    let scale: CGFloat
    let image: CGImage
}

/// Failures the capture flows treat as "log and drop" — a menu-bar app
/// survives a bad capture.
enum ScreenGrabError: Error {
    case noDisplays
    case emptyRegion
    case renderFailed
}

/// ScreenCaptureKit one-shots. SCScreenshotManager.captureImage is the
/// supported path on 14+ (the CGDisplay/CGWindowList family is deprecated
/// and increasingly hostile).
@MainActor
enum ScreenGrabber {
    /// Grab every display at its native scale. esgee's own windows (shelf,
    /// archive) are excluded via the content filter — exclusion replaces the
    /// Windows hide/show dance outright: no compositor settle delay, no
    /// flicker, nothing to forget to re-show on an error path.
    /// SCShareableContent is not Sendable on this SDK, so the async-throws
    /// convenience can't deliver it into a @MainActor context — the callback
    /// form plus a box crosses the boundary the same way ThumbBox does.
    private struct ContentBox: @unchecked Sendable { let content: SCShareableContent }
    private struct ImageBox: @unchecked Sendable { let image: CGImage }

    /// The continuation closures live in nonisolated helpers on purpose: SCK
    /// invokes its completion handlers on an XPC reply queue, and a closure
    /// that inherited @MainActor isolation would trip the Swift 6 runtime
    /// executor check there (dispatch_assert_queue_fail — found the hard way).
    nonisolated private static func fetchShareableContent() async throws -> ContentBox {
        try await withCheckedThrowingContinuation { cont in
            SCShareableContent.getExcludingDesktopWindows(
                false, onScreenWindowsOnly: true) { content, error in
                if let content { cont.resume(returning: ContentBox(content: content)) }
                else { cont.resume(throwing: error ?? ScreenGrabError.renderFailed) }
            }
        }
    }

    /// Filter and config ride INTO the nonisolated helper in the box for the
    /// same reason the image rides out in one.
    private struct CaptureRequest: @unchecked Sendable {
        let filter: SCContentFilter
        let config: SCStreamConfiguration
    }

    nonisolated private static func captureBoxed(_ req: CaptureRequest) async throws -> ImageBox {
        try await withCheckedThrowingContinuation { cont in
            SCScreenshotManager.captureImage(
                contentFilter: req.filter, configuration: req.config) { image, error in
                if let image { cont.resume(returning: ImageBox(image: image)) }
                else { cont.resume(throwing: error ?? ScreenGrabError.renderFailed) }
            }
        }
    }

    static func freezeAllDisplays() async throws -> [FrozenDisplay] {
        let content = try await fetchShareableContent().content
        let ourBundle = Bundle.main.bundleIdentifier
        let excluded = content.windows.filter {
            $0.owningApplication?.bundleIdentifier == ourBundle
        }

        var frozen: [FrozenDisplay] = []
        for screen in NSScreen.screens {
            guard let number = screen.deviceDescription[
                NSDeviceDescriptionKey("NSScreenNumber")] as? NSNumber else { continue }
            let displayID = CGDirectDisplayID(number.uint32Value)

            guard let scDisplay = content.displays.first(where: { $0.displayID == displayID })
            else {
                // A screen NSScreen knows about but SCK cannot serve (mid
                // hot-plug, mirroring transitions). Skip it; a partial freeze
                // beats no capture.
                Log.warn("display \(displayID) not in shareable content; skipping")
                continue
            }

            let scale = screen.backingScaleFactor
            let config = SCStreamConfiguration()
            config.width = Int(screen.frame.width * scale)
            config.height = Int(screen.frame.height * scale)
            config.showsCursor = false
            config.captureResolution = .best

            let filter = SCContentFilter(display: scDisplay, excludingWindows: excluded)
            let image = try await captureBoxed(
                CaptureRequest(filter: filter, config: config)).image

            frozen.append(FrozenDisplay(displayID: displayID,
                                        framePoints: screen.frame,
                                        scale: scale,
                                        image: image))
        }

        guard !frozen.isEmpty else { throw ScreenGrabError.noDisplays }
        return frozen
    }

    /// Composite a global-points rect out of frozen frames. Renders at the
    /// HIGHEST scale among intersected displays so a Retina region never gets
    /// downsampled by a 1x neighbour (docs/MAC.md). Returns PNG bytes and the
    /// pixel size.
    ///
    /// PERF (deliberate, revisit): this render + PNG encode runs on the main
    /// actor — SPEC.md declares ScreenGrabber @MainActor and every begin*
    /// flow calls from a MainActor task — so a fullscreen multi-display
    /// Retina capture (~100+ MP) stalls hotkeys and the shelf for hundreds
    /// of ms. Only save() (sha256 + DB) is detached today. The fix for the
    /// perf pass is a nonisolated composite returning boxed CGImage/Data;
    /// per-contract for now, NOT a compile fix.
    static func composite(rectPoints: CGRect, from frames: [FrozenDisplay])
        throws -> (png: Data, size: PixelSize) {
        let rect = rectPoints.standardized
        let hit = frames.filter { $0.framePoints.intersects(rect) }
        guard !hit.isEmpty, rect.width >= 1, rect.height >= 1 else {
            throw ScreenGrabError.emptyRegion
        }

        let maxScale = hit.map(\.scale).max() ?? 1
        let pxW = max(1, Int((rect.width * maxScale).rounded()))
        let pxH = max(1, Int((rect.height * maxScale).rounded()))

        guard let space = CGColorSpace(name: CGColorSpace.sRGB),
              let ctx = CGContext(data: nil, width: pxW, height: pxH,
                                  bitsPerComponent: 8, bytesPerRow: 0,
                                  space: space,
                                  bitmapInfo: CGImageAlphaInfo.premultipliedLast.rawValue)
        else { throw ScreenGrabError.renderFailed }

        // A selection can span the dead zone between non-adjacent displays;
        // black there matches what every multi-monitor tool ships.
        ctx.setFillColor(CGColor(gray: 0, alpha: 1))
        ctx.fill(CGRect(x: 0, y: 0, width: CGFloat(pxW), height: CGFloat(pxH)))
        ctx.interpolationQuality = .high

        for display in hit {
            let local = rect.intersection(display.framePoints)
            guard local.width > 0, local.height > 0 else { continue }

            // CGImage is top-left origin, Cocoa is bottom-left — this flip is
            // where the off-by-one bugs live; keep it in one place.
            let src = CGRect(
                x: (local.minX - display.framePoints.minX) * display.scale,
                y: (display.framePoints.maxY - local.maxY) * display.scale,
                width: local.width * display.scale,
                height: local.height * display.scale)
            guard let piece = display.image.cropping(to: src) else { continue }

            // The context itself is bottom-left, so destination math stays in
            // Cocoa orientation — only the source crop needed the flip.
            let dest = CGRect(
                x: (local.minX - rect.minX) * maxScale,
                y: (local.minY - rect.minY) * maxScale,
                width: local.width * maxScale,
                height: local.height * maxScale)
            ctx.draw(piece, in: dest)
        }

        guard let composited = ctx.makeImage() else { throw ScreenGrabError.renderFailed }

        let data = NSMutableData()
        guard let sink = CGImageDestinationCreateWithData(
            data as CFMutableData, "public.png" as CFString, 1, nil)
        else { throw ScreenGrabError.renderFailed }
        CGImageDestinationAddImage(sink, composited, nil)
        guard CGImageDestinationFinalize(sink) else { throw ScreenGrabError.renderFailed }

        return (data as Data, PixelSize(width: pxW, height: pxH))
    }
}
