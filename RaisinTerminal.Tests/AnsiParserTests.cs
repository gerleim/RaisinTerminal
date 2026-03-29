using System.Text;
using RaisinTerminal.Core.Terminal;
using Xunit;

namespace RaisinTerminal.Tests;

public class AnsiParserTests
{
    [Fact]
    public void Feed_PlainAscii_FiresPrintForEachChar()
    {
        var parser = new AnsiParser();
        var printed = new List<char>();
        parser.Print += c => printed.Add(c);

        parser.Feed(Encoding.UTF8.GetBytes("Hello"));

        Assert.Equal(new[] { 'H', 'e', 'l', 'l', 'o' }, printed.ToArray());
    }

    [Fact]
    public void Feed_ControlChars_FiresExecute()
    {
        var parser = new AnsiParser();
        var executed = new List<byte>();
        parser.Execute += b => executed.Add(b);

        parser.Feed([(byte)'\r', (byte)'\n', 0x08]);

        Assert.Equal(new byte[] { 0x0D, 0x0A, 0x08 }, executed.ToArray());
    }

    [Fact]
    public void Feed_CsiCursorPosition_DispatchesCorrectParams()
    {
        var parser = new AnsiParser();
        char? finalChar = null;
        int[]? pars = null;
        parser.CsiDispatch += (f, p, _, _) => { finalChar = f; pars = p; };

        // ESC[5;10H — cursor to row 5, col 10
        parser.Feed(Encoding.UTF8.GetBytes("\x1b[5;10H"));

        Assert.Equal('H', finalChar);
        Assert.Equal(new[] { 5, 10 }, pars);
    }

    [Fact]
    public void Feed_CsiSgr_DispatchesColorParam()
    {
        var parser = new AnsiParser();
        char? finalChar = null;
        int[]? pars = null;
        parser.CsiDispatch += (f, p, _, _) => { finalChar = f; pars = p; };

        parser.Feed(Encoding.UTF8.GetBytes("\x1b[31m"));

        Assert.Equal('m', finalChar);
        Assert.Equal(new[] { 31 }, pars);
    }

    [Fact]
    public void Feed_CsiMultipleSgrParams()
    {
        var parser = new AnsiParser();
        int[]? pars = null;
        parser.CsiDispatch += (_, p, _, _) => pars = p;

        parser.Feed(Encoding.UTF8.GetBytes("\x1b[1;31;42m"));

        Assert.Equal(new[] { 1, 31, 42 }, pars);
    }

    [Fact]
    public void Feed_CsiPrivateMode_PassesPrivateMarker()
    {
        var parser = new AnsiParser();
        byte? marker = null;
        int[]? pars = null;
        parser.CsiDispatch += (_, p, _, m) => { marker = m; pars = p; };

        // ESC[?1049h — alternate screen
        parser.Feed(Encoding.UTF8.GetBytes("\x1b[?1049h"));

        Assert.Equal((byte)'?', marker);
        Assert.Equal(new[] { 1049 }, pars);
    }

    [Fact]
    public void Feed_OscTitle_DispatchesTitleString()
    {
        var parser = new AnsiParser();
        string? oscData = null;
        parser.OscDispatch += s => oscData = s;

        // OSC 0;My Title BEL
        parser.Feed(Encoding.UTF8.GetBytes("\x1b]0;My Title\x07"));

        Assert.Equal("0;My Title", oscData);
    }

    [Fact]
    public void Feed_Utf8MultiByte_PrintsCorrectChar()
    {
        var parser = new AnsiParser();
        var printed = new List<char>();
        parser.Print += c => printed.Add(c);

        // U+2580 (▀) = 0xE2 0x96 0x80 in UTF-8
        parser.Feed([0xE2, 0x96, 0x80]);

        Assert.Contains('\u2580', printed);
    }

    [Fact]
    public void Feed_Utf8Emoji_PrintsSurrogatePair()
    {
        var parser = new AnsiParser();
        var printed = new List<char>();
        parser.Print += c => printed.Add(c);

        // U+1F600 (😀) = 0xF0 0x9F 0x98 0x80 in UTF-8
        parser.Feed([0xF0, 0x9F, 0x98, 0x80]);

        // C# represents this as a surrogate pair
        Assert.Equal(2, printed.Count);
        Assert.True(char.IsHighSurrogate(printed[0]));
        Assert.True(char.IsLowSurrogate(printed[1]));
    }

    [Fact]
    public void Feed_EscDispatch_SingleCharSequence()
    {
        var parser = new AnsiParser();
        char? dispatched = null;
        parser.EscDispatch += c => dispatched = c;

        // ESC M — Reverse Index (RI)
        // Using 'M' (0x4D) which is unambiguously in the 0x40-0x7E range
        parser.Feed(new byte[] { 0x1B, 0x4D });

        Assert.Equal('M', dispatched);
    }

    [Fact]
    public void Feed_CsiNoParams_DefaultsToZero()
    {
        var parser = new AnsiParser();
        int[]? pars = null;
        parser.CsiDispatch += (_, p, _, _) => pars = p;

        // ESC[H — cursor home (no params)
        parser.Feed(Encoding.UTF8.GetBytes("\x1b[H"));

        Assert.Equal(new[] { 0 }, pars);
    }

    [Fact]
    public void Feed_InterleavedTextAndCsi_ParsesCorrectly()
    {
        var parser = new AnsiParser();
        var printed = new List<char>();
        var csiCount = 0;
        parser.Print += c => printed.Add(c);
        parser.CsiDispatch += (_, _, _, _) => csiCount++;

        parser.Feed(Encoding.UTF8.GetBytes("AB\x1b[1mCD"));

        Assert.Equal(new[] { 'A', 'B', 'C', 'D' }, printed.ToArray());
        Assert.Equal(1, csiCount);
    }
}
