using System.Text;
using System.Windows.Threading;
using RaisinTerminal.Core.Helpers;
using RaisinTerminal.Core.Terminal;

namespace RaisinTerminal.ViewModels;

public partial class TerminalSessionViewModel
{
    private int _inputSuppressionCount;
    private readonly Queue<byte[]> _inputQueue = new();
    private bool _claudeReady;
    private bool _resumePickerPending;
    private bool _renameInProgress;

    private string? _outputLoopChildName;
    private DateTime _lastChildCheckTime;
    private DateTime? _claudeExitTime;

    public string? ClaudeSessionName { get; set; }

    public Func<string>? GenerateClaudeName { get; set; }

    private void HandleClaudeTitleChanged(string title)
    {
        if (HasRunningCommand &&
            string.Equals(RunningChildName, "claude", StringComparison.OrdinalIgnoreCase))
        {
            if (ClaudeTitleHelper.IsTuiTitle(title) &&
                _emulator != null && !_emulator.ClaudeRedrawSuppression)
            {
                lock (_lock) { _emulator.ClaudeRedrawSuppression = true; }
            }

            var claudeTitleName = ExtractClaudeTitleName(title);
            if (claudeTitleName != null &&
                claudeTitleName.Equals("Claude Code", StringComparison.OrdinalIgnoreCase))
            {
                if (!_claudeReady)
                {
                    _claudeReady = true;
                    if (string.IsNullOrEmpty(ClaudeSessionName))
                    {
                        var generated = GenerateClaudeName?.Invoke();
                        if (!string.IsNullOrEmpty(generated))
                        {
                            ClaudeSessionName = generated;
                            SendRenameAfterClear(generated);
                        }
                    }
                    else if (!_resumePickerPending)
                    {
                        SendRenameAfterClear(ClaudeSessionName);
                    }
                }
                else
                {
                    if (!string.IsNullOrEmpty(ClaudeSessionName))
                    {
                        SendRenameAfterClear(ClaudeSessionName);
                    }
                }
            }
            else if (claudeTitleName != null && _claudeReady)
            {
                ClaudeSessionName = claudeTitleName;
            }
        }
        else if (_claudeReady)
        {
            _claudeReady = false;
        }
    }

    private static string? ExtractClaudeTitleName(string title) =>
        ClaudeTitleHelper.ExtractSessionName(title);

    private async Task ReplayCommandAfterStartup(string command)
    {
        LastCommand = command;
        await Task.Delay(500);
        var data = InputEncoder.EncodeText(command + "\r");
        WriteInput(data);

        if (command.Contains("--resume", StringComparison.OrdinalIgnoreCase))
        {
            _resumePickerPending = true;
            try
            {
                await WaitForResumePickerAndSelect();
            }
            finally
            {
                _resumePickerPending = false;
            }
        }
    }

    private async Task WaitForResumePickerAndSelect()
    {
        for (int i = 0; i < 30; i++)
        {
            await Task.Delay(500);

            if (ScreenContainsResumePickerHint())
            {
                await Task.Delay(1000);

                for (int attempt = 0; attempt < 3; attempt++)
                {
                    if (!ScreenContainsResumePickerHint())
                        return;
                    WriteEnterDirect();
                    await Task.Delay(500);
                }
                return;
            }
        }
    }

    private bool ScreenContainsResumePickerHint()
    {
        var rows = ScreenRows;
        for (int row = 0; row < rows; row++)
        {
            if (ReadScreenLine(row).Contains("Esc to", StringComparison.Ordinal))
                return true;
        }
        return false;
    }

    private void WriteEnterDirect()
    {
        var stream = _conPty?.InputStream;
        if (stream == null) return;
        try
        {
            stream.Write([0x0D], 0, 1);
            stream.Flush();
        }
        catch { }
    }

    public void BeginInputSuppression()
    {
        _inputSuppressionCount++;

        _ = Task.Delay(5000).ContinueWith(_ =>
        {
            if (_inputSuppressionCount > 0)
            {
                _inputSuppressionCount = 0;
                FlushInputQueue();
            }
        }, TaskScheduler.FromCurrentSynchronizationContext());
    }

    private async void SendRenameAfterClear(string sessionName)
    {
        if (_renameInProgress)
            return;
        _renameInProgress = true;
        _inputSuppressionCount++;

        try
        {
            const int pollIntervalMs = 100;
            const int maxWaitMs = 3000;
            int elapsed = 0;

            while (elapsed < maxWaitMs)
            {
                await Task.Delay(pollIntervalMs);
                elapsed += pollIntervalMs;

                var status = ClaudeScreenStateClassifier.Classify(
                    row => ReadScreenLine(row), CursorRow, ScreenRows);
                if (status == TerminalStatus.Idle)
                    break;
            }

            if (!string.IsNullOrEmpty(ClaudeSessionName) &&
                HasRunningCommand &&
                string.Equals(RunningChildName, "claude", StringComparison.OrdinalIgnoreCase))
            {
                WriteInput(InputEncoder.EncodeText($"/rename {sessionName}\r"));
            }

            await Task.Delay(500);
        }
        finally
        {
            _renameInProgress = false;
            _inputSuppressionCount = 0;
            FlushInputQueue();
        }
    }

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

    private void CheckForTuiExitCleanup()
    {
        var now = DateTime.UtcNow;
        if (now - _lastChildCheckTime < TimeSpan.FromMilliseconds(300)) return;
        _lastChildCheckTime = now;

        var (childName, _) = _conPty?.GetChildProcessInfo() ?? (null, 0);
        var prevName = _outputLoopChildName;
        _outputLoopChildName = childName;

        bool wasClaude = string.Equals(prevName, "claude", StringComparison.OrdinalIgnoreCase);

        if (wasClaude && childName == null)
        {
            lock (_lock)
            {
                if (_emulator != null) _emulator.ClaudeRedrawSuppression = false;
            }
            _claudeExitTime = now;
        }

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
}
