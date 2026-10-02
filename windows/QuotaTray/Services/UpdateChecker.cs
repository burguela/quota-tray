using System;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace QuotaTray.Services;

/// <summary>Where the update offer stands, as the panel shows it.</summary>
public enum UpdatePhase { Available, Installing, Failed }

/// <summary>A newer Quota Tray the panel offers to install.</summary>
public sealed record UpdateOffer(string Version, UpdatePhase Phase, string? Error = null);

/// <summary>A published Quota Tray release newer than the running one.</summary>
public sealed record UpdateRelease(Version Version, Uri DownloadUrl, string? Sha256);

/// <summary>
/// Looks for a newer Quota Tray on this fork's GitHub releases, downloads its installer, and runs it.
/// Releases are tagged <c>quotatray-vX.Y.Z</c> and carry <c>QuotaTray-Setup-x64.exe</c> (see
/// docs/windows.md). The check is one unauthenticated request to api.github.com; nothing about the
/// user is sent beyond what any request carries.
/// </summary>
public static class UpdateChecker
{
    private const string Repository = "burguela/quota-tray";
    private const string AssetName = "QuotaTray-Setup-x64.exe";
    private static readonly Regex ReleaseTag = new(@"^quotatray-v(\d+\.\d+\.\d+)$", RegexOptions.CultureInvariant);

    private static readonly HttpClient Http = CreateClient();

    /// <summary>The running version (the csproj's Version), or null for a build without one.</summary>
    public static Version? Current { get; } = ParseCurrent();

    private static HttpClient CreateClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd($"QuotaTray/{Current?.ToString() ?? "dev"}");
        client.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        return client;
    }

    private static Version? ParseCurrent()
    {
        var informational = (System.Reflection.Assembly.GetExecutingAssembly()
            .GetCustomAttributes(typeof(System.Reflection.AssemblyInformationalVersionAttribute), false)
            .FirstOrDefault() as System.Reflection.AssemblyInformationalVersionAttribute)?.InformationalVersion;
        var text = informational?.Split('+', '-')[0];
        return Version.TryParse(text, out var version) ? version : null;
    }

    /// <summary>The newest published release that is newer than the running version, if any.</summary>
    public static async Task<UpdateRelease?> CheckAsync(CancellationToken cancellation = default)
    {
        if (Current == null)
        {
            return null;
        }
        using var response = await Http.GetAsync($"https://api.github.com/repos/{Repository}/releases?per_page=30", cancellation);
        response.EnsureSuccessStatusCode();
        return Newest(await response.Content.ReadAsStringAsync(cancellation), Current);
    }

    /// <summary>
    /// Picks the highest <c>quotatray-vX.Y.Z</c> release above <paramref name="current"/> that has the
    /// installer attached, from the GitHub releases list. Drafts, pre-releases, other tags (the
    /// original project's), and a download link outside this repository's releases are ignored.
    /// </summary>
    internal static UpdateRelease? Newest(string releasesJson, Version current)
    {
        UpdateRelease? best = null;
        using var document = JsonDocument.Parse(releasesJson);
        foreach (var release in document.RootElement.EnumerateArray())
        {
            if (release.GetProperty("draft").GetBoolean() || release.GetProperty("prerelease").GetBoolean())
            {
                continue;
            }
            var match = ReleaseTag.Match(release.GetProperty("tag_name").GetString() ?? "");
            if (!match.Success || !Version.TryParse(match.Groups[1].Value, out var version) ||
                version <= current || (best != null && version <= best.Version))
            {
                continue;
            }
            foreach (var asset in release.GetProperty("assets").EnumerateArray())
            {
                if (asset.GetProperty("name").GetString() != AssetName ||
                    !Uri.TryCreate(asset.GetProperty("browser_download_url").GetString(), UriKind.Absolute, out var url) ||
                    !IsReleaseAsset(url))
                {
                    continue;
                }
                var digest = asset.TryGetProperty("digest", out var value) ? value.GetString() : null;
                var sha256 = digest != null && digest.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase) ? digest[7..] : null;
                best = new UpdateRelease(version, url, sha256);
            }
        }
        return best;
    }

    private static bool IsReleaseAsset(Uri url) =>
        url.Scheme == Uri.UriSchemeHttps && url.Host == "github.com" &&
        url.AbsolutePath.StartsWith($"/{Repository}/releases/download/", StringComparison.OrdinalIgnoreCase);

    /// <summary>Downloads the installer to the temp folder, checking GitHub's SHA-256 when it gives one.</summary>
    public static async Task<string> DownloadAsync(UpdateRelease release, CancellationToken cancellation = default)
    {
        var folder = Path.Combine(Path.GetTempPath(), "QuotaTray-Update");
        Directory.CreateDirectory(folder);
        var path = Path.Combine(folder, AssetName);
        using (var response = await Http.GetAsync(release.DownloadUrl, HttpCompletionOption.ResponseHeadersRead, cancellation))
        {
            response.EnsureSuccessStatusCode();
            await using var source = await response.Content.ReadAsStreamAsync(cancellation);
            await using var target = File.Create(path);
            await source.CopyToAsync(target, cancellation);
        }
        if (release.Sha256 == null)
        {
            AppLog.Warn($"update {release.Version}: GitHub gave no checksum for the installer, so it was not verified");
            return path;
        }
        await using var file = File.OpenRead(path);
        var actual = Convert.ToHexString(await SHA256.HashDataAsync(file, cancellation));
        if (!actual.Equals(release.Sha256, StringComparison.OrdinalIgnoreCase))
        {
            file.Close();
            File.Delete(path);
            throw new InvalidDataException($"the downloaded installer's SHA-256 ({actual}) doesn't match the release's ({release.Sha256})");
        }
        return path;
    }

    /// <summary>
    /// Runs the installer silently, keeping the user's Launch at Login and shortcut choices, and asks it
    /// to start the app again afterwards. It goes through <c>cmd /c start</c> so it isn't a child of this
    /// app: the installer ends the running app and everything under it before replacing the files.
    /// </summary>
    public static void StartInstaller(string setupPath)
    {
        var start = new System.Diagnostics.ProcessStartInfo("cmd.exe",
            $"/c start \"\" \"{setupPath}\" /VERYSILENT /SUPPRESSMSGBOXES /NORESTART /MERGETASKS=\"!startup,!desktopicon\" /RELAUNCH=1")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        System.Diagnostics.Process.Start(start)?.Dispose();
    }
}
