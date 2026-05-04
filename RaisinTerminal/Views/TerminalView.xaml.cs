using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Raisin.EventSystem;
using RaisinTerminal.Controls;
using RaisinTerminal.Core.Terminal;
using RaisinTerminal.Services;
using RaisinTerminal.Settings;
using RaisinTerminal.ViewModels;

namespace RaisinTerminal.Views;

public partial class TerminalView : UserControl
{
    private TerminalSessionViewModel? _vm;
    private DispatcherTimer? _cursorTimer;
    private readonly InputLineEditor _inputEditor = new();
    private readonly TerminalViewport _viewport = new() { IsLive = true };
    private int _savedScrollOffsetBeforeAltScreen;
    private DateTime _lastUndoRedoTime;
    private bool _resizePendingFromDrag;

    public TerminalView()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
        Canvas.SizeChanged += OnCanvasSizeChanged;
        VerticalScrollBar.Scroll += OnScrollBarScroll;
        Drop += OnFileDrop;
        DragOver += OnDragOver;
        InitSearch();
        InitSplitView();
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        _vm = DataContext as TerminalSessionViewModel;
        if (_vm == null) return;

        Canvas.Emulator = _vm.Emulator;
        Canvas.Viewport = _viewport;
        Canvas.CompressEmptyLines = SettingsService.Current.CompressEmptyLines;
        PinnedCanvas.Emulator = _vm.Emulator;
        PinnedCanvas.Viewport = _pinnedViewport;
        PinnedCanvas.CompressEmptyLines = SettingsService.Current.CompressEmptyLines;
        _inputEditor.Attach(_vm, Canvas);
        _vm.RenderRequested += OnRenderRequested;
        _vm.SplitToggleRequested += ToggleSplit;

        RegisterViewportAndAltScreenHooks();

        // Start cursor blink timer
        _cursorTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(530) };
        _cursorTimer.Tick += (_, _) =>
        {
            Canvas.CursorVisible = !Canvas.CursorVisible;
            Canvas.Invalidate();
        };
        _cursorTimer.Start();

        // Start session if not already started
        if (!_vm.IsConnected && Canvas.Columns > 0 && Canvas.Rows > 0)
        {
            _vm.StartSession(Canvas.Columns, Canvas.Rows);
            Canvas.Emulator = _vm.Emulator;
            RegisterViewportAndAltScreenHooks();
        }

        // Defer focus to avoid NullReferenceException when HWND isn't ready yet
        Dispatcher.BeginInvoke(() => Canvas.Focus(), System.Windows.Threading.DispatcherPriority.Input);
    }

    /// <summary>
    /// Registers this view's viewport with the buffer and hooks alt-screen
    /// save/restore. Idempotent so OnLoaded and the deferred StartSession path
    /// can both call it safely.
    /// </summary>
    private void RegisterViewportAndAltScreenHooks()
    {
        var emulator = _vm?.Emulator;
        if (emulator == null) return;

        var buffer = emulator.Buffer;
        if (!buffer.Viewports.Contains(_viewport))
            buffer.Viewports.Add(_viewport);

        // Only subscribe once
        emulator.AlternateScreenEntered -= OnAlternateScreenEntered;
        emulator.AlternateScreenExited -= OnAlternateScreenExited;
        emulator.AlternateScreenEntered += OnAlternateScreenEntered;
        emulator.AlternateScreenExited += OnAlternateScreenExited;
    }

    private void OnAlternateScreenEntered()
    {
        _savedScrollOffsetBeforeAltScreen = _viewport.ScrollOffset;
        _viewport.ScrollOffset = 0;
        _viewport.UserScrolledBack = false;
    }

    private void OnAlternateScreenExited()
    {
        _viewport.ScrollOffset = _savedScrollOffsetBeforeAltScreen;
        _viewport.UserScrolledBack = _viewport.ScrollOffset > 0;
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        if (_vm != null)
        {
            _vm.RenderRequested -= OnRenderRequested;
            _vm.SplitToggleRequested -= ToggleSplit;
            var emulator = _vm.Emulator;
            if (emulator != null)
            {
                emulator.AlternateScreenEntered -= OnAlternateScreenEntered;
                emulator.AlternateScreenExited -= OnAlternateScreenExited;
                emulator.Buffer.Viewports.Remove(_viewport);
                emulator.Buffer.Viewports.Remove(_pinnedViewport);
            }
        }
        _inputEditor.Detach();
        _cursorTimer?.Stop();
        if (_resizePendingFromDrag)
            MainWindow.WindowResizeCompleted -= OnWindowResizeCompleted;
    }

    private void OnRenderRequested()
    {
        // Pick up any settings changes (e.g. after Options dialog)
        Canvas.CompressEmptyLines = SettingsService.Current.CompressEmptyLines;
        PinnedCanvas.CompressEmptyLines = SettingsService.Current.CompressEmptyLines;
        // Reset cursor to visible on new output
        Canvas.CursorVisible = true;
        _cursorTimer?.Stop();
        _cursorTimer?.Start();

        // Auto-scroll to bottom on new output unless the user explicitly scrolled back.
        // ScrollOffset may be > 0 from ScrollUpRegion auto-incrementing while the tab
        // was inactive — that's not user-initiated, so we should snap back to live.
        if (_viewport.IsLive && !_viewport.UserScrolledBack)
            ScrollToBottom();

        UpdateScrollBar();
        Canvas.Invalidate();
        if (_isSplit)
        {
            UpdatePinnedScrollBar();
            PinnedCanvas.Invalidate();
        }

        // Refresh search matches if search is active (buffer content changed)
        if (_searchActive && !string.IsNullOrEmpty(SearchInput.Text))
            ExecuteSearch();

        // Show/hide input overlay on alternate screen transitions
        var altScreen = _vm?.Emulator?.AlternateScreen ?? false;
        if (altScreen != _lastAlternateScreen)
        {
            _lastAlternateScreen = altScreen;
            if (altScreen) ShowOverlay();
            else HideOverlay();
        }
    }

    private void OnCanvasSizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (_vm == null) return;
        if (_suppressResize) return;

        int cols = Canvas.Columns;
        int rows = Canvas.Rows;
        if (cols <= 0 || rows <= 0) return;

        SessionDimensionsService.Set(_vm.ContentId, cols, rows);
        SettingsService.Current.LastCanvasColumns = cols;
        SettingsService.Current.LastCanvasRows = rows;

        if (!_vm.IsConnected)
        {
            _vm.StartSession(cols, rows);
            Canvas.Emulator = _vm.Emulator;
            RegisterViewportAndAltScreenHooks();
        }
        else if (MainWindow.IsUserResizing)
        {
            if (!_resizePendingFromDrag)
            {
                _resizePendingFromDrag = true;
                MainWindow.WindowResizeCompleted += OnWindowResizeCompleted;
            }
        }
        else
        {
            _vm.Resize(cols, rows);
        }
    }

    private void OnWindowResizeCompleted()
    {
        MainWindow.WindowResizeCompleted -= OnWindowResizeCompleted;
        _resizePendingFromDrag = false;
        if (_vm == null) return;
        int cols = Canvas.Columns;
        int rows = Canvas.Rows;
        if (cols > 0 && rows > 0)
            _vm.Resize(cols, rows);
    }

    // ─── Keyboard Input ──────────────────────────────────────────────────

    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        if (e.Handled) return;
        if (_vm == null || !_vm.IsConnected) return;

        // User-rebindable commands. Checked before built-in shortcuts so users
        // can remap them without colliding with the defaults below.
        // When an IME consumes the key, e.Key is reported as ImeProcessed and the
        // real key surfaces on e.ImeProcessedKey.
        var pressedKey = e.Key == Key.ImeProcessed ? e.ImeProcessedKey : e.Key;
        var commandId = KeyBindingsService.TryResolve(pressedKey, Keyboard.Modifiers, KeyBindingScope.Terminal);

        // When search bar is active, only handle search-specific commands
        // (toggle, scroll); let the TextBox handle paste/undo/copy natively.
        if (_searchActive)
        {
            if (commandId == KeyBindingIds.ToggleSearch)
            {
                CloseSearch();
                e.Handled = true;
            }
            return;
        }

        if (commandId != null)
        {
            if (DispatchBoundCommand(commandId))
            {
                e.Handled = true;
                return;
            }
        }

        // When overlay is active, route keys to the overlay handler
        if (_overlayActive)
        {
            HandleOverlayKeyDown(e);
            return;
        }

        bool ctrl = (Keyboard.Modifiers & ModifierKeys.Control) != 0;
        bool alt = (Keyboard.Modifiers & ModifierKeys.Alt) != 0;
        bool shift = (Keyboard.Modifiers & ModifierKeys.Shift) != 0;

        // AltGr (Ctrl+Alt) produces characters on non-US layouts (e.g. HUN: AltGr+Q → \).
        // Let these fall through to OnPreviewTextInput for proper text composition.
        if (ctrl && alt)
            return;

        // Shift+Enter or Ctrl+Enter → insert newline instead of submitting
        if (e.Key == Key.Enter && (shift || ctrl))
        {
            _vm.WriteUserInput(new byte[] { (byte)'\n' });
            _vm.CurrentInputLine.Append('\n');
            _vm.InputUndo.Clear(); // Can't undo across line submissions in cmd.exe
            e.Handled = true;
            return;
        }

        // Track Enter → save command to history
        if (e.Key == Key.Enter)
        {
            var command = _vm.CurrentInputLine.ToString();
            if (!string.IsNullOrWhiteSpace(command))
            {
                CommandHistoryService.Instance.Add(command);
                _vm.LastCommand = command;
            }
            _vm.CurrentInputLine.Clear();
            _vm.InputUndo.Clear();
            CommandHistoryService.Instance.ResetNavigation();

            // Begin input suppression after /clear is submitted during a Claude session,
            // so no keystrokes leak before the title-change callback triggers SendRenameAfterClear.
            // Send Enter explicitly here then suppress immediately — Dispatcher.BeginInvoke
            // would leave a gap where fast keystrokes leak through unsuppressed.
            if (command.Trim().Equals("/clear", StringComparison.OrdinalIgnoreCase) &&
                !string.IsNullOrEmpty(_vm.ClaudeSessionName) &&
                _vm.HasRunningCommand &&
                string.Equals(_vm.RunningChildName, "claude", StringComparison.OrdinalIgnoreCase))
            {
                _vm.WriteTranscriptMarker("Session cleared (/clear)");
                _vm.WriteUserInput(InputEncoder.EncodeKey(ConsoleKey.Enter));
                _vm.BeginInputSuppression();
                // Wipe pre-existing scrollback and pre-arm suppression so the
                // newlines Claude emits before its ED 2 don't leak the prior
                // banner into history. The idle-tick path releases suppression
                // once Claude's redraw goes quiet.
                _vm.ClearScrollback();
                _vm.Emulator?.BeginScrollbackSuppressionForRedraw();
                e.Handled = true;
                return;
            }
        }

        // Track Backspace for current input line
        if (e.Key == Key.Back)
        {
            if (_inputEditor.HasSelection && _inputEditor.DeleteSelection())
            {
                ScrollToBottom();
                e.Handled = true;
                return;
            }
            if (_vm.CurrentInputLine.Length > 0)
            {
                _vm.CurrentInputLine.Length--;
                _vm.InputUndo.Record(_vm.CurrentInputLine.ToString(), "delete");
            }
        }

        // Track Delete key for selection-delete
        if (e.Key == Key.Delete)
        {
            if (_inputEditor.HasSelection && _inputEditor.DeleteSelection())
            {
                ScrollToBottom();
                e.Handled = true;
                return;
            }
        }

        // Track Ctrl+C (no selection) and Escape → reset current input
        if (e.Key == Key.Escape || (ctrl && e.Key == Key.C && !_inputEditor.HasSelection))
        {
            _vm.CurrentInputLine.Clear();
            _vm.InputUndo.Clear();
            CommandHistoryService.Instance.ResetNavigation();
        }

        // Shift+Arrow / Ctrl+Shift+Arrow / Shift+Home / Shift+End → keyboard selection
        if (shift && !alt && (pressedKey == Key.Left || pressedKey == Key.Right || pressedKey == Key.Home || pressedKey == Key.End))
        {
            if (_inputEditor.HandleShiftArrow(pressedKey, ctrl))
            {
                e.Handled = true;
                return;
            }
        }

        // Map WPF Key to ConsoleKey and send
        var consoleKey = MapKey(e.Key);
        if (consoleKey != null)
        {
            var data = InputEncoder.EncodeKey(consoleKey.Value, ctrl, shift: shift);
            if (data.Length > 0)
            {
                _inputEditor.ClearSelection();
                ScrollToBottom();
                _vm.WriteUserInput(data);
                e.Handled = true;
                return;
            }
        }

        // Don't set e.Handled for regular character keys —
        // let them propagate to OnPreviewTextInput for text entry.
    }

    /// <summary>
    /// Executes a user-rebindable command resolved by KeyBindingsService.
    /// Returns true if the command was handled (caller should mark e.Handled).
    /// Returning false lets the keystroke fall through to default handling — used
    /// for commands that only apply in a specific mode (e.g. copy-selection only
    /// when there is a selection, select-input-line only at the shell prompt).
    /// </summary>
    private bool DispatchBoundCommand(string commandId)
    {
        if (_vm == null) return false;

        switch (commandId)
        {
            case KeyBindingIds.ToggleSearch:
                if (_searchActive) CloseSearch(); else OpenSearch();
                return true;

            case KeyBindingIds.ScrollPageUp:
            case KeyBindingIds.ScrollPageDown:
                return TryScrollPage(commandId == KeyBindingIds.ScrollPageUp);

            case KeyBindingIds.UndoInput:
                return TryUndoRedo(redo: false);

            case KeyBindingIds.RedoInput:
                return TryUndoRedo(redo: true);

            case KeyBindingIds.CopySelection:
                return TryCopySelection();

            case KeyBindingIds.Paste:
                PasteFromClipboard();
                return true;

            case KeyBindingIds.SelectInputLine:
                return _inputEditor.SelectAll();

            default:
                return false;
        }
    }

    private bool TryScrollPage(bool up)
    {
        var buf = _vm?.Emulator?.Buffer;
        if (buf == null || (_vm?.Emulator?.AlternateScreen ?? false)) return false;

        int pageSize = Math.Max(1, Canvas.Rows - 1);
        int maxOffset = ViewportCalculator.MaxScrollOffset(buf.Rows, Canvas.Rows, buf.ScrollbackCount);
        _viewport.ScrollOffset = up
            ? Math.Min(_viewport.ScrollOffset + pageSize, maxOffset)
            : Math.Max(_viewport.ScrollOffset - pageSize, 0);
        _viewport.UserScrolledBack = _viewport.ScrollOffset > 0;
        UpdateScrollBar();
        Canvas.Invalidate();
        return true;
    }

    private bool TryUndoRedo(bool redo)
    {
        if (_vm == null) return false;

        var now = DateTime.UtcNow;
        if ((now - _lastUndoRedoTime).TotalMilliseconds < 150)
            return true; // swallow rapid repeats but mark handled
        _lastUndoRedoTime = now;

        var target = redo ? _vm.InputUndo.Redo() : _vm.InputUndo.Undo();
        if (target != null)
            ApplyUndoRedo(target);
        return true;
    }

    private bool TryCopySelection()
    {
        var selCanvas = Canvas.SelectionStart != null ? Canvas
                      : (_isSplit && PinnedCanvas.SelectionStart != null ? PinnedCanvas : null);
        if (selCanvas == null) return false;

        var text = selCanvas.GetSelectedText();
        if (!string.IsNullOrEmpty(text))
        {
            Clipboard.SetText(text);
            ClearSelection(selCanvas);
            selCanvas.Invalidate();
        }
        return true;
    }

    private void PasteFromClipboard()
    {
        if (_vm == null) return;
        string? textToPaste = null;
        if (Clipboard.ContainsImage())
        {
            var image = Clipboard.GetImage();
            if (image != null)
            {
                var path = _vm.PasteImage?.Invoke(image);
                if (path != null)
                    textToPaste = path;
            }
        }
        else if (Clipboard.ContainsText())
        {
            textToPaste = Clipboard.GetText();
        }

        if (textToPaste == null) return;

        if (_inputEditor.HasSelection && !textToPaste.Contains('\n') && _inputEditor.DeleteSelection(textToPaste))
        {
            ScrollToBottom();
            return;
        }

        PasteText(textToPaste);
    }

    private long ScreenRowToAbsoluteRow(TerminalBuffer buffer, int screenRow)
        => InputLineEditor.ScreenRowToAbsoluteRow(buffer, Canvas.Rows, screenRow);

    protected override void OnPreviewTextInput(TextCompositionEventArgs e)
    {
        if (_vm == null || !_vm.IsConnected || string.IsNullOrEmpty(e.Text)) return;

        // When search bar or overlay is active, let the TextBox handle text input natively
        if (_searchActive || _overlayActive) return;

        // If there is a selection within the input line, replace it with the typed text
        if (_inputEditor.HasSelection && _inputEditor.DeleteSelection(e.Text))
        {
            ScrollToBottom();
            e.Handled = true;
            return;
        }

        // Track typed characters for undo
        _vm.CurrentInputLine.Append(e.Text);
        _vm.InputUndo.Record(_vm.CurrentInputLine.ToString(), "char");

        _inputEditor.ClearSelection();
        ScrollToBottom();
        _vm.WriteUserInput(InputEncoder.EncodeText(e.Text));
        e.Handled = true;
    }

    // ─── Scrollbar ────────────────────────────────────────────────────────

    private void OnScrollBarScroll(object sender, ScrollEventArgs e)
    {
        var buffer = _vm?.Emulator?.Buffer;
        if (buffer == null) return;

        // ScrollBar value: 0 = top of scrollback, max = at bottom (live)
        // ScrollOffset: 0 = at bottom (live), max = top of scrollback
        int maxOffset = ViewportCalculator.MaxScrollOffset(buffer.Rows, Canvas.Rows, buffer.ScrollbackCount);
        _viewport.ScrollOffset = Math.Clamp(maxOffset - (int)e.NewValue, 0, maxOffset);
        _viewport.UserScrolledBack = _viewport.ScrollOffset > 0;
        Canvas.Invalidate();
    }

    private void UpdateScrollBar()
    {
        var buffer = _vm?.Emulator?.Buffer;
        if (buffer == null) return;

        bool isAlternate = _vm?.Emulator?.AlternateScreen ?? false;
        int maxOffset = ViewportCalculator.MaxScrollOffset(buffer.Rows, Canvas.Rows, buffer.ScrollbackCount);

        if (isAlternate || maxOffset == 0)
        {
            VerticalScrollBar.Visibility = Visibility.Collapsed;
            return;
        }

        int effectiveMax = ViewportCalculator.MaxScrollOffset(buffer.Rows, Canvas.Rows, buffer.EffectiveScrollbackCount);
        if (effectiveMax == 0)
        {
            VerticalScrollBar.Visibility = Visibility.Collapsed;
            _viewport.ScrollOffset = 0;
            return;
        }

        VerticalScrollBar.Visibility = Visibility.Visible;
        VerticalScrollBar.Maximum = maxOffset;
        VerticalScrollBar.ViewportSize = Math.Min(Canvas.Rows, buffer.Rows);
        // Convert ScrollOffset (0=bottom) to ScrollBar value (max=bottom)
        VerticalScrollBar.Value = maxOffset - _viewport.ScrollOffset;
    }

    private void ScrollToBottom()
    {
        var buffer = _vm?.Emulator?.Buffer;
        if (buffer == null) return;
        _viewport.ScrollOffset = 0;
        _viewport.UserScrolledBack = false;
        UpdateScrollBar();
    }

    // ─── Drag & Drop ──────────────────────────────────────────────────────

    private void OnDragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop)
            ? DragDropEffects.Copy
            : DragDropEffects.None;
        e.Handled = true;
    }

    private void OnFileDrop(object sender, DragEventArgs e)
    {
        if (e.Data.GetDataPresent(DataFormats.FileDrop) &&
            e.Data.GetData(DataFormats.FileDrop) is string[] files && files.Length > 0)
        {
            PasteText(files[0]);
            Canvas.Focus();
        }
        e.Handled = true;
    }

    // ─── Helpers ─────────────────────────────────────────────────────────

    private void PasteText(string text)
    {
        if (_vm == null || !_vm.IsConnected) return;

        if (_vm.Emulator?.BracketedPasteMode == true)
        {
            // Wrap in bracketed paste markers: ESC[200~ ... ESC[201~
            _vm.WriteUserInput(new byte[] { 0x1B, (byte)'[', (byte)'2', (byte)'0', (byte)'0', (byte)'~' });
            _vm.WriteUserInput(InputEncoder.EncodeText(text));
            _vm.WriteUserInput(new byte[] { 0x1B, (byte)'[', (byte)'2', (byte)'0', (byte)'1', (byte)'~' });
        }
        else
        {
            _vm.WriteUserInput(InputEncoder.EncodeText(text));
        }

        _vm.CurrentInputLine.Append(text);
        if (text.Contains('\n'))
            _vm.InputUndo.Clear(); // Can't undo across line submissions
        else
            _vm.InputUndo.Record(_vm.CurrentInputLine.ToString(), "paste");
    }

    private void ApplyUndoRedo(string targetText) => _inputEditor.ReplaceInputLine(targetText);

    private static ConsoleKey? MapKey(Key key)
    {
        return key switch
        {
            Key.Up => ConsoleKey.UpArrow,
            Key.Down => ConsoleKey.DownArrow,
            Key.Left => ConsoleKey.LeftArrow,
            Key.Right => ConsoleKey.RightArrow,
            Key.Home => ConsoleKey.Home,
            Key.End => ConsoleKey.End,
            Key.Insert => ConsoleKey.Insert,
            Key.Delete => ConsoleKey.Delete,
            Key.PageUp => ConsoleKey.PageUp,
            Key.PageDown => ConsoleKey.PageDown,
            Key.F1 => ConsoleKey.F1,
            Key.F2 => ConsoleKey.F2,
            Key.F3 => ConsoleKey.F3,
            Key.F4 => ConsoleKey.F4,
            Key.F5 => ConsoleKey.F5,
            Key.F6 => ConsoleKey.F6,
            Key.F7 => ConsoleKey.F7,
            Key.F8 => ConsoleKey.F8,
            Key.F9 => ConsoleKey.F9,
            Key.F10 => ConsoleKey.F10,
            Key.F11 => ConsoleKey.F11,
            Key.F12 => ConsoleKey.F12,
            Key.Tab => ConsoleKey.Tab,
            Key.Enter => ConsoleKey.Enter,
            Key.Escape => ConsoleKey.Escape,
            Key.Back => ConsoleKey.Backspace,
            // Ctrl+letter handled via modifier check
            Key.A => ConsoleKey.A, Key.B => ConsoleKey.B, Key.C => ConsoleKey.C,
            Key.D => ConsoleKey.D, Key.E => ConsoleKey.E, Key.F => ConsoleKey.F,
            Key.G => ConsoleKey.G, Key.H => ConsoleKey.H, Key.I => ConsoleKey.I,
            Key.J => ConsoleKey.J, Key.K => ConsoleKey.K, Key.L => ConsoleKey.L,
            Key.M => ConsoleKey.M, Key.N => ConsoleKey.N, Key.O => ConsoleKey.O,
            Key.P => ConsoleKey.P, Key.Q => ConsoleKey.Q, Key.R => ConsoleKey.R,
            Key.S => ConsoleKey.S, Key.T => ConsoleKey.T, Key.U => ConsoleKey.U,
            Key.V => ConsoleKey.V, Key.W => ConsoleKey.W, Key.X => ConsoleKey.X,
            Key.Y => ConsoleKey.Y, Key.Z => ConsoleKey.Z,
            _ => null
        };
    }
}
