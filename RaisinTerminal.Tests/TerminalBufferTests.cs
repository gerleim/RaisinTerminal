using RaisinTerminal.Core.Models;
using RaisinTerminal.Core.Terminal;
using Xunit;

namespace RaisinTerminal.Tests;

public class TerminalBufferTests
{
    [Fact]
    public void PutChar_WritesToCurrentPosition()
    {
        var buffer = new TerminalBuffer(80, 24);
        buffer.PutChar('A');

        var cell = buffer.GetCell(0, 0);
        Assert.Equal('A', cell.Character);
        Assert.Equal(1, buffer.CursorCol);
    }

    [Fact]
    public void LineFeed_MovesCursorDown()
    {
        var buffer = new TerminalBuffer(80, 24);
        buffer.LineFeed();
        Assert.Equal(1, buffer.CursorRow);
    }

    [Fact]
    public void Clear_ResetsBuffer()
    {
        var buffer = new TerminalBuffer(80, 24);
        buffer.PutChar('X');
        buffer.Clear();

        Assert.Equal(0, buffer.CursorRow);
        Assert.Equal(0, buffer.CursorCol);
        Assert.Equal(' ', buffer.GetCell(0, 0).Character);
    }

    [Fact]
    public void Resize_GrowingBuffer_FillsNewCellsWithDefaultBackground()
    {
        // default(CellData) has bg=(0,0,0) which renders as black; Resize must
        // initialize freshly-allocated rows to CellData.Empty so the canvas's
        // per-cell bg-rect path doesn't paint black for unused rows.
        var buffer = new TerminalBuffer(80, 10);
        buffer.Resize(80, 24);

        for (int row = 10; row < 24; row++)
        {
            var cell = buffer.GetCell(row, 0);
            Assert.Equal(CellData.DefaultBgR, cell.BackgroundR);
            Assert.Equal(CellData.DefaultBgG, cell.BackgroundG);
            Assert.Equal(CellData.DefaultBgB, cell.BackgroundB);
        }
    }

    [Fact]
    public void GetVisibleCell_SmallerViewport_AnchorsToBottomOfLiveScreen()
    {
        // Reproduces the split-pane bug: a canvas shorter than buffer.Rows must
        // see the BOTTOM of the live screen at scrollOffset=0, not the top.
        // Simulates a 12-row pinned canvas above a 36-row live pane.
        var buffer = new TerminalBuffer(10, 36);
        for (int r = 0; r < 36; r++)
        {
            buffer.CursorRow = r;
            buffer.CursorCol = 0;
            // Tag each row with a distinct character so we can identify it.
            buffer.PutChar((char)('A' + r));
        }

        const int viewRows = 12;
        // At scrollOffset=0, viewRow 0 of the small canvas must show buffer row
        // (36 - 12) = 24, and viewRow 11 must show buffer row 35.
        Assert.Equal((char)('A' + 24), buffer.GetVisibleCell(0, 0, 0, viewRows).Character);
        Assert.Equal((char)('A' + 35), buffer.GetVisibleCell(11, 0, 0, viewRows).Character);
    }

    [Fact]
    public void GetVisibleCell_SmallerViewport_ScrollOffsetShiftsViewByOneRowPerStep()
    {
        // Each unit of scrollOffset should shift the visible window up by exactly
        // one buffer row — that's the property whose absence made scrolling feel
        // "stuck" in the pinned pane.
        var buffer = new TerminalBuffer(10, 36);
        for (int r = 0; r < 36; r++)
        {
            buffer.CursorRow = r;
            buffer.CursorCol = 0;
            buffer.PutChar((char)('A' + r));
        }

        const int viewRows = 12;
        // Bottom-row character at offset N should be ('A' + 35 - N).
        for (int n = 0; n <= 24; n++)
        {
            char bottom = buffer.GetVisibleCell(viewRows - 1, 0, n, viewRows).Character;
            Assert.Equal((char)('A' + 35 - n), bottom);
        }
    }

    [Fact]
    public void GetVisibleCell_FullHeightViewport_MatchesLegacyBehavior()
    {
        // When viewRows == buffer.Rows the new math must reduce to the historical
        // GetVisibleCell semantics so the live pane is unaffected by the fix.
        var buffer = new TerminalBuffer(10, 24);
        for (int r = 0; r < 24; r++)
        {
            buffer.CursorRow = r;
            buffer.CursorCol = 0;
            buffer.PutChar((char)('A' + r));
        }

        for (int viewRow = 0; viewRow < 24; viewRow++)
        {
            var withRows = buffer.GetVisibleCell(viewRow, 0, 0, 24);
            var legacy = buffer.GetVisibleCell(viewRow, 0, 0);
            Assert.Equal(legacy.Character, withRows.Character);
        }
    }

    [Fact]
    public void GetCellAtAbsoluteRow_ReturnsLiveAndScrollbackCellsCorrectly()
    {
        var buffer = new TerminalBuffer(10, 4);
        // Type 6 letters separated by linefeeds. The first two scroll into
        // scrollback when LineFeed at the bottom row triggers ScrollUpRegion.
        for (int i = 0; i < 6; i++)
        {
            buffer.PutChar((char)('A' + i));
            buffer.CarriageReturn();
            buffer.LineFeed();
        }

        // 6 chars/LFs starting at row 0 with rows=4: cursor reaches the bottom
        // after 'D', then 'D','E','F' each trigger a scroll-up — so 'A','B','C'
        // end up in scrollback and 'D','E','F' remain on screen.
        Assert.Equal(3, buffer.ScrollbackCount);
        Assert.Equal(3, buffer.TotalLinesScrolled);

        Assert.Equal('A', buffer.GetCellAtAbsoluteRow(0, 0).Character);
        Assert.Equal('B', buffer.GetCellAtAbsoluteRow(1, 0).Character);
        Assert.Equal('C', buffer.GetCellAtAbsoluteRow(2, 0).Character);
        // absRow 3 = first live row ('D')
        Assert.Equal('D', buffer.GetCellAtAbsoluteRow(3, 0).Character);
        // absRow 5 = third live row ('F')
        Assert.Equal('F', buffer.GetCellAtAbsoluteRow(5, 0).Character);
        // Out of range returns Empty (space)
        Assert.Equal(' ', buffer.GetCellAtAbsoluteRow(99, 0).Character);
    }

    [Fact]
    public void Resize_Shrink_PushesTopRowsToScrollback()
    {
        var buffer = new TerminalBuffer(10, 6);
        for (int i = 0; i < 6; i++)
        {
            buffer.CursorRow = i;
            buffer.CursorCol = 0;
            buffer.PutChar((char)('A' + i));
        }
        // Cursor at row 5 (last row). Shrink to 4 rows: pushCount = 5 - 4 + 1 = 2.
        buffer.CursorRow = 5;
        buffer.Resize(10, 4);

        Assert.Equal(2, buffer.ScrollbackCount);
        Assert.Equal('A', buffer.GetCellAtAbsoluteRow(0, 0).Character);
        Assert.Equal('B', buffer.GetCellAtAbsoluteRow(1, 0).Character);
        // Live screen row 0 = old row 2
        Assert.Equal('C', buffer.GetCell(0, 0).Character);
        Assert.Equal(3, buffer.CursorRow);
    }

    [Fact]
    public void Resize_Shrink_WithSuppressScrollback_ShiftsButDoesNotPushToScrollback()
    {
        // When SuppressScrollback is set, shrink still shifts content to keep the
        // cursor on-screen (matching ConPTY's layout), but discards the shifted-out
        // top rows instead of pushing them to scrollback.
        var buffer = new TerminalBuffer(10, 6);
        for (int i = 0; i < 6; i++)
        {
            buffer.CursorRow = i;
            buffer.CursorCol = 0;
            buffer.PutChar((char)('A' + i));
        }
        buffer.CursorRow = 5;
        buffer.SuppressScrollback = true;
        buffer.Resize(10, 4);

        Assert.Equal(0, buffer.ScrollbackCount);
        // Bottom rows preserved (C-F), top rows (A-B) discarded, cursor adjusted.
        Assert.Equal('C', buffer.GetCell(0, 0).Character);
        Assert.Equal('F', buffer.GetCell(3, 0).Character);
        Assert.Equal(3, buffer.CursorRow);
    }
}
