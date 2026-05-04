using RaisinTerminal.Core.Terminal;
using Xunit;

namespace RaisinTerminal.Tests;

public class CanvasLayoutEngineTests
{
    private const double CellHeight = 20.0;
    private const double EmptyRowScale = 0.35;
    private const double EmptyHeight = 7.0; // Math.Round(20 * 0.35)

    // ========================================================================
    // Bug 1: Gap at bottom when compression shrinks content and no scrollback
    // ========================================================================

    [Fact]
    public void NoGapAtBottom_WhenCompressionShrinksContent_NoScrollback()
    {
        // 10-row terminal, 40 cols
        // Content: row0="Hello", row1="", row2="", row3="World", rows 4-9 empty (trailing)
        // With compression: rows 1,2 compressed (interior empties)
        // Trailing rows 4-9 trimmed then restored by gap-fill loop
        // Expected: content should reach the bottom of the canvas (no gap)
        var t = new TerminalTestHarness(40, 10);
        t.Feed("Hello\r\n\r\n\r\nWorld");

        double canvasHeight = 10 * CellHeight; // 200

        var result = CanvasLayoutEngine.Compute(
            t.Buffer,
            scrollOffset: 0,
            canvasRows: 10,
            displayCursorRow: 3, // cursor on "World" row
            canvasHeight: canvasHeight,
            cellHeight: CellHeight,
            emptyRowScale: EmptyRowScale,
            topAnchor: false);

        int totalRows = result.DisplayedBaseRows + result.ExtraRows;
        double lastRowBottom = result.RowYPositions[totalRows];

        // The last displayed row should reach the canvas bottom.
        // Gap should be less than EmptyHeight (the smallest possible row).
        double gap = canvasHeight - lastRowBottom;
        Assert.True(gap < EmptyHeight,
            $"Gap at bottom ({gap:F1}px) should be less than {EmptyHeight}px. " +
            $"totalRows={totalRows}, displayedBase={result.DisplayedBaseRows}, extra={result.ExtraRows}");
    }

    [Fact]
    public void NoGapAtBottom_WhenManyInteriorEmpties_NoScrollback()
    {
        // 20-row terminal with content at rows 0, 10, 19 and empties between
        // Interior empties save significant space but there's no scrollback
        var t = new TerminalTestHarness(40, 20);
        t.Feed("First");
        t.CursorTo(11, 1).Feed("Middle");
        t.CursorTo(20, 1).Feed("Last");

        double canvasHeight = 20 * CellHeight; // 400

        var result = CanvasLayoutEngine.Compute(
            t.Buffer,
            scrollOffset: 0,
            canvasRows: 20,
            displayCursorRow: 19,
            canvasHeight: canvasHeight,
            cellHeight: CellHeight,
            emptyRowScale: EmptyRowScale,
            topAnchor: false);

        int totalRows = result.DisplayedBaseRows + result.ExtraRows;
        double lastRowBottom = result.RowYPositions[totalRows];

        double gap = canvasHeight - lastRowBottom;
        Assert.True(gap < EmptyHeight,
            $"Gap at bottom ({gap:F1}px) should be less than {EmptyHeight}px with many interior empties");
    }

    // ========================================================================
    // Bug 2: Empty lines show at full height when scrolled up in scrollback
    // ========================================================================

    [Fact]
    public void ScrolledUp_InteriorEmptyLines_AreCompressed()
    {
        // When scrolled up far enough that no extra scrollback rows are available
        // above, the layout hits Path B which disables compression entirely.
        // But the visible content still has interior empties that should compress.
        var t = new TerminalTestHarness(40, 10);

        // Write content with empties between blocks, creating scrollback
        // Pattern: content, empty, empty, content — fills 10-row buffer + scrollback
        for (int i = 0; i < 30; i++)
        {
            if (i % 3 == 0)
                t.Feed($"Line{i}\r\n");
            else
                t.Feed("\r\n");
        }

        Assert.True(t.Buffer.ScrollbackCount > 0,
            "Setup: should have scrollback for this test");

        double canvasHeight = 10 * CellHeight; // 200

        // Scroll up far enough that availableAbove = scrollbackCount - scrollOffset <= 0
        // This forces extraRows = 0 and displayedBaseRows == baseRowCount → Path B
        int scrollOffset = t.Buffer.ScrollbackCount;

        var result = CanvasLayoutEngine.Compute(
            t.Buffer,
            scrollOffset: scrollOffset,
            canvasRows: 10,
            displayCursorRow: -1,
            canvasHeight: canvasHeight,
            cellHeight: CellHeight,
            emptyRowScale: EmptyRowScale,
            topAnchor: false);

        int totalRows = result.DisplayedBaseRows + result.ExtraRows;

        // Verify the visible content actually has empty rows between content
        int emptyCount = 0;
        int nonEmptyCount = 0;
        for (int i = 0; i < totalRows; i++)
        {
            if (IsDisplayRowEmpty(t.Buffer, i, result.ExtraRows, scrollOffset, 10))
                emptyCount++;
            else
                nonEmptyCount++;
        }
        Assert.True(emptyCount > 0 && nonEmptyCount > 1,
            $"Setup: should have interior empties. empty={emptyCount}, nonEmpty={nonEmptyCount}");

        // Interior empties between content should be compressed
        bool hasCompressedRow = false;
        for (int i = 0; i < totalRows; i++)
        {
            double h = result.RowYPositions[i + 1] - result.RowYPositions[i];
            if (Math.Abs(h - EmptyHeight) < 0.01)
                hasCompressedRow = true;
        }

        Assert.True(hasCompressedRow,
            "When scrolled up with interior empty lines visible, some rows should be compressed");
    }

    [Fact]
    public void ScrolledUp_EmptyLinesBetweenContent_NotFullHeight()
    {
        // Simpler scenario: fill 10-row terminal, push content into scrollback,
        // then scroll up to see scrollback with empties between content lines
        var t = new TerminalTestHarness(40, 10);

        // Write content with empty gaps that will go into scrollback
        t.FeedLines("Alpha", "", "", "Beta", "", "", "Gamma", "", "", "Delta",
                     "", "", "Epsilon", "", "", "Zeta", "", "", "Eta", "",
                     "", "Theta", "", "", "Iota", "", "", "Kappa");

        Assert.True(t.Buffer.ScrollbackCount > 0, "Setup: should have scrollback");

        double canvasHeight = 10 * CellHeight; // 200

        // Scroll up enough to see content with empties
        int scrollOffset = Math.Min(10, t.Buffer.ScrollbackCount);

        var result = CanvasLayoutEngine.Compute(
            t.Buffer,
            scrollOffset: scrollOffset,
            canvasRows: 10,
            displayCursorRow: -1,
            canvasHeight: canvasHeight,
            cellHeight: CellHeight,
            emptyRowScale: EmptyRowScale,
            topAnchor: false);

        int totalRows = result.DisplayedBaseRows + result.ExtraRows;

        // Count full-height vs compressed rows
        int fullHeightEmpty = 0;
        int compressedEmpty = 0;
        for (int i = 0; i < totalRows; i++)
        {
            double h = result.RowYPositions[i + 1] - result.RowYPositions[i];
            // Check if this display row is empty
            bool isEmpty = IsDisplayRowEmpty(t.Buffer, i, result.ExtraRows, scrollOffset, 10);
            if (isEmpty)
            {
                if (Math.Abs(h - CellHeight) < 0.01)
                    fullHeightEmpty++;
                else if (Math.Abs(h - EmptyHeight) < 0.01)
                    compressedEmpty++;
            }
        }

        // Interior empty rows should be compressed, not full height
        // (trailing empties after last content are allowed to be full height)
        Assert.True(compressedEmpty > 0 || fullHeightEmpty == 0,
            $"Expected compressed empty rows when scrolled up. " +
            $"Got {fullHeightEmpty} full-height empties and {compressedEmpty} compressed empties");
    }

    // ========================================================================
    // Sanity checks — verify extraction matches expected behavior
    // ========================================================================

    [Fact]
    public void FullScreen_NoCompression_UniformHeight()
    {
        var t = new TerminalTestHarness(40, 10);
        for (int i = 0; i < 10; i++)
            t.Feed($"Row{i}\r\n");

        double canvasHeight = 10 * CellHeight;

        var result = CanvasLayoutEngine.Compute(
            t.Buffer, 0, 10, 9, canvasHeight, CellHeight, EmptyRowScale, false);

        int totalRows = result.DisplayedBaseRows + result.ExtraRows;
        Assert.Equal(10, totalRows);

        for (int i = 0; i < 10; i++)
        {
            double h = result.RowYPositions[i + 1] - result.RowYPositions[i];
            Assert.Equal(CellHeight, h, 0.01);
        }
    }

    [Fact]
    public void InteriorEmpties_AreCompressed_WhenNotScrolled()
    {
        var t = new TerminalTestHarness(40, 10);
        // Content at rows 0, 5, 9 — empties 1-4, 6-8 are interior
        t.Feed("First");
        t.CursorTo(6, 1).Feed("Middle");
        t.CursorTo(10, 1).Feed("Last");

        double canvasHeight = 10 * CellHeight;

        var result = CanvasLayoutEngine.Compute(
            t.Buffer, 0, 10, 9, canvasHeight, CellHeight, EmptyRowScale, false);

        // Should have compressed rows
        bool hasCompressed = false;
        int totalRows = result.DisplayedBaseRows + result.ExtraRows;
        for (int i = 0; i < totalRows; i++)
        {
            double h = result.RowYPositions[i + 1] - result.RowYPositions[i];
            if (Math.Abs(h - EmptyHeight) < 0.01)
                hasCompressed = true;
        }

        Assert.True(hasCompressed, "Interior empty rows should be compressed");
    }

    private static bool IsDisplayRowEmpty(TerminalBuffer buffer, int displayRow, int extraRows, int scrollOffset, int baseRowCount)
    {
        int cols = buffer.Columns;
        for (int c = 0; c < cols; c++)
        {
            var cell = buffer.GetVisibleCell(displayRow, c, scrollOffset, baseRowCount + extraRows);
            if (cell.Character != ' ' && cell.Character != '\0' && cell.Character != '│')
                return false;
        }
        return true;
    }
}
