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

    private static TerminalCanvas RenderCanvasWithHeight(
        TerminalEmulator emulator,
        TerminalViewport? viewport,
        int widthCols,
        double canvasHeight)
    {
        var canvas = new TerminalCanvas
        {
            Emulator = emulator,
            Viewport = viewport,
            CompressEmptyLines = true
        };

        canvas.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        double w = canvas.CellWidth * widthCols;
        canvas.Arrange(new Rect(0, 0, w, canvasHeight));

        var rtb = new RenderTargetBitmap(
            Math.Max(1, (int)w), Math.Max(1, (int)canvasHeight), 96, 96, PixelFormats.Pbgra32);
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

    // ─── Pinned pane scroll-down clipping fix ────────────────────────

    /// <summary>
    /// Bug: pinned pane at scrollOffset=0 with empty line compression. ExtraRows
    /// computed at compressed height push content below canvas boundary because the
    /// extra scrollback rows contain real content (full height). The bottom of the
    /// buffer becomes unreachable — the user cannot scroll down any further.
    /// After fix: extraRows are capped so the top-anchored layout fits within the canvas.
    /// </summary>
    [StaFact]
    public void PinnedPane_ExtraRowsDoNotClipBottomContent()
    {
        int bufferRows = 30;
        int canvasRows = 20;
        var emulator = CreateWithScrollback(80, bufferRows, 200);
        // Erase bottom third of buffer to create empty rows that trigger compression
        emulator.Feed(Encoding.UTF8.GetBytes("\x1b[21;1H\x1b[J"));

        var viewport = new TerminalViewport { IsLive = false, ScrollOffset = 0 };
        var canvas = RenderCanvas(emulator, viewport, 80, canvasRows);

        int totalRendered = (canvas._rowYPositions?.Length ?? 1) - 1;
        double totalHeight = canvas._rowYPositions![totalRendered];

        Assert.True(totalHeight <= canvas.ActualHeight + 0.5,
            $"Pinned pane layout height ({totalHeight:F1}) exceeds canvas ({canvas.ActualHeight:F1}) — " +
            $"bottom content is clipped and unreachable by scrolling " +
            $"(extraRows={canvas._extraRows}, baseRowCount={canvas._baseRowCount})");
    }

    /// <summary>
    /// Pinned pane at mid-scroll with compression: layout must not overflow canvas.
    /// </summary>
    [StaFact]
    public void PinnedPane_MidScroll_ExtraRowsDoNotOverflow()
    {
        int bufferRows = 30;
        int canvasRows = 15;
        var emulator = CreateWithScrollback(80, bufferRows, 200);
        // Create a mix of content and empty rows
        emulator.Feed(Encoding.UTF8.GetBytes("\x1b[10;1H\x1b[5M"));

        int maxOffset = ViewportCalculator.MaxScrollOffset(bufferRows, canvasRows, emulator.Buffer.ScrollbackCount);
        var viewport = new TerminalViewport { IsLive = false, ScrollOffset = maxOffset / 2 };
        var canvas = RenderCanvas(emulator, viewport, 80, canvasRows);

        int totalRendered = (canvas._rowYPositions?.Length ?? 1) - 1;
        double totalHeight = canvas._rowYPositions![totalRendered];

        Assert.True(totalHeight <= canvas.ActualHeight + 0.5,
            $"Pinned pane layout overflows at mid-scroll: height={totalHeight:F1}, canvas={canvas.ActualHeight:F1} " +
            $"(extraRows={canvas._extraRows})");
    }

    // ─── Sparse content / not-filled page tests ─────────────────────

    [StaFact]
    public void LivePane_SparseContent_EmptyScrollback_NoExtraRows()
    {
        // Bug: terminal with few content lines and empty scrollback
        // pulls empty scrollback as extra rows, pushing content down.
        int bufferRows = 30;
        int canvasRows = 30;
        var emulator = new TerminalEmulator(80, bufferRows);

        // Write content at row 0, then use ESC[S to scroll the screen,
        // pushing the content row + empty rows into scrollback.
        emulator.Feed(Encoding.UTF8.GetBytes("ContentRow"));
        // Scroll up 20 lines: row 0 ("ContentRow") → scrollback,
        // then 19 empty rows → scrollback. Screen becomes all empty.
        emulator.Feed(Encoding.UTF8.GetBytes("\x1b[20S"));
        // Write new content at the top
        emulator.Feed(Encoding.UTF8.GetBytes("\x1b[H")); // CUP 1;1
        emulator.Feed(Encoding.UTF8.GetBytes("NewLine1\r\nNewLine2\r\nNewLine3"));

        // Scrollback: 20 lines (1 content "ContentRow" + 19 empty)
        Assert.Equal(20, emulator.Buffer.ScrollbackCount);
        Assert.Equal(1, emulator.Buffer.EffectiveScrollbackCount);

        // Render as live viewport (scrollOffset=0)
        var canvas = RenderCanvas(emulator, null, 80, canvasRows);

        // Extra rows should be limited to meaningful scrollback only (1 line:
        // "ContentRow"). The 19 empty trailing scrollback lines are skipped.
        Assert.Equal(1, canvas._extraRows);
    }

    [StaFact]
    public void LivePane_SparseContent_TopAligned()
    {
        // After fix: content that doesn't fill the canvas should start
        // at the top, not float at the bottom.
        int bufferRows = 30;
        int canvasRows = 30;
        var emulator = new TerminalEmulator(80, bufferRows);
        emulator.Feed(Encoding.UTF8.GetBytes("Line1\r\nLine2\r\nLine3"));

        var canvas = RenderCanvas(emulator, null, 80, canvasRows);

        // Layout should be top-aligned: first row at y=0
        Assert.True(canvas._rowYPositions != null);
        Assert.True(canvas._rowYPositions![0] == 0,
            "Content should start at the top of the canvas");
    }

    [StaFact]
    public void LivePane_SparseContent_ScrollbarHiddenWhenEmpty()
    {
        // When all scrollback is empty, MaxScrollOffset with effective
        // scrollback should be 0 (no scrollbar needed).
        int bufferRows = 30;
        int canvasRows = 30;
        var emulator = new TerminalEmulator(80, bufferRows);

        // Push only empty rows to scrollback
        emulator.Feed(Encoding.UTF8.GetBytes("\x1b[10S")); // scroll up 10 empty lines

        int effectiveMax = ViewportCalculator.MaxScrollOffset(
            bufferRows, canvasRows, emulator.Buffer.EffectiveScrollbackCount);
        Assert.Equal(0, effectiveMax);

        // Standard max would be non-zero
        int standardMax = ViewportCalculator.MaxScrollOffset(
            bufferRows, canvasRows, emulator.Buffer.ScrollbackCount);
        Assert.True(standardMax > 0,
            "Standard max should be > 0 with raw scrollback count");
    }

    // ─── Split view regression: empty lines between content ─────────

    /// <summary>
    /// Regression: opening split view (live pane shorter than buffer) caused
    /// interior empty lines to appear at full height when compression made
    /// totalHeight &lt; canvasHeight. The live pane must bottom-align so
    /// compressed empty rows at the top are pushed off-screen.
    /// </summary>
    [StaFact]
    public void LivePane_SplitView_CompressedEmptyRows_BottomAligned()
    {
        int bufferRows = 30;
        int canvasRows = 15; // split view: canvas shorter than buffer
        var emulator = CreateWithScrollback(80, bufferRows, 200);
        // Erase middle rows to create empty interior rows
        emulator.Feed(Encoding.UTF8.GetBytes("\x1b[10;1H\x1b[10M"));

        var viewport = new TerminalViewport { IsLive = true, ScrollOffset = 0 };
        var canvas = RenderCanvas(emulator, viewport, 80, canvasRows);

        int totalRendered = (canvas._rowYPositions?.Length ?? 1) - 1;
        double lastRowEnd = canvas._rowYPositions![totalRendered];

        // Content should be bottom-aligned (last row ends at canvas height)
        Assert.Equal(canvas.ActualHeight, lastRowEnd, 0.5);
    }

    /// <summary>
    /// Sparse content with buffer smaller than canvas: content should be
    /// top-aligned (handled by TerminalCanvas choosing ComputeRowYPositions).
    /// </summary>
    [StaFact]
    public void LivePane_SmallBuffer_TopAligned()
    {
        int bufferRows = 5;
        int canvasRows = 30;
        var emulator = new TerminalEmulator(80, bufferRows);
        emulator.Feed(Encoding.UTF8.GetBytes("Line1\r\nLine2\r\nLine3"));

        var canvas = RenderCanvas(emulator, null, 80, canvasRows);

        Assert.True(canvas._rowYPositions != null);
        Assert.True(canvas._rowYPositions![0] == 0,
            "Small buffer content should start at the top of the canvas");
    }

    // ─── Full buffer: no gaps at bottom, including when scrolled back ──

    /// <summary>
    /// A full terminal (content fills more than one screen with scrollback)
    /// must have no gap at the bottom — the last rendered row must end at
    /// canvas height. Tests both at scroll offset 0 (live position) and
    /// scrolled back to various offsets.
    /// </summary>
    [StaFact]
    public void LivePane_FullBuffer_NoBottomGap_AtCurrentPosition()
    {
        int bufferRows = 30;
        int canvasRows = 30;
        var emulator = CreateWithScrollback(80, bufferRows, 200);

        var canvas = RenderCanvas(emulator, null, 80, canvasRows);

        int totalRendered = (canvas._rowYPositions?.Length ?? 1) - 1;
        double lastRowEnd = canvas._rowYPositions![totalRendered];

        Assert.Equal(canvas.ActualHeight, lastRowEnd, 0.5);
    }

    [StaFact]
    public void LivePane_FullBuffer_NoBottomGap_ScrolledBackPartially()
    {
        int bufferRows = 30;
        int canvasRows = 30;
        var emulator = CreateWithScrollback(80, bufferRows, 200);

        int maxOffset = ViewportCalculator.MaxScrollOffset(bufferRows, canvasRows, emulator.Buffer.ScrollbackCount);
        int scrollOffset = maxOffset / 2;
        var viewport = new TerminalViewport { IsLive = true, ScrollOffset = scrollOffset };
        viewport.UserScrolledBack = true;
        var canvas = RenderCanvas(emulator, viewport, 80, canvasRows);

        int totalRendered = (canvas._rowYPositions?.Length ?? 1) - 1;
        double lastRowEnd = canvas._rowYPositions![totalRendered];

        Assert.Equal(canvas.ActualHeight, lastRowEnd, 0.5);
    }

    [StaFact]
    public void LivePane_FullBuffer_NoBottomGap_ScrolledBackToTop()
    {
        int bufferRows = 30;
        int canvasRows = 30;
        var emulator = CreateWithScrollback(80, bufferRows, 200);

        int maxOffset = ViewportCalculator.MaxScrollOffset(bufferRows, canvasRows, emulator.Buffer.ScrollbackCount);
        var viewport = new TerminalViewport { IsLive = true, ScrollOffset = maxOffset };
        viewport.UserScrolledBack = true;
        var canvas = RenderCanvas(emulator, viewport, 80, canvasRows);

        int totalRendered = (canvas._rowYPositions?.Length ?? 1) - 1;
        double lastRowEnd = canvas._rowYPositions![totalRendered];

        Assert.Equal(canvas.ActualHeight, lastRowEnd, 0.5);
    }

    /// <summary>
    /// Full buffer with some interior empty rows (compression active).
    /// Even with compression, the last row must end at canvas height —
    /// no visible gap at the bottom.
    /// </summary>
    [StaFact]
    public void LivePane_FullBuffer_WithCompression_NoBottomGap()
    {
        int bufferRows = 30;
        int canvasRows = 30;
        var emulator = CreateWithScrollback(80, bufferRows, 200);
        // Delete 10 lines in the middle to create compressible empty rows
        emulator.Feed(Encoding.UTF8.GetBytes("\x1b[10;1H\x1b[10M"));

        var canvas = RenderCanvas(emulator, null, 80, canvasRows);

        int totalRendered = (canvas._rowYPositions?.Length ?? 1) - 1;
        double lastRowEnd = canvas._rowYPositions![totalRendered];

        Assert.Equal(canvas.ActualHeight, lastRowEnd, 0.5);
    }

    [StaFact]
    public void LivePane_FullBuffer_WithCompression_NoBottomGap_ScrolledBack()
    {
        int bufferRows = 30;
        int canvasRows = 30;
        var emulator = CreateWithScrollback(80, bufferRows, 200);
        emulator.Feed(Encoding.UTF8.GetBytes("\x1b[10;1H\x1b[10M"));

        int maxOffset = ViewportCalculator.MaxScrollOffset(bufferRows, canvasRows, emulator.Buffer.ScrollbackCount);
        var viewport = new TerminalViewport { IsLive = true, ScrollOffset = maxOffset / 2 };
        viewport.UserScrolledBack = true;
        var canvas = RenderCanvas(emulator, viewport, 80, canvasRows);

        int totalRendered = (canvas._rowYPositions?.Length ?? 1) - 1;
        double lastRowEnd = canvas._rowYPositions![totalRendered];

        Assert.Equal(canvas.ActualHeight, lastRowEnd, 0.5);
    }

    /// <summary>
    /// Fresh terminal: only a few lines of content, no scrollback.
    /// Content should start at the top (no gap above the first line).
    /// </summary>
    [StaFact]
    public void LivePane_SparseBuffer_NoGapAtTop()
    {
        int bufferRows = 30;
        int canvasRows = 30;
        var emulator = new TerminalEmulator(80, bufferRows);
        emulator.Feed(Encoding.UTF8.GetBytes("Line1\r\nLine2\r\nLine3\r\n> "));

        var canvas = RenderCanvas(emulator, null, 80, canvasRows);

        Assert.True(canvas._rowYPositions![0] >= 0,
            $"First row Y should not be negative (was {canvas._rowYPositions[0]})");
        Assert.True(canvas._rowYPositions![0] < canvas.CellHeight,
            $"Content should start near the top, not at Y={canvas._rowYPositions[0]}");
    }

    // ─── Split view toggle: buffer must not be resized ──────────────

    /// <summary>
    /// Regression: opening split view triggered OnCanvasSizeChanged which
    /// called _vm.Resize(), pushing content to scrollback and corrupting
    /// both panes. The buffer must stay the same size during split toggle.
    /// This test verifies that rendering with a smaller canvas does NOT
    /// require the buffer to be resized — both panes can share the same
    /// unchanged buffer via viewports.
    /// </summary>
    [StaFact]
    public void SplitView_BufferUnchanged_PinnedShowsTop_LiveShowsBottom()
    {
        int bufferRows = 30;
        var emulator = new TerminalEmulator(80, bufferRows);
        // Write 5 lines at the top, rest empty (simulates /clear scenario)
        emulator.Feed(Encoding.UTF8.GetBytes("Header1\r\nHeader2\r\nHeader3\r\n/clear\r\n> "));

        int originalRows = emulator.Buffer.Rows;
        int originalScrollback = emulator.Buffer.ScrollbackCount;

        // Simulate split: render pinned pane (top) and live pane (bottom)
        // with smaller canvas, WITHOUT resizing the buffer
        int pinnedCanvasRows = 15;
        int liveCanvasRows = 15;

        int pinnedOffset = ViewportCalculator.PinnedInitialOffset(
            bufferRows, pinnedCanvasRows, emulator.Buffer.ScrollbackCount);
        var pinnedViewport = new TerminalViewport { IsLive = false, ScrollOffset = pinnedOffset };
        var pinnedCanvas = RenderCanvas(emulator, pinnedViewport, 80, pinnedCanvasRows);

        var liveViewport = new TerminalViewport { IsLive = true, ScrollOffset = 0 };
        var liveCanvas = RenderCanvas(emulator, liveViewport, 80, liveCanvasRows);

        // Buffer must NOT have been resized
        Assert.Equal(originalRows, emulator.Buffer.Rows);
        Assert.Equal(originalScrollback, emulator.Buffer.ScrollbackCount);

        // Pinned pane should show content (first row has "Header1")
        var pinnedCell = emulator.Buffer.GetVisibleCell(0, 0, pinnedOffset,
            Math.Min(bufferRows, pinnedCanvasRows));
        Assert.Equal('H', pinnedCell.Character);

        // After closing split: render full canvas again, no duplication
        var fullCanvas = RenderCanvas(emulator, null, 80, bufferRows);
        Assert.Equal(originalRows, emulator.Buffer.Rows);
        Assert.Equal(originalScrollback, emulator.Buffer.ScrollbackCount);
    }

    // ─── Gap-at-top regression: partial content with scrollback ──────

    /// <summary>
    /// Bug: when the buffer is partially filled (content doesn't reach the
    /// last row) but scrollback exists, the compression path computes
    /// extraRows > 0 from scrollback. But the extras + displayed base rows
    /// don't fill the canvas. ComputeLayout bottom-aligns, pushing content
    /// to the bottom with a visible gap at the top.
    ///
    /// The correct behavior: content should start at the top of the canvas.
    /// If total content height is less than the canvas, the gap belongs at
    /// the bottom (after the cursor), not at the top.
    /// </summary>
    [StaFact]
    public void LivePane_PartialContent_WithScrollback_NoGapAtTop()
    {
        int bufferRows = 40;
        int canvasRows = 40;
        var emulator = new TerminalEmulator(80, bufferRows);

        // Write 55 lines: 15 go to scrollback, buffer rows 0-39 get lines 16-55
        var sb = new StringBuilder();
        for (int i = 0; i < 55; i++)
        {
            if (i > 0) sb.Append("\r\n");
            sb.Append($"line-{i + 1:D4}");
        }
        emulator.Feed(Encoding.UTF8.GetBytes(sb.ToString()));
        Assert.True(emulator.Buffer.ScrollbackCount > 0,
            "Test setup: need scrollback for this scenario");

        // Erase from row 20 downward, leaving content in rows 0-19, empty 20-39
        emulator.Feed(Encoding.UTF8.GetBytes("\x1b[21;1H\x1b[J"));

        var canvas = RenderCanvas(emulator, null, 80, canvasRows);

        // The first rendered row must start at or near Y=0 — no gap at top
        double firstRowY = canvas._rowYPositions![0];
        Assert.True(firstRowY <= 0,
            $"Gap at top: first row starts at Y={firstRowY}. " +
            $"Content should be top-aligned when it doesn't fill the canvas. " +
            $"(extraRows={canvas._extraRows}, baseRowCount={canvas._baseRowCount})");

        // With 15 scrollback lines available, the canvas should be nearly filled —
        // gap at bottom must be less than one cell height (rounding from compression)
        int totalRendered = (canvas._rowYPositions?.Length ?? 1) - 1;
        double lastRowEnd = canvas._rowYPositions![totalRendered];
        double bottomGap = canvas.ActualHeight - lastRowEnd;
        Assert.True(bottomGap < canvas.CellHeight,
            $"Bottom gap too large: {bottomGap}px (cellHeight={canvas.CellHeight}). " +
            $"extraRows={canvas._extraRows}, totalRendered={totalRendered}");
    }

    /// <summary>
    /// Same scenario as above, but after scrolling up 2 "wheel" increments
    /// (scrollOffset = 6). The content should still start at the top.
    /// In practice, scrolling up currently "fixes" the layout by changing
    /// the code path — this test verifies the fix is stable across scroll
    /// positions.
    /// </summary>
    [StaFact]
    public void LivePane_PartialContent_WithScrollback_ScrolledUp_NoGapAtTop()
    {
        int bufferRows = 40;
        int canvasRows = 40;
        var emulator = new TerminalEmulator(80, bufferRows);

        var sb = new StringBuilder();
        for (int i = 0; i < 55; i++)
        {
            if (i > 0) sb.Append("\r\n");
            sb.Append($"line-{i + 1:D4}");
        }
        emulator.Feed(Encoding.UTF8.GetBytes(sb.ToString()));
        emulator.Feed(Encoding.UTF8.GetBytes("\x1b[21;1H\x1b[J"));

        // Scroll up 2 wheel increments (3 lines each = offset 6)
        var viewport = new TerminalViewport { IsLive = true, ScrollOffset = 6 };
        viewport.UserScrolledBack = true;
        var canvas = RenderCanvas(emulator, viewport, 80, canvasRows);

        double firstRowY = canvas._rowYPositions![0];
        Assert.True(firstRowY <= 0,
            $"Gap at top after scroll: first row Y={firstRowY}. " +
            $"(extraRows={canvas._extraRows}, baseRowCount={canvas._baseRowCount})");
    }

    /// <summary>
    /// Simulates a restored session: a few lines of shell output (Windows
    /// banner + command), then Claude Code TUI output filling ~25 rows,
    /// with trailing empty rows. Scrollback has content from the prior
    /// session. Content must start at the top.
    /// </summary>
    [StaFact]
    public void LivePane_RestoredSession_PartialContent_NoGapAtTop()
    {
        int bufferRows = 40;
        int canvasRows = 40;
        var emulator = new TerminalEmulator(80, bufferRows);

        // Simulate prior session: write enough to create scrollback
        var sb = new StringBuilder();
        for (int i = 0; i < 50; i++)
        {
            if (i > 0) sb.Append("\r\n");
            sb.Append($"prior-{i + 1:D4}");
        }
        emulator.Feed(Encoding.UTF8.GetBytes(sb.ToString()));

        // Simulate /clear: erase screen and move cursor home
        emulator.Feed(Encoding.UTF8.GetBytes("\x1b[2J\x1b[H"));

        // Write new session content (~8 lines, like Windows banner + claude command)
        emulator.Feed(Encoding.UTF8.GetBytes(
            "Microsoft Windows [Version 10.0.26200.8246]\r\n" +
            "(c) Microsoft Corporation. All rights reserved.\r\n" +
            "\r\n" +
            "D:\\Sources>claude --resume \"RT 1\"\r\n" +
            "  Claude Code v2.1.119\r\n" +
            "  Opus 4.6\r\n" +
            "\r\n" +
            "> "));

        int scrollbackCount = emulator.Buffer.ScrollbackCount;
        Assert.True(scrollbackCount > 0,
            "Test setup: restored session should have scrollback from prior content");

        var canvas = RenderCanvas(emulator, null, 80, canvasRows);

        double firstRowY = canvas._rowYPositions![0];
        Assert.True(firstRowY <= 0,
            $"Gap at top in restored session: first row Y={firstRowY}. " +
            $"Content should start at top when it doesn't fill the canvas. " +
            $"(scrollback={scrollbackCount}, extraRows={canvas._extraRows}, " +
            $"baseRowCount={canvas._baseRowCount})");

        // With scrollback available, canvas should be nearly filled —
        // gap at bottom must be less than one cell height
        int totalRendered = (canvas._rowYPositions?.Length ?? 1) - 1;
        double lastRowEnd = canvas._rowYPositions![totalRendered];
        double bottomGap = canvas.ActualHeight - lastRowEnd;
        Assert.True(bottomGap < canvas.CellHeight,
            $"Bottom gap too large: {bottomGap}px (cellHeight={canvas.CellHeight}). " +
            $"extraRows={canvas._extraRows}, totalRendered={totalRendered}");
    }

    /// <summary>
    /// Bug: layout is inconsistent between scroll positions. At offset 0,
    /// content is pushed to the bottom (gap at top). After scrolling up
    /// past the scrollback count, extraRows drops to 0 and content snaps
    /// to the top. The same content should render at the same position
    /// regardless of scroll offset — both should be top-aligned when
    /// content doesn't fill the canvas.
    /// </summary>
    [StaFact]
    public void LivePane_LayoutConsistent_AcrossScrollPositions()
    {
        int bufferRows = 40;
        int canvasRows = 40;
        var emulator = new TerminalEmulator(80, bufferRows);

        // Create a small amount of scrollback (5 lines)
        var sb = new StringBuilder();
        for (int i = 0; i < 45; i++)
        {
            if (i > 0) sb.Append("\r\n");
            sb.Append($"line-{i + 1:D4}");
        }
        emulator.Feed(Encoding.UTF8.GetBytes(sb.ToString()));

        // Erase from row 20 down: content in rows 0-19, empty 20-39
        emulator.Feed(Encoding.UTF8.GetBytes("\x1b[21;1H\x1b[J"));

        int scrollbackCount = emulator.Buffer.ScrollbackCount;

        // Render at scroll offset 0 (live position)
        var canvas0 = RenderCanvas(emulator, null, 80, canvasRows);
        double firstRowY_offset0 = canvas0._rowYPositions![0];

        // Render scrolled past scrollback: extraRows should drop to 0
        int scrollPastScrollback = scrollbackCount + 3;
        var viewport = new TerminalViewport
        {
            IsLive = true,
            ScrollOffset = scrollPastScrollback,
            UserScrolledBack = true
        };
        var canvasScrolled = RenderCanvas(emulator, viewport, 80, canvasRows);
        double firstRowY_scrolled = canvasScrolled._rowYPositions![0];

        // Both positions should be consistent — content at the top
        // The bug: at offset 0, firstRowY > 0 (gap at top), but after
        // scrolling, firstRowY == 0 (content snaps to top)
        Assert.True(
            Math.Abs(firstRowY_offset0 - firstRowY_scrolled) < canvasScrolled.CellHeight,
            $"Layout inconsistency: at offset 0, first row Y={firstRowY_offset0} " +
            $"(extraRows={canvas0._extraRows}); " +
            $"at offset {scrollPastScrollback}, first row Y={firstRowY_scrolled} " +
            $"(extraRows={canvasScrolled._extraRows}). " +
            $"Content jumps when scrolling — should be stable.");
    }

    // ─── Split view with partial content: gap behavior in both panes ─

    /// <summary>
    /// Split view with partial content and scrollback. The buffer has
    /// content in the first ~20 rows out of 40, with scrollback from
    /// prior output. Opening a split gives each pane ~20 rows of canvas.
    ///
    /// Pinned pane (top): should show scrollback + top content, top-aligned.
    /// Live pane (bottom): should show content without gap at top.
    /// Buffer must not be resized.
    /// </summary>
    [StaFact]
    public void SplitView_PartialContent_WithScrollback_NoGapInEitherPane()
    {
        int bufferRows = 40;
        var emulator = new TerminalEmulator(80, bufferRows);

        // Write 55 lines: 15 go to scrollback, buffer has lines 16-55
        var sb = new StringBuilder();
        for (int i = 0; i < 55; i++)
        {
            if (i > 0) sb.Append("\r\n");
            sb.Append($"line-{i + 1:D4}");
        }
        emulator.Feed(Encoding.UTF8.GetBytes(sb.ToString()));

        // Erase from row 20 down: content in rows 0-19, empty 20-39
        emulator.Feed(Encoding.UTF8.GetBytes("\x1b[21;1H\x1b[J"));

        int originalRows = emulator.Buffer.Rows;
        int scrollbackCount = emulator.Buffer.ScrollbackCount;
        Assert.True(scrollbackCount > 0);

        // Split: each pane gets ~20 rows
        int pinnedCanvasRows = 20;
        int liveCanvasRows = 20;

        // Pinned pane: scrolled to show scrollback + top content
        int pinnedOffset = ViewportCalculator.PinnedInitialOffset(
            bufferRows, pinnedCanvasRows, scrollbackCount);
        var pinnedViewport = new TerminalViewport
        {
            IsLive = false,
            ScrollOffset = pinnedOffset
        };
        var pinnedCanvas = RenderCanvas(emulator, pinnedViewport, 80, pinnedCanvasRows);

        // Live pane: at scroll offset 0
        var liveViewport = new TerminalViewport { IsLive = true, ScrollOffset = 0 };
        var liveCanvas = RenderCanvas(emulator, liveViewport, 80, liveCanvasRows);

        // Buffer must not be resized
        Assert.Equal(originalRows, emulator.Buffer.Rows);

        // Pinned pane: content should be top-aligned (no gap at top)
        double pinnedFirstRowY = pinnedCanvas._rowYPositions![0];
        Assert.True(pinnedFirstRowY <= 0,
            $"Pinned pane gap at top: first row Y={pinnedFirstRowY}. " +
            $"(extraRows={pinnedCanvas._extraRows}, baseRowCount={pinnedCanvas._baseRowCount})");

        // Live pane: content should start at top, no gap
        double liveFirstRowY = liveCanvas._rowYPositions![0];
        Assert.True(liveFirstRowY <= 0,
            $"Live pane gap at top in split view: first row Y={liveFirstRowY}. " +
            $"(extraRows={liveCanvas._extraRows}, baseRowCount={liveCanvas._baseRowCount})");
    }

    /// <summary>
    /// Split view with a restored session (small content, scrollback from
    /// prior session). Simulates: prior output → /clear → fresh shell
    /// banner → open split. Both panes must render without gaps.
    /// </summary>
    [StaFact]
    public void SplitView_RestoredSession_NoGapInEitherPane()
    {
        int bufferRows = 40;
        var emulator = new TerminalEmulator(80, bufferRows);

        // Prior session content → creates scrollback
        var sb = new StringBuilder();
        for (int i = 0; i < 50; i++)
        {
            if (i > 0) sb.Append("\r\n");
            sb.Append($"prior-{i + 1:D4}");
        }
        emulator.Feed(Encoding.UTF8.GetBytes(sb.ToString()));

        // /clear: erase screen, cursor home
        emulator.Feed(Encoding.UTF8.GetBytes("\x1b[2J\x1b[H"));

        // Fresh shell content (~8 lines)
        emulator.Feed(Encoding.UTF8.GetBytes(
            "Microsoft Windows [Version 10.0.26200.8246]\r\n" +
            "(c) Microsoft Corporation. All rights reserved.\r\n" +
            "\r\n" +
            "D:\\Sources>claude --resume \"RT 1\"\r\n" +
            "  Claude Code v2.1.119\r\n" +
            "  Opus 4.6\r\n" +
            "\r\n" +
            "> "));

        int scrollbackCount = emulator.Buffer.ScrollbackCount;
        Assert.True(scrollbackCount > 0);

        // Open split: 20 rows each
        int pinnedCanvasRows = 20;
        int liveCanvasRows = 20;

        int pinnedOffset = ViewportCalculator.PinnedInitialOffset(
            bufferRows, pinnedCanvasRows, scrollbackCount);
        var pinnedViewport = new TerminalViewport
        {
            IsLive = false,
            ScrollOffset = pinnedOffset
        };
        var pinnedCanvas = RenderCanvas(emulator, pinnedViewport, 80, pinnedCanvasRows);

        var liveViewport = new TerminalViewport { IsLive = true, ScrollOffset = 0 };
        var liveCanvas = RenderCanvas(emulator, liveViewport, 80, liveCanvasRows);

        // Pinned pane: should show scrollback content, top-aligned
        double pinnedFirstRowY = pinnedCanvas._rowYPositions![0];
        Assert.True(pinnedFirstRowY <= 0,
            $"Pinned pane gap at top: first row Y={pinnedFirstRowY}. " +
            $"(scrollback={scrollbackCount}, extraRows={pinnedCanvas._extraRows})");

        // Live pane: should show the shell banner at the top
        double liveFirstRowY = liveCanvas._rowYPositions![0];
        Assert.True(liveFirstRowY <= 0,
            $"Live pane gap at top in split: first row Y={liveFirstRowY}. " +
            $"(scrollback={scrollbackCount}, extraRows={liveCanvas._extraRows})");
    }

    /// <summary>
    /// After closing the split, the full canvas must also render without
    /// a gap at the top. Verifies that the split toggle itself doesn't
    /// leave layout in a broken state.
    /// </summary>
    [StaFact]
    public void SplitView_AfterClose_PartialContent_NoGapAtTop()
    {
        int bufferRows = 40;
        int canvasRows = 40;
        var emulator = new TerminalEmulator(80, bufferRows);

        // Create scrollback + partial content
        var sb = new StringBuilder();
        for (int i = 0; i < 55; i++)
        {
            if (i > 0) sb.Append("\r\n");
            sb.Append($"line-{i + 1:D4}");
        }
        emulator.Feed(Encoding.UTF8.GetBytes(sb.ToString()));
        emulator.Feed(Encoding.UTF8.GetBytes("\x1b[21;1H\x1b[J"));

        // Simulate split open: render both panes at half size
        int halfRows = 20;
        var pinnedViewport = new TerminalViewport { IsLive = false, ScrollOffset = 10 };
        RenderCanvas(emulator, pinnedViewport, 80, halfRows);
        var liveViewport = new TerminalViewport { IsLive = true, ScrollOffset = 0 };
        RenderCanvas(emulator, liveViewport, 80, halfRows);

        // Simulate split close: render full canvas again
        var fullCanvas = RenderCanvas(emulator, null, 80, canvasRows);

        double firstRowY = fullCanvas._rowYPositions![0];
        Assert.True(firstRowY <= 0,
            $"Gap at top after closing split: first row Y={firstRowY}. " +
            $"(extraRows={fullCanvas._extraRows}, baseRowCount={fullCanvas._baseRowCount})");
    }

    // ─── Full buffer with interior empties: gap-at-top regression ─────

    /// <summary>
    /// Full buffer (no trailing empties), no scrollback to fill compression
    /// gaps. Compression shrinks interior empties → bottom-aligned layout
    /// anchors the last row at canvas bottom → gap appears at top.
    /// This is acceptable: compressed empties are more important than a
    /// gap-free top edge. The last row must still end at canvas height
    /// (no bottom gap), and interior empties must be compressed.
    /// </summary>
    [StaFact]
    public void LivePane_FullBuffer_InteriorEmpties_NoBottomGap_Compressed()
    {
        int bufferRows = 43;
        int canvasRows = 43;
        var emulator = new TerminalEmulator(80, bufferRows);

        var sb = new StringBuilder();
        for (int i = 0; i < 36; i++)
        {
            if (i > 0) sb.Append("\r\n");
            sb.Append($"output-row-{i + 1:D3}");
        }
        emulator.Feed(Encoding.UTF8.GetBytes(sb.ToString()));

        emulator.Feed(Encoding.UTF8.GetBytes("\r\n\r\n\r\n"));

        emulator.Feed(Encoding.UTF8.GetBytes("━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━\r\n"));
        emulator.Feed(Encoding.UTF8.GetBytes("❯ next\r\n"));
        emulator.Feed(Encoding.UTF8.GetBytes("━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━\r\n"));
        emulator.Feed(Encoding.UTF8.GetBytes("* Working... (5s)\r\n"));
        emulator.Feed(Encoding.UTF8.GetBytes("━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━"));

        var canvas = RenderCanvas(emulator, null, 80, canvasRows);

        int totalRendered = canvas._rowYPositions!.Length - 1;
        double lastRowEnd = canvas._rowYPositions[totalRendered];
        Assert.True(Math.Abs(lastRowEnd - canvas.ActualHeight) < 0.5,
            $"Bottom gap: last row ends at {lastRowEnd:F1}, canvas height {canvas.ActualHeight:F1}");

        bool hasCompressedRow = false;
        for (int i = 0; i < totalRendered; i++)
        {
            double h = canvas._rowYPositions[i + 1] - canvas._rowYPositions[i];
            if (h < canvas.CellHeight - 0.5)
                hasCompressedRow = true;
        }
        Assert.True(hasCompressedRow, "Interior empties should be compressed");
    }

    /// <summary>
    /// Same as above but with scrollback present. The compression gap at
    /// top should be filled by pulling scrollback rows from above, or by
    /// not compressing when the buffer is full.
    /// </summary>
    [StaFact]
    public void LivePane_FullBuffer_WithScrollback_InteriorEmpties_NoGapAtTop()
    {
        int bufferRows = 43;
        int canvasRows = 43;
        var emulator = new TerminalEmulator(80, bufferRows);

        // Create scrollback first
        var sb = new StringBuilder();
        for (int i = 0; i < 60; i++)
        {
            if (i > 0) sb.Append("\r\n");
            sb.Append($"scrollback-{i + 1:D3}");
        }
        emulator.Feed(Encoding.UTF8.GetBytes(sb.ToString()));
        Assert.True(emulator.Buffer.ScrollbackCount > 0);

        // Clear and write TUI content that fills ALL rows
        emulator.Feed(Encoding.UTF8.GetBytes("\x1b[2J\x1b[H"));

        sb.Clear();
        for (int i = 0; i < 36; i++)
        {
            if (i > 0) sb.Append("\r\n");
            sb.Append($"tui-row-{i + 1:D3}");
        }
        emulator.Feed(Encoding.UTF8.GetBytes(sb.ToString()));

        // 3 \r\n = move to row 36, then 37, then 38 — leaving rows 36-37 empty
        emulator.Feed(Encoding.UTF8.GetBytes("\r\n\r\n\r\n"));

        // Status area filling rows 38-42
        emulator.Feed(Encoding.UTF8.GetBytes("━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━\r\n"));
        emulator.Feed(Encoding.UTF8.GetBytes("❯ next\r\n"));
        emulator.Feed(Encoding.UTF8.GetBytes("━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━\r\n"));
        emulator.Feed(Encoding.UTF8.GetBytes("* Working... (5s)\r\n"));
        emulator.Feed(Encoding.UTF8.GetBytes("━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━"));

        var canvas = RenderCanvas(emulator, null, 80, canvasRows);

        double firstRowY = canvas._rowYPositions![0];
        Assert.True(firstRowY <= 0,
            $"Gap at top with scrollback: first row Y={firstRowY:F1}. " +
            $"Scrollback should fill the compression gap. " +
            $"(extraRows={canvas._extraRows}, totalRendered={(canvas._rowYPositions.Length - 1)}, " +
            $"baseRowCount={canvas._baseRowCount})");
    }

    // ─── Realistic Claude TUI with box-drawing table ────────────────────

    /// <summary>
    /// Reproduces a real Claude Code TUI screen: prior scrollback from a
    /// long session, then the current screen filled with analysis output
    /// including a box-drawing table, verdict text, and the TUI status bar
    /// with prompt. Interior empty rows appear between text sections
    /// (between quoted text and "Analysis:", between table and "Verdict:",
    /// etc.). The last row has content (status bar).
    ///
    /// This catches any gap issues specific to box-drawing content and
    /// realistic TUI layouts vs the simplified test patterns.
    /// </summary>
    [StaFact]
    public void LivePane_ClaudeTUI_TableOutput_NoGapAtTop()
    {
        int bufferRows = 43;
        int canvasRows = 43;
        int cols = 155;
        var emulator = new TerminalEmulator(cols, bufferRows);

        // Prior session output → creates scrollback with real (non-empty) content
        var sb = new StringBuilder();
        for (int i = 0; i < 80; i++)
        {
            if (i > 0) sb.Append("\r\n");
            sb.Append($"prior-output-line-{i + 1:D4}");
        }
        emulator.Feed(Encoding.UTF8.GetBytes(sb.ToString()));
        Assert.True(emulator.Buffer.ScrollbackCount > 0);

        // Clear screen (simulates Claude TUI full repaint)
        emulator.Feed(Encoding.UTF8.GetBytes("\x1b[2J\x1b[H"));

        emulator.Feed(Encoding.UTF8.GetBytes(BuildTuiTableScreen(cols)));

        // Verify setup: last row has content
        var buffer = emulator.Buffer;
        bool lastRowEmpty = true;
        for (int c = 0; c < Math.Min(10, buffer.Columns); c++)
        {
            var cell = buffer.GetCell(buffer.Rows - 1, c);
            if (cell.Character != ' ' && cell.Character != '\0')
            {
                lastRowEmpty = false;
                break;
            }
        }
        Assert.False(lastRowEmpty,
            "Test setup: last row must have content (status bar)");

        var canvas = RenderCanvas(emulator, null, cols, canvasRows);

        // No gap at top
        double firstRowY = canvas._rowYPositions![0];
        Assert.True(firstRowY <= 0,
            $"Gap at top with table content: first row Y={firstRowY:F1}. " +
            $"(extraRows={canvas._extraRows}, baseRowCount={canvas._baseRowCount})");

        // No excessive gap at bottom
        int totalRendered = (canvas._rowYPositions.Length) - 1;
        double lastRowEnd = canvas._rowYPositions[totalRendered];
        double bottomGap = canvas.ActualHeight - lastRowEnd;
        Assert.True(bottomGap < canvas.CellHeight,
            $"Bottom gap with table content: {bottomGap:F1}px (cellHeight={canvas.CellHeight:F1}). " +
            $"(extraRows={canvas._extraRows}, totalRendered={totalRendered})");
    }

    /// <summary>
    /// Same table layout but WITHOUT scrollback (no prior session).
    /// With no scrollback to fill the compression gap, a top gap is
    /// acceptable. Verify no bottom gap and that compression is active.
    /// </summary>
    [StaFact]
    public void LivePane_ClaudeTUI_TableOutput_NoScrollback_NoBottomGap_Compressed()
    {
        int bufferRows = 43;
        int canvasRows = 43;
        int cols = 155;
        var emulator = new TerminalEmulator(cols, bufferRows);

        emulator.Feed(Encoding.UTF8.GetBytes(BuildTuiTableScreen(cols)));
        Assert.Equal(0, emulator.Buffer.ScrollbackCount);

        var canvas = RenderCanvas(emulator, null, cols, canvasRows);

        int totalRendered = canvas._rowYPositions!.Length - 1;
        double lastRowEnd = canvas._rowYPositions[totalRendered];
        Assert.True(Math.Abs(lastRowEnd - canvas.ActualHeight) < 0.5,
            $"Bottom gap: last row ends at {lastRowEnd:F1}, canvas height {canvas.ActualHeight:F1}");

        bool hasCompressedRow = false;
        for (int i = 0; i < totalRendered; i++)
        {
            double h = canvas._rowYPositions[i + 1] - canvas._rowYPositions[i];
            if (h < canvas.CellHeight - 0.5)
                hasCompressedRow = true;
        }
        Assert.True(hasCompressedRow, "Interior empties should be compressed");
    }

    // ─── Full buffer: compression must remain active even without extra rows ──

    /// <summary>
    /// Bug: Claude Code TUI fills the entire buffer (no trailing empties)
    /// and either there's no scrollback or the newest scrollback line is
    /// empty, so extraRows = 0. The "full buffer, no extra rows" code path
    /// disables compression via a noCompression array, making ALL empty
    /// rows render at full cell height — including interior empties between
    /// content sections and the TUI prompt/status bar.
    ///
    /// Correct behavior: interior empties should always be compressed
    /// regardless of the extraRows/scrollback state.
    /// </summary>
    [StaFact]
    public void LivePane_FullBuffer_NoScrollback_InteriorEmptiesCompressed()
    {
        int bufferRows = 43;
        int canvasRows = 43;
        int cols = 155;
        var emulator = new TerminalEmulator(cols, bufferRows);

        // No scrollback — simplest path to extraRows == 0
        emulator.Feed(Encoding.UTF8.GetBytes(BuildTuiTableScreen(cols)));
        Assert.Equal(0, emulator.Buffer.ScrollbackCount);

        VerifyInteriorEmptiesCompressed(emulator, cols, canvasRows, scrollOffset: 0,
            "Interior empty rows should be compressed even without scrollback");
    }

    /// <summary>
    /// Same as above but with scrollback whose newest line is empty (from
    /// content that scrolled up with a trailing blank). This forces
    /// extraRows = 0 via the "newest scrollback empty" guard, hitting the
    /// same noCompression path.
    /// </summary>
    [StaFact]
    public void LivePane_FullBuffer_EmptyNewestScrollback_InteriorEmptiesCompressed()
    {
        int bufferRows = 43;
        int canvasRows = 43;
        int cols = 155;
        var emulator = new TerminalEmulator(cols, bufferRows);

        // Feed lines where the 7th (0-based index 6) is empty so it ends
        // up as the newest scrollback line after 50 lines into a 43-row
        // buffer (lines 0-6 go to scrollback, line 6 is empty).
        var sb = new StringBuilder();
        for (int i = 0; i < 50; i++)
        {
            if (i > 0) sb.Append("\r\n");
            if (i == 6)
                sb.Append(""); // empty line → newest scrollback
            else
                sb.Append($"prior-output-line-{i + 1:D4}");
        }
        emulator.Feed(Encoding.UTF8.GetBytes(sb.ToString()));

        var buffer = emulator.Buffer;
        Assert.True(buffer.ScrollbackCount > 0, "Setup: must have scrollback");
        Assert.True(buffer.IsScrollbackLineEmpty(buffer.ScrollbackCount - 1),
            "Setup: newest scrollback line must be empty to trigger the bug path");

        // Clear screen and fill with TUI content
        emulator.Feed(Encoding.UTF8.GetBytes("\x1b[2J\x1b[H"));
        emulator.Feed(Encoding.UTF8.GetBytes(BuildTuiTableScreen(cols)));

        VerifyInteriorEmptiesCompressed(emulator, cols, canvasRows, scrollOffset: 0,
            "Interior empty rows should be compressed even when newest scrollback is empty");
    }

    /// <summary>
    /// When scrolled up with empty newest scrollback (the same condition
    /// that causes the live-view bug), interior empty rows between content
    /// blocks should remain compressed, not rendered at full cell height.
    /// </summary>
    [StaFact]
    public void LivePane_FullBuffer_ScrolledUp_EmptyNewestScrollback_InteriorEmptiesCompressed()
    {
        int bufferRows = 43;
        int canvasRows = 43;
        int cols = 155;
        var emulator = new TerminalEmulator(cols, bufferRows);

        // Create scrollback with empty newest line (same as the live-view test)
        var sb = new StringBuilder();
        for (int i = 0; i < 50; i++)
        {
            if (i > 0) sb.Append("\r\n");
            if (i == 6)
                sb.Append("");
            else
                sb.Append($"prior-output-line-{i + 1:D4}");
        }
        emulator.Feed(Encoding.UTF8.GetBytes(sb.ToString()));

        emulator.Feed(Encoding.UTF8.GetBytes("\x1b[2J\x1b[H"));
        emulator.Feed(Encoding.UTF8.GetBytes(BuildTuiTableScreen(cols)));

        var buffer = emulator.Buffer;
        Assert.True(buffer.ScrollbackCount > 0);
        Assert.True(buffer.IsScrollbackLineEmpty(buffer.ScrollbackCount - 1),
            "Setup: newest scrollback line must be empty");

        // Scroll up 1 line
        var viewport = new TerminalViewport { IsLive = true, ScrollOffset = 1 };
        var canvas = RenderCanvas(emulator, viewport, cols, canvasRows);

        int totalRows = canvas._rowYPositions!.Length - 1;
        int fullHeightInteriorEmpties = 0;
        int compressedInteriorEmpties = 0;

        int lastNonEmpty = -1;
        for (int row = totalRows - 1; row >= 0; row--)
        {
            if (!IsRenderedRowEmpty(buffer, row, 1, totalRows))
            { lastNonEmpty = row; break; }
        }

        for (int row = 0; row < lastNonEmpty; row++)
        {
            if (!IsRenderedRowEmpty(buffer, row, 1, totalRows)) continue;
            double h = canvas._rowYPositions![row + 1] - canvas._rowYPositions[row];
            if (Math.Abs(h - canvas.CellHeight) < 0.5)
                fullHeightInteriorEmpties++;
            else if (h < canvas.CellHeight)
                compressedInteriorEmpties++;
        }

        Assert.True(compressedInteriorEmpties > 0,
            $"Interior empty rows should be compressed when scrolled up. " +
            $"Got {fullHeightInteriorEmpties} full-height and {compressedInteriorEmpties} compressed. " +
            $"(extraRows={canvas._extraRows}, baseRowCount={canvas._baseRowCount})");
        Assert.Equal(0, fullHeightInteriorEmpties);
    }

    private void VerifyInteriorEmptiesCompressed(
        TerminalEmulator emulator, int cols, int canvasRows, int scrollOffset, string message)
    {
        var buffer = emulator.Buffer;

        // Verify last row has content (full buffer)
        bool lastRowHasContent = false;
        for (int c = 0; c < Math.Min(10, buffer.Columns); c++)
        {
            var cell = buffer.GetCell(buffer.Rows - 1, c);
            if (cell.Character != ' ' && cell.Character != '\0')
            { lastRowHasContent = true; break; }
        }
        Assert.True(lastRowHasContent, "Setup: last row must have content (full buffer)");

        TerminalViewport? viewport = scrollOffset > 0
            ? new TerminalViewport { IsLive = true, ScrollOffset = scrollOffset }
            : null;

        var canvas = RenderCanvas(emulator, viewport, cols, canvasRows);

        int totalRows = canvas._rowYPositions!.Length - 1;
        int fullHeightInteriorEmpties = 0;
        int compressedInteriorEmpties = 0;

        int lastNonEmpty = -1;
        for (int row = totalRows - 1; row >= 0; row--)
        {
            if (!IsRenderedRowEmpty(buffer, row, scrollOffset, totalRows))
            { lastNonEmpty = row; break; }
        }

        for (int row = 0; row < lastNonEmpty; row++)
        {
            if (!IsRenderedRowEmpty(buffer, row, scrollOffset, totalRows)) continue;
            double h = canvas._rowYPositions[row + 1] - canvas._rowYPositions[row];
            if (Math.Abs(h - canvas.CellHeight) < 0.5)
                fullHeightInteriorEmpties++;
            else if (h < canvas.CellHeight)
                compressedInteriorEmpties++;
        }

        Assert.True(compressedInteriorEmpties > 0,
            $"{message}. " +
            $"Got {fullHeightInteriorEmpties} full-height and {compressedInteriorEmpties} compressed interior empties. " +
            $"(extraRows={canvas._extraRows}, baseRowCount={canvas._baseRowCount})");
        Assert.Equal(0, fullHeightInteriorEmpties);
    }

    // ─── Colored backgrounds on empty rows must not block compression ──

    /// <summary>
    /// Bug: Claude Code's diff output uses colored backgrounds (e.g. green
    /// for additions) on ALL rows in a diff block, including empty separator
    /// lines within the block. The IsRowEmpty check rejected rows with
    /// non-default backgrounds, so colored empty lines were never compressed.
    ///
    /// Correct behavior: rows containing only whitespace should compress
    /// regardless of background color.
    /// </summary>
    [StaFact]
    public void LivePane_ColoredEmptyRows_StillCompressed()
    {
        int bufferRows = 43;
        int canvasRows = 43;
        int cols = 80;
        var emulator = new TerminalEmulator(cols, bufferRows);

        // Simulate Claude Code diff output: text content with green
        // background, including empty lines within the colored block.
        var sb = new StringBuilder();
        string green = "\x1b[42m"; // green background
        string reset = "\x1b[m";

        // Rows 0-15: content with default background
        for (int i = 0; i < 16; i++)
        {
            if (i > 0) sb.Append("\r\n");
            sb.Append($"output-line-{i + 1:D3}");
        }

        // Rows 16-35: diff block with green background (some content, some empty)
        for (int i = 16; i < 36; i++)
        {
            sb.Append("\r\n");
            sb.Append(green);
            if (i % 3 == 0)
                sb.Append($"  + added-line-{i:D3}".PadRight(cols));
            else
                sb.Append(new string(' ', cols)); // empty row with green background
            sb.Append(reset);
        }

        // Rows 36-37: empty (no background)
        sb.Append("\r\n\r\n");

        // Rows 38-42: TUI chrome
        sb.Append("\r\n" + new string('━', cols));
        sb.Append("\r\n" + "❯ ".PadRight(cols));
        sb.Append("\r\n" + new string('━', cols));
        sb.Append("\r\n" + "✻ Working… (5s)".PadRight(cols));
        sb.Append(new string('━', cols));

        emulator.Feed(Encoding.UTF8.GetBytes(sb.ToString()));

        var canvas = RenderCanvas(emulator, null, cols, canvasRows);

        // Count compressed vs full-height empty rows
        var buffer = emulator.Buffer;
        int totalRows = canvas._rowYPositions!.Length - 1;
        int fullHeightEmpty = 0;
        int compressedEmpty = 0;

        int lastNonEmpty = -1;
        for (int row = totalRows - 1; row >= 0; row--)
        {
            if (!IsRenderedRowEmpty(buffer, row, 0, totalRows))
            { lastNonEmpty = row; break; }
        }

        for (int row = 0; row < lastNonEmpty; row++)
        {
            if (!IsRenderedRowEmpty(buffer, row, 0, totalRows)) continue;
            double h = canvas._rowYPositions[row + 1] - canvas._rowYPositions[row];
            if (Math.Abs(h - canvas.CellHeight) < 0.5)
                fullHeightEmpty++;
            else if (h < canvas.CellHeight)
                compressedEmpty++;
        }

        Assert.True(compressedEmpty > 0,
            $"Empty rows with colored backgrounds should be compressed. " +
            $"Got {fullHeightEmpty} full-height and {compressedEmpty} compressed. " +
            $"(extraRows={canvas._extraRows}, baseRowCount={canvas._baseRowCount})");
        Assert.Equal(0, fullHeightEmpty);
    }

    // ─── Scroll-up compression regression ────────────────────────────

    /// <summary>
    /// Bug: scrolling up one mouse wheel notch (scrollOffset += 3) causes
    /// interior empty lines between content sections to lose compression
    /// and render at full cell height. Tests the TUI table layout at
    /// multiple scroll offsets to verify compression is stable.
    /// </summary>
    [StaFact]
    public void ScrollUpOneNotch_InteriorEmptiesStayCompressed()
    {
        int bufferRows = 43;
        int canvasRows = 43;
        int cols = 155;
        var emulator = new TerminalEmulator(cols, bufferRows);

        // Create scrollback so extraRows can be computed
        var sb = new StringBuilder();
        for (int i = 0; i < 80; i++)
        {
            if (i > 0) sb.Append("\r\n");
            sb.Append($"prior-output-line-{i + 1:D4}");
        }
        emulator.Feed(Encoding.UTF8.GetBytes(sb.ToString()));
        emulator.Feed(Encoding.UTF8.GetBytes("\x1b[2J\x1b[H"));
        emulator.Feed(Encoding.UTF8.GetBytes(BuildTuiTableScreen(cols)));

        var buffer = emulator.Buffer;
        Assert.True(buffer.ScrollbackCount > 0, "Setup: must have scrollback");

        // scrollOffset = 0: baseline
        {
            var canvas = RenderCanvas(emulator, null, cols, canvasRows);
            var (fullHeight, compressed) = CountInteriorEmptyHeights(buffer, canvas, scrollOffset: 0);
            Assert.True(compressed > 0,
                $"Baseline (scrollOffset=0): expected compressed interior empties. " +
                $"full={fullHeight}, compressed={compressed}");
        }

        // scrollOffset = 3: one mouse wheel scroll up
        {
            var viewport = new TerminalViewport { IsLive = true, ScrollOffset = 3 };
            var canvas = RenderCanvas(emulator, viewport, cols, canvasRows);
            var (fullHeight, compressed) = CountInteriorEmptyHeights(buffer, canvas, scrollOffset: 3);
            Assert.True(compressed > 0,
                $"Scrolled up (scrollOffset=3): interior empties should stay compressed. " +
                $"full={fullHeight}, compressed={compressed}, " +
                $"extraRows={canvas._extraRows}, baseRowCount={canvas._baseRowCount}");
            Assert.Equal(0, fullHeight);
        }

        // scrollOffset = 6: two wheel notches
        {
            var viewport = new TerminalViewport { IsLive = true, ScrollOffset = 6 };
            var canvas = RenderCanvas(emulator, viewport, cols, canvasRows);
            var (fullHeight, compressed) = CountInteriorEmptyHeights(buffer, canvas, scrollOffset: 6);
            Assert.True(compressed > 0,
                $"Scrolled up (scrollOffset=6): interior empties should stay compressed. " +
                $"full={fullHeight}, compressed={compressed}");
            Assert.Equal(0, fullHeight);
        }
    }

    /// <summary>
    /// Same scroll-up test but with a canvas height that is not an exact
    /// multiple of cellHeight, matching real WPF layout where ActualHeight
    /// has sub-pixel remainder.
    /// </summary>
    [StaFact]
    public void ScrollUpOneNotch_FractionalCanvasHeight_InteriorEmptiesStayCompressed()
    {
        int bufferRows = 43;
        int cols = 155;
        var emulator = new TerminalEmulator(cols, bufferRows);

        var sb = new StringBuilder();
        for (int i = 0; i < 80; i++)
        {
            if (i > 0) sb.Append("\r\n");
            sb.Append($"prior-output-line-{i + 1:D4}");
        }
        emulator.Feed(Encoding.UTF8.GetBytes(sb.ToString()));
        emulator.Feed(Encoding.UTF8.GetBytes("\x1b[2J\x1b[H"));
        emulator.Feed(Encoding.UTF8.GetBytes(BuildTuiTableScreen(cols)));

        var buffer = emulator.Buffer;

        var probe = new TerminalCanvas();
        probe.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        double cellHeight = probe.CellHeight;
        double canvasHeight = (bufferRows - 0.5) * cellHeight;

        // scrollOffset = 0
        {
            var canvas = RenderCanvasWithHeight(emulator, null, cols, canvasHeight);
            var (fullHeight, compressed) = CountInteriorEmptyHeights(buffer, canvas, scrollOffset: 0);
            Assert.True(compressed > 0,
                $"Fractional height baseline: full={fullHeight}, compressed={compressed}");
        }

        // scrollOffset = 3
        {
            var viewport = new TerminalViewport { IsLive = true, ScrollOffset = 3 };
            var canvas = RenderCanvasWithHeight(emulator, viewport, cols, canvasHeight);
            var (fullHeight, compressed) = CountInteriorEmptyHeights(buffer, canvas, scrollOffset: 3);
            Assert.True(compressed > 0,
                $"Fractional height scrolled: full={fullHeight}, compressed={compressed}");
            Assert.Equal(0, fullHeight);
        }
    }

    /// <summary>
    /// Bug scenario: Claude TUI is actively streaming output (partial screen
    /// redraw in progress) when the user scrolls up. The buffer is in a
    /// transient state — top rows have new content, bottom rows still have
    /// stale content from the previous frame, with the TUI cursor positioned
    /// mid-screen. Interior empties between the written sections must still
    /// compress even during this transient state.
    /// </summary>
    [StaFact]
    public void ScrollUpDuringPartialRedraw_InteriorEmptiesStayCompressed()
    {
        int bufferRows = 43;
        int canvasRows = 43;
        int cols = 155;
        var emulator = new TerminalEmulator(cols, bufferRows);

        // Phase 1: establish scrollback + initial TUI screen
        var sb = new StringBuilder();
        for (int i = 0; i < 80; i++)
        {
            if (i > 0) sb.Append("\r\n");
            sb.Append($"prior-output-line-{i + 1:D4}");
        }
        emulator.Feed(Encoding.UTF8.GetBytes(sb.ToString()));
        emulator.Feed(Encoding.UTF8.GetBytes("\x1b[2J\x1b[H"));
        emulator.Feed(Encoding.UTF8.GetBytes(BuildTuiTableScreen(cols)));

        // Phase 2: simulate partial TUI redraw — cursor home, overwrite top half
        // The TUI clears each line then writes new content (like a streaming update)
        sb.Clear();
        sb.Append("\x1b[H"); // cursor home
        for (int i = 0; i < 20; i++)
        {
            sb.Append($"\x1b[{i + 1};1H"); // position cursor
            sb.Append("\x1b[2K"); // clear line
            if (i == 0)
                sb.Append("● Updated Section — Streaming Output".PadRight(cols));
            else if (i == 1 || i == 6 || i == 13)
                { /* leave empty (separator line) */ }
            else
                sb.Append($"  Updated content line {i}".PadRight(cols));
        }
        emulator.Feed(Encoding.UTF8.GetBytes(sb.ToString()));

        // Phase 3: user scrolls up DURING the partial redraw
        // Buffer now has updated content in rows 0-19, old content in rows 20-42
        var buffer = emulator.Buffer;

        // scrollOffset = 3: one wheel notch up
        var viewport = new TerminalViewport { IsLive = true, ScrollOffset = 3 };
        var canvas = RenderCanvas(emulator, viewport, cols, canvasRows);
        var (fullHeight, compressed) = CountInteriorEmptyHeights(buffer, canvas, scrollOffset: 3);
        Assert.True(compressed > 0,
            $"During partial redraw, interior empties should still compress. " +
            $"full={fullHeight}, compressed={compressed}, " +
            $"extraRows={canvas._extraRows}, baseRowCount={canvas._baseRowCount}");
        Assert.Equal(0, fullHeight);
    }

    /// <summary>
    /// Race condition variant: TUI clears the entire screen (ESC[2J) then
    /// starts writing content row by row. User scrolls up after only half
    /// the screen has been written. The bottom half is empty. Interior
    /// empties in the written portion must still compress.
    /// </summary>
    [StaFact]
    public void ScrollUpAfterScreenClear_PartialContent_InteriorEmptiesStayCompressed()
    {
        int bufferRows = 43;
        int canvasRows = 43;
        int cols = 155;
        var emulator = new TerminalEmulator(cols, bufferRows);

        // Establish scrollback
        var sb = new StringBuilder();
        for (int i = 0; i < 60; i++)
        {
            if (i > 0) sb.Append("\r\n");
            sb.Append($"prior-output-{i + 1:D4}");
        }
        emulator.Feed(Encoding.UTF8.GetBytes(sb.ToString()));

        // Full TUI screen
        emulator.Feed(Encoding.UTF8.GetBytes("\x1b[2J\x1b[H"));
        emulator.Feed(Encoding.UTF8.GetBytes(BuildTuiTableScreen(cols)));

        // TUI starts a new redraw: clear screen, then write only first 25 rows
        emulator.Feed(Encoding.UTF8.GetBytes("\x1b[2J\x1b[H"));
        sb.Clear();
        for (int i = 0; i < 25; i++)
        {
            if (i > 0) sb.Append("\r\n");
            if (i == 5 || i == 12 || i == 18)
                { /* empty separator line */ }
            else
                sb.Append($"Section content line {i + 1}".PadRight(cols));
        }
        emulator.Feed(Encoding.UTF8.GetBytes(sb.ToString()));

        // User scrolls up during this partial state
        var buffer = emulator.Buffer;
        var viewport = new TerminalViewport { IsLive = true, ScrollOffset = 3 };
        var canvas = RenderCanvas(emulator, viewport, cols, canvasRows);
        var (fullHeight, compressed) = CountInteriorEmptyHeights(buffer, canvas, scrollOffset: 3);
        Assert.True(compressed > 0,
            $"After screen clear + partial write, interior empties should compress. " +
            $"full={fullHeight}, compressed={compressed}");
        Assert.Equal(0, fullHeight);
    }

    /// <summary>
    /// Same test at 155×53 — the exact dimensions from the bug report.
    /// The larger buffer creates more interior empties and different
    /// extraRows counts, which might trigger edge cases.
    /// </summary>
    [StaFact]
    public void ScrollUpOneNotch_53Rows_InteriorEmptiesStayCompressed()
    {
        int bufferRows = 53;
        int canvasRows = 53;
        int cols = 155;
        var emulator = new TerminalEmulator(cols, bufferRows);

        var sb = new StringBuilder();
        for (int i = 0; i < 100; i++)
        {
            if (i > 0) sb.Append("\r\n");
            sb.Append($"prior-output-line-{i + 1:D4}");
        }
        emulator.Feed(Encoding.UTF8.GetBytes(sb.ToString()));
        emulator.Feed(Encoding.UTF8.GetBytes("\x1b[2J\x1b[H"));
        emulator.Feed(Encoding.UTF8.GetBytes(BuildTuiScreen53(cols)));

        var buffer = emulator.Buffer;
        Assert.True(buffer.ScrollbackCount > 0, "Setup: must have scrollback");

        for (int offset = 0; offset <= 9; offset += 3)
        {
            TerminalViewport? viewport = offset > 0
                ? new TerminalViewport { IsLive = true, ScrollOffset = offset }
                : null;
            var canvas = RenderCanvas(emulator, viewport, cols, canvasRows);
            var (fullHeight, compressed) = CountInteriorEmptyHeights(buffer, canvas, offset);
            Assert.True(compressed > 0,
                $"scrollOffset={offset}: expected compressed interior empties. " +
                $"full={fullHeight}, compressed={compressed}, " +
                $"extraRows={canvas._extraRows}, baseRowCount={canvas._baseRowCount}");

            var detail = DescribeFullHeightInteriorEmpties(buffer, canvas, offset);
            Assert.True(fullHeight == 0,
                $"scrollOffset={offset}: {fullHeight} full-height interior empties. " +
                $"extraRows={canvas._extraRows}, baseRowCount={canvas._baseRowCount}\n{detail}");
        }
    }

    /// <summary>
    /// 53-row variant with hidden cursor (CursorEnabled=false), matching a
    /// live Claude Code TUI session. FindVisualCursorRow scans for a
    /// reverse-video row, which shifts with scrollOffset and may change
    /// the displayCursorRow used in compression calculations.
    /// </summary>
    [StaFact]
    public void ScrollUpOneNotch_53Rows_CursorHidden_InteriorEmptiesStayCompressed()
    {
        int bufferRows = 53;
        int canvasRows = 53;
        int cols = 155;
        var emulator = new TerminalEmulator(cols, bufferRows);

        var sb = new StringBuilder();
        for (int i = 0; i < 100; i++)
        {
            if (i > 0) sb.Append("\r\n");
            sb.Append($"prior-output-line-{i + 1:D4}");
        }
        emulator.Feed(Encoding.UTF8.GetBytes(sb.ToString()));
        emulator.Feed(Encoding.UTF8.GetBytes("\x1b[2J\x1b[H"));
        emulator.Feed(Encoding.UTF8.GetBytes(BuildTuiScreen53(cols)));

        // Hide cursor (Claude TUI does this)
        emulator.Feed(Encoding.UTF8.GetBytes("\x1b[?25l"));

        var buffer = emulator.Buffer;

        for (int offset = 0; offset <= 9; offset += 3)
        {
            TerminalViewport? viewport = offset > 0
                ? new TerminalViewport { IsLive = true, ScrollOffset = offset }
                : null;
            var canvas = RenderCanvas(emulator, viewport, cols, canvasRows);
            var (fullHeight, compressed) = CountInteriorEmptyHeights(buffer, canvas, offset);
            Assert.True(compressed > 0,
                $"scrollOffset={offset} (cursor hidden): expected compressed interior empties. " +
                $"full={fullHeight}, compressed={compressed}, " +
                $"extraRows={canvas._extraRows}, baseRowCount={canvas._baseRowCount}");
            Assert.Equal(0, fullHeight);
        }
    }

    private static (int fullHeight, int compressed) CountInteriorEmptyHeights(
        TerminalBuffer buffer, TerminalCanvas canvas, int scrollOffset)
    {
        int totalRows = canvas._rowYPositions!.Length - 1;
        // The canvas maps cells using baseRowCount + extraRows as viewRows,
        // which may differ from totalRows when trailing base rows are trimmed.
        int viewRows = canvas._baseRowCount + canvas._extraRows;
        int fullHeightInteriorEmpties = 0;
        int compressedInteriorEmpties = 0;

        int lastNonEmpty = -1;
        for (int row = totalRows - 1; row >= 0; row--)
        {
            if (!IsRenderedRowEmpty(buffer, row, scrollOffset, viewRows))
            { lastNonEmpty = row; break; }
        }

        for (int row = 0; row < lastNonEmpty; row++)
        {
            if (!IsRenderedRowEmpty(buffer, row, scrollOffset, viewRows)) continue;
            double h = canvas._rowYPositions![row + 1] - canvas._rowYPositions[row];
            if (Math.Abs(h - canvas.CellHeight) < 0.5)
                fullHeightInteriorEmpties++;
            else if (h < canvas.CellHeight)
                compressedInteriorEmpties++;
        }

        return (fullHeightInteriorEmpties, compressedInteriorEmpties);
    }

    private static string DescribeFullHeightInteriorEmpties(
        TerminalBuffer buffer, TerminalCanvas canvas, int scrollOffset)
    {
        int totalRows = canvas._rowYPositions!.Length - 1;
        int viewRows = canvas._baseRowCount + canvas._extraRows;
        var lines = new List<string>();

        int lastNonEmpty = -1;
        for (int row = totalRows - 1; row >= 0; row--)
        {
            if (!IsRenderedRowEmpty(buffer, row, scrollOffset, viewRows))
            { lastNonEmpty = row; break; }
        }

        for (int row = 0; row < totalRows; row++)
        {
            double h = canvas._rowYPositions[row + 1] - canvas._rowYPositions[row];
            bool empty = IsRenderedRowEmpty(buffer, row, scrollOffset, viewRows);
            bool isInterior = empty && row < lastNonEmpty;
            bool isFullHeight = Math.Abs(h - canvas.CellHeight) < 0.5;
            string marker = isInterior && isFullHeight ? " *** BUG" : "";
            string preview = "";
            for (int c = 0; c < Math.Min(60, buffer.Columns); c++)
            {
                var cell = buffer.GetVisibleCell(row, c, scrollOffset, viewRows);
                if (cell.Character != '\0') preview += cell.Character;
            }
            lines.Add($"  row {row,2}: h={h,6:F1} empty={empty,-5} interior={isInterior,-5} [{preview.TrimEnd()}]{marker}");
        }
        return string.Join("\n", lines);
    }

    /// <summary>
    /// Builds a 43-row TUI screen with text sections, a box-drawing table,
    /// verdict, and status bar — matching the layout pattern of a Claude
    /// Code analysis screen. Interior empty rows appear between sections.
    /// </summary>
    private static string BuildTuiTableScreen(int cols)
    {
        var tui = new StringBuilder();
        string pad(string s) => s.PadRight(cols);

        // Row 0: section header
        tui.Append(pad("● Lorem Ipsum — Section 7 (Analysis §3)"));

        // Row 1: empty
        tui.Append("\r\n");

        // Row 2: source label
        tui.Append("\r\n" + pad("  Source A (lines 100-110, p. 42):"));

        // Rows 3-5: quoted text block
        tui.Append("\r\n" + pad("  ▎ Lorem ipsum dolor sit amet, consectetur adipiscing elit, sed do eiusmod tempor incididunt ut labore."));
        tui.Append("\r\n" + pad("  ▎ Ut enim ad minim veniam, quis nostrud exercitation ullamco laboris nisi ut aliquip ex ea commodo."));
        tui.Append("\r\n" + pad("  ▎ Duis aute irure dolor in reprehenderit in voluptate velit esse cillum dolore eu fugiat nulla pariatur."));

        // Row 6: empty
        tui.Append("\r\n");

        // Row 7: second source label
        tui.Append("\r\n" + pad("  Source B (lines 200-210, p. 87):"));

        // Rows 8-11: second quoted text block
        tui.Append("\r\n" + pad("  ▎ Sed ut perspiciatis unde omnis iste natus error sit voluptatem accusantium doloremque laudantium."));
        tui.Append("\r\n" + pad("  ▎ Totam rem aperiam eaque ipsa quae ab illo inventore veritatis et quasi architecto beatae vitae dicta."));
        tui.Append("\r\n" + pad("  ▎ Nemo enim ipsam voluptatem quia voluptas sit aspernatur aut odit aut fugit sed quia consequuntur."));
        tui.Append("\r\n" + pad("  ▎ Neque porro quisquam est qui dolorem ipsum quia dolor sit amet consectetur adipisci velit numquam."));

        // Row 12: analysis header
        tui.Append("\r\n" + pad("  Analysis:"));

        // Row 13: empty
        tui.Append("\r\n");

        // Row 14: table top border
        string border_t = "  ┌─────┬───────────────────────────┬────────────────────────────────────────┬──────────────────────────────────────────────────────────────────────┐";
        tui.Append("\r\n" + pad(border_t));

        // Row 15: header
        string header = "  │  #  │           Col A              │                 Col B                  │                               Col C                                │";
        tui.Append("\r\n" + pad(header));

        // Row 16: header separator
        string border_m = "  ├─────┼───────────────────────────┼────────────────────────────────────────┼──────────────────────────────────────────────────────────────────────┤";
        tui.Append("\r\n" + pad(border_m));

        // Table data rows (6 entries with separators = 16 rows)
        string[] tableRows = {
            "  │ 1   │ \"lorem ipsum dolor\"        │ \"amet consectetur adipiscing\"           │ ✓                                                                  │",
            "  │     │                           │ (sed do eiusmod tempor)                │                                                                    │",
            border_m,
            "  │ 2   │ \"ut enim ad minim\"         │ \"veniam quis nostrud\"                   │ exercitation ullamco laboris nisi aliquip                           │",
            "  │     │                           │                                        │                                                                    │",
            border_m,
            "  │ 3   │ \"duis aute irure dolor\"    │ \"in reprehenderit voluptate\"            │ ✓                                                                  │",
            "  │     │                           │                                        │                                                                    │",
            border_m,
            "  │ 4   │ \"excepteur sint occaecat\"  │ —                                      │ cupidatat non proident sunt in culpa qui officia deserunt           │",
            "  │     │                           │                                        │ mollit anim id est laborum sed perspiciatis.                       │",
            border_m,
            "  │ 5   │ \"nemo enim ipsam\"          │ \"voluptatem quia voluptas\"              │ ✓                                                                  │",
            "  │     │                           │                                        │                                                                    │",
            border_m,
            "  │ 6   │ \"neque porro quisquam\"     │ \"dolorem ipsum quia dolor\"              │ slightly compressed but meaning preserved                           │",
            "  │     │                           │                                        │                                                                    │",
        };
        foreach (var row in tableRows)
            tui.Append("\r\n" + pad(row));

        // Table bottom border
        string border_b = "  └─────┴───────────────────────────┴────────────────────────────────────────┴──────────────────────────────────────────────────────────────────────┘";
        tui.Append("\r\n" + pad(border_b));

        // Row 35: empty
        tui.Append("\r\n");

        // Row 36: verdict
        tui.Append("\r\n" + pad("  Verdict: Clean — no significant issues. Lorem ipsum dolor sit amet."));

        // Row 37: empty
        tui.Append("\r\n");

        // Rows 38-42: Claude TUI chrome (status line + bars + prompt)
        string statusBar = new string('━', cols);
        tui.Append("\r\n" + pad("✻ Working… (27s · ↓ 699 tokens)"));
        tui.Append("\r\n");
        tui.Append("\r\n" + statusBar);
        tui.Append("\r\n" + pad("❯ "));
        tui.Append(statusBar);  // last row — no trailing \r\n

        return tui.ToString();
    }

    /// <summary>
    /// Builds a 53-row TUI screen matching the user's real session dimensions.
    /// Multiple content sections separated by empty lines, a large table,
    /// verdict, and the TUI status bar at the bottom.
    /// </summary>
    private static string BuildTuiScreen53(int cols)
    {
        var tui = new StringBuilder();
        string pad(string s) => s.PadRight(cols);

        // Rows 0-5: first section with header + quoted text
        tui.Append(pad("● Section 1 — EN (GMC p. 232, lines 5382-5438):"));
        tui.Append("\r\n"); // row 1: empty
        tui.Append("\r\n" + pad("  Source A (p. 232, lines 5382-5395):"));
        tui.Append("\r\n" + pad("  ▎ Lorem ipsum dolor sit amet, consectetur adipiscing elit, sed do eiusmod tempor incididunt ut labore."));
        tui.Append("\r\n" + pad("  ▎ Ut enim ad minim veniam, quis nostrud exercitation ullamco laboris nisi ut aliquip ex ea commodo."));
        tui.Append("\r\n" + pad("  ▎ Duis aute irure dolor in reprehenderit in voluptate velit esse cillum dolore eu fugiat nulla pariatur."));

        // Row 6: empty
        tui.Append("\r\n");

        // Rows 7-12: second quoted block
        tui.Append("\r\n" + pad("  Source B (p. 235, lines 5400-5438):"));
        tui.Append("\r\n" + pad("  ▎ Sed ut perspiciatis unde omnis iste natus error sit voluptatem accusantium doloremque laudantium."));
        tui.Append("\r\n" + pad("  ▎ Totam rem aperiam eaque ipsa quae ab illo inventore veritatis et quasi architecto beatae vitae dicta."));
        tui.Append("\r\n" + pad("  ▎ Nemo enim ipsam voluptatem quia voluptas sit aspernatur aut odit aut fugit sed quia consequuntur."));
        tui.Append("\r\n" + pad("  ▎ Neque porro quisquam est qui dolorem ipsum quia dolor sit amet consectetur adipisci velit numquam."));

        // Row 13: empty
        tui.Append("\r\n");

        // Rows 14-17: analysis paragraph
        tui.Append("\r\n" + pad("  Analysis:"));
        tui.Append("\r\n" + pad("  The source materials demonstrate substantial overlap in terminology and phrasing patterns."));
        tui.Append("\r\n" + pad("  Key structural elements are preserved across both versions with minor reformulation."));

        // Row 18: empty
        tui.Append("\r\n");

        // Rows 19-38: large table (20 rows)
        string border_t = "  ┌─────┬───────────────────────────────┬──────────────────────────────────────────┬────────────────────────────────────────────────────────────────┐";
        string border_m = "  ├─────┼───────────────────────────────┼──────────────────────────────────────────┼────────────────────────────────────────────────────────────────┤";
        string border_b = "  └─────┴───────────────────────────────┴──────────────────────────────────────────┴────────────────────────────────────────────────────────────────┘";
        tui.Append("\r\n" + pad(border_t));
        tui.Append("\r\n" + pad("  │  #  │           Source Phrase          │              Target Phrase               │                          Notes                               │"));
        tui.Append("\r\n" + pad(border_m));

        string[] entries = {
            "  │ 1   │ \"lorem ipsum dolor sit\"        │ \"amet consectetur adipiscing\"             │ ✓ Direct correspondence                                        │",
            "  │ 2   │ \"ut enim ad minim veniam\"      │ \"quis nostrud exercitation\"               │ ✓ Structural match                                             │",
            "  │ 3   │ \"duis aute irure dolor\"        │ \"in reprehenderit voluptate\"              │ Minor reformulation                                            │",
            "  │ 4   │ \"excepteur sint occaecat\"      │ \"cupidatat non proident\"                  │ ✓ Direct match                                                 │",
            "  │ 5   │ \"sed ut perspiciatis\"          │ \"unde omnis iste natus error\"             │ Expanded but equivalent                                        │",
            "  │ 6   │ \"nemo enim ipsam voluptatem\"   │ \"quia voluptas sit aspernatur\"            │ ✓ Near-identical                                               │",
            "  │ 7   │ \"at vero eos et accusamus\"     │ \"et iusto odio dignissimos\"               │ ✓ Direct match                                                 │",
            "  │ 8   │ \"nam libero tempore\"           │ \"cum soluta nobis est eligendi\"           │ Compressed but meaning preserved                               │",
        };
        foreach (var entry in entries)
        {
            tui.Append("\r\n" + pad(entry));
            tui.Append("\r\n" + pad(border_m));
        }

        tui.Append("\r\n" + pad(border_b));

        // Row 39: empty
        tui.Append("\r\n");

        // Rows 40-43: verdict section
        tui.Append("\r\n" + pad("  Verdict: Substantial overlap (7 of 8 entries show direct correspondence)."));
        tui.Append("\r\n" + pad("  Recommendation: Flag for further review. Consider comparing full document context."));

        // Row 44: empty
        tui.Append("\r\n");

        // Rows 45-47: additional notes
        tui.Append("\r\n" + pad("  Additional context notes:"));
        tui.Append("\r\n" + pad("  - The source predates the target by approximately 18 months."));
        tui.Append("\r\n" + pad("  - Both documents reference the same regulatory framework (GMC guidelines)."));

        // Row 48: empty
        tui.Append("\r\n");

        // Rows 49-52: TUI chrome (status + bars + prompt)
        string statusBar = new string('━', cols);
        tui.Append("\r\n" + pad("✻ Working… (42s · ↓ 1.2k tokens)"));
        tui.Append("\r\n");
        tui.Append("\r\n" + statusBar);
        tui.Append("\r\n" + pad("❯ "));
        tui.Append(statusBar); // row 52, no trailing newline

        return tui.ToString();
    }

    // ─── Gap-at-top after scroll-up: 37-row session ────────────────

    /// <summary>
    /// Bug: 155×37 session (canvas matches buffer). At scrollOffset=0 the
    /// layout is bottom-aligned (correct). After one mouse-wheel scroll-up
    /// (scrollOffset=3), the last visible base row becomes an empty CUF
    /// line, triggering displayedBaseRows &lt; baseRowCount. This switches to
    /// the top-aligned code path — content jumps and a gap appears at top.
    ///
    /// Correct behavior: content should remain bottom-anchored after scroll.
    /// </summary>
    [StaFact]
    public void UIScroll_37Rows_NoGapAtTopAfterScrollUp()
    {
        int bufferRows = 37;
        int canvasRows = 37;
        int cols = 155;
        var emulator = new TerminalEmulator(cols, bufferRows);

        // Create scrollback where the newest line is empty.
        // This forces extraRows = 0 via the "newest scrollback empty"
        // guard, matching the real session where TUI redraws left empty
        // lines at the scrollback boundary.
        // Feed: 80 content + 3 empty + 37 filler → 120 total lines.
        // SB = 120 - 37 = 83 entries. Lines 80-82 are empty → newest SB is empty.
        var sb = new StringBuilder();
        for (int i = 0; i < 80; i++)
        {
            if (i > 0) sb.Append("\r\n");
            sb.Append($"prior-output-line-{i + 1:D4}");
        }
        sb.Append("\r\n\r\n\r\n"); // 3 empty lines (indices 80-82)
        for (int i = 0; i < 37; i++)
        {
            sb.Append("\r\n");
            sb.Append($"filler-line-{i + 1:D4}");
        }
        emulator.Feed(Encoding.UTF8.GetBytes(sb.ToString()));

        var buffer = emulator.Buffer;
        Assert.True(buffer.ScrollbackCount > 0, "Setup: must have scrollback after initial feed");
        Assert.True(buffer.IsScrollbackLineEmpty(buffer.ScrollbackCount - 1),
            "Setup: newest scrollback line must be empty");

        // Clear screen, fill with TUI content matching the 2026 1 session
        emulator.Feed(Encoding.UTF8.GetBytes("\x1b[2J\x1b[H"));
        emulator.Feed(Encoding.UTF8.GetBytes(BuildTuiScreen37(cols)));

        // Hide cursor (Claude TUI does this)
        emulator.Feed(Encoding.UTF8.GetBytes("\x1b[?25l"));

        var viewport = new TerminalViewport { IsLive = true, ScrollOffset = 0 };
        var canvas = CreateAndRenderCanvas(emulator, viewport, cols, canvasRows);

        // Baseline: record first row Y at scrollOffset=0
        double firstRowY_offset0 = canvas._rowYPositions![0];
        int totalRendered0 = canvas._rowYPositions.Length - 1;
        double lastRowEnd0 = canvas._rowYPositions[totalRendered0];

        // Content should be bottom-anchored (last row ends at canvas height)
        Assert.True(Math.Abs(lastRowEnd0 - canvas.ActualHeight) < canvas.CellHeight,
            $"Baseline: last row ends at {lastRowEnd0:F1}, canvas={canvas.ActualHeight:F1}. " +
            $"Content should fill canvas at scrollOffset=0.");

        // Scroll up 1 notch
        ScrollUpAndRerender(canvas, viewport, buffer);

        double firstRowY_scrolled = canvas._rowYPositions![0];
        int totalRendered1 = canvas._rowYPositions.Length - 1;
        double lastRowEnd1 = canvas._rowYPositions[totalRendered1];

        // After scroll, content should still be bottom-anchored
        Assert.True(Math.Abs(lastRowEnd1 - canvas.ActualHeight) < canvas.CellHeight,
            $"After scroll-up (offset={viewport.ScrollOffset}): last row ends at {lastRowEnd1:F1}, " +
            $"canvas={canvas.ActualHeight:F1}. Content should still fill canvas. " +
            $"totalRendered={totalRendered1}, " +
            $"extraRows={canvas._extraRows}, baseRowCount={canvas._baseRowCount}");

        // Content should fill the canvas (firstRowY <= 0 means content
        // reaches or overflows the top edge).
        Assert.True(firstRowY_scrolled <= canvas.CellHeight,
            $"After scroll-up (offset={viewport.ScrollOffset}): gap at top! " +
            $"firstRowY={firstRowY_scrolled:F1} (should be <= {canvas.CellHeight:F1}). " +
            $"At offset=0 it was {firstRowY_offset0:F1}. " +
            $"extraRows={canvas._extraRows}, baseRowCount={canvas._baseRowCount}");
    }

    /// <summary>
    /// Same test with visible cursor (CursorEnabled=true), which changes the
    /// displayCursorRow calculation path.
    /// </summary>
    [StaFact]
    public void UIScroll_37Rows_CursorVisible_NoGapAtTopAfterScrollUp()
    {
        int bufferRows = 37;
        int canvasRows = 37;
        int cols = 155;
        var emulator = new TerminalEmulator(cols, bufferRows);

        var sb = new StringBuilder();
        for (int i = 0; i < 80; i++)
        {
            if (i > 0) sb.Append("\r\n");
            sb.Append($"prior-output-line-{i + 1:D4}");
        }
        sb.Append("\r\n\r\n\r\n"); // 3 empty lines (indices 80-82)
        for (int i = 0; i < 37; i++)
        {
            sb.Append("\r\n");
            sb.Append($"filler-line-{i + 1:D4}");
        }
        emulator.Feed(Encoding.UTF8.GetBytes(sb.ToString()));

        var buffer = emulator.Buffer;
        Assert.True(buffer.ScrollbackCount > 0, "Setup: must have scrollback");
        Assert.True(buffer.IsScrollbackLineEmpty(buffer.ScrollbackCount - 1),
            "Setup: newest scrollback line must be empty");

        emulator.Feed(Encoding.UTF8.GetBytes("\x1b[2J\x1b[H"));
        emulator.Feed(Encoding.UTF8.GetBytes(BuildTuiScreen37(cols)));
        // Cursor stays visible (default)
        var viewport = new TerminalViewport { IsLive = true, ScrollOffset = 0 };
        var canvas = CreateAndRenderCanvas(emulator, viewport, cols, canvasRows);

        double firstRowY0 = canvas._rowYPositions![0];
        double lastRowEnd0 = canvas._rowYPositions[canvas._rowYPositions.Length - 1];
        Assert.True(Math.Abs(lastRowEnd0 - canvas.ActualHeight) < canvas.CellHeight,
            $"Baseline: content not bottom-anchored. lastRow={lastRowEnd0:F1}, canvas={canvas.ActualHeight:F1}");

        ScrollUpAndRerender(canvas, viewport, buffer);

        double firstRowY1 = canvas._rowYPositions![0];
        double lastRowEnd1 = canvas._rowYPositions[canvas._rowYPositions.Length - 1];

        Assert.True(Math.Abs(lastRowEnd1 - canvas.ActualHeight) < canvas.CellHeight,
            $"After scroll (offset={viewport.ScrollOffset}): content not bottom-anchored. " +
            $"lastRow={lastRowEnd1:F1}, canvas={canvas.ActualHeight:F1}");

        Assert.True(firstRowY1 <= canvas.CellHeight,
            $"After scroll (offset={viewport.ScrollOffset}): gap at top! " +
            $"firstRowY={firstRowY1:F1} (should be <= {canvas.CellHeight:F1}). " +
            $"At offset=0 it was {firstRowY0:F1}. (extraRows={canvas._extraRows})");
    }

    private static string BuildTuiScreen37(int cols)
    {
        var tui = new StringBuilder();
        string pad(string s) => (s.Length > cols ? s[..cols] : s).PadRight(cols);

        // Rows 0-3: text content (translation analysis)
        tui.Append(pad("  HU (lines 5999-6005):"));
        tui.Append("\r\n" + pad("  ▎ Minden pokol, es amikor egy haborus ovezetben szolgalsz, az ido egyre csak nyulik es torzul."));
        tui.Append("\r\n" + pad("  ▎ Csakogy ennek nem kene szo szerint igy tortennie. Az osi sivatagot ert hatalmas bombazasok felfedtek."));
        tui.Append("\r\n" + pad("  ▎ volna meglatnia, es azota mar latod a haboru valodi arcat."));

        // Rows 4-6: table header + one data row + bottom border
        string border_t = "  ┌─────┬───────────────────────────────┬──────────────────────────────────────┬──────────────┬─────────────────────────────────────────────────────────────────┐";
        string border_b = "  └─────┴───────────────────────────────┴──────────────────────────────────────┴──────────────┴─────────────────────────────────────────────────────────────────┘";
        tui.Append("\r\n" + pad(border_t));
        tui.Append("\r\n" + pad("  │  #  │           EN                  │               HU                     │  Dimension   │                          Note                                   │"));
        tui.Append("\r\n" + pad("  │ 1   │ \"the true meaning of war\"     │ \"a haboru valodi arcat\"               │  precision   │ \"meaning\" → \"face/visage\" – stylistic shift, more vivid in HU    │"));

        // Row 7: table bottom
        tui.Append("\r\n" + pad(border_b));

        // Row 8: empty
        tui.Append("\r\n");

        // Row 9: verdict
        tui.Append("\r\n" + pad("  Clean paragraph — the one shift is a stylistic improvement if anything. No correction needed. Ready for next?"));

        // Row 10: prompt
        tui.Append("\r\n" + pad("❯ next"));

        // Row 11: section header
        tui.Append("\r\n" + pad("● EN — Infrastructure, paragraph 1 (lines 5472-5478):"));

        // Rows 12-14: quoted EN text (3 lines)
        tui.Append("\r\n" + pad("  ▎ Your chaplain thinks everyone died when that shell hit and uncovered an artifact, the Flag of Elam."));
        tui.Append("\r\n" + pad("  ▎ he can stop crying long enough to speak. He isn't taking it well. But you're elite, you were trained."));
        tui.Append("\r\n" + pad("  ▎ that's why you and your team can observe all of this without going completely Section 8."));

        // Row 15: HU label
        tui.Append("\r\n" + pad("  HU (lines 6007-6014):"));

        // Rows 16-18: quoted HU text (3 lines)
        tui.Append("\r\n" + pad("  ▎ A tabori Lelkeszed ugy hiszi, hogy mindenki meghalt, amikor az a lovedek becsapodott es felfedte."));
        tui.Append("\r\n" + pad("  ▎ mi is folyik itt, mar amikor eppen nem tor ra a siras. Nem igazan viseli jol. Viszont te elit vagy."));
        tui.Append("\r\n" + pad("  ▎ talan ez lehet az oka annak is, hogy te es az embereid kepesek vagytok szembenezni mindezzel."));

        // Row 19: empty
        tui.Append("\r\n");

        // Rows 20-30: second table (header + 3 data rows with separators)
        string border_m = "  ├─────┼───────────────────────────────┼──────────────────────────────────────┼──────────────┼─────────────────────────────────────────────────────────────────┤";
        tui.Append("\r\n" + pad(border_t));
        tui.Append("\r\n" + pad("  │  #  │           EN                  │               HU                     │  Dimension   │                          Note                                   │"));
        tui.Append("\r\n" + pad(border_m));
        tui.Append("\r\n" + pad("  │ 1   │ \"That's how he explains\"      │ \"Probalja elmondani, hogy mi is\"      │  precision   │ EN: the chaplain has a theory and that's his explanation.        │"));
        tui.Append("\r\n" + pad(border_m));
        tui.Append("\r\n" + pad("  │ 2   │ \"when he can stop crying\"     │ \"mar amikor eppen nem tor ra a\"       │  completeness│ Acceptable but slightly weaker — EN emphasizes the duration.    │"));
        tui.Append("\r\n" + pad(border_m));
        tui.Append("\r\n" + pad("  │ 3   │ \"going completely Section 8\"  │ \"teljesen beleorulnetek\" (going crazy)│  precision   │ Good cultural adaptation — \"Section 8\" means nothing in HU.     │"));
        tui.Append("\r\n" + pad(border_m));
        tui.Append("\r\n" + pad("  │ 4   │ \"observe all of this\"         │ \"szembenezni mindezzel\"               │  accuracy    │ \"observe\" vs \"face/confront\" — HU is more active, acceptable.   │"));
        tui.Append("\r\n" + pad(border_b));

        // Row 33: empty (CUF gap between table and verdict)
        tui.Append("\r\n");

        // Row 32: verdict
        tui.Append("\r\n" + pad("  Issue #1 is the only one worth correcting — the chaplain's theory framing is lost. Want to see a proposal?"));

        // Row 33: empty (CUF gap before TUI chrome)
        tui.Append("\r\n");

        // Rows 34-36: TUI chrome
        string statusBar = new string('━', cols);
        tui.Append("\r\n" + statusBar);
        // Row 35: prompt with reverse-video cursor
        tui.Append("\r\n" + "❯ \x1b[7m \x1b[27m" + new string(' ', cols - 3));
        tui.Append(statusBar); // Row 36: bottom status bar, no trailing \r\n

        return tui.ToString();
    }

    // ─── UI scroll simulation helpers ────────────────────────────────

    private static TerminalCanvas CreateAndRenderCanvas(
        TerminalEmulator emulator,
        TerminalViewport viewport,
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

    private static void ScrollUpAndRerender(
        TerminalCanvas canvas,
        TerminalViewport viewport,
        TerminalBuffer buffer,
        int notches = 1)
    {
        for (int i = 0; i < notches; i++)
        {
            int maxOffset = ViewportCalculator.MaxScrollOffset(
                buffer.Rows, canvas.Rows, buffer.ScrollbackCount);
            viewport.ScrollOffset = Math.Clamp(viewport.ScrollOffset + 3, 0, maxOffset);
            viewport.UserScrolledBack = viewport.ScrollOffset > 0;
        }

        double w = canvas.ActualWidth;
        double h = canvas.ActualHeight;
        canvas.InvalidateVisual();
        canvas.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        canvas.Arrange(new Rect(0, 0, w, h));

        var rtb = new RenderTargetBitmap(
            Math.Max(1, (int)w), Math.Max(1, (int)h),
            96, 96, PixelFormats.Pbgra32);
        rtb.Render(canvas);
    }

    // ─── UI scroll simulation tests ─────────────────────────────────

    [StaFact]
    public void UIScroll_43Rows_ScrollUpPreservesCompression()
    {
        int bufferRows = 43;
        int canvasRows = 43;
        int cols = 155;
        var emulator = new TerminalEmulator(cols, bufferRows);

        var sb = new StringBuilder();
        for (int i = 0; i < 80; i++)
        {
            if (i > 0) sb.Append("\r\n");
            sb.Append($"prior-output-line-{i + 1:D4}");
        }
        emulator.Feed(Encoding.UTF8.GetBytes(sb.ToString()));
        emulator.Feed(Encoding.UTF8.GetBytes("\x1b[2J\x1b[H"));
        emulator.Feed(Encoding.UTF8.GetBytes(BuildTuiTableScreen(cols)));

        var buffer = emulator.Buffer;
        var viewport = new TerminalViewport { IsLive = true, ScrollOffset = 0 };
        var canvas = CreateAndRenderCanvas(emulator, viewport, cols, canvasRows);

        // Baseline: verify compression works at scrollOffset=0
        var (fullHeight0, compressed0) = CountInteriorEmptyHeights(buffer, canvas, 0);
        Assert.True(compressed0 > 0,
            $"Baseline: expected compressed empties. full={fullHeight0}, compressed={compressed0}");

        // Scroll up 1 notch (scrollOffset goes from 0 → 3)
        ScrollUpAndRerender(canvas, viewport, buffer);
        var (fullHeight1, compressed1) = CountInteriorEmptyHeights(buffer, canvas, viewport.ScrollOffset);
        var detail1 = DescribeFullHeightInteriorEmpties(buffer, canvas, viewport.ScrollOffset);
        Assert.True(compressed1 > 0,
            $"After 1 scroll-up (offset={viewport.ScrollOffset}): no compressed empties. " +
            $"full={fullHeight1}, compressed={compressed1}");
        Assert.True(fullHeight1 == 0,
            $"After 1 scroll-up (offset={viewport.ScrollOffset}): {fullHeight1} full-height interior empties.\n{detail1}");

        // Scroll up 2 more notches (offset → 6, 9)
        ScrollUpAndRerender(canvas, viewport, buffer, 2);
        var (fullHeight2, compressed2) = CountInteriorEmptyHeights(buffer, canvas, viewport.ScrollOffset);
        var detail2 = DescribeFullHeightInteriorEmpties(buffer, canvas, viewport.ScrollOffset);
        Assert.True(compressed2 > 0,
            $"After 3 scroll-ups (offset={viewport.ScrollOffset}): no compressed empties. " +
            $"full={fullHeight2}, compressed={compressed2}");
        Assert.True(fullHeight2 == 0,
            $"After 3 scroll-ups (offset={viewport.ScrollOffset}): {fullHeight2} full-height interior empties.\n{detail2}");
    }

    [StaFact]
    public void UIScroll_53Rows_ScrollUpPreservesCompression()
    {
        int bufferRows = 53;
        int canvasRows = 53;
        int cols = 155;
        var emulator = new TerminalEmulator(cols, bufferRows);

        var sb = new StringBuilder();
        for (int i = 0; i < 100; i++)
        {
            if (i > 0) sb.Append("\r\n");
            sb.Append($"prior-output-line-{i + 1:D4}");
        }
        emulator.Feed(Encoding.UTF8.GetBytes(sb.ToString()));
        emulator.Feed(Encoding.UTF8.GetBytes("\x1b[2J\x1b[H"));
        emulator.Feed(Encoding.UTF8.GetBytes(BuildTuiScreen53(cols)));

        var buffer = emulator.Buffer;
        var viewport = new TerminalViewport { IsLive = true, ScrollOffset = 0 };
        var canvas = CreateAndRenderCanvas(emulator, viewport, cols, canvasRows);

        // Baseline
        var (fullHeight0, compressed0) = CountInteriorEmptyHeights(buffer, canvas, 0);
        Assert.True(compressed0 > 0,
            $"Baseline: expected compressed empties. full={fullHeight0}, compressed={compressed0}");

        // Scroll up 1, 2, 3 notches — check after each
        for (int notch = 1; notch <= 3; notch++)
        {
            ScrollUpAndRerender(canvas, viewport, buffer);
            var (fullHeight, compressed) = CountInteriorEmptyHeights(buffer, canvas, viewport.ScrollOffset);
            var detail = DescribeFullHeightInteriorEmpties(buffer, canvas, viewport.ScrollOffset);
            Assert.True(compressed > 0,
                $"After {notch} scroll-up(s) (offset={viewport.ScrollOffset}): " +
                $"no compressed empties. full={fullHeight}, compressed={compressed}");
            Assert.True(fullHeight == 0,
                $"After {notch} scroll-up(s) (offset={viewport.ScrollOffset}): " +
                $"{fullHeight} full-height interior empties.\n{detail}");
        }
    }

    [StaFact]
    public void UIScroll_53Rows_CursorHidden_ScrollUpPreservesCompression()
    {
        int bufferRows = 53;
        int canvasRows = 53;
        int cols = 155;
        var emulator = new TerminalEmulator(cols, bufferRows);

        var sb = new StringBuilder();
        for (int i = 0; i < 100; i++)
        {
            if (i > 0) sb.Append("\r\n");
            sb.Append($"prior-output-line-{i + 1:D4}");
        }
        emulator.Feed(Encoding.UTF8.GetBytes(sb.ToString()));
        emulator.Feed(Encoding.UTF8.GetBytes("\x1b[2J\x1b[H"));
        emulator.Feed(Encoding.UTF8.GetBytes(BuildTuiScreen53(cols)));
        emulator.Feed(Encoding.UTF8.GetBytes("\x1b[?25l")); // hide cursor

        var buffer = emulator.Buffer;
        var viewport = new TerminalViewport { IsLive = true, ScrollOffset = 0 };
        var canvas = CreateAndRenderCanvas(emulator, viewport, cols, canvasRows);

        // Baseline
        var (fullHeight0, compressed0) = CountInteriorEmptyHeights(buffer, canvas, 0);
        Assert.True(compressed0 > 0,
            $"Baseline (cursor hidden): expected compressed empties. full={fullHeight0}, compressed={compressed0}");

        for (int notch = 1; notch <= 3; notch++)
        {
            ScrollUpAndRerender(canvas, viewport, buffer);
            var (fullHeight, compressed) = CountInteriorEmptyHeights(buffer, canvas, viewport.ScrollOffset);
            Assert.True(compressed > 0,
                $"After {notch} scroll-up(s) (offset={viewport.ScrollOffset}, cursor hidden): " +
                $"no compressed empties. full={fullHeight}, compressed={compressed}");
            Assert.True(fullHeight == 0,
                $"After {notch} scroll-up(s) (offset={viewport.ScrollOffset}, cursor hidden): " +
                $"{fullHeight} full-height interior empties.");
        }
    }
}
