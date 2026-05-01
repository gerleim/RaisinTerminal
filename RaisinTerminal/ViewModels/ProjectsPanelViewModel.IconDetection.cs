using System.IO;

namespace RaisinTerminal.ViewModels;

public partial class ProjectsPanelViewModel
{
    private static readonly HashSet<string> SkipDirs = new(StringComparer.OrdinalIgnoreCase)
        { "bin", "obj", "node_modules", ".git", "packages", ".vs", ".idea", "TestResults" };

    private static readonly HashSet<string> ImageExtensions = new(StringComparer.OrdinalIgnoreCase)
        { ".ico", ".png", ".jpg", ".jpeg", ".bmp", ".gif" };

    internal static string? FindBestIcon(string rootPath)
    {
        var candidates = new List<string>();
        CollectIcoFiles(rootPath, candidates, depth: 0, maxDepth: 4);

        if (candidates.Count == 0)
        {
            // No .ico files — look for image files with "icon" or "logo" in the name
            var imageHits = new List<string>();
            CollectImageFiles(rootPath, imageHits, depth: 0, maxDepth: 4);
            return imageHits.Count == 1 ? imageHits[0] : null;
        }
        if (candidates.Count == 1) return candidates[0];

        // Score each candidate
        string? best = null;
        int bestScore = int.MinValue;

        foreach (var icoPath in candidates)
        {
            int score = 0;
            var dir = Path.GetDirectoryName(icoPath)!;
            var fileName = Path.GetFileName(icoPath);

            // Prefer app.ico over other names
            if (string.Equals(fileName, "app.ico", StringComparison.OrdinalIgnoreCase))
                score += 2;

            // Check path segments for hints
            var relativePath = icoPath.Substring(rootPath.Length).ToLowerInvariant();
            if (relativePath.Contains("wpf") || relativePath.Contains("desktop") || relativePath.Contains(".ui"))
                score += 3;
            if (relativePath.Contains("test"))
                score -= 5;

            // Fewer directory levels = closer to root = better
            int depth = relativePath.Count(c => c == '\\' || c == '/');
            score += Math.Max(0, 5 - depth);

            // Find nearest .csproj and check for WPF/WinExe indicators
            var csproj = FindNearestCsproj(dir, rootPath);
            if (csproj != null)
            {
                try
                {
                    var content = File.ReadAllText(csproj);
                    if (content.Contains("<UseWPF>true</UseWPF>", StringComparison.OrdinalIgnoreCase))
                        score += 10;
                    if (content.Contains("<OutputType>WinExe</OutputType>", StringComparison.OrdinalIgnoreCase))
                        score += 5;
                    if (content.Contains("<OutputType>Exe</OutputType>", StringComparison.OrdinalIgnoreCase))
                        score += 2;
                }
                catch { }
            }

            if (score > bestScore)
            {
                bestScore = score;
                best = icoPath;
            }
        }

        return best;
    }

    private static void CollectIcoFiles(string dir, List<string> results, int depth, int maxDepth)
    {
        if (depth > maxDepth) return;

        try
        {
            foreach (var file in Directory.EnumerateFiles(dir, "*.ico"))
                results.Add(file);

            foreach (var subDir in Directory.EnumerateDirectories(dir))
            {
                var dirName = Path.GetFileName(subDir);
                if (!SkipDirs.Contains(dirName))
                    CollectIcoFiles(subDir, results, depth + 1, maxDepth);
            }
        }
        catch { } // Access denied, etc.
    }

    private static void CollectImageFiles(string dir, List<string> results, int depth, int maxDepth)
    {
        if (depth > maxDepth) return;

        try
        {
            foreach (var file in Directory.EnumerateFiles(dir))
            {
                var ext = Path.GetExtension(file);
                if (ImageExtensions.Contains(ext))
                {
                    var name = Path.GetFileNameWithoutExtension(file);
                    if (name.Contains("icon", StringComparison.OrdinalIgnoreCase) ||
                        name.Contains("logo", StringComparison.OrdinalIgnoreCase))
                        results.Add(file);
                }
            }

            foreach (var subDir in Directory.EnumerateDirectories(dir))
            {
                var dirName = Path.GetFileName(subDir);
                if (!SkipDirs.Contains(dirName))
                    CollectImageFiles(subDir, results, depth + 1, maxDepth);
            }
        }
        catch { } // Access denied, etc.
    }

    private static string? FindNearestCsproj(string dir, string rootPath)
    {
        var current = dir;
        while (current != null && current.StartsWith(rootPath, StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                var csprojs = Directory.GetFiles(current, "*.csproj");
                if (csprojs.Length > 0) return csprojs[0];
            }
            catch { }
            current = Path.GetDirectoryName(current);
        }
        return null;
    }
}
