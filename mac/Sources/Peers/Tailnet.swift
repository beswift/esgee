import Foundation
import Darwin

/// One tailnet machine, as reported by `tailscale status --json`.
struct TailnetNode: Sendable {
    let hostName: String
    let ip: String
    let online: Bool
    let isSelf: Bool
}

/// Address discovery. Better than the Windows build on purpose: self-address
/// comes from getifaddrs, not the CLI. On macOS the tailscale binary is a
/// moving target — the standalone build lives in /usr/local/bin, the App
/// Store build is buried inside Tailscale.app — so the server startup path
/// must not depend on finding it (docs/MAC.md "Peer layer").
enum Tailnet {

    /// This machine's IPv4 tailnet address (100.x.y.z), or nil when Tailscale
    /// is not running. First IPv4 in 100.64.0.0/10 across up interfaces —
    /// Tailscale addresses always live in that CGNAT range. No CLI, no path
    /// guessing, no subprocess; PeerServer.tryStart sits on this call and a
    /// process spawn there would be a startup stall.
    static func selfIPv4() -> String? {
        var first: UnsafeMutablePointer<ifaddrs>?
        guard getifaddrs(&first) == 0, let start = first else { return nil }
        defer { freeifaddrs(start) }

        var cursor: UnsafeMutablePointer<ifaddrs>? = start
        while let ifa = cursor {
            defer { cursor = ifa.pointee.ifa_next }

            guard (ifa.pointee.ifa_flags & UInt32(IFF_UP)) != 0 else { continue }
            guard let sa = ifa.pointee.ifa_addr,
                  sa.pointee.sa_family == sa_family_t(AF_INET) else { continue }

            var sin = sockaddr_in()
            memcpy(&sin, sa, MemoryLayout<sockaddr_in>.size)
            var addr = sin.sin_addr
            var buf = [CChar](repeating: 0, count: Int(INET_ADDRSTRLEN))
            guard inet_ntop(AF_INET, &addr, &buf, socklen_t(INET_ADDRSTRLEN)) != nil else {
                continue
            }
            let ip = String(cString: buf)
            if isCgnat(ip) { return ip }
        }
        return nil
    }

    /// Tailscale assigns from 100.64.0.0/10 — the CGNAT block. The range test
    /// alone (without an adapter-name check) is acceptable here because the
    /// only machines that assign from that block on a developer Mac are mesh
    /// VPNs; a false positive still binds a private, non-routable address.
    private static func isCgnat(_ ip: String) -> Bool {
        let parts = ip.split(separator: ".").compactMap { UInt8($0) }
        guard parts.count == 4 else { return false }
        return parts[0] == 100 && (parts[1] & 0b1100_0000) == 0b0100_0000
    }

    /// All tailnet nodes (self included) with an IPv4 address. Fleet
    /// enumeration still needs `tailscale status --json` — interface scanning
    /// can find OUR address but not the fleet. Blocking (subprocess, 10 s
    /// cap) — never call on the main actor.
    static func nodes() -> [TailnetNode] {
        guard let json = run(["status", "--json"]) else { return [] }

        var nodes: [TailnetNode] = []
        do {
            guard let root = try JSONSerialization.jsonObject(with: json) as? [String: Any] else {
                return nodes
            }
            if let selfEl = root["Self"] as? [String: Any],
               let me = parse(selfEl, isSelf: true) {
                nodes.append(me)
            }
            if let peers = root["Peer"] as? [String: Any] {
                for (_, value) in peers {
                    if let el = value as? [String: Any],
                       let node = parse(el, isSelf: false) {
                        nodes.append(node)
                    }
                }
            }
        } catch {
            Log.warn("tailscale status parse failed: \(error.localizedDescription)")
        }
        return nodes
    }

    private static func parse(_ el: [String: Any], isSelf: Bool) -> TailnetNode? {
        guard let host = el["HostName"] as? String else { return nil }
        let online = (el["Online"] as? Bool) ?? false

        // First entry without a colon is the IPv4; the list also carries the
        // node's IPv6 tailnet address.
        var ip: String?
        if let ips = el["TailscaleIPs"] as? [Any] {
            ip = ips.compactMap { $0 as? String }.first { !$0.contains(":") }
        }
        guard let ip else { return nil }
        return TailnetNode(hostName: host, ip: ip, online: online, isSelf: isSelf)
    }

    /// CLI candidates, first that exists wins: "tailscale" on PATH, then the
    /// standalone install, Homebrew, and the App Store bundle — the four
    /// places the binary is actually found in the wild.
    private static func cliPath() -> String? {
        var candidates: [String] = []
        if let pathVar = ProcessInfo.processInfo.environment["PATH"] {
            for dir in pathVar.split(separator: ":") where !dir.isEmpty {
                candidates.append(String(dir) + "/tailscale")
            }
        }
        candidates.append("/usr/local/bin/tailscale")
        candidates.append("/opt/homebrew/bin/tailscale")
        candidates.append("/Applications/Tailscale.app/Contents/MacOS/Tailscale")

        let fm = FileManager.default
        return candidates.first { fm.isExecutableFile(atPath: $0) }
    }

    private static func run(_ args: [String]) -> Data? {
        guard let path = cliPath() else {
            Log.warn("tailscale CLI unavailable: no binary found")
            return nil
        }

        let proc = Process()
        proc.executableURL = URL(fileURLWithPath: path)
        proc.arguments = args
        let out = Pipe()
        proc.standardOutput = out
        proc.standardError = Pipe()

        do {
            try proc.run()
        } catch {
            Log.warn("tailscale CLI unavailable: \(error.localizedDescription)")
            return nil
        }

        // A hung CLI must not wedge discovery forever. The watchdog captures
        // only the pid (Process itself is not Sendable), and SIGKILL — not
        // SIGTERM — because a kill that can be ignored is not a cap: the
        // read below blocks until the pipe reaches EOF.
        let pid = proc.processIdentifier
        let watchdog = DispatchWorkItem { kill(pid, SIGKILL) }
        DispatchQueue.global(qos: .utility).asyncAfter(deadline: .now() + 10, execute: watchdog)

        let data = out.fileHandleForReading.readDataToEndOfFile()
        proc.waitUntilExit()
        watchdog.cancel()

        return proc.terminationStatus == 0 ? data : nil
    }
}
