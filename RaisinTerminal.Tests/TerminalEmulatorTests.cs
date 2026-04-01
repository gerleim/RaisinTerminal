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
}
