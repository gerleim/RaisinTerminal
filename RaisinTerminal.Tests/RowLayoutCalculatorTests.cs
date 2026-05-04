using RaisinTerminal.Core.Terminal;
using Xunit;

namespace RaisinTerminal.Tests;

public class RowLayoutCalculatorTests
{
    private const double CellHeight = 19.0;
    private const double EmptyRowScale = 0.35;
    private const double EmptyHeight = 7.0; // Math.Round(19 * 0.35)
    private const double CanvasHeight = 570.0; // 30 rows * 19

    // ── ComputeRowYPositions tests (top-down, no canvas constraint) ──

    [Fact]
    public void TopDown_AllRowsNonEmpty_UniformHeight()
    {
        var empty = new bool[30];
        var pos = RowLayoutCalculator.ComputeRowYPositions(empty, 0, CellHeight, EmptyRowScale);

        for (int i = 0; i < 30; i++)
            Assert.Equal(i * CellHeight, pos[i]);
        Assert.Equal(30 * CellHeight, pos[30]);
    }

    [Fact]
    public void TopDown_InteriorEmptyRows_AreCompressed()
    {
        var empty = new bool[10];
        empty[1] = empty[2] = empty[3] = true; // interior
        empty[5] = empty[6] = empty[7] = empty[8] = empty[9] = true; // trailing

        var pos = RowLayoutCalculator.ComputeRowYPositions(empty, -1, CellHeight, EmptyRowScale);

        for (int i = 1; i <= 3; i++)
            Assert.Equal(EmptyHeight, pos[i + 1] - pos[i], 0.01);

        // Trailing: full height
        for (int i = 5; i <= 9; i++)
            Assert.Equal(CellHeight, pos[i + 1] - pos[i], 0.01);
    }

    [Fact]
    public void TopDown_CursorRow_NeverCompressed()
    {
        var empty = new bool[10];
        for (int i = 1; i < 9; i++) empty[i] = true;

        var pos = RowLayoutCalculator.ComputeRowYPositions(empty, 5, CellHeight, EmptyRowScale);
        Assert.Equal(CellHeight, pos[6] - pos[5], 0.01);
    }

    // ── ComputeLayout tests (bottom-aligned, canvas-filling) ──

    [Fact]
    public void Layout_LastRowEndsAtCanvasHeight()
    {
        var empty = new bool[30];
        var pos = RowLayoutCalculator.ComputeLayout(empty, 0, CellHeight, EmptyRowScale, CanvasHeight);

        Assert.Equal(CanvasHeight, pos[30]);
    }

    [Fact]
    public void Layout_AllNonEmpty_FillsCanvas()
    {
        var empty = new bool[30]; // all non-empty
        var pos = RowLayoutCalculator.ComputeLayout(empty, 0, CellHeight, EmptyRowScale, CanvasHeight);

        // First row starts at 0 (exact fit: 30 * 19 = 570)
        Assert.Equal(0.0, pos[0], 0.01);
        Assert.Equal(CanvasHeight, pos[30]);
    }

    [Fact]
    public void Layout_WithCompression_TopRowsHaveNegativeY()
    {
        // 35 candidate rows, more than fit in 570px canvas
        var empty = new bool[35];
        var pos = RowLayoutCalculator.ComputeLayout(empty, -1, CellHeight, EmptyRowScale, CanvasHeight);

        // 35 * 19 = 665 > 570, so first row starts at 570 - 665 = -95
        Assert.True(pos[0] < 0, $"pos[0]={pos[0]} should be negative when too many rows");
        Assert.Equal(CanvasHeight, pos[35]);
    }

    [Fact]
    public void Layout_RowHeightsAreConstant()
    {
        // Mix of empty and non-empty rows
        var empty = new bool[30];
        empty[3] = empty[5] = empty[7] = empty[10] = empty[12] = true; // interior
        empty[25] = empty[26] = empty[27] = empty[28] = empty[29] = true; // trailing

        var pos = RowLayoutCalculator.ComputeLayout(empty, -1, CellHeight, EmptyRowScale, CanvasHeight);

        for (int i = 0; i < 30; i++)
        {
            double h = pos[i + 1] - pos[i];
            // Height is either cellHeight or emptyHeight, nothing else
            bool isCellHeight = Math.Abs(h - CellHeight) < 0.01;
            bool isEmptyHeight = Math.Abs(h - EmptyHeight) < 0.01;
            Assert.True(isCellHeight || isEmptyHeight,
                $"Row {i} height {h} is neither cellHeight ({CellHeight}) nor emptyHeight ({EmptyHeight})");
        }
    }

    [Fact]
    public void Layout_ExtraRowsFillCompressionGap()
    {
        // 30 base rows, 10 interior empties → saves ~120px
        // Add ~6 extra rows (120/19) to fill the gap
        int baseRows = 30;
        int extraRows = 7;
        int totalRows = baseRows + extraRows;

        var empty = new bool[totalRows];
        // Interior empties in the base region (rows extraRows+5..extraRows+15)
        for (int i = extraRows + 5; i < extraRows + 15; i++)
            empty[i] = true;

        var pos = RowLayoutCalculator.ComputeLayout(empty, -1, CellHeight, EmptyRowScale, CanvasHeight);

        Assert.Equal(CanvasHeight, pos[totalRows]);

        // With enough extra rows, pos[0] should be near 0 or negative (filled)
        // Total natural height: 27 normal + 10 compressed = 27*19 + 10*7 = 513+70 = 583 > 570
        // So pos[0] should be negative (some rows clipped at top)
        Assert.True(pos[0] <= 0.01, $"pos[0]={pos[0]} should be near 0 with enough extra rows");
    }

    [Fact]
    public void Layout_InteriorCompression_NotTrailing()
    {
        // Content at rows 0 and 20, empties 1-19 (interior), empties 21-29 (trailing)
        var empty = new bool[30];
        for (int i = 1; i < 20; i++) empty[i] = true;
        for (int i = 21; i < 30; i++) empty[i] = true;

        var pos = RowLayoutCalculator.ComputeLayout(empty, -1, CellHeight, EmptyRowScale, CanvasHeight);

        // Interior empties (1-19) should be compressed
        for (int i = 1; i < 20; i++)
        {
            double h = pos[i + 1] - pos[i];
            Assert.Equal(EmptyHeight, h, 0.01);
        }

        // Trailing empties (21-29) should be full height
        for (int i = 21; i < 30; i++)
        {
            double h = pos[i + 1] - pos[i];
            Assert.Equal(CellHeight, h, 0.01);
        }
    }

    [Fact]
    public void Layout_CursorProtected()
    {
        var empty = new bool[30];
        for (int i = 1; i < 29; i++) empty[i] = true;

        var pos = RowLayoutCalculator.ComputeLayout(empty, 15, CellHeight, EmptyRowScale, CanvasHeight);

        double cursorH = pos[16] - pos[15];
        Assert.Equal(CellHeight, cursorH, 0.01);
    }

    [Fact]
    public void Layout_PositionsAreMonotonicallyIncreasing()
    {
        var empty = new bool[30];
        empty[3] = empty[5] = empty[7] = true;

        var pos = RowLayoutCalculator.ComputeLayout(empty, -1, CellHeight, EmptyRowScale, CanvasHeight);

        for (int i = 0; i < pos.Length - 1; i++)
            Assert.True(pos[i + 1] > pos[i], $"pos[{i + 1}]={pos[i + 1]} <= pos[{i}]={pos[i]}");
    }

    [Fact]
    public void Layout_EmptyArray_ReturnsCanvasHeight()
    {
        var pos = RowLayoutCalculator.ComputeLayout([], -1, CellHeight, EmptyRowScale, CanvasHeight);
        Assert.Single(pos);
        Assert.Equal(CanvasHeight, pos[0]);
    }

    [Fact]
    public void Layout_AlwaysBottomAligns()
    {
        var empty = new bool[5];
        var pos = RowLayoutCalculator.ComputeLayout(empty, -1, CellHeight, EmptyRowScale, CanvasHeight);

        // Last row ends at canvasHeight (bottom-aligned)
        Assert.Equal(CanvasHeight, pos[5]);
        // First row starts offset from top
        Assert.Equal(CanvasHeight - 5 * CellHeight, pos[0]);
        // Each row is cellHeight
        for (int i = 0; i < 5; i++)
            Assert.Equal(CellHeight, pos[i + 1] - pos[i], 0.01);
    }

    [Fact]
    public void Layout_SameContentPattern_SameRowHeights()
    {
        // Same interior compression pattern → same row heights regardless of candidate count
        var empty1 = new bool[] { false, true, true, false, true, true, true };
        var empty2 = new bool[] { false, false, false, true, true, false, true, true, true };

        var pos1 = RowLayoutCalculator.ComputeLayout(empty1, -1, CellHeight, EmptyRowScale, 200);
        var pos2 = RowLayoutCalculator.ComputeLayout(empty2, -1, CellHeight, EmptyRowScale, 200);

        // Both: interior compressed rows should be emptyHeight
        Assert.Equal(EmptyHeight, pos1[2] - pos1[1], 0.01);
        Assert.Equal(EmptyHeight, pos1[3] - pos1[2], 0.01);
    }

    [Fact]
    public void Layout_AllEmpty_NoCompression()
    {
        // All rows empty → lastNonEmptyRow == -1 → no compression
        var empty = Enumerable.Repeat(true, 30).ToArray();
        var pos = RowLayoutCalculator.ComputeLayout(empty, -1, CellHeight, EmptyRowScale, CanvasHeight);

        // Every row should be cellHeight (no interior empties when all empty)
        for (int i = 0; i < 30; i++)
            Assert.Equal(CellHeight, pos[i + 1] - pos[i], 0.01);
    }
}
