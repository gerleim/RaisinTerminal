namespace RaisinTerminal.Core.Terminal;

/// <summary>
/// Classifies Claude Code's TUI screen state by scanning terminal buffer content.
/// Works in two phases: (1) scan all rows collecting positional facts, (2) evaluate
/// priority-ordered rules against those facts to determine status.
/// </summary>
public static class ClaudeScreenStateClassifier
{
    // Claude Code's thinking spinner cycles through these glyphs:
    // · (U+00B7) ✢ (U+2722) ✳ (U+2733) ✶ (U+2736) ✻ (U+273B) ✽ (U+273D)
    internal static readonly HashSet<char> ClaudeSpinnerGlyphs =
        ['\u00B7', '\u2722', '\u2733', '\u2736', '\u273B', '\u273D'];

    /// <summary>
    /// Positional facts collected from a single scan of all screen rows.
    /// </summary>
    internal struct ScreenScanResult
    {
        public bool FoundIdlePrompt;
        public int IdlePromptRow;

        public bool FoundWorkingIndicator;

        public bool FoundCompletionSummary;
        public int CompletionSummaryRow;

        public bool FoundInkSelectionCursor;
        public int Option1Row;
        public int Option2Row;

        public int EscToCancelRow;
        public int LastNonEmptyRow;

        public bool FoundExplicitWaitingTrigger;
        public bool FoundLocalAgents;

        public bool FoundDoYouWantQuestion;
        public int DoYouWantQuestionRow;

        public static ScreenScanResult Create() => new()
        {
            IdlePromptRow = -1,
            CompletionSummaryRow = -1,
            Option1Row = -1,
            Option2Row = -1,
            EscToCancelRow = -1,
            LastNonEmptyRow = -1,
            DoYouWantQuestionRow = -1,
        };
    }

    /// <summary>
    /// Scans screen lines to classify Claude Code's state.
    /// </summary>
    /// <param name="getLineText">Delegate that returns the text content of a screen row.</param>
    /// <param name="cursorRow">The current cursor row position.</param>
    /// <param name="screenRows">Total number of visible screen rows.</param>
    public static TerminalStatus Classify(Func<int, string> getLineText, int cursorRow, int screenRows)
    {
        if (cursorRow < 0 || screenRows <= 0) return TerminalStatus.Working;

        var scan = ScanAllRows(getLineText, cursorRow, screenRows);
        return EvaluateStatus(scan, cursorRow);
    }

    // ── Phase 1: Scan ────────────────────────────────────────────────

    private static ScreenScanResult ScanAllRows(Func<int, string> getLineText, int cursorRow, int screenRows)
    {
        var scan = ScreenScanResult.Create();

        for (int row = 0; row < screenRows; row++)
        {
            var line = getLineText(row);
            var trimmed = line.TrimStart();

            CheckIdlePrompt(trimmed, row, ref scan);
            CheckSpinnerOrCompletion(trimmed, row, ref scan);
            CheckBroadWorkingIndicator(trimmed, ref scan);
            CheckNumberedOptions(trimmed, row, ref scan);
            CheckDoYouWantQuestion(trimmed, row, ref scan);
            CheckExplicitWaitingTriggers(trimmed, row, cursorRow, screenRows, ref scan);
            CheckLocalAgents(trimmed, row, screenRows, ref scan);

            if (trimmed.Length > 0)
                scan.LastNonEmptyRow = row;
        }

        return scan;
    }

    /// <summary>
    /// Detects Claude's idle prompt: ❯ (U+276F) on any line, or ASCII ">"
    /// on short lines (≤3 chars) to avoid matching quoted text.
    /// </summary>
    private static void CheckIdlePrompt(string trimmed, int row, ref ScreenScanResult scan)
    {
        if (trimmed.StartsWith("\u276F"))
        {
            scan.FoundIdlePrompt = true;
            scan.IdlePromptRow = row;
        }

        if (trimmed.StartsWith(">") && trimmed.Length <= 3)
        {
            scan.FoundIdlePrompt = true;
            scan.IdlePromptRow = row;
        }
    }

    /// <summary>
    /// Detects lines starting with a Claude spinner glyph (·✢✳✶✻✽).
    /// With ellipsis (… or ...) → working spinner.
    /// Without ellipsis but with duration → completion summary (e.g. "✻ Brewed for 5m 43s").
    /// Without either → not a spinner (e.g. · as bullet point).
    /// </summary>
    private static void CheckSpinnerOrCompletion(string trimmed, int row, ref ScreenScanResult scan)
    {
        if (trimmed.Length == 0 || !ClaudeSpinnerGlyphs.Contains(trimmed[0]))
            return;

        if (trimmed.Contains('\u2026') || trimmed.Contains("..."))
            scan.FoundWorkingIndicator = true;
        else if (HasDurationPattern(trimmed))
        {
            scan.FoundCompletionSummary = true;
            scan.CompletionSummaryRow = row;
        }
    }

    /// <summary>
    /// Broader working detection for unknown spinner glyphs: non-alnum char + space +
    /// ellipsis + ↓ (U+2193). Catches new spinner characters Claude Code may add.
    /// </summary>
    private static void CheckBroadWorkingIndicator(string trimmed, ref ScreenScanResult scan)
    {
        if (scan.FoundWorkingIndicator) return;
        if (trimmed.Length <= 2) return;
        if (char.IsLetterOrDigit(trimmed[0])) return;
        if (trimmed[1] != ' ') return;

        if ((trimmed.Contains('\u2026') || trimmed.Contains("...")) && trimmed.Contains('\u2193'))
            scan.FoundWorkingIndicator = true;
    }

    /// <summary>
    /// Tracks "1." and "2." numbered option lines and the Ink "> " selection cursor.
    /// The Ink cursor distinguishes interactive selection UI from regular numbered lists
    /// in Claude's output text.
    /// </summary>
    private static void CheckNumberedOptions(string trimmed, int row, ref ScreenScanResult scan)
    {
        var optLine = trimmed;
        if (optLine.StartsWith("> "))
        {
            optLine = optLine.Substring(2);
            if (optLine.Length > 2 && char.IsDigit(optLine[0]) && optLine[1] == '.')
                scan.FoundInkSelectionCursor = true;
        }

        if (optLine.Length > 2 && optLine[0] == '1' && optLine[1] == '.')
            scan.Option1Row = row;
        if (optLine.Length > 2 && optLine[0] == '2' && optLine[1] == '.')
            scan.Option2Row = row;
    }

    /// <summary>
    /// Detects "Do you want ...?" free-form questions from Claude.
    /// Case-sensitive match for "Do you want" + "?" on the same line.
    /// Proximity to the idle prompt is checked in the evaluation phase.
    /// </summary>
    private static void CheckDoYouWantQuestion(string trimmed, int row, ref ScreenScanResult scan)
    {
        if (trimmed.Contains("Do you want") && trimmed.Contains('?'))
        {
            scan.FoundDoYouWantQuestion = true;
            scan.DoYouWantQuestionRow = row;
        }
    }

    /// <summary>
    /// Detects explicit WaitingForInput triggers:
    /// - "Yes" + "No" on same line near cursor (tool approval buttons)
    /// - "Enter to select" near cursor
    /// - "Esc to cancel" footer near cursor or near screen bottom
    /// - "ctrl-g to edit" footer near screen bottom (plan mode selection UI)
    /// Also records EscToCancelRow for post-loop proximity check against last content.
    /// </summary>
    private static void CheckExplicitWaitingTriggers(string trimmed, int row, int cursorRow, int screenRows, ref ScreenScanResult scan)
    {
        // Near-cursor checks (within 5 rows)
        if (Math.Abs(row - cursorRow) <= 5)
        {
            if (trimmed.Contains("Yes") && trimmed.Contains("No"))
                scan.FoundExplicitWaitingTrigger = true;

            if (trimmed.Contains("Enter to select"))
                scan.FoundExplicitWaitingTrigger = true;

            if (trimmed.StartsWith("Esc to cancel"))
                scan.FoundExplicitWaitingTrigger = true;
        }

        // Near-bottom checks (within bottom 5 rows)
        if (row >= screenRows - 5)
        {
            if (trimmed.StartsWith("Esc to cancel"))
                scan.FoundExplicitWaitingTrigger = true;

            if (trimmed.Contains("ctrl-g to edit"))
                scan.FoundExplicitWaitingTrigger = true;
        }

        // Track position for proximity check against last content row
        if (trimmed.StartsWith("Esc to cancel"))
            scan.EscToCancelRow = row;
    }

    /// <summary>
    /// Detects "local agent" text in bottom 5 screen rows (Claude's agent status bar).
    /// </summary>
    private static void CheckLocalAgents(string trimmed, int row, int screenRows, ref ScreenScanResult scan)
    {
        if (row >= screenRows - 5 && trimmed.Contains("local agent"))
            scan.FoundLocalAgents = true;
    }

    // ── Phase 2: Evaluate ────────────────────────────────────────────

    /// <summary>
    /// Determines terminal status from scan results.
    /// Priority: WaitingForInput > AgentsRunning > Idle > Working (default).
    /// </summary>
    private static TerminalStatus EvaluateStatus(ScreenScanResult scan, int cursorRow)
    {
        // 1. Explicit waiting triggers (Yes/No, Enter to select, Esc to cancel, ctrl-g)
        if (scan.FoundExplicitWaitingTrigger)
            return TerminalStatus.WaitingForInput;

        // 2. "Esc to cancel" near last content row (large terminal, cursor at status bar)
        if (IsEscToCancelNearContent(scan))
            return TerminalStatus.WaitingForInput;

        // 3. Numbered options with Ink cursor near idle prompt or cursor
        if (IsNumberedOptionsActive(scan, cursorRow))
            return TerminalStatus.WaitingForInput;

        // 4. "Do you want ...?" question near idle prompt
        if (IsDoYouWantQuestionActive(scan))
            return TerminalStatus.WaitingForInput;

        // 5. Idle prompt visible (no active spinner)
        if (scan.FoundIdlePrompt && !scan.FoundWorkingIndicator)
        {
            if (scan.FoundLocalAgents)
                return TerminalStatus.AgentsRunning;
            return TerminalStatus.Idle;
        }

        // 6. Numbered options near cursor without idle prompt (plan mode selection UI)
        if (IsNumberedOptionsActive(scan, cursorRow) && !scan.FoundWorkingIndicator)
            return TerminalStatus.WaitingForInput;

        // 7. Completion summary + agents, no idle prompt
        if (scan.FoundLocalAgents && !scan.FoundWorkingIndicator && scan.FoundCompletionSummary)
            return TerminalStatus.AgentsRunning;

        // 8. Default
        return TerminalStatus.Working;
    }

    // ── Evaluation helpers ───────────────────────────────────────────

    /// <summary>
    /// "Esc to cancel" near last non-empty row — handles large terminals where
    /// the cursor sits at the Ink status bar far below content.
    /// </summary>
    private static bool IsEscToCancelNearContent(ScreenScanResult scan)
        => scan.EscToCancelRow >= 0 && scan.LastNonEmptyRow >= 0
           && scan.LastNonEmptyRow - scan.EscToCancelRow <= 5;

    /// <summary>
    /// Returns true when numbered options (1./2.) with Ink selection cursor appear
    /// near the idle prompt or cursor, and are not suppressed by a completion summary
    /// above them (which would make them stale output text).
    /// </summary>
    private static bool IsNumberedOptionsActive(ScreenScanResult scan, int cursorRow)
    {
        if (scan.Option1Row < 0 || scan.Option2Row < 0) return false;
        if (!scan.FoundInkSelectionCursor) return false;

        // Suppress options above a completion summary (they're old response text)
        if (scan.FoundCompletionSummary && scan.CompletionSummaryRow > scan.Option1Row)
            return false;

        if (scan.IdlePromptRow >= 0)
        {
            return scan.IdlePromptRow - scan.Option1Row is >= 0 and <= 10
                && scan.IdlePromptRow - scan.Option2Row is >= 0 and <= 10;
        }

        // No idle prompt (Ink TUI screens like plan mode) — options must be
        // close to each other AND near the cursor (not stale selections scrolled above)
        return Math.Abs(scan.Option1Row - scan.Option2Row) <= 5
            && Math.Abs(cursorRow - scan.Option2Row) <= 10;
    }

    /// <summary>
    /// Returns true when "Do you want ...?" appears within 10 rows above the idle
    /// prompt, no active spinner is present, and the idle prompt is visible.
    /// </summary>
    private static bool IsDoYouWantQuestionActive(ScreenScanResult scan)
        => scan.FoundDoYouWantQuestion && scan.FoundIdlePrompt && !scan.FoundWorkingIndicator
           && scan.IdlePromptRow - scan.DoYouWantQuestionRow is >= 0 and <= 10;

    // ── Shared utilities ─────────────────────────────────────────────

    /// <summary>
    /// Returns true if the line contains a duration like "5m 43s", "30s", "1h 2m", etc.
    /// Used to distinguish Claude's completion summary from the working spinner.
    /// </summary>
    internal static bool HasDurationPattern(string line)
    {
        for (int i = 0; i < line.Length - 1; i++)
        {
            if (char.IsDigit(line[i]))
            {
                char next = line[i + 1];
                if (next is 's' or 'm' or 'h')
                {
                    if (i + 2 >= line.Length || line[i + 2] == ' ' || char.IsDigit(line[i + 2]))
                        return true;
                }
            }
        }
        return false;
    }
}
