namespace RaisinTerminal.Core.Terminal;

/// <summary>
/// A single search match location in the terminal buffer.
/// Uses absolute row coordinates (same system as selection) so matches survive scrolling.
/// </summary>
public readonly record struct SearchMatch(long AbsoluteRow, int StartCol, int Length);

/// <summary>
/// Holds the full state of an active search: query, all matches, current index.
/// </summary>
public class TerminalSearchState
{
    public string Query { get; set; } = "";
    public List<SearchMatch> Matches { get; } = new();
    public int CurrentMatchIndex { get; set; } = -1;

    public int MatchCount => Matches.Count;

    public SearchMatch? CurrentMatch =>
        CurrentMatchIndex >= 0 && CurrentMatchIndex < Matches.Count
            ? Matches[CurrentMatchIndex]
            : null;

    public void Clear()
    {
        Matches.Clear();
        CurrentMatchIndex = -1;
        Query = "";
    }
}
