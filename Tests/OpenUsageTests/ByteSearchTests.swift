import Foundation
import XCTest
@testable import OpenUsage

final class ByteSearchTests: XCTestCase {
    func testFindsBytesAndNeedlesLikeTheGenericSearch() {
        let text = Array(#"{"a":1}"usage":{"x"}\#n"usage""#.utf8)
        let marker = Array(#""usage":{"#.utf8)
        text.withUnsafeBytes { bytes in
            XCTAssertEqual(ByteSearch.firstIndex(of: UInt8(ascii: "\n"), in: bytes, from: 0), text.firstIndex(of: UInt8(ascii: "\n")))
            XCTAssertNil(ByteSearch.firstIndex(of: UInt8(ascii: "\n"), in: bytes, from: text.count))
            XCTAssertTrue(ByteSearch.contains(marker, in: bytes))
            XCTAssertEqual(ByteSearch.firstIndex(of: marker, in: bytes), 7)
            XCTAssertNil(ByteSearch.firstIndex(of: Array("nope".utf8), in: bytes))
            let tail = UnsafeRawBufferPointer(rebasing: bytes[20...])
            XCTAssertFalse(ByteSearch.contains(marker, in: tail))
            XCTAssertFalse(ByteSearch.contains(marker, in: UnsafeRawBufferPointer(rebasing: bytes[0..<3])))
        }
    }
}
