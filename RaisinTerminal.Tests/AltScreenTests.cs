using RaisinTerminal.Core.Terminal;
using Xunit;

namespace RaisinTerminal.Tests;

public class AltScreenTests
{
    [Fact]
    public void AltScreen1049_CursorRestoredAfterExit()
    {
        var t = new TerminalTestHarness(40, 10);

        t.Feed("prompt>claude --name \"RT 2\"");
        int col0 = t.Buffer.CursorCol;
        Assert.True(col0 > 0);

        t.NewLine();
        t.Feed("Starting Claude Code...");
        t.NewLine();
        int savedRow = t.Buffer.CursorRow;
        int savedCol = t.Buffer.CursorCol;

        t.AltScreenEnter();
        Assert.True(t.Emulator.AlternateScreen);
        t.AssertCursor(0, 0);

        t.Feed("TUI content line 1");
        t.NewLine();
        t.Feed("TUI content line 2");
        t.CursorTo(5, 10);

        t.AltScreenExit();
        Assert.False(t.Emulator.AlternateScreen);
        Assert.Equal(savedRow, t.Buffer.CursorRow);
        Assert.Equal(savedCol, t.Buffer.CursorCol);
    }

    [Fact]
    public void AltScreen1049_MainScreenContentRestoredAfterExit()
    {
        var t = new TerminalTestHarness(40, 6);

        t.Feed("prompt>claude --name \"RT 2\"");
        t.NewLine();
        t.Feed("Claude Code v2.1.119");
        t.NewLine();
        t.Feed("Opus 4.6");

        var snapshot = t.TakeSnapshot();

        t.AltScreenEnter();
        t.Feed("TUI STUFF");
        t.AltScreenExit();

        t.AssertScreenRow(0, snapshot.ScreenRows[0]);
        t.AssertScreenRow(1, snapshot.ScreenRows[1]);
        t.AssertScreenRow(2, snapshot.ScreenRows[2]);
    }

    [Fact]
    public void AltScreen1049_PostExitOutput_CursorTracksCorrectly()
    {
        var t = new TerminalTestHarness(50, 10);

        t.Feed("D:\\Project>claude --name \"RT 2\"");
        int cmdLen = t.Buffer.CursorCol;
        t.NewLine();
        t.Feed("Claude Code v2.1.119");
        t.NewLine();

        t.AltScreenEnter();
        t.Feed("TUI running...");
        t.CursorTo(8, 1);

        t.AltScreenExit();

        t.Feed("Resume this session with:");
        t.NewLine();
        t.Feed("claude --resume \"RT 2\"");
        t.NewLine();

        t.Feed("D:\\Project>");
        int promptRow = t.Buffer.CursorRow;
        int promptCol = t.Buffer.CursorCol;

        t.Feed("test input");
        t.AssertCursor(promptRow, promptCol + 10);

        t.CarriageReturn();
        t.AssertCursor(promptRow, 0);
        t.Feed("D:\\Project>claude --name \"RT 2\"");
        t.AssertCursor(promptRow, cmdLen);

        t.AssertScreenRow(promptRow, "D:\\Project>claude --name \"RT 2\"");
    }

    [Fact]
    public void AltScreen1049_ScrollRegionResetOnExit()
    {
        var t = new TerminalTestHarness(40, 10);

        t.Feed("Line before alt screen");
        t.NewLine();

        t.AltScreenEnter();

        t.SetScrollRegion(1, 9);
        Assert.Equal(0, t.Buffer.ScrollTop);
        Assert.Equal(8, t.Buffer.ScrollBottom);

        t.AltScreenExit();

        // xterm resets scroll region on alt screen exit
        Assert.Equal(0, t.Buffer.ScrollTop);
        Assert.Equal(9, t.Buffer.ScrollBottom);
    }

    [Fact]
    public void AltScreen1049_UpArrowAfterExit_CorrectRow()
    {
        // Reproduces the bug: after exiting Claude Code TUI, the shell's
        // up-arrow command recall renders at the wrong position because
        // the TUI's scroll region persists.

        var t = new TerminalTestHarness(40, 24);

        // Shell prompt + Claude startup header
        t.Feed("D:\\Project>claude --name \"RT 2\"");
        int cmdLen = t.Buffer.CursorCol;
        t.NewLine();
        t.FeedLines(
            "     Claude Code v2.1.119",
            "     Opus 4.6",
            "     D:\\Project");
        t.NewLine();

        // TUI session with scroll region (status bar on last row)
        t.AltScreenEnter();
        t.SetScrollRegion(1, 23);
        for (int i = 0; i < 20; i++)
        {
            t.CursorTo(i + 1, 1);
            t.Feed($"TUI line {i}");
        }

        t.AltScreenExit();

        // Exit messages
        t.Feed("Resume this session with:");
        t.NewLine();
        t.Feed("claude --resume \"RT 2\"");
        t.NewLine();

        // Shell prompt + user input
        t.Feed("D:\\Project>");
        int promptRow = t.Buffer.CursorRow;
        t.Feed("asda sdasd as d");

        // Up-arrow: CR + erase line + prompt + recalled command
        t.CarriageReturn();
        t.EraseLine(2);
        t.Feed("D:\\Project>claude --name \"RT 2\"");

        t.AssertCursor(promptRow, cmdLen);
        t.AssertScreenRow(promptRow, "D:\\Project>claude --name \"RT 2\"");
    }

    [Fact]
    public void AltScreen1049_MultipleCycles_CursorStable()
    {
        var t = new TerminalTestHarness(40, 10);

        t.Feed("prompt>");
        t.NewLine();

        for (int cycle = 0; cycle < 3; cycle++)
        {
            int rowBefore = t.Buffer.CursorRow;
            int colBefore = t.Buffer.CursorCol;

            t.AltScreenEnter();
            t.Feed($"Alt cycle {cycle}");
            t.CursorTo(5, 5);
            t.AltScreenExit();

            Assert.Equal(rowBefore, t.Buffer.CursorRow);
            Assert.Equal(colBefore, t.Buffer.CursorCol);
        }
    }

    [Fact]
    public void AltScreen47_ScrollRegionResetOnExit()
    {
        var t = new TerminalTestHarness(40, 10);

        // Enter alt screen via mode 47
        t.Feed("\x1b[?47h");
        t.SetScrollRegion(2, 8);
        Assert.Equal(1, t.Buffer.ScrollTop);
        Assert.Equal(7, t.Buffer.ScrollBottom);

        // Exit alt screen via mode 47
        t.Feed("\x1b[?47l");

        Assert.Equal(0, t.Buffer.ScrollTop);
        Assert.Equal(9, t.Buffer.ScrollBottom);
    }
}
