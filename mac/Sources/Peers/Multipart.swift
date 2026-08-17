import Foundation

/// One part of a multipart/form-data body.
struct MultipartPart {
    let name: String
    let fileName: String?
    let body: [UInt8]
}

/// Just enough multipart/form-data parsing for /ingest — a handful of named
/// parts, binary-safe, no nesting. Bodies are scanned as bytes end to end;
/// converting a PNG through String would corrupt it, so only part headers
/// ever become text.
enum Multipart {
    static func parse(_ req: HttpRequest) -> [MultipartPart]? {
        guard let contentType = req.header("Content-Type") else { return nil }
        guard contentType.range(of: "multipart/", options: .caseInsensitive) != nil,
              let marker = contentType.range(of: "boundary=", options: .caseInsensitive)
        else { return nil }

        let boundary = String(contentType[marker.upperBound...])
            .trimmingCharacters(in: .whitespaces)
            .trimmingCharacters(in: CharacterSet(charactersIn: "\""))
        let delimiter = Array("--\(boundary)".utf8)
        let crlfcrlf: [UInt8] = [13, 10, 13, 10]
        let body = req.body

        var parts: [MultipartPart] = []
        var pos = indexOf(body, delimiter, from: 0)
        while pos >= 0 {
            pos += delimiter.count
            // "--" right after the delimiter = closing boundary.
            if pos + 1 < body.count,
               body[pos] == UInt8(ascii: "-"), body[pos + 1] == UInt8(ascii: "-") {
                break
            }
            pos += 2 // CRLF after the delimiter

            let headerEnd = indexOf(body, crlfcrlf, from: pos)
            if headerEnd < 0 { break }
            guard pos <= headerEnd,
                  let headerText = String(bytes: body[pos..<headerEnd], encoding: .utf8)
            else { break }
            let contentStart = headerEnd + 4

            let next = indexOf(body, delimiter, from: contentStart)
            if next < 0 { break }
            let contentEnd = next - 2 // strip the CRLF before the boundary

            if let name = headerValue(headerText, "name"), contentEnd >= contentStart {
                parts.append(MultipartPart(name: name,
                                           fileName: headerValue(headerText, "filename"),
                                           body: Array(body[contentStart..<contentEnd])))
            }

            pos = next
        }

        return parts
    }

    /// Content-Disposition attribute value, quoted or bare — curl sends
    /// name="meta", .NET's MultipartFormDataContent sends name=meta.
    private static func headerValue(_ headers: String, _ attr: String) -> String? {
        // "; name=" (or the ";"-joined form) so `name=` can't match inside
        // `filename=`.
        for marker in ["; " + attr + "=", ";" + attr + "="] {
            guard let range = headers.range(of: marker, options: .caseInsensitive) else { continue }
            var start = range.upperBound
            if start < headers.endIndex, headers[start] == "\"" {
                start = headers.index(after: start)
                guard let end = headers[start...].firstIndex(of: "\"") else { return nil }
                return String(headers[start..<end])
            }
            let stops: Set<Character> = [";", "\r", "\n"]
            let end = headers[start...].firstIndex(where: { stops.contains($0) }) ?? headers.endIndex
            return String(headers[start..<end]).trimmingCharacters(in: .whitespaces)
        }
        return nil
    }

    private static func indexOf(_ haystack: [UInt8], _ needle: [UInt8], from start: Int) -> Int {
        guard !needle.isEmpty, start >= 0 else { return -1 }
        var i = start
        while i + needle.count <= haystack.count {
            var match = true
            var j = 0
            while j < needle.count {
                if haystack[i + j] != needle[j] { match = false; break }
                j += 1
            }
            if match { return i }
            i += 1
        }
        return -1
    }
}
