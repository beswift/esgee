import Foundation

/// Mirrors the Windows Settings shape — PascalCase JSON keys included — so
/// the documented "copy PeerToken / Peers entries between machines'
/// settings.json" workflow survives the platform crossing byte for byte.
/// Recording fields are deliberately absent: no recorder ships in Mac v1,
/// and adding them later is additive.
struct Settings: Codable, Sendable {
    /// Where PNGs and the index live. NOT ~/Pictures — Pictures is a Photos
    /// library and iCloud-sync target, and thousands of PNGs a day through
    /// iCloud is its own outage. Top of the home folder, trivial to find.
    var archiveRoot: String = Settings.defaultArchiveRoot()

    /// Seconds a card sits on the shelf before it fades.
    var lingerSeconds: Int = 8

    /// Cards visible at once before the oldest is pushed off.
    var maxCards: Int = 6

    /// Run OCR over each capture so the archive is full-text searchable.
    var ocrEnabled: Bool = true

    /// ⌃⇧ + the same letters the Windows app uses, so muscle memory
    /// transfers; the system owns ⌘⇧3/4/5/6 (docs/MAC.md "Hotkeys"). "Ctrl"
    /// means the Control key here — a chord string that fails to parse or
    /// register logs and is skipped, never aborts startup.
    var regionHotkey: String = "Ctrl+Shift+S"

    /// Captures every display immediately — no overlay.
    var fullscreenHotkey: String = "Ctrl+Shift+F"

    /// Re-captures the last selected rectangle immediately. Falls back to
    /// the region overlay if no region has been selected yet.
    var lastRegionHotkey: String = "Ctrl+Shift+L"

    /// Timed capture: countdown pill, then the region overlay opens on a
    /// freshly frozen frame — for hover states and menus you have to arm.
    var timerHotkey: String = "Ctrl+Shift+D"

    /// Opens the archive window. Mac-only chord: Windows reaches the archive
    /// from the tray, the Mac menu bar item is further away.
    var archiveHotkey: String = "Ctrl+Shift+A"

    /// Seconds on the timed-capture fuse.
    var timerSeconds: Int = 3

    /// [x, y, w, h] of the last committed selection in Cocoa global points
    /// (origin bottom-left of the primary display), persisted so "repeat
    /// last region" survives restarts. Same idea as Windows, different
    /// coordinate space — the value never travels between platforms.
    var lastRegion: [Int]?

    // ---- Peers (machine-to-machine over Tailscale) --------------------------
    // All OFF by default. With peersEnabled=false esgee opens zero sockets
    // and behaves exactly as if these settings did not exist.

    /// Serve this machine's archive to other machines on the tailnet. The
    /// server binds ONLY to this machine's Tailscale IP — never a public or
    /// LAN interface — and every request must carry peerToken.
    var peersEnabled: Bool = false

    /// TCP port for the peer API (on the Tailscale IP only).
    var peerPort: Int = 43117

    /// Shared secret for the peer API. Minted automatically the first time
    /// peers are enabled or a pairing succeeds; the same value must be
    /// present on every machine that should talk to this one.
    var peerToken: String = ""

    /// Manual peer list — "name=host:port", "host:port", or a full base URL —
    /// the fallback for when tailscale-status discovery can't see a machine.
    var peers: [String] = []

    /// When set (a tailnet machine name, "host:port", or base URL), every new
    /// capture is pushed to that peer in the background. Empty = no push sync.
    var syncTargetPeer: String = ""

    enum CodingKeys: String, CodingKey {
        case archiveRoot = "ArchiveRoot"
        case lingerSeconds = "LingerSeconds"
        case maxCards = "MaxCards"
        case ocrEnabled = "OcrEnabled"
        case regionHotkey = "RegionHotkey"
        case fullscreenHotkey = "FullscreenHotkey"
        case lastRegionHotkey = "LastRegionHotkey"
        case timerHotkey = "TimerHotkey"
        case archiveHotkey = "ArchiveHotkey"
        case timerSeconds = "TimerSeconds"
        case lastRegion = "LastRegion"
        case peersEnabled = "PeersEnabled"
        case peerPort = "PeerPort"
        case peerToken = "PeerToken"
        case peers = "Peers"
        case syncTargetPeer = "SyncTargetPeer"
    }

    init() {}

    /// Hand-rolled so a partial or older settings.json loads with defaults
    /// filling the gaps — additive settings must never brick a launch.
    init(from decoder: Decoder) throws {
        let c = try decoder.container(keyedBy: CodingKeys.self)
        let d = Settings()
        archiveRoot = (try? c.decodeIfPresent(String.self, forKey: .archiveRoot)) ?? d.archiveRoot
        lingerSeconds = (try? c.decodeIfPresent(Int.self, forKey: .lingerSeconds)) ?? d.lingerSeconds
        maxCards = (try? c.decodeIfPresent(Int.self, forKey: .maxCards)) ?? d.maxCards
        ocrEnabled = (try? c.decodeIfPresent(Bool.self, forKey: .ocrEnabled)) ?? d.ocrEnabled
        regionHotkey = (try? c.decodeIfPresent(String.self, forKey: .regionHotkey)) ?? d.regionHotkey
        fullscreenHotkey = (try? c.decodeIfPresent(String.self, forKey: .fullscreenHotkey)) ?? d.fullscreenHotkey
        lastRegionHotkey = (try? c.decodeIfPresent(String.self, forKey: .lastRegionHotkey)) ?? d.lastRegionHotkey
        timerHotkey = (try? c.decodeIfPresent(String.self, forKey: .timerHotkey)) ?? d.timerHotkey
        archiveHotkey = (try? c.decodeIfPresent(String.self, forKey: .archiveHotkey)) ?? d.archiveHotkey
        timerSeconds = (try? c.decodeIfPresent(Int.self, forKey: .timerSeconds)) ?? d.timerSeconds
        lastRegion = (try? c.decodeIfPresent([Int].self, forKey: .lastRegion)) ?? d.lastRegion
        peersEnabled = (try? c.decodeIfPresent(Bool.self, forKey: .peersEnabled)) ?? d.peersEnabled
        peerPort = (try? c.decodeIfPresent(Int.self, forKey: .peerPort)) ?? d.peerPort
        peerToken = (try? c.decodeIfPresent(String.self, forKey: .peerToken)) ?? d.peerToken
        peers = (try? c.decodeIfPresent([String].self, forKey: .peers)) ?? d.peers
        syncTargetPeer = (try? c.decodeIfPresent(String.self, forKey: .syncTargetPeer)) ?? d.syncTargetPeer
    }

    /// ~/Library/Application Support/esgee/settings.json — beside the log,
    /// away from the archive, so wiping the archive never loses the pairing.
    static let fileURL: URL = {
        FileManager.default
            .urls(for: .applicationSupportDirectory, in: .userDomainMask)[0]
            .appendingPathComponent("esgee", isDirectory: true)
            .appendingPathComponent("settings.json")
    }()

    static func defaultArchiveRoot() -> String {
        FileManager.default.homeDirectoryForCurrentUser
            .appendingPathComponent("esgee", isDirectory: true).path
    }

    static func load() -> Settings {
        if !FileManager.default.fileExists(atPath: fileURL.path) {
            let fresh = Settings()
            fresh.save() // so there's something to edit on first run
            return fresh
        }

        do {
            var loaded = try JSONDecoder().decode(Settings.self, from: Data(contentsOf: fileURL))
            loaded.enforceInvariants()
            return loaded
        } catch {
            // Unreadable is NOT replaceable: the file may hold a PeerToken
            // recoverable only by re-pairing, and hand-editing it is a
            // documented workflow. Run on defaults but leave the file exactly
            // as the user left it — same rule as the C# Settings.Load.
            Log.warn("settings unreadable, running on defaults; " +
                     "file left for repair: \(error.localizedDescription)")
        }
        return Settings()
    }

    /// Structural rules the rest of the app may assume after load — the Mac
    /// subset of the C# EnforceInvariants. Hand-editing settings.json is a
    /// documented workflow, and a blank ArchiveRoot (a template copied from
    /// another machine) must degrade to the default, never make the app
    /// unlaunchable. Heals in memory only; the file stays as written.
    mutating func enforceInvariants() {
        if archiveRoot.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty {
            archiveRoot = Settings.defaultArchiveRoot()
        }
    }

    func save() {
        do {
            try FileManager.default.createDirectory(
                at: Self.fileURL.deletingLastPathComponent(),
                withIntermediateDirectories: true)
            let enc = JSONEncoder()
            enc.outputFormatting = [.prettyPrinted]
            try enc.encode(self).write(to: Self.fileURL, options: .atomic)
        } catch {
            Log.warn("settings save failed: \(error.localizedDescription)")
        }
    }
}

/// The one mutable copy. Main-actor code reads `current` freely; background
/// components take value snapshots at construction and are torn down and
/// rebuilt when the fields they were built from change (token adoption
/// restarts the peer server — same shape as Windows). This split is what
/// keeps Settings freely Sendable.
@MainActor
final class SettingsStore {
    private(set) var current: Settings

    init(_ loaded: Settings) {
        current = loaded
    }

    /// Mutate-and-persist in one step so no call site can forget the save.
    func update(_ mutate: (inout Settings) -> Void) {
        mutate(&current)
        current.save()
    }
}
