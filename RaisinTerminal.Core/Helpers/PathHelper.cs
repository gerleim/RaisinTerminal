using System.IO;

namespace RaisinTerminal.Core.Helpers;

public static class PathHelper
{
    /// <summary>
    /// Returns true if <paramref name="path"/> is equal to or a subdirectory of <paramref name="basePath"/>.
    /// Uses case-insensitive comparison and normalizes separators.
    /// </summary>
    public static bool IsSubPath(string path, string basePath)
    {
        if (string.IsNullOrEmpty(basePath)) return false;
        var normalizedPath = Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var normalizedBase = Path.GetFullPath(basePath).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (!normalizedPath.StartsWith(normalizedBase, StringComparison.OrdinalIgnoreCase))
            return false;
        return normalizedPath.Length == normalizedBase.Length
            || normalizedPath[normalizedBase.Length] == Path.DirectorySeparatorChar
            || normalizedPath[normalizedBase.Length] == Path.AltDirectorySeparatorChar;
    }
}
