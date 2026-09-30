import Foundation
import XCTest
@testable import OpenUsage

final class JSONLScannerCancellationTests: XCTestCase {
    func testCancelledQueuedScanReturnsNilAndNeverInvokesItsParser() async throws {
        let base = try makeDirectory("Queue")
        defer { try? FileManager.default.removeItem(at: base) }
        let file = try makeIntegerFile(in: base)
        let scanner = IncrementalJSONLScanner<Int>()
        let blockingParser = BlockingParser()
        let firstTask = Task {
            await scanner.items(
                from: [file], since: .distantPast, cacheIdentity: "shared-home", parse: blockingParser.parse
            )
        }
        guard await waitUntil({ blockingParser.hasStarted }) else {
            blockingParser.unblock()
            firstTask.cancel()
            _ = await firstTask.value
            return XCTFail("the first parser did not start before the timeout")
        }

        let queuedParser = ParseCounter()
        let queuedTask = Task {
            await scanner.items(
                from: [file], since: .distantPast, cacheIdentity: "shared-home", parse: queuedParser.parse
            )
        }
        guard await waitUntil({ await scanner.queuedScanCountForTesting(identity: "shared-home") > 0 }) else {
            queuedTask.cancel()
            blockingParser.unblock()
            _ = await firstTask.value
            _ = await queuedTask.value
            return XCTFail("the second scan did not queue before the timeout")
        }
        queuedTask.cancel()
        blockingParser.unblock()

        let firstResult = await firstTask.value
        let queuedResult = await queuedTask.value
        XCTAssertEqual(firstResult, [7])
        XCTAssertNil(queuedResult)
        XCTAssertEqual(queuedParser.count, 0)
    }

    func testCancelledClaudeScanIsNotAnAuthoritativeEmptyHistory() async throws {
        let now = Date()
        let home = try ClaudeLogFixture.makeHome(files: [
            "project/session.jsonl": ClaudeLogFixture.usageLine(
                timestamp: OpenUsageISO8601.string(from: now),
                input: 10,
                output: 5,
                costUSD: 0.25
            )
        ])
        defer { try? FileManager.default.removeItem(at: home) }
        let file = try XCTUnwrap(JSONLScanning.jsonlFiles(under: home.appendingPathComponent("projects")).first)
        let incremental = IncrementalJSONLScanner<ClaudeLogUsageScanner.Entry>()
        let blockingParser = BlockingParser(finish: { ClaudeLogUsageScanner.parseFile($0) })
        let firstTask = Task {
            await incremental.items(
                from: [file],
                since: .distantPast,
                cacheIdentity: "shared-home",
                parse: blockingParser.parse
            )
        }
        guard await waitUntil({ blockingParser.hasStarted }) else {
            blockingParser.unblock()
            firstTask.cancel()
            _ = await firstTask.value
            return XCTFail("the first parser did not start before the timeout")
        }

        let scanner = ClaudeLogUsageScanner(
            environment: FakeEnvironment(["CLAUDE_CONFIG_DIR": home.path]),
            homeDirectory: { home },
            incrementalScanner: incremental,
            cacheIdentityOverride: "shared-home"
        )
        let cancelledTask = Task {
            await scanner.scan(now: now, pricing: TestPricing.bundled)
        }
        guard await waitUntil({ await incremental.queuedScanCountForTesting(identity: "shared-home") > 0 }) else {
            cancelledTask.cancel()
            blockingParser.unblock()
            _ = await firstTask.value
            _ = await cancelledTask.value
            return XCTFail("the Claude scan did not queue before the timeout")
        }
        cancelledTask.cancel()
        blockingParser.unblock()

        _ = await firstTask.value
        let cancelledResult = await cancelledTask.value
        XCTAssertNil(cancelledResult)
    }

    func testCancellationDuringParseReturnsNilAndPersistsOnlyFinishedFiles() async throws {
        let base = try makeDirectory("ActiveParse")
        defer { try? FileManager.default.removeItem(at: base) }
        let first = try makeIntegerFile(named: "first.jsonl", value: 1, in: base)
        let second = try makeIntegerFile(named: "second.jsonl", value: 2, in: base)
        let persistence = JSONLScanCachePersistence(
            namespace: "test",
            schemaVersion: 1,
            directory: base.appendingPathComponent("cache"),
            writeDebounce: .milliseconds(1)
        )
        let scanner = IncrementalJSONLScanner<Int>(maxConcurrentParses: 1, persistence: persistence)
        let blockingParser = BlockingParser()
        let task = Task {
            await scanner.items(
                from: [first, second],
                since: .distantPast,
                cacheIdentity: "home",
                parse: blockingParser.parse
            )
        }
        guard await waitUntil({ blockingParser.hasStarted }) else {
            blockingParser.unblock()
            task.cancel()
            _ = await task.value
            return XCTFail("the parser did not start before the timeout")
        }
        task.cancel()
        blockingParser.unblock()

        let cancelledResult = await task.value
        XCTAssertNil(cancelledResult)
        await scanner.waitForPendingWritesForTesting()

        let reloadCounter = ParseCounter()
        let reloaded = IncrementalJSONLScanner<Int>(persistence: persistence)
        let reloadedItems = await reloaded.items(
            from: [first, second],
            since: .distantPast,
            cacheIdentity: "home",
            parse: reloadCounter.parse
        )
        XCTAssertEqual(reloadedItems, [1, 2])
        // The first file's parse ran to completion, so it is cached; the second never started.
        XCTAssertEqual(reloadCounter.count, 1)
    }

    /// Regression: a Claude history too big to read within one refresh deadline restarted from zero on
    /// every refresh, so it never finished. The files finished before the deadline must survive, and a
    /// one-shot process's final flush must wait for the cancelled scan to hand them over.
    func testFlushAfterCancelledScanPersistsFinishedFilesForTheNextScan() async throws {
        let base = try makeDirectory("Resume")
        defer { try? FileManager.default.removeItem(at: base) }
        let first = try makeIntegerFile(named: "first.jsonl", value: 1, in: base)
        let second = try makeIntegerFile(named: "second.jsonl", value: 2, in: base)
        let third = try makeIntegerFile(named: "third.jsonl", value: 3, in: base)
        let persistence = JSONLScanCachePersistence(
            namespace: "test",
            schemaVersion: 1,
            directory: base.appendingPathComponent("cache"),
            writeDebounce: .seconds(60)
        )
        let scanner = IncrementalJSONLScanner<Int>(maxConcurrentParses: 1, persistence: persistence)
        let gate = BlockingParser()
        let task = Task {
            await scanner.items(
                from: [first, second, third],
                since: .distantPast,
                cacheIdentity: "home",
                parse: { data in
                    let value = String(data: data, encoding: .utf8).flatMap(Int.init)
                    return value == 2 ? gate.parse(data) : value.map { [$0] }
                }
            )
        }
        guard await waitUntil({ gate.hasStarted }) else {
            gate.unblock()
            task.cancel()
            _ = await task.value
            return XCTFail("the parser did not reach the second file before the timeout")
        }
        task.cancel()
        let flush = Task { await scanner.flushPendingWrites() }
        guard await waitUntil({ await scanner.queuedScanCountForTesting(identity: "home") > 0 }) else {
            gate.unblock()
            _ = await flush.value
            _ = await task.value
            return XCTFail("the flush did not wait for the cancelled scan")
        }
        gate.unblock()
        await flush.value
        let cancelledResult = await task.value
        XCTAssertNil(cancelledResult)

        let reloadCounter = ParseCounter()
        let reloaded = IncrementalJSONLScanner<Int>(persistence: persistence)
        let reloadedItems = await reloaded.items(
            from: [first, second, third],
            since: .distantPast,
            cacheIdentity: "home",
            parse: reloadCounter.parse
        )
        XCTAssertEqual(reloadedItems, [1, 2, 3])
        // The first two finished before the scan stopped; only the third is parsed again.
        XCTAssertEqual(reloadCounter.count, 1)
    }

    private func waitUntil(
        _ condition: @escaping @Sendable () async -> Bool
    ) async -> Bool {
        let deadline = ContinuousClock.now.advanced(by: .seconds(2))
        while ContinuousClock.now < deadline {
            if await condition() { return true }
            try? await Task.sleep(for: .milliseconds(1))
        }
        return await condition()
    }

    /// On Windows `jsonlFiles` walks with `FindFirstFileExW`; it must report exactly what the portable
    /// Foundation walk does (same path strings, since those key the parse cache), and elsewhere the two
    /// are the same walk.
    func testNativeFileDiscoveryMatchesTheFoundationWalk() throws {
        let base = try makeDirectory("NativeDiscovery")
        defer { try? FileManager.default.removeItem(at: base) }
        let fileManager = FileManager.default
        let nested = base.appendingPathComponent("a/b/c", isDirectory: true)
        try fileManager.createDirectory(at: nested, withIntermediateDirectories: true)
        for relative in ["one.jsonl", "a/two.jsonl", "a/b/three.jsonl", "a/b/c/four.jsonl", "notes.txt", "a/b/c/five.json"] {
            try Data(relative.utf8).write(to: base.appendingPathComponent(relative))
        }

        let native = JSONLScanning.jsonlFiles(under: base)
        let reference = JSONLScanning.foundationJSONLFiles(under: base.resolvingSymlinksInPath())

        XCTAssertEqual(native.count, 4)
        XCTAssertEqual(native.map(\.path), reference.map(\.path))
        XCTAssertEqual(native.map(\.size), reference.map(\.size))
        for (lhs, rhs) in zip(native, reference) {
            XCTAssertTrue(lhs.mtime.isSameFileTimestamp(as: rhs.mtime), "\(lhs.path): \(lhs.mtime) vs \(rhs.mtime)")
        }
    }

    func testFileDiscoveryStopsWhenTheTaskIsCancelled() async throws {
        let base = try makeDirectory("Discovery")
        defer { try? FileManager.default.removeItem(at: base) }
        try Data("1".utf8).write(to: base.appendingPathComponent("a.jsonl"))
        XCTAssertEqual(JSONLScanning.jsonlFiles(under: base).count, 1)

        // The task is cancelled before its body runs, so the walk must bail out on its first entry.
        let task = Task { () -> Int in
            while !Task.isCancelled { await Task.yield() }
            return JSONLScanning.jsonlFiles(under: base).count
        }
        task.cancel()
        let discovered = await task.value
        XCTAssertEqual(discovered, 0)
    }

    private func makeDirectory(_ suffix: String) throws -> URL {
        let directory = FileManager.default.temporaryDirectory
            .appendingPathComponent("OpenUsageScannerCancellation\(suffix)-\(UUID().uuidString)", isDirectory: true)
        try FileManager.default.createDirectory(at: directory, withIntermediateDirectories: true)
        return directory
    }

    private func makeIntegerFile(
        named name: String = "usage.jsonl",
        value: Int = 7,
        in directory: URL
    ) throws -> JSONLScanning.DiscoveredFile {
        let url = directory.appendingPathComponent(name)
        try Data(String(value).utf8).write(to: url)
        let values = try url.resourceValues(forKeys: [.fileSizeKey, .contentModificationDateKey])
        return JSONLScanning.DiscoveredFile(
            path: url.path,
            size: try XCTUnwrap(values.fileSize),
            mtime: try XCTUnwrap(values.contentModificationDate)
        )
    }
}

