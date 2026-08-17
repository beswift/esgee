import AppKit

/// The multi-representation pasteboard payload — the reason the app exists.
/// Windows offers exactly one representation (CF_HDROP); AppKit lets one item
/// carry several, so drop targets pick what they want with no temp-file round
/// trip (docs/MAC.md "Drag-out").
///
/// Images: file URL + PNG bytes + TIFF, so Finder-likes take the file while
/// image-first targets (web textareas, editors) take bytes directly.
/// Videos: file URL only, pointing at the GIF when one exists else the MP4 —
/// offering a still frame would make image-first paste targets silently take
/// a frame instead of the clip.
///
/// Every caller invokes its beforePasteboardWrite hook BEFORE copy(), or the
/// clipboard watcher would loop our own write back in as a fresh capture.
@MainActor
enum ShotPasteboard {

    static func pasteboardItem(for shot: Shot) -> NSPasteboardItem {
        let item = NSPasteboardItem()

        if shot.isVideo {
            // GIF over MP4 when both exist: the GIF is the paste-anywhere
            // artifact; anything that truly wants the MP4 gets it via the
            // archive, not the pasteboard. Same choice as Windows DragSource.
            let path = shot.gifPath ?? shot.path
            item.setString(URL(fileURLWithPath: path).absoluteString, forType: .fileURL)
            return item
        }

        item.setString(URL(fileURLWithPath: shot.path).absoluteString, forType: .fileURL)

        // Byte representations are best-effort: a deleted or unreadable file
        // must not break the copy — the file URL alone is still a valid paste
        // for most targets, and the failure surfaces at drop time regardless.
        if let png = try? Data(contentsOf: URL(fileURLWithPath: shot.path)) {
            item.setData(png, forType: .png)
            // TIFF is what legacy AppKit paste targets ask for first; derive
            // it from the PNG bytes already in hand rather than re-reading.
            if let tiff = NSBitmapImageRep(data: png)?.tiffRepresentation {
                item.setData(tiff, forType: .tiff)
            }
        }

        return item
    }

    /// clearContents + writeObjects on .general — the same payload a drag
    /// carries, so ⌘V and drag-out are interchangeable.
    static func copy(_ shot: Shot) {
        let pasteboard = NSPasteboard.general
        pasteboard.clearContents()
        pasteboard.writeObjects([pasteboardItem(for: shot)])
    }
}
