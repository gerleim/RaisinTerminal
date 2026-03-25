namespace RaisinTerminal.Core.Terminal;

public class InputUndoManager
{
    private readonly List<string> _states = [""];
    private int _current;
    private int _mergeOriginLength;
    private DateTime _lastRecordTime = DateTime.MinValue;
    private const int MaxMergeLength = 8;
    private const int PauseThresholdMs = 1000;

    /// <summary>
    /// Records a new input state. Called after every input mutation (type, paste, backspace).
    /// </summary>
    /// <param name="currentText">Current full text of the input line.</param>
    /// <param name="groupHint">"char" for typed characters (word-grouped), "paste"/"delete" for discrete ops.</param>
    public void Record(string currentText, string groupHint = "char")
    {
        var now = DateTime.UtcNow;

        // Detect "new word started": previous state ended with a boundary char,
        // and current text ends with a non-boundary (i.e., user started typing a new word).
        // This keeps the space/punctuation with the PRECEDING word, not the next one.
        // E.g., "hello " stays as one undo unit; "hello world" starts a new unit at "w".
        bool startedNewWord = _states[_current].Length > 0
            && EndsWithWordBoundary(_states[_current])
            && currentText.Length > 0
            && !EndsWithWordBoundary(currentText);

        bool shouldMerge = groupHint == "char"
            && _current == _states.Count - 1
            && currentText.Length > 0
            && _states[_current].Length > 0
            && !startedNewWord
            && (currentText.Length - _mergeOriginLength) < MaxMergeLength
            && (now - _lastRecordTime).TotalMilliseconds < PauseThresholdMs;

        // Truncate any redo history beyond current position
        if (_current < _states.Count - 1)
            _states.RemoveRange(_current + 1, _states.Count - _current - 1);

        if (shouldMerge)
        {
            // Replace the latest state (merge consecutive chars within same word)
            _states[_current] = currentText;
        }
        else
        {
            // Don't record duplicate states
            if (_states[_current] != currentText)
            {
                _states.Add(currentText);
                _current = _states.Count - 1;
                _mergeOriginLength = currentText.Length;
            }
        }

        _lastRecordTime = now;
    }

    /// <summary>Returns the previous state, or null if nothing to undo.</summary>
    public string? Undo()
    {
        if (_current <= 0) return null;
        _current--;
        return _states[_current];
    }

    /// <summary>Returns the next state, or null if nothing to redo.</summary>
    public string? Redo()
    {
        if (_current >= _states.Count - 1) return null;
        _current++;
        return _states[_current];
    }

    /// <summary>Clears all undo/redo history. Called on Enter, Ctrl+C, Escape.</summary>
    public void Clear()
    {
        _states.Clear();
        _states.Add("");
        _current = 0;
        _mergeOriginLength = 0;
        _lastRecordTime = DateTime.MinValue;
    }

    public bool CanUndo => _current > 0;
    public bool CanRedo => _current < _states.Count - 1;

    /// <summary>Debug: returns a string representation of the undo stack state.</summary>
    public string DebugDump() =>
        $"states({_states.Count}): [{string.Join(", ", _states.Select((s, i) => i == _current ? $">>>\"{s}\"<<<" : $"\"{s}\""))}]";

    private static bool EndsWithWordBoundary(string text)
    {
        if (text.Length == 0) return false;
        char last = text[^1];
        return last is ' ' or '\n' or '/' or '\\' or '-' or '.' or '_';
    }
}
