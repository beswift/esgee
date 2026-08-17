import Foundation
import Darwin

/// A peer we can talk to: display name + the base URL its API lives at
/// (e.g. http://100.72.104.102:43117). The URL is opaque past construction —
/// routes are appended to it, never rebuilt from host/port parts, so a future
/// https endpoint with a path differs only in this string
/// (docs/PROTOCOL.md "Addressing", proto 2).
struct PeerInfo: Sendable, Hashable {
    let name: String
    let baseURL: URL
}

/// Client side of the peer API. One instance per peer; every call is async
/// and safe from any thread — the archive window keeps the main actor clean
/// by doing every network hop through methods here.
///
/// Files fetched from a peer are materialized into a local cache so anything
/// OS-facing (Show in Finder, Pull, the pasteboard) gets a real local path,
/// and so re-drags don't re-download. On Mac the cache is an optimization,
/// not a prerequisite: drag-out streams through a file promise
/// (docs/MAC.md "Drag-out").
final class PeerClient: Sendable {
    let peer: PeerInfo

    private let session: URLSession
    /// Base URL with a guaranteed trailing slash: trailing slash + relative
    /// route paths = true concatenation, so a base URL that carries a path
    /// (a hosted relay) keeps working.
    private let base: URL

    /// ~/Library/Application Support/esgee/peercache. Layout:
    /// <sanitized peer name>/<id>_<fileName> — the id prefix keeps two
    /// same-named captures from different days from colliding.
    static var cacheRoot: URL {
        FileManager.default
            .urls(for: .applicationSupportDirectory, in: .userDomainMask)[0]
            .appendingPathComponent("esgee", isDirectory: true)
            .appendingPathComponent("peercache", isDirectory: true)
    }

    init(peer: PeerInfo, token: String) {
        self.peer = peer

        let raw = peer.baseURL.absoluteString
        self.base = URL(string: raw.hasSuffix("/") ? raw : raw + "/") ?? peer.baseURL

        let config = URLSessionConfiguration.ephemeral
        // 5 minutes: big MP4 pulls over a relay link.
        config.timeoutIntervalForRequest = 300
        config.timeoutIntervalForResource = 300
        config.httpAdditionalHeaders = [PeerProtocol.tokenHeader: token]
        self.session = URLSession(configuration: config)
    }

    // ---- queries ------------------------------------------------------------

    func ping(timeout: TimeInterval) async throws -> PingDto {
        var request = URLRequest(url: try route("ping"))
        request.timeoutInterval = timeout
        let (data, response) = try await session.data(for: request)
        try Self.ensureOK(response)
        return try PeerProtocol.makeDecoder().decode(PingDto.self, from: data)
    }

    func recent(_ n: Int) async throws -> [ShotDto] {
        let list: [ShotDto] = try await getJSON("recent?n=\(n)")
        Log.info("peer \(peer.name): /recent n=\(n) -> \(list.count) items")
        return list
    }

    func search(_ query: String) async throws -> [ShotDto] {
        let escaped = query.addingPercentEncoding(
            withAllowedCharacters: Self.queryValueAllowed) ?? query
        let list: [ShotDto] = try await getJSON("search?q=\(escaped)")
        Log.info("peer \(peer.name): /search \"\(query)\" -> \(list.count) items")
        return list
    }

    func meta(id: Int64) async throws -> ShotDto? {
        let (data, response) = try await session.data(from: try route("meta/\(id)"))
        guard let http = response as? HTTPURLResponse else {
            throw URLError(.badServerResponse)
        }
        if http.statusCode == 404 { return nil }
        guard (200..<300).contains(http.statusCode) else {
            throw URLError(.badServerResponse)
        }
        return try PeerProtocol.makeDecoder().decode(ShotDto.self, from: data)
    }

    func thumb(id: Int64) async throws -> Data {
        let (data, response) = try await session.data(from: try route("thumb/\(id)"))
        try Self.ensureOK(response)
        return data
    }

    // ---- local materialization ----------------------------------------------

    /// Cache path this peer's shot will occupy locally. Prefixed with the id
    /// so two same-named captures from different days can't collide. fileName
    /// is wire data — a hostile or compromised server could send
    /// "../../Library/…/x" — so it is flattened through sanitize (path
    /// separators and every other invalid file-name char become '_') before
    /// it may name a file under the cache, exactly like the C# CachePathFor.
    func cachePath(for dto: ShotDto) -> URL {
        Self.cacheRoot
            .appendingPathComponent(Self.sanitize(peer.name), isDirectory: true)
            .appendingPathComponent("\(dto.id)_\(Self.sanitize(dto.fileName))")
    }

    func isCached(_ dto: ShotDto) -> Bool {
        FileManager.default.fileExists(atPath: cachePath(for: dto).path)
    }

    /// Downloads the shot's file (and, for recordings, the GIF + preview-frame
    /// siblings) into the peer cache, then returns a Shot pointing at the
    /// LOCAL copy — from there the ordinary pasteboard/preview code paths work
    /// unchanged. No-op when already cached; ".part" temp + rename keeps a
    /// torn download from ever looking cached.
    func ensureLocal(_ dto: ShotDto) async throws -> Shot {
        let dest = cachePath(for: dto)
        let fm = FileManager.default

        if !fm.fileExists(atPath: dest.path) {
            try fm.createDirectory(at: dest.deletingLastPathComponent(),
                                   withIntermediateDirectories: true)

            try await download("file/\(dto.id)", to: dest)
            if dto.kind == "video" {
                if dto.hasGif {
                    let gif = dest.deletingPathExtension().appendingPathExtension("gif")
                    try await download("file/\(dto.id)?alt=gif", to: gif)
                }
                // The preview frame may not exist on the sender; its absence
                // is not a failed pull.
                let thumb = URL(fileURLWithPath: dest.path + ".png")
                try await download("file/\(dto.id)?alt=thumb", to: thumb, optional: true)
            }

            let attrs = try? fm.attributesOfItem(atPath: dest.path)
            let bytes = (attrs?[.size] as? UInt64) ?? 0
            Log.info("peer \(peer.name): cached shot \(dto.id) -> \(dest.path) " +
                     "(\(bytes / 1024) KB)")
        }

        return toLocalShot(dto, localPath: dest.path)
    }

    /// takenAtRaw travels verbatim (the round-trip rule); takenAt is derived
    /// for display only, with the epoch as the never-drop fallback. A shot
    /// with an empty origin came from the serving machine itself — the peer's
    /// name IS its origin from this side of the wire.
    func toLocalShot(_ dto: ShotDto, localPath: String) -> Shot {
        Shot(id: dto.id, path: localPath,
             takenAt: IsoStamp.parse(dto.takenAt) ?? Date(timeIntervalSince1970: 0),
             takenAtRaw: dto.takenAt,
             width: dto.width, height: dto.height, sha256: dto.sha256,
             kind: dto.kind, durationMs: dto.durationMs,
             origin: dto.origin.isEmpty ? peer.name : dto.origin)
    }

    private func download(_ path: String, to dest: URL, optional: Bool = false) async throws {
        let (tmp, response) = try await session.download(from: try route(path))
        let fm = FileManager.default

        guard let http = response as? HTTPURLResponse,
              (200..<300).contains(http.statusCode) else {
            try? fm.removeItem(at: tmp)
            if optional { return }
            throw URLError(.badServerResponse)
        }

        // ".part" beside the destination, then rename: the rename is the
        // commit, so a crash mid-copy never leaves a plausible-looking file.
        // Unique per attempt — two fetches of the same shot (Entry replaced
        // by a refresh mid-prefetch, or a second archive process) must not
        // fight over one temp name or delete each other's in-flight file
        // (same rule as the C# DownloadAsync).
        let part = URL(fileURLWithPath:
            dest.path + "." + String(UUID().uuidString.prefix(8)) + ".part")
        defer { try? fm.removeItem(at: part) }
        do {
            try fm.moveItem(at: tmp, to: part)
            try? fm.removeItem(at: dest)
            try fm.moveItem(at: part, to: dest)
        } catch {
            // Two racing commits can collide on the destination itself; the
            // bytes are identical, so whoever landed is right.
            guard fm.fileExists(atPath: dest.path) else { throw error }
        }
    }

    // ---- push ---------------------------------------------------------------

    /// POST /ingest: the media file plus its JSON sidecar (and any recording
    /// siblings). The receiver dedupes by sha256, so retries are safe.
    func ingest(meta: IngestMeta, filePath: String,
                gifPath: String?, thumbPath: String?) async throws -> IngestResult {
        let boundary = "esgee-" + UUID().uuidString
        var body = Data()

        let metaJSON = try PeerProtocol.makeEncoder().encode(meta)
        Self.appendPart(name: "meta", filename: nil, contentType: nil,
                        payload: metaJSON, boundary: boundary, into: &body)

        let fileData = try Data(contentsOf: URL(fileURLWithPath: filePath))
        Self.appendPart(name: "file",
                        filename: (filePath as NSString).lastPathComponent,
                        contentType: "application/octet-stream",
                        payload: fileData, boundary: boundary, into: &body)

        if let gifPath, FileManager.default.fileExists(atPath: gifPath) {
            let gifData = try Data(contentsOf: URL(fileURLWithPath: gifPath))
            Self.appendPart(name: "gif",
                            filename: (gifPath as NSString).lastPathComponent,
                            contentType: "application/octet-stream",
                            payload: gifData, boundary: boundary, into: &body)
        }
        if let thumbPath, FileManager.default.fileExists(atPath: thumbPath) {
            let thumbData = try Data(contentsOf: URL(fileURLWithPath: thumbPath))
            Self.appendPart(name: "thumb",
                            filename: (thumbPath as NSString).lastPathComponent,
                            contentType: "application/octet-stream",
                            payload: thumbData, boundary: boundary, into: &body)
        }
        body.append(Data("--\(boundary)--\r\n".utf8))

        var request = URLRequest(url: try route("ingest"))
        request.httpMethod = "POST"
        request.setValue("multipart/form-data; boundary=\(boundary)",
                         forHTTPHeaderField: "Content-Type")
        request.httpBody = body

        let (data, response) = try await session.data(for: request)
        try Self.ensureOK(response)
        return try PeerProtocol.makeDecoder().decode(IngestResult.self, from: data)
    }

    // ---- discovery ----------------------------------------------------------

    /// Peers worth showing in the machine switcher: every candidate that
    /// answers /ping with our token, probed in parallel with a 2 s timeout —
    /// offline nodes just don't appear. Includes this machine itself when its
    /// own server is up; the self-peer loopback is the supported way to test
    /// the peer layer with one machine.
    static func discover(settings: Settings) async -> [(info: PeerInfo, ping: PingDto)] {
        let candidates = candidatePeers(settings: settings)
        let token = settings.peerToken

        var found: [(info: PeerInfo, ping: PingDto)] = []
        await withTaskGroup(of: (PeerInfo, PingDto)?.self) { group in
            for info in candidates {
                group.addTask {
                    // Construction inside the child task: one malformed entry
                    // must not fault the batch and hide every healthy peer.
                    let client = PeerClient(peer: info, token: token)
                    guard let ping = try? await client.ping(timeout: 2),
                          ping.app == "esgee" else {
                        return nil // not running esgee peers, or offline — fine
                    }
                    return (info, ping)
                }
            }
            for await probe in group {
                if let (info, ping) = probe {
                    found.append((info: info, ping: ping))
                }
            }
        }
        Log.info("peers: discovery probed \(candidates.count) candidates, found \(found.count)")
        return found
    }

    /// Everywhere a peer might live: online tailnet nodes (self included —
    /// the loopback config is supported) plus manual Peers entries, deduped
    /// by URL. Blocking (Tailnet.nodes shells out) — never on the main actor.
    /// Entry grammar: "name=addr" or "addr", where addr is a full http(s)
    /// URL, "host:port", or bare host (default port).
    static func candidatePeers(settings: Settings) -> [PeerInfo] {
        var candidates: [PeerInfo] = []

        for node in Tailnet.nodes() where node.online {
            if let url = URL(string: "http://\(node.ip):\(settings.peerPort)") {
                candidates.append(PeerInfo(name: node.hostName, baseURL: url))
            }
        }

        for entry in settings.peers {
            var name = entry
            var addr = entry
            if let eq = entry.firstIndex(of: "="), eq != entry.startIndex {
                name = String(entry[..<eq])
                addr = String(entry[entry.index(after: eq)...])
            }

            if let url = toBaseURL(addr, defaultPort: settings.peerPort) {
                candidates.append(PeerInfo(name: name, baseURL: url))
            } else {
                Log.warn("peers: ignoring malformed Peers entry '\(entry)'")
            }
        }

        var seen = Set<String>()
        return candidates.filter { seen.insert($0.baseURL.absoluteString).inserted }
    }

    /// Manual-entry address → base URL, or nil when the entry can't name an
    /// endpoint (truncated paste, non-numeric port). Full http(s) URLs pass
    /// through (an endpoint may carry a path); bare "host" and "host:port"
    /// expand to the same http://host:port they always did. Validated here so
    /// one bad settings entry is dropped at the edge instead of throwing
    /// later inside a batch of healthy peers.
    static func toBaseURL(_ addr: String, defaultPort: Int) -> URL? {
        var addr = addr.trimmingCharacters(in: .whitespaces)
        let lower = addr.lowercased()

        if !lower.hasPrefix("http://") && !lower.hasPrefix("https://") {
            var host = addr
            var port = defaultPort
            if let colon = addr.lastIndex(of: ":"), colon != addr.startIndex,
               let p = Int(addr[addr.index(after: colon)...]) {
                port = p
                host = String(addr[..<colon])
            }
            addr = "http://\(host):\(port)"
        }

        while addr.hasSuffix("/") { addr.removeLast() }
        guard let url = URL(string: addr), url.scheme != nil, url.host != nil else {
            return nil
        }
        return url
    }

    /// Sync-target address → base URL. Accepts everything toBaseURL does plus
    /// bare tailnet machine names, which resolve through `tailscale status`.
    /// Nil when the entry is malformed or the name isn't on the tailnet right
    /// now. Can shell out — keep off the main actor.
    static func resolveTargetURL(_ target: String, defaultPort: Int) -> URL? {
        var target = target.trimmingCharacters(in: .whitespaces)
        let lower = target.lowercased()

        if !lower.hasPrefix("http://") && !lower.hasPrefix("https://") {
            var host = target
            var port = defaultPort
            if let colon = target.lastIndex(of: ":"), colon != target.startIndex,
               let p = Int(target[target.index(after: colon)...]) {
                host = String(target[..<colon])
                port = p
            }

            if !isIPAddress(host) {
                guard let node = Tailnet.nodes().first(where: {
                    $0.hostName.caseInsensitiveCompare(host) == .orderedSame
                }) else { return nil }
                host = node.ip
            }
            target = "\(host):\(port)"
        }

        return toBaseURL(target, defaultPort: defaultPort)
    }

    // ---- pairing ------------------------------------------------------------

    enum PairOutcome: Sendable {
        case paired
        case wrongPin   // a pairing window IS open over there, but the PIN missed
        case noPairing  // offline, not esgee, or no pairing window open
    }

    struct PairAttempt: Sendable {
        let outcome: PairOutcome
        let result: PairResult?
        let peer: PeerInfo
    }

    /// One POST /pair to one candidate. No token header — the PIN is the
    /// credential; the token is what comes back. Never logs either value.
    static func tryPair(peer: PeerInfo, pin: String, timeout: TimeInterval) async -> PairAttempt {
        let config = URLSessionConfiguration.ephemeral
        config.timeoutIntervalForRequest = timeout
        config.timeoutIntervalForResource = timeout
        let session = URLSession(configuration: config)
        defer { session.finishTasksAndInvalidate() }

        do {
            let raw = peer.baseURL.absoluteString
            guard let url = URL(string: (raw.hasSuffix("/") ? raw : raw + "/") + "pair") else {
                return PairAttempt(outcome: .noPairing, result: nil, peer: peer)
            }
            var request = URLRequest(url: url)
            request.httpMethod = "POST"
            request.setValue("application/json", forHTTPHeaderField: "Content-Type")
            request.httpBody = try PeerProtocol.makeEncoder().encode(
                PairRequest(pin: pin, machine: AppInfo.machineName))

            let (data, response) = try await session.data(for: request)
            guard let http = response as? HTTPURLResponse else {
                return PairAttempt(outcome: .noPairing, result: nil, peer: peer)
            }

            if http.statusCode == 401 {
                // Only a live pairing session says "wrong pin". A pre-pairing
                // esgee answers every tokenless request 401 with a different
                // error — that's noPairing, not a missed PIN.
                let text = String(data: data, encoding: .utf8) ?? ""
                return PairAttempt(
                    outcome: text.contains("wrong pin") ? .wrongPin : .noPairing,
                    result: nil, peer: peer)
            }
            guard (200..<300).contains(http.statusCode) else {
                return PairAttempt(outcome: .noPairing, result: nil, peer: peer)
            }

            let result = try PeerProtocol.makeDecoder().decode(PairResult.self, from: data)
            return result.token.isEmpty
                ? PairAttempt(outcome: .noPairing, result: nil, peer: peer)
                : PairAttempt(outcome: .paired, result: result, peer: peer)
        } catch {
            return PairAttempt(outcome: .noPairing, result: nil, peer: peer) // unreachable — fine
        }
    }

    // ---- plumbing -----------------------------------------------------------

    private func route(_ path: String) throws -> URL {
        guard let url = URL(string: path, relativeTo: base) else {
            throw URLError(.badURL)
        }
        return url
    }

    private func getJSON<T: Decodable>(_ path: String) async throws -> T {
        let (data, response) = try await session.data(from: try route(path))
        try Self.ensureOK(response)
        return try PeerProtocol.makeDecoder().decode(T.self, from: data)
    }

    private static func ensureOK(_ response: URLResponse) throws {
        guard let http = response as? HTTPURLResponse,
              (200..<300).contains(http.statusCode) else {
            throw URLError(.badServerResponse)
        }
    }

    private static func appendPart(name: String, filename: String?, contentType: String?,
                                   payload: Data, boundary: String, into body: inout Data) {
        var head = "--\(boundary)\r\nContent-Disposition: form-data; name=\"\(name)\""
        if let filename { head += "; filename=\"\(filename)\"" }
        head += "\r\n"
        if let contentType { head += "Content-Type: \(contentType)\r\n" }
        head += "\r\n"
        body.append(Data(head.utf8))
        body.append(payload)
        body.append(Data("\r\n".utf8))
    }

    /// Uri.EscapeDataString's set: unreserved characters only, so a query
    /// means the same bytes on the wire from either platform.
    private static let queryValueAllowed: CharacterSet = {
        var set = CharacterSet.alphanumerics
        set.insert(charactersIn: "-._~")
        return set
    }()

    /// Cache folders are named after peers; peer names come off the network.
    private static func sanitize(_ name: String) -> String {
        let invalid: Set<Character> = ["/", "\\", ":", "*", "?", "\"", "<", ">", "|", "\0"]
        return String(name.map { invalid.contains($0) ? "_" : $0 })
    }

    private static func isIPAddress(_ s: String) -> Bool {
        var v4 = in_addr()
        var v6 = in6_addr()
        return s.withCString {
            inet_pton(AF_INET, $0, &v4) == 1 || inet_pton(AF_INET6, $0, &v6) == 1
        }
    }
}
