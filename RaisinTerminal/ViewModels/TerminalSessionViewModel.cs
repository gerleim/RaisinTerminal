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

public class TerminalSessionViewModel : ToolWindowViewModel
{
    private ConPtySession? _conPty;
    private TerminalEmulator? _emulator;
    private CancellationTokenSource? _readCts;
    private readonly object _lock = new();
    private readonly Channel<byte[]> _writeChannel = Channel.CreateUnbounded<byte[]>(
        new UnboundedChannelOptions { SingleReader = true });
    private int _inputSuppressionCount;
    private readonly Queue<byte[]> _inputQueue = new();
    private bool _claudeReady;
    private SessionTranscriptLogger? _transcriptLogger;

    // TUI exit cleanup: track child process in the output loop to detect Claude exit.
    // Uses a timed window (not one-shot) because ConPTY sends output in multiple
    // batches — a later batch can re-introduce artifacts after a single cleanup pass.
    private string? _outputLoopChildName;
    private DateTime _lastChildCheckTime;
    private DateTime? _claudeExitTime;

    public ICommand CloseCommand { get; }
    public TerminalEmulator? Emulator => _emulator;

    /// <summary>Raised on UI thread when the terminal buffer has changed and needs re-rendering.</summary>
    public event Action? RenderRequested;

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

    /// <summary>The Claude session display name (set via --name, used for --resume).</summary>
    public string? ClaudeSessionName { get; set; }

    /// <summary>Callback to generate a unique Claude session name (e.g. "RT 1").
    /// Set by MainViewModel; invoked when Claude starts without a pre-set name.</summary>
    public Func<string>? GenerateClaudeName { get; set; }

    /// <summary>Whether the emulator is currently in alternate screen mode.</summary>
    public bool IsInAlternateScreen => _emulator?.AlternateScreen ?? false;

    /// <summary>When set, suppresses title updates from the terminal until the title matches this value
    /// or contains a Claude status glyph, preventing flicker from cmd.exe → claude → real name.</summary>
    public string? PendingTitle { get; set; }

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
        _emulator.AnsiLogging = SettingsService.Current.AnsiLogging;

        var sessionsDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "RaisinTerminal", "sessions");
        _transcriptLogger = new SessionTranscriptLogger(sessionsDir, ContentId);
        _transcriptLogger.WriteMarker(RestoreCommand != null ? "Session restored" : "Session started");
        _emulator.TranscriptLogger = _transcriptLogger;

        _emulator.TitleChanged += title =>
        {
            Dispatcher.CurrentDispatcher.BeginInvoke(() =>
            {
                UpdateBaseTitle(title);
                // cmd.exe sets the console title to the current directory
                if (title.Length >= 3 && title[1] == ':' && char.IsLetter(title[0]) && Directory.Exists(title))
                {
                    WorkingDirectory = title;
                }

                // Track Claude session name from title and detect /clear resets.
                // _claudeReady gates all title tracking: Node.js sets process.title to the
                // command line on startup (via ConPTY → OSC 2) before Claude sets "Claude Code".
                // We ignore all titles until we first see "Claude Code".
                if (HasRunningCommand &&
                    string.Equals(RunningChildName, "claude", StringComparison.OrdinalIgnoreCase))
                {
                    var claudeTitleName = ExtractClaudeTitleName(title);
                    if (claudeTitleName != null &&
                        claudeTitleName.Equals("Claude Code", StringComparison.OrdinalIgnoreCase))
                    {
                        if (!_claudeReady)
                        {
                            // First startup — Claude just initialized
                            _claudeReady = true;
                            if (string.IsNullOrEmpty(ClaudeSessionName))
                            {
                                // Manual start — generate a name and rename
                                var generated = GenerateClaudeName?.Invoke();
                                if (!string.IsNullOrEmpty(generated))
                                {
                                    ClaudeSessionName = generated;
                                    SendRenameAfterClear(generated);
                                }
                            }
                            else
                            {
                                // Restored session with pre-set name — rename to it
                                SendRenameAfterClear(ClaudeSessionName);
                            }
                        }
                        else
                        {
                            // /clear detected — re-rename with stored name
                            if (!string.IsNullOrEmpty(ClaudeSessionName))
                            {
                                SendRenameAfterClear(ClaudeSessionName);
                            }
                        }
                    }
                    else if (claudeTitleName != null && _claudeReady)
                    {
                        // Real title update (user or app renamed) — track it
                        ClaudeSessionName = claudeTitleName;
                    }
                    // else: _claudeReady is false and title isn't "Claude Code" → startup noise, ignore
                }
                else if (_claudeReady)
                {
                    // Claude is no longer running — reset so next manual start gets a fresh rename
                    _claudeReady = false;
                }
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

    private async Task ReplayCommandAfterStartup(string command)
    {
        // Track the replayed command so it persists across save/restore cycles
        LastCommand = command;
        // Wait for the shell prompt to appear
        await Task.Delay(500);
        var data = InputEncoder.EncodeText(command + "\r");
        WriteInput(data);

        // If resuming a Claude session, --resume opens an interactive picker.
        // Wait for the picker to render, then send Enter to auto-select the first match.
        if (command.Contains("--resume", StringComparison.OrdinalIgnoreCase))
        {
            await WaitForResumePickerAndSelect();
        }
    }

    private static string? ExtractClaudeTitleName(string title) =>
        ClaudeTitleHelper.ExtractSessionName(title);

    /// <summary>
    /// Polls the screen buffer waiting for the Claude resume session picker to appear,
    /// then sends Enter to auto-select the first (most recent) match.
    /// </summary>
    private async Task WaitForResumePickerAndSelect()
    {
        // Poll every 500ms for up to 15 seconds
        for (int i = 0; i < 30; i++)
        {
            await Task.Delay(500);

            // Check screen buffer for the resume picker's navigation hints
            var rows = ScreenRows;
            for (int row = 0; row < rows; row++)
            {
                var line = ReadScreenLine(row);
                if (line.Contains("Esc to", StringComparison.Ordinal))
                {
                    // Picker is rendered — send Enter to select the first entry
                    WriteInput(InputEncoder.EncodeText("\r"));
                    return;
                }
            }
        }
    }

    /// <summary>
    /// Begins input suppression early (e.g. when /clear is typed),
    /// before the title-change callback triggers SendRenameAfterClear.
    /// Includes a safety timeout in case the rename sequence never fires.
    /// </summary>
    public void BeginInputSuppression()
    {
        _inputSuppressionCount++;

        // Safety: auto-release if SendRenameAfterClear never fires
        _ = Task.Delay(5000).ContinueWith(_ =>
        {
            if (_inputSuppressionCount > 0)
            {
                _inputSuppressionCount = 0;
                FlushInputQueue();
            }
        }, TaskScheduler.FromCurrentSynchronizationContext());
    }

    private void SendRenameAfterClear(string sessionName)
    {
        _inputSuppressionCount++;
        var scheduler = TaskScheduler.FromCurrentSynchronizationContext();

        // Wait for Claude to finish processing /clear and show its prompt
        Task.Delay(1500).ContinueWith(_ =>
        {
            // Re-verify Claude is still running and hasn't been manually renamed
            if (!string.IsNullOrEmpty(ClaudeSessionName) &&
                HasRunningCommand &&
                string.Equals(RunningChildName, "claude", StringComparison.OrdinalIgnoreCase))
            {
                WriteInput(InputEncoder.EncodeText($"/rename {sessionName}\r"));
            }

            // Allow time for the rename command to be processed before releasing queued input
            Task.Delay(500).ContinueWith(__ =>
            {
                // Reset all suppression (covers both our own and the early suppression from the view)
                _inputSuppressionCount = 0;
                FlushInputQueue();
            }, scheduler);
        }, scheduler);
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

    /// <summary>
    /// Sends user-originated input, queuing it if input is currently suppressed
    /// (e.g. during the /clear → /rename sequence).
    /// </summary>
    public void WriteUserInput(byte[] data)
    {
        if (_inputSuppressionCount > 0)
        {
            _inputQueue.Enqueue(data);
            return;
        }
        WriteInput(data);
    }

    private void FlushInputQueue()
    {
        while (_inputQueue.TryDequeue(out var data))
            WriteInput(data);
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
        }
        _conPty?.Resize(cols, rows);
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
                // or until the safety timeout expires to avoid a frozen screen.
                bool synced = _emulator?.SynchronizedOutput ?? false;
                if (synced)
                {
                    if (syncStartTime == default)
                        syncStartTime = DateTime.UtcNow;

                    if ((DateTime.UtcNow - syncStartTime).TotalMilliseconds < SyncTimeoutMs)
                        continue; // skip render, keep reading
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
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"ReadOutputLoop: {ex}"); }
    }

    /// <summary>
    /// Checks whether Claude Code has recently exited and runs artifact cleanup.
    /// Uses a timed window (not one-shot) because ConPTY sends output in multiple
    /// batches — a later batch can re-introduce artifacts after a single cleanup pass.
    /// Throttled to avoid excessive ToolHelp32 snapshots.
    /// </summary>
    private void CheckForTuiExitCleanup()
    {
        var now = DateTime.UtcNow;
        if (now - _lastChildCheckTime < TimeSpan.FromMilliseconds(300)) return;
        _lastChildCheckTime = now;

        var (childName, _) = _conPty?.GetChildProcessInfo() ?? (null, 0);
        var prevName = _outputLoopChildName;
        _outputLoopChildName = childName;

        // Detect Claude exit → start cleanup window
        if (string.Equals(prevName, "claude", StringComparison.OrdinalIgnoreCase) && childName == null)
            _claudeExitTime = now;

        // Run cleanup repeatedly during a 2-second window after Claude exits
        if (!_claudeExitTime.HasValue) return;
        if (now - _claudeExitTime.Value > TimeSpan.FromSeconds(2))
        {
            _claudeExitTime = null;
            return;
        }

        lock (_lock)
        {
            RunClaudeArtifactCleanup();
        }
    }

    private void RunClaudeArtifactCleanup()
    {
        if (_emulator?.Buffer == null) return;
        TuiArtifactCleaner.CleanClaudeArtifacts(_emulator, _emulator.Buffer);
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
        _transcriptLogger?.Dispose();
        _transcriptLogger = null;
        UnregisterInstance();
        base.OnClose();
    }
}
