using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Net.Http.Json;
using System.Reflection;
using System.Text.Json;

namespace RaisinTerminal.Services;

public record AppUpdateInfo(
    Version CurrentVersion,
    Version? LatestVersion,
    string? DownloadUrl,
    string? ReleaseNotes,
    string? HtmlUrl)
{
    public bool IsUpdateAvailable =>
        LatestVersion is not null &&
        LatestVersion > CurrentVersion;
}

public static class AppUpdateService
{
    private const string GitHubRepo = "gerleim/RaisinTerminal";
    private const string AssetPattern = "RaisinTerminal-v{0}-win-x64.zip";

    private static readonly HttpClient HttpClient = new()
    {
        DefaultRequestHeaders =
        {
            { "User-Agent", "RaisinTerminal" },
            { "Accept", "application/vnd.github+json" }
        },
        Timeout = TimeSpan.FromSeconds(15)
    };

    public static Version GetCurrentVersion()
    {
        var version = Assembly.GetExecutingAssembly().GetName().Version;
        return version ?? new Version(0, 0, 0);
    }

    public static async Task<AppUpdateInfo> CheckForUpdateAsync()
    {
        var current = GetCurrentVersion();
        try
        {
            var url = $"https://api.github.com/repos/{GitHubRepo}/releases/latest";
            using var response = await HttpClient.GetAsync(url);
            response.EnsureSuccessStatusCode();

            using var doc = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync());
            var root = doc.RootElement;

            var tagName = root.GetProperty("tag_name").GetString() ?? "";
            var versionStr = tagName.TrimStart('v');
            if (!Version.TryParse(versionStr, out var latestVersion))
                return new AppUpdateInfo(current, null, null, null, null);

            // Find the win-x64 ZIP asset
            string? downloadUrl = null;
            var expectedAsset = string.Format(AssetPattern, versionStr);
            if (root.TryGetProperty("assets", out var assets))
            {
                foreach (var asset in assets.EnumerateArray())
                {
                    var name = asset.GetProperty("name").GetString();
                    if (name is not null && name.Equals(expectedAsset, StringComparison.OrdinalIgnoreCase))
                    {
                        downloadUrl = asset.GetProperty("browser_download_url").GetString();
                        break;
                    }
                }
            }

            var releaseNotes = root.TryGetProperty("body", out var body) ? body.GetString() : null;
            var htmlUrl = root.TryGetProperty("html_url", out var hu) ? hu.GetString() : null;

            return new AppUpdateInfo(current, latestVersion, downloadUrl, releaseNotes, htmlUrl);
        }
        catch
        {
            return new AppUpdateInfo(current, null, null, null, null);
        }
    }

    public static async Task<string?> DownloadUpdateAsync(string downloadUrl, IProgress<double>? progress = null)
    {
        try
        {
            using var response = await HttpClient.GetAsync(downloadUrl, HttpCompletionOption.ResponseHeadersRead);
            response.EnsureSuccessStatusCode();

            var totalBytes = response.Content.Headers.ContentLength ?? -1;
            var zipPath = Path.Combine(Path.GetTempPath(), $"RaisinTerminal-update-{Guid.NewGuid():N}.zip");

            await using var contentStream = await response.Content.ReadAsStreamAsync();
            await using var fileStream = new FileStream(zipPath, FileMode.Create, FileAccess.Write, FileShare.None, 81920);

            var buffer = new byte[81920];
            long totalRead = 0;
            int bytesRead;

            while ((bytesRead = await contentStream.ReadAsync(buffer)) > 0)
            {
                await fileStream.WriteAsync(buffer.AsMemory(0, bytesRead));
                totalRead += bytesRead;

                if (totalBytes > 0)
                    progress?.Report((double)totalRead / totalBytes * 100);
            }

            return zipPath;
        }
        catch
        {
            return null;
        }
    }

    public static bool LaunchUpdateAndExit(string zipPath)
    {
        var installDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "RaisinTerminal");

        var scriptPath = Path.Combine(Path.GetTempPath(), $"RaisinTerminal-update-{Guid.NewGuid():N}.bat");

        // Batch script that:
        // 1. Waits for this process to exit
        // 2. Extracts the ZIP (overwriting existing files)
        // 3. Cleans up the ZIP and script
        // 4. Launches the new version
        var pid = Environment.ProcessId;
        var script = $"""
            @echo off
            echo Waiting for RaisinTerminal to exit...
            :waitloop
            tasklist /fi "PID eq {pid}" 2>nul | %SystemRoot%\System32\find.exe /i "{pid}" >nul
            if not errorlevel 1 (
                %SystemRoot%\System32\timeout.exe /t 1 /nobreak >nul
                goto :waitloop
            )
            echo Installing update...
            powershell -NoProfile -Command "Expand-Archive -Path '{zipPath}' -DestinationPath '{installDir}' -Force"
            if errorlevel 1 (
                echo Update failed!
                pause
                goto :cleanup
            )
            echo Starting RaisinTerminal...
            start "" "{Path.Combine(installDir, "RaisinTerminal.exe")}"
            :cleanup
            del /q "{zipPath}" 2>nul
            del /q "%~f0" 2>nul
            exit
            """;

        File.WriteAllText(scriptPath, script);

        var psi = new ProcessStartInfo
        {
            FileName = "cmd.exe",
            Arguments = $"/c \"{scriptPath}\"",
            UseShellExecute = false,
            CreateNoWindow = true
        };

        try
        {
            Process.Start(psi);
            return true;
        }
        catch
        {
            return false;
        }
    }
}
