using System.Text;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using RaisinTerminal.Controls;
using RaisinTerminal.Core.Models;
using RaisinTerminal.Core.Terminal;
using Xunit;

namespace RaisinTerminal.Tests.UI;

public class TerminalCanvasRenderTests
{
    private static TerminalCanvas RenderCanvas(
        TerminalEmulator emulator,
        TerminalViewport? viewport,
        int widthCols,
        int heightRows)
    {
        var canvas = new TerminalCanvas
        {
            Emulator = emulator,
            Viewport = viewport,
            CompressEmptyLines = true
        };

        canvas.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        double w = canvas.CellWidth * widthCols;
        double h = canvas.CellHeight * heightRows;
        canvas.Arrange(new Rect(0, 0, w, h));

        var rtb = new RenderTargetBitmap(
            Math.Max(1, (int)w), Math.Max(1, (int)h), 96, 96, PixelFormats.Pbgra32);
        rtb.Render(canvas);
        return canvas;
    }

    private static bool IsRenderedRowEmpty(TerminalBuffer buffer, int row, int scrollOffset, int viewRows)
    {
        for (int c = 0; c < buffer.Columns; c++)
        {
            var cell = buffer.GetVisibleCell(row, c, scrollOffset, viewRows);
            if (cell.Character != ' ' && cell.Character != '\0')
                return false;
        }
        return true;
    }

    private static int CountLeadingEmpties(TerminalBuffer buffer, int canvasRows, int scrollOffset, int viewRows)
    {
        int count = 0;
        for (int row = 0; row < canvasRows; row++)
        {
            if (!IsRenderedRowEmpty(buffer, row, scrollOffset, viewRows)) break;
            count++;
        }
        return count;
    }

    private static int CountTrailingEmpties(TerminalBuffer buffer, int totalRendered, int scrollOffset, int viewRows)
    {
        int count = 0;
        for (int row = totalRendered - 1; row >= 0; row--)
        {
            if (!IsRenderedRowEmpty(buffer, row, scrollOffset, viewRows)) break;
            count++;
        }
        return count;
    }

    private static TerminalEmulator CreateWithScrollback(int cols, int rows, int totalLines)
    {
        var emulator = new TerminalEmulator(cols, rows);
        var sb = new StringBuilder();
        for (int i = 0; i < totalLines; i++)
        {
            if (i > 0) sb.Append("\r\n");
            sb.Append($"line-{i + 1:D4}");
        }
        emulator.Feed(Encoding.UTF8.GetBytes(sb.ToString()));
        return emulator;
    }

    // ─── Bottom gap tests ────────────────────────────────────────────

    /// <summary>
    /// Bug: canvasRows > bufferRows after splitter drag. The pinned pane must fill
    /// all canvas rows — no empty gap at the bottom.
    /// </summary>
    [StaFact]
    public void PinnedPane_CanvasTallerThanBuffer_NoBottomGap()
    {
        int bufferRows = 21;
        int canvasRows = 32;
        var emulator = CreateWithScrollback(80, bufferRows, 200);

        var viewport = new TerminalViewport { IsLive = false, ScrollOffset = canvasRows };
        var canvas = RenderCanvas(emulator, viewport, 80, canvasRows);

        int totalRendered = (canvas._rowYPositions?.Length ?? 1) - 1;
        Assert.True(totalRendered >= canvasRows,
            $"Bottom gap: rendered {totalRendered} rows but canvas has {canvasRows} " +
            $"(extraRows={canvas._extraRows}, baseRowCount={canvas._baseRowCount})");
    }

    /// <summary>
    /// Bug: canvasRows == bufferRows, but trailing empty rows get trimmed by
    /// compression. The topAnchor path must fill the gap with scrollback.
    /// </summary>
    [StaFact]
    public void PinnedPane_CompressionTrimsTrailing_NoBottomGap()
    {
        int bufferRows = 30;
        int canvasRows = 30;
        var emulator = CreateWithScrollback(80, bufferRows, 200);
        emulator.Feed(Encoding.UTF8.GetBytes("\x1b[21;1H\x1b[J"));

        var viewport = new TerminalViewport { IsLive = false, ScrollOffset = 0 };
        var canvas = RenderCanvas(emulator, viewport, 80, canvasRows);

        int totalRendered = (canvas._rowYPositions?.Length ?? 1) - 1;
        Assert.True(totalRendered >= canvasRows,
            $"Bottom gap: rendered {totalRendered} rows but canvas has {canvasRows} — " +
            $"compression trimmed trailing rows without filling from scrollback " +
            $"(extraRows={canvas._extraRows}, baseRowCount={canvas._baseRowCount})");
    }

    /// <summary>
    /// At max scroll offset with enlarged viewport, no bottom gap should appear.
    /// The scrollbar max should account for the enlarged viewport.
    /// </summary>
    [StaFact]
    public void PinnedPane_CanvasTallerThanBuffer_MaxScroll_NoBottomGap()
    {
        int bufferRows = 21;
        int canvasRows = 32;
        var emulator = CreateWithScrollback(80, bufferRows, 200);
        // scrollbackCount = 179

        int maxOffset = ViewportCalculator.MaxScrollOffset(bufferRows, canvasRows, emulator.Buffer.ScrollbackCount);
        var viewport = new TerminalViewport { IsLive = false, ScrollOffset = maxOffset };
        var canvas = RenderCanvas(emulator, viewport, 80, canvasRows);

        int totalRendered = (canvas._rowYPositions?.Length ?? 1) - 1;
        Assert.True(totalRendered >= canvasRows,
            $"Bottom gap at maxScroll: rendered {totalRendered} rows but canvas has {canvasRows} " +
            $"(maxOffset={maxOffset}, extraRows={canvas._extraRows})");
    }

    // ─── Leading empties tests ───────────────────────────────────────

    /// <summary>
    /// After initial split open: the first visible row in the pinned pane should
    /// have content, not be an empty line.
    /// </summary>
    [StaFact]
    public void PinnedPane_InitialOpen_NoLeadingEmpties()
    {
        int bufferRows = 21;
        int canvasRows = 32;
        var emulator = CreateWithScrollback(80, bufferRows, 200);

        int scrollOffset = ViewportCalculator.PinnedInitialOffset(bufferRows, canvasRows, emulator.Buffer.ScrollbackCount);
        var viewport = new TerminalViewport { IsLive = false, ScrollOffset = scrollOffset };
        var canvas = RenderCanvas(emulator, viewport, 80, canvasRows);

        int viewRows = canvas._baseRowCount + canvas._extraRows;
        int leading = CountLeadingEmpties(emulator.Buffer, canvasRows, scrollOffset, viewRows);
        Assert.Equal(0, leading);
    }

    /// <summary>
    /// After scrolling the pinned pane to max, no leading empties should appear.
    /// If the enlarged viewport (baseRowCount + extraRows) exceeds the content,
    /// the renderer or scrollbar must prevent empty top rows.
    /// </summary>
    [StaFact]
    public void PinnedPane_CanvasTallerThanBuffer_MaxScroll_NoLeadingEmpties()
    {
        int bufferRows = 21;
        int canvasRows = 32;
        var emulator = CreateWithScrollback(80, bufferRows, 200);

        int maxOffset = ViewportCalculator.MaxScrollOffset(bufferRows, canvasRows, emulator.Buffer.ScrollbackCount);
        var viewport = new TerminalViewport { IsLive = false, ScrollOffset = maxOffset };
        var canvas = RenderCanvas(emulator, viewport, 80, canvasRows);

        int viewRows = canvas._baseRowCount + canvas._extraRows;
        int leading = CountLeadingEmpties(emulator.Buffer, canvasRows, maxOffset, viewRows);
        Assert.Equal(0, leading);
    }

    /// <summary>
    /// Simulates splitter drag: canvas grows from 15 to 32 rows. The scroll offset
    /// is adjusted per OnPinnedCanvasSizeChanged logic. No empties at top or bottom.
    /// </summary>
    [StaFact]
    public void PinnedPane_SplitterDrag_GrowCanvas_NoEmpties()
    {
        int bufferRows = 21;
        int initialCanvasRows = 15;
        int finalCanvasRows = 32;
        var emulator = CreateWithScrollback(80, bufferRows, 200);

        int scrollOffset = ViewportCalculator.PinnedInitialOffset(bufferRows, initialCanvasRows, emulator.Buffer.ScrollbackCount);
        // Simulate OnPinnedCanvasSizeChanged: delta = finalCanvasRows - initialCanvasRows
        int delta = finalCanvasRows - initialCanvasRows;
        scrollOffset = Math.Max(0, scrollOffset - delta);

        var viewport = new TerminalViewport { IsLive = false, ScrollOffset = scrollOffset };
        var canvas = RenderCanvas(emulator, viewport, 80, finalCanvasRows);

        int totalRendered = (canvas._rowYPositions?.Length ?? 1) - 1;
        int viewRows = canvas._baseRowCount + canvas._extraRows;

        int leading = CountLeadingEmpties(emulator.Buffer, totalRendered, scrollOffset, viewRows);
        Assert.Equal(0, leading);

        Assert.True(totalRendered >= finalCanvasRows,
            $"Bottom gap after splitter drag: rendered {totalRendered} rows but canvas has {finalCanvasRows} " +
            $"(scrollOffset={scrollOffset}, extraRows={canvas._extraRows})");
    }

    // ─── Regression guards ───────────────────────────────────────────

    /// <summary>
    /// Live pane compression + extraRows must still work after the topAnchor fix.
    /// </summary>
    [StaFact]
    public void LivePane_ExtraRowsFromCompression_StillWorks()
    {
        int bufferRows = 30;
        int canvasRows = 30;
        var emulator = CreateWithScrollback(80, bufferRows, 200);
        emulator.Feed(Encoding.UTF8.GetBytes("\x1b[21;1H\x1b[J"));

        var canvas = RenderCanvas(emulator, null, 80, canvasRows);

        int totalRendered = (canvas._rowYPositions?.Length ?? 1) - 1;
        Assert.True(totalRendered >= canvasRows,
            $"Live pane rendered {totalRendered} rows but canvas has {canvasRows}");
    }
}
