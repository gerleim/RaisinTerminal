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

        // Extra rows should be 0: the newest scrollback lines are all empty,
        // so no extra rows should be pulled from compression.
        Assert.Equal(0, canvas._extraRows);
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
    /// Bug: Claude Code TUI fills the entire buffer (no trailing empties).
    /// The TUI layout has content on most rows, with a few interior empty
    /// rows (blank separators between output and status bar). Since the
    /// buffer is full (displayedBaseRows == baseRowCount), the code takes
    /// the ComputeLayout (bottom-aligned) path. Compression shrinks
    /// interior empties → total content height &lt; canvas height →
    /// ComputeLayout puts pos[0] &gt; 0 → visible gap at the top.
    ///
    /// After scrolling up, the code path changes (extraRows changes) and
    /// the gap disappears — proving the content CAN fill the screen.
    ///
    /// Correct behavior: no gap at top. When compression saves space in a
    /// full buffer, either skip compression for the last few empties or
    /// use the saved space to show scrollback above.
    /// </summary>
    [StaFact]
    public void LivePane_FullBuffer_InteriorEmpties_NoGapAtTop()
    {
        int bufferRows = 43;
        int canvasRows = 43;
        var emulator = new TerminalEmulator(80, bufferRows);

        // Simulate Claude TUI: content fills all 43 rows with some blank
        // separator rows in the middle. Crucially, the LAST row has content
        // so there are NO trailing empties → displayedBaseRows == baseRowCount.
        //
        // Layout: rows 0-35 content, rows 36-37 empty (interior separators),
        // rows 38-42 content (status area, prompt, status bar)
        var sb = new StringBuilder();
        for (int i = 0; i < 36; i++)
        {
            if (i > 0) sb.Append("\r\n");
            sb.Append($"output-row-{i + 1:D3}");
        }
        emulator.Feed(Encoding.UTF8.GetBytes(sb.ToString()));

        // 3 \r\n = move to row 36, then 37, then 38 — leaving rows 36-37 empty
        emulator.Feed(Encoding.UTF8.GetBytes("\r\n\r\n\r\n"));

        // Status area filling rows 38-42 (5 rows, last has no trailing \r\n)
        emulator.Feed(Encoding.UTF8.GetBytes("━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━\r\n"));
        emulator.Feed(Encoding.UTF8.GetBytes("❯ next\r\n"));
        emulator.Feed(Encoding.UTF8.GetBytes("━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━\r\n"));
        emulator.Feed(Encoding.UTF8.GetBytes("* Working... (5s)\r\n"));
        emulator.Feed(Encoding.UTF8.GetBytes("━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━"));

        // Verify setup: last row has content (no trailing empties)
        var buffer = emulator.Buffer;
        bool lastRowEmpty = true;
        for (int c = 0; c < buffer.Columns; c++)
        {
            var cell = buffer.GetCell(buffer.Rows - 1, c);
            if (cell.Character != ' ' && cell.Character != '\0')
            {
                lastRowEmpty = false;
                break;
            }
        }
        Assert.False(lastRowEmpty, "Test setup: last row must have content (no trailing empties)");

        var canvas = RenderCanvas(emulator, null, 80, canvasRows);

        double firstRowY = canvas._rowYPositions![0];
        Assert.True(firstRowY <= 0,
            $"Gap at top: first row starts at Y={firstRowY:F1}. " +
            $"Full buffer with interior compression should not create a gap at top. " +
            $"(extraRows={canvas._extraRows}, totalRendered={(canvas._rowYPositions.Length - 1)}, " +
            $"baseRowCount={canvas._baseRowCount})");
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
    /// This is the case where extraRows == 0 and the fix must skip
    /// compression to avoid a gap at the top.
    /// </summary>
    [StaFact]
    public void LivePane_ClaudeTUI_TableOutput_NoScrollback_NoGapAtTop()
    {
        int bufferRows = 43;
        int canvasRows = 43;
        int cols = 155;
        var emulator = new TerminalEmulator(cols, bufferRows);

        emulator.Feed(Encoding.UTF8.GetBytes(BuildTuiTableScreen(cols)));

        Assert.Equal(0, emulator.Buffer.ScrollbackCount);

        var canvas = RenderCanvas(emulator, null, cols, canvasRows);

        double firstRowY = canvas._rowYPositions![0];
        Assert.True(firstRowY <= 0,
            $"Gap at top (no scrollback): first row Y={firstRowY:F1}. " +
            $"(extraRows={canvas._extraRows}, baseRowCount={canvas._baseRowCount})");
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
}
