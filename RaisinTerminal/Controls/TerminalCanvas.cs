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
public class TerminalCanvas : FrameworkElement
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
    private double[]? _rowYPositions;

    // Number of extra scrollback rows shown above the normal viewport
    // due to empty line compression. Used by HitTest and cursor positioning.
    private int _extraRows;

    // Glyph cache: avoids allocating a new FormattedText per cell per frame.
    // Key: (character, typeface index 0-3, fg color).
    private readonly Dictionary<(char, byte, byte, byte, byte), FormattedText> _glyphCache = new();

    public int Columns => _cellWidth > 0 ? (int)(ActualWidth / _cellWidth) : 0;
    public int Rows => _cellHeight > 0 ? (int)(ActualHeight / _cellHeight) : 0;

    public double CellWidth => _cellWidth;
    public double CellHeight => _cellHeight;

    public TerminalEmulator? Emulator { get; set; }
    public bool CompressEmptyLines { get; set; } = true;

    // Cursor blinking
    public bool CursorVisible { get; set; } = true;

    // Selection (in absolute row coordinates — tracks content across scrolling)
    public (long Row, int Col)? SelectionStart { get; set; }
    public (long Row, int Col)? SelectionEnd { get; set; }

    private static readonly Brush DefaultBg = new SolidColorBrush(Color.FromRgb(0x21, 0x21, 0x21));
    private static readonly Brush CursorBrush = new SolidColorBrush(Color.FromRgb(0xCC, 0xCC, 0xCC));
    private static readonly Brush SelectionBrush;

    static TerminalCanvas()
    {
        DefaultBg.Freeze();
        CursorBrush.Freeze();
        var sel = new SolidColorBrush(Color.FromArgb(0x60, 0x26, 0x4F, 0x78));
        sel.Freeze();
        SelectionBrush = sel;
    }

    public TerminalCanvas()
    {
        Focusable = true;
        SnapsToDevicePixels = true;
        UseLayoutRounding = true;
        ClipToBounds = true;
        EnsureMeasured();
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

        // Background
        dc.DrawRectangle(DefaultBg, null, new Rect(0, 0, ActualWidth, ActualHeight));

        var buffer = Emulator?.Buffer;
        if (buffer == null) return;

        // Normalize selection range
        var (selStartRow, selStartCol, selEndRow, selEndCol) = GetNormalizedSelection();

        // Brush cache to avoid per-cell allocations
        var brushCache = new Dictionary<(byte, byte, byte), Brush>();

        int baseRowCount = Math.Min(buffer.Rows, Rows);

        // Build per-row emptiness flags and find cursor row.
        int cursorRow = buffer.CursorRow;
        if (!(Emulator?.CursorEnabled ?? true))
            cursorRow = FindVisualCursorRow(buffer, baseRowCount);

        int extraRows = 0;
        double[] rowYPositions;

        if (buffer.ScrollOffset > 0 && CompressEmptyLines)
        {
            // Scrolled back: bottom-up layout with extra scrollback rows
            // to fill the canvas. Scrollback content is stable, no flickering.
            var baseIsEmpty = new bool[baseRowCount];
            for (int row = 0; row < baseRowCount; row++)
                baseIsEmpty[row] = IsRowEmpty(buffer, row);

            var basePositions = RowLayoutCalculator.ComputeRowYPositions(
                baseIsEmpty, cursorRow, _cellHeight, EmptyRowScale);
            double savedPixels = ActualHeight - basePositions[baseRowCount];

            if (savedPixels > _cellHeight)
            {
                int availableScrollback = buffer.ScrollbackCount - buffer.ScrollOffset;
                int maxExtra = (int)Math.Ceiling(savedPixels / (Math.Round(_cellHeight * EmptyRowScale)));
                extraRows = Math.Min(maxExtra, Math.Max(0, availableScrollback));
            }

            int totalCandidateRows = baseRowCount + extraRows;
            var allIsEmpty = new bool[totalCandidateRows];
            for (int row = 0; row < totalCandidateRows; row++)
                allIsEmpty[row] = IsRowEmptyForDisplay(buffer, row, extraRows);

            int adjustedCursorRow = cursorRow + extraRows;
            rowYPositions = RowLayoutCalculator.ComputeLayout(
                allIsEmpty, adjustedCursorRow, _cellHeight, EmptyRowScale, ActualHeight);
        }
        else
        {
            // Live bottom (or compression off): top-down layout matching the
            // original behaviour — content starts at y=0, gap (if any) at bottom.
            var rowIsEmpty = new bool[baseRowCount];
            if (CompressEmptyLines)
            {
                for (int row = 0; row < baseRowCount; row++)
                    rowIsEmpty[row] = IsRowEmpty(buffer, row);
            }

            rowYPositions = RowLayoutCalculator.ComputeRowYPositions(
                rowIsEmpty, cursorRow, _cellHeight, EmptyRowScale);
        }

        int totalRows = baseRowCount + extraRows;
        _rowYPositions = rowYPositions;
        _extraRows = extraRows;

        // Base for converting display rows to absolute rows
        long absRowBase = buffer.TotalLinesScrolled - buffer.ScrollOffset - extraRows;

        for (int row = 0; row < totalRows; row++)
        {
            double y = rowYPositions[row];
            double rowH = rowYPositions[row + 1] - y;

            // Skip rows fully above the canvas (clipped)
            if (rowH <= 0 || y + rowH <= 0) continue;

            for (int col = 0; col < buffer.Columns && col < Columns; col++)
            {
                var cell = GetDisplayCell(buffer, row, col, extraRows);
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
                if (effBgR != 33 || effBgG != 33 || effBgB != 33)
                {
                    var bgBrush = GetCachedBrush(brushCache, effBgR, effBgG, effBgB);
                    dc.DrawRectangle(bgBrush, null, new Rect(x, y, _cellWidth, rowH));
                }

                // Draw selection highlight (selection is in absolute-space)
                long absRow = absRowBase + row;
                if (IsInSelection(absRow, col, selStartRow, selStartCol, selEndRow, selEndCol))
                {
                    dc.DrawRectangle(SelectionBrush, null, new Rect(x, y, _cellWidth, rowH));
                }

                // Draw character
                if (cell.Character != ' ' && cell.Character != '\0')
                {
                    var fgBrush = GetCachedBrush(brushCache, effFgR, effFgG, effFgB);

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
                        dc.DrawLine(new Pen(fgBrush, 1),
                            new Point(x, y + rowH - 1),
                            new Point(x + _cellWidth, y + rowH - 1));
                    }

                    if (cell.Strikethrough)
                    {
                        dc.DrawLine(new Pen(fgBrush, 1),
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
        int displayCursorRow = buffer.CursorRow + extraRows;
        if (CursorVisible && cursorEnabled && buffer.ScrollOffset == 0 && displayCursorRow < totalRows && buffer.CursorCol < Columns)
        {
            double cx = buffer.CursorCol * _cellWidth;
            double cy = rowYPositions[displayCursorRow];
            double cursorH = rowYPositions[displayCursorRow + 1] - cy;

            // Draw a block cursor with semi-transparent overlay
            var cursorBlock = new SolidColorBrush(Color.FromArgb(0xA0, 0xCC, 0xCC, 0xCC));
            cursorBlock.Freeze();
            dc.DrawRectangle(cursorBlock, null, new Rect(cx, cy, _cellWidth, cursorH));
        }
    }

    /// <summary>
    /// Renders Unicode block drawing characters (U+2580–U+259F) and shade characters
    /// as filled rectangles for pixel-perfect rendering. Returns false if the character
    /// is not a block element.
    /// </summary>
    private static bool TryDrawBlockChar(DrawingContext dc, char ch, Brush brush, double x, double y, double w, double h)
    {
        switch (ch)
        {
            case '\u2580': // ▀ UPPER HALF BLOCK
                dc.DrawRectangle(brush, null, new Rect(x, y, w, h / 2));
                return true;
            case '\u2581': // ▁ LOWER ONE EIGHTH BLOCK
                dc.DrawRectangle(brush, null, new Rect(x, y + h * 7 / 8, w, h / 8));
                return true;
            case '\u2582': // ▂ LOWER ONE QUARTER BLOCK
                dc.DrawRectangle(brush, null, new Rect(x, y + h * 3 / 4, w, h / 4));
                return true;
            case '\u2583': // ▃ LOWER THREE EIGHTHS BLOCK
                dc.DrawRectangle(brush, null, new Rect(x, y + h * 5 / 8, w, h * 3 / 8));
                return true;
            case '\u2584': // ▄ LOWER HALF BLOCK
                dc.DrawRectangle(brush, null, new Rect(x, y + h / 2, w, h / 2));
                return true;
            case '\u2585': // ▅ LOWER FIVE EIGHTHS BLOCK
                dc.DrawRectangle(brush, null, new Rect(x, y + h * 3 / 8, w, h * 5 / 8));
                return true;
            case '\u2586': // ▆ LOWER THREE QUARTERS BLOCK
                dc.DrawRectangle(brush, null, new Rect(x, y + h / 4, w, h * 3 / 4));
                return true;
            case '\u2587': // ▇ LOWER SEVEN EIGHTHS BLOCK
                dc.DrawRectangle(brush, null, new Rect(x, y + h / 8, w, h * 7 / 8));
                return true;
            case '\u2588': // █ FULL BLOCK
                dc.DrawRectangle(brush, null, new Rect(x, y, w, h));
                return true;
            case '\u2589': // ▉ LEFT SEVEN EIGHTHS BLOCK
                dc.DrawRectangle(brush, null, new Rect(x, y, w * 7 / 8, h));
                return true;
            case '\u258A': // ▊ LEFT THREE QUARTERS BLOCK
                dc.DrawRectangle(brush, null, new Rect(x, y, w * 3 / 4, h));
                return true;
            case '\u258B': // ▋ LEFT FIVE EIGHTHS BLOCK
                dc.DrawRectangle(brush, null, new Rect(x, y, w * 5 / 8, h));
                return true;
            case '\u258C': // ▌ LEFT HALF BLOCK
                dc.DrawRectangle(brush, null, new Rect(x, y, w / 2, h));
                return true;
            case '\u258D': // ▍ LEFT THREE EIGHTHS BLOCK
                dc.DrawRectangle(brush, null, new Rect(x, y, w * 3 / 8, h));
                return true;
            case '\u258E': // ▎ LEFT ONE QUARTER BLOCK
                dc.DrawRectangle(brush, null, new Rect(x, y, w / 4, h));
                return true;
            case '\u258F': // ▏ LEFT ONE EIGHTH BLOCK
                dc.DrawRectangle(brush, null, new Rect(x, y, w / 8, h));
                return true;
            case '\u2590': // ▐ RIGHT HALF BLOCK
                dc.DrawRectangle(brush, null, new Rect(x + w / 2, y, w / 2, h));
                return true;
            case '\u2591': // ░ LIGHT SHADE (25%)
                dc.PushOpacity(0.25);
                dc.DrawRectangle(brush, null, new Rect(x, y, w, h));
                dc.Pop();
                return true;
            case '\u2592': // ▒ MEDIUM SHADE (50%)
                dc.PushOpacity(0.5);
                dc.DrawRectangle(brush, null, new Rect(x, y, w, h));
                dc.Pop();
                return true;
            case '\u2593': // ▓ DARK SHADE (75%)
                dc.PushOpacity(0.75);
                dc.DrawRectangle(brush, null, new Rect(x, y, w, h));
                dc.Pop();
                return true;
            case '\u2594': // ▔ UPPER ONE EIGHTH BLOCK
                dc.DrawRectangle(brush, null, new Rect(x, y, w, h / 8));
                return true;
            case '\u2595': // ▕ RIGHT ONE EIGHTH BLOCK
                dc.DrawRectangle(brush, null, new Rect(x + w * 7 / 8, y, w / 8, h));
                return true;
            case '\u2596': // ▖ QUADRANT LOWER LEFT
                dc.DrawRectangle(brush, null, new Rect(x, y + h / 2, w / 2, h / 2));
                return true;
            case '\u2597': // ▗ QUADRANT LOWER RIGHT
                dc.DrawRectangle(brush, null, new Rect(x + w / 2, y + h / 2, w / 2, h / 2));
                return true;
            case '\u2598': // ▘ QUADRANT UPPER LEFT
                dc.DrawRectangle(brush, null, new Rect(x, y, w / 2, h / 2));
                return true;
            case '\u2599': // ▙ QUADRANT UPPER LEFT AND LOWER LEFT AND LOWER RIGHT
                dc.DrawRectangle(brush, null, new Rect(x, y, w / 2, h)); // left full
                dc.DrawRectangle(brush, null, new Rect(x + w / 2, y + h / 2, w / 2, h / 2)); // lower right
                return true;
            case '\u259A': // ▚ QUADRANT UPPER LEFT AND LOWER RIGHT
                dc.DrawRectangle(brush, null, new Rect(x, y, w / 2, h / 2));
                dc.DrawRectangle(brush, null, new Rect(x + w / 2, y + h / 2, w / 2, h / 2));
                return true;
            case '\u259B': // ▛ QUADRANT UPPER LEFT AND UPPER RIGHT AND LOWER LEFT
                dc.DrawRectangle(brush, null, new Rect(x, y, w, h / 2)); // top full
                dc.DrawRectangle(brush, null, new Rect(x, y + h / 2, w / 2, h / 2)); // lower left
                return true;
            case '\u259C': // ▜ QUADRANT UPPER LEFT AND UPPER RIGHT AND LOWER RIGHT
                dc.DrawRectangle(brush, null, new Rect(x, y, w, h / 2)); // top full
                dc.DrawRectangle(brush, null, new Rect(x + w / 2, y + h / 2, w / 2, h / 2)); // lower right
                return true;
            case '\u259D': // ▝ QUADRANT UPPER RIGHT
                dc.DrawRectangle(brush, null, new Rect(x + w / 2, y, w / 2, h / 2));
                return true;
            case '\u259E': // ▞ QUADRANT UPPER RIGHT AND LOWER LEFT
                dc.DrawRectangle(brush, null, new Rect(x + w / 2, y, w / 2, h / 2));
                dc.DrawRectangle(brush, null, new Rect(x, y + h / 2, w / 2, h / 2));
                return true;
            case '\u259F': // ▟ QUADRANT UPPER RIGHT AND LOWER LEFT AND LOWER RIGHT
                dc.DrawRectangle(brush, null, new Rect(x + w / 2, y, w / 2, h)); // right full
                dc.DrawRectangle(brush, null, new Rect(x, y + h / 2, w / 2, h / 2)); // lower left
                return true;
            // Box Drawing: light horizontal line
            case '\u2500': // ─
            case '\u2574': // ╴ left half
            case '\u2576': // ╶ right half
            {
                double cy = Math.Round(y + h / 2) + 0.5; // snap to pixel center for crisp 1px line
                var pen = new Pen(brush, 1);
                pen.Freeze();
                double left = (ch == '\u2576') ? x + w / 2 : x;
                double right = (ch == '\u2574') ? x + w / 2 : x + w;
                dc.DrawLine(pen, new Point(left, cy), new Point(right, cy));
                return true;
            }
            // Box Drawing: light vertical line
            case '\u2502': // │
            {
                double cx = Math.Round(x + w / 2) + 0.5; // snap to pixel center for crisp 1px line
                var pen = new Pen(brush, 1);
                pen.Freeze();
                dc.DrawLine(pen, new Point(cx, y), new Point(cx, y + h));
                return true;
            }
            default:
                return false;
        }
    }

    /// <summary>
    /// Finds the visual cursor row when the terminal cursor is hidden (TUI apps).
    /// TUI apps render their cursor as a reverse-video cell. Scan from the bottom
    /// for the last row that has a reverse-video cell on an otherwise empty row.
    /// Returns -1 if not found.
    /// </summary>
    private static int FindVisualCursorRow(TerminalBuffer buffer, int rowCount)
    {
        for (int row = rowCount - 1; row >= 0; row--)
        {
            bool hasReverse = false;
            bool hasContent = false;
            int cols = buffer.Columns;
            for (int c = 0; c < cols; c++)
            {
                var cell = buffer.GetVisibleCell(row, c);
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
    /// Rows 0..extraRows-1 are extra scrollback; extraRows..total-1 are normal buffer rows.
    /// </summary>
    private static CellData GetDisplayCell(TerminalBuffer buffer, int displayRow, int col, int extraRows)
    {
        if (displayRow < extraRows)
        {
            int sbIndex = buffer.ScrollbackCount - buffer.ScrollOffset - extraRows + displayRow;
            if (sbIndex < 0 || sbIndex >= buffer.ScrollbackCount)
                return CellData.Empty;
            var line = buffer.GetScrollbackLine(sbIndex);
            return col < line.Length ? line[col] : CellData.Empty;
        }
        return buffer.GetVisibleCell(displayRow - extraRows, col);
    }

    /// <summary>
    /// Checks if a display row is empty, accounting for extra scrollback rows.
    /// </summary>
    private bool IsRowEmptyForDisplay(TerminalBuffer buffer, int displayRow, int extraRows)
    {
        int cols = buffer.Columns;
        for (int c = 0; c < cols; c++)
        {
            var cell = GetDisplayCell(buffer, displayRow, c, extraRows);
            if (cell.Character != ' ' && cell.Character != '\0' && cell.Character != '\u2502')
                return false;
            if (cell.BackgroundR != 33 || cell.BackgroundG != 33 || cell.BackgroundB != 33)
                return false;
        }
        return true;
    }

    private static bool IsRowEmpty(TerminalBuffer buffer, int row)
    {
        int cols = buffer.Columns;
        for (int c = 0; c < cols; c++)
        {
            var cell = buffer.GetVisibleCell(row, c);
            if (cell.Character != ' ' && cell.Character != '\0' && cell.Character != '\u2502')
                return false;
            if (cell.BackgroundR != 33 || cell.BackgroundG != 33 || cell.BackgroundB != 33)
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

        // Convert from display-space to buffer-space, then to absolute
        int bufferRow = Math.Clamp(displayRow - _extraRows, 0, Math.Max(0, Rows - 1));
        var buffer = Emulator?.Buffer;
        long absRow = buffer != null
            ? buffer.TotalLinesScrolled - buffer.ScrollOffset + bufferRow
            : bufferRow;
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
            // Trim trailing spaces on each line except the last
            if (r < er)
            {
                while (sb.Length > 0 && sb[sb.Length - 1] == ' ')
                    sb.Length--;
                sb.AppendLine();
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
