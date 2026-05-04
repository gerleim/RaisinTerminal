using RaisinTerminal.Core.Collections;
using RaisinTerminal.Core.Models;

namespace RaisinTerminal.Core.Terminal;

/// <summary>
/// Character grid + scrollback buffer for terminal emulation.
/// </summary>
public class TerminalBuffer
{
    private CircularBuffer<CellData[]> _scrollback;
    private CircularBuffer<bool> _scrollbackWrapped;
    private CellData[,] _screen;
    private bool[] _screenWrapped;

    public int Columns { get; private set; }
    public int Rows { get; private set; }
    public int CursorRow { get; set; }
    public int CursorCol { get; set; }
    public int ScrollbackCount => _scrollback.Count;
    public int MaxScrollback { get; set; } = 10_000;

    /// <summary>
    /// Viewports currently rendering this buffer. Used by ScrollUpRegion to
    /// keep each scrolled-back viewport stable as new lines arrive.
    /// </summary>
    public List<TerminalViewport> Viewports { get; } = new();

    /// <summary>
    /// Fired after a line is committed to scrollback (top==0 scroll, not in alt-screen).
    /// The argument is the snapshotted row added to scrollback. Used by the transcript
    /// logger to record screen-accurate text as rows scroll out of view.
    /// </summary>
    public event Action<CellData[]>? ScrollbackLineAdded;

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

    /// <summary>
    /// When true AND <see cref="SuppressScrollback"/> is also true, scrolled-off rows
    /// are saved to a deferred list instead of being discarded. The emulator calls
    /// <see cref="ClearDeferredScrollback"/> at each TUI frame start (so only the
    /// latest frame's overflow survives) and <see cref="FlushDeferredScrollback"/>
    /// when output goes idle, committing the rows to real scrollback.
    /// </summary>
    public bool DeferScrollbackOnSuppress
    {
        get => _deferScrollbackOnSuppress;
        set
        {
            _deferScrollbackOnSuppress = value;
            if (value)
                _scrollbackCountAtDeferStart = _scrollback.Count;
        }
    }
    private bool _deferScrollbackOnSuppress;
    private int _scrollbackCountAtDeferStart;
    private List<CellData[]> _deferredScrollback = new();
    private List<bool> _deferredScrollbackWrapped = new();

    // Tracks rows pushed to scrollback by resize shrinks, so grow can pull them back.
    private int _resizePushCount;

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
        _scrollbackWrapped = new CircularBuffer<bool>(MaxScrollback);
        _screen = new CellData[rows, cols];
        _screenWrapped = new bool[rows];
        Clear();
    }

    public CellData GetCell(int row, int col) => _screen[row, col];

    /// <summary>
    /// Returns the cell visible at a given viewport row/col, accounting for scroll offset.
    /// Equivalent to <see cref="GetVisibleCell(int, int, int, int)"/> with viewRows = Rows.
    /// </summary>
    public CellData GetVisibleCell(int viewRow, int viewCol, int scrollOffset)
        => GetVisibleCell(viewRow, viewCol, scrollOffset, Rows);

    /// <summary>
    /// Returns the cell at (viewRow, viewCol) for a viewport of <paramref name="viewRows"/>
    /// rows anchored to the bottom of the live screen at scrollOffset = 0. As scrollOffset
    /// increases, the view shifts up by that many lines into scrollback. When viewRows
    /// equals <see cref="Rows"/>, this matches the historical "view spans the live screen"
    /// behavior. When viewRows &lt; Rows (e.g. a smaller pinned canvas above a larger live
    /// pane), the view stays anchored to the live screen's bottom row at offset 0 instead
    /// of the top.
    /// </summary>
    public CellData GetVisibleCell(int viewRow, int viewCol, int scrollOffset, int viewRows)
    {
        int totalLines = _scrollback.Count + Rows;
        int lineIndex = totalLines - viewRows - scrollOffset + viewRow;

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

    /// <summary>
    /// Returns the cell at the given absolute row index (the same coordinate system used
    /// by selection and search). Returns Empty if the row has been evicted from scrollback
    /// or is past the live screen's bottom.
    /// </summary>
    public CellData GetCellAtAbsoluteRow(long absRow, int col)
    {
        long lineIndex = absRow - (TotalLinesScrolled - _scrollback.Count);
        if (lineIndex < 0 || lineIndex >= _scrollback.Count + Rows)
            return CellData.Empty;

        if (lineIndex < _scrollback.Count)
        {
            var line = _scrollback[(int)lineIndex];
            return col < line.Length ? line[col] : CellData.Empty;
        }

        int screenRow = (int)(lineIndex - _scrollback.Count);
        return col < Columns ? _screen[screenRow, col] : CellData.Empty;
    }

    /// <summary>
    /// Monotonically increasing counter incremented on every cell write. Used by
    /// the transcript logger to detect whether the visible screen has changed
    /// since the last snapshot, so idle flushes can skip identical states.
    /// </summary>
    public long ModificationCount { get; private set; }

    public void SetCell(int row, int col, CellData cell)
    {
        _screen[row, col] = cell;
        ModificationCount++;
    }

    /// <summary>
    /// Marks a screen row as a soft-wrapped continuation of the previous row.
    /// </summary>
    public void SetLineWrapped(int row, bool wrapped) => _screenWrapped[row] = wrapped;

    /// <summary>Returns true if a screen row is a soft-wrapped continuation of the previous row.</summary>
    public bool IsScreenLineWrapped(int row) => row >= 0 && row < Rows && _screenWrapped[row];

    /// <summary>Returns true if a scrollback line is a soft-wrapped continuation of the previous line.</summary>
    public bool IsScrollbackLineWrapped(int index) => index >= 0 && index < _scrollback.Count && _scrollbackWrapped[index];

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
        Array.Clear(_screenWrapped);
        CursorRow = 0;
        CursorCol = 0;
    }

    /// <summary>
    /// Drops all saved scrollback lines. Used to implement ED 3 (ESC[3J),
    /// which xterm defines as "erase saved lines". The visible screen is not
    /// touched. Viewports are reset to live since their scroll positions
    /// reference content that no longer exists.
    /// </summary>
    public void ClearScrollback()
    {
        _scrollback.Clear();
        _scrollbackWrapped.Clear();
        _deferredScrollback.Clear();
        _deferredScrollbackWrapped.Clear();
        _resizePushCount = 0;
        foreach (var vp in Viewports)
        {
            vp.ScrollOffset = 0;
            vp.UserScrolledBack = false;
        }
    }

    /// <summary>
    /// Commits deferred scrollback from the previous frame, then resets for
    /// the new frame. Called at TUI frame start (CUP 1;1). The flush uses
    /// content-based dedup so identical rows from successive redraws are not
    /// duplicated; unique content (e.g. streaming output that scrolled off
    /// during the previous frame) is preserved.
    /// </summary>
    public void ClearDeferredScrollback()
    {
        FlushDeferredScrollback();
    }

    /// <summary>
    /// Commits deferred scrollback rows to the real scrollback buffer. Called when
    /// TUI output goes idle or Claude exits, so overflow from the last frame is
    /// preserved rather than lost.
    /// </summary>
    public void FlushDeferredScrollback()
    {
        if (_deferredScrollback.Count == 0) return;

        // Skip deferred rows that already exist in real scrollback (identical
        // TUI redraws produce the same overflow). Two strategies:
        // 1. From-start: deferred rows match the start of the committed region
        //    (handles successive TUI redraws where each frame overflows the same rows).
        // 2. Tail-match: deferred rows match the last N rows of scrollback
        //    (handles resize-push + redraw where pushed rows are a superset of deferred).
        int alreadyCommitted = _scrollback.Count - _scrollbackCountAtDeferStart;
        int skip = 0;
        int maxSkip = Math.Clamp(alreadyCommitted, 0, _deferredScrollback.Count);
        for (int i = 0; i < maxSkip; i++)
        {
            if (RowsEqual(_scrollback[_scrollbackCountAtDeferStart + i],
                          _deferredScrollback[i]))
                skip++;
            else
                break;
        }

        if (skip == 0 && _deferredScrollback.Count > 0
            && _scrollback.Count >= _deferredScrollback.Count)
        {
            int tailStart = _scrollback.Count - _deferredScrollback.Count;
            if (tailStart >= _scrollbackCountAtDeferStart)
            {
                int tailSkip = 0;
                for (int i = 0; i < _deferredScrollback.Count; i++)
                {
                    if (RowsEqual(_scrollback[tailStart + i], _deferredScrollback[i]))
                        tailSkip++;
                    else
                        break;
                }
                if (tailSkip == _deferredScrollback.Count)
                    skip = tailSkip;
            }
        }

        for (int i = skip; i < _deferredScrollback.Count; i++)
        {
            var row = _deferredScrollback[i];
            bool evicted = _scrollback.Add(row);
            _scrollbackWrapped.Add(_deferredScrollbackWrapped[i]);
            TotalLinesScrolled++;
            ScrollbackLineAdded?.Invoke(row);

            foreach (var vp in Viewports)
            {
                if (vp.ScrollOffset > 0)
                {
                    vp.ScrollOffset++;
                    if (evicted)
                        vp.ScrollOffset--;
                }
            }
        }
        _deferredScrollback.Clear();
        _deferredScrollbackWrapped.Clear();
    }

    public void Resize(int cols, int rows)
    {
        if (cols == Columns && rows == Rows) return;

        // SHRINK: shift content so cursor stays on-screen.
        // Always compute the shift so our buffer matches ConPTY's layout;
        // only commit rows to scrollback when not suppressed.
        int pushCount = 0;
        if (rows < Rows && CursorRow >= rows)
        {
            pushCount = CursorRow - rows + 1;
            if (!SuppressScrollback)
            {
                for (int r = 0; r < pushCount; r++)
                {
                    var row = new CellData[Columns];
                    for (int c = 0; c < Columns; c++)
                        row[c] = _screen[r, c];
                    _scrollback.Add(row);
                    _scrollbackWrapped.Add(_screenWrapped[r]);
                    TotalLinesScrolled++;
                }
                _resizePushCount += pushCount;

                foreach (var vp in Viewports)
                {
                    if (vp.ScrollOffset > 0)
                    {
                        int c = vp.CanvasRows;
                        if (c > 0 && c < Rows)
                        {
                            int vOld = Math.Min(Rows, c);
                            int vNew = Math.Min(rows, c);
                            vp.ScrollOffset += vOld - vNew;
                        }
                        else
                        {
                            vp.ScrollOffset += pushCount;
                        }
                    }
                }
            }
            CursorRow -= pushCount;
        }

        // GROW: pull rows from scrollback to match ConPTY's behavior
        int pullCount = 0;
        if (rows > Rows && !SuppressScrollback && _resizePushCount > 0)
        {
            int extraRows = rows - Rows;
            pullCount = Math.Min(extraRows, Math.Min(_resizePushCount, _scrollback.Count));
        }

        var newScreen = new CellData[rows, cols];
        for (int r = 0; r < rows; r++)
            for (int c = 0; c < cols; c++)
                newScreen[r, c] = CellData.Empty;

        var newWrapped = new bool[rows];
        int copyCols = Math.Min(cols, Columns);

        // Place pulled scrollback rows at top of new screen
        for (int r = 0; r < pullCount; r++)
        {
            int sbIdx = _scrollback.Count - pullCount + r;
            var line = _scrollback[sbIdx];
            newWrapped[r] = _scrollbackWrapped[sbIdx];
            for (int c = 0; c < Math.Min(cols, line.Length); c++)
                newScreen[r, c] = line[c];
        }

        // Copy old screen content after pulled rows
        int copyRows = Math.Min(rows - pullCount, Rows - pushCount);
        for (int r = 0; r < copyRows; r++)
        {
            newWrapped[pullCount + r] = _screenWrapped[r + pushCount];
            for (int c = 0; c < copyCols; c++)
                newScreen[pullCount + r, c] = _screen[r + pushCount, c];
        }

        // Remove pulled rows from scrollback
        if (pullCount > 0)
        {
            _scrollback.RemoveNewest(pullCount);
            _scrollbackWrapped.RemoveNewest(pullCount);
            TotalLinesScrolled -= pullCount;
            _resizePushCount -= pullCount;
            CursorRow += pullCount;

            foreach (var vp in Viewports)
            {
                if (vp.ScrollOffset > 0)
                {
                    int c = vp.CanvasRows;
                    if (c > 0 && c < rows)
                    {
                        int vOld = Math.Min(Rows, c);
                        int vNew = Math.Min(rows, c);
                        vp.ScrollOffset = Math.Max(0, vp.ScrollOffset + vOld - vNew);
                    }
                    else
                    {
                        vp.ScrollOffset = Math.Max(0, vp.ScrollOffset - pullCount);
                    }
                }
            }
        }

        _screen = newScreen;
        _screenWrapped = newWrapped;
        int oldRows = Rows;
        Columns = cols;
        Rows = rows;

        if (ScrollTop >= rows || ScrollBottom >= rows)
        {
            ScrollTop = 0;
            ScrollBottom = rows - 1;
        }
        else if (ScrollBottom == oldRows - 1)
        {
            ScrollBottom = rows - 1;
        }

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
            _scrollbackWrapped.Add(_screenWrapped[0]);
            TotalLinesScrolled++;
            ScrollbackLineAdded?.Invoke(row);

            // Keep each scrolled-back viewport stable as new lines arrive.
            foreach (var vp in Viewports)
            {
                if (vp.ScrollOffset > 0)
                {
                    vp.ScrollOffset++;
                    if (evicted)
                        vp.ScrollOffset--;
                }
            }
        }
        else if (top == 0 && SuppressScrollback && DeferScrollbackOnSuppress)
        {
            var row = new CellData[Columns];
            for (int c = 0; c < Columns; c++)
                row[c] = _screen[0, c];
            _deferredScrollback.Add(row);
            _deferredScrollbackWrapped.Add(_screenWrapped[0]);
        }

        // Shift rows up within region
        for (int r = top; r < bottom; r++)
        {
            _screenWrapped[r] = _screenWrapped[r + 1];
            for (int c = 0; c < Columns; c++)
                _screen[r, c] = _screen[r + 1, c];
        }

        // Clear bottom row
        _screenWrapped[bottom] = false;
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
        {
            _screenWrapped[r] = _screenWrapped[r - 1];
            for (int c = 0; c < Columns; c++)
                _screen[r, c] = _screen[r - 1, c];
        }

        // Clear top row
        _screenWrapped[top] = false;
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

    private static bool RowsEqual(CellData[] a, CellData[] b)
    {
        if (a.Length != b.Length) return false;
        for (int i = 0; i < a.Length; i++)
            if (a[i].Character != b[i].Character)
                return false;
        return true;
    }

    public CellData[] GetScrollbackLine(int index) => _scrollback[index];

    public bool IsScrollbackLineEmpty(int index)
    {
        if (index < 0 || index >= _scrollback.Count) return true;
        var line = _scrollback[index];
        for (int c = 0; c < line.Length; c++)
        {
            if (line[c].Character != ' ' && line[c].Character != '\0')
                return false;
        }
        return true;
    }

    public int EffectiveScrollbackCount
    {
        get
        {
            for (int i = _scrollback.Count - 1; i >= 0; i--)
            {
                if (!IsScrollbackLineEmpty(i))
                    return i + 1;
            }
            return 0;
        }
    }

    /// <summary>
    /// Reads the text content of a scrollback line as a string.
    /// </summary>
    public string GetScrollbackLineText(int index)
    {
        var line = _scrollback[index];
        var sb = new System.Text.StringBuilder(line.Length);
        for (int c = 0; c < line.Length; c++)
            sb.Append(line[c].Character == '\0' ? ' ' : line[c].Character);
        return sb.ToString();
    }

    /// <summary>
    /// Reads the text content of a screen row as a string.
    /// </summary>
    public string GetScreenLineText(int row)
    {
        var sb = new System.Text.StringBuilder(Columns);
        for (int c = 0; c < Columns; c++)
            sb.Append(_screen[row, c].Character == '\0' ? ' ' : _screen[row, c].Character);
        return sb.ToString();
    }

}
