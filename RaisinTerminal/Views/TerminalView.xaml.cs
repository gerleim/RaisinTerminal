using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Raisin.EventSystem;
using RaisinTerminal.Core.Terminal;
using RaisinTerminal.Services;
using RaisinTerminal.ViewModels;

namespace RaisinTerminal.Views;

public partial class TerminalView : UserControl
{
    private TerminalSessionViewModel? _vm;
    private DispatcherTimer? _cursorTimer;
    private bool _selecting;
    private bool _userScrolledBack;
    private DateTime _lastUndoRedoTime;
    private bool _overlayActive;
    private bool _lastAlternateScreen;

    public TerminalView()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
        Canvas.SizeChanged += OnCanvasSizeChanged;
        VerticalScrollBar.Scroll += OnScrollBarScroll;
        Drop += OnFileDrop;
        DragOver += OnDragOver;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        _vm = DataContext as TerminalSessionViewModel;
        if (_vm == null) return;

        Canvas.Emulator = _vm.Emulator;
        Canvas.CompressEmptyLines = SettingsService.Current.CompressEmptyLines;
        _vm.RenderRequested += OnRenderRequested;

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
        }

        // Defer focus to avoid NullReferenceException when HWND isn't ready yet
        Dispatcher.BeginInvoke(() => Canvas.Focus(), System.Windows.Threading.DispatcherPriority.Input);
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        if (_vm != null)
            _vm.RenderRequested -= OnRenderRequested;
        _cursorTimer?.Stop();
    }

    private void OnRenderRequested()
    {
        // Pick up any settings changes (e.g. after Options dialog)
        Canvas.CompressEmptyLines = SettingsService.Current.CompressEmptyLines;
        // Reset cursor to visible on new output
        Canvas.CursorVisible = true;
        _cursorTimer?.Stop();
        _cursorTimer?.Start();

        // Auto-scroll to bottom on new output unless the user explicitly scrolled back.
        // ScrollOffset may be > 0 from ScrollUpRegion auto-incrementing while the tab
        // was inactive — that's not user-initiated, so we should snap back to live.
        if (!_userScrolledBack)
            ScrollToBottom();

        UpdateScrollBar();
        Canvas.Invalidate();

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

        int cols = Canvas.Columns;
        int rows = Canvas.Rows;
        if (cols <= 0 || rows <= 0) return;

        if (!_vm.IsConnected)
        {
            _vm.StartSession(cols, rows);
            Canvas.Emulator = _vm.Emulator;
        }
        else
        {
            _vm.Resize(cols, rows);
        }
    }

    // ─── Keyboard Input ──────────────────────────────────────────────────

    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        if (_vm == null || !_vm.IsConnected) return;

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

        // Ctrl+Z → undo input
        if (ctrl && !shift && e.Key == Key.Z)
        {
            var now = DateTime.UtcNow;
            if (e.IsRepeat || (now - _lastUndoRedoTime).TotalMilliseconds < 150)
            {
                e.Handled = true;
                return;
            }
            _lastUndoRedoTime = now;

            var target = _vm.InputUndo.Undo();
            if (target != null)
                ApplyUndoRedo(target);
            e.Handled = true;
            return;
        }

        // Ctrl+Y or Ctrl+Shift+Z → redo input
        if ((ctrl && !shift && e.Key == Key.Y) || (ctrl && shift && e.Key == Key.Z))
        {
            var now = DateTime.UtcNow;
            if (e.IsRepeat || (now - _lastUndoRedoTime).TotalMilliseconds < 150)
            {
                e.Handled = true;
                return;
            }
            _lastUndoRedoTime = now;

            var target = _vm.InputUndo.Redo();
            if (target != null)
                ApplyUndoRedo(target);
            e.Handled = true;
            return;
        }

        // Ctrl+C with selection → copy to clipboard
        if (ctrl && e.Key == Key.C && Canvas.SelectionStart != null)
        {
            var text = Canvas.GetSelectedText();
            if (!string.IsNullOrEmpty(text))
            {
                Clipboard.SetText(text);
                ClearSelection();
                Canvas.Invalidate();
            }
            e.Handled = true;
            return;
        }

        // Ctrl+V → paste from clipboard (image or text)
        if (ctrl && e.Key == Key.V)
        {
            if (Clipboard.ContainsImage())
            {
                var image = Clipboard.GetImage();
                if (image != null)
                {
                    var path = _vm.PasteImage?.Invoke(image);
                    if (path != null)
                        PasteText(path);
                }
            }
            else if (Clipboard.ContainsText())
            {
                PasteText(Clipboard.GetText());
            }
            e.Handled = true;
            return;
        }

        // Ctrl+A → select current input line
        if (ctrl && e.Key == Key.A && !(_vm.Emulator?.AlternateScreen ?? false))
        {
            var inputLen = _vm.CurrentInputLine.Length;
            var buffer = _vm.Emulator?.Buffer;
            if (inputLen > 0 && buffer != null)
            {
                int row = buffer.CursorRow;
                int endCol = buffer.Columns - 1;
                while (endCol >= 0 && buffer.GetCell(row, endCol).Character == ' ')
                    endCol--;
                if (endCol >= 0)
                {
                    int startCol = Math.Max(0, endCol - inputLen + 1);
                    long absRow = buffer.TotalLinesScrolled + row;
                    Canvas.SelectionStart = (absRow, startCol);
                    Canvas.SelectionEnd = (absRow, endCol);
                    Canvas.Invalidate();
                }
            }
            e.Handled = true;
            return;
        }

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
            // Deferred via BeginInvoke so the Enter keypress that submits /clear is sent first.
            if (command.Trim().Equals("/clear", StringComparison.OrdinalIgnoreCase) &&
                !string.IsNullOrEmpty(_vm.ClaudeSessionName) &&
                _vm.HasRunningCommand &&
                string.Equals(_vm.RunningChildName, "claude", StringComparison.OrdinalIgnoreCase))
            {
                Dispatcher.BeginInvoke(_vm.BeginInputSuppression);
            }
        }

        // Track Backspace for current input line
        if (e.Key == Key.Back)
        {
            if (_vm.CurrentInputLine.Length > 0)
            {
                _vm.CurrentInputLine.Length--;
                _vm.InputUndo.Record(_vm.CurrentInputLine.ToString(), "delete");
            }
        }

        // Track Ctrl+C (no selection) and Escape → reset current input
        if (e.Key == Key.Escape || (ctrl && e.Key == Key.C && Canvas.SelectionStart == null))
        {
            _vm.CurrentInputLine.Clear();
            _vm.InputUndo.Clear();
            CommandHistoryService.Instance.ResetNavigation();
        }

        // Up/Down arrow keys are passed through to the child process,
        // which handles its own history navigation (readline, Claude CLI, etc.).

        // Shift+PageUp/PageDown for scrollback navigation
        if (shift && (e.Key == Key.PageUp || e.Key == Key.PageDown))
        {
            var buf = _vm.Emulator?.Buffer;
            if (buf != null && !(_vm.Emulator?.AlternateScreen ?? false))
            {
                int pageSize = Math.Max(1, Canvas.Rows - 1);
                buf.ScrollOffset = e.Key == Key.PageUp
                    ? Math.Min(buf.ScrollOffset + pageSize, buf.ScrollbackCount)
                    : Math.Max(buf.ScrollOffset - pageSize, 0);
                _userScrolledBack = buf.ScrollOffset > 0;
                UpdateScrollBar();
                Canvas.Invalidate();
            }
            e.Handled = true;
            return;
        }

        // Map WPF Key to ConsoleKey and send
        var consoleKey = MapKey(e.Key);
        if (consoleKey != null)
        {
            var data = InputEncoder.EncodeKey(consoleKey.Value, ctrl, shift: shift);
            if (data.Length > 0)
            {
                ClearSelection();
                ScrollToBottom();
                _vm.WriteUserInput(data);
                e.Handled = true;
                return;
            }
        }

        // Don't set e.Handled for regular character keys —
        // let them propagate to OnPreviewTextInput for text entry.
    }

    protected override void OnPreviewTextInput(TextCompositionEventArgs e)
    {
        if (_vm == null || !_vm.IsConnected || string.IsNullOrEmpty(e.Text)) return;

        // When overlay is active, let the TextBox handle text input natively
        if (_overlayActive) return;

        // Track typed characters for undo
        _vm.CurrentInputLine.Append(e.Text);
        _vm.InputUndo.Record(_vm.CurrentInputLine.ToString(), "char");

        ClearSelection();
        ScrollToBottom();
        _vm.WriteUserInput(InputEncoder.EncodeText(e.Text));
        e.Handled = true;
    }

    // ─── Mouse Input ─────────────────────────────────────────────────────

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        // Don't intercept mouse events targeted at the overlay TextBox
        if (_overlayActive && e.OriginalSource is DependencyObject src &&
            IsDescendantOf(src, InputOverlay))
            return;

        Canvas.Focus();
        var pos = e.GetPosition(Canvas);
        var (row, col) = Canvas.HitTest(pos);

        if (e.ClickCount == 2)
        {
            // Double-click: select the word under cursor
            SelectWordAt(row, col);
            _selecting = false;
            e.Handled = true;
            return;
        }

        ClearSelection();
        Canvas.SelectionStart = (row, col);
        Canvas.SelectionEnd = (row, col);
        _selecting = true;
        Canvas.CaptureMouse();
        e.Handled = true;
    }

    private void SelectWordAt(long absRow, int col)
    {
        var buffer = _vm?.Emulator?.Buffer;
        if (buffer == null) return;

        // Convert absolute row to viewport row for cell access
        int viewRow = (int)(absRow - buffer.TotalLinesScrolled + buffer.ScrollOffset);
        if (viewRow < 0 || viewRow >= buffer.Rows) return;

        char CharAt(int c) => c >= 0 && c < buffer.Columns
            ? buffer.GetVisibleCell(viewRow, c).Character
            : '\0';

        char ch = CharAt(col);
        if (ch == '\0' || ch == ' ') return;

        bool IsWordChar(char c) => c != '\0' && c != ' ';

        int start = col;
        while (start > 0 && IsWordChar(CharAt(start - 1)))
            start--;

        int end = col;
        while (end < buffer.Columns - 1 && IsWordChar(CharAt(end + 1)))
            end++;

        Canvas.SelectionStart = (absRow, start);
        Canvas.SelectionEnd = (absRow, end);
        Canvas.Invalidate();
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        if (!_selecting) return;

        var pos = e.GetPosition(Canvas);
        var (row, col) = Canvas.HitTest(pos);
        Canvas.SelectionEnd = (row, col);
        Canvas.Invalidate();
        e.Handled = true;
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        if (!_selecting) return;
        _selecting = false;
        Canvas.ReleaseMouseCapture();

        // If selection is trivial (click with minimal drag), treat as a click to reposition cursor
        var start = Canvas.SelectionStart;
        var end = Canvas.SelectionEnd;
        if (start != null && end != null
            && start.Value.Row == end.Value.Row
            && Math.Abs(start.Value.Col - end.Value.Col) <= 3)
        {
            ClearSelection();
            TryRepositionCursor(end.Value);
        }

        Canvas.Invalidate();
        e.Handled = true;
    }

    /// <summary>
    /// Sends left/right arrow key sequences to move the shell cursor to the clicked column.
    /// Only works at the prompt line (not in alternate screen, not scrolled back).
    /// </summary>
    private void TryRepositionCursor((long Row, int Col) target)
    {
        if (_vm?.Emulator == null || !_vm.IsConnected) return;

        var emulator = _vm.Emulator;
        var buffer = emulator.Buffer;

        // Only at live prompt: not in TUI mode, not scrolled back
        if (emulator.AlternateScreen) return;
        if (buffer.ScrollOffset != 0) return;

        // Only on the cursor's current row (compare in absolute space)
        if (target.Row != buffer.TotalLinesScrolled + buffer.CursorRow) return;

        int delta = target.Col - buffer.CursorCol;
        System.Diagnostics.Debug.WriteLine($"[Reposition] target=({target.Row},{target.Col}) cursor=({buffer.CursorRow},{buffer.CursorCol}) delta={delta}");
        if (delta == 0) return;

        // Send the appropriate arrow key sequences as a single batch
        var arrowKey = delta > 0 ? ConsoleKey.RightArrow : ConsoleKey.LeftArrow;
        int count = Math.Abs(delta);
        var oneArrow = InputEncoder.EncodeKey(arrowKey, ctrl: false, shift: false);
        var batch = new byte[oneArrow.Length * count];
        for (int i = 0; i < count; i++)
            Buffer.BlockCopy(oneArrow, 0, batch, i * oneArrow.Length, oneArrow.Length);
        _vm.WriteUserInput(batch);
    }

    protected override void OnMouseRightButtonDown(MouseButtonEventArgs e)
    {
        // Don't intercept mouse events targeted at the overlay TextBox
        if (_overlayActive && e.OriginalSource is DependencyObject src &&
            IsDescendantOf(src, InputOverlay))
            return;

        // Right-click paste
        Canvas.Focus();
        if (_vm != null && _vm.IsConnected && Clipboard.ContainsText())
        {
            PasteText(Clipboard.GetText());
        }
        e.Handled = true;
    }

    protected override void OnMouseWheel(MouseWheelEventArgs e)
    {
        var buffer = _vm?.Emulator?.Buffer;
        if (buffer == null || (_vm?.Emulator?.AlternateScreen ?? false))
        {
            e.Handled = true;
            return;
        }

        int delta = e.Delta > 0 ? 3 : -3; // scroll up = increase offset
        buffer.ScrollOffset = Math.Clamp(buffer.ScrollOffset + delta, 0, buffer.ScrollbackCount);
        _userScrolledBack = buffer.ScrollOffset > 0;
        UpdateScrollBar();
        Canvas.Invalidate();
        e.Handled = true;
    }

    // ─── Scrollbar ────────────────────────────────────────────────────────

    private void OnScrollBarScroll(object sender, ScrollEventArgs e)
    {
        var buffer = _vm?.Emulator?.Buffer;
        if (buffer == null) return;

        // ScrollBar value: 0 = top of scrollback, max = at bottom (live)
        // ScrollOffset: 0 = at bottom (live), max = top of scrollback
        int maxOffset = buffer.ScrollbackCount;
        buffer.ScrollOffset = maxOffset - (int)e.NewValue;
        buffer.ClampScrollOffset();
        _userScrolledBack = buffer.ScrollOffset > 0;
        Canvas.Invalidate();
    }

    private void UpdateScrollBar()
    {
        var buffer = _vm?.Emulator?.Buffer;
        if (buffer == null) return;

        bool isAlternate = _vm?.Emulator?.AlternateScreen ?? false;
        int scrollbackCount = buffer.ScrollbackCount;

        if (isAlternate || scrollbackCount == 0)
        {
            VerticalScrollBar.Visibility = Visibility.Collapsed;
            return;
        }

        VerticalScrollBar.Visibility = Visibility.Visible;
        VerticalScrollBar.Maximum = scrollbackCount;
        VerticalScrollBar.ViewportSize = buffer.Rows;
        // Convert ScrollOffset (0=bottom) to ScrollBar value (max=bottom)
        VerticalScrollBar.Value = scrollbackCount - buffer.ScrollOffset;
    }

    private void ScrollToBottom()
    {
        var buffer = _vm?.Emulator?.Buffer;
        if (buffer == null) return;
        buffer.ScrollOffset = 0;
        _userScrolledBack = false;
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

    private void ApplyUndoRedo(string targetText)
    {
        if (_vm == null) return;
        var currentText = _vm.CurrentInputLine.ToString();

        // Only delete the differing suffix and type the replacement,
        // sending everything as a single atomic write to avoid race conditions.
        int commonLen = 0;
        int minLen = Math.Min(currentText.Length, targetText.Length);
        while (commonLen < minLen && currentText[commonLen] == targetText[commonLen])
            commonLen++;

        int charsToDelete = currentText.Length - commonLen;
        string charsToType = targetText.Substring(commonLen);

        var backspaces = new byte[charsToDelete];
        Array.Fill(backspaces, (byte)0x7F);
        var replacement = InputEncoder.EncodeText(charsToType);

        // Combine into a single write so ConPTY delivers atomically
        var combined = new byte[backspaces.Length + replacement.Length];
        Buffer.BlockCopy(backspaces, 0, combined, 0, backspaces.Length);
        Buffer.BlockCopy(replacement, 0, combined, backspaces.Length, replacement.Length);
        if (combined.Length > 0)
            _vm.WriteUserInput(combined);

        // Update tracking
        _vm.CurrentInputLine.Clear();
        _vm.CurrentInputLine.Append(targetText);
    }

    private void ClearSelection()
    {
        Canvas.SelectionStart = null;
        Canvas.SelectionEnd = null;
    }

    private static bool IsDescendantOf(DependencyObject child, DependencyObject parent)
    {
        var current = child;
        while (current != null)
        {
            if (current == parent) return true;
            current = VisualTreeHelper.GetParent(current);
        }
        return false;
    }

    // ─── Alternate Screen Input Overlay ───────────────────────────────────

    private static readonly byte[] BracketedPasteStart = { 0x1B, (byte)'[', (byte)'2', (byte)'0', (byte)'0', (byte)'~' };
    private static readonly byte[] BracketedPasteEnd = { 0x1B, (byte)'[', (byte)'2', (byte)'0', (byte)'1', (byte)'~' };

    private void ShowOverlay()
    {
        App.Events.Log(this, "Overlay shown (alternate screen entered)", category: "Terminal");
        _overlayActive = true;
        InputOverlay.Visibility = Visibility.Visible;
        Dispatcher.BeginInvoke(() => OverlayInput.Focus(), DispatcherPriority.Input);
    }

    private void HideOverlay()
    {
        App.Events.Log(this, "Overlay hidden (alternate screen exited)", category: "Terminal");
        _overlayActive = false;
        InputOverlay.Visibility = Visibility.Collapsed;
        // Don't clear text — preserve it across hide/show in case the TUI app
        // briefly exits and re-enters alternate screen (e.g. Claude status updates)
        Dispatcher.BeginInvoke(() => Canvas.Focus(), DispatcherPriority.Input);
    }

    private void HandleOverlayKeyDown(KeyEventArgs e)
    {
        if (_vm == null) return;

        bool ctrl = (Keyboard.Modifiers & ModifierKeys.Control) != 0;
        bool shift = (Keyboard.Modifiers & ModifierKeys.Shift) != 0;

        // Enter (no modifier) → submit text to ConPTY
        if (e.Key == Key.Enter && !shift && !ctrl)
        {
            SubmitOverlayInput();
            e.Handled = true;
            return;
        }

        // Shift+Enter → insert newline in TextBox
        if (e.Key == Key.Enter && (shift || ctrl))
        {
            OverlayInput.SelectedText = "\n";
            OverlayInput.CaretIndex = OverlayInput.SelectionStart + 1;
            e.Handled = true;
            return;
        }

        // Escape → clear TextBox, send Escape to ConPTY
        if (e.Key == Key.Escape)
        {
            OverlayInput.Clear();
            _vm.WriteUserInput(InputEncoder.EncodeKey(ConsoleKey.Escape));
            e.Handled = true;
            return;
        }

        // Up/Down with empty TextBox → forward to ConPTY (TUI history/navigation)
        if ((e.Key == Key.Up || e.Key == Key.Down) && string.IsNullOrEmpty(OverlayInput.Text))
        {
            var key = e.Key == Key.Up ? ConsoleKey.UpArrow : ConsoleKey.DownArrow;
            _vm.WriteUserInput(InputEncoder.EncodeKey(key, ctrl, shift: shift));
            e.Handled = true;
            return;
        }

        // Tab → forward to ConPTY (TUI completion)
        if (e.Key == Key.Tab)
        {
            _vm.WriteUserInput(InputEncoder.EncodeKey(ConsoleKey.Tab, shift: shift));
            e.Handled = true;
            return;
        }

        // Ctrl+C → clear TextBox, send interrupt to ConPTY
        if (ctrl && e.Key == Key.C)
        {
            OverlayInput.Clear();
            _vm.WriteUserInput(new byte[] { 0x03 }); // ETX
            e.Handled = true;
            return;
        }

        // Ctrl+V → paste into TextBox
        if (ctrl && e.Key == Key.V)
        {
            if (Clipboard.ContainsText())
            {
                OverlayInput.SelectedText = Clipboard.GetText();
            }
            e.Handled = true;
            return;
        }

        // All other keys: let the TextBox handle natively
        // (Ctrl+Z/Y undo/redo, Ctrl+A select all, arrow keys, etc.)
    }

    private void SubmitOverlayInput()
    {
        if (_vm == null) return;

        var text = OverlayInput.Text;

        // Send the text to ConPTY
        if (!string.IsNullOrEmpty(text))
        {
            if (text.Contains('\n') && _vm.Emulator?.BracketedPasteMode == true)
            {
                _vm.WriteUserInput(BracketedPasteStart);
                _vm.WriteUserInput(InputEncoder.EncodeText(text));
                _vm.WriteUserInput(BracketedPasteEnd);
            }
            else
            {
                _vm.WriteUserInput(InputEncoder.EncodeText(text));
            }

            // Track in command history
            if (!(_vm.Emulator?.AlternateScreen ?? false))
            {
                CommandHistoryService.Instance.Add(text);
                _vm.LastCommand = text;
            }
        }

        // Send Enter
        _vm.WriteUserInput(new byte[] { (byte)'\r' });

        OverlayInput.Clear();
    }

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
