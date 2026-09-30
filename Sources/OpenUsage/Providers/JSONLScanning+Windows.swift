#if os(Windows)
import Foundation
import WinSDK

extension JSONLScanning {
    /// `jsonlFiles(under:)` for Windows. Foundation's enumerator plus a per-file `resourceValues` call took
    /// 30-60s for ~2,000 Claude logs here (a PowerShell walk of the same tree takes 0.1s), which alone
    /// used up the scan budget. `FindFirstFileExW` returns each entry's size and last-write time with its
    /// name, so nothing is opened or stat'ed per file. Reparse points (symlinks, junctions) are skipped,
    /// as the enumerator does not follow them either.
    static func windowsJSONLFiles(under dir: URL) -> [DiscoveredFile] {
        var files: [DiscoveredFile] = []
        var pending = [dir]
        while let directory = pending.popLast() {
            if Task.isCancelled { return [] }
            // The `\?\` prefix lifts the 260-character limit that deep project folders can exceed.
            let pattern = "\\?\\" + directory.path.replacingOccurrences(of: "/", with: "\\") + "\*"
            var entry = WIN32_FIND_DATAW()
            let handle = pattern.withCString(encodedAs: UTF16.self) {
                FindFirstFileExW($0, FindExInfoBasic, &entry, FindExSearchNameMatch, nil, DWORD(FIND_FIRST_EX_LARGE_FETCH))
            }
            guard let handle, handle != INVALID_HANDLE_VALUE else { continue }
            defer { FindClose(handle) }
            repeat {
                let name = withUnsafePointer(to: &entry.cFileName) { tuple in
                    tuple.withMemoryRebound(to: UInt16.self, capacity: Int(MAX_PATH)) {
                        String(decodingCString: $0, as: UTF16.self)
                    }
                }
                let attributes = entry.dwFileAttributes
                guard name != ".", name != "..", attributes & DWORD(FILE_ATTRIBUTE_REPARSE_POINT) == 0 else {
                    continue
                }
                if attributes & DWORD(FILE_ATTRIBUTE_DIRECTORY) != 0 {
                    pending.append(directory.appendingPathComponent(name, isDirectory: true))
                } else if name.hasSuffix(".jsonl"), name.count > ".jsonl".count {
                    files.append(DiscoveredFile(
                        path: directory.appendingPathComponent(name, isDirectory: false).path,
                        size: Int((UInt64(entry.nFileSizeHigh) << 32) | UInt64(entry.nFileSizeLow)),
                        mtime: date(
                            highDateTime: entry.ftLastWriteTime.dwHighDateTime,
                            lowDateTime: entry.ftLastWriteTime.dwLowDateTime
                        )
                    ))
                }
            } while FindNextFileW(handle, &entry)
        }
        return files.sorted { $0.path < $1.path }
    }

    /// FILETIME counts 100ns ticks since 1601-01-01; the Unix epoch is 116,444,736,000,000,000 ticks later.
    private static func date(highDateTime: DWORD, lowDateTime: DWORD) -> Date {
        let ticks = (UInt64(highDateTime) << 32) | UInt64(lowDateTime)
        return Date(timeIntervalSince1970: (Double(ticks) - 116_444_736_000_000_000) / 10_000_000)
    }
}
#endif
