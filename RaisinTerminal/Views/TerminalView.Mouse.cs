using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using RaisinTerminal.Controls;
using RaisinTerminal.Core.Terminal;

namespace RaisinTerminal.Views;

public partial class TerminalView
{
    private bool _selecting;

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
            SelectWordAt(Canvas, row, col);
            _selecting = false;
            e.Handled = true;
            return;
        }

        _inputEditor.ClearSelection();
        Canvas.SelectionStart = (row, col);
        Canvas.SelectionEnd = (row, col);
        _selecting = true;
        Canvas.CaptureMouse();
        e.Handled = true;
    }

    private void SelectWordAt(TerminalCanvas canvas, long absRow, int col)
    {
        var buffer = _vm?.Emulator?.Buffer;
        if (buffer == null) return;

        char CharAt(int c) => c >= 0 && c < buffer.Columns
            ? buffer.GetCellAtAbsoluteRow(absRow, c).Character
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

        canvas.SelectionStart = (absRow, start);
        canvas.SelectionEnd = (absRow, end);
        canvas.Invalidate();
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
            _inputEditor.ClearSelection();
            TryRepositionCursor(end.Value);
        }

        Canvas.Invalidate();
        e.Handled = true;
    }

    private void TryRepositionCursor((long Row, int Col) target)
    {
        if (_vm?.Emulator == null || !_vm.IsConnected) return;

        var emulator = _vm.Emulator;
        var buffer = emulator.Buffer;

        // Only at live prompt: not in TUI mode, not scrolled back
        if (emulator.AlternateScreen) return;
        if (_viewport.ScrollOffset != 0) return;

        // Only on the cursor's current row (compare in absolute space)
        if (target.Row != ScreenRowToAbsoluteRow(buffer, buffer.CursorRow)) return;

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
        int maxOffset = ViewportCalculator.MaxScrollOffset(buffer.Rows, Canvas.Rows, buffer.ScrollbackCount);
        _viewport.ScrollOffset = Math.Clamp(_viewport.ScrollOffset + delta, 0, maxOffset);
        _viewport.UserScrolledBack = _viewport.ScrollOffset > 0;
        UpdateScrollBar();
        Canvas.Invalidate();
        e.Handled = true;
    }

    private static void ClearSelection(TerminalCanvas canvas)
    {
        canvas.SelectionStart = null;
        canvas.SelectionEnd = null;
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
}
