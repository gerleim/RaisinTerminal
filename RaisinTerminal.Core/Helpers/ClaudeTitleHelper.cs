namespace RaisinTerminal.Core.Helpers;

/// <summary>
/// Shared logic for parsing Claude Code session names from terminal titles.
/// </summary>
public static class ClaudeTitleHelper
{
    /// <summary>
    /// Extracts the session name from a Claude title. Handles both plain "Claude Code"
    /// and glyphed titles like "⏵ Claude Code" or "✻ SessionName".
    /// Also handles bare session names like "RT 3" (no glyph prefix).
    /// Returns null if the title cannot be parsed.
    /// </summary>
    public static string? ExtractSessionName(string title)
    {
        if (title.Equals("Claude Code", StringComparison.OrdinalIgnoreCase))
            return "Claude Code";

        var spaceIdx = title.IndexOf(' ');
        if (spaceIdx < 0 || spaceIdx >= title.Length - 1)
            return null;

        // Check if the prefix before the space is a single non-alphanumeric code point
        // (a status glyph like ✻, ⏵, ·, 🤖). If so, strip it. Otherwise the entire
        // title is the session name (e.g. "RT 3").
        bool isSingleCodePoint = spaceIdx == 1 || (spaceIdx == 2 && char.IsHighSurrogate(title[0]));
        if (isSingleCodePoint && !char.IsLetterOrDigit(title[0]))
            return title[(spaceIdx + 1)..];

        return title;
    }

    /// <summary>
    /// Strips the deduplication suffix added by RefreshTitles (e.g. " (2)") from a title.
    /// </summary>
    public static string StripDedupSuffix(string title)
    {
        var dedupIdx = title.LastIndexOf(" (");
        if (dedupIdx >= 0 && title.EndsWith(')'))
            return title[..dedupIdx];
        return title;
    }
}
