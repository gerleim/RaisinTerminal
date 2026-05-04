using RaisinTerminal.Core.Models;

namespace RaisinTerminal.Core.Terminal;

public static class CanvasLayoutEngine
{
    public record struct LayoutResult(
        double[] RowYPositions,
        int ExtraRows,
        int DisplayedBaseRows);

    public static LayoutResult Compute(
        TerminalBuffer buffer,
        int scrollOffset,
        int canvasRows,
        int displayCursorRow,
        double canvasHeight,
        double cellHeight,
        double emptyRowScale,
        bool topAnchor)
    {
        int baseRowCount = ViewportCalculator.BaseRowCount(buffer.Rows, canvasRows);
        int viewOffset = ViewportCalculator.ViewOffset(buffer.Rows, canvasRows);

        int extraRows = 0;
        int displayedBaseRows = baseRowCount;

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
            trimmedEmpty, displayCursorRow, cellHeight, emptyRowScale);

        {
            double savedPixels = canvasHeight - basePositions[displayedBaseRows];
            extraRows = ViewportCalculator.ExtraRowsFromCompression(
                savedPixels, cellHeight, emptyRowScale,
                buffer.ScrollbackCount, viewOffset, scrollOffset);
        }

        if (extraRows > 0 && buffer.ScrollbackCount > 0)
        {
            int newestSb = buffer.ScrollbackCount - 1;
            if (buffer.IsScrollbackLineEmpty(newestSb))
                extraRows = 0;
        }

        double[] rowYPositions;

        if (extraRows == 0 && (topAnchor || displayedBaseRows < baseRowCount))
        {
            rowYPositions = basePositions;
        }
        else if (extraRows == 0 && displayedBaseRows == baseRowCount)
        {
            // Full buffer, no scrollback to fill compression gaps.
            // Bottom-align with compression — interior empties compress,
            // the gap appears at top which is fine for a bottom-anchored view.
            rowYPositions = RowLayoutCalculator.ComputeLayout(
                baseIsEmpty, displayCursorRow, cellHeight, emptyRowScale, canvasHeight);
        }
        else
        {
            int totalCandidateRows = displayedBaseRows + extraRows;
            var allIsEmpty = BuildEmptyArray(buffer, totalCandidateRows, extraRows, scrollOffset, baseRowCount);

            int adjustedCursorRow = displayCursorRow + extraRows;
            if (topAnchor || baseRowCount < canvasRows || displayedBaseRows < baseRowCount)
            {
                rowYPositions = RowLayoutCalculator.ComputeRowYPositions(
                    allIsEmpty, adjustedCursorRow, cellHeight, emptyRowScale);

                while (extraRows > 0 && rowYPositions[totalCandidateRows] > canvasHeight)
                {
                    extraRows--;
                    totalCandidateRows = displayedBaseRows + extraRows;
                    allIsEmpty = BuildEmptyArray(buffer, totalCandidateRows, extraRows, scrollOffset, baseRowCount);
                    adjustedCursorRow = displayCursorRow + extraRows;
                    rowYPositions = RowLayoutCalculator.ComputeRowYPositions(
                        allIsEmpty, adjustedCursorRow, cellHeight, emptyRowScale);
                }

                int maxAvailable = Math.Max(0, buffer.ScrollbackCount + viewOffset - scrollOffset);
                while (extraRows < maxAvailable && rowYPositions[totalCandidateRows] + cellHeight <= canvasHeight)
                {
                    extraRows++;
                    totalCandidateRows = displayedBaseRows + extraRows;
                    allIsEmpty = BuildEmptyArray(buffer, totalCandidateRows, extraRows, scrollOffset, baseRowCount);
                    adjustedCursorRow = displayCursorRow + extraRows;
                    rowYPositions = RowLayoutCalculator.ComputeRowYPositions(
                        allIsEmpty, adjustedCursorRow, cellHeight, emptyRowScale);
                    if (rowYPositions[totalCandidateRows] > canvasHeight)
                    {
                        extraRows--;
                        totalCandidateRows = displayedBaseRows + extraRows;
                        allIsEmpty = BuildEmptyArray(buffer, totalCandidateRows, extraRows, scrollOffset, baseRowCount);
                        adjustedCursorRow = displayCursorRow + extraRows;
                        rowYPositions = RowLayoutCalculator.ComputeRowYPositions(
                            allIsEmpty, adjustedCursorRow, cellHeight, emptyRowScale);
                        break;
                    }
                }
            }
            else
            {
                rowYPositions = RowLayoutCalculator.ComputeLayout(
                    allIsEmpty, adjustedCursorRow, cellHeight, emptyRowScale, canvasHeight);
            }
        }

        // If top-aligned content doesn't fill the canvas and there are
        // trimmed trailing empty rows we can restore, grow displayedBaseRows
        // to eliminate the gap at the bottom.
        while (displayedBaseRows < baseRowCount)
        {
            int total = displayedBaseRows + extraRows;
            if (rowYPositions[total] + cellHeight > canvasHeight)
                break;
            displayedBaseRows++;
            total = displayedBaseRows + extraRows;
            var allIsEmpty2 = BuildEmptyArray(buffer, total, extraRows, scrollOffset, baseRowCount);
            int adjCursor = displayCursorRow + extraRows;
            rowYPositions = RowLayoutCalculator.ComputeRowYPositions(
                allIsEmpty2, adjCursor, cellHeight, emptyRowScale);
        }

        // After restoring trailing rows, if content still doesn't fill the
        // canvas (compression saved more than trailing rows could reclaim),
        // shift everything down to eliminate the bottom gap.
        {
            int total = displayedBaseRows + extraRows;
            double contentBottom = rowYPositions[total];
            if (!topAnchor && contentBottom < canvasHeight)
            {
                double shift = canvasHeight - contentBottom;
                for (int i = 0; i <= total; i++)
                    rowYPositions[i] += shift;
            }
        }

        return new LayoutResult(rowYPositions, extraRows, displayedBaseRows);
    }

    private static bool[] BuildEmptyArray(TerminalBuffer buffer, int totalRows, int extraRows, int scrollOffset, int baseRowCount)
    {
        var arr = new bool[totalRows];
        for (int row = 0; row < totalRows; row++)
            arr[row] = IsRowEmptyForDisplay(buffer, row, extraRows, scrollOffset, baseRowCount);
        return arr;
    }

    private static bool IsRowEmptyForDisplay(TerminalBuffer buffer, int displayRow, int extraRows, int scrollOffset, int baseRowCount)
    {
        int cols = buffer.Columns;
        for (int c = 0; c < cols; c++)
        {
            var cell = buffer.GetVisibleCell(displayRow, c, scrollOffset, baseRowCount + extraRows);
            if (cell.Character != ' ' && cell.Character != '\0' && cell.Character != '│')
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
            if (cell.Character != ' ' && cell.Character != '\0' && cell.Character != '│')
                return false;
            if (cell.BackgroundR != CellData.DefaultBgR || cell.BackgroundG != CellData.DefaultBgG || cell.BackgroundB != CellData.DefaultBgB)
                return false;
        }
        return true;
    }
}
