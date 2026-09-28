import Foundation
import Testing
@testable import OpenUsage

/// Where the engine looks for other apps' files on Windows. The path logic is plain Foundation, so it
/// runs on every platform's CI.
struct WindowsPathTests {
    private func temporaryHome() throws -> URL {
        let home = FileManager.default.temporaryDirectory.appendingPathComponent(UUID().uuidString)
        try FileManager.default.createDirectory(at: home, withIntermediateDirectories: true)
        return home
    }

    @Test func claudeDesktopUsesTheClassicFolderWithoutAStorePackage() throws {
        let home = try temporaryHome()
        defer { try? FileManager.default.removeItem(at: home) }
        #expect(ClaudeDesktopAuthStore.windowsUserDataRelativePath(home: home) == "AppData/Roaming/Claude")
    }

    /// Regression: the Microsoft Store (MSIX) build keeps its data under its package's virtualized
    /// `LocalCache\Roaming`, so its sign-in and Cowork sessions were never found.
    @Test func claudeDesktopPrefersTheStorePackageFolder() throws {
        let home = try temporaryHome()
        defer { try? FileManager.default.removeItem(at: home) }
        let packaged = "AppData/Local/Packages/Claude_pzs8sxrjxfjjc/LocalCache/Roaming/Claude"
        try FileManager.default.createDirectory(
            at: home.appendingPathComponent(packaged), withIntermediateDirectories: true
        )
        // Another app's package and a Claude package that never ran don't count.
        try FileManager.default.createDirectory(
            at: home.appendingPathComponent("AppData/Local/Packages/Claude_aaaa/LocalCache"),
            withIntermediateDirectories: true
        )
        try FileManager.default.createDirectory(
            at: home.appendingPathComponent("AppData/Local/Packages/Other_x/LocalCache/Roaming/Claude"),
            withIntermediateDirectories: true
        )
        #expect(ClaudeDesktopAuthStore.windowsUserDataRelativePath(home: home) == packaged)
    }

    @Test func windowsAbsolutePathsAreAccepted() {
        #expect(Platform.isAbsolutePath("/Users/me"))
        #if os(Windows)
        #expect(Platform.isAbsolutePath("C:\\Users\\me"))
        #expect(Platform.isAbsolutePath("C:/Users/me"))
        #expect(Platform.isAbsolutePath("\\\\server\\share"))
        #endif
        #expect(!Platform.isAbsolutePath("relative/path"))
    }

    #if os(Windows)
    @Test func devinCLICredentialsLiveInRoamingAppData() {
        #expect(DevinAuthStore.credentialsPath == "~/AppData/Roaming/devin/credentials.toml")
    }

    @Test func backslashTildeExpandsToTheHomeFolder() {
        let home = FileManager.default.homeDirectoryForCurrentUser.path
        #expect(expandHome("~\\.codex") == home + "/.codex")
    }

    @Test func antigravityCacheStaysInTheAppFolder() {
        #expect(AntigravityAuthStore.cachePath == "~/AppData/Local/QuotaTray/antigravity/auth.json")
    }
    #endif
}
