using RaisinTerminal.Core.Terminal;
using Xunit;

namespace RaisinTerminal.Tests;

public class ClaudeScreenStateTests
{
    /// <summary>
    /// Helper: builds a getLineText delegate from an array of strings.
    /// Rows beyond the array (or null entries) return empty strings.
    /// </summary>
    private static Func<int, string> Screen(params string[] lines)
        => row => row >= 0 && row < lines.Length ? lines[row] ?? string.Empty : string.Empty;

    // ── Idle ──────────────────────────────────────────────────────────

    [Fact]
    public void IdlePrompt_Unicode_ReturnsIdle()
    {
        var screen = Screen(
            "Some output text",
            "\u276F ");  // ❯
        Assert.Equal(TerminalStatus.Idle,
            ClaudeScreenStateClassifier.Classify(screen, cursorRow: 1, screenRows: 2));
    }

    [Fact]
    public void IdlePrompt_AsciiShort_ReturnsIdle()
    {
        var screen = Screen(
            "Some output text",
            "> ");
        Assert.Equal(TerminalStatus.Idle,
            ClaudeScreenStateClassifier.Classify(screen, cursorRow: 1, screenRows: 2));
    }

    [Fact]
    public void IdlePrompt_AsciiAfterPredictiveTextStripped_ReturnsIdle()
    {
        // After ReadScreenLineBrightOnly strips dim predictive text,
        // the prompt line becomes just "> " which is short enough to match
        var screen = Screen(
            "Some output text",
            "> ");
        Assert.Equal(TerminalStatus.Idle,
            ClaudeScreenStateClassifier.Classify(screen, cursorRow: 1, screenRows: 2));
    }

    [Fact]
    public void AsciiGreaterThan_LongLine_NotTreatedAsIdle()
    {
        // ">" on a long line is quoted text, not the idle prompt
        var screen = Screen(
            "> This is a long quoted message from the user that should not match");
        Assert.Equal(TerminalStatus.Working,
            ClaudeScreenStateClassifier.Classify(screen, cursorRow: 0, screenRows: 1));
    }

    // ── Working ──────────────────────────────────────────────────────

    [Fact]
    public void SpinnerWithEllipsis_ReturnsWorking()
    {
        var screen = Screen(
            "\u273B Sketching\u2026 (1m 44s \u00B7 \u2193 269 tokens)"); // ✻ Sketching… (1m 44s · ↓ 269 tokens)
        Assert.Equal(TerminalStatus.Working,
            ClaudeScreenStateClassifier.Classify(screen, cursorRow: 0, screenRows: 1));
    }

    [Fact]
    public void SpinnerWithAsciiEllipsis_ReturnsWorking()
    {
        var screen = Screen(
            "\u2733 Thinking... (5s)");
        Assert.Equal(TerminalStatus.Working,
            ClaudeScreenStateClassifier.Classify(screen, cursorRow: 0, screenRows: 1));
    }

    [Fact]
    public void BroadDetection_UnknownGlyph_WithEllipsisAndArrow_ReturnsWorking()
    {
        // Unknown spinner glyph but matches the broader pattern: non-alnum + space + ellipsis + ↓
        var screen = Screen(
            "\u2605 Compiling\u2026 (30s \u00B7 \u2193 100 tokens)"); // ★ Compiling… (30s · ↓ 100 tokens)
        Assert.Equal(TerminalStatus.Working,
            ClaudeScreenStateClassifier.Classify(screen, cursorRow: 0, screenRows: 1));
    }

    [Fact]
    public void SpinnerWithEllipsis_OverridesIdlePrompt()
    {
        // Active spinner should keep Working even if idle prompt is also visible
        var screen = Screen(
            "\u273B Sketching\u2026 (1m 44s)",
            "\u276F ");
        Assert.Equal(TerminalStatus.Working,
            ClaudeScreenStateClassifier.Classify(screen, cursorRow: 1, screenRows: 2));
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
            ClaudeScreenStateClassifier.Classify(screen, cursorRow: 1, screenRows: 2));
    }

    [Fact]
    public void CompletionSummary_WithoutIdlePrompt_ReturnsWorking()
    {
        // Completion summary alone without idle prompt = still Working (default fallback)
        var screen = Screen(
            "\u273B Brewed for 5m 43s");
        Assert.Equal(TerminalStatus.Working,
            ClaudeScreenStateClassifier.Classify(screen, cursorRow: 0, screenRows: 1));
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
            ClaudeScreenStateClassifier.Classify(Screen(lines), cursorRow: 1, screenRows: 5));
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
            ClaudeScreenStateClassifier.Classify(Screen(lines), cursorRow: 1, screenRows: 5));
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
            ClaudeScreenStateClassifier.Classify(Screen(lines), cursorRow: 0, screenRows: 5));
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
            ClaudeScreenStateClassifier.Classify(Screen(lines), cursorRow: 1, screenRows: 5));
    }

    // ── WaitingForInput ──────────────────────────────────────────────

    [Fact]
    public void YesNo_NearCursor_ReturnsWaitingForInput()
    {
        var screen = Screen(
            "Allow this tool?",
            "  Yes    No");
        Assert.Equal(TerminalStatus.WaitingForInput,
            ClaudeScreenStateClassifier.Classify(screen, cursorRow: 1, screenRows: 2));
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
            ClaudeScreenStateClassifier.Classify(Screen(lines), cursorRow: 0, screenRows: 5));
    }

    [Fact]
    public void EnterToSelect_NearCursor_ReturnsWaitingForInput()
    {
        var screen = Screen(
            "Pick an option:",
            "Enter to select");
        Assert.Equal(TerminalStatus.WaitingForInput,
            ClaudeScreenStateClassifier.Classify(screen, cursorRow: 1, screenRows: 2));
    }

    [Fact]
    public void NumberedOptions_WithInkCursor_NearIdlePrompt_ReturnsWaitingForInput()
    {
        var screen = Screen(
            "> 1. Option A",
            "  2. Option B",
            "\u276F ");
        Assert.Equal(TerminalStatus.WaitingForInput,
            ClaudeScreenStateClassifier.Classify(screen, cursorRow: 2, screenRows: 3));
    }

    [Fact]
    public void NumberedList_WithoutInkCursor_NearIdlePrompt_ReturnsIdle()
    {
        // Regular numbered list in Claude's output (e.g. commit list) should not
        // trigger WaitingForInput — only Ink selection UI with "> " cursor prefix.
        var screen = Screen(
            "Three commits created:",
            "1. a215e55 \u2014 ConfigureAwait(false) across 18 files",
            "2. 08555a0 \u2014 Extract NextTradingDay to shared ExchangeCalendar",
            "3. c72b830 \u2014 Harden SendAsync exception handling",
            "\u276F ");
        Assert.Equal(TerminalStatus.Idle,
            ClaudeScreenStateClassifier.Classify(screen, cursorRow: 4, screenRows: 5));
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
            ClaudeScreenStateClassifier.Classify(Screen(lines), cursorRow: 1, screenRows: 5));
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
            ClaudeScreenStateClassifier.Classify(Screen(lines), cursorRow: 0, screenRows: 5));
    }

    [Fact]
    public void EscToCancel_NearLastContent_CursorAtBottom_ReturnsWaitingForInput()
    {
        // Large terminal: "Esc to cancel" is near last content but cursor is at the
        // Ink status bar at screen bottom, far from content.
        var lines = new string[35];
        lines[20] = "Do you want to proceed?";
        lines[21] = "> 1. Yes";
        lines[22] = "   2. No";
        lines[23] = "";
        lines[24] = "Esc to cancel \u00B7 Tab to amend \u00B7 ctrl+e to explain";
        for (int i = 25; i < 35; i++) lines[i] = "";
        Assert.Equal(TerminalStatus.WaitingForInput,
            ClaudeScreenStateClassifier.Classify(Screen(lines), cursorRow: 34, screenRows: 35));
    }

    [Fact]
    public void ToolApprovalWithAgentTree_ReturnsWaitingForInput()
    {
        // Real scenario: Claude running agents shows tool approval prompt.
        // The agent tree lines should not trigger false working indicator.
        var lines = new string[35];
        lines[5] = "\u25CF Running 2 Explore agents\u2026 (ctrl+o to expand)";
        lines[6] = "\u251C\u2500 Explore alert/audio system \u00B7 0 tool uses \u00B7 13.1k tokens";
        lines[7] = "\u2502  \u2514 Initializing\u2026";
        lines[8] = "\u251C\u2500 Explore session state \u00B7 15 tool uses \u00B7 41.5k tokens";
        lines[9] = "\u2502  \u2514 Searching for 8 patterns, reading 7 files\u2026";
        lines[12] = "Bash command";
        lines[14] = "  cd /d/Sources && find . -type f";
        lines[16] = "Do you want to proceed?";
        lines[17] = "> 1. Yes";
        lines[18] = "   2. No";
        lines[19] = "";
        lines[20] = "Esc to cancel \u00B7 Tab to amend \u00B7 ctrl+e to explain";
        for (int i = 21; i < 35; i++) lines[i] = "";
        Assert.Equal(TerminalStatus.WaitingForInput,
            ClaudeScreenStateClassifier.Classify(Screen(lines), cursorRow: 34, screenRows: 35));
    }

    [Fact]
    public void SpinnerGlyph_WithoutEllipsis_NotTreatedAsWorking()
    {
        // A line starting with · (U+00B7) as a bullet/separator should not
        // trigger the working indicator when it has no ellipsis.
        var screen = Screen(
            "\u00B7 Some bullet point text",
            "> 1. Option A",
            "  2. Option B",
            "Esc to cancel");
        Assert.Equal(TerminalStatus.WaitingForInput,
            ClaudeScreenStateClassifier.Classify(screen, cursorRow: 3, screenRows: 4));
    }

    [Fact]
    public void EscToCancel_InResponseText_NotTreatedAsWaiting()
    {
        // "Esc to cancel" appearing mid-sentence in Claude's response text
        // should not trigger WaitingForInput — only the actual TUI footer
        // (which starts with "Esc to cancel") should match.
        var lines = new string[5];
        lines[0] = "Added a third check: \"Esc to cancel\" within 5 rows of the last row.";
        lines[1] = "\u273B Crunched for 13m 40s";  // completion summary
        lines[2] = "\u276F ";                       // idle prompt
        lines[3] = "";
        lines[4] = "";
        Assert.Equal(TerminalStatus.Idle,
            ClaudeScreenStateClassifier.Classify(Screen(lines), cursorRow: 2, screenRows: 5));
    }

    // ── "Do you want ...?" detection ─────────────────────────────────

    [Fact]
    public void DoYouWant_NearIdlePrompt_ReturnsWaitingForInput()
    {
        var screen = Screen(
            "Some output text",
            "Do you want me to check what version you're on so the tag matches?",
            "\u276F ");
        Assert.Equal(TerminalStatus.WaitingForInput,
            ClaudeScreenStateClassifier.Classify(screen, cursorRow: 2, screenRows: 3));
    }

    [Fact]
    public void DoYouWant_FarFromIdlePrompt_ReturnsIdle()
    {
        // "Do you want" more than 10 rows above prompt — too far, treated as output text
        var lines = new string[15];
        lines[0] = "Do you want me to proceed?";
        for (int i = 1; i < 14; i++) lines[i] = "";
        lines[14] = "\u276F ";
        Assert.Equal(TerminalStatus.Idle,
            ClaudeScreenStateClassifier.Classify(Screen(lines), cursorRow: 14, screenRows: 15));
    }

    [Fact]
    public void DoYouWant_WithWorkingSpinner_ReturnsWorking()
    {
        var screen = Screen(
            "Do you want me to proceed?",
            "\u273B Sketching\u2026 (1m 44s)",
            "\u276F ");
        Assert.Equal(TerminalStatus.Working,
            ClaudeScreenStateClassifier.Classify(screen, cursorRow: 2, screenRows: 3));
    }

    [Fact]
    public void DoYouWant_WithoutIdlePrompt_ReturnsWorking()
    {
        // "Do you want" without any idle prompt visible — Claude is still producing output
        var screen = Screen(
            "Do you want me to proceed?",
            "Some more output");
        Assert.Equal(TerminalStatus.Working,
            ClaudeScreenStateClassifier.Classify(screen, cursorRow: 1, screenRows: 2));
    }

    // ── Edge cases ───────────────────────────────────────────────────

    [Fact]
    public void InvalidCursorRow_ReturnsWorking()
    {
        var screen = Screen("some text");
        Assert.Equal(TerminalStatus.Working,
            ClaudeScreenStateClassifier.Classify(screen, cursorRow: -1, screenRows: 1));
    }

    [Fact]
    public void ZeroScreenRows_ReturnsWorking()
    {
        var screen = Screen();
        Assert.Equal(TerminalStatus.Working,
            ClaudeScreenStateClassifier.Classify(screen, cursorRow: 0, screenRows: 0));
    }

    [Fact]
    public void EmptyScreen_ReturnsWorking()
    {
        var screen = Screen("", "", "");
        Assert.Equal(TerminalStatus.Working,
            ClaudeScreenStateClassifier.Classify(screen, cursorRow: 0, screenRows: 3));
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
            ClaudeScreenStateClassifier.Classify(Screen(lines), cursorRow: 14, screenRows: 15));
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
            ClaudeScreenStateClassifier.Classify(Screen(lines), cursorRow: 3, screenRows: 5));
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
            ClaudeScreenStateClassifier.Classify(Screen(lines), cursorRow: 19, screenRows: 20));
    }

    [Fact]
    public void StaleInkSelection_FarFromCursor_NotWaiting()
    {
        var lines = new string[40];
        lines[2] = "> 1. let me test the current state";
        lines[3] = "  2. then we might do enum refactor";
        for (int i = 4; i < 39; i++) lines[i] = "";
        lines[38] = "Some output text";
        lines[39] = "";
        Assert.Equal(TerminalStatus.Working,
            ClaudeScreenStateClassifier.Classify(Screen(lines), cursorRow: 39, screenRows: 40));
    }
}
