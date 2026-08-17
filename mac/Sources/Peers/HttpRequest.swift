import Foundation

/// A parsed HTTP request: line, headers, and (for POST) the body. Mirrors the
/// C# HttpRequest in PeerServer.cs — same limits, same tolerances — so the two
/// servers accept and reject exactly the same traffic.
struct HttpRequest {
    /// A recording plus its GIF can be large, but this is a private API on a
    /// private network — cap the body so a bug can't balloon memory forever.
    static let maxBody: Int64 = 1 << 30 // 1 GB

    let method: String
    let rawPath: String
    let path: String
    let body: [UInt8]

    // Keys lowercased at parse time: header names are case-insensitive and
    // clients genuinely vary (URLSession title-cases, curl doesn't).
    private let headerFields: [String: String]
    private let queryItems: [String: String]

    func header(_ name: String) -> String? { headerFields[name.lowercased()] }

    func query(_ key: String) -> String? { queryItems[key.lowercased()] }

    func queryInt(_ key: String) -> Int? { query(key).flatMap(Int.init) }

    /// Reads one request off a connection. `nextChunk` blocks until bytes
    /// arrive and returns nil on EOF or timeout — the caller owns the socket
    /// and its guard timers; this function only owns the framing.
    static func read(using nextChunk: () -> Data?) -> HttpRequest? {
        // Accumulate until the blank line ending the headers; anything after
        // it in the same reads is the start of the body.
        var buffer: [UInt8] = []
        var headerEnd = findDoubleCrlf(buffer)
        while headerEnd < 0 {
            if buffer.count > 64 * 1024 { return nil } // header flood
            guard let chunk = nextChunk(), !chunk.isEmpty else { return nil }
            buffer.append(contentsOf: chunk)
            headerEnd = findDoubleCrlf(buffer)
        }

        guard let headerText = String(bytes: buffer[0..<headerEnd], encoding: .ascii) else {
            return nil
        }
        let lines = headerText.components(separatedBy: "\r\n")
        let requestLine = lines[0].components(separatedBy: " ")
        guard requestLine.count >= 2 else { return nil }

        var headers: [String: String] = [:]
        for line in lines.dropFirst() {
            guard let colon = line.firstIndex(of: ":"), colon != line.startIndex else { continue }
            let key = String(line[..<colon]).trimmingCharacters(in: .whitespaces).lowercased()
            let value = String(line[line.index(after: colon)...]).trimmingCharacters(in: .whitespaces)
            headers[key] = value
        }

        var body: [UInt8] = []
        if let lenText = headers["content-length"], let len = Int64(lenText), len > 0 {
            if len > maxBody { return nil }
            let want = Int(len)
            let start = headerEnd + 4
            if start < buffer.count {
                body = Array(buffer[start..<min(buffer.count, start + want)])
            }
            while body.count < want {
                guard let chunk = nextChunk(), !chunk.isEmpty else { return nil } // truncated
                body.append(contentsOf: chunk)
            }
            if body.count > want { body.removeSubrange(want...) }
        }

        return HttpRequest(method: requestLine[0], rawPath: requestLine[1],
                           body: body, headers: headers)
    }

    private init(method: String, rawPath: String, body: [UInt8], headers: [String: String]) {
        self.method = method
        self.rawPath = rawPath
        self.body = body
        self.headerFields = headers

        // Query parsing matches the C# side exactly: '&'-split, percent-decode,
        // '+' means space in values only — keys pass through untouched.
        if let q = rawPath.firstIndex(of: "?") {
            self.path = String(rawPath[..<q])
            var items: [String: String] = [:]
            let queryText = String(rawPath[rawPath.index(after: q)...])
            for pair in queryText.components(separatedBy: "&") where !pair.isEmpty {
                if let eq = pair.firstIndex(of: "=") {
                    let rawKey = String(pair[..<eq])
                    let key = rawKey.removingPercentEncoding ?? rawKey
                    let rawValue = String(pair[pair.index(after: eq)...])
                        .replacingOccurrences(of: "+", with: " ")
                    items[key.lowercased()] = rawValue.removingPercentEncoding ?? rawValue
                } else {
                    let key = pair.removingPercentEncoding ?? pair
                    items[key.lowercased()] = ""
                }
            }
            self.queryItems = items
        } else {
            self.path = rawPath
            self.queryItems = [:]
        }
    }

    private static func findDoubleCrlf(_ data: [UInt8]) -> Int {
        guard data.count >= 4 else { return -1 }
        for i in 0...(data.count - 4) {
            if data[i] == 13, data[i + 1] == 10, data[i + 2] == 13, data[i + 3] == 10 {
                return i
            }
        }
        return -1
    }
}
