using RaisinTerminal.Core.Terminal;
using Xunit;

namespace RaisinTerminal.Tests;

public class TerminalBufferTests
{
    [Fact]
    public void PutChar_WritesToCurrentPosition()
    {
        var buffer = new TerminalBuffer(80, 24);
        buffer.PutChar('A');

        var cell = buffer.GetCell(0, 0);
        Assert.Equal('A', cell.Character);
        Assert.Equal(1, buffer.CursorCol);
    }

    [Fact]
    public void LineFeed_MovesCursorDown()
    {
        var buffer = new TerminalBuffer(80, 24);
        buffer.LineFeed();
        Assert.Equal(1, buffer.CursorRow);
    }

    [Fact]
    public void Clear_ResetsBuffer()
    {
        var buffer = new TerminalBuffer(80, 24);
        buffer.PutChar('X');
        buffer.Clear();

        Assert.Equal(0, buffer.CursorRow);
        Assert.Equal(0, buffer.CursorCol);
        Assert.Equal(' ', buffer.GetCell(0, 0).Character);
    }
}
