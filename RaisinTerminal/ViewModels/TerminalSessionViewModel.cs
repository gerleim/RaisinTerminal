using System.IO;
using System.Text;
using System.Threading.Channels;
using System.Windows.Input;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Raisin.WPF.Base;
using RaisinTerminal.Core.Helpers;
using RaisinTerminal.Core.Models;
using RaisinTerminal.Core.Terminal;
using RaisinTerminal.Services;

namespace RaisinTerminal.ViewModels;

public partial class TerminalSessionViewModel : ToolWindowViewModel
{
    private ConPtySession? _conPty;
    private TerminalEmulator? _emulator;
    private CancellationTokenSource? _readCts;
    private readonly object _lock = new();
    private readonly Channel<byte[]> _writeChannel = Channel.CreateUnbounded<byte[]>(
        new UnboundedChannelOptions { SingleReader = true });
    private SessionTranscriptLogger? _transcriptLogger;

    public ICommand CloseCommand { get; }
    public TerminalEmulator? Emulator => _emulator;

    /// <summary>Raised on UI thread when the terminal buffer has changed and needs re-rendering.</summary>
    public event Action? RenderRequested;

    /// <summary>Force a repaint (e.g. after monitor sleep/wake).</summary>
    public void RequestRepaint() => RenderRequested?.Invoke();

    /// <summary>
    /// Raised when an external command (e.g. the View menu) asks to toggle
    /// split view for this session. The view listens and calls ToggleSplit().
    /// </summary>
    public event Action? SplitToggleRequested;

    /// <summary>Requests the view to toggle its split-pane state.</summary>
    public void RequestSplitToggle() => SplitToggleRequested?.Invoke();

    /// <summary>
    /// Drops all saved scrollback lines from this session's buffer. The visible
    /// screen is left untouched. Triggers a repaint so viewports update.
    /// </summary>
    public void ClearScrollback()
    {
        lock (_lock)
        {
            _emulator?.Buffer.ClearScrollback();
        }
        RenderRequested?.Invoke();
    }

    private string _workingDirectory = "";
    public string WorkingDirectory
    {
        get => _workingDirectory;
        set => SetProperty(ref _workingDirectory, value);
    }

    private bool _isConnected;
    public bool IsConnected
    {
        get => _isConnected;
        set => SetProperty(ref _isConnected, value);
    }

    /// <summary>The last command the user typed (tracked for session restore).</summary>
    public string LastCommand { get; set; } = "";

    /// <summary>Whether the emulator is currently in alternate screen mode.</summary>
    public bool IsInAlternateScreen => _emulator?.AlternateScreen ?? false;

    /// <summary>When set, suppresses title updates from the terminal until the title matches this value
    /// or contains a Claude status glyph, preventing flicker from cmd.exe → claude → real name.</summary>
    public string? PendingTitle { get; set; }

    /// <summary>Whether transcript log files exist for this session on disk.</summary>
    public bool HasTranscriptFiles
    {
        get
        {
            var sessionsDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "RaisinTerminal", "sessions");
            return File.Exists(Path.Combine(sessionsDir, ContentId + ".txt"));
        }
    }

    /// <summary>Whether the shell has a child process running (e.g. nslookup, claude, python).</summary>
    public bool HasRunningCommand { get { RefreshProcessCache(); return _cachedHasRunning; } }

    /// <summary>The executable name of the running child process (e.g. "claude", "python"), or null.</summary>
    public string? RunningChildName { get { RefreshProcessCache(); return _cachedChildName; } }

    /// <summary>The PID of the running child process, or 0 if none.</summary>
    public int RunningChildPid { get { RefreshProcessCache(); return _cachedChildPid; } }

    private bool _cachedHasRunning;
    private string? _cachedChildName;
    private int _cachedChildPid;
    private DateTime _processCacheTime;
    private static readonly TimeSpan ProcessCacheTTL = TimeSpan.FromMilliseconds(500);

    private void RefreshProcessCache()
    {
        if (DateTime.UtcNow - _processCacheTime < ProcessCacheTTL) return;
        var (name, pid) = _conPty?.GetChildProcessInfo() ?? (null, 0);
        _cachedHasRunning = name != null;
        _cachedChildName = name;
        _cachedChildPid = pid;
        _processCacheTime = DateTime.UtcNow;
    }

    /// <summary>Command to replay after session starts (set during restore).</summary>
    public string? RestoreCommand { get; set; }

    /// <summary>Timestamp of the last output received from the terminal process.</summary>
    public DateTime LastOutputTime { get; private set; }

    /// <summary>
    /// Reads a line of text from the live screen buffer at the given row.
    /// Returns the trimmed text content. Thread-safe (acquires _lock).
    /// </summary>
    public string ReadScreenLine(int row)
    {
        lock (_lock)
        {
            var buf = _emulator?.Buffer;
            if (buf == null || row < 0 || row >= buf.Rows) return "";

            var sb = new StringBuilder(buf.Columns);
            for (int col = 0; col < buf.Columns; col++)
                sb.Append(buf.GetCell(row, col).Character);
            return sb.ToString().TrimEnd();
        }
    }

    /// <summary>
    /// Reads a line of text, replacing faded/predictive characters with spaces.
    /// Cells with the Dim attribute or a foreground brightness below 50% of the
    /// default are treated as predictive text (e.g. Claude Code's auto-suggestions).
    /// </summary>
    public string ReadScreenLineBrightOnly(int row)
    {
        lock (_lock)
        {
            var buf = _emulator?.Buffer;
            if (buf == null || row < 0 || row >= buf.Rows) return "";

            var sb = new StringBuilder(buf.Columns);
            for (int col = 0; col < buf.Columns; col++)
            {
                var cell = buf.GetCell(row, col);
                if (IsFadedCell(cell))
                    sb.Append(' ');
                else
                    sb.Append(cell.Character);
            }
            return sb.ToString().TrimEnd();
        }
    }

    private static bool IsFadedCell(CellData cell)
    {
        if (cell.Character == ' ') return false;
        if (cell.Dim) return true;
        int brightness = (cell.ForegroundR + cell.ForegroundG + cell.ForegroundB) / 3;
        return brightness < 102;
    }

    /// <summary>
    /// Searches the entire buffer (scrollback + screen) for the given text.
    /// Returns results sorted by position (top to bottom, left to right).
    /// Thread-safe (acquires _lock).
    /// </summary>
    public List<SearchMatch> SearchBuffer(string query)
    {
        var results = new List<SearchMatch>();
        if (string.IsNullOrEmpty(query)) return results;

        lock (_lock)
        {
            var buf = _emulator?.Buffer;
            if (buf == null) return results;

            // Search scrollback lines
            for (int i = 0; i < buf.ScrollbackCount; i++)
            {
                string lineText = buf.GetScrollbackLineText(i);
                long absRow = buf.TotalLinesScrolled - buf.ScrollbackCount + i;
                FindMatchesInLine(lineText, query, absRow, results);
            }

            // Search screen lines
            for (int row = 0; row < buf.Rows; row++)
            {
                string lineText = buf.GetScreenLineText(row);
                long absRow = buf.TotalLinesScrolled + row;
                FindMatchesInLine(lineText, query, absRow, results);
            }
        }
        return results;
    }

    private static void FindMatchesInLine(string line, string query, long absRow, List<SearchMatch> results)
    {
        int idx = 0;
        while ((idx = line.IndexOf(query, idx, StringComparison.OrdinalIgnoreCase)) >= 0)
        {
            results.Add(new SearchMatch(absRow, idx, query.Length));
            idx += 1; // allow overlapping matches
        }
    }

    /// <summary>
    /// Returns the cursor row on the live screen, or -1 if not available.
    /// </summary>
    public int CursorRow
    {
        get
        {
            lock (_lock)
            {
                return _emulator?.Buffer.CursorRow ?? -1;
            }
        }
    }

    /// <summary>
    /// Returns the number of rows in the live screen buffer, or 0 if not available.
    /// </summary>
    public int ScreenRows
    {
        get
        {
            lock (_lock)
            {
                return _emulator?.Buffer.Rows ?? 0;
            }
        }
    }

    /// <summary>Tracks the current input line being typed by the user.</summary>
    internal readonly StringBuilder CurrentInputLine = new();

    /// <summary>Undo/redo manager for the current input line.</summary>
    internal readonly InputUndoManager InputUndo = new();

    /// <summary>
    /// Callback set by ProjectsPanelViewModel to handle clipboard image paste.
    /// Takes a BitmapSource and returns the saved file path, or null if no project matched.
    /// </summary>
    public Func<BitmapSource, string?>? PasteImage { get; set; }

    public TerminalSessionViewModel()
    {
        ContentId = Guid.NewGuid().ToString();
        UpdateBaseTitle("Claude");
        CloseCommand = new RelayCommand(() => CloseAction?.Invoke());
    }

    /// <summary>
    /// Starts the ConPTY session and begins reading output.
    /// Must be called from UI thread after the view has measured its size.
    /// </summary>
    public void StartSession(int cols, int rows)
    {
        if (_conPty != null) return;

        cols = Math.Max(cols, 20);
        rows = Math.Max(rows, 5);

        _emulator = new TerminalEmulator(cols, rows, App.Events);

        if (SettingsService.Current.SessionFileLogging)
        {
            var sessionsDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "RaisinTerminal", "sessions");
            _transcriptLogger = new SessionTranscriptLogger(sessionsDir, ContentId);
            _transcriptLogger.WriteMarker(RestoreCommand != null ? "Session restored" : "Session started");
            _emulator.TranscriptLogger = _transcriptLogger;
        }

        _emulator.TitleChanged += title =>
        {
            Dispatcher.CurrentDispatcher.BeginInvoke(() =>
            {
                UpdateBaseTitle(title);
                if (title.Length >= 3 && title[1] == ':' && char.IsLetter(title[0]) && Directory.Exists(title))
                {
                    WorkingDirectory = title;
                }
                HandleClaudeTitleChanged(title);
            });
        };
        _emulator.WorkingDirectoryChanged += path =>
        {
            Dispatcher.CurrentDispatcher.BeginInvoke(() =>
            {
                if (Directory.Exists(path))
                    WorkingDirectory = path;
            });
        };

        _conPty = new ConPtySession();

        string? startDir = string.IsNullOrEmpty(WorkingDirectory) ? null : WorkingDirectory;
        _conPty.Start("cmd.exe", cols, rows, startDir);
        _conPty.Exited += (_, _) =>
        {
            System.Windows.Application.Current?.Dispatcher.BeginInvoke(() =>
            {
                IsConnected = false;
            });
        };

        IsConnected = true;

        _readCts = new CancellationTokenSource();
        _ = ReadOutputLoop(_readCts.Token);
        _ = WriteInputLoop(_readCts.Token);

        // If there's a command to restore (e.g. Claude was running), replay it after a short delay
        if (!string.IsNullOrEmpty(RestoreCommand))
        {
            var cmd = RestoreCommand;
            RestoreCommand = null;
#pragma warning disable CS4014 // Fire-and-forget is intentional
            ReplayCommandAfterStartup(cmd);
#pragma warning restore CS4014
        }
    }

    /// <summary>
    /// Sends raw bytes to the terminal process via a background writer queue.
    /// Returns immediately without blocking the UI thread.
    /// </summary>
    public void WriteInput(byte[] data)
    {
        if (_conPty?.InputStream == null || !_conPty.IsRunning) return;
        _writeChannel.Writer.TryWrite(data);
    }

    private async Task WriteInputLoop(CancellationToken ct)
    {
        var stream = _conPty?.InputStream;
        if (stream == null) return;

        try
        {
            await foreach (var data in _writeChannel.Reader.ReadAllAsync(ct))
            {
                stream.Write(data, 0, data.Length);
                // Batch: drain any queued writes before flushing
                while (_writeChannel.Reader.TryRead(out var more))
                    stream.Write(more, 0, more.Length);
                stream.Flush();
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception) { }
    }

    /// <summary>
    /// Resizes the terminal to new dimensions.
    /// </summary>
    public void Resize(int cols, int rows)
    {
        cols = Math.Max(cols, 20);
        rows = Math.Max(rows, 5);

        lock (_lock)
        {
            _emulator?.Resize(cols, rows);
            _conPty?.Resize(cols, rows);
        }
    }

    private async Task ReadOutputLoop(CancellationToken ct)
    {
        var stream = _conPty?.OutputStream;
        if (stream == null) return;

        var buf = new byte[32768];
        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        if (dispatcher == null) return;

        // DEC 2026 synchronized output: track when sync mode was entered
        // so we can force a render if the app never sends the reset sequence.
        const int SyncTimeoutMs = 200;
        DateTime syncStartTime = default;

        try
        {
            Task<int>? pendingRead = null;
            while (!ct.IsCancellationRequested)
            {
                pendingRead ??= stream.ReadAsync(buf, 0, buf.Length, ct);
                int read = await pendingRead;
                pendingRead = null;
                if (read == 0) break;

                _transcriptLogger?.WriteRaw(buf, 0, read);
                lock (_lock)
                {
                    _emulator?.Feed(buf.AsSpan(0, read));
                }

                // Drain any immediately available data before rendering.
                // ConPTY sends full screen updates that can exceed one read buffer,
                // and the cursor position is only correct after the final CUP sequence.
                // Without draining, a render can fire mid-update showing the cursor
                // at an intermediate position (e.g. bottom-right corner).
                while (true)
                {
                    pendingRead = stream.ReadAsync(buf, 0, buf.Length, ct);
                    if (!pendingRead.IsCompletedSuccessfully)
                        break;

                    read = pendingRead.Result;
                    pendingRead = null;
                    if (read == 0) return;

                    _transcriptLogger?.WriteRaw(buf, 0, read);
                    lock (_lock)
                    {
                        _emulator?.Feed(buf.AsSpan(0, read));
                    }
                }

                LastOutputTime = DateTime.UtcNow;

                // DEC 2026 synchronized output: the app has told us it's mid-update.
                // Defer rendering until the reset sequence arrives (CSI ?2026l),
                // or until a data-arrival gap exceeds the timeout (app hung / lost
                // sync-end sequence).  The timeout is measured from the last data
                // arrival, not from when sync mode started — large TUI frames
                // (e.g. Claude Code with multiple agents) can arrive in many chunks
                // spread over >200ms total, and rendering mid-frame produces garbled
                // text because the screen is only half-drawn.
                bool synced = _emulator?.SynchronizedOutput ?? false;
                if (synced)
                {
                    if (syncStartTime != default
                        && (DateTime.UtcNow - syncStartTime).TotalMilliseconds >= SyncTimeoutMs)
                    {
                        // Gap since last data exceeded timeout — fall through to render
                        // so the screen isn't frozen indefinitely.
                    }
                    else
                    {
                        syncStartTime = DateTime.UtcNow;
                        continue; // skip render, keep reading
                    }
                }

                // Post-sync grace: Claude Code's Ink TUI emits cursor positioning
                // (including CR/LF) between sync frames. A LF at the bottom row scrolls
                // the buffer, causing a 1-line jump that the next full redraw corrects.
                // Batch this between-frame content with the next sync frame by briefly
                // waiting for follow-up data before rendering.
                if (!synced && syncStartTime != default)
                {
                    syncStartTime = default;
                    pendingRead ??= stream.ReadAsync(buf, 0, buf.Length, ct);
                    if (!pendingRead.IsCompletedSuccessfully)
                    {
                        var delay = Task.Delay(16, ct);
                        await Task.WhenAny(pendingRead, delay);
                    }
                    while (pendingRead.IsCompletedSuccessfully)
                    {
                        int n = pendingRead.Result;
                        pendingRead = null;
                        if (n == 0) goto loopDone;
                        _transcriptLogger?.WriteRaw(buf, 0, n);
                        lock (_lock) { _emulator?.Feed(buf.AsSpan(0, n)); }
                        pendingRead = stream.ReadAsync(buf, 0, buf.Length, ct);
                    }
                    // Re-check: if we re-entered sync mode, defer rendering
                    synced = _emulator?.SynchronizedOutput ?? false;
                    if (synced)
                    {
                        syncStartTime = DateTime.UtcNow;
                        continue;
                    }

                    // TUI stopped sending sync frames — restore normal scrollback.
                    // This was deferred from the emulator's sync-ON handler to avoid
                    // a brief window where stray LFs could leak old frame content
                    // into the scrollback.
                    lock (_lock) { _emulator?.RestoreScrollbackAfterSync(); }
                }
                syncStartTime = default;

                // After draining all available output, check whether a TUI child
                // (e.g. Claude Code) has just exited. ConPTY often fails to relay
                // the TUI's inline cleanup sequences, leaving autocomplete/dropdown
                // artifacts on screen. Erase from cursor to end-of-screen (ED 0)
                // so the shell prompt renders cleanly.
                CheckForTuiExitCleanup();

                // Coalesce rendering: post at Render priority (below Input) so keyboard
                // events are always processed before screen repaints during heavy output.
                _ = dispatcher.BeginInvoke(DispatcherPriority.Render, () => RenderRequested?.Invoke());
            }
            loopDone:;
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"ReadOutputLoop: {ex}"); }
    }

    /// <summary>
    /// Snapshots the child process's current working directory before save/close.
    /// </summary>
    public void UpdateWorkingDirectoryFromProcess()
    {
        var dir = _conPty?.GetWorkingDirectory();
        if (!string.IsNullOrEmpty(dir))
            WorkingDirectory = dir;
    }

    public void WriteTranscriptMarker(string label) => _transcriptLogger?.WriteMarker(label);

    public override void OnClose()
    {
        _writeChannel.Writer.TryComplete();
        _readCts?.Cancel();
        _conPty?.Dispose();
        _conPty = null;
        _emulator?.Dispose();
        _transcriptLogger?.Dispose();
        _transcriptLogger = null;
        UnregisterInstance();
        base.OnClose();
    }
}
