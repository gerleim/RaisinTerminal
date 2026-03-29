using RaisinTerminal.Core.Collections;
using RaisinTerminal.Core.Models;

namespace RaisinTerminal.Core.Terminal;

/// <summary>
/// Character grid + scrollback buffer for terminal emulation.
/// </summary>
public class TerminalBuffer
{
    private CircularBuffer<CellData[]> _scrollback;
    private CellData[,] _screen;

    public int Columns { get; private set; }
    public int Rows { get; private set; }
    public int CursorRow { get; set; }
    public int CursorCol { get; set; }
    public int ScrollbackCount => _scrollback.Count;
    public int MaxScrollback { get; set; } = 10_000;

    /// <summary>
    /// Number of lines scrolled back from bottom (0 = live/at bottom).
    /// </summary>
    public int ScrollOffset { get; set; }

    /// <summary>
    /// Monotonically increasing count of lines scrolled into scrollback.
    /// Used for absolute row addressing (e.g., selection tracking).
    /// </summary>
    public long TotalLinesScrolled { get; private set; }

    /// <summary>
    /// When true, lines scrolled off the top are discarded instead of added to scrollback.
    /// Set during alternate screen mode.
    /// </summary>
    public bool SuppressScrollback { get; set; }

    // Scrolling region (DECSTBM)
    public int ScrollTop { get; set; }
    public int ScrollBottom { get; set; }

    public TerminalBuffer(int cols, int rows)
    {
        Columns = cols;
        Rows = rows;
        ScrollTop = 0;
        ScrollBottom = rows - 1;
        _scrollback = new CircularBuffer<CellData[]>(MaxScrollback);
        _screen = new CellData[rows, cols];
        Clear();
    }

    public CellData GetCell(int row, int col) => _screen[row, col];

    /// <summary>
    /// Returns the cell visible at a given viewport row/col, accounting for scroll offset.
    /// When ScrollOffset == 0, returns the live screen cell. When scrolled back,
    /// maps viewport rows to scrollback or screen buffer lines.
    /// </summary>
    public CellData GetVisibleCell(int viewRow, int viewCol)
    {
        if (ScrollOffset == 0)
            return _screen[viewRow, viewCol];

        int totalLines = _scrollback.Count + Rows;
        int viewStart = totalLines - ScrollOffset - Rows;
        int lineIndex = viewStart + viewRow;

        if (lineIndex < 0)
            return CellData.Empty;

        if (lineIndex < _scrollback.Count)
        {
            var line = _scrollback[lineIndex];
            return viewCol < line.Length ? line[viewCol] : CellData.Empty;
        }

        int screenRow = lineIndex - _scrollback.Count;
        return screenRow < Rows && viewCol < Columns ? _screen[screenRow, viewCol] : CellData.Empty;
    }

    public void SetCell(int row, int col, CellData cell) => _screen[row, col] = cell;

    public void PutChar(char c)
    {
        if (CursorCol >= Columns)
        {
            CursorCol = 0;
            LineFeed();
        }
        _screen[CursorRow, CursorCol] = new CellData(c);
        CursorCol++;
    }

    public void LineFeed()
    {
        if (CursorRow == ScrollBottom)
        {
            ScrollUpRegion(ScrollTop, ScrollBottom, CellData.Empty);
        }
        else if (CursorRow < ScrollBottom)
        {
            CursorRow++;
        }
        else
        {
            // Below scroll region
            if (CursorRow < Rows - 1)
                CursorRow++;
        }
    }

    public void CarriageReturn() => CursorCol = 0;

    public void Backspace()
    {
        if (CursorCol > 0) CursorCol--;
    }

    public void Clear()
    {
        for (int r = 0; r < Rows; r++)
            for (int c = 0; c < Columns; c++)
                _screen[r, c] = CellData.Empty;
        CursorRow = 0;
        CursorCol = 0;
    }

    public void Resize(int cols, int rows)
    {
        var newScreen = new CellData[rows, cols];
        int copyRows = Math.Min(rows, Rows);
        int copyCols = Math.Min(cols, Columns);
        for (int r = 0; r < copyRows; r++)
            for (int c = 0; c < copyCols; c++)
                newScreen[r, c] = _screen[r, c];
        _screen = newScreen;
        Columns = cols;
        Rows = rows;
        ScrollTop = 0;
        ScrollBottom = rows - 1;
        CursorRow = Math.Min(CursorRow, rows - 1);
        CursorCol = Math.Min(CursorCol, cols - 1);
    }

    /// <summary>
    /// Scrolls the region [top..bottom] up by one line.
    /// Row at top is removed (added to scrollback if top==0), bottom row cleared with fillCell.
    /// </summary>
    public void ScrollUpRegion(int top, int bottom, CellData fillCell)
    {
        // Add top row to scrollback only if scrolling from absolute top
        // and not in alternate screen mode (alt screen content is discarded)
        if (top == 0 && !SuppressScrollback)
        {
            var row = new CellData[Columns];
            for (int c = 0; c < Columns; c++)
                row[c] = _screen[0, c];
            bool evicted = _scrollback.Add(row);
            TotalLinesScrolled++;

            // Keep viewport stable when user is scrolled back
            if (ScrollOffset > 0)
            {
                ScrollOffset++;
                // If an old line was evicted, adjust back down
                if (evicted)
                    ScrollOffset--;
            }
        }

        // Shift rows up within region
        for (int r = top; r < bottom; r++)
            for (int c = 0; c < Columns; c++)
                _screen[r, c] = _screen[r + 1, c];

        // Clear bottom row
        for (int c = 0; c < Columns; c++)
            _screen[bottom, c] = fillCell;
    }

    /// <summary>
    /// Scrolls the region [top..bottom] down by one line.
    /// Bottom row is lost, top row cleared with fillCell.
    /// </summary>
    public void ScrollDownRegion(int top, int bottom, CellData fillCell)
    {
        // Shift rows down within region
        for (int r = bottom; r > top; r--)
            for (int c = 0; c < Columns; c++)
                _screen[r, c] = _screen[r - 1, c];

        // Clear top row
        for (int c = 0; c < Columns; c++)
            _screen[top, c] = fillCell;
    }

    /// <summary>
    /// Scrolls the entire screen up (delegates to ScrollUpRegion with full screen).
    /// </summary>
    public void ScrollUp()
    {
        ScrollUpRegion(0, Rows - 1, CellData.Empty);
    }

    /// <summary>
    /// Scrolls the entire screen down (delegates to ScrollDownRegion with full screen).
    /// </summary>
    public void ScrollDown()
    {
        ScrollDownRegion(0, Rows - 1, CellData.Empty);
    }

    public CellData[] GetScrollbackLine(int index) => _scrollback[index];

    /// <summary>
    /// Clamps ScrollOffset to valid range based on current scrollback size.
    /// </summary>
    public void ClampScrollOffset()
    {
        ScrollOffset = Math.Clamp(ScrollOffset, 0, _scrollback.Count);
    }
}
