using RaisinTerminal.Core.Models;
using RaisinTerminal.Core.Terminal;
using Xunit;

namespace RaisinTerminal.Tests;

// ============================================================================
// Level 1: Basic Text Output
// Establish that plain text fills the screen and overflows correctly.
// ============================================================================

public class Level1_BasicTextTests
{
    [Fact]
    public void BasicText_FillsScreen()
    {
        var t = new TerminalTestHarness(20, 4);
        t.FeedLines("AAA", "BBB", "CCC", "DDD")
         .AssertScreenRows("AAA", "BBB", "CCC", "DDD")
         .AssertScrollbackCount(0);
    }

    [Fact]
    public void BasicText_Overflow_ScrollsToScrollback()
    {
        var t = new TerminalTestHarness(20, 4);
        t.FeedLines("L0", "L1", "L2", "L3", "L4", "L5")
         .AssertScrollbackCount(2)
         .AssertScreenRow(0, "L2")
         .AssertScreenRow(3, "L5");
    }

    [Fact]
    public void BasicText_ScrollbackContent_MatchesOverflow()
    {
        var t = new TerminalTestHarness(20, 4);
        t.FeedLines("L0", "L1", "L2", "L3", "L4", "L5")
         .AssertAllScrollback("L0", "L1")
         .AssertScreenRows("L2", "L3", "L4", "L5");
    }

    [Fact]
    public void BasicText_TotalContent_IsComplete()
    {
        var t = new TerminalTestHarness(20, 4);
        t.FeedLines("L0", "L1", "L2", "L3", "L4", "L5")
         .AssertTotalContent("L0", "L1", "L2", "L3", "L4", "L5");
    }

    [Fact]
    public void BasicText_ExactFit_NoScrollback()
    {
        var t = new TerminalTestHarness(20, 4);
        t.FeedLines("A", "B", "C", "D")
         .AssertScrollbackCount(0)
         .AssertScreenRows("A", "B", "C", "D");
    }
}

// ============================================================================
// Level 2: Simple Resize (no TUI, no suppression)
// Plain text + resize, verifying content preservation.
// ============================================================================

public class Level2_SimpleResizeTests
{
    [Fact]
    public void Resize_Shrink_PushesExcessToScrollback()
    {
        var t = new TerminalTestHarness(10, 6);
        t.FeedLines("A", "B", "C", "D", "E", "F")
         .Resize(10, 4)
         .AssertAllScrollback("A", "B")
         .AssertScreenRows("C", "D", "E", "F");
    }

    [Fact]
    public void Resize_Grow_PullsFromScrollback()
    {
        var t = new TerminalTestHarness(10, 6);
        t.FeedLines("A", "B", "C", "D", "E", "F")
         .Resize(10, 4)
         .Resize(10, 6)
         .AssertScrollbackCount(0)
         .AssertScreenRows("A", "B", "C", "D", "E", "F");
    }

    [Fact]
    public void Resize_ShrinkGrow_RoundTrip_PreservesAllContent()
    {
        var t = new TerminalTestHarness(10, 6);
        t.FeedLines("A", "B", "C", "D", "E", "F");
        var before = t.TakeSnapshot();

        t.Resize(10, 3)
         .Resize(10, 6)
         .AssertMatchesSnapshot(before);
    }

    [Fact]
    public void Resize_ColumnShrink_PreservesRowCount()
    {
        var t = new TerminalTestHarness(20, 4);
        t.FeedLines("AAAA", "BBBB", "CCCC", "DDDD")
         .Resize(10, 4)
         .AssertScreenRow(0, "AAAA")
         .AssertScreenRow(3, "DDDD")
         .AssertScrollbackCount(0);
    }

    [Fact]
    public void Resize_CursorBelowNewHeight_AdjustsCursorAndShifts()
    {
        var t = new TerminalTestHarness(10, 6);
        t.FeedLines("A", "B", "C", "D", "E", "F")
         .AssertCursor(5, 1) // after writing F, cursor at row 5
         .Resize(10, 4)
         .AssertCursor(3, 1); // cursor adjusted to fit
    }

    [Fact]
    public void Resize_CursorAboveNewHeight_NoShift()
    {
        var t = new TerminalTestHarness(10, 6);
        t.FeedLines("A", "B") // cursor at row 1
         .Resize(10, 4)
         .AssertCursor(1, 1)
         .AssertScrollbackCount(0)
         .AssertScreenRow(0, "A")
         .AssertScreenRow(1, "B");
    }

    [Fact]
    public void Resize_MultipleShrinksAndGrows_PreservesContent()
    {
        var t = new TerminalTestHarness(10, 8);
        t.FeedLines("A", "B", "C", "D", "E", "F", "G", "H");
        var original = t.TakeSnapshot();

        t.Resize(10, 4)
         .Resize(10, 2)
         .Resize(10, 8)
         .AssertMatchesSnapshot(original);
    }
}

// ============================================================================
// Level 3: Cursor Movement + Erase (TUI-like patterns)
// Test the fundamental building blocks that Claude's TUI uses.
// ============================================================================

public class Level3_CursorAndEraseTests
{
    [Fact]
    public void CursorHome_ThenRewrite_OverwritesScreen()
    {
        var t = new TerminalTestHarness(20, 4);
        t.FeedLines("OLD1", "OLD2", "OLD3", "OLD4")
         .CursorHome()
         .FeedLines("NEW1", "NEW2", "NEW3", "NEW4")
         .AssertScreenRows("NEW1", "NEW2", "NEW3", "NEW4");
    }

    [Fact]
    public void EraseDisplay_ClearsScreen()
    {
        var t = new TerminalTestHarness(20, 4);
        t.FeedLines("A", "B", "C", "D")
         .EraseDisplay(2)
         .AssertScreenRows("", "", "", "");
    }

    [Fact]
    public void EraseLine_ClearsFromCursor()
    {
        var t = new TerminalTestHarness(20, 1);
        t.Feed("ABCDEF")
         .CursorTo(1, 4)  // 1-based: row 1, col 4
         .EraseLine(0)     // erase from cursor to end
         .AssertScreenRow(0, "ABC");
    }

    [Fact]
    public void CursorHome_Rewrite_NoScrollbackGrowth()
    {
        var t = new TerminalTestHarness(20, 4);
        t.FeedLines("A", "B", "C", "D");
        int sbBefore = t.Buffer.ScrollbackCount;

        t.CursorHome()
         .FeedLines("X", "Y", "Z", "W");
        Assert.Equal(sbBefore, t.Buffer.ScrollbackCount);
    }

    [Fact]
    public void FullScreenRedraw_MultipleFrames_NoScrollbackGrowth()
    {
        var t = new TerminalTestHarness(20, 4);
        t.FeedLines("A", "B", "C", "D");

        for (int frame = 0; frame < 5; frame++)
        {
            t.CursorHome()
             .FeedLines($"F{frame}-0", $"F{frame}-1", $"F{frame}-2", $"F{frame}-3");
        }

        t.AssertScrollbackCount(0)
         .AssertScreenRows("F4-0", "F4-1", "F4-2", "F4-3");
    }

    [Fact]
    public void CursorHome_Rewrite_MoreLinesThanScreen_OverflowsToScrollback()
    {
        var t = new TerminalTestHarness(20, 4);
        t.FeedLines("A", "B", "C", "D")
         .CursorHome()
         .FeedLines("L0", "L1", "L2", "L3", "L4", "L5")
         .AssertScrollbackCount(2)
         .AssertScreenRows("L2", "L3", "L4", "L5");
    }
}

// ============================================================================
// Level 4: TUI Patterns — Scroll Region + Insert/Delete Lines
// What generic TUI apps do (less, vim, etc.)
// ============================================================================

public class Level4_TuiPatternsTests
{
    [Fact]
    public void ScrollRegion_ScrollUp_OnlyAffectsRegion()
    {
        var t = new TerminalTestHarness(10, 6);
        t.FeedLines("HEAD", "R1", "R2", "R3", "R4", "FOOT")
         .SetScrollRegion(2, 5)  // 1-based: rows 2-5
         .ScrollUp(1)
         .AssertScreenRow(0, "HEAD")
         .AssertScreenRow(1, "R2")     // shifted up
         .AssertScreenRow(2, "R3")
         .AssertScreenRow(3, "R4")
         .AssertScreenRow(4, "")       // cleared
         .AssertScreenRow(5, "FOOT");
    }

    [Fact]
    public void InsertLine_ShiftsDown_WithinScrollRegion()
    {
        var t = new TerminalTestHarness(10, 4);
        t.FeedLines("A", "B", "C", "D")
         .SetScrollRegion(1, 4) // 1-based: full screen
         .CursorTo(2, 1)       // cursor at row 2 (1-based)
         .InsertLines(1)
         .AssertScreenRow(0, "A")
         .AssertScreenRow(1, "")   // inserted blank
         .AssertScreenRow(2, "B")  // shifted down
         .AssertScreenRow(3, "C"); // D falls off
    }

    [Fact]
    public void DeleteLine_ShiftsUp_WithinScrollRegion()
    {
        var t = new TerminalTestHarness(10, 4);
        t.FeedLines("A", "B", "C", "D")
         .SetScrollRegion(1, 4)
         .CursorTo(2, 1) // row 2 (1-based)
         .DeleteLines(1)
         .AssertScreenRow(0, "A")
         .AssertScreenRow(1, "C")  // shifted up
         .AssertScreenRow(2, "D")
         .AssertScreenRow(3, "");  // cleared
    }

    [Fact]
    public void Resize_PreservesScrollRegionIfValid()
    {
        var t = new TerminalTestHarness(10, 10);
        t.SetScrollRegion(2, 8); // 1-based: rows 2-8 → 0-based: 1-7
        Assert.Equal(1, t.Buffer.ScrollTop);
        Assert.Equal(7, t.Buffer.ScrollBottom);

        t.Resize(10, 12);
        Assert.Equal(1, t.Buffer.ScrollTop);
        Assert.Equal(7, t.Buffer.ScrollBottom);
    }

    [Fact]
    public void Resize_ResetsScrollRegionIfOutOfBounds()
    {
        var t = new TerminalTestHarness(10, 10);
        t.SetScrollRegion(2, 10); // 0-based: 1-9
        Assert.Equal(1, t.Buffer.ScrollTop);
        Assert.Equal(9, t.Buffer.ScrollBottom);

        t.Resize(10, 6);
        // Region bottom (9) >= new rows (6), must reset
        Assert.Equal(0, t.Buffer.ScrollTop);
        Assert.Equal(5, t.Buffer.ScrollBottom);
    }
}

// ============================================================================
// Level 5: Claude-Specific Suppression (no resize)
// Test the scrollback suppression and deferred overflow logic.
// ============================================================================

public class Level5_ClaudeSuppressionTests
{
    [Fact]
    public void ClaudeSuppression_InitialFrame_FlowsToScrollback()
    {
        var t = new TerminalTestHarness(20, 4);
        t.SetClaudeRedrawSuppression(true)
         .CursorHome()
         .FeedLines("H0", "H1", "H2", "H3", "H4", "H5");

        Assert.True(t.Buffer.ScrollbackCount >= 2,
            $"Initial frame must populate scrollback (got {t.Buffer.ScrollbackCount})");
    }

    [Fact]
    public void ClaudeSuppression_SteadyState_DoesNotGrowScrollback()
    {
        var t = new TerminalTestHarness(20, 4);
        t.SetClaudeRedrawSuppression(true)
         .CursorHome()
         .FeedLines("A", "B", "C", "D", "E");
        int afterInitial = t.Buffer.ScrollbackCount;

        // Frames 2-4: identical redraws
        for (int i = 0; i < 3; i++)
        {
            t.CursorHome()
             .FeedLines("A", "B", "C", "D", "E");
        }

        Assert.Equal(afterInitial, t.Buffer.ScrollbackCount);
    }

    [Fact]
    public void ClaudeSuppression_GrowingResponse_DeferredOnlyNewRows()
    {
        var t = new TerminalTestHarness(20, 4);
        t.SetClaudeRedrawSuppression(true);

        // Frame 1: 5 lines in 4-row viewport → 1 overflow committed
        t.CursorHome().FeedLines("L0", "L1", "L2", "L3", "L4");
        int afterInitial = t.Buffer.ScrollbackCount;
        Assert.True(afterInitial >= 1);

        // Frame 2: identical (deferred, not committed)
        t.CursorHome().FeedLines("L0", "L1", "L2", "L3", "L4");
        Assert.Equal(afterInitial, t.Buffer.ScrollbackCount);

        // Frame 3: grows to 7 lines → 3 overflow deferred, 1 overlaps with committed
        t.CursorHome().FeedLines("L0", "L1", "L2", "L3", "L4", "L5", "L6");
        Assert.Equal(afterInitial, t.Buffer.ScrollbackCount);

        // Exit: flush → only 2 new rows committed (not L0 again)
        t.SetClaudeRedrawSuppression(false);
        int flushed = t.Buffer.ScrollbackCount - afterInitial;
        Assert.Equal(2, flushed);
    }

    [Fact]
    public void ClaudeSuppression_IdenticalFrames_ZeroFlushed()
    {
        var t = new TerminalTestHarness(20, 4);
        t.SetClaudeRedrawSuppression(true);

        // Frame 1
        t.CursorHome().FeedLines("A", "B", "C", "D", "E");
        int afterInitial = t.Buffer.ScrollbackCount;

        // Frames 2-4: identical
        for (int i = 0; i < 3; i++)
            t.CursorHome().FeedLines("A", "B", "C", "D", "E");

        t.SetClaudeRedrawSuppression(false);
        Assert.Equal(0, t.Buffer.ScrollbackCount - afterInitial);
    }

    [Fact]
    public void ClaudeSuppression_StreamingThenRedraw_DeferredRowsNotDropped()
    {
        // Reproduces the bug: Claude streams output with SuppressScrollback off
        // (first CUP is skipped), then a TUI redraw activates suppression.
        // The deferred rows from the redraw overflow were incorrectly skipped
        // by FlushDeferredScrollback because _scrollbackCountAtDeferStart was
        // captured at DeferScrollbackOnSuppress=true time (before streaming),
        // not when suppression actually activated.
        var t = new TerminalTestHarness(20, 4);
        t.SetClaudeRedrawSuppression(true);

        // First CUP is skipped (_skipNextCursorHomeSuppress) — no suppression
        t.CursorHome();
        Assert.False(t.Buffer.SuppressScrollback, "First CUP should be skipped");

        // Stream 10 lines as normal output (SuppressScrollback=false)
        // Lines go to real scrollback
        t.FeedLines("S0", "S1", "S2", "S3", "S4", "S5", "S6", "S7", "S8", "S9");
        int afterStreaming = t.Buffer.ScrollbackCount;
        Assert.True(afterStreaming >= 6, "Streaming should add to scrollback");

        // Second CUP: TUI redraw — activates SuppressScrollback
        t.CursorHome();
        Assert.True(t.Buffer.SuppressScrollback, "Second CUP should activate suppression");

        // TUI redraws 6 lines (2 overflow to deferred)
        t.FeedLines("R0", "R1", "R2", "R3", "R4", "R5");

        // Exit: flush deferred rows
        t.SetClaudeRedrawSuppression(false);

        // The 2 overflow rows from the TUI redraw must be preserved
        int flushed = t.Buffer.ScrollbackCount - afterStreaming;
        Assert.True(flushed >= 2,
            $"TUI redraw overflow must be flushed, not skipped (got {flushed})");
    }

    [Fact]
    public void ClaudeSuppression_CupContentSurvivesIdleFlushAndScroll()
    {
        // Reproduces: Claude streams content via CUP to rows near the bottom,
        // an idle flush commits deferred scrollback, then more streaming output
        // scrolls the CUP-written content off-screen. The CUP-written rows
        // must appear in scrollback after the second idle flush.
        var t = new TerminalTestHarness(30, 10);
        t.SetClaudeRedrawSuppression(true);

        // Frame 1: initial render fills screen, activates suppression
        t.CursorHome();
        Assert.False(t.Buffer.SuppressScrollback, "First CUP skipped");
        t.CursorHome();
        Assert.True(t.Buffer.SuppressScrollback, "Second CUP activates suppression");

        // Fill screen with baseline content (rows 0-9)
        t.FeedLines("R0", "R1", "R2", "R3", "R4", "R5", "R6", "R7", "R8", "R9");

        // Simulate idle flush — commits any deferred overflow
        t.Buffer.FlushDeferredScrollback();
        int afterFlush1 = t.Buffer.ScrollbackCount;

        // Now Claude streams NEW content via CUP at rows near the bottom
        // (mimics Claude painting "EN header" at row 8, blockquote at row 9)
        t.CursorTo(8, 1).Feed("EN HEADER LINE HERE");
        t.NewLine();
        t.Feed("BLOCKQUOTE TEXT HERE IMPORTANT");
        // Cursor is at row 9 (bottom) after writing blockquote

        // More output scrolls everything up — 12 new lines pushes all content through
        t.NewLine();
        for (int i = 0; i < 11; i++)
            t.Feed($"NEW{i}\r\n");
        t.Feed("LAST");

        // Simulate idle flush
        t.Buffer.FlushDeferredScrollback();

        // The CUP-written content must survive in scrollback
        var allScrollback = t.GetAllScrollbackLines();
        bool foundHeader = allScrollback.Any(l => l.Contains("EN HEADER"));
        bool foundBlockquote = allScrollback.Any(l => l.Contains("BLOCKQUOTE TEXT"));

        Assert.True(foundHeader,
            $"EN HEADER must be in scrollback. Scrollback ({allScrollback.Length} lines): " +
            string.Join(" | ", allScrollback.Select((l, i) => $"[{i}]={l}")));
        Assert.True(foundBlockquote,
            $"BLOCKQUOTE TEXT must be in scrollback. Scrollback ({allScrollback.Length} lines): " +
            string.Join(" | ", allScrollback.Select((l, i) => $"[{i}]={l}")));
    }

    [Fact]
    public void ClaudeSuppression_WrappedLineThenCup_BlockquotePreserved()
    {
        // Real scenario: 152-col/49-row terminal. CUP 45;3 writes header.
        // CRLF. 154 spaces (>152 cols → wrap). Title on next row. CRLF.
        // Blockquote text at the row below. CUP 49;3 targets the last row.
        //
        // With enough rows between header and bottom, blockquote is safe.
        // But when header is close to the bottom AND the wrap-induced scroll
        // pushes blockquote onto the CUP target row, it gets overwritten.
        //
        // Reproduces with 30-col, 20-row screen:
        // CUP to row 16 (4 from bottom), wrap adds extra row, blockquote
        // and CUP 20;3 target different rows — no overwrite.
        var t = new TerminalTestHarness(30, 20);
        t.SetClaudeRedrawSuppression(true);
        t.CursorHome();
        t.CursorHome();

        // Fill baseline
        for (int i = 0; i < 20; i++)
            t.Feed($"R{i}\r\n");
        t.Buffer.FlushDeferredScrollback();

        // CUP to row 16 for header
        t.CursorTo(16, 3).Feed("EN HEADER");
        // CRLF then 34 spaces (wraps: 30 on current row, 4 on next)
        t.NewLine();
        t.Feed(new string(' ', 34) + "TITLE");
        t.NewLine();
        t.Feed("BLOCKQUOTE TEXT IMPORTANT");
        // CUP to last row
        t.CursorTo(20, 3).Feed("CONTINUATION LINE");
        for (int i = 0; i < 25; i++)
            t.Feed($"\r\nSCROLL{i}");
        t.Buffer.FlushDeferredScrollback();

        var totalContent = t.GetAllScrollbackLines()
            .Concat(t.GetAllScreenRows()).ToArray();

        bool foundBlockquote = totalContent.Any(l => l.Contains("BLOCKQUOTE TEXT IMPORTANT"));
        Assert.True(foundBlockquote,
            "BLOCKQUOTE TEXT must survive:\n" +
            string.Join("\n", totalContent.Select((l, i) => $"  [{i}] \"{l}\"")));

        int titleIdx = Array.FindIndex(totalContent, l => l.Contains("TITLE"));
        int blockIdx = Array.FindIndex(totalContent, l => l.Contains("BLOCKQUOTE TEXT"));
        int contIdx = Array.FindIndex(totalContent, l => l.Contains("CONTINUATION"));
        Assert.True(titleIdx < blockIdx && blockIdx < contIdx,
            $"Order: TITLE[{titleIdx}] < BLOCKQUOTE[{blockIdx}] < CONTINUATION[{contIdx}]");
    }

    [Fact]
    public void ClaudeSuppression_RealRawReplay_BlockquotePreservedInScrollback()
    {
        // Replay the exact raw bytes from the real session where the EN
        // blockquote text disappeared after "▎ Switchable Mechanisms".
        // Screen: 152 cols × 49 rows. Pre-fill rows 0-43 with content,
        // then replay the raw data starting from CUP 45;3.
        var t = new TerminalTestHarness(152, 49);
        t.SetClaudeRedrawSuppression(true);
        t.CursorHome();
        t.CursorHome();

        // Fill rows 0-43 with baseline
        for (int i = 0; i < 44; i++)
            t.Feed($"ROW{i}\r\n");
        t.Buffer.FlushDeferredScrollback();

        // Replay the raw section starting from CUP 45;3
        var path = Path.Combine(AppContext.BaseDirectory, "..", "..", "..",
            "TestData", "interchangeable_parts_section.raw");
        t.FeedFile(path);
        t.Buffer.FlushDeferredScrollback();

        var totalContent = t.GetAllScrollbackLines()
            .Concat(t.GetAllScreenRows()).ToArray();

        // The EN blockquote text must be present somewhere
        bool foundBlockquote = totalContent.Any(l =>
            l.Contains("The specimens can be anywhere"));
        Assert.True(foundBlockquote,
            "EN blockquote text must survive in total content. " +
            $"Total lines: {totalContent.Length}. " +
            "Lines containing 'Switchable': " +
            string.Join(", ", totalContent.Select((l, i) => (l, i))
                .Where(x => x.l.Contains("Switchable") || x.l.Contains("specimens"))
                .Select(x => $"[{x.i}]=\"{x.l.Substring(0, Math.Min(60, x.l.Length))}...\"")));
    }

    [Fact]
    public void ClaudeSuppression_RealRawReplay_BlockquoteVisibleWhenScrolledUp()
    {
        var t = new TerminalTestHarness(152, 49);
        t.SetClaudeRedrawSuppression(true);
        t.CursorHome();
        t.CursorHome();

        for (int i = 0; i < 44; i++)
            t.Feed($"ROW{i}\r\n");
        t.Buffer.FlushDeferredScrollback();

        var path = Path.Combine(AppContext.BaseDirectory, "..", "..", "..",
            "TestData", "interchangeable_parts_section.raw");
        t.FeedFile(path);
        t.Buffer.FlushDeferredScrollback();

        var totalContent = t.GetAllScrollbackLines()
            .Concat(t.GetAllScreenRows()).ToArray();
        int scrollbackCount = t.Buffer.ScrollbackCount;

        int titleIdx = -1, blockquoteIdx = -1, huIdx = -1;
        for (int i = 0; i < totalContent.Length; i++)
        {
            if (totalContent[i].Contains("Switchable Mechanisms") && titleIdx == -1)
                titleIdx = i;
            if (totalContent[i].Contains("The specimens can be anywhere"))
                blockquoteIdx = i;
            if (totalContent[i].Contains("HU (REF DOC"))
                huIdx = i;
        }

        Assert.True(titleIdx >= 0, "Title 'Switchable Mechanisms' must be in total content");
        Assert.True(blockquoteIdx >= 0,
            $"Blockquote must be in total content. Title at [{titleIdx}]. " +
            $"Nearby: " + string.Join(" | ",
                totalContent.Skip(Math.Max(0, titleIdx - 1)).Take(8)
                .Select((l, i) => $"[{titleIdx - 1 + i}]=\"{l.Substring(0, Math.Min(60, l.Length))}\"")));
        Assert.True(titleIdx < blockquoteIdx, "Title before blockquote");
        Assert.True(huIdx < 0 || blockquoteIdx < huIdx, "Blockquote before HU");

        int viewRows = 49;
        int maxOffset = ViewportCalculator.MaxScrollOffset(
            t.Buffer.Rows, viewRows, scrollbackCount);

        // Scroll so the title row is near the top of the viewport.
        // titleIdx is an absolute index in total content.
        // To show absolute row R at viewport row 0, scrollOffset =
        //   scrollbackCount + bufferRows - viewRows - R
        // which is the same as maxOffset - R (when buffer == viewRows).
        int scrollOffset = Math.Max(0, Math.Min(maxOffset,
            scrollbackCount + t.Buffer.Rows - viewRows - titleIdx));

        var visibleRows = t.GetVisibleRows(scrollOffset, viewRows);
        bool blockquoteVisible = visibleRows.Any(r =>
            r.Contains("The specimens can be anywhere"));

        Assert.True(blockquoteVisible,
            $"Blockquote must be visible when scrolled to title area. " +
            $"scrollOffset={scrollOffset}, maxOffset={maxOffset}, " +
            $"scrollbackCount={scrollbackCount}, titleIdx={titleIdx}, " +
            $"blockquoteIdx={blockquoteIdx}. " +
            $"Visible content: " + string.Join(" | ",
                visibleRows.Where(r => !string.IsNullOrWhiteSpace(r))
                .Take(15)
                .Select(r => $"\"{r.Substring(0, Math.Min(60, r.Length))}\"")));
    }

    [Fact]
    public void ClaudeSuppression_DeferredScrollbackSurvivesNextFrame()
    {
        // Reproduces the bug: content scrolled into deferred scrollback
        // during one TUI frame is lost when CUP 1;1 starts the next frame.
        // Setup: 40 cols × 5 rows.
        // Frame 0: initial render (CUP 1;1 skips suppression per _skipNextCursorHomeSuppress)
        // Frame 1: CUP 1;1 enables SuppressScrollback. Overflow goes to deferred.
        // Frame 2: CUP 1;1 clears deferred → content lost!
        var t = new TerminalTestHarness(40, 5);
        t.SetClaudeRedrawSuppression(true);

        // Frame 0: first CUP 1;1 after enabling suppression — skip flag consumed
        t.CursorHome();
        t.FeedLines("INIT1", "INIT2", "INIT3", "INIT4", "INIT5");

        // Frame 1: second CUP 1;1 → enables SuppressScrollback
        t.CursorHome();
        t.FeedLines("ALPHA", "BRAVO", "CHARLIE", "DELTA", "ECHO");
        // Screen now: ALPHA/BRAVO/CHARLIE/DELTA/ECHO
        // Add more lines → ALPHA, BRAVO scroll into deferred scrollback
        t.Feed("\r\n");
        t.Feed("FOXTROT\r\n");
        t.Feed("GOLF");
        // Deferred should contain ALPHA and BRAVO

        // Frame 2: third CUP 1;1 → ClearDeferredScrollback loses ALPHA/BRAVO
        t.CursorHome();
        t.FeedLines("NEW1", "NEW2", "NEW3", "NEW4", "NEW5");

        // ALPHA and BRAVO must survive in real scrollback
        var scrollback = t.GetAllScrollbackLines();
        Assert.Contains("ALPHA", scrollback);
        Assert.Contains("BRAVO", scrollback);
    }

    [Fact]
    public void ClaudeSuppression_ExitAndReenter_SkipRearms()
    {
        var t = new TerminalTestHarness(20, 4);
        t.SetClaudeRedrawSuppression(true);

        // Session 1: two frames
        t.CursorHome().FeedLines("A", "B", "C", "D", "E", "F");
        t.CursorHome().FeedLines("X", "Y", "Z", "W", "V", "U"); // suppressed

        t.SetClaudeRedrawSuppression(false);
        t.SetClaudeRedrawSuppression(true); // re-enter

        int before = t.Buffer.ScrollbackCount;
        t.CursorHome().FeedLines("P", "Q", "R", "S", "T", "U");
        Assert.True(t.Buffer.ScrollbackCount > before,
            "Post-restart first frame must flow to scrollback");
    }
}

// ============================================================================
// Level 6: Resize During Claude Suppression (the actual bugs)
// These tests exercise the known failure modes.
// ============================================================================

public class Level6_ResizeDuringSuppressionTests
{
    [Fact]
    public void ResizeShrink_DuringSuppression_PreservesRows()
    {
        var t = new TerminalTestHarness(10, 6);
        t.SetClaudeRedrawSuppression(true);

        // Frame 1 (initial — flows to scrollback): fill screen
        t.CursorHome().FeedLines("A", "B", "C", "D", "E", "F");

        // Frame 2 (steady-state — suppressed): rewrite same content
        // Cursor ends at row 5 after writing all lines
        t.CursorHome().FeedLines("A", "B", "C", "D", "E", "F");
        Assert.True(t.Buffer.SuppressScrollback, "Steady-state suppression should be active");
        Assert.Equal(5, t.Buffer.CursorRow);

        // Resize smaller: rows must be preserved, not discarded
        t.Resize(10, 4);
        Assert.True(t.Buffer.ScrollbackCount >= 2,
            $"Resize should push excess rows to scrollback (got {t.Buffer.ScrollbackCount})");
    }

    [Fact]
    public void ResizeShrink_ThenConPTYReflow_NoReenableSuppression()
    {
        var t = new TerminalTestHarness(10, 6);
        t.FeedLines("A", "B", "C", "D", "E", "F");
        t.SetClaudeRedrawSuppression(true);
        t.CursorHome();
        t.CursorHome(); // steady-state

        t.Resize(10, 4);
        Assert.False(t.Buffer.SuppressScrollback, "Suppression should be lifted after resize");

        // ConPTY reflow: ED 2 + CUP 1;1 in same Feed batch
        t.Feed("\x1b[2J\x1b[H");
        Assert.False(t.Buffer.SuppressScrollback,
            "ConPTY reflow must not re-enable suppression during grace");
    }

    [Fact]
    public void ResizeShrink_GraceThenClaudeRedraw_ReenablesSuppression()
    {
        var t = new TerminalTestHarness(10, 6);
        t.FeedLines("A", "B", "C", "D", "E", "F");
        t.SetClaudeRedrawSuppression(true);
        t.CursorHome();
        t.CursorHome(); // steady-state

        t.Resize(10, 4);

        // Grace Feed: ConPTY reflow
        t.Feed("\x1b[2J\x1b[H");
        Assert.False(t.Buffer.SuppressScrollback);

        // Next Feed: Claude's SIGWINCH redraw
        t.CursorHome();
        Assert.True(t.Buffer.SuppressScrollback,
            "Claude's redraw after grace should re-enable suppression");
    }

    [Fact]
    public void ResizeShrink_BetweenFrames_NoScrollbackDuplication()
    {
        var t = new TerminalTestHarness(20, 4);
        t.SetClaudeRedrawSuppression(true);

        // Frame 1: initial render (5 lines, 1 overflow committed)
        t.CursorHome().FeedLines("L0", "L1", "L2", "L3", "L4");
        int afterInitial = t.Buffer.ScrollbackCount;
        Assert.True(afterInitial >= 1);

        // Resize smaller between frames
        t.Resize(20, 3);

        // Frame 2 with new dimensions (4 lines, 1 overflow deferred)
        t.CursorHome().FeedLines("L0", "L1", "L2", "L3");

        // Exit: flush deferred
        t.SetClaudeRedrawSuppression(false);

        // Verify no duplicates
        t.AssertNoDuplicateScrollback();
    }

    [Fact]
    public void ResizeGrow_AfterShrinkDuringSuppression_PullsBack()
    {
        var t = new TerminalTestHarness(10, 6);
        t.SetClaudeRedrawSuppression(true);

        // Frame 1 (initial): fill screen
        t.CursorHome().FeedLines("A", "B", "C", "D", "E", "F");

        // Frame 2 (steady-state): cursor ends at row 5
        t.CursorHome().FeedLines("A", "B", "C", "D", "E", "F");

        // Shrink then grow back
        t.Resize(10, 4);
        int sbAfterShrink = t.Buffer.ScrollbackCount;
        Assert.True(sbAfterShrink >= 2, "Shrink should have pushed rows");

        t.Resize(10, 6);
        Assert.True(t.Buffer.ScrollbackCount < sbAfterShrink,
            "Grow after shrink should pull rows back from scrollback");
    }

    [Fact]
    public void ResizeShrink_MidFrame_ContentStillCoherent()
    {
        var t = new TerminalTestHarness(20, 6);
        t.SetClaudeRedrawSuppression(true);

        // Frame 1: initial render (full) — each line erases to EOL like real TUI
        t.CursorHome();
        for (int i = 0; i < 6; i++)
            t.Feed($"L{i}").EraseLine(0).NewLine();

        // Frame 2: start writing (3 of 6 lines)
        t.CursorHome();
        for (int i = 0; i < 3; i++)
            t.Feed($"M{i}").EraseLine(0).NewLine();

        // Resize mid-frame
        t.Resize(20, 4);

        // New complete frame with correct dimensions (as Claude would after SIGWINCH)
        t.CursorHome();
        for (int i = 0; i < 4; i++)
        {
            t.Feed($"N{i}").EraseLine(0);
            if (i < 3) t.NewLine();
        }

        t.AssertScreenRow(0, "N0");
        t.AssertScreenRow(1, "N1");
        t.AssertScreenRow(2, "N2");
        t.AssertScreenRow(3, "N3");
    }

    [Fact]
    public void MultipleResizes_DuringSuppression_NoAccumulation()
    {
        var t = new TerminalTestHarness(10, 8);
        t.FeedLines("A", "B", "C", "D", "E", "F", "G", "H");

        t.SetClaudeRedrawSuppression(true);
        t.CursorHome();
        t.CursorHome(); // steady-state

        // Multiple resizes
        t.Resize(10, 6);
        t.Resize(10, 4);
        t.Resize(10, 8);

        // After growing back, verify content is reasonable
        // (exact behavior depends on pull-back logic, but no crashes/asserts)
        Assert.True(t.Buffer.Rows == 8);
        Assert.True(t.Buffer.CursorRow >= 0 && t.Buffer.CursorRow < 8);
    }

    [Fact]
    public void Resize_WithDeferredScrollback_CountTrackingCorrect()
    {
        var t = new TerminalTestHarness(20, 4);
        t.SetClaudeRedrawSuppression(true);

        // Frame 1: initial render (6 lines, 2 overflow → committed to scrollback)
        t.CursorHome().FeedLines("L0", "L1", "L2", "L3", "L4", "L5");
        int afterInitial = t.Buffer.ScrollbackCount;

        // Frame 2: steady-state (same content, overflow → deferred)
        t.CursorHome().FeedLines("L0", "L1", "L2", "L3", "L4", "L5");
        Assert.Equal(afterInitial, t.Buffer.ScrollbackCount);

        // Resize between frames (pushes rows to real scrollback)
        t.Resize(20, 3);
        int afterResize = t.Buffer.ScrollbackCount;

        // Frame 3: new size (5 lines, 2 overflow → deferred)
        t.CursorHome().FeedLines("L0", "L1", "L2", "L3", "L4");

        // Exit: flush deferred
        t.SetClaudeRedrawSuppression(false);

        // Key assertion: no duplicate lines in scrollback
        t.AssertNoDuplicateScrollback();
    }

    [Fact]
    public void ResizeShrink_NoSuppression_PreservesContent()
    {
        // Baseline: resize without any Claude suppression should always work
        var t = new TerminalTestHarness(10, 6);
        t.FeedLines("A", "B", "C", "D", "E", "F")
         .Resize(10, 4)
         .AssertTotalContent("A", "B", "C", "D", "E", "F")
         .Resize(10, 6)
         .AssertTotalContent("A", "B", "C", "D", "E", "F");
    }
}

// ============================================================================
// Level 7: Split-Pane (Viewport) Tests
// Two viewports on one buffer: live pane (bottom) and pinned pane (scrolled back).
// ============================================================================

public class Level7_SplitPaneTests
{
    [Fact]
    public void SplitPane_PinnedViewport_SeesScrollback()
    {
        // 10-col, 6-row buffer. Fill with 10 lines so 4 are in scrollback.
        var t = new TerminalTestHarness(10, 6);
        t.FeedLines("L0", "L1", "L2", "L3", "L4", "L5", "L6", "L7", "L8", "L9");
        t.AssertScrollbackCount(4);

        // Pinned pane: 3 rows, scrolled back by 4 (looking at scrollback start)
        // viewRows=3 means the pane shows 3 rows of the total content
        // scrollOffset=4 shifts the view up by 4 from the live bottom
        // Total lines = 4 scrollback + 6 screen = 10
        // lineIndex = 10 - 3 - 4 + viewRow = 3 + viewRow
        // So viewRow 0 → lineIndex 3 → scrollback[3] = "L3"
        //    viewRow 1 → lineIndex 4 → screen[0] = "L4"
        //    viewRow 2 → lineIndex 5 → screen[1] = "L5"
        t.AssertVisibleRows(4, 3, "L3", "L4", "L5");
    }

    [Fact]
    public void SplitPane_LiveViewport_SeesBottomOfScreen()
    {
        var t = new TerminalTestHarness(10, 6);
        t.FeedLines("L0", "L1", "L2", "L3", "L4", "L5", "L6", "L7", "L8", "L9");

        // Live pane: 3 rows, scrollOffset=0 → bottom of screen
        // lineIndex = 10 - 3 - 0 + viewRow = 7 + viewRow
        // viewRow 0 → lineIndex 7 → screen[3] = "L7"
        // viewRow 1 → lineIndex 8 → screen[4] = "L8"
        // viewRow 2 → lineIndex 9 → screen[5] = "L9"
        t.AssertVisibleRows(0, 3, "L7", "L8", "L9");
    }

    [Fact]
    public void SplitPane_ViewportOffsetStable_AsNewLinesArrive()
    {
        var t = new TerminalTestHarness(10, 6);
        t.FeedLines("L0", "L1", "L2", "L3", "L4", "L5");

        // Add pinned viewport scrolled back by 2
        var pinned = t.AddViewport(isLive: false, scrollOffset: 2);

        // Feed 3 more lines (start with \r\n so L6 is on its own row)
        t.Feed("\r\nL6\r\nL7\r\nL8");
        Assert.Equal(3, t.Buffer.ScrollbackCount);

        // Viewport offset should have been bumped: 2 + 3 = 5
        Assert.Equal(5, pinned.ScrollOffset);

        // Pinned pane (3 rows, offset 5) should still see content near where it started
        t.AssertVisibleRow(0, pinned.ScrollOffset, 3, "L1");
    }

    [Fact]
    public void SplitPane_ResizeShrink_ViewportOffsetAdjusted()
    {
        var t = new TerminalTestHarness(10, 6);
        t.FeedLines("A", "B", "C", "D", "E", "F");
        // cursor at row 5, no scrollback

        var pinned = t.AddViewport(isLive: false, scrollOffset: 2);

        // Resize smaller: pushes 2 rows to scrollback
        t.Resize(10, 4);
        t.AssertScrollbackCount(2);

        // Pinned viewport offset should increase: 2 + 2 = 4
        Assert.Equal(4, pinned.ScrollOffset);
    }

    [Fact]
    public void SplitPane_ResizeGrow_ViewportOffsetAdjusted()
    {
        var t = new TerminalTestHarness(10, 6);
        t.FeedLines("A", "B", "C", "D", "E", "F");

        var pinned = t.AddViewport(isLive: false, scrollOffset: 2);

        // Shrink then grow
        t.Resize(10, 4);
        int offsetAfterShrink = pinned.ScrollOffset; // 2 + 2 = 4

        t.Resize(10, 6);
        // Grow pulls 2 rows back, offset decreases: 4 - 2 = 2
        Assert.Equal(offsetAfterShrink - 2, pinned.ScrollOffset);
    }

    [Fact]
    public void SplitPane_BothPanesShowCorrectContent_AfterResize()
    {
        // Start with content already in scrollback so the pinned pane has
        // something to look at.
        var t = new TerminalTestHarness(10, 4);
        t.FeedLines("S0", "S1", "S2", "S3", "S4", "S5", "S6", "S7");
        // scrollback: S0,S1,S2,S3 (4 lines), screen: S4,S5,S6,S7
        t.AssertScrollbackCount(4);

        // Pinned viewport looking at the scrollback area (offset 4 = 4 lines back)
        var pinned = t.AddViewport(isLive: false, scrollOffset: 4);
        // Total = 4 + 4 = 8, 3-row view at offset 4:
        // lineIndex = 8 - 3 - 4 + vr = 1 + vr → scrollback[1],scrollback[2],scrollback[3]
        t.AssertVisibleRows(pinned.ScrollOffset, 3, "S1", "S2", "S3");

        // Resize smaller: 4→3 rows, pushes 1 row to scrollback
        t.Resize(10, 3);
        // scrollback: S0,S1,S2,S3,S4 (5 lines), screen: S5,S6,S7
        t.AssertScrollbackCount(5);

        // Pinned offset bumped: 4+1=5
        Assert.Equal(5, pinned.ScrollOffset);

        // Live pane (3 rows, offset=0): bottom of screen
        t.AssertVisibleRows(0, 3, "S5", "S6", "S7");

        // Pinned pane: still sees the same content
        // Total = 5 + 3 = 8, 3-row view at offset 5:
        // lineIndex = 8 - 3 - 5 + vr = 0 + vr → scrollback[0],scrollback[1],scrollback[2]
        t.AssertVisibleRows(pinned.ScrollOffset, 3, "S0", "S1", "S2");
    }

    [Fact]
    public void SplitPane_NoDuplication_BetweenPanes()
    {
        // Ensure no line appears in both pinned and live views simultaneously
        // (unless they genuinely overlap in the buffer)
        var t = new TerminalTestHarness(10, 6);
        t.FeedLines("L0", "L1", "L2", "L3", "L4", "L5", "L6", "L7", "L8", "L9");
        // scrollback: L0-L3, screen: L4-L9

        // Pinned: 4 rows at offset 6 → sees L0-L3
        var pinnedRows = t.GetVisibleRows(6, 4);
        // Live: 4 rows at offset 0 → sees L6-L9
        var liveRows = t.GetVisibleRows(0, 4);

        // No overlap between pinned and live
        var overlap = pinnedRows.Intersect(liveRows).Where(r => r != "").ToArray();
        Assert.Empty(overlap);
    }

    [Fact]
    public void SplitPane_ScrollbackGrowth_DoesNotCorruptPinnedView()
    {
        var t = new TerminalTestHarness(10, 4);
        t.FeedLines("A", "B", "C", "D");

        // Pinned viewport must start scrolled back (offset > 0) to be tracked
        // Feed one line to create scrollback, then set pinned offset
        t.Feed("\r\nE");
        t.AssertScrollbackCount(1); // "A" in scrollback
        var pinned = t.AddViewport(isLive: false, scrollOffset: 1);

        // Feed many more lines
        for (int i = 0; i < 19; i++)
            t.Feed($"\r\nN{i:D2}");

        // Pinned offset should have tracked: 1 + 19 = 20
        Assert.Equal(20, pinned.ScrollOffset);

        // Pinned pane sees the original content area
        // scrollback has 20 lines, screen has 4 lines, total 24
        // 4-row view at offset 20: lineIndex = 24 - 4 - 20 + vr = vr
        // viewRow 0 → scrollback[0] = "A"
        t.AssertVisibleRow(0, pinned.ScrollOffset, 4, "A");
    }

    [Fact]
    public void SplitPane_ResizeDuringSuppression_PinnedStable()
    {
        var t = new TerminalTestHarness(10, 6);
        t.FeedLines("L0", "L1", "L2", "L3", "L4", "L5");

        var pinned = t.AddViewport(isLive: false, scrollOffset: 0);

        // Enable Claude suppression, enter steady-state
        t.SetClaudeRedrawSuppression(true);
        t.CursorHome().FeedLines("L0", "L1", "L2", "L3", "L4", "L5"); // frame 1
        t.CursorHome().FeedLines("L0", "L1", "L2", "L3", "L4", "L5"); // frame 2 (suppressed)

        int offsetBefore = pinned.ScrollOffset;

        // Resize smaller
        t.Resize(10, 4);

        // Pinned viewport should have adjusted (increased by pushed rows)
        Assert.True(pinned.ScrollOffset >= offsetBefore,
            $"Pinned offset should not decrease during shrink (was {offsetBefore}, now {pinned.ScrollOffset})");
    }

    [Fact]
    public void SplitPane_ShrinkGrowRoundTrip_PinnedViewPreserved()
    {
        var t = new TerminalTestHarness(10, 6);
        t.FeedLines("A", "B", "C", "D", "E", "F");

        var pinned = t.AddViewport(isLive: false, scrollOffset: 2);

        // Snapshot what the pinned pane sees
        var pinnedContentBefore = t.GetVisibleRows(pinned.ScrollOffset, 3);

        // Shrink then grow back
        t.Resize(10, 4).Resize(10, 6);

        // Pinned pane should see the same content after round-trip
        var pinnedContentAfter = t.GetVisibleRows(pinned.ScrollOffset, 3);
        Assert.Equal(pinnedContentBefore, pinnedContentAfter);
    }

    // --- Split-open initial offset tests ---
    // These mirror the exact logic in TerminalView.SplitView.OpenSplit and
    // SkipPinnedLeadingEmpties to catch leading-empty-row bugs in Core.

    /// <summary>
    /// Computes the initial pinned-pane scroll offset the same way OpenSplit does,
    /// then runs the skip-leading-empties adjustment. Returns the final offset.
    /// </summary>
    private static int ComputePinnedInitialOffset(TerminalTestHarness t, int pinnedCanvasRows)
    {
        var buffer = t.Buffer;
        int offset = ViewportCalculator.PinnedInitialOffset(buffer.Rows, pinnedCanvasRows, buffer.ScrollbackCount);

        // Mirror SkipPinnedLeadingEmpties
        int viewRows = Math.Min(buffer.Rows, pinnedCanvasRows);
        int skip = 0;
        for (int row = 0; row < pinnedCanvasRows; row++)
        {
            bool empty = true;
            for (int c = 0; c < buffer.Columns; c++)
            {
                var cell = buffer.GetVisibleCell(row, c, offset, viewRows);
                if (cell.Character != ' ' && cell.Character != '\0')
                {
                    empty = false;
                    break;
                }
            }
            if (!empty) break;
            skip++;
        }
        if (skip > 0 && skip < pinnedCanvasRows)
            offset = Math.Max(0, offset - skip);

        return offset;
    }

    private static void AssertNoLeadingEmpties(TerminalTestHarness t, int offset, int pinnedCanvasRows)
    {
        int viewRows = Math.Min(t.Buffer.Rows, pinnedCanvasRows);
        var rows = t.GetVisibleRows(offset, viewRows);
        Assert.True(rows.Length > 0 && rows[0] != "",
            $"First visible row in pinned pane is empty. offset={offset}, viewRows={viewRows}, " +
            $"bufferRows={t.Buffer.Rows}, scrollback={t.Buffer.ScrollbackCount}. " +
            $"Rows: [{string.Join(", ", rows.Select(r => $"\"{r}\""))}]");
    }

    [Fact]
    public void SplitOpen_NoLeadingEmpties_FullScreen()
    {
        // Buffer completely full, lots of scrollback
        var t = new TerminalTestHarness(20, 24);
        for (int i = 0; i < 100; i++)
            t.Feed($"Line{i:D3}\r\n");

        int pinnedRows = 10;
        int offset = ComputePinnedInitialOffset(t, pinnedRows);
        AssertNoLeadingEmpties(t, offset, pinnedRows);
    }

    [Fact]
    public void SplitOpen_NoLeadingEmpties_CursorMidScreen()
    {
        // Buffer 24 rows, only 12 lines written, cursor at row 11.
        // Bottom half of buffer is empty.
        var t = new TerminalTestHarness(20, 24);
        for (int i = 0; i < 12; i++)
            t.Feed($"Line{i:D2}\r\n");

        int pinnedRows = 10;
        int offset = ComputePinnedInitialOffset(t, pinnedRows);
        AssertNoLeadingEmpties(t, offset, pinnedRows);
    }

    [Fact]
    public void SplitOpen_NoLeadingEmpties_SparseBuffer()
    {
        // Large buffer with cursor near top, most rows empty.
        // Common when a fresh terminal has just a few lines of output.
        var t = new TerminalTestHarness(20, 50);
        for (int i = 0; i < 5; i++)
            t.Feed($"Line{i}\r\n");
        // cursor at row 5, rows 5-49 are empty

        int pinnedRows = 15;
        int offset = ComputePinnedInitialOffset(t, pinnedRows);
        AssertNoLeadingEmpties(t, offset, pinnedRows);
    }

    [Fact]
    public void SplitOpen_NoLeadingEmpties_AfterResize()
    {
        // Content fills screen, then resize shrinks buffer (simulating split)
        var t = new TerminalTestHarness(20, 30);
        for (int i = 0; i < 60; i++)
            t.Feed($"Line{i:D2}\r\n");

        // Simulate split: live canvas shrinks, ConPTY resizes
        t.Resize(20, 15);

        int pinnedRows = 14;
        int offset = ComputePinnedInitialOffset(t, pinnedRows);
        AssertNoLeadingEmpties(t, offset, pinnedRows);
    }

    [Fact]
    public void SplitOpen_NoLeadingEmpties_WithBlankLinesInContent()
    {
        // Content has blank lines interspersed (common in Claude output)
        var t = new TerminalTestHarness(20, 24);
        for (int i = 0; i < 40; i++)
        {
            t.Feed($"Block{i:D2}\r\n");
            t.Feed("\r\n"); // blank line between blocks
        }

        int pinnedRows = 10;
        int offset = ComputePinnedInitialOffset(t, pinnedRows);
        AssertNoLeadingEmpties(t, offset, pinnedRows);
    }

    [Fact]
    public void SplitOpen_NoLeadingEmpties_MinimalScrollback()
    {
        // Just barely enough content to have scrollback
        var t = new TerminalTestHarness(20, 10);
        for (int i = 0; i < 12; i++)
            t.Feed($"L{i:D2}\r\n");
        // 2 lines in scrollback, screen rows 0-9 have content

        int pinnedRows = 5;
        int offset = ComputePinnedInitialOffset(t, pinnedRows);
        AssertNoLeadingEmpties(t, offset, pinnedRows);
    }

    private static bool IsRowEmpty(TerminalTestHarness t, int row, int offset, int viewRows)
    {
        for (int c = 0; c < t.Buffer.Columns; c++)
        {
            var cell = t.Buffer.GetVisibleCell(row, c, offset, viewRows);
            if (cell.Character != ' ' && cell.Character != '\0' && cell.Character != '│')
                return false;
            if (cell.BackgroundR != CellData.DefaultBgR || cell.BackgroundG != CellData.DefaultBgG || cell.BackgroundB != CellData.DefaultBgB)
                return false;
        }
        return true;
    }

    /// <summary>
    /// Mirrors AdjustPinnedForEmpties: skips leading AND trailing empties.
    /// </summary>
    private static int AdjustForEmpties(TerminalTestHarness t, TerminalViewport vp, int pinnedCanvasRows)
    {
        var buffer = t.Buffer;
        int offset = vp.ScrollOffset;
        int viewRows = Math.Min(buffer.Rows, pinnedCanvasRows);
        int maxOffset = ViewportCalculator.MaxScrollOffset(buffer.Rows, pinnedCanvasRows, buffer.ScrollbackCount);

        // First: skip trailing empties by increasing offset to pull more scrollback
        if (offset < maxOffset)
        {
            int trailSkip = 0;
            for (int row = pinnedCanvasRows - 1; row >= 0; row--)
            {
                if (!IsRowEmpty(t, row, offset, viewRows)) break;
                trailSkip++;
            }
            if (trailSkip > 0 && trailSkip < pinnedCanvasRows)
                offset = Math.Min(maxOffset, offset + trailSkip);
        }

        // Then: skip leading empties (takes priority — no blank rows at top)
        if (offset > 0)
        {
            int leadSkip = 0;
            for (int row = 0; row < pinnedCanvasRows; row++)
            {
                if (!IsRowEmpty(t, row, offset, viewRows)) break;
                leadSkip++;
            }
            if (leadSkip > 0 && leadSkip < pinnedCanvasRows)
                offset = Math.Max(0, offset - leadSkip);
        }

        vp.ScrollOffset = offset;
        vp.UserScrolledBack = offset > 0;
        return offset;
    }

    [Fact]
    public void SplitOpen_FullSequence_NoLeadingEmpties()
    {
        // Simulate the EXACT sequence that happens in the real app:
        // 1. Buffer at full window size with lots of content
        // 2. OpenSplit calculates initial offset with PinnedCanvas.Rows=0
        //    (uses buffer.Rows as fallback)
        // 3. Pinned viewport added and tracked
        // 4. ConPTY resize shrinks buffer (live canvas shrunk for split)
        //    → viewport offset auto-adjusted by buffer
        // 5. OnPinnedCanvasSizeChanged recalculates with actual canvas rows
        // 6. SkipPinnedLeadingEmpties runs

        int fullWindowRows = 30;
        var t = new TerminalTestHarness(80, fullWindowRows);

        // Fill with Claude-like output: content with blank lines interspersed
        for (int i = 0; i < 120; i++)
        {
            if (i % 5 == 0)
                t.Feed("\r\n"); // blank line separator
            else
                t.Feed($"Output line {i:D3}: some content here\r\n");
        }

        int scrollbackBefore = t.Buffer.ScrollbackCount;
        Assert.True(scrollbackBefore > 0, "Should have scrollback");

        // Step 2: OpenSplit with PinnedCanvas.Rows=0 → fallback to buffer.Rows
        int fallbackRows = t.Buffer.Rows; // = fullWindowRows = 30
        int initialOffset = ViewportCalculator.PinnedInitialOffset(
            t.Buffer.Rows, fallbackRows, t.Buffer.ScrollbackCount);

        // Step 3: Add tracked viewport
        var pinned = t.AddViewport(isLive: false, scrollOffset: initialOffset);

        // Step 4: ConPTY resize — buffer shrinks to half (split takes half the window)
        int newLiveRows = fullWindowRows / 2; // = 15
        int pinnedCanvasRows = newLiveRows - 1; // pinned pane is slightly smaller than live
        pinned.CanvasRows = pinnedCanvasRows;
        t.Resize(80, newLiveRows);

        // Step 5: OnPinnedCanvasSizeChanged recalculates
        pinned.ScrollOffset = ViewportCalculator.PinnedInitialOffset(
            t.Buffer.Rows, pinnedCanvasRows, t.Buffer.ScrollbackCount);
        pinned.UserScrolledBack = pinned.ScrollOffset > 0;

        // Step 6: SkipPinnedLeadingEmpties
        AdjustForEmpties(t, pinned, pinnedCanvasRows);

        // Assert: no leading empties
        int viewRows = Math.Min(t.Buffer.Rows, pinnedCanvasRows);
        var rows = t.GetVisibleRows(pinned.ScrollOffset, viewRows);
        Assert.True(rows.Length > 0 && rows[0] != "",
            $"Leading empty row after full split-open sequence. " +
            $"offset={pinned.ScrollOffset}, viewRows={viewRows}, " +
            $"bufferRows={t.Buffer.Rows}, scrollback={t.Buffer.ScrollbackCount}. " +
            $"First 5 rows: [{string.Join(", ", rows.Take(5).Select(r => $"\"{r}\""))}]");
    }

    [Fact]
    public void SplitOpen_FullSequence_WithManyBlankLines_NoLeadingEmpties()
    {
        // Same as above but with longer runs of blank lines (3-4 consecutive)
        int fullWindowRows = 30;
        var t = new TerminalTestHarness(80, fullWindowRows);

        for (int i = 0; i < 80; i++)
        {
            t.Feed($"Block {i:D3} content\r\n");
            // 3 blank lines after each block
            t.Feed("\r\n\r\n\r\n");
        }

        int newLiveRows = fullWindowRows / 2;
        int pinnedCanvasRows = newLiveRows - 1;

        var pinned = new TerminalViewport { IsLive = false, CanvasRows = pinnedCanvasRows };
        t.Buffer.Viewports.Add(pinned);

        // Initial offset with canvas not yet measured
        pinned.ScrollOffset = ViewportCalculator.PinnedInitialOffset(
            t.Buffer.Rows, t.Buffer.Rows, t.Buffer.ScrollbackCount);

        // Resize for split
        t.Resize(80, newLiveRows);

        // Recalculate after first sizing
        pinned.ScrollOffset = ViewportCalculator.PinnedInitialOffset(
            t.Buffer.Rows, pinnedCanvasRows, t.Buffer.ScrollbackCount);
        pinned.UserScrolledBack = pinned.ScrollOffset > 0;

        // Skip empties
        AdjustForEmpties(t, pinned, pinnedCanvasRows);

        int viewRows = Math.Min(t.Buffer.Rows, pinnedCanvasRows);
        var rows = t.GetVisibleRows(pinned.ScrollOffset, viewRows);
        Assert.True(rows.Length > 0 && rows[0] != "",
            $"Leading empty row with many blank lines. " +
            $"offset={pinned.ScrollOffset}, viewRows={viewRows}, " +
            $"bufferRows={t.Buffer.Rows}, scrollback={t.Buffer.ScrollbackCount}. " +
            $"First 5 rows: [{string.Join(", ", rows.Take(5).Select(r => $"\"{r}\""))}]");
    }

    [Fact]
    public void SplitPane_ResizeShrink_SmallViewport_ContentPreserved()
    {
        // The buffer auto-adjustment adds pushCount to viewport offset during resize.
        // This is correct when viewRows == buffer.Rows, but WRONG when viewRows < buffer.Rows
        // (pinned pane smaller than buffer). The view shifts backwards by pushCount lines.
        var t = new TerminalTestHarness(20, 30);
        for (int i = 0; i < 60; i++)
            t.Feed($"Line{i:D2}\r\n");
        // scrollback = 30, screen rows 0-29 = Line30..Line59

        // Pinned viewport: 14 rows, scrolled to see specific content
        var pinned = t.AddViewport(isLive: false, scrollOffset: 14);
        pinned.CanvasRows = 14;

        // What does pinned row 0 show?
        int viewRows = Math.Min(t.Buffer.Rows, 14);
        string row0Before = t.GetVisibleRow(0, pinned.ScrollOffset, viewRows);

        // Resize shrink: simulates ConPTY resize when split opens
        t.Resize(20, 14);

        // After resize, pinned row 0 should show the same content
        int viewRowsAfter = Math.Min(t.Buffer.Rows, 14);
        string row0After = t.GetVisibleRow(0, pinned.ScrollOffset, viewRowsAfter);

        Assert.Equal(row0Before, row0After);
    }

    [Fact]
    public void SplitOpen_MaxOffset_NoLeadingEmpties()
    {
        // Verify that scrolling to max offset in the pinned pane doesn't show empties
        var t = new TerminalTestHarness(20, 24);
        for (int i = 0; i < 100; i++)
            t.Feed($"Line{i:D3}\r\n");

        int pinnedRows = 10;
        int maxOffset = ViewportCalculator.MaxScrollOffset(t.Buffer.Rows, pinnedRows, t.Buffer.ScrollbackCount);
        int viewRows = Math.Min(t.Buffer.Rows, pinnedRows);
        var rows = t.GetVisibleRows(maxOffset, viewRows);
        Assert.True(rows.Length > 0 && rows[0] != "",
            $"Leading empty at max offset. maxOffset={maxOffset}, viewRows={viewRows}, " +
            $"bufferRows={t.Buffer.Rows}, scrollback={t.Buffer.ScrollbackCount}. " +
            $"First 5 rows: [{string.Join(", ", rows.Take(5).Select(r => $"\"{r}\""))}]");
    }

    [Fact]
    public void SplitOpen_NoTrailingEmpties_AfterResize()
    {
        // After split-open + ConPTY resize, the pinned pane might show
        // trailing empty rows (content doesn't fill the pane).
        // AdjustForEmpties should increase offset to pull more scrollback in.
        var t = new TerminalTestHarness(20, 30);
        for (int i = 0; i < 80; i++)
            t.Feed($"Line{i:D2}\r\n");

        int pinnedCanvasRows = 14;
        var pinned = t.AddViewport(isLive: false);
        pinned.CanvasRows = pinnedCanvasRows;
        pinned.ScrollOffset = ViewportCalculator.PinnedInitialOffset(
            t.Buffer.Rows, t.Buffer.Rows, t.Buffer.ScrollbackCount);

        t.Resize(20, 15);

        pinned.ScrollOffset = ViewportCalculator.PinnedInitialOffset(
            t.Buffer.Rows, pinnedCanvasRows, t.Buffer.ScrollbackCount);
        pinned.UserScrolledBack = pinned.ScrollOffset > 0;

        AdjustForEmpties(t, pinned, pinnedCanvasRows);

        int viewRows = Math.Min(t.Buffer.Rows, pinnedCanvasRows);
        var rows = t.GetVisibleRows(pinned.ScrollOffset, viewRows);

        // Last visible row should have content (no trailing empties)
        Assert.True(rows[rows.Length - 1] != "",
            $"Trailing empty row. offset={pinned.ScrollOffset}, viewRows={viewRows}. " +
            $"Last 5 rows: [{string.Join(", ", rows.Skip(Math.Max(0, rows.Length - 5)).Select(r => $"\"{r}\""))}]");
    }

    [Fact]
    public void SplitOpen_NoTrailingEmpties_SparseBuffer()
    {
        // Buffer with cursor near top — lots of empty rows below cursor.
        // The pinned pane should not show these trailing empties.
        var t = new TerminalTestHarness(20, 50);
        for (int i = 0; i < 8; i++)
            t.Feed($"Line{i}\r\n");
        // cursor at row 8, rows 8-49 are empty

        int pinnedCanvasRows = 15;
        var pinned = t.AddViewport(isLive: false);
        pinned.CanvasRows = pinnedCanvasRows;
        pinned.ScrollOffset = ViewportCalculator.PinnedInitialOffset(
            t.Buffer.Rows, pinnedCanvasRows, t.Buffer.ScrollbackCount);

        AdjustForEmpties(t, pinned, pinnedCanvasRows);

        int viewRows = Math.Min(t.Buffer.Rows, pinnedCanvasRows);
        var rows = t.GetVisibleRows(pinned.ScrollOffset, viewRows);

        // No leading empties
        Assert.True(rows[0] != "",
            $"Leading empty. offset={pinned.ScrollOffset}. First row: \"{rows[0]}\"");
        // No trailing empties (unless there's genuinely less content than pane rows)
        int contentLines = 8; // we wrote 8 lines
        if (contentLines >= pinnedCanvasRows)
        {
            Assert.True(rows[rows.Length - 1] != "",
                $"Trailing empty. offset={pinned.ScrollOffset}. Last row: \"{rows[rows.Length - 1]}\"");
        }
    }

    [Fact]
    public void SplitOpen_RealWpfSequence_NoTrailingEmpties()
    {
        // Reproduces the EXACT WPF event ordering:
        // 1. Buffer at full window size with Claude-like output
        // 2. OpenSplit() → PinnedInitialOffset with fallback (canvas not sized yet)
        // 3. Pinned viewport added to buffer
        // 4. OnPinnedCanvasSizeChanged fires BEFORE buffer resize →
        //    recalculates offset with actual canvas rows + AdjustForEmpties
        // 5. Live canvas SizeChanged → ConPTY resize → buffer.Resize() →
        //    viewport offset auto-adjusted by resize code
        // 6. Deferred AdjustForEmpties runs with post-resize state
        //
        // The bug: step 4's AdjustForEmpties fixes empties relative to OLD
        // buffer.Rows. Step 5's resize may shift the viewport to show new
        // trailing empties. Step 6 must fix those.

        int fullWindowRows = 50;
        var t = new TerminalTestHarness(80, fullWindowRows);

        // Claude-like output: lots of content with the cursor partway down
        for (int i = 0; i < 200; i++)
            t.Feed($"Output line {i:D3}\r\n");
        // Cursor is at some row, rows below cursor are empty

        int scrollbackBefore = t.Buffer.ScrollbackCount;
        Assert.True(scrollbackBefore > 0);

        // Step 2: OpenSplit with PinnedCanvas.Rows=0 → fallback to buffer.Rows
        int initialOffset = ViewportCalculator.PinnedInitialOffset(
            t.Buffer.Rows, t.Buffer.Rows, t.Buffer.ScrollbackCount);

        // Step 3: Add viewport
        var pinned = t.AddViewport(isLive: false, scrollOffset: initialOffset);

        // Step 4: OnPinnedCanvasSizeChanged fires BEFORE resize
        // PinnedCanvas gets actual size (half the window minus splitter)
        int pinnedCanvasRows = fullWindowRows / 2 - 2;
        pinned.CanvasRows = pinnedCanvasRows;
        pinned.ScrollOffset = ViewportCalculator.PinnedInitialOffset(
            t.Buffer.Rows, pinnedCanvasRows, t.Buffer.ScrollbackCount);
        pinned.UserScrolledBack = pinned.ScrollOffset > 0;
        AdjustForEmpties(t, pinned, pinnedCanvasRows);

        // Step 5: Buffer resize (live canvas shrinks to make room for pinned pane)
        int newLiveRows = fullWindowRows / 2;
        t.Resize(80, newLiveRows);

        // Step 6: Deferred AdjustForEmpties with POST-resize state
        AdjustForEmpties(t, pinned, pinnedCanvasRows);

        // Assert: no leading AND no trailing empties
        int viewRows = Math.Min(t.Buffer.Rows, pinnedCanvasRows);
        var rows = t.GetVisibleRows(pinned.ScrollOffset, viewRows);

        Assert.True(rows.Length > 0 && rows[0] != "",
            $"Leading empty. offset={pinned.ScrollOffset}, viewRows={viewRows}, " +
            $"bufferRows={t.Buffer.Rows}, scrollback={t.Buffer.ScrollbackCount}. " +
            $"First 3: [{string.Join(", ", rows.Take(3).Select(r => $"\"{r}\""))}]");

        Assert.True(rows[rows.Length - 1] != "",
            $"Trailing empty. offset={pinned.ScrollOffset}, viewRows={viewRows}, " +
            $"bufferRows={t.Buffer.Rows}, scrollback={t.Buffer.ScrollbackCount}. " +
            $"Last 3: [{string.Join(", ", rows.Skip(Math.Max(0, rows.Length - 3)).Select(r => $"\"{r}\""))}]");
    }

    [Fact]
    public void SplitOpen_RealWpfSequence_SparseOutput_NoTrailingEmpties()
    {
        // Same as above but with cursor NOT at the bottom of the buffer.
        // This creates empty rows below the cursor in the active screen.
        int fullWindowRows = 50;
        var t = new TerminalTestHarness(80, fullWindowRows);

        // Write only enough output to fill half the screen + some scrollback
        for (int i = 0; i < 70; i++)
            t.Feed($"Line {i:D3}\r\n");
        // cursor at row 20, rows 21-49 are empty in the 50-row buffer

        int pinnedCanvasRows = fullWindowRows / 2 - 2; // 23
        var pinned = t.AddViewport(isLive: false);
        pinned.CanvasRows = pinnedCanvasRows;

        // Step 4: PinnedInitialOffset before resize (buffer still 50 rows)
        pinned.ScrollOffset = ViewportCalculator.PinnedInitialOffset(
            t.Buffer.Rows, pinnedCanvasRows, t.Buffer.ScrollbackCount);
        pinned.UserScrolledBack = pinned.ScrollOffset > 0;
        AdjustForEmpties(t, pinned, pinnedCanvasRows);

        // Step 5: Buffer resize
        int newLiveRows = fullWindowRows / 2; // 25
        t.Resize(80, newLiveRows);

        // Step 6: Deferred AdjustForEmpties after resize
        AdjustForEmpties(t, pinned, pinnedCanvasRows);

        int viewRows = Math.Min(t.Buffer.Rows, pinnedCanvasRows);
        var rows = t.GetVisibleRows(pinned.ScrollOffset, viewRows);

        Assert.True(rows.Length > 0 && rows[0] != "",
            $"Leading empty. offset={pinned.ScrollOffset}, viewRows={viewRows}. " +
            $"First 3: [{string.Join(", ", rows.Take(3).Select(r => $"\"{r}\""))}]");

        // Only assert trailing if there's enough total content
        int totalContent = t.Buffer.ScrollbackCount + t.Buffer.CursorRow + 1;
        if (totalContent >= pinnedCanvasRows)
        {
            Assert.True(rows[rows.Length - 1] != "",
                $"Trailing empty. offset={pinned.ScrollOffset}, viewRows={viewRows}. " +
                $"Last 3: [{string.Join(", ", rows.Skip(Math.Max(0, rows.Length - 3)).Select(r => $"\"{r}\""))}]");
        }
    }

    [Fact]
    public void SplitOpen_RealWpfSequence_CursorMidScreen_NoTrailingEmpties()
    {
        // Key scenario: Claude writes output, cursor is in the middle of the
        // buffer (not at bottom). Rows below cursor are empty. After split open
        // + resize, the pinned pane should not show these trailing empties.
        int fullWindowRows = 50;
        var t = new TerminalTestHarness(80, fullWindowRows);

        for (int i = 0; i < 30; i++)
            t.Feed($"Diff line {i:D3}\r\n");

        int cursorRow = t.Buffer.CursorRow;
        int emptyBelow = fullWindowRows - cursorRow - 1;

        int initialOffset = ViewportCalculator.PinnedInitialOffset(
            t.Buffer.Rows, t.Buffer.Rows, t.Buffer.ScrollbackCount);
        var pinned = t.AddViewport(isLive: false, scrollOffset: initialOffset);

        int pinnedCanvasRows = fullWindowRows / 2 - 2;
        pinned.CanvasRows = pinnedCanvasRows;
        pinned.ScrollOffset = ViewportCalculator.PinnedInitialOffset(
            t.Buffer.Rows, pinnedCanvasRows, t.Buffer.ScrollbackCount);
        pinned.UserScrolledBack = pinned.ScrollOffset > 0;
        int offsetAfterStep4 = AdjustForEmpties(t, pinned, pinnedCanvasRows);

        int newLiveRows = fullWindowRows / 2;
        t.Resize(80, newLiveRows);
        int offsetAfterResize = pinned.ScrollOffset;

        AdjustForEmpties(t, pinned, pinnedCanvasRows);

        int viewRows = Math.Min(t.Buffer.Rows, pinnedCanvasRows);
        var rows = t.GetVisibleRows(pinned.ScrollOffset, viewRows);

        Assert.True(rows[0] != "",
            $"Leading empty. offset={pinned.ScrollOffset}, cursorRow={cursorRow}, " +
            $"emptyBelow={emptyBelow}, offsetAfterStep4={offsetAfterStep4}, " +
            $"offsetAfterResize={offsetAfterResize}");

        int totalContent = t.Buffer.ScrollbackCount + t.Buffer.CursorRow + 1;
        Assert.True(totalContent >= pinnedCanvasRows,
            $"Not enough content ({totalContent}) to fill pinned pane ({pinnedCanvasRows})");

        Assert.True(rows[rows.Length - 1] != "",
            $"Trailing empty! offset={pinned.ScrollOffset}, viewRows={viewRows}, " +
            $"cursorRow={t.Buffer.CursorRow}, bufferRows={t.Buffer.Rows}, " +
            $"scrollback={t.Buffer.ScrollbackCount}, " +
            $"offsetAfterStep4={offsetAfterStep4}, offsetAfterResize={offsetAfterResize}. " +
            $"Last 5: [{string.Join(", ", rows.Skip(Math.Max(0, rows.Length - 5)).Select(r => $"\"{r}\""))}]");
    }

    [Fact]
    public void SplitOpen_RealWpfSequence_PostOutputContinues_NoTrailingEmpties()
    {
        // Simulate: split opens, AdjustForEmpties runs, then Claude continues
        // writing output. The ScrollUpRegion bumps the viewport offset.
        // After more output, are there trailing empties?
        int fullWindowRows = 50;
        var t = new TerminalTestHarness(80, fullWindowRows);

        // Initial Claude output
        for (int i = 0; i < 100; i++)
            t.Feed($"Line {i:D3}\r\n");

        // Open split — full WPF sequence
        var pinned = t.AddViewport(isLive: false);
        int pinnedCanvasRows = fullWindowRows / 2 - 2; // 23
        pinned.CanvasRows = pinnedCanvasRows;
        pinned.ScrollOffset = ViewportCalculator.PinnedInitialOffset(
            t.Buffer.Rows, pinnedCanvasRows, t.Buffer.ScrollbackCount);
        pinned.UserScrolledBack = pinned.ScrollOffset > 0;
        AdjustForEmpties(t, pinned, pinnedCanvasRows);

        t.Resize(80, fullWindowRows / 2);
        AdjustForEmpties(t, pinned, pinnedCanvasRows);

        // Now Claude continues writing MORE output (50 more lines)
        // Each scroll bumps viewports via ScrollUpRegion
        for (int i = 100; i < 150; i++)
            t.Feed($"Line {i:D3}\r\n");

        // Check pinned pane for trailing empties
        int viewRows = Math.Min(t.Buffer.Rows, pinnedCanvasRows);
        var rows = t.GetVisibleRows(pinned.ScrollOffset, viewRows);

        Assert.True(rows[0] != "",
            $"Leading empty after continued output. offset={pinned.ScrollOffset}");

        Assert.True(rows[rows.Length - 1] != "",
            $"Trailing empty after continued output. offset={pinned.ScrollOffset}, " +
            $"viewRows={viewRows}, bufferRows={t.Buffer.Rows}, " +
            $"scrollback={t.Buffer.ScrollbackCount}. " +
            $"Last 5: [{string.Join(", ", rows.Skip(Math.Max(0, rows.Length - 5)).Select(r => $"\"{r}\""))}]");
    }

    [Fact]
    public void SplitOpen_RealWpfSequence_TuiRedraw_NoTrailingEmpties()
    {
        // Simulate: Claude TUI clears and redraws, cursor ends up mid-screen.
        // The pinned pane should handle this.
        int fullWindowRows = 50;
        var t = new TerminalTestHarness(80, fullWindowRows);

        // Initial output to build scrollback
        for (int i = 0; i < 200; i++)
            t.Feed($"Line {i:D3}\r\n");

        // Open split
        var pinned = t.AddViewport(isLive: false);
        int pinnedCanvasRows = fullWindowRows / 2 - 2;
        pinned.CanvasRows = pinnedCanvasRows;
        pinned.ScrollOffset = ViewportCalculator.PinnedInitialOffset(
            t.Buffer.Rows, pinnedCanvasRows, t.Buffer.ScrollbackCount);
        pinned.UserScrolledBack = pinned.ScrollOffset > 0;
        AdjustForEmpties(t, pinned, pinnedCanvasRows);

        t.Resize(80, fullWindowRows / 2);
        AdjustForEmpties(t, pinned, pinnedCanvasRows);

        // Simulate Claude TUI clearing and redrawing (cursor positioning)
        // Move cursor to home and write partial content
        t.Feed("\x1b[H"); // cursor home
        for (int i = 0; i < 10; i++)
            t.Feed($"Redraw line {i}\r\n");
        // cursor at row 10, rows 10-24 have old content from before

        // Check pinned pane — it should still show scrollback content,
        // not affected by active screen changes
        int viewRows = Math.Min(t.Buffer.Rows, pinnedCanvasRows);
        var rows = t.GetVisibleRows(pinned.ScrollOffset, viewRows);

        Assert.True(rows[0] != "",
            $"Leading empty after TUI redraw. offset={pinned.ScrollOffset}");

        Assert.True(rows[rows.Length - 1] != "",
            $"Trailing empty after TUI redraw. offset={pinned.ScrollOffset}, " +
            $"viewRows={viewRows}. " +
            $"Last 5: [{string.Join(", ", rows.Skip(Math.Max(0, rows.Length - 5)).Select(r => $"\"{r}\""))}]");
    }

    [Fact]
    public void SplitOpen_BoxDrawingTrailingRows_TreatedAsEmpty()
    {
        // Claude Code TUI uses │ (U+2502) for borders. The rendering code in
        // TerminalCanvas treats rows with only │ as empty (for compression).
        // AdjustForEmpties must also treat them as empty, otherwise it won't
        // shift the view to replace them with scrollback content, leaving
        // visible empty space at the bottom of the pinned pane.
        int fullWindowRows = 50;
        var t = new TerminalTestHarness(80, fullWindowRows);

        // Build scrollback with normal content
        for (int i = 0; i < 150; i++)
            t.Feed($"Output line {i:D3}\r\n");

        // Now simulate Claude TUI border: rows with only │ at column 0
        // (mimics the vertical border in Claude's tool-use display)
        for (int i = 0; i < 10; i++)
            t.Feed("│\r\n");
        // A few more real content lines after the border
        for (int i = 0; i < 5; i++)
            t.Feed($"Content after border {i}\r\n");

        // Open split
        int pinnedCanvasRows = fullWindowRows / 2 - 2;
        var pinned = t.AddViewport(isLive: false);
        pinned.CanvasRows = pinnedCanvasRows;
        pinned.ScrollOffset = ViewportCalculator.PinnedInitialOffset(
            t.Buffer.Rows, pinnedCanvasRows, t.Buffer.ScrollbackCount);
        pinned.UserScrolledBack = pinned.ScrollOffset > 0;

        AdjustForEmpties(t, pinned, pinnedCanvasRows);

        int viewRows = Math.Min(t.Buffer.Rows, pinnedCanvasRows);
        var rows = t.GetVisibleRows(pinned.ScrollOffset, viewRows);

        // The │-only rows should be treated as empty and skipped
        // Last visible row should have real content, not just │
        string lastRow = rows[rows.Length - 1].Trim();
        Assert.True(lastRow != "" && lastRow != "│",
            $"Trailing row is empty or box-drawing only. offset={pinned.ScrollOffset}, " +
            $"viewRows={viewRows}. Last row: \"{rows[rows.Length - 1]}\"");
    }

    [Fact]
    public void SplitResize_ClaudeRedraw_NoDuplicateScrollback()
    {
        // Reproduces: split opens → buffer resizes (pushes rows to scrollback)
        // → Claude redraws at new size (some rows scroll off → deferred scrollback)
        // → deferred flush should deduplicate against resize-pushed rows.
        var t = new TerminalTestHarness(30, 20);

        // Simulate Claude's TUI: turn on ClaudeRedrawSuppression.
        // First CUP 1;1 is skipped (initial frame), so render content normally.
        t.SetClaudeRedrawSuppression(true);

        // Fill the screen: 20 lines fit exactly, no scrollback yet.
        for (int i = 0; i < 20; i++)
        {
            if (i > 0) t.Feed("\r\n");
            t.Feed($"Item{i:D2}");
        }
        Assert.Equal(0, t.Buffer.ScrollbackCount);

        // Feed 10 more to push content into scrollback (no trailing \r\n on last)
        for (int i = 20; i < 30; i++)
            t.Feed($"\r\nItem{i:D2}");
        // scrollback: Item00..Item09 (10 lines), screen: Item10..Item29
        int sbBefore = t.Buffer.ScrollbackCount;
        Assert.Equal(10, sbBefore);

        // Second CUP 1;1 → enables suppression (not skipped this time).
        t.CursorHome();
        for (int i = 10; i < 30; i++)
        {
            if (i > 10) t.Feed("\r\n");
            t.Feed($"Item{i:D2}");
        }
        // Suppression is on; no new scrollback from this redraw.
        Assert.Equal(sbBefore, t.Buffer.ScrollbackCount);

        // Simulate the split: resize from 20 to 10 rows.
        // Emulator.Resize lifts suppression before buffer resize so rows are preserved.
        t.Resize(30, 10);
        int sbAfterResize = t.Buffer.ScrollbackCount;
        Assert.True(sbAfterResize > sbBefore,
            $"Resize should push rows to scrollback: before={sbBefore}, after={sbAfterResize}");

        // Claude receives SIGWINCH → redraws at 10 rows.
        // CUP 1;1 flushes deferred, then re-enables suppression.
        t.CursorHome();
        for (int i = 10; i < 30; i++)
        {
            if (i > 10) t.Feed("\r\n");
            t.Feed($"Item{i:D2}");
        }
        // Rows that scroll off during this redraw go to deferred scrollback.
        // They duplicate content just pushed by resize.

        // Flush deferred scrollback (simulates idle tick or next CUP 1;1 flush).
        t.Buffer.FlushDeferredScrollback();

        // Check that scrollback has no duplicates anywhere (not just consecutive).
        var allScrollback = t.GetAllScrollbackLines();
        var nonEmpty = allScrollback.Where(l => l != "").ToList();
        var dupes = nonEmpty.GroupBy(l => l).Where(g => g.Count() > 1).ToList();
        Assert.True(dupes.Count == 0,
            $"Duplicate scrollback lines found: [{string.Join(", ", dupes.Select(g => $"\"{g.Key}\" x{g.Count()}"))}]. " +
            $"Total scrollback: {allScrollback.Length}, non-empty: {nonEmpty.Count}");

        // Verify screen content is correct (last 10 items)
        t.AssertScreenRow(0, "Item20");
        t.AssertScreenRow(9, "Item29");
    }

    [Fact]
    public void SplitResize_ClaudeRedraw_ContentNotDisplaced()
    {
        // Reproduces: #8's content vanishes after split because duplicate #5-#7
        // rows from deferred flush displace it. The resize pushes #5-#8 to
        // scrollback; Claude's redraw only re-renders #5-#7 (which duplicate);
        // if dedup fails, #8 gets buried behind extra rows.
        var t = new TerminalTestHarness(30, 20);
        t.SetClaudeRedrawSuppression(true);

        // Build content: items 0-19 on screen, with distinct text per item.
        for (int i = 0; i < 20; i++)
        {
            if (i > 0) t.Feed("\r\n");
            t.Feed($"Item{i:D2}");
        }

        // Push items 0-4 to scrollback, screen: items 5-19 + 5 more
        for (int i = 20; i < 25; i++)
            t.Feed($"\r\nItem{i:D2}");
        Assert.Equal(5, t.Buffer.ScrollbackCount);

        // Second CUP 1;1 → enables suppression
        t.CursorHome();
        for (int i = 5; i < 25; i++)
        {
            if (i > 5) t.Feed("\r\n");
            t.Feed($"Item{i:D2}");
        }

        // Split resize: 20 → 10 rows. Pushes screen[0..9] (Items 5-14) to scrollback.
        t.Resize(30, 10);
        int sbAfterResize = t.Buffer.ScrollbackCount;

        // Claude redraws at 10 rows but only renders items 10-19
        // (items 5-9 scroll off the top → deferred, duplicating resize-pushed rows)
        t.CursorHome();
        for (int i = 10; i < 25; i++)
        {
            if (i > 10) t.Feed("\r\n");
            t.Feed($"Item{i:D2}");
        }

        t.Buffer.FlushDeferredScrollback();

        // Verify ALL items 0-14 appear exactly once in scrollback (no displacement)
        var sb = t.GetAllScrollbackLines().Where(l => l != "").ToList();
        for (int i = 0; i <= 14; i++)
        {
            string expected = $"Item{i:D2}";
            int count = sb.Count(l => l == expected);
            Assert.True(count == 1,
                $"\"{expected}\" appears {count} time(s) in scrollback (expected 1). " +
                $"Scrollback: [{string.Join(", ", sb)}]");
        }
    }
}

/// <summary>
/// Tests for content positioning when the terminal has fewer content lines
/// than the viewport height.
/// </summary>
public class Level8_SparseContentTests
{
    [Fact]
    public void SparseContent_EmptyScrollback_ContentAtTop()
    {
        // Scenario: terminal with only a few lines of content and empty rows
        // pushed to scrollback (e.g., after scrolling through empty space).
        // GetVisibleCell should return content at the top of the viewport.
        var t = new TerminalTestHarness(40, 20);

        // Write 5 lines of content
        t.FeedLines("Line1", "Line2", "Line3", "Line4", "Line5");
        Assert.Equal(4, t.Buffer.CursorRow); // cursor on row 4

        // Scroll enough times to push ALL content + some empty rows into scrollback.
        // Cursor at row 4, need 16 LFs to reach bottom (row 19) and start scrolling,
        // then 5 more to push the content rows, then more to push empties.
        for (int i = 0; i < 30; i++)
            t.Feed("\n");

        // Now scrollback has 5 content lines + some empty lines
        Assert.True(t.Buffer.ScrollbackCount > 5,
            $"Expected scrollback > 5, got {t.Buffer.ScrollbackCount}");

        // EffectiveScrollbackCount should only count the non-empty lines
        Assert.Equal(5, t.Buffer.EffectiveScrollbackCount);
    }

    [Fact]
    public void SparseContent_AllEmptyScrollback_EffectiveCountIsZero()
    {
        var t = new TerminalTestHarness(40, 10);

        // Just do line feeds to push empty rows into scrollback
        for (int i = 0; i < 15; i++)
            t.Feed("\n");

        Assert.True(t.Buffer.ScrollbackCount > 0);
        Assert.Equal(0, t.Buffer.EffectiveScrollbackCount);
    }

    [Fact]
    public void SparseContent_MaxScrollOffset_ZeroWhenAllScrollbackEmpty()
    {
        var t = new TerminalTestHarness(40, 20);

        // Write a few lines, then scroll to push empties
        t.FeedLines("Hello", "World");
        for (int i = 0; i < 25; i++)
            t.Feed("\n");

        int canvasRows = 20;
        int effectiveMax = ViewportCalculator.MaxScrollOffset(
            t.Buffer.Rows, canvasRows, t.Buffer.EffectiveScrollbackCount);

        // With only 2 effective scrollback lines and 20 canvas rows,
        // maxOffset should be small (2 + 20 - 20 = 2) but NOT include
        // the empty trailing scrollback
        Assert.True(effectiveMax <= 2,
            $"Effective max scroll should be small, got {effectiveMax}");
    }

    [Fact]
    public void SparseContent_ViewportShowsContentAtTop_AtScrollOffsetZero()
    {
        // When scrollOffset=0, the viewport should show the live screen.
        // Content written to rows 0-4 should appear at viewRows 0-4.
        var t = new TerminalTestHarness(40, 20);

        t.FeedLines("Line1", "Line2", "Line3", "Line4", "Line5");

        // Push 15 empty rows into scrollback by scrolling
        for (int i = 0; i < 15; i++)
            t.Feed("\n");

        // At scrollOffset=0, the live screen should still have content
        // (it was scrolled up, so screen is now empty with content in scrollback)
        // This tests that GetVisibleCell at offset 0 shows the live screen
        var rows = t.GetVisibleRows(0, 20);
        // After scrolling 15+5=20 times on a 20-row terminal,
        // the content was pushed to scrollback and screen is empty
        // At scrollOffset=0 we see the (empty) live screen
        // The point is: no rendering artifact places content at bottom

        // Extra rows from compression should not pull empty scrollback lines
        int effectiveScrollback = t.Buffer.EffectiveScrollbackCount;
        int extraRows = ViewportCalculator.ExtraRowsFromCompression(
            savedPixels: 200, cellHeight: 19, emptyRowScale: 0.35,
            scrollbackCount: effectiveScrollback, viewOffset: 0, scrollOffset: 0);

        // With effectiveScrollback=5, we might get some extra rows,
        // but not from the empty trailing scrollback
        int maxPossibleExtra = ViewportCalculator.ExtraRowsFromCompression(
            savedPixels: 200, cellHeight: 19, emptyRowScale: 0.35,
            scrollbackCount: t.Buffer.ScrollbackCount, viewOffset: 0, scrollOffset: 0);

        Assert.True(extraRows <= effectiveScrollback,
            $"Extra rows ({extraRows}) should not exceed effective scrollback ({effectiveScrollback}), " +
            $"but would be {maxPossibleExtra} with full scrollback");
    }

    [Fact]
    public void ComputeLayout_AlwaysBottomAligns()
    {
        var isEmpty = new bool[8]; // 8 rows, less than canvas capacity
        double cellHeight = 19;
        double canvasHeight = 40 * cellHeight; // space for 40 rows

        var positions = RowLayoutCalculator.ComputeLayout(
            isEmpty, cursorRow: 7, cellHeight, emptyRowScale: 0.35, canvasHeight);

        // Last row ends at canvasHeight (bottom-aligned)
        Assert.Equal(canvasHeight, positions[8]);
        // First row starts offset from top
        Assert.Equal(canvasHeight - 8 * cellHeight, positions[0]);
    }

    [Fact]
    public void SparseContent_LayoutBottomAligned_WhenFull()
    {
        // When content fills or exceeds the canvas, keep bottom-alignment.
        var isEmpty = new bool[40]; // exactly fills canvas
        double cellHeight = 19;
        double canvasHeight = 40 * cellHeight;

        var positions = RowLayoutCalculator.ComputeLayout(
            isEmpty, cursorRow: 39, cellHeight, emptyRowScale: 0.35, canvasHeight);

        // Last row ends at canvas height (bottom-aligned)
        Assert.Equal(canvasHeight, positions[40]);
    }

    [Fact]
    public void SparseContent_SplitView_InteriorEmptyRowsCompressed()
    {
        // Regression: opening split view caused empty lines between content
        // to appear at full height. ComputeLayout must bottom-align and
        // compress interior empty rows so they don't show as gaps.
        double cellHeight = 19;
        double emptyRowScale = 0.35;
        double emptyHeight = Math.Round(cellHeight * emptyRowScale);

        // Simulate live pane in split: 15 visible rows, content at top
        // and bottom with empty rows in between (rows 3-10 empty)
        var isEmpty = new bool[15];
        for (int i = 3; i <= 10; i++) isEmpty[i] = true;

        var positions = RowLayoutCalculator.ComputeLayout(
            isEmpty, cursorRow: 14, cellHeight, emptyRowScale, 15 * cellHeight);

        // Bottom-aligned: last row ends at canvas height
        Assert.Equal(15 * cellHeight, positions[15]);

        // Interior empty rows should be compressed
        for (int i = 3; i <= 10; i++)
        {
            double rowHeight = positions[i + 1] - positions[i];
            Assert.Equal(emptyHeight, rowHeight);
        }

        // Non-empty rows should be full height
        Assert.Equal(cellHeight, positions[1] - positions[0]);
        Assert.Equal(cellHeight, positions[12] - positions[11]);
    }

    [Fact]
    public void SparseContent_AllEmptyBeforeLastContent_Compressed()
    {
        // All empty rows before the last non-empty row are compressed,
        // including leading empty rows. This keeps scrollback compact.
        double cellHeight = 19;
        double emptyRowScale = 0.35;
        double emptyHeight = Math.Round(cellHeight * emptyRowScale);

        // Rows 0-5 empty, rows 6-14 content
        var isEmpty = new bool[15];
        for (int i = 0; i <= 5; i++) isEmpty[i] = true;

        var positions = RowLayoutCalculator.ComputeLayout(
            isEmpty, cursorRow: 14, cellHeight, emptyRowScale, 15 * cellHeight);

        // Empty rows before last content are compressed
        for (int i = 0; i <= 5; i++)
        {
            double rowHeight = positions[i + 1] - positions[i];
            Assert.Equal(emptyHeight, rowHeight);
        }
    }
}