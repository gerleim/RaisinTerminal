namespace RaisinTerminal.Core.Helpers;

public static class ProjectNameHelper
{
    /// <summary>
    /// Derives a short abbreviation from a project name.
    /// CamelCase → capitals + trailing digits (RaisinTerminal → RT, StockRaisin2 → SR2).
    /// ALL CAPS multi-word → first word (SNOP DEV PBIP CLAUDE → SNOP).
    /// Fallback → name as-is.
    /// </summary>
    public static string Abbreviate(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return name;

        var trimmed = name.Trim();

        // Check if it's a multi-word all-caps name (e.g. "SNOP DEV PBIP CLAUDE")
        if (trimmed.Contains(' ') && !HasLowerCase(trimmed))
        {
            var spaceIdx = trimmed.IndexOf(' ');
            return trimmed[..spaceIdx];
        }

        // Check if it's CamelCase (has both upper and lower case letters)
        if (HasUpperCase(trimmed) && HasLowerCase(trimmed))
        {
            return ExtractCamelCaseAbbreviation(trimmed);
        }

        return trimmed;
    }

    /// <summary>
    /// Returns an abbreviation with a disambiguation suffix if multiple project names
    /// produce the same abbreviation. Projects are sorted by name for stable indexing.
    /// </summary>
    public static string AbbreviateWithDisambiguation(string name, IReadOnlyList<string> allNames)
    {
        var abbr = Abbreviate(name);

        // Find all project names that produce the same abbreviation
        var collisions = new List<string>();
        foreach (var n in allNames)
        {
            if (Abbreviate(n) == abbr)
                collisions.Add(n);
        }

        if (collisions.Count <= 1)
            return abbr;

        collisions.Sort(StringComparer.OrdinalIgnoreCase);
        var index = collisions.IndexOf(name) + 1;
        return $"{abbr}{index}";
    }

    /// <summary>
    /// Generates a session name like "RT 1" from an abbreviation and session number.
    /// </summary>
    public static string GenerateSessionName(string abbreviation, int number)
    {
        return $"{abbreviation} {number}";
    }

    private static string ExtractCamelCaseAbbreviation(string name)
    {
        var chars = new List<char>();
        for (int i = 0; i < name.Length; i++)
        {
            if (char.IsUpper(name[i]))
                chars.Add(name[i]);
        }

        // Append trailing digits (e.g. StockRaisin23 → SR23)
        int digitStart = name.Length;
        while (digitStart > 0 && char.IsDigit(name[digitStart - 1]))
            digitStart--;
        for (int i = digitStart; i < name.Length; i++)
            chars.Add(name[i]);

        return chars.Count > 0 ? new string(chars.ToArray()) : name;
    }

    private static bool HasUpperCase(string s)
    {
        foreach (var c in s)
            if (char.IsUpper(c)) return true;
        return false;
    }

    private static bool HasLowerCase(string s)
    {
        foreach (var c in s)
            if (char.IsLower(c)) return true;
        return false;
    }
}
