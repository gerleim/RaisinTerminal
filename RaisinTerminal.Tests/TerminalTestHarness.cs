using System.Text;
using RaisinTerminal.Core.Terminal;
using Xunit;

namespace RaisinTerminal.Tests;

public record TerminalSnapshot(
    string[] ScreenRows,
    string[] ScrollbackLines,
    int CursorRow,
    int CursorCol);

public class TerminalTestHarness
{
    public TerminalEmulator Emulator { get; }
    public TerminalBuffer Buffer => Emulator.Buffer;

    public TerminalTestHarness(int cols, int rows)
    {
        Emulator = new TerminalEmulator(cols, rows);
    }

    // --- Feeding ---

    public TerminalTestHarness Feed(string text)
    {
        Emulator.Feed(Encoding.UTF8.GetBytes(text));
        return this;
    }

    public TerminalTestHarness FeedRaw(byte[] data)
    {
        Emulator.Feed(data);
        return this;
    }

    public TerminalTestHarness FeedLines(params string[] lines)
    {
        Feed(string.Join("\r\n", lines));
        return this;
    }

    // --- ANSI sequence helpers ---

    public TerminalTestHarness CursorHome() => Feed("\x1b[H");
    public TerminalTestHarness CursorTo(int row, int col) => Feed($"\x1b[{row};{col}H");
    public TerminalTestHarness EraseDisplay(int mode = 2) => Feed($"\x1b[{mode}J");
    public TerminalTestHarness EraseLine(int mode = 0) => Feed($"\x1b[{mode}K");
    public TerminalTestHarness SetScrollRegion(int top, int bottom) => Feed($"\x1b[{top};{bottom}r");
    public TerminalTestHarness ResetScrollRegion() => Feed("\x1b[r");
    public TerminalTestHarness LineFeed() => Feed("\n");
    public TerminalTestHarness CarriageReturn() => Feed("\r");
    public TerminalTestHarness NewLine() => Feed("\r\n");
    public TerminalTestHarness ScrollUp(int n = 1) => Feed($"\x1b[{n}S");
    public TerminalTestHarness ScrollDown(int n = 1) => Feed($"\x1b[{n}T");
    public TerminalTestHarness InsertLines(int n = 1) => Feed($"\x1b[{n}L");
    public TerminalTestHarness DeleteLines(int n = 1) => Feed($"\x1b[{n}M");
    public TerminalTestHarness SyncBegin() => Feed("\x1b[?2026h");
    public TerminalTestHarness SyncEnd() => Feed("\x1b[?2026l");
    public TerminalTestHarness AltScreenEnter() => Feed("\x1b[?1049h");
    public TerminalTestHarness AltScreenExit() => Feed("\x1b[?1049l");

    // --- State manipulation ---

    public TerminalTestHarness Resize(int cols, int rows)
    {
        Emulator.Resize(cols, rows);
        return this;
    }

    public TerminalTestHarness SetClaudeRedrawSuppression(bool on)
    {
        Emulator.ClaudeRedrawSuppression = on;
        return this;
    }

    // --- Accessors ---

    public string GetScreenRow(int row) => Buffer.GetScreenLineText(row).TrimEnd();

    public string[] GetAllScreenRows()
    {
        var rows = new string[Buffer.Rows];
        for (int r = 0; r < Buffer.Rows; r++)
            rows[r] = GetScreenRow(r);
        return rows;
    }

    public string GetScrollbackLine(int index) => Buffer.GetScrollbackLineText(index).TrimEnd();

    public string[] GetAllScrollbackLines()
    {
        var lines = new string[Buffer.ScrollbackCount];
        for (int i = 0; i < Buffer.ScrollbackCount; i++)
            lines[i] = GetScrollbackLine(i);
        return lines;
    }

    // --- Snapshot ---

    public TerminalSnapshot TakeSnapshot() => new(
        GetAllScreenRows(),
        GetAllScrollbackLines(),
        Buffer.CursorRow,
        Buffer.CursorCol);

    public TerminalTestHarness AssertMatchesSnapshot(TerminalSnapshot snap)
    {
        Assert.Equal(snap.ScreenRows, GetAllScreenRows());
        Assert.Equal(snap.ScrollbackLines, GetAllScrollbackLines());
        Assert.Equal(snap.CursorRow, Buffer.CursorRow);
        Assert.Equal(snap.CursorCol, Buffer.CursorCol);
        return this;
    }

    // --- Assertions ---

    public TerminalTestHarness AssertScreenRow(int row, string expected)
    {
        var actual = GetScreenRow(row);
        Assert.Equal(expected, actual);
        return this;
    }

    public TerminalTestHarness AssertScreenRows(params string[] expected)
    {
        for (int i = 0; i < expected.Length; i++)
            AssertScreenRow(i, expected[i]);
        for (int i = expected.Length; i < Buffer.Rows; i++)
            AssertScreenRow(i, "");
        return this;
    }

    public TerminalTestHarness AssertScrollbackCount(int expected)
    {
        Assert.Equal(expected, Buffer.ScrollbackCount);
        return this;
    }

    public TerminalTestHarness AssertScrollbackLine(int index, string expected)
    {
        var actual = GetScrollbackLine(index);
        Assert.Equal(expected, actual);
        return this;
    }

    public TerminalTestHarness AssertAllScrollback(params string[] expected)
    {
        AssertScrollbackCount(expected.Length);
        for (int i = 0; i < expected.Length; i++)
            AssertScrollbackLine(i, expected[i]);
        return this;
    }

    public TerminalTestHarness AssertCursor(int row, int col)
    {
        Assert.Equal(row, Buffer.CursorRow);
        Assert.Equal(col, Buffer.CursorCol);
        return this;
    }

    public TerminalTestHarness AssertNoDuplicateScrollback()
    {
        for (int i = 1; i < Buffer.ScrollbackCount; i++)
        {
            var prev = GetScrollbackLine(i - 1);
            var curr = GetScrollbackLine(i);
            if (prev == curr && prev != "")
                Assert.Fail($"Duplicate non-empty scrollback lines at index {i - 1} and {i}: \"{curr}\"");
        }
        return this;
    }

    public TerminalTestHarness AssertTotalContent(params string[] expected)
    {
        var all = GetAllScrollbackLines().Concat(GetAllScreenRows()).ToArray();
        // Trim trailing empty lines from both for comparison
        var allTrimmed = TrimTrailingEmpty(all);
        var expectedTrimmed = TrimTrailingEmpty(expected);
        Assert.Equal(expectedTrimmed, allTrimmed);
        return this;
    }

    // --- File replay ---

    public TerminalTestHarness FeedFile(string path)
    {
        var bytes = File.ReadAllBytes(path);
        Emulator.Feed(bytes);
        return this;
    }

    public TerminalTestHarness FeedFileWithResizes(string path,
        params (long offset, int cols, int rows)[] resizes)
    {
        var bytes = File.ReadAllBytes(path);
        var sorted = resizes.OrderBy(r => r.offset).ToArray();
        long pos = 0;
        foreach (var (offset, cols, rows) in sorted)
        {
            if (offset > pos)
                Emulator.Feed(bytes.AsSpan((int)pos, (int)(offset - pos)));
            Emulator.Resize(cols, rows);
            pos = offset;
        }
        if (pos < bytes.Length)
            Emulator.Feed(bytes.AsSpan((int)pos));
        return this;
    }

    // --- Split-pane / viewport support ---

    public TerminalViewport AddViewport(bool isLive = false, int scrollOffset = 0)
    {
        var vp = new TerminalViewport { IsLive = isLive, ScrollOffset = scrollOffset };
        Buffer.Viewports.Add(vp);
        return vp;
    }

    public TerminalTestHarness RemoveViewport(TerminalViewport vp)
    {
        Buffer.Viewports.Remove(vp);
        return this;
    }

    public string GetVisibleRow(int viewRow, int scrollOffset, int viewRows)
    {
        var sb = new StringBuilder();
        for (int c = 0; c < Buffer.Columns; c++)
        {
            char ch = Buffer.GetVisibleCell(viewRow, c, scrollOffset, viewRows).Character;
            sb.Append(ch == '\0' ? ' ' : ch);
        }
        return sb.ToString().TrimEnd();
    }

    public string[] GetVisibleRows(int scrollOffset, int viewRows)
    {
        var rows = new string[viewRows];
        for (int r = 0; r < viewRows; r++)
            rows[r] = GetVisibleRow(r, scrollOffset, viewRows);
        return rows;
    }

    public TerminalTestHarness AssertVisibleRow(int viewRow, int scrollOffset, int viewRows, string expected)
    {
        var actual = GetVisibleRow(viewRow, scrollOffset, viewRows);
        Assert.Equal(expected, actual);
        return this;
    }

    public TerminalTestHarness AssertVisibleRows(int scrollOffset, int viewRows, params string[] expected)
    {
        for (int i = 0; i < expected.Length; i++)
            AssertVisibleRow(i, scrollOffset, viewRows, expected[i]);
        for (int i = expected.Length; i < viewRows; i++)
            AssertVisibleRow(i, scrollOffset, viewRows, "");
        return this;
    }

    // --- Helpers ---

    private static string[] TrimTrailingEmpty(string[] lines)
    {
        int last = lines.Length - 1;
        while (last >= 0 && lines[last] == "")
            last--;
        return lines.Take(last + 1).ToArray();
    }
}
