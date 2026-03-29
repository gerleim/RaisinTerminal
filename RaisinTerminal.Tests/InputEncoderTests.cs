using System.Text;
using RaisinTerminal.Core.Terminal;
using Xunit;

namespace RaisinTerminal.Tests;

public class InputEncoderTests
{
    [Theory]
    [InlineData(ConsoleKey.UpArrow, "\x1b[A")]
    [InlineData(ConsoleKey.DownArrow, "\x1b[B")]
    [InlineData(ConsoleKey.RightArrow, "\x1b[C")]
    [InlineData(ConsoleKey.LeftArrow, "\x1b[D")]
    [InlineData(ConsoleKey.Home, "\x1b[H")]
    [InlineData(ConsoleKey.End, "\x1b[F")]
    public void EncodeKey_ArrowAndNavKeys_ProducesCorrectSequence(ConsoleKey key, string expected)
    {
        var result = InputEncoder.EncodeKey(key);
        Assert.Equal(expected, Encoding.UTF8.GetString(result));
    }

    [Fact]
    public void EncodeKey_CtrlArrow_IncludesModifier()
    {
        // Ctrl modifier = 5, so CSI 1;5 A
        var result = InputEncoder.EncodeKey(ConsoleKey.UpArrow, ctrl: true);
        Assert.Equal("\x1b[1;5A", Encoding.UTF8.GetString(result));
    }

    [Fact]
    public void EncodeKey_ShiftArrow_IncludesModifier()
    {
        // Shift modifier = 2, so CSI 1;2 A
        var result = InputEncoder.EncodeKey(ConsoleKey.UpArrow, shift: true);
        Assert.Equal("\x1b[1;2A", Encoding.UTF8.GetString(result));
    }

    [Theory]
    [InlineData(ConsoleKey.Delete, "\x1b[3~")]
    [InlineData(ConsoleKey.Insert, "\x1b[2~")]
    [InlineData(ConsoleKey.PageUp, "\x1b[5~")]
    [InlineData(ConsoleKey.PageDown, "\x1b[6~")]
    public void EncodeKey_TildeKeys_ProducesCorrectSequence(ConsoleKey key, string expected)
    {
        var result = InputEncoder.EncodeKey(key);
        Assert.Equal(expected, Encoding.UTF8.GetString(result));
    }

    [Theory]
    [InlineData(ConsoleKey.F1, "\x1bOP")]
    [InlineData(ConsoleKey.F2, "\x1bOQ")]
    [InlineData(ConsoleKey.F3, "\x1bOR")]
    [InlineData(ConsoleKey.F4, "\x1bOS")]
    public void EncodeKey_F1ToF4_UsesSS3(ConsoleKey key, string expected)
    {
        var result = InputEncoder.EncodeKey(key);
        Assert.Equal(expected, Encoding.UTF8.GetString(result));
    }

    [Theory]
    [InlineData(ConsoleKey.F5, "\x1b[15~")]
    [InlineData(ConsoleKey.F12, "\x1b[24~")]
    public void EncodeKey_F5Plus_UsesTilde(ConsoleKey key, string expected)
    {
        var result = InputEncoder.EncodeKey(key);
        Assert.Equal(expected, Encoding.UTF8.GetString(result));
    }

    [Fact]
    public void EncodeKey_Enter_ReturnsCR()
    {
        var result = InputEncoder.EncodeKey(ConsoleKey.Enter);
        Assert.Equal("\r", Encoding.UTF8.GetString(result));
    }

    [Fact]
    public void EncodeKey_Backspace_ReturnsDEL()
    {
        var result = InputEncoder.EncodeKey(ConsoleKey.Backspace);
        Assert.Equal("\x7f", Encoding.UTF8.GetString(result));
    }

    [Fact]
    public void EncodeKey_Tab_ReturnsHT()
    {
        var result = InputEncoder.EncodeKey(ConsoleKey.Tab);
        Assert.Equal("\t", Encoding.UTF8.GetString(result));
    }

    [Fact]
    public void EncodeKey_ShiftTab_ReturnsReverseTab()
    {
        var result = InputEncoder.EncodeKey(ConsoleKey.Tab, shift: true);
        Assert.Equal("\x1b[Z", Encoding.UTF8.GetString(result));
    }

    [Fact]
    public void EncodeKey_CtrlC_ReturnsETX()
    {
        var result = InputEncoder.EncodeKey(ConsoleKey.C, ctrl: true);
        Assert.Equal(new byte[] { 3 }, result);
    }

    [Fact]
    public void EncodeText_Ascii_ReturnsUtf8Bytes()
    {
        var result = InputEncoder.EncodeText("hello");
        Assert.Equal(Encoding.UTF8.GetBytes("hello"), result);
    }

    [Fact]
    public void EncodeText_Unicode_ReturnsUtf8Bytes()
    {
        var result = InputEncoder.EncodeText("\u00e9"); // é
        Assert.Equal(Encoding.UTF8.GetBytes("\u00e9"), result);
    }
}
