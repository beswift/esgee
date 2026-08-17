import AppKit

/// Shared palette, translated from src/Esgee/Ui/Theme.xaml. ArchiveUI reads
/// these through Color(nsColor:) so the shelf and the archive stay one app;
/// values here are the contract, not the XAML hex codes — where SPEC.md and
/// Theme.xaml disagree (hairline alpha, ink brightness), SPEC.md wins so both
/// Mac surfaces agree with each other.
enum Theme {
    /// #5B8CFF — the one saturated color in the app. Timer track, pin-active
    /// tint, focus rings. Anything else that wants color is wrong.
    static let accent = NSColor(srgbRed: 91.0 / 255.0, green: 140.0 / 255.0,
                                blue: 1.0, alpha: 1.0)

    /// Primary text and glyphs. Not pure white — full white over the dark
    /// glass reads as glare on a HiDPI panel.
    static let ink = NSColor(white: 0.92, alpha: 1.0)

    /// Secondary text: captions, timestamps, the archive's empty labels.
    static let inkMuted = NSColor(white: 0.55, alpha: 1.0)

    /// 1 pt borders. Alpha, not gray — a hairline must survive whatever the
    /// glass happens to be composited over.
    static let hairline = NSColor(white: 1.0, alpha: 0.12)

    /// Card and chrome background: near-black at ~0.92 so a behind-window
    /// blur still ghosts through. Mirrors the Windows #1B1B1F surface, which
    /// fakes the same depth with a solid because WPF has no cheap blur.
    static let surface = NSColor(srgbRed: 27.0 / 255.0, green: 27.0 / 255.0,
                                 blue: 31.0 / 255.0, alpha: 0.92)

    /// Hover state of the same surface: one step lighter and slightly more
    /// solid, so the change reads as lift rather than as a color swap.
    static let surfaceHover = NSColor(srgbRed: 38.0 / 255.0, green: 38.0 / 255.0,
                                      blue: 43.0 / 255.0, alpha: 0.95)
}
