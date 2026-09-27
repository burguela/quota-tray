import Foundation

/// Conflicting ownership must never be treated as an unattributed session.
enum ClaudeSessionIdentity: Equatable, Sendable {
    case owned(organizationID: String, accountID: String?)
    case unattributed
    case conflicted

    /// Jump from one owner marker to the next with C-speed byte searches, parsing only the records that
    /// carry one. Even a huge single record checks cancellation each megabyte; a marker that straddles a
    /// megabyte edge is still found.
    static func parse(
        _ data: Data,
        isCancelled: () -> Bool = { Task.isCancelled }
    ) -> Self? {
        data.withUnsafeBytes { parse(bytes: $0, isCancelled: isCancelled) }
    }

    private static let marker = Array(#""ownerOrganizationUuid""#.utf8)
    private static let chunkSize = 1_048_576

    private static func parse(bytes: UnsafeRawBufferPointer, isCancelled: () -> Bool) -> Self? {
        let newline = UInt8(ascii: "\n")
        var owner: String?
        var account: String?
        // Start of the first line not yet parsed, and where the next marker search begins.
        var lineFloor = 0
        var cursor = 0
        while cursor < bytes.count {
            guard !isCancelled() else { return nil }
            let chunkEnd = min(cursor + chunkSize, bytes.count)
            let searchEnd = min(chunkEnd + marker.count - 1, bytes.count)
            guard let hit = ByteSearch.firstIndex(
                of: marker, in: UnsafeRawBufferPointer(rebasing: bytes[cursor..<searchEnd])
            ) else {
                cursor = chunkEnd
                continue
            }
            let position = cursor + hit
            var lineStart = position
            while lineStart > lineFloor, bytes[lineStart - 1] != newline { lineStart -= 1 }
            let lineEnd = ByteSearch.firstIndex(of: newline, in: bytes, from: position) ?? bytes.count
            lineFloor = min(lineEnd + 1, bytes.count)
            cursor = lineFloor
            let line = Data(UnsafeRawBufferPointer(rebasing: bytes[lineStart..<lineEnd]))
            guard let object = try? JSONSerialization.jsonObject(with: line) as? [String: Any],
                  let value = object["ownerOrganizationUuid"] as? String, !value.isEmpty
            else { continue }
            let candidate = value.lowercased()
            if let owner, owner != candidate { return .conflicted }
            owner = candidate
            if let value = object["ownerAccountUuid"] as? String, !value.isEmpty {
                let candidate = value.lowercased()
                if let account, account != candidate { return .conflicted }
                account = candidate
            }
        }
        guard !isCancelled() else { return nil }
        return owner.map { .owned(organizationID: $0, accountID: account) } ?? .unattributed
    }
}
