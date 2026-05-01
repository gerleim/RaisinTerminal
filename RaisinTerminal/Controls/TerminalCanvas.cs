using System.Globalization;
using System.Windows;
using System.Windows.Media;
using RaisinTerminal.Core.Models;
using RaisinTerminal.Core.Terminal;

namespace RaisinTerminal.Controls;

/// <summary>
/// Custom FrameworkElement for terminal rendering. Draws the character grid
/// from TerminalBuffer using low-level DrawingContext for performance.
/// </summary>
public partial class TerminalCanvas : FrameworkElement
{
    private readonly Typeface _typeface = new("Consolas");
    private readonly Typeface _typefaceBold = new(new FontFamily("Consolas"), FontStyles.Normal, FontWeights.Bold, FontStretches.Normal);
    private readonly Typeface _typefaceItalic = new(new FontFamily("Consolas"), FontStyles.Italic, FontWeights.Normal, FontStretches.Normal);
    private readonly Typeface _typefaceBoldItalic = new(new FontFamily("Consolas"), FontStyles.Italic, FontWeights.Bold, FontStretches.Normal);
    private const double FontSize = 14;
    private const double Dpi = 96;

    private double _cellWidth;
    private double _cellHeight;
    private double _baseline;
    private bool _measured;

    // Fraction of normal height for empty rows (visual compression)
    private const double EmptyRowScale = 0.35;

    // Row Y-positions from the last render, used by HitTest
    internal double[]? _rowYPositions;

    // Number of extra scrollback rows shown above the normal viewport
    // due to empty line compression. Used by HitTest and cursor positioning.
    internal int _extraRows;

    // baseRowCount used at last render. Captured for HitTest so it can recover
    // the same display→absolute row mapping OnRender used.
    internal int _baseRowCount;

    // absRowBase from the last render, for HitTest consistency.
    private long _absRowBase;

    // Glyph cache: avoids allocating a new FormattedText per cell per frame.
    // Key: (character, typeface index 0-3, fg color).
    private readonly Dictionary<(char, byte, byte, byte, byte), FormattedText> _glyphCache = new();

    // Brush cache: avoids per-cell allocations. Frozen brushes persist across frames.
    private readonly Dictionary<(byte, byte, byte), Brush> _brushCache = new();

    // Pen cache: avoids per-cell allocations for underline/strikethrough.
    private readonly Dictionary<(byte, byte, byte), Pen> _penCache = new();

    public int Columns => _cellWidth > 0 ? (int)(ActualWidth / _cellWidth) : 0;
    public int Rows => _cellHeight > 0 ? (int)(ActualHeight / _cellHeight) : 0;

    public double CellWidth => _cellWidth;
    public double CellHeight => _cellHeight;

    public TerminalEmulator? Emulator { get; set; }

    /// <summary>
    /// Viewport driving this canvas's scroll position. Required for rendering
    /// anything other than the live bottom of the buffer.
    /// </summary>
    public TerminalViewport? Viewport { get; set; }

    public bool CompressEmptyLines { get; set; } = true;

    // Cursor blinking
    public bool CursorVisible { get; set; } = true;

    // Selection (in absolute row coordinates — tracks content across scrolling)
    public (long Row, int Col)? SelectionStart { get; set; }
    public (long Row, int Col)? SelectionEnd { get; set; }

    // Search highlights (absolute row coordinates, like selection)
    public List<SearchMatch>? SearchMatches { get; set; }
    public SearchMatch? CurrentSearchMatch { get; set; }

    private static readonly Brush DefaultBg = new SolidColorBrush(Color.FromRgb(CellData.DefaultBgR, CellData.DefaultBgG, CellData.DefaultBgB));
    private static readonly Brush CursorBrush = new SolidColorBrush(Color.FromRgb(CellData.DefaultFgR, CellData.DefaultFgG, CellData.DefaultFgB));
    private static readonly Brush CursorBlockBrush = new SolidColorBrush(Color.FromArgb(0xA0, CellData.DefaultFgR, CellData.DefaultFgG, CellData.DefaultFgB));
    private static readonly Brush SelectionBrush;
    private static readonly Brush SearchHighlightBrush;
    private static readonly Brush CurrentSearchHighlightBrush;

    static TerminalCanvas()
    {
        DefaultBg.Freeze();
        CursorBrush.Freeze();
        CursorBlockBrush.Freeze();
        var sel = new SolidColorBrush(Color.FromArgb(0x60, 0x26, 0x4F, 0x78));
        sel.Freeze();
        SelectionBrush = sel;
        var searchHl = new SolidColorBrush(Color.FromArgb(0x80, 0xAA, 0x88, 0x00));
        searchHl.Freeze();
        SearchHighlightBrush = searchHl;
        var currentSearchHl = new SolidColorBrush(Color.FromArgb(0xB0, 0xFF, 0xAA, 0x00));
        currentSearchHl.Freeze();
        CurrentSearchHighlightBrush = currentSearchHl;
    }

    public TerminalCanvas()
    {
        Focusable = true;
        SnapsToDevicePixels = true;
        UseLayoutRounding = true;
        ClipToBounds = true;
        EnsureMeasured();
    }

    /// <summary>
    /// WPF does not re-run OnRender on layout size changes by default; without
    /// this, dragging a GridSplitter reveals unpainted area behind the canvas
    /// (black from the dock container) until the next output-driven repaint.
    /// </summary>
    protected override void OnRenderSizeChanged(SizeChangedInfo sizeInfo)
    {
        base.OnRenderSizeChanged(sizeInfo);
        InvalidateVisual();
    }

    private void EnsureMeasured()
    {
        if (_measured) return;
        var ft = new FormattedText("M", CultureInfo.InvariantCulture,
            FlowDirection.LeftToRight, _typeface, FontSize,
            Brushes.White, new NumberSubstitution(), TextFormattingMode.Display, Dpi);
        // Round to whole pixels to prevent sub-pixel gaps between cells
        _cellWidth = Math.Round(ft.WidthIncludingTrailingWhitespace);
        _cellHeight = Math.Round(ft.Height);
        _baseline = ft.Baseline;
        _measured = true;
    }

    protected override void OnRender(DrawingContext dc)
    {
        EnsureMeasured();

        // Cap caches to prevent unbounded growth
        if (_glyphCache.Count > 10_000) _glyphCache.Clear();
        if (_brushCache.Count > 10_000) _brushCache.Clear();
        if (_penCache.Count > 10_000) _penCache.Clear();

        // Background
        dc.DrawRectangle(DefaultBg, null, new Rect(0, 0, ActualWidth, ActualHeight));

        var buffer = Emulator?.Buffer;
        if (buffer == null) return;

        bool topAnchor = Viewport != null && !Viewport.IsLive;

        int scrollOffset = Viewport?.ScrollOffset ?? 0;

        // Normalize selection range
        var (selStartRow, selStartCol, selEndRow, selEndCol) = GetNormalizedSelection();

        int baseRowCount = ViewportCalculator.BaseRowCount(buffer.Rows, Rows);
        int viewOffset = ViewportCalculator.ViewOffset(buffer.Rows, Rows);

        int displayCursorRow = ViewportCalculator.DisplayCursorRow(buffer.CursorRow, viewOffset);
        if (!(Emulator?.CursorEnabled ?? true))
            displayCursorRow = FindVisualCursorRow(buffer, baseRowCount, scrollOffset);

        int extraRows = 0;
        int displayedBaseRows = baseRowCount;
        double[] rowYPositions;

        bool compress = CompressEmptyLines && !(Emulator?.AlternateScreen ?? false);

        if (compress)
        {
            var baseIsEmpty = new bool[baseRowCount];
            int lastMeaningful = -1;
            for (int row = 0; row < baseRowCount; row++)
            {
                baseIsEmpty[row] = IsRowEmpty(buffer, row, scrollOffset, baseRowCount);
                if (!baseIsEmpty[row]) lastMeaningful = row;
            }
            if (scrollOffset == 0 && displayCursorRow > lastMeaningful && displayCursorRow < baseRowCount)
                lastMeaningful = displayCursorRow;

            if (lastMeaningful >= 0) displayedBaseRows = lastMeaningful + 1;
            var trimmedEmpty = displayedBaseRows == baseRowCount
                ? baseIsEmpty
                : baseIsEmpty[..displayedBaseRows];

            var basePositions = RowLayoutCalculator.ComputeRowYPositions(
                trimmedEmpty, displayCursorRow, _cellHeight, EmptyRowScale);

            {
                double savedPixels = ActualHeight - basePositions[displayedBaseRows];
                extraRows = ViewportCalculator.ExtraRowsFromCompression(
                    savedPixels, _cellHeight, EmptyRowScale,
                    buffer.ScrollbackCount, viewOffset, scrollOffset);
            }

            if (extraRows == 0 && (topAnchor || displayedBaseRows < baseRowCount))
            {
                rowYPositions = basePositions;
            }
            else
            {
                int totalCandidateRows = displayedBaseRows + extraRows;
                var allIsEmpty = new bool[totalCandidateRows];
                for (int row = 0; row < totalCandidateRows; row++)
                    allIsEmpty[row] = IsRowEmptyForDisplay(buffer, row, extraRows, scrollOffset, baseRowCount);

                int adjustedCursorRow = displayCursorRow + extraRows;
                if (topAnchor)
                {
                    rowYPositions = RowLayoutCalculator.ComputeRowYPositions(
                        allIsEmpty, adjustedCursorRow, _cellHeight, EmptyRowScale);
                }
                else
                {
                    rowYPositions = RowLayoutCalculator.ComputeLayout(
                        allIsEmpty, adjustedCursorRow, _cellHeight, EmptyRowScale, ActualHeight);
                }
            }
        }
        else
        {
            rowYPositions = RowLayoutCalculator.ComputeRowYPositions(
                new bool[baseRowCount], displayCursorRow, _cellHeight, EmptyRowScale);
        }

        int totalRows = displayedBaseRows + extraRows;
        _rowYPositions = rowYPositions;
        _extraRows = extraRows;
        _baseRowCount = baseRowCount;

        long absRowBase = ViewportCalculator.AbsoluteRowBase(buffer.TotalLinesScrolled, viewOffset, scrollOffset, extraRows);
        _absRowBase = absRowBase;

        for (int row = 0; row < totalRows; row++)
        {
            double y = rowYPositions[row];
            double rowH = rowYPositions[row + 1] - y;

            if (rowH <= 0 || y + rowH <= 0) continue;

            for (int col = 0; col < buffer.Columns && col < Columns; col++)
            {
                var cell = GetDisplayCell(buffer, row, col, extraRows, scrollOffset, baseRowCount);
                double x = col * _cellWidth;

                // Compute effective fg/bg (swap if reverse)
                byte effFgR = cell.ForegroundR, effFgG = cell.ForegroundG, effFgB = cell.ForegroundB;
                byte effBgR = cell.BackgroundR, effBgG = cell.BackgroundG, effBgB = cell.BackgroundB;
                if (cell.Reverse)
                {
                    (effFgR, effFgG, effFgB, effBgR, effBgG, effBgB) =
                        (effBgR, effBgG, effBgB, effFgR, effFgG, effFgB);
                }

                // Apply dim: halve foreground intensity
                if (cell.Dim)
                {
                    effFgR = (byte)(effFgR / 2);
                    effFgG = (byte)(effFgG / 2);
                    effFgB = (byte)(effFgB / 2);
                }

                // Draw cell background if not default
                if (effBgR != CellData.DefaultBgR || effBgG != CellData.DefaultBgG || effBgB != CellData.DefaultBgB)
                {
                    var bgBrush = GetCachedBrush(_brushCache, effBgR, effBgG, effBgB);
                    dc.DrawRectangle(bgBrush, null, new Rect(x, y, _cellWidth, rowH));
                }

                // Draw selection highlight (selection is in absolute-space)
                long absRow = absRowBase + row;
                if (IsInSelection(absRow, col, selStartRow, selStartCol, selEndRow, selEndCol))
                {
                    dc.DrawRectangle(SelectionBrush, null, new Rect(x, y, _cellWidth, rowH));
                }

                // Draw search highlights
                if (SearchMatches != null)
                {
                    foreach (var match in SearchMatches)
                    {
                        if (match.AbsoluteRow != absRow) continue;
                        if (col >= match.StartCol && col < match.StartCol + match.Length)
                        {
                            bool isCurrent = CurrentSearchMatch.HasValue &&
                                             CurrentSearchMatch.Value.AbsoluteRow == match.AbsoluteRow &&
                                             CurrentSearchMatch.Value.StartCol == match.StartCol;
                            dc.DrawRectangle(isCurrent ? CurrentSearchHighlightBrush : SearchHighlightBrush,
                                null, new Rect(x, y, _cellWidth, rowH));
                            break; // only one match can cover this cell
                        }
                    }
                }

                // Draw character
                if (cell.Character != ' ' && cell.Character != '\0')
                {
                    var fgBrush = GetCachedBrush(_brushCache, effFgR, effFgG, effFgB);

                    // Render block drawing characters as geometric primitives
                    // for pixel-perfect tiling (font glyphs often have gaps)
                    if (!TryDrawBlockChar(dc, cell.Character, fgBrush, x, y, _cellWidth, rowH))
                    {
                        byte tfIdx = (byte)((cell.Bold ? 1 : 0) | (cell.Italic ? 2 : 0));
                        var glyphKey = (cell.Character, tfIdx, effFgR, effFgG, effFgB);

                        if (!_glyphCache.TryGetValue(glyphKey, out var ft))
                        {
                            var typeface = tfIdx switch
                            {
                                3 => _typefaceBoldItalic,
                                1 => _typefaceBold,
                                2 => _typefaceItalic,
                                _ => _typeface
                            };
                            ft = new FormattedText(cell.Character.ToString(),
                                CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
                                typeface, FontSize, fgBrush,
                                new NumberSubstitution(), TextFormattingMode.Display, Dpi);
                            _glyphCache[glyphKey] = ft;
                        }

                        dc.DrawText(ft, new Point(x, y));
                    }

                    if (cell.Underline)
                    {
                        var pen = GetCachedPen(_penCache, effFgR, effFgG, effFgB);
                        dc.DrawLine(pen,
                            new Point(x, y + rowH - 1),
                            new Point(x + _cellWidth, y + rowH - 1));
                    }

                    if (cell.Strikethrough)
                    {
                        var pen = GetCachedPen(_penCache, effFgR, effFgG, effFgB);
                        dc.DrawLine(pen,
                            new Point(x, y + rowH / 2),
                            new Point(x + _cellWidth, y + rowH / 2));
                    }
                }
            }
        }

        // Draw cursor only when the application hasn't hidden it via DECTCEM (CSI ?25l).
        // TUI apps like Claude Code hide the terminal cursor and render their own as
        // a character in the buffer, so we must respect CursorEnabled to avoid drawing
        // a stray cursor at ConPTY's internal cursor position.
        bool cursorEnabled = Emulator?.CursorEnabled ?? true;
        int cursorDisplayRow = displayCursorRow + extraRows;
        if (CursorVisible && cursorEnabled && scrollOffset == 0
            && cursorDisplayRow >= 0 && cursorDisplayRow < totalRows
            && buffer.CursorCol < Columns)
        {
            double cx = buffer.CursorCol * _cellWidth;
            double cy = rowYPositions[cursorDisplayRow];
            double cursorH = rowYPositions[cursorDisplayRow + 1] - cy;

            dc.DrawRectangle(CursorBlockBrush, null, new Rect(cx, cy, _cellWidth, cursorH));
        }
    }


    /// <summary>
    /// Finds the visual cursor row when the terminal cursor is hidden (TUI apps).
    /// TUI apps render their cursor as a reverse-video cell. Scan from the bottom
    /// for the last row that has a reverse-video cell on an otherwise empty row.
    /// Returns -1 if not found.
    /// </summary>
    private static int FindVisualCursorRow(TerminalBuffer buffer, int rowCount, int scrollOffset)
    {
        for (int row = rowCount - 1; row >= 0; row--)
        {
            bool hasReverse = false;
            bool hasContent = false;
            int cols = buffer.Columns;
            for (int c = 0; c < cols; c++)
            {
                var cell = buffer.GetVisibleCell(row, c, scrollOffset, rowCount);
                if (cell.Reverse)
                    hasReverse = true;
                else if (cell.Character != ' ' && cell.Character != '\0' && cell.Character != '\u2502')
                    hasContent = true;
            }
            if (hasReverse && !hasContent)
                return row;
        }
        return -1;
    }

    /// <summary>
    /// Gets a cell for display rendering, accounting for extra scrollback rows.
    /// The combined view (extras + base) has (baseRowCount + extraRows) rows anchored
    /// to the bottom of the buffer's live screen at scrollOffset = 0.
    /// </summary>
    private static CellData GetDisplayCell(TerminalBuffer buffer, int displayRow, int col, int extraRows, int scrollOffset, int baseRowCount)
        => buffer.GetVisibleCell(displayRow, col, scrollOffset, baseRowCount + extraRows);

    /// <summary>
    /// Checks if a display row is empty, accounting for extra scrollback rows.
    /// </summary>
    private bool IsRowEmptyForDisplay(TerminalBuffer buffer, int displayRow, int extraRows, int scrollOffset, int baseRowCount)
    {
        int cols = buffer.Columns;
        for (int c = 0; c < cols; c++)
        {
            var cell = GetDisplayCell(buffer, displayRow, c, extraRows, scrollOffset, baseRowCount);
            if (cell.Character != ' ' && cell.Character != '\0' && cell.Character != '\u2502')
                return false;
            if (cell.BackgroundR != CellData.DefaultBgR || cell.BackgroundG != CellData.DefaultBgG || cell.BackgroundB != CellData.DefaultBgB)
                return false;
        }
        return true;
    }

    private static bool IsRowEmpty(TerminalBuffer buffer, int row, int scrollOffset, int baseRowCount)
    {
        int cols = buffer.Columns;
        for (int c = 0; c < cols; c++)
        {
            var cell = buffer.GetVisibleCell(row, c, scrollOffset, baseRowCount);
            if (cell.Character != ' ' && cell.Character != '\0' && cell.Character != '\u2502')
                return false;
            if (cell.BackgroundR != CellData.DefaultBgR || cell.BackgroundG != CellData.DefaultBgG || cell.BackgroundB != CellData.DefaultBgB)
                return false;
        }
        return true;
    }

    private static Brush GetCachedBrush(Dictionary<(byte, byte, byte), Brush> cache, byte r, byte g, byte b)
    {
        var key = (r, g, b);
        if (!cache.TryGetValue(key, out var brush))
        {
            brush = new SolidColorBrush(Color.FromRgb(r, g, b));
            brush.Freeze();
            cache[key] = brush;
        }
        return brush;
    }

    private static Pen GetCachedPen(Dictionary<(byte, byte, byte), Pen> cache, byte r, byte g, byte b)
    {
        var key = (r, g, b);
        if (!cache.TryGetValue(key, out var pen))
        {
            var brush = new SolidColorBrush(Color.FromRgb(r, g, b));
            brush.Freeze();
            pen = new Pen(brush, 1);
            pen.Freeze();
            cache[key] = pen;
        }
        return pen;
    }

    /// <summary>
    /// Converts a pixel position to absolute (row, col).
    /// Row is an absolute row index that tracks content across scrolling.
    /// </summary>
    public (long Row, int Col) HitTest(Point position)
    {
        EnsureMeasured();
        int col = Math.Clamp((int)(position.X / _cellWidth), 0, Math.Max(0, Columns - 1));

        int displayRow = 0;
        if (_rowYPositions != null && _rowYPositions.Length > 1)
        {
            // Binary search through variable row positions
            for (int r = 0; r < _rowYPositions.Length - 1; r++)
            {
                if (position.Y < _rowYPositions[r + 1])
                {
                    displayRow = r;
                    break;
                }
                displayRow = r;
            }
        }
        else
        {
            displayRow = (int)(position.Y / _cellHeight);
        }

        // Clamp displayRow to the range of rows from the last render
        int totalDisplayRows = (_rowYPositions?.Length ?? 1) - 1;
        displayRow = Math.Clamp(displayRow, 0, Math.Max(0, totalDisplayRows - 1));

        long absRow = _absRowBase + displayRow;
        return (absRow, col);
    }

    /// <summary>
    /// Gets the selected text from the buffer.
    /// </summary>
    public string GetSelectedText()
    {
        if (SelectionStart == null || SelectionEnd == null || Emulator?.Buffer == null)
            return "";

        var buffer = Emulator.Buffer;
        var (sr, sc, er, ec) = GetNormalizedSelection();
        if (sr < 0) return "";

        var sb = new System.Text.StringBuilder();
        for (long r = sr; r <= er; r++)
        {
            int screenRow = (int)(r - buffer.TotalLinesScrolled);
            int cs = r == sr ? sc : 0;
            int ce = r == er ? ec : buffer.Columns - 1;
            for (int c = cs; c <= ce && c < buffer.Columns; c++)
            {
                CellData cell;
                if (screenRow >= 0 && screenRow < buffer.Rows)
                {
                    cell = buffer.GetCell(screenRow, c);
                }
                else
                {
                    int sbIndex = buffer.ScrollbackCount + screenRow;
                    if (sbIndex >= 0 && sbIndex < buffer.ScrollbackCount)
                    {
                        var line = buffer.GetScrollbackLine(sbIndex);
                        cell = c < line.Length ? line[c] : CellData.Empty;
                    }
                    else
                        continue;
                }
                sb.Append(cell.Character == '\0' ? ' ' : cell.Character);
            }
            // Add line break between rows, but not for soft-wrapped continuations
            if (r < er)
            {
                // Check if the next row is a soft-wrapped continuation
                long nextR = r + 1;
                int nextScreenRow = (int)(nextR - buffer.TotalLinesScrolled);
                bool nextIsWrapped;
                if (nextScreenRow >= 0 && nextScreenRow < buffer.Rows)
                    nextIsWrapped = buffer.IsScreenLineWrapped(nextScreenRow);
                else
                {
                    int nextSbIndex = buffer.ScrollbackCount + nextScreenRow;
                    nextIsWrapped = nextSbIndex >= 0 && nextSbIndex < buffer.ScrollbackCount
                        && buffer.IsScrollbackLineWrapped(nextSbIndex);
                }

                if (!nextIsWrapped)
                {
                    // Hard line break: trim trailing spaces and add newline
                    while (sb.Length > 0 && sb[sb.Length - 1] == ' ')
                        sb.Length--;
                    sb.AppendLine();
                }
            }
        }
        return sb.ToString().TrimEnd();
    }

    private (long StartRow, int StartCol, long EndRow, int EndCol) GetNormalizedSelection()
    {
        if (SelectionStart == null || SelectionEnd == null)
            return (-1, -1, -1, -1);

        var s = SelectionStart.Value;
        var e = SelectionEnd.Value;

        if (s.Row > e.Row || (s.Row == e.Row && s.Col > e.Col))
            (s, e) = (e, s);

        return (s.Row, s.Col, e.Row, e.Col);
    }

    private static bool IsInSelection(long row, int col, long sr, int sc, long er, int ec)
    {
        if (sr < 0) return false;
        if (row < sr || row > er) return false;
        if (row == sr && row == er) return col >= sc && col <= ec;
        if (row == sr) return col >= sc;
        if (row == er) return col <= ec;
        return true;
    }

    public void Invalidate() => InvalidateVisual();
}
