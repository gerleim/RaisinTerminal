namespace RaisinTerminal.Core.Terminal;

/// <summary>
/// Pure layout calculator for terminal row Y-positions with empty line compression.
/// Extracted from TerminalCanvas.OnRender for testability.
/// </summary>
public static class RowLayoutCalculator
{
    /// <summary>
    /// Computes Y-positions for each row, compressing interior empty rows.
    /// Row heights are constant (cellHeight or emptyHeight). No inflation.
    /// </summary>
    public static double[] ComputeRowYPositions(
        bool[] rowIsEmpty,
        int cursorRow,
        double cellHeight,
        double emptyRowScale)
    {
        int rowCount = rowIsEmpty.Length;
        var positions = new double[rowCount + 1];

        if (rowCount == 0)
        {
            positions[0] = 0;
            return positions;
        }

        double emptyHeight = Math.Round(cellHeight * emptyRowScale);

        int lastNonEmptyRow = FindLastNonEmptyRow(rowIsEmpty);

        double currentY = 0;
        for (int row = 0; row < rowCount; row++)
        {
            positions[row] = currentY;
            currentY += GetRowHeight(row, rowIsEmpty, lastNonEmptyRow, cursorRow, cellHeight, emptyHeight);
        }
        positions[rowCount] = currentY;

        return positions;
    }

    /// <summary>
    /// Computes bottom-aligned Y-positions for candidate rows.
    /// The last row always ends at canvasHeight. Rows that don't fit
    /// at the top get negative Y positions (clipped by the renderer).
    /// Row heights are constant — no inflation, no redistribution.
    /// </summary>
    public static double[] ComputeLayout(
        bool[] rowIsEmpty,
        int cursorRow,
        double cellHeight,
        double emptyRowScale,
        double canvasHeight)
    {
        int rowCount = rowIsEmpty.Length;
        var positions = new double[rowCount + 1];

        if (rowCount == 0)
        {
            positions[0] = canvasHeight;
            return positions;
        }

        double emptyHeight = Math.Round(cellHeight * emptyRowScale);
        int lastNonEmptyRow = FindLastNonEmptyRow(rowIsEmpty);

        // Compute row heights
        var heights = new double[rowCount];
        for (int row = 0; row < rowCount; row++)
        {
            heights[row] = GetRowHeight(row, rowIsEmpty, lastNonEmptyRow, cursorRow, cellHeight, emptyHeight);
        }

        // Position bottom-up: last row ends at canvasHeight
        positions[rowCount] = canvasHeight;
        for (int row = rowCount - 1; row >= 0; row--)
        {
            positions[row] = positions[row + 1] - heights[row];
        }

        return positions;
    }

    private static int FindLastNonEmptyRow(bool[] rowIsEmpty)
    {
        for (int row = rowIsEmpty.Length - 1; row >= 0; row--)
        {
            if (!rowIsEmpty[row])
                return row;
        }
        return -1;
    }

    private static double GetRowHeight(
        int row, bool[] rowIsEmpty, int lastNonEmptyRow, int cursorRow,
        double cellHeight, double emptyHeight)
    {
        bool compress = lastNonEmptyRow > 0
            && row < lastNonEmptyRow
            && row != cursorRow
            && rowIsEmpty[row];
        return compress ? emptyHeight : cellHeight;
    }
}
