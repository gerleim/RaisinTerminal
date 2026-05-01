using System.Text;
using RaisinTerminal.Core.Terminal;
using Xunit;

namespace RaisinTerminal.Tests;

public class TerminalEmulatorTests
{
    private static TerminalEmulator Create(int cols = 80, int rows = 24)
        => new(cols, rows);

    private static void Feed(TerminalEmulator emu, string text)
        => emu.Feed(Encoding.UTF8.GetBytes(text));

    [Fact]
    public void Print_WritesCharactersToBuffer()
    {
        var emu = Create();
        Feed(emu, "AB");

        Assert.Equal('A', emu.Buffer.GetCell(0, 0).Character);
        Assert.Equal('B', emu.Buffer.GetCell(0, 1).Character);
        Assert.Equal(2, emu.Buffer.CursorCol);
    }

    [Fact]
    public void CursorPosition_MovesToCorrectLocation()
    {
        var emu = Create();
        // ESC[3;5H — move to row 3, col 5 (1-based)
        Feed(emu, "\x1b[3;5H");

        Assert.Equal(2, emu.Buffer.CursorRow); // 0-based
        Assert.Equal(4, emu.Buffer.CursorCol);
    }

    [Fact]
    public void Sgr_SetsRedForeground()
    {
        var emu = Create();
        Feed(emu, "\x1b[31mX");

        var cell = emu.Buffer.GetCell(0, 0);
        Assert.Equal('X', cell.Character);
        Assert.Equal(205, cell.ForegroundR);
        Assert.Equal(49, cell.ForegroundG);
        Assert.Equal(49, cell.ForegroundB);
    }

    [Fact]
    public void Sgr_Reset_RestoresDefaults()
    {
        var emu = Create();
        Feed(emu, "\x1b[31m\x1b[0mX");

        var cell = emu.Buffer.GetCell(0, 0);
        Assert.Equal(204, cell.ForegroundR);
        Assert.Equal(204, cell.ForegroundG);
        Assert.Equal(204, cell.ForegroundB);
    }

    [Fact]
    public void Sgr_Bold_SetsAttribute()
    {
        var emu = Create();
        Feed(emu, "\x1b[1mX");

        Assert.True(emu.Buffer.GetCell(0, 0).Bold);
    }

    [Fact]
    public void EraseDisplay_ClearsScreen()
    {
        var emu = Create(10, 5);
        Feed(emu, "XXXXXXXXXX");
        Feed(emu, "\x1b[2J");

        for (int c = 0; c < 10; c++)
            Assert.Equal(' ', emu.Buffer.GetCell(0, c).Character);
    }

    [Fact]
    public void LineFeed_ScrollsIntoScrollback()
    {
        var emu = Create(10, 3);
        Feed(emu, "Row0\r\nRow1\r\nRow2\r\n");

        // After 3 LFs in a 3-row buffer, "Row0" should be in scrollback
        Assert.Equal(1, emu.Buffer.ScrollbackCount);
        var sbLine = emu.Buffer.GetScrollbackLine(0);
        Assert.Equal('R', sbLine[0].Character);
        Assert.Equal('o', sbLine[1].Character);
        Assert.Equal('w', sbLine[2].Character);
        Assert.Equal('0', sbLine[3].Character);
    }

    [Fact]
    public void AlternateScreen_SwitchAndRestore()
    {
        var emu = Create(10, 5);
        Feed(emu, "Hello");

        // Enter alternate screen
        Feed(emu, "\x1b[?1049h");
        Assert.True(emu.AlternateScreen);
        // Screen should be cleared
        Assert.Equal(' ', emu.Buffer.GetCell(0, 0).Character);

        Feed(emu, "Alt");

        // Exit alternate screen
        Feed(emu, "\x1b[?1049l");
        Assert.False(emu.AlternateScreen);
        // Original content restored
        Assert.Equal('H', emu.Buffer.GetCell(0, 0).Character);
    }

    [Fact]
    public void AlternateScreen_Mode47_SwitchAndRestore()
    {
        var emu = Create(10, 5);
        Feed(emu, "Hello");

        // Enter alternate screen via DECSET 47 (no cursor save, no clear)
        Feed(emu, "\x1b[?47h");
        Assert.True(emu.AlternateScreen);

        Feed(emu, "Alt");

        // Exit alternate screen
        Feed(emu, "\x1b[?47l");
        Assert.False(emu.AlternateScreen);
        // Original content restored
        Assert.Equal('H', emu.Buffer.GetCell(0, 0).Character);
    }

    [Fact]
    public void AlternateScreen_Mode1047_ClearsOnEnter()
    {
        var emu = Create(10, 5);
        Feed(emu, "Hello");

        // Enter alternate screen via DECSET 1047 (clear on enter, no cursor save)
        Feed(emu, "\x1b[?1047h");
        Assert.True(emu.AlternateScreen);
        // Screen should be cleared
        Assert.Equal(' ', emu.Buffer.GetCell(0, 0).Character);

        Feed(emu, "Alt");

        // Exit alternate screen
        Feed(emu, "\x1b[?1047l");
        Assert.False(emu.AlternateScreen);
        // Original content restored
        Assert.Equal('H', emu.Buffer.GetCell(0, 0).Character);
    }

    [Fact]
    public void EraseBelow_ClearsFromCursorPositionToEnd()
    {
        var emu = Create(10, 5);
        // Fill rows 0-4 with text
        Feed(emu, "Row0\r\nRow1\r\nRow2\r\nRow3\r\nRow4");
        // Move cursor to row 1, col 3 (after "Row")
        Feed(emu, "\x1b[2;4H");
        Assert.Equal(1, emu.Buffer.CursorRow);
        Assert.Equal(3, emu.Buffer.CursorCol);

        emu.EraseBelow();

        // Row 0 fully intact
        Assert.Equal('R', emu.Buffer.GetCell(0, 0).Character);
        // Row 1: "Row" preserved, rest of line erased
        Assert.Equal('R', emu.Buffer.GetCell(1, 0).Character);
        Assert.Equal('o', emu.Buffer.GetCell(1, 1).Character);
        Assert.Equal('w', emu.Buffer.GetCell(1, 2).Character);
        Assert.Equal(' ', emu.Buffer.GetCell(1, 3).Character); // was '1', now erased
        // Rows 2-4 should be erased
        Assert.Equal(' ', emu.Buffer.GetCell(2, 0).Character);
        Assert.Equal(' ', emu.Buffer.GetCell(3, 0).Character);
        Assert.Equal(' ', emu.Buffer.GetCell(4, 0).Character);
    }

    [Fact]
    public void Sgr_256Color_SetsForeground()
    {
        var emu = Create();
        // ESC[38;5;196m — 256-color red
        Feed(emu, "\x1b[38;5;196mX");

        var cell = emu.Buffer.GetCell(0, 0);
        // Color index 196 = color cube: (196-16) = 180, b=180%6=0, g=(180/6)%6=0, r=180/36=5
        // r=5: 55+5*40=255, g=0:0, b=0:0
        Assert.Equal(255, cell.ForegroundR);
        Assert.Equal(0, cell.ForegroundG);
        Assert.Equal(0, cell.ForegroundB);
    }

    [Fact]
    public void Sgr_TrueColor_SetsForeground()
    {
        var emu = Create();
        // ESC[38;2;100;150;200m — true color
        Feed(emu, "\x1b[38;2;100;150;200mX");

        var cell = emu.Buffer.GetCell(0, 0);
        Assert.Equal(100, cell.ForegroundR);
        Assert.Equal(150, cell.ForegroundG);
        Assert.Equal(200, cell.ForegroundB);
    }

    [Fact]
    public void Tab_AdvancesToNextTabStop()
    {
        var emu = Create();
        Feed(emu, "AB\t");
        Assert.Equal(8, emu.Buffer.CursorCol);
    }

    [Fact]
    public void AutoWrap_WrapsAtEndOfLine()
    {
        var emu = Create(5, 3);
        Feed(emu, "ABCDE"); // fills row 0
        Feed(emu, "F");     // should wrap to row 1

        Assert.Equal('F', emu.Buffer.GetCell(1, 0).Character);
        Assert.Equal(1, emu.Buffer.CursorRow);
    }

    [Fact]
    public void OscTitle_RaisesEvent()
    {
        var emu = Create();
        string? receivedTitle = null;
        emu.TitleChanged += t => receivedTitle = t;

        Feed(emu, "\x1b]0;Test Title\x07");

        Assert.Equal("Test Title", receivedTitle);
    }

    [Fact]
    public void DeleteCharacters_ShiftsLeft()
    {
        var emu = Create(10, 3);
        Feed(emu, "ABCDE");
        Feed(emu, "\x1b[1;2H");  // cursor to col 1 (0-based)
        Feed(emu, "\x1b[2P");    // delete 2 chars

        Assert.Equal('A', emu.Buffer.GetCell(0, 0).Character);
        Assert.Equal('D', emu.Buffer.GetCell(0, 1).Character);
        Assert.Equal('E', emu.Buffer.GetCell(0, 2).Character);
    }

    [Fact]
    public void SynchronizedOutput_SetAndReset()
    {
        var emu = Create();
        Assert.False(emu.SynchronizedOutput);

        Feed(emu, "\x1b[?2026h"); // enable
        Assert.True(emu.SynchronizedOutput);

        Feed(emu, "\x1b[?2026l"); // disable
        Assert.False(emu.SynchronizedOutput);
    }

    [Fact]
    public void ClaudeRedrawSuppression_CursorHomeSuppressesScrollbackOnNextScroll()
    {
        // Mirrors a Claude Code spinner-phase redraw that uses [H + per-line
        // rewrite without ED 2 or DEC 2026. The trailing newlines past viewport
        // bottom must NOT push duplicates into scrollback. The first frame after
        // activation is the initial render and is allowed through; the second
        // (steady-state spinner redraw) must suppress.
        var emu = Create(20, 4);
        emu.ClaudeRedrawSuppression = true;

        // Frame 1 — initial render — flows to scrollback.
        Feed(emu, "\x1b[H");
        Feed(emu, "L0\r\nL1\r\nL2\r\nL3\r\nL4");
        int after1 = emu.Buffer.ScrollbackCount;

        // Frame 2 — steady-state spinner redraw — must NOT add to scrollback.
        Feed(emu, "\x1b[H");
        Feed(emu, "L0\r\nL1\r\nL2\r\nL3\r\nL4");
        Assert.Equal(after1, emu.Buffer.ScrollbackCount);
    }

    [Fact]
    public void ClaudeRedrawSuppression_DisabledWithoutFlag()
    {
        // Same input as above but without ClaudeRedrawSuppression — the LFs
        // should scroll content into scrollback as normal.
        var emu = Create(20, 4);

        Feed(emu, "\x1b[H");
        Feed(emu, "L0\r\nL1\r\nL2\r\nL3\r\nL4");

        Assert.True(emu.Buffer.ScrollbackCount >= 1);
    }

    [Fact]
    public void ClaudeRedrawSuppression_FirstFrameAfterActivationFlowsToScrollback()
    {
        // Mirrors a Claude --resume: suppression flips on, then Claude does its
        // ONE-FRAME initial-history render (CUP 1;1 + many LFs). That first frame
        // must NOT suppress, so the conversation history reaches scrollback. Only
        // subsequent frames (steady-state spinner) should suppress.
        var emu = Create(20, 4);
        emu.ClaudeRedrawSuppression = true;

        // Frame 1: initial render — should leak rows into scrollback.
        Feed(emu, "\x1b[H");
        Feed(emu, "H0\r\nH1\r\nH2\r\nH3\r\nH4\r\nH5\r\nH6");
        int afterFirstFrame = emu.Buffer.ScrollbackCount;
        Assert.True(afterFirstFrame >= 3, $"initial render must populate scrollback (got {afterFirstFrame})");

        // Frame 2: steady-state spinner redraw — should suppress.
        Feed(emu, "\x1b[H");
        Feed(emu, "S0\r\nS1\r\nS2\r\nS3\r\nS4\r\nS5\r\nS6");
        Assert.Equal(afterFirstFrame, emu.Buffer.ScrollbackCount);
    }

    [Fact]
    public void ClaudeRedrawSuppression_DeferredOverflowCommitsOnExit()
    {
        // A Claude response that grows beyond the viewport must not lose its
        // top rows. Growth overflow is deferred during frames and only the NEW
        // rows (not already committed by the initial frame) are flushed on exit.
        var emu = Create(20, 4);
        emu.ClaudeRedrawSuppression = true;

        // Frame 1: initial render (5 lines in 4-row viewport → 1 overflow).
        Feed(emu, "\x1b[H");
        Feed(emu, "L0\r\nL1\r\nL2\r\nL3\r\nL4");
        int afterInitial = emu.Buffer.ScrollbackCount;
        Assert.True(afterInitial >= 1);

        // Frame 2: steady-state, same size — deferred, not in scrollback yet.
        Feed(emu, "\x1b[H");
        Feed(emu, "L0\r\nL1\r\nL2\r\nL3\r\nL4");
        Assert.Equal(afterInitial, emu.Buffer.ScrollbackCount);

        // Frame 3: response grows (7 lines → 3 overflow deferred, but 1 overlaps
        // with initial frame's committed row, so only 2 are new).
        Feed(emu, "\x1b[H");
        Feed(emu, "L0\r\nL1\r\nL2\r\nL3\r\nL4\r\nL5\r\nL6");
        Assert.Equal(afterInitial, emu.Buffer.ScrollbackCount);

        // Claude exits → only new growth rows committed (L1, L2), not L0 again.
        emu.ClaudeRedrawSuppression = false;
        int flushed = emu.Buffer.ScrollbackCount - afterInitial;
        Assert.Equal(2, flushed);
    }

    [Fact]
    public void ClaudeRedrawSuppression_DeferredOverflowReplacedEachFrame()
    {
        // Each frame clears the previous frame's deferred rows so only the
        // latest frame's overflow survives. When the content is identical to the
        // initial frame, all deferred rows are duplicates and nothing is flushed.
        var emu = Create(20, 4);
        emu.ClaudeRedrawSuppression = true;

        // Frame 1: initial render
        Feed(emu, "\x1b[H");
        Feed(emu, "A\r\nB\r\nC\r\nD\r\nE");
        int afterInitial = emu.Buffer.ScrollbackCount;

        // Frames 2-4: identical spinner redraws (1 overflow each)
        for (int i = 0; i < 3; i++)
        {
            Feed(emu, "\x1b[H");
            Feed(emu, "A\r\nB\r\nC\r\nD\r\nE");
        }
        Assert.Equal(afterInitial, emu.Buffer.ScrollbackCount);

        // Exit: deferred row is a duplicate of what initial frame committed → 0 flushed.
        emu.ClaudeRedrawSuppression = false;
        int flushed = emu.Buffer.ScrollbackCount - afterInitial;
        Assert.Equal(0, flushed);
    }

    [Fact]
    public void ClaudeRedrawSuppression_SkipResetsAfterClaudeExitAndRestart()
    {
        // After Claude exits and restarts, the next first-frame should again be
        // treated as initial render and flow into scrollback.
        var emu = Create(20, 4);
        emu.ClaudeRedrawSuppression = true;
        Feed(emu, "\x1b[H");
        Feed(emu, "A\r\nB\r\nC\r\nD\r\nE\r\nF");
        Feed(emu, "\x1b[H");
        Feed(emu, "X\r\nY\r\nZ\r\nW\r\nV\r\nU"); // suppressed (steady-state)

        emu.ClaudeRedrawSuppression = false;
        emu.ClaudeRedrawSuppression = true;     // re-enter Claude — skip flag re-armed

        int before = emu.Buffer.ScrollbackCount;
        Feed(emu, "\x1b[H");
        Feed(emu, "P\r\nQ\r\nR\r\nS\r\nT\r\nU");
        Assert.True(emu.Buffer.ScrollbackCount > before, "post-restart first frame must reach scrollback");
    }

    [Fact]
    public void Resize_LiftsSyncSuppressionForConPtyReflow()
    {
        var emu = Create(10, 6);
        for (int i = 0; i < 6; i++)
        {
            emu.Buffer.CursorRow = i;
            emu.Buffer.CursorCol = 0;
            emu.Buffer.PutChar((char)('A' + i));
        }
        emu.Buffer.CursorRow = 5;

        emu.ClaudeRedrawSuppression = true;
        Feed(emu, "\x1b[H");
        Feed(emu, "\x1b[H");
        Assert.True(emu.Buffer.SuppressScrollback, "SuppressScrollback should be active before resize");

        emu.Resize(10, 4);

        Assert.False(emu.Buffer.SuppressScrollback, "SuppressScrollback should be lifted after resize");

        // Grace covers exactly one Feed() batch — ConPTY's reflow can include
        // both ED 2 and CUP 1;1 in a single batch without re-enabling suppression.
        Feed(emu, "\x1b[2J\x1b[H");
        Assert.False(emu.Buffer.SuppressScrollback, "Reflow batch must not re-enable suppression");

        // Grace cleared after Feed() returns. Next Feed() (Claude's redraw)
        // restores suppression.
        Feed(emu, "\x1b[H");
        Assert.True(emu.Buffer.SuppressScrollback, "Claude's redraw CUP 1;1 should re-enable suppression");
    }
}
