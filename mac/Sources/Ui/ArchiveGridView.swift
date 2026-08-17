import AppKit
import SwiftUI
import AVKit
import UniformTypeIdentifiers

/// SwiftUI lives only inside this window (docs/MAC.md): the grid is the one
/// surface where declarative layout pays for itself. Everything that needs
/// real AppKit — drag sessions, file promises, context menus — goes through
/// ArchiveDragHost below.
struct ArchiveView: View {
    @ObservedObject var model: ArchiveModel
    @FocusState private var searchFocused: Bool

    var body: some View {
        ZStack {
            VStack(spacing: 0) {
                header
                Divider().overlay(Color(nsColor: Theme.hairline))
                grid
            }
            if model.previewVisible {
                ArchivePreviewLayer(model: model)
            }
        }
        .background(Color(nsColor: Theme.surface).ignoresSafeArea())
        .onAppear { searchFocused = true }
        .onChange(of: model.searchFocusToken) { searchFocused = true }
    }

    private var header: some View {
        HStack(spacing: 10) {
            Image(systemName: "magnifyingglass")
                .foregroundColor(Color(nsColor: Theme.inkMuted))
            TextField("Search screen text…", text: $model.query)
                .textFieldStyle(.plain)
                .font(.system(size: 13))
                .foregroundColor(Color(nsColor: Theme.ink))
                .focused($searchFocused)
            if model.switcherVisible {
                Picker("", selection: $model.selectedMachineID) {
                    ForEach(model.machines) { machine in
                        Text(machine.label).tag(machine.id)
                    }
                }
                .labelsHidden()
                .pickerStyle(.menu)
                .fixedSize()
            }
        }
        .padding(.horizontal, 14)
        .padding(.vertical, 10)
    }

    private var grid: some View {
        ScrollView {
            LazyVGrid(columns: [GridItem(.adaptive(minimum: 224), spacing: 14, alignment: .top)],
                      alignment: .leading, spacing: 14) {
                ForEach(model.entries) { entry in
                    ArchiveTileView(entry: entry, model: model)
                }
            }
            .padding(14)
        }
        .overlay {
            if model.entries.isEmpty && !model.emptyText.isEmpty {
                Text(model.emptyText)
                    .font(.system(size: 13))
                    .foregroundColor(Color(nsColor: Theme.inkMuted))
            }
        }
    }
}

/// One 224 pt tile. The thumbnail decodes off the main actor (the entry owns
/// that); the caption mirrors the Windows tile text shape exactly.
struct ArchiveTileView: View {
    @ObservedObject var entry: ArchiveEntry
    let model: ArchiveModel

    var body: some View {
        VStack(alignment: .leading, spacing: 5) {
            thumb
            Text(caption)
                .font(.system(size: 11))
                .foregroundColor(Color(nsColor: Theme.inkMuted))
                .lineLimit(1)
                .padding(.horizontal, 2)
        }
        .frame(width: 224)
        .contentShape(Rectangle())
        .overlay(ArchiveDragHost(entry: entry, model: model))
        .help(entry.isRemote
            ? "on \(entry.client?.peer.name ?? "peer") — click to preview · drag out · right-click to pull"
            : "click to preview · drag out · right-click for more")
        .onAppear { entry.loadThumbnail() }
    }

    private var thumb: some View {
        Group {
            if let img = entry.thumbnail {
                Image(nsImage: img)
                    .resizable()
                    .aspectRatio(contentMode: .fit)
                    .frame(maxHeight: 150)
            } else {
                // Holds layout while the decode is in flight.
                RoundedRectangle(cornerRadius: 4)
                    .fill(Color(nsColor: Theme.surfaceHover))
                    .frame(height: 126)
            }
        }
        .frame(width: 224)
        .frame(minHeight: 60)
        .clipShape(RoundedRectangle(cornerRadius: 4))
    }

    private var caption: String {
        let shot = entry.shot
        let when = ArchiveFmt.tileStamp.string(from: shot.takenAt)
        let dims = "\(shot.width)×\(shot.height)"
        // ⇄ marks locally-held shots that arrived from another machine; a
        // remote tile is already labelled by the switcher.
        let origin = (!shot.origin.isEmpty && !entry.isRemote) ? "   ⇄ \(shot.origin)" : ""
        return shot.isVideo
            ? "\(when)   ▶ \(shot.durationText)   \(dims)\(origin)"
            : "\(when)   \(dims)\(origin)"
    }
}

/// Scrim + centered content. The entry list was snapshotted at open (in the
/// model), so a live-poll refresh can't shift navigation.
struct ArchivePreviewLayer: View {
    @ObservedObject var model: ArchiveModel

    var body: some View {
        ZStack {
            Color.black.opacity(0.72)
                .ignoresSafeArea()
                .contentShape(Rectangle())
                .onTapGesture { model.closePreview() }

            VStack(spacing: 12) {
                content
                    .frame(maxWidth: .infinity, maxHeight: .infinity)
                controls
                if model.ocrPanelOpen { ocrPanel }
            }
            .padding(28)
        }
    }

    @ViewBuilder private var content: some View {
        if let player = model.previewPlayer {
            VideoPlayer(player: player)
        } else if let img = model.previewImage {
            Image(nsImage: img)
                .resizable()
                .aspectRatio(contentMode: .fit)
        } else {
            ProgressView()
                .controlSize(.large)
        }
    }

    private var controls: some View {
        HStack(spacing: 16) {
            Text(model.previewCaption)
                .font(.system(size: 12))
                .foregroundColor(Color(nsColor: Theme.inkMuted))
            Spacer()
            if let entry = model.previewEntry {
                Button("Copy") { model.copy(entry) }
                if !entry.shot.isVideo {
                    Button("Screen text") { model.toggleOcrPanel() }
                        .foregroundColor(model.ocrPanelOpen
                            ? Color(nsColor: Theme.accent)
                            : Color(nsColor: Theme.ink))
                }
                if entry.isRemote {
                    Button("Pull to this Mac") { model.pull(entry) }
                } else {
                    Button("Show in Finder") { model.reveal(entry) }
                }
            }
            Button("Close") { model.closePreview() }
        }
        .buttonStyle(.plain)
        .font(.system(size: 12))
        .foregroundColor(Color(nsColor: Theme.ink))
    }

    private var ocrPanel: some View {
        VStack(alignment: .leading, spacing: 8) {
            ScrollView {
                Text(model.ocrDisplayText)
                    .font(.system(size: 12))
                    .foregroundColor(Color(nsColor: Theme.ink))
                    .textSelection(.enabled)
                    .frame(maxWidth: .infinity, alignment: .leading)
            }
            .frame(height: 160)
            HStack {
                Spacer()
                Button("Copy all") { model.copyAllOcrText() }
                    .buttonStyle(.plain)
                    .font(.system(size: 12))
                    .foregroundColor(Color(nsColor: Theme.accent))
            }
        }
        .padding(12)
        .background(Color(nsColor: Theme.surfaceHover))
        .clipShape(RoundedRectangle(cornerRadius: 8))
    }
}

/// AppKit overlay owning mouse tracking for each tile — SwiftUI's onDrag
/// cannot express file promises, and the click/drag threshold logic needs
/// real events. 4 pt threshold; below it, mouse-up = click = open preview.
struct ArchiveDragHost: NSViewRepresentable {
    let entry: ArchiveEntry
    let model: ArchiveModel

    func makeNSView(context: Context) -> ArchiveDragHostView {
        let view = ArchiveDragHostView()
        view.entry = entry
        view.model = model
        return view
    }

    func updateNSView(_ nsView: ArchiveDragHostView, context: Context) {
        nsView.entry = entry
        nsView.model = model
    }
}

// @preconcurrency, same as ShotCardView: the SDK's isolation annotation on
// NSDraggingSource differs across releases; these callbacks are main-thread.
final class ArchiveDragHostView: NSView, @preconcurrency NSDraggingSource {
    var entry: ArchiveEntry?
    weak var model: ArchiveModel?

    private var pressAt: NSPoint = .zero
    private var pressed = false
    // The provider's write can outlive the dragging session; the delegate
    // must outlive both, so the view keeps the last one alive.
    private var promiseDelegate: ShotPromiseDelegate?

    override func acceptsFirstMouse(for event: NSEvent?) -> Bool { true }

    override func mouseDown(with event: NSEvent) {
        pressed = true
        pressAt = convert(event.locationInWindow, from: nil)
        // Remote: start the download NOW, so by the time a drag crosses the
        // threshold (or a preview opens) the file is usually already local.
        if let entry, entry.isRemote { _ = entry.materialize() }
    }

    override func mouseDragged(with event: NSEvent) {
        guard pressed, let entry else { return }
        let p = convert(event.locationInWindow, from: nil)
        guard abs(p.x - pressAt.x) >= 4 || abs(p.y - pressAt.y) >= 4 else { return }
        pressed = false
        beginDrag(with: event, entry: entry)
    }

    override func mouseUp(with event: NSEvent) {
        // A press that never crossed the drag threshold is a click: preview.
        if pressed, let entry { model?.openPreview(entry) }
        pressed = false
    }

    private func beginDrag(with event: NSEvent, entry: ArchiveEntry) {
        let item: NSDraggingItem

        if entry.isRemote, let dto = entry.dto {
            // File promise: the payload resolves AFTER the drop, streaming
            // from the peer — a cold drag works with no prefetch and no
            // stall. This asymmetry is why the Mac drag-out beats the
            // Windows one (docs/MAC.md).
            let ext = (dto.fileName as NSString).pathExtension
            let type = UTType(filenameExtension: ext) ?? .data
            let delegate = ShotPromiseDelegate(fileName: dto.fileName, fetch: entry.materialize())
            promiseDelegate = delegate
            let provider = NSFilePromiseProvider(fileType: type.identifier, delegate: delegate)
            item = NSDraggingItem(pasteboardWriter: provider)
        } else {
            // Local: file URL + PNG + TIFF in one item, so Finder-likes take
            // the file while image-first targets take bytes.
            item = NSDraggingItem(pasteboardWriter: ShotPasteboard.pasteboardItem(for: entry.shot))
        }

        let dragImage = entry.thumbnail
            ?? NSImage(systemSymbolName: "photo", accessibilityDescription: nil)
        item.setDraggingFrame(bounds, contents: dragImage)
        beginDraggingSession(with: [item], event: event, source: self)
    }

    // ---- NSDraggingSource -----------------------------------------------------

    func draggingSession(_ session: NSDraggingSession,
                         sourceOperationMaskFor context: NSDraggingContext) -> NSDragOperation {
        .copy
    }

    func draggingSession(_ session: NSDraggingSession, willBeginAt screenPoint: NSPoint) {
        // The live poll must sit out until the drop completes — a mid-drag
        // refresh would tear the tile out from under the drag.
        model?.dragSuspended = true
    }

    func draggingSession(_ session: NSDraggingSession, endedAt screenPoint: NSPoint,
                         operation: NSDragOperation) {
        model?.dragSuspended = false
    }

    // ---- context menu ------------------------------------------------------------

    override func menu(for event: NSEvent) -> NSMenu? {
        guard let entry else { return nil }
        let menu = NSMenu()

        func add(_ title: String, _ selector: Selector) {
            let item = NSMenuItem(title: title, action: selector, keyEquivalent: "")
            item.target = self
            menu.addItem(item)
        }

        add("Copy to clipboard", #selector(menuCopy))
        if !entry.shot.isVideo {
            add("Copy text", #selector(menuCopyText))
        }
        if entry.isRemote {
            add("Pull to this Mac", #selector(menuPull))
        } else {
            add("Show in Finder", #selector(menuReveal))
        }
        return menu
    }

    @objc private func menuCopy() {
        if let entry { model?.copy(entry) }
    }

    @objc private func menuCopyText() {
        if let entry { model?.copyOcrText(entry) }
    }

    @objc private func menuPull() {
        if let entry { model?.pull(entry) }
    }

    @objc private func menuReveal() {
        if let entry { model?.reveal(entry) }
    }
}

/// Fulfils a remote tile's file promise: await the shared download task,
/// copy the cached file to wherever the drop landed. Runs on its own queue —
/// blocking the main thread inside a drop would beachball the drop target's
/// process too.
final class ShotPromiseDelegate: NSObject, NSFilePromiseProviderDelegate {
    private let fileName: String
    private let fetch: Task<Shot, Error>
    private let queue: OperationQueue

    init(fileName: String, fetch: Task<Shot, Error>) {
        self.fileName = fileName
        self.fetch = fetch
        self.queue = OperationQueue()
        self.queue.maxConcurrentOperationCount = 1
        super.init()
    }

    func filePromiseProvider(_ filePromiseProvider: NSFilePromiseProvider,
                             fileNameForType fileType: String) -> String {
        fileName
    }

    func operationQueue(for filePromiseProvider: NSFilePromiseProvider) -> OperationQueue {
        queue
    }

    func filePromiseProvider(_ filePromiseProvider: NSFilePromiseProvider,
                             writePromiseTo url: URL,
                             completionHandler: @escaping (Error?) -> Void) {
        let fetch = self.fetch
        let done = PromiseCompletion(completionHandler)
        Task.detached {
            do {
                let local = try await fetch.value
                try FileManager.default.copyItem(at: URL(fileURLWithPath: local.path), to: url)
                done.finish(nil)
            } catch {
                Log.error("archive drag failed: \(error.localizedDescription)")
                done.finish(error)
            }
        }
    }
}

/// The SDK doesn't mark the promise completion handler Sendable; boxing it is
/// the contained way to carry it into the download task.
private final class PromiseCompletion: @unchecked Sendable {
    private let fn: (Error?) -> Void
    init(_ fn: @escaping (Error?) -> Void) { self.fn = fn }
    func finish(_ error: Error?) { fn(error) }
}
