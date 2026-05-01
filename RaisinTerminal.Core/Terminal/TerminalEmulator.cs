using Raisin.EventSystem;
using RaisinTerminal.Core.Models;

namespace RaisinTerminal.Core.Terminal;

/// <summary>
/// Wires AnsiParser → TerminalBuffer: interprets ANSI/VT escape sequences
/// and translates them into buffer operations (cursor moves, erases, color changes).
/// </summary>
public partial class TerminalEmulator : IDisposable
{
    private const int IdleSnapshotMs = 5000;

    private readonly AnsiParser _parser = new();
    private readonly EventSystem? _events;
    private readonly System.Threading.Timer _idleSnapshotTimer;
    private long _lastFlushedModCount;
    private bool _disposed;

    public TerminalBuffer Buffer { get; private set; }

    // Suppresses scrollback during TUI redraws (sync output + ED 2)
    private bool _syncRedrawSuppressScrollback;

    // DEC private mode state
    public bool CursorEnabled { get; private set; } = true;
    public bool ApplicationCursorKeys { get; private set; }
    public bool AutoWrap { get; private set; } = true;
    public bool AlternateScreen { get; private set; }
    public bool BracketedPasteMode { get; private set; }

    /// <summary>
    /// DEC mode 2026: synchronized output. When true, the application is in the
    /// middle of a screen update and the host should defer rendering until reset.
    /// </summary>
    public bool SynchronizedOutput { get; private set; }

    /// <summary>
    /// Opt-in flag: while true, ED 2 (full-screen clear) or cursor home (CUP 1;1)
    /// outside the alternate screen suppresses scrollback push, just like ED 2
    /// inside a DEC 2026 sync block does. Restored on idle by the snapshot timer,
    /// or immediately when this flag is cleared.
    ///
    /// Activated by the session view-model when Claude's main TUI title
    /// (Claude Code or "✳ &lt;name&gt;") arrives — that signal marks "resume
    /// picker is past, steady-state in-place redraws have begun." Activating
    /// earlier (on process detection) would eat the conversation-history render
    /// emitted by --resume. Deactivated when the claude child exits.
    /// Claude does in-place TUI redraws (cursor home + per-line rewrite, sometimes
    /// preceded by ED 2) without always wrapping them in DEC 2026 sync. With a
    /// frame taller than the viewport, each redraw leaks the same top rows into
    /// scrollback. This flag lets us treat redraw signals outside alt-screen as
    /// suppression triggers while Claude owns the terminal, and keep normal
    /// semantics for other workloads.
    /// </summary>
    public bool ClaudeRedrawSuppression
    {
        get => _claudeRedrawSuppression;
        set
        {
            bool wasOff = !_claudeRedrawSuppression;
            _claudeRedrawSuppression = value;
            // The first CUP 1;1 after Claude takes over is always the initial
            // render (resume picker dismiss → conversation history dump), never
            // a spinner redraw. Skip suppressing on it so the history flows into
            // scrollback. Subsequent CUP 1;1 events are steady-state redraws and
            // do trigger suppression.
            if (value && wasOff)
            {
                _skipNextCursorHomeSuppress = true;
                Buffer.DeferScrollbackOnSuppress = true;
            }
            // When the flag flips off (Claude exited), commit any deferred overflow
            // and drop active suppression so shell output flows normally.
            if (!value)
            {
                Buffer.FlushDeferredScrollback();
                Buffer.DeferScrollbackOnSuppress = false;
                if (_syncRedrawSuppressScrollback &&
                    !SynchronizedOutput && !AlternateScreen)
                {
                    _syncRedrawSuppressScrollback = false;
                    Buffer.SuppressScrollback = false;
                }
            }
        }
    }
    private bool _claudeRedrawSuppression;
    private bool _skipNextCursorHomeSuppress;
    private bool _resizeGrace;

    /// <summary>
    /// When set, printed characters and newlines are written to the transcript log.
    /// </summary>
    public SessionTranscriptLogger? TranscriptLogger { get; set; }

    /// <summary>Raised after a chunk of data has been processed and the buffer is dirty.</summary>
    public event Action? BufferChanged;

    /// <summary>Raised when an OSC sequence sets the window title (OSC 0 or OSC 2).</summary>
    public event Action<string>? TitleChanged;

    /// <summary>Raised when an OSC 7 or OSC 9;9 sequence reports the current working directory.</summary>
    public event Action<string>? WorkingDirectoryChanged;

    /// <summary>
    /// Raised when the emulator enters alternate screen mode (DECSET 1047/1049).
    /// The view uses this to snapshot viewport scroll state before TUI apps take over.
    /// </summary>
    public event Action? AlternateScreenEntered;

    /// <summary>
    /// Raised when the emulator leaves alternate screen mode. The view uses this
    /// to restore the viewport scroll state saved at enter.
    /// </summary>
    public event Action? AlternateScreenExited;

    public TerminalEmulator(int cols, int rows, EventSystem? events = null)
    {
        _events = events;
        Buffer = new TerminalBuffer(cols, rows);
        Buffer.ScrollbackLineAdded += OnScrollbackLineAdded;
        _parser.Print += OnPrint;
        _parser.Execute += OnExecute;
        _parser.EscDispatch += OnEscDispatch;
        _parser.CsiDispatch += OnCsiDispatch;
        _parser.OscDispatch += OnOscDispatch;
        _idleSnapshotTimer = new System.Threading.Timer(OnIdleTick, null,
            System.Threading.Timeout.Infinite, System.Threading.Timeout.Infinite);
    }

    private void OnIdleTick(object? state)
    {
        if (_disposed) return;
        FlushVisibleScreen("idle screen snapshot");

        // Flush deferred scrollback so new rows appear in history, but keep
        // suppression active — releasing it here creates a gap where the next
        // output scrolls the banner into real scrollback as a duplicate before
        // the next CUP 1;1 / ED 2 re-enables suppression.
        if (ClaudeRedrawSuppression && _syncRedrawSuppressScrollback &&
            !SynchronizedOutput && !AlternateScreen)
        {
            Buffer.FlushDeferredScrollback();
        }
    }

    /// <summary>
    /// Writes the current visible screen to the transcript log, demarcated with a
    /// marker. Skips when nothing has changed since the previous flush so an idle
    /// session doesn't accumulate identical snapshots. Safe to call from any thread.
    /// </summary>
    public void FlushVisibleScreen(string label)
    {
        var logger = TranscriptLogger;
        if (logger == null) return;

        long mod = Buffer.ModificationCount;
        if (mod == _lastFlushedModCount) return;
        _lastFlushedModCount = mod;

        logger.WriteTextMarker(label);
        int cols = Buffer.Columns;
        int rows = Buffer.Rows;
        var chars = new char[cols];
        for (int r = 0; r < rows; r++)
        {
            int end = -1;
            for (int c = 0; c < cols; c++)
            {
                char ch = Buffer.GetCell(r, c).Character;
                if (ch == '\0') ch = ' ';
                chars[c] = ch;
                if (ch != ' ') end = c;
            }
            logger.WriteTextLine(end < 0 ? string.Empty : new string(chars, 0, end + 1));
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _idleSnapshotTimer.Dispose();
        FlushVisibleScreen("final screen");
    }

    private void OnScrollbackLineAdded(CellData[] row)
    {
        var logger = TranscriptLogger;
        if (logger == null) return;

        // Find last non-blank cell so we right-trim trailing padding/empty cells.
        int end = row.Length - 1;
        while (end >= 0)
        {
            char ch = row[end].Character;
            if (ch != ' ' && ch != '\0') break;
            end--;
        }
        if (end < 0)
        {
            logger.WriteTextLine(string.Empty);
            return;
        }

        var chars = new char[end + 1];
        for (int i = 0; i <= end; i++)
        {
            char ch = row[i].Character;
            chars[i] = ch == '\0' ? ' ' : ch;
        }
        logger.WriteTextLine(new string(chars));
    }

    public void Feed(ReadOnlySpan<byte> data)
    {
        _parser.Feed(data);
        _resizeGrace = false;
        BufferChanged?.Invoke();
        if (!_disposed)
            _idleSnapshotTimer.Change(IdleSnapshotMs, System.Threading.Timeout.Infinite);
    }

    public void Resize(int cols, int rows)
    {
        // Lift suppression BEFORE buffer resize so that shifted-out rows are
        // preserved in scrollback. Resize is a structural change, not a TUI
        // redraw — rows must never be silently discarded.
        bool wasSyncSuppressed = _syncRedrawSuppressScrollback;
        if (wasSyncSuppressed)
        {
            _syncRedrawSuppressScrollback = false;
            Buffer.SuppressScrollback = false;
        }

        Buffer.Resize(cols, rows);

        // Start grace period so ConPTY's post-resize reflow (CUP 1;1 / ED 2)
        // doesn't re-enable suppression. Grace is cleared at end of next Feed(),
        // so only the first output batch is covered. Claude's SIGWINCH redraw
        // arrives in a later Feed() and restores suppression normally.
        if (wasSyncSuppressed)
            _resizeGrace = true;
    }

    /// <summary>
    /// Erases from the current cursor position to end-of-screen (equivalent to ED 0).
    /// Used to clean up visual artifacts left by TUI applications that exit without
    /// proper cleanup (e.g., ConPTY may not relay all erase sequences from inline-
    /// rendering TUI frameworks like ink).
    /// </summary>
    public void EraseBelow()
    {
        var fill = new CellData(' ', CellData.DefaultFgR, CellData.DefaultFgG, CellData.DefaultFgB,
                                CellData.DefaultBgR, CellData.DefaultBgG, CellData.DefaultBgB);
        EraseCells(Buffer.CursorRow, Buffer.CursorCol, Buffer.Rows - 1, Buffer.Columns - 1, fill);
    }

    /// <summary>
    /// Preemptively suppresses scrollback push ahead of a known TUI redraw. Used when
    /// the user submits /clear during a Claude session: between Enter and Claude's
    /// first cursor-home / ED 2 there's a window where Claude can emit literal LFs
    /// at the bottom of the viewport, scrolling old rows (e.g. the previous banner)
    /// into history. Setting suppression up front plugs that window. Released by
    /// the existing idle-tick path.
    /// </summary>
    public void BeginScrollbackSuppressionForRedraw()
    {
        if (!AlternateScreen && !_syncRedrawSuppressScrollback)
        {
            Buffer.ClearDeferredScrollback();
            _syncRedrawSuppressScrollback = true;
            Buffer.SuppressScrollback = true;
        }
    }

    /// <summary>
    /// Restores normal scrollback behaviour after the TUI stops sending synchronized
    /// output frames.  Called by the session view-model once the post-sync grace period
    /// expires without re-entering sync mode — i.e. the TUI has finished redrawing.
    /// </summary>
    public void RestoreScrollbackAfterSync()
    {
        if (_syncRedrawSuppressScrollback)
        {
            _syncRedrawSuppressScrollback = false;
            if (!AlternateScreen)
                Buffer.SuppressScrollback = false;
        }
    }

    /// <summary>
    /// Clears the pending auto-wrap state. Per xterm spec, cursor movement commands
    /// cancel any deferred wrap so the next printed character doesn't trigger an
    /// unwanted line feed.
    /// </summary>
    private void ClearPendingWrap()
    {
        if (Buffer.CursorCol >= Buffer.Columns)
            Buffer.CursorCol = Buffer.Columns - 1;
    }

    /// <summary>
    /// Creates a cell for erase operations — uses current SGR background but no text attributes.
    /// </summary>
    private CellData MakeEraseCell()
    {
        return new CellData(' ', _fgR, _fgG, _fgB, _bgR, _bgG, _bgB);
    }

    private void OnPrint(char c)
    {
        if (Buffer.CursorCol >= Buffer.Columns)
        {
            if (AutoWrap)
            {
                Buffer.CursorCol = 0;
                Buffer.LineFeed();
                Buffer.SetLineWrapped(Buffer.CursorRow, true);
            }
            else
            {
                // No auto-wrap: overwrite last column
                Buffer.CursorCol = Buffer.Columns - 1;
            }
        }
        Buffer.SetCell(Buffer.CursorRow, Buffer.CursorCol,
            new CellData(c, _fgR, _fgG, _fgB, _bgR, _bgG, _bgB, _bold, _italic, _underline, _reverse, _dim, _strikethrough));
        Buffer.CursorCol++;
        _lastPrintedChar = c;
    }

    private void OnExecute(byte b)
    {
        switch (b)
        {
            case 0x0A: // LF
            case 0x0B: // VT
            case 0x0C: // FF
                Buffer.LineFeed();
                break;
            case 0x0D: // CR
                Buffer.CarriageReturn();
                break;
            case 0x08: // BS
                Buffer.Backspace();
                break;
            case 0x09: // HT (tab)
                int nextTab = (Buffer.CursorCol / 8 + 1) * 8;
                Buffer.CursorCol = Math.Min(nextTab, Buffer.Columns - 1);
                break;
            case 0x07: // BEL
                break;
        }
    }

}
