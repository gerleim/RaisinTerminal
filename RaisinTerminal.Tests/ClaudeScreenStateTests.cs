using RaisinTerminal.ViewModels;
using Xunit;

namespace RaisinTerminal.Tests;

public class ClaudeScreenStateTests
{
    /// <summary>
    /// Helper: builds a getLineText delegate from an array of strings.
    /// Rows beyond the array return empty strings.
    /// </summary>
    private static Func<int, string> Screen(params string[] lines)
        => row => row >= 0 && row < lines.Length ? lines[row] : string.Empty;

    // ── Idle ──────────────────────────────────────────────────────────

    [Fact]
    public void IdlePrompt_Unicode_ReturnsIdle()
    {
        var screen = Screen(
            "Some output text",
            "\u276F ");  // ❯
        Assert.Equal(TerminalStatus.Idle,
            ProjectsPanelViewModel.ClassifyClaudeScreenState(screen, cursorRow: 1, screenRows: 2));
    }

    [Fact]
    public void IdlePrompt_AsciiShort_ReturnsIdle()
    {
        var screen = Screen(
            "Some output text",
            "> ");
        Assert.Equal(TerminalStatus.Idle,
            ProjectsPanelViewModel.ClassifyClaudeScreenState(screen, cursorRow: 1, screenRows: 2));
    }

    [Fact]
    public void AsciiGreaterThan_LongLine_NotTreatedAsIdle()
    {
        // ">" on a long line is quoted text, not the idle prompt
        var screen = Screen(
            "> This is a long quoted message from the user that should not match");
        Assert.Equal(TerminalStatus.Working,
            ProjectsPanelViewModel.ClassifyClaudeScreenState(screen, cursorRow: 0, screenRows: 1));
    }

    // ── Working ──────────────────────────────────────────────────────

    [Fact]
    public void SpinnerWithEllipsis_ReturnsWorking()
    {
        var screen = Screen(
            "\u273B Sketching\u2026 (1m 44s \u00B7 \u2193 269 tokens)"); // ✻ Sketching… (1m 44s · ↓ 269 tokens)
        Assert.Equal(TerminalStatus.Working,
            ProjectsPanelViewModel.ClassifyClaudeScreenState(screen, cursorRow: 0, screenRows: 1));
    }

    [Fact]
    public void SpinnerWithAsciiEllipsis_ReturnsWorking()
    {
        var screen = Screen(
            "\u2733 Thinking... (5s)");
        Assert.Equal(TerminalStatus.Working,
            ProjectsPanelViewModel.ClassifyClaudeScreenState(screen, cursorRow: 0, screenRows: 1));
    }

    [Fact]
    public void BroadDetection_UnknownGlyph_WithEllipsisAndArrow_ReturnsWorking()
    {
        // Unknown spinner glyph but matches the broader pattern: non-alnum + space + ellipsis + ↓
        var screen = Screen(
            "\u2605 Compiling\u2026 (30s \u00B7 \u2193 100 tokens)"); // ★ Compiling… (30s · ↓ 100 tokens)
        Assert.Equal(TerminalStatus.Working,
            ProjectsPanelViewModel.ClassifyClaudeScreenState(screen, cursorRow: 0, screenRows: 1));
    }

    [Fact]
    public void SpinnerWithEllipsis_OverridesIdlePrompt()
    {
        // Active spinner should keep Working even if idle prompt is also visible
        var screen = Screen(
            "\u273B Sketching\u2026 (1m 44s)",
            "\u276F ");
        Assert.Equal(TerminalStatus.Working,
            ProjectsPanelViewModel.ClassifyClaudeScreenState(screen, cursorRow: 1, screenRows: 2));
    }

    // ── Completion summary ───────────────────────────────────────────

    [Fact]
    public void CompletionSummary_WithIdlePrompt_ReturnsIdle()
    {
        // "✻ Cooked for 39s" has a duration but no ellipsis = completion summary
        var screen = Screen(
            "\u273B Cooked for 39s",
            "\u276F ");
        Assert.Equal(TerminalStatus.Idle,
            ProjectsPanelViewModel.ClassifyClaudeScreenState(screen, cursorRow: 1, screenRows: 2));
    }

    [Fact]
    public void CompletionSummary_WithoutIdlePrompt_ReturnsWorking()
    {
        // Completion summary alone without idle prompt = still Working (default fallback)
        var screen = Screen(
            "\u273B Brewed for 5m 43s");
        Assert.Equal(TerminalStatus.Working,
            ProjectsPanelViewModel.ClassifyClaudeScreenState(screen, cursorRow: 0, screenRows: 1));
    }

    // ── AgentsRunning ────────────────────────────────────────────────

    [Fact]
    public void AgentsRunning_IdlePromptWithLocalAgents_ReturnsAgentsRunning()
    {
        // Screen height = 5, so "local agent" text at row 3 is within bottom 5 rows
        var lines = new string[5];
        lines[0] = "\u273B Cooked for 39s \u00B7 9 local agents still running";
        lines[1] = "\u276F ";   // idle prompt
        lines[2] = "";
        lines[3] = "9 local agents";
        lines[4] = "";
        Assert.Equal(TerminalStatus.AgentsRunning,
            ProjectsPanelViewModel.ClassifyClaudeScreenState(Screen(lines), cursorRow: 1, screenRows: 5));
    }

    [Fact]
    public void AgentsRunning_CompletionSummaryWithAgents_NoIdlePrompt()
    {
        // Completion summary + agents but no idle prompt → AgentsRunning
        var lines = new string[5];
        lines[0] = "\u273B Cooked for 39s";
        lines[1] = "";
        lines[2] = "";
        lines[3] = "9 local agents";
        lines[4] = "";
        Assert.Equal(TerminalStatus.AgentsRunning,
            ProjectsPanelViewModel.ClassifyClaudeScreenState(Screen(lines), cursorRow: 1, screenRows: 5));
    }

    [Fact]
    public void ActiveSpinner_WithAgents_StaysWorking()
    {
        // Active spinner (ellipsis) takes priority over local agents
        var lines = new string[5];
        lines[0] = "\u273B Sketching\u2026 (1m 44s)";
        lines[1] = "";
        lines[2] = "";
        lines[3] = "9 local agents";
        lines[4] = "";
        Assert.Equal(TerminalStatus.Working,
            ProjectsPanelViewModel.ClassifyClaudeScreenState(Screen(lines), cursorRow: 0, screenRows: 5));
    }

    [Fact]
    public void NoAgents_IdlePrompt_ReturnsIdle()
    {
        // Idle prompt without local agents = plain Idle
        var lines = new string[5];
        lines[0] = "\u273B Cooked for 39s";
        lines[1] = "\u276F ";
        lines[2] = "";
        lines[3] = "";
        lines[4] = "";
        Assert.Equal(TerminalStatus.Idle,
            ProjectsPanelViewModel.ClassifyClaudeScreenState(Screen(lines), cursorRow: 1, screenRows: 5));
    }

    // ── WaitingForInput ──────────────────────────────────────────────

    [Fact]
    public void YesNo_NearCursor_ReturnsWaitingForInput()
    {
        var screen = Screen(
            "Allow this tool?",
            "  Yes    No");
        Assert.Equal(TerminalStatus.WaitingForInput,
            ProjectsPanelViewModel.ClassifyClaudeScreenState(screen, cursorRow: 1, screenRows: 2));
    }

    [Fact]
    public void EscToCancel_NearBottom_ReturnsWaitingForInput()
    {
        var lines = new string[5];
        lines[0] = "Some content";
        lines[1] = "";
        lines[2] = "";
        lines[3] = "Esc to cancel";
        lines[4] = "";
        Assert.Equal(TerminalStatus.WaitingForInput,
            ProjectsPanelViewModel.ClassifyClaudeScreenState(Screen(lines), cursorRow: 0, screenRows: 5));
    }

    [Fact]
    public void EnterToSelect_NearCursor_ReturnsWaitingForInput()
    {
        var screen = Screen(
            "Pick an option:",
            "Enter to select");
        Assert.Equal(TerminalStatus.WaitingForInput,
            ProjectsPanelViewModel.ClassifyClaudeScreenState(screen, cursorRow: 1, screenRows: 2));
    }

    [Fact]
    public void NumberedOptions_NearIdlePrompt_ReturnsWaitingForInput()
    {
        var screen = Screen(
            "1. Option A",
            "2. Option B",
            "\u276F ");
        Assert.Equal(TerminalStatus.WaitingForInput,
            ProjectsPanelViewModel.ClassifyClaudeScreenState(screen, cursorRow: 2, screenRows: 3));
    }

    [Fact]
    public void WaitingForInput_WithAgents_StaysWaiting()
    {
        // WaitingForInput takes priority over AgentsRunning
        var lines = new string[5];
        lines[0] = "Allow this tool?";
        lines[1] = "  Yes    No";
        lines[2] = "\u276F ";
        lines[3] = "9 local agents";
        lines[4] = "";
        Assert.Equal(TerminalStatus.WaitingForInput,
            ProjectsPanelViewModel.ClassifyClaudeScreenState(Screen(lines), cursorRow: 1, screenRows: 5));
    }

    [Fact]
    public void CtrlGToEdit_NearBottom_ReturnsWaitingForInput()
    {
        var lines = new string[5];
        lines[0] = "Some content";
        lines[1] = "";
        lines[2] = "";
        lines[3] = "ctrl-g to edit";
        lines[4] = "";
        Assert.Equal(TerminalStatus.WaitingForInput,
            ProjectsPanelViewModel.ClassifyClaudeScreenState(Screen(lines), cursorRow: 0, screenRows: 5));
    }

    // ── Edge cases ───────────────────────────────────────────────────

    [Fact]
    public void InvalidCursorRow_ReturnsWorking()
    {
        var screen = Screen("some text");
        Assert.Equal(TerminalStatus.Working,
            ProjectsPanelViewModel.ClassifyClaudeScreenState(screen, cursorRow: -1, screenRows: 1));
    }

    [Fact]
    public void ZeroScreenRows_ReturnsWorking()
    {
        var screen = Screen();
        Assert.Equal(TerminalStatus.Working,
            ProjectsPanelViewModel.ClassifyClaudeScreenState(screen, cursorRow: 0, screenRows: 0));
    }

    [Fact]
    public void EmptyScreen_ReturnsWorking()
    {
        var screen = Screen("", "", "");
        Assert.Equal(TerminalStatus.Working,
            ProjectsPanelViewModel.ClassifyClaudeScreenState(screen, cursorRow: 0, screenRows: 3));
    }

    [Fact]
    public void NumberedOptions_FarFromPrompt_NotTreatedAsWaiting()
    {
        // Options more than 10 rows above prompt should not trigger WaitingForInput
        var lines = new string[15];
        lines[0] = "1. Option A";
        lines[1] = "2. Option B";
        for (int i = 2; i < 14; i++) lines[i] = "";
        lines[14] = "\u276F ";
        Assert.Equal(TerminalStatus.Idle,
            ProjectsPanelViewModel.ClassifyClaudeScreenState(Screen(lines), cursorRow: 14, screenRows: 15));
    }

    [Fact]
    public void CompletionSummary_SuppressesOldNumberedOptions()
    {
        // Numbered options ABOVE a completion summary are from old output,
        // should not trigger WaitingForInput
        var lines = new string[5];
        lines[0] = "1. Old option A";
        lines[1] = "2. Old option B";
        lines[2] = "\u273B Brewed for 5m 43s";
        lines[3] = "\u276F ";
        lines[4] = "";
        Assert.Equal(TerminalStatus.Idle,
            ProjectsPanelViewModel.ClassifyClaudeScreenState(Screen(lines), cursorRow: 3, screenRows: 5));
    }

    [Fact]
    public void LocalAgents_NotInBottom5Rows_Ignored()
    {
        // "local agent" text far from the bottom should not be detected
        var lines = new string[20];
        lines[0] = "9 local agents";  // row 0, far from bottom (row 15+)
        for (int i = 1; i < 19; i++) lines[i] = "";
        lines[19] = "\u276F ";
        Assert.Equal(TerminalStatus.Idle,
            ProjectsPanelViewModel.ClassifyClaudeScreenState(Screen(lines), cursorRow: 19, screenRows: 20));
    }
}
