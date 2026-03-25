using System.IO;
using System.Text;
using System.Threading.Channels;
using System.Windows.Input;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Raisin.WPF.Base;
using RaisinTerminal.Core.Terminal;

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
    public bool HasRunningCommand => _conPty?.HasChildProcesses() ?? false;

    /// <summary>The executable name of the running child process (e.g. "claude", "python"), or null.</summary>
    public string? RunningChildName => _conPty?.GetChildProcessName();

    /// <summary>The PID of the running child process, or 0 if none.</summary>
    public int RunningChildPid => _conPty?.GetChildProcessInfo().Pid ?? 0;

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
#pragma warning disable CS4014 // Fire-and-forget is intentional
                                    SendRenameAfterClear(generated);
#pragma warning restore CS4014
                                }
                            }
                            else
                            {
                                // Restored session with pre-set name — rename to it
#pragma warning disable CS4014 // Fire-and-forget is intentional
                                SendRenameAfterClear(ClaudeSessionName);
#pragma warning restore CS4014
                            }
                        }
                        else
                        {
                            // /clear detected — re-rename with stored name
                            if (!string.IsNullOrEmpty(ClaudeSessionName))
                            {
#pragma warning disable CS4014 // Fire-and-forget is intentional
                                SendRenameAfterClear(ClaudeSessionName);
#pragma warning restore CS4014
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

    /// <summary>
    /// Extracts the session name from a Claude title. Handles both plain "Claude Code"
    /// and glyphed titles like "⏵ Claude Code" or "✻ SessionName".
    /// Also handles bare session names like "RT 3" (no glyph prefix).
    /// </summary>
    private static string? ExtractClaudeTitleName(string title)
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

    private async Task SendRenameAfterClear(string sessionName)
    {
        _inputSuppressionCount++;
        try
        {
            // Wait for Claude to finish processing /clear and show its prompt
            await Task.Delay(1500);

            // Re-verify Claude is still running and hasn't been manually renamed
            if (!string.IsNullOrEmpty(ClaudeSessionName) &&
                HasRunningCommand &&
                string.Equals(RunningChildName, "claude", StringComparison.OrdinalIgnoreCase))
            {
                WriteInput(InputEncoder.EncodeText($"/rename {sessionName}\r"));
            }
        }
        finally
        {
            // Reset all suppression (covers both our own and the early suppression from the view)
            _inputSuppressionCount = 0;
            FlushInputQueue();
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

        try
        {
            Task<int>? pendingRead = null;
            while (!ct.IsCancellationRequested)
            {
                pendingRead ??= stream.ReadAsync(buf, 0, buf.Length, ct);
                int read = await pendingRead;
                pendingRead = null;
                if (read == 0) break;

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

                    lock (_lock)
                    {
                        _emulator?.Feed(buf.AsSpan(0, read));
                    }
                }

                LastOutputTime = DateTime.UtcNow;

                // Coalesce rendering: post at Render priority (below Input) so keyboard
                // events are always processed before screen repaints during heavy output.
                dispatcher.BeginInvoke(DispatcherPriority.Render, () => RenderRequested?.Invoke());
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception) { }
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

    public override void OnClose()
    {
        _writeChannel.Writer.TryComplete();
        _readCts?.Cancel();
        _conPty?.Dispose();
        _conPty = null;
        UnregisterInstance();
        base.OnClose();
    }
}
