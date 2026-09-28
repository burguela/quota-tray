import Foundation

/// C-speed byte searches for the local log scanners. Swift's generic `Collection` search over `Data`
/// walks one byte at a time through `Data`'s subscript, which on Linux and Windows made a cold read of
/// a large Claude history take minutes instead of seconds.
enum ByteSearch {
    /// Offset of the first `byte` at or after `start`, or `nil` when there is none.
    static func firstIndex(of byte: UInt8, in haystack: UnsafeRawBufferPointer, from start: Int) -> Int? {
        guard start < haystack.count, let base = haystack.baseAddress,
              let hit = memchr(base + start, Int32(byte), haystack.count - start)
        else { return nil }
        return base.distance(to: UnsafeRawPointer(hit))
    }

    /// Whether `needle` occurs anywhere in `haystack`.
    static func contains(_ needle: [UInt8], in haystack: UnsafeRawBufferPointer) -> Bool {
        firstIndex(of: needle, in: haystack) != nil
    }

    /// Offset of the first occurrence of `needle` in `haystack`, or `nil` when there is none.
    static func firstIndex(of needle: [UInt8], in haystack: UnsafeRawBufferPointer) -> Int? {
        guard let first = needle.first else { return 0 }
        guard haystack.count >= needle.count, let base = haystack.baseAddress else { return nil }
        let lastStart = haystack.count - needle.count
        var offset = 0
        while offset <= lastStart {
            guard let hit = memchr(base + offset, Int32(first), lastStart - offset + 1) else { return nil }
            let position = base.distance(to: UnsafeRawPointer(hit))
            if needle.withUnsafeBytes({ memcmp(base + position, $0.baseAddress!, needle.count) }) == 0 {
                return position
            }
            offset = position + 1
        }
        return nil
    }
}
