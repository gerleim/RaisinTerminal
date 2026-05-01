using System.Windows;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Threading;
using RaisinTerminal.Controls;
using RaisinTerminal.Core.Models;
using RaisinTerminal.Core.Terminal;

namespace RaisinTerminal.Views;

public partial class TerminalView
{
    private readonly TerminalViewport _pinnedViewport = new() { IsLive = false };
    private bool _pinnedSelecting;
    private bool _isSplit;

    private void InitSplitView()
    {
        PinnedCanvas.PreviewMouseLeftButtonDown += OnPinnedMouseLeftButtonDown;
        PinnedCanvas.PreviewMouseMove += OnPinnedMouseMove;
        PinnedCanvas.PreviewMouseLeftButtonUp += OnPinnedMouseLeftButtonUp;
        PinnedCanvas.PreviewMouseWheel += OnPinnedMouseWheel;
        PinnedScrollBar.Scroll += OnPinnedScrollBarScroll;
        PinnedCanvas.SizeChanged += OnPinnedCanvasSizeChanged;
    }

    private void ToggleSplit()
    {
        if (_isSplit) CloseSplit();
        else OpenSplit();
    }

    private void OpenSplit()
    {
        if (_isSplit) return;
        var buffer = _vm?.Emulator?.Buffer;
        if (buffer == null) return;

        if (!buffer.Viewports.Contains(_pinnedViewport))
            buffer.Viewports.Add(_pinnedViewport);

        int start = _viewport.ScrollOffset;
        if (start == 0)
        {
            int canvasRows = PinnedCanvas.Rows > 0 ? PinnedCanvas.Rows : buffer.Rows;
            start = ViewportCalculator.PinnedInitialOffset(buffer.Rows, canvasRows, buffer.ScrollbackCount);
        }
        _pinnedViewport.ScrollOffset = start;
        _pinnedViewport.UserScrolledBack = _pinnedViewport.ScrollOffset > 0;

        PinnedPane.Visibility = Visibility.Visible;
        PaneSplitter.Visibility = Visibility.Visible;
        PinnedPaneRow.Height = new GridLength(1, GridUnitType.Star);
        SplitterRow.Height = GridLength.Auto;

        _isSplit = true;
        UpdatePinnedScrollBar();
        PinnedCanvas.Invalidate();

        Dispatcher.BeginInvoke(AdjustPinnedForEmpties, DispatcherPriority.Background);
    }

    private bool IsVisibleRowEmpty(TerminalBuffer buffer, int row, int offset, int viewRows)
    {
        for (int c = 0; c < buffer.Columns; c++)
        {
            var cell = buffer.GetVisibleCell(row, c, offset, viewRows);
            if (cell.Character != ' ' && cell.Character != '\0' && cell.Character != '│')
                return false;
            if (cell.BackgroundR != CellData.DefaultBgR || cell.BackgroundG != CellData.DefaultBgG || cell.BackgroundB != CellData.DefaultBgB)
                return false;
        }
        return true;
    }

    private void AdjustPinnedForEmpties()
    {
        var buffer = _vm?.Emulator?.Buffer;
        if (buffer == null || !_isSplit) return;
        if (PinnedCanvas.Rows <= 0 || buffer.Rows <= 0) return;

        int offset = _pinnedViewport.ScrollOffset;
        int canvasRows = PinnedCanvas.Rows;
        int viewRows = Math.Min(buffer.Rows, canvasRows);
        int maxOffset = ViewportCalculator.MaxScrollOffset(buffer.Rows, canvasRows, buffer.ScrollbackCount);
        bool changed = false;

        // First: skip trailing empties by increasing offset to pull more scrollback
        if (offset < maxOffset)
        {
            int trailSkip = 0;
            for (int row = canvasRows - 1; row >= 0; row--)
            {
                if (!IsVisibleRowEmpty(buffer, row, offset, viewRows)) break;
                trailSkip++;
            }
            if (trailSkip > 0 && trailSkip < canvasRows)
            {
                offset = Math.Min(maxOffset, offset + trailSkip);
                changed = true;
            }
        }

        // Then: skip leading empties (takes priority — no blank rows at top)
        if (offset > 0)
        {
            int leadSkip = 0;
            for (int row = 0; row < canvasRows; row++)
            {
                if (!IsVisibleRowEmpty(buffer, row, offset, viewRows)) break;
                leadSkip++;
            }
            if (leadSkip > 0 && leadSkip < canvasRows)
            {
                offset = Math.Max(0, offset - leadSkip);
                changed = true;
            }
        }

        if (changed)
        {
            _pinnedViewport.ScrollOffset = offset;
            _pinnedViewport.UserScrolledBack = _pinnedViewport.ScrollOffset > 0;
            UpdatePinnedScrollBar();
            PinnedCanvas.Invalidate();
        }
    }

    private void CloseSplit()
    {
        if (!_isSplit) return;

        var buffer = _vm?.Emulator?.Buffer;
        buffer?.Viewports.Remove(_pinnedViewport);

        ClearSelection(PinnedCanvas);
        PinnedPane.Visibility = Visibility.Collapsed;
        PaneSplitter.Visibility = Visibility.Collapsed;
        PinnedPaneRow.Height = new GridLength(0);
        SplitterRow.Height = new GridLength(0);
        PinnedScrollBar.Visibility = Visibility.Collapsed;

        _isSplit = false;
        _pinnedCanvasRowsPrev = 0;
        Canvas.Focus();
    }

    private void OnCloseSplit(object sender, RoutedEventArgs e) => CloseSplit();

    private void UpdatePinnedScrollBar()
    {
        var buffer = _vm?.Emulator?.Buffer;
        if (buffer == null) return;

        bool isAlternate = _vm?.Emulator?.AlternateScreen ?? false;
        int maxOffset = ViewportCalculator.MaxScrollOffset(buffer.Rows, PinnedCanvas.Rows, buffer.ScrollbackCount);

        if (isAlternate || maxOffset == 0)
        {
            PinnedScrollBar.Visibility = Visibility.Collapsed;
            return;
        }

        PinnedScrollBar.Visibility = Visibility.Visible;
        PinnedScrollBar.Maximum = maxOffset;
        PinnedScrollBar.ViewportSize = Math.Min(PinnedCanvas.Rows, buffer.Rows);
        PinnedScrollBar.Value = maxOffset - _pinnedViewport.ScrollOffset;
    }

    private void OnPinnedScrollBarScroll(object sender, ScrollEventArgs e)
    {
        var buffer = _vm?.Emulator?.Buffer;
        if (buffer == null) return;

        int maxOffset = ViewportCalculator.MaxScrollOffset(buffer.Rows, PinnedCanvas.Rows, buffer.ScrollbackCount);
        _pinnedViewport.ScrollOffset = Math.Clamp(maxOffset - (int)e.NewValue, 0, maxOffset);
        _pinnedViewport.UserScrolledBack = _pinnedViewport.ScrollOffset > 0;
        PinnedCanvas.Invalidate();
    }

    private void OnPinnedMouseWheel(object sender, MouseWheelEventArgs e)
    {
        var buffer = _vm?.Emulator?.Buffer;
        if (buffer == null || (_vm?.Emulator?.AlternateScreen ?? false))
        {
            e.Handled = true;
            return;
        }

        int delta = e.Delta > 0 ? 3 : -3;
        int maxOffset = ViewportCalculator.MaxScrollOffset(buffer.Rows, PinnedCanvas.Rows, buffer.ScrollbackCount);
        _pinnedViewport.ScrollOffset = Math.Clamp(_pinnedViewport.ScrollOffset + delta, 0, maxOffset);
        _pinnedViewport.UserScrolledBack = _pinnedViewport.ScrollOffset > 0;
        UpdatePinnedScrollBar();
        PinnedCanvas.Invalidate();
        e.Handled = true;
    }

    private int _pinnedCanvasRowsPrev;

    private void OnPinnedCanvasSizeChanged(object sender, SizeChangedEventArgs e)
    {
        var buffer = _vm?.Emulator?.Buffer;
        if (buffer == null || !_isSplit) return;

        int newRows = PinnedCanvas.Rows;
        _pinnedViewport.CanvasRows = newRows;

        if (_pinnedCanvasRowsPrev == 0 && newRows > 0)
        {
            _pinnedViewport.ScrollOffset = ViewportCalculator.PinnedInitialOffset(buffer.Rows, newRows, buffer.ScrollbackCount);
            _pinnedViewport.UserScrolledBack = _pinnedViewport.ScrollOffset > 0;
            _pinnedCanvasRowsPrev = newRows;
            
            UpdatePinnedScrollBar();
            PinnedCanvas.Invalidate();
            AdjustPinnedForEmpties();
            return;
        }
        if (_pinnedCanvasRowsPrev > 0 && newRows != _pinnedCanvasRowsPrev)
        {
            int delta = newRows - _pinnedCanvasRowsPrev;
            _pinnedViewport.ScrollOffset = Math.Max(0, _pinnedViewport.ScrollOffset - delta);
            _pinnedViewport.UserScrolledBack = _pinnedViewport.ScrollOffset > 0;

        }
        _pinnedCanvasRowsPrev = newRows;
        UpdatePinnedScrollBar();
    }

    private void OnPinnedMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        var pos = e.GetPosition(PinnedCanvas);
        var (row, col) = PinnedCanvas.HitTest(pos);

        if (e.ClickCount == 2)
        {
            SelectWordAtPinned(row, col);
            _pinnedSelecting = false;
            e.Handled = true;
            return;
        }

        ClearSelection(PinnedCanvas);
        PinnedCanvas.SelectionStart = (row, col);
        PinnedCanvas.SelectionEnd = (row, col);
        _pinnedSelecting = true;
        PinnedCanvas.CaptureMouse();
        PinnedCanvas.Invalidate();
        e.Handled = true;
    }

    private void OnPinnedMouseMove(object sender, MouseEventArgs e)
    {
        if (!_pinnedSelecting) return;
        var pos = e.GetPosition(PinnedCanvas);
        var (row, col) = PinnedCanvas.HitTest(pos);
        PinnedCanvas.SelectionEnd = (row, col);
        PinnedCanvas.Invalidate();
        e.Handled = true;
    }

    private void OnPinnedMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (!_pinnedSelecting) return;
        _pinnedSelecting = false;
        PinnedCanvas.ReleaseMouseCapture();
        PinnedCanvas.Invalidate();
        e.Handled = true;
    }

    private void SelectWordAtPinned(long absRow, int col) =>
        SelectWordAt(PinnedCanvas, absRow, col);

}
