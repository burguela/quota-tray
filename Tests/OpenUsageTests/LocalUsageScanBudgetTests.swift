import Foundation
import XCTest
@testable import OpenUsage

final class LocalUsageScanBudgetTests: XCTestCase {
    func testReturnsTheScanResultWithinBudget() async {
        let result = await LocalUsageScanBudget.run(budget: .seconds(30), providerID: "test") { 42 }
        XCTAssertEqual(result, 42)
    }

    func testCancelsAScanThatOutlastsItsBudget() async {
        let start = ContinuousClock.now
        let result = await LocalUsageScanBudget.run(budget: .milliseconds(50), providerID: "test") { () -> Int? in
            do {
                try await Task.sleep(for: .seconds(30))
                return 1
            } catch {
                return nil
            }
        }
        XCTAssertNil(result)
        // Returning promptly proves the scan was cancelled rather than awaited to the end.
        XCTAssertLessThan(ContinuousClock.now - start, .seconds(10))
    }

    /// Regression: a scan stuck in work that never checks for cancellation (a slow directory walk) held the
    /// refresh past its budget, because the task group waited for that child; Windows refreshes ran 90-120s.
    func testReturnsAtTheBudgetEvenWhenTheScanIgnoresCancellation() async {
        let start = ContinuousClock.now
        let result = await LocalUsageScanBudget.run(budget: .milliseconds(50), providerID: "test") { () -> Int? in
            Thread.sleep(forTimeInterval: 3)
            return 1
        }
        let elapsed = ContinuousClock.now - start
        XCTAssertNil(result)
        XCTAssertLessThan(elapsed, .seconds(2))
    }

    func testReturnsPromptlyWhenTheCallerIsCancelled() async {
        let start = ContinuousClock.now
        let task = Task {
            await LocalUsageScanBudget.run(budget: .seconds(30), providerID: "test") { () -> Int? in
                Thread.sleep(forTimeInterval: 3)
                return 1
            }
        }
        try? await Task.sleep(for: .milliseconds(50))
        task.cancel()
        let result = await task.value
        XCTAssertNil(result)
        XCTAssertLessThan(ContinuousClock.now - start, .seconds(2))
    }
}

@MainActor
final class ClaudeSlowHistoryTests: XCTestCase {
    /// Regression: on Windows the first read of a large Claude history outlasted the 120s provider
    /// deadline, so the refresh timed out and the Claude card never appeared, limits included.
    func testSlowHistoryScanStillPublishesLiveLimits() async throws {
        let now = OpenUsageISO8601.date(from: "2026-02-20T16:00:00.000Z")!
        let home = try ClaudeLogFixture.makeHome(files: [
            "project-a/session.jsonl": ClaudeLogFixture.usageLine(
                timestamp: "2026-02-20T16:00:00.000Z", input: 100, output: 50, costUSD: 0.25
            )
        ])
        defer { try? FileManager.default.removeItem(at: home) }
        // Another scan of the same home holds the scanner, so the provider's own scan waits past its budget.
        let incremental = IncrementalJSONLScanner<ClaudeLogUsageScanner.Entry>()
        let busy = BlockingParser(finish: { ClaudeLogUsageScanner.parseFile($0) })
        let file = try XCTUnwrap(JSONLScanning.jsonlFiles(under: home.appendingPathComponent("projects")).first)
        let busyScan = Task {
            await incremental.items(from: [file], since: .distantPast, cacheIdentity: "home", parse: busy.parse)
        }
        defer { busy.unblock() }
        let deadline = ContinuousClock.now.advanced(by: .seconds(2))
        while !busy.hasStarted, ContinuousClock.now < deadline {
            try await Task.sleep(for: .milliseconds(1))
        }
        XCTAssertTrue(busy.hasStarted)

        let httpClient = FakeHTTPClient(response: HTTPResponse(
            statusCode: 200,
            headers: [:],
            body: Data(#"{"five_hour":{"utilization":25,"resets_at":"2099-01-01T00:00:00.000Z"}}"#.utf8)
        ))
        let provider = ClaudeProvider(
            authStore: ClaudeAuthStore(
                environment: FakeEnvironment(["CLAUDE_CONFIG_DIR": "/tmp/claude"]),
                files: FakeFiles([
                    "/tmp/claude/.credentials.json": #"{"claudeAiOauth":{"accessToken":"token","subscriptionType":"pro","scopes":["user:profile"]}}"#
                ]),
                keychain: FakeKeychain(),
                now: { now }
            ),
            usageClient: ClaudeUsageClient(httpClient: httpClient),
            logUsageScanner: ClaudeLogUsageScanner(
                environment: FakeEnvironment(["CLAUDE_CONFIG_DIR": home.path]),
                homeDirectory: { home },
                incrementalScanner: incremental,
                cacheIdentityOverride: "home"
            ),
            allowsUnattributedPiUsage: false,
            localUsageScanBudget: .milliseconds(50),
            now: { now },
            pricing: { TestPricing.bundled }
        )

        let snapshot = await provider.refresh()

        XCTAssertEqual(snapshot.plan, "Pro")
        XCTAssertNil(snapshot.errorCategory)
        XCTAssertNotNil(snapshot.lines.first(where: { $0.label == "Session" }))
        XCTAssertNil(snapshot.usageHistory)
        busy.unblock()
        _ = await busyScan.value
    }
}
