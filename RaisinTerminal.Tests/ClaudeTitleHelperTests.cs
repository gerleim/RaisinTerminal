using RaisinTerminal.Core.Helpers;
using Xunit;

namespace RaisinTerminal.Tests;

public class ClaudeTitleHelperTests
{
    [Theory]
    [InlineData("Claude Code", true)]
    [InlineData("✳ 2026 1", true)]
    [InlineData("⏵ RT 2", true)]
    [InlineData("· RT 2", true)]
    [InlineData("🤖 RT 2", true)]   // surrogate-pair glyph
    [InlineData("claude", false)]
    [InlineData("claude · resume", false)]
    [InlineData("C:\\WINDOWS\\SYSTEM32\\cmd.exe", false)]
    [InlineData("RT 3", false)]      // bare name with no glyph prefix is not a TUI title
    [InlineData("", false)]
    [InlineData("X ", false)]        // trailing space, no name
    public void IsTuiTitle_ClassifiesClaudeMainTuiTitles(string title, bool expected)
    {
        Assert.Equal(expected, ClaudeTitleHelper.IsTuiTitle(title));
    }
}
