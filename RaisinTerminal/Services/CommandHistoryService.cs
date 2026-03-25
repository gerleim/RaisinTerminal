using System.IO;

namespace RaisinTerminal.Services;

/// <summary>
/// Persists command history across terminal sessions.
/// </summary>
public class CommandHistoryService
{
    private static readonly string HistoryPath =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "RaisinTerminal", "command-history.txt");

    private const int MaxEntries = 500;
    private readonly List<string> _history = [];
    private int _index;

    public static CommandHistoryService Instance { get; } = new();

    private CommandHistoryService()
    {
        Load();
    }

    public void Add(string command)
    {
        var trimmed = command.Trim();
        if (string.IsNullOrEmpty(trimmed)) return;

        // Don't duplicate the last entry
        if (_history.Count > 0 && _history[^1] == trimmed) return;

        _history.Add(trimmed);
        if (_history.Count > MaxEntries)
            _history.RemoveAt(0);

        ResetNavigation();
        Save();
    }

    /// <summary>Navigate to the previous (older) entry. Returns null if at the beginning.</summary>
    public string? NavigateUp()
    {
        if (_history.Count == 0) return null;
        if (_index > 0) _index--;
        return _history[_index];
    }

    /// <summary>Navigate to the next (newer) entry. Returns "" when past the end (clears the line).</summary>
    public string? NavigateDown()
    {
        if (_history.Count == 0) return null;
        if (_index < _history.Count - 1)
        {
            _index++;
            return _history[_index];
        }
        _index = _history.Count;
        return "";
    }

    public void ResetNavigation() => _index = _history.Count;

    private void Load()
    {
        try
        {
            if (!File.Exists(HistoryPath)) return;
            var lines = File.ReadAllLines(HistoryPath);
            foreach (var line in lines)
            {
                if (!string.IsNullOrWhiteSpace(line))
                    _history.Add(line);
            }
            _index = _history.Count;
        }
        catch { }
    }

    private void Save()
    {
        try
        {
            var dir = Path.GetDirectoryName(HistoryPath)!;
            Directory.CreateDirectory(dir);
            // Take last MaxEntries to keep file size bounded
            var toSave = _history.Count > MaxEntries
                ? _history.Skip(_history.Count - MaxEntries)
                : _history;
            File.WriteAllLines(HistoryPath, toSave);
        }
        catch { }
    }
}
