namespace RaisinTerminal.Core.Terminal;

public static class ViewportCalculator
{
    public static int BaseRowCount(int bufferRows, int canvasRows)
        => Math.Min(bufferRows, canvasRows);

    public static int ViewOffset(int bufferRows, int canvasRows)
        => bufferRows - BaseRowCount(bufferRows, canvasRows);

    public static int MaxScrollOffset(int bufferRows, int canvasRows, int scrollbackCount)
        => Math.Max(0, scrollbackCount + bufferRows - canvasRows);

    public static int DisplayCursorRow(int bufferCursorRow, int viewOffset)
        => bufferCursorRow - viewOffset;

    public static int ExtraRowsFromCompression(
        double savedPixels,
        double cellHeight,
        double emptyRowScale,
        int scrollbackCount,
        int viewOffset,
        int scrollOffset)
    {
        if (savedPixels <= cellHeight)
            return 0;

        int availableAbove = scrollbackCount + viewOffset - scrollOffset;
        double compressedHeight = Math.Round(cellHeight * emptyRowScale);
        int maxExtra = (int)Math.Ceiling(savedPixels / compressedHeight);
        return Math.Min(maxExtra, Math.Max(0, availableAbove));
    }

    public static int PinnedInitialOffset(int bufferRows, int pinnedCanvasRows, int scrollbackCount)
    {
        int viewOffset = Math.Max(0, bufferRows - pinnedCanvasRows);
        int maxOffset = MaxScrollOffset(bufferRows, pinnedCanvasRows, scrollbackCount);
        return Math.Min(maxOffset, viewOffset + pinnedCanvasRows);
    }

    public static long AbsoluteRowBase(long totalLinesScrolled, int viewOffset, int scrollOffset, int extraRows)
        => totalLinesScrolled + viewOffset - scrollOffset - extraRows;
}
