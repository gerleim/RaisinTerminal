using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;

namespace RaisinTerminal.Services;

public record ClaudeCodeVersionInfo(string? InstalledVersion, string? LatestVersion)
{
    public bool IsUpdateAvailable =>
        InstalledVersion is not null &&
        LatestVersion is not null &&
        InstalledVersion != LatestVersion;
}

public static partial class ClaudeCodeUpdateService
{
    public static async Task<string?> GetInstalledVersionAsync()
    {
        var output = await RunCommandAsync("claude", "--version");
        if (output is null) return null;

        // Output is like "2.1.77 (Claude Code)" — extract just the version
        var match = VersionPattern().Match(output);
        return match.Success ? match.Groups[1].Value : output.Trim();
    }

    /// <summary>
    /// Runs "claude update" and parses the output to get both current and latest versions.
    /// Output patterns:
    ///   "New version available: 2.1.80 (current: 2.1.77)" — update available
    ///   "Claude Code is up to date (2.1.80)" — already latest
    /// </summary>
    public static async Task<ClaudeCodeVersionInfo> CheckVersionsAsync()
    {
        var installed = await GetInstalledVersionAsync();

        // Run claude update to check — it reports available version without installing
        // if already up to date
        var output = await RunCommandAsync("claude", "update", timeoutSeconds: 60);
        if (output is null)
            return new ClaudeCodeVersionInfo(installed, null);

        // Try "New version available: X.Y.Z (current: A.B.C)"
        var newVersionMatch = NewVersionPattern().Match(output);
        if (newVersionMatch.Success)
            return new ClaudeCodeVersionInfo(installed, newVersionMatch.Groups[1].Value);

        // Try "is up to date (X.Y.Z)"
        var upToDateMatch = UpToDatePattern().Match(output);
        if (upToDateMatch.Success)
            return new ClaudeCodeVersionInfo(installed, upToDateMatch.Groups[1].Value);

        // Try "Successfully updated from X.Y.Z to version A.B.C"
        var updatedMatch = SuccessfulUpdatePattern().Match(output);
        if (updatedMatch.Success)
        {
            // It actually performed the update — re-read installed version
            var newInstalled = await GetInstalledVersionAsync();
            return new ClaudeCodeVersionInfo(newInstalled, updatedMatch.Groups[2].Value);
        }

        // Fallback: assume latest is same as installed if we got output but couldn't parse
        return new ClaudeCodeVersionInfo(installed, installed);
    }

    public static async Task<(bool Success, string Output)> UpdateAsync()
    {
        var output = await RunCommandAsync("claude", "update", timeoutSeconds: 120);
        if (output is null)
            return (false, "Update command timed out or failed to start.");

        var newVersion = await GetInstalledVersionAsync();
        return (newVersion is not null, output);
    }

    /// <summary>
    /// On Windows, Process.Start with UseShellExecute=false cannot resolve .cmd/.bat
    /// files by bare name. npm-installed CLIs (like claude) are .cmd shims, so we
    /// must explicitly use "claude.cmd" on Windows.
    /// </summary>
    private static string ResolveCommand(string command) =>
        RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? $"{command}.cmd" : command;

    private static async Task<string?> RunCommandAsync(string fileName, string arguments, int timeoutSeconds = 30)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = ResolveCommand(fileName),
                Arguments = arguments,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            using var process = Process.Start(psi);
            if (process is null)
                return null;

            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(timeoutSeconds));
            var stdoutTask = process.StandardOutput.ReadToEndAsync(cts.Token);
            var stderrTask = process.StandardError.ReadToEndAsync(cts.Token);
            await process.WaitForExitAsync(cts.Token);

            var stdout = await stdoutTask;
            var stderr = await stderrTask;

            // Combine both streams — claude update writes to both
            var combined = stdout + "\n" + stderr;
            return !string.IsNullOrWhiteSpace(combined) ? combined.Trim() : null;
        }
        catch
        {
            return null;
        }
    }

    [GeneratedRegex(@"(\d+\.\d+\.\d+)")]
    private static partial Regex VersionPattern();

    [GeneratedRegex(@"New version available:\s*(\d+\.\d+\.\d+)")]
    private static partial Regex NewVersionPattern();

    [GeneratedRegex(@"up to date\s*\((\d+\.\d+\.\d+)\)")]
    private static partial Regex UpToDatePattern();

    [GeneratedRegex(@"updated from\s*(\d+\.\d+\.\d+)\s*to\s*(?:version\s*)?(\d+\.\d+\.\d+)")]
    private static partial Regex SuccessfulUpdatePattern();
}
