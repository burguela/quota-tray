using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using QuotaTray.Services;

namespace QuotaTray.Engine;

public enum RefreshMode
{
    /// <summary>Only cached values; never touches the network.</summary>
    Cached,
    /// <summary>Refresh providers whose cache is missing or older than five minutes.</summary>
    IfStale,
    /// <summary>Refresh every enabled provider now.</summary>
    Force,
}

public sealed class EngineException : Exception
{
    public EngineException(string message, Exception? inner = null) : base(message, inner) { }
}

/// <summary>
/// Runs the shared Swift engine (<c>openusage-cli.exe</c>) and parses what it prints. Calls are
/// serialized: the engine persists settings and its cache on exit, so two overlapping runs could
/// overwrite each other's writes.
/// </summary>
public sealed class EngineClient
{
    // The shared Swift engine (the `openusage-cli` product), renamed when packaged.
    private const string EngineFileName = "quotatray-engine.exe";
    // A forced refresh can legitimately take up to the engine's own two-minute per-provider deadline.
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(180);

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    private readonly SemaphoreSlim _gate = new(1, 1);

    public string ExecutablePath { get; }

    private EngineClient(string executablePath)
    {
        ExecutablePath = executablePath;
    }

    /// <summary>
    /// The engine ships beside QuotaTray.exe. <c>QUOTATRAY_ENGINE</c> overrides the path for
    /// development builds that keep the two in separate folders.
    /// </summary>
    public static EngineClient Locate()
    {
        var overridePath = Environment.GetEnvironmentVariable("QUOTATRAY_ENGINE");
        if (!string.IsNullOrWhiteSpace(overridePath))
        {
            return new EngineClient(overridePath);
        }
        var baseDirectory = AppContext.BaseDirectory;
        foreach (var candidate in new[]
                 {
                     Path.Combine(baseDirectory, EngineFileName),
                     Path.Combine(baseDirectory, "engine", EngineFileName),
                 })
        {
            if (File.Exists(candidate))
            {
                return new EngineClient(candidate);
            }
        }
        // Not found: keep the expected path so the failure message names where it should be.
        return new EngineClient(Path.Combine(baseDirectory, EngineFileName));
    }

    public Task<Dashboard> DashboardAsync(RefreshMode mode) => mode switch
    {
        RefreshMode.Cached => RunDashboardAsync("dashboard", "--cached"),
        RefreshMode.Force => RunDashboardAsync("dashboard", "--force"),
        _ => RunDashboardAsync("dashboard"),
    };

    public Task<Dashboard> SetProviderEnabledAsync(string providerId, bool enabled) =>
        RunDashboardAsync(enabled ? "enable" : "disable", providerId);

    public Task<Dashboard> SetMeterStyleAsync(bool showRemaining) =>
        RunDashboardAsync("meter-style", showRemaining ? "left" : "used");

    private async Task<Dashboard> RunDashboardAsync(params string[] arguments)
    {
        var output = await RunAsync(arguments).ConfigureAwait(false);
        try
        {
            var dashboard = JsonSerializer.Deserialize<Dashboard>(output, JsonOptions);
            if (dashboard == null || dashboard.Schema != "openusage.desktop.v1")
            {
                throw new EngineException($"The engine returned an unexpected document ({dashboard?.Schema ?? "empty"}).");
            }
            return dashboard;
        }
        catch (JsonException error)
        {
            throw new EngineException("The engine's output could not be read.", error);
        }
    }

    private async Task<string> RunAsync(string[] arguments)
    {
        if (!File.Exists(ExecutablePath))
        {
            throw new EngineException($"Quota Tray's engine is missing: {ExecutablePath}");
        }

        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            var startInfo = new ProcessStartInfo(ExecutablePath)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8,
                WorkingDirectory = Path.GetDirectoryName(ExecutablePath) ?? AppContext.BaseDirectory,
            };
            foreach (var argument in arguments)
            {
                startInfo.ArgumentList.Add(argument);
            }

            using var process = new Process { StartInfo = startInfo };
            var stopwatch = Stopwatch.StartNew();
            try
            {
                process.Start();
            }
            catch (Exception error)
            {
                throw new EngineException($"Quota Tray's engine could not start: {error.Message}", error);
            }

            var stdout = process.StandardOutput.ReadToEndAsync();
            var stderr = process.StandardError.ReadToEndAsync();
            using var timeout = new CancellationTokenSource(Timeout);
            try
            {
                await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                try
                {
                    process.Kill(entireProcessTree: true);
                }
                catch (Exception error) when (error is InvalidOperationException or System.ComponentModel.Win32Exception or AggregateException)
                {
                    // Already exiting, or part of the tree couldn't be ended; still report the timeout.
                    AppLog.Warn($"could not end the timed-out engine: {error.Message}");
                }
                throw new EngineException($"Quota Tray's engine did not finish within {Timeout.TotalSeconds:0} seconds.");
            }

            var output = await stdout.ConfigureAwait(false);
            var errors = (await stderr.ConfigureAwait(false)).Trim();
            AppLog.Info($"engine {string.Join(' ', arguments)} -> exit {process.ExitCode} in {stopwatch.ElapsedMilliseconds} ms");
            if (process.ExitCode != 0)
            {
                throw new EngineException(errors.Length > 0
                    ? errors.Replace("openusage: ", "", StringComparison.Ordinal)
                    : $"Quota Tray's engine failed (exit code {process.ExitCode}).");
            }
            if (errors.Length > 0)
            {
                AppLog.Warn($"engine stderr: {errors}");
            }
            return output;
        }
        finally
        {
            _gate.Release();
        }
    }
}
