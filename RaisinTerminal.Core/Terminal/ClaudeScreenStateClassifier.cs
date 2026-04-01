namespace RaisinTerminal.Core.Terminal;

/// <summary>
/// Classifies Claude Code's TUI screen state by scanning terminal buffer content.
/// </summary>
public static class ClaudeScreenStateClassifier
{
    // Claude Code's thinking spinner cycles through these glyphs:
    // · (U+00B7) ✢ (U+2722) ✳ (U+2733) ✶ (U+2736) ✻ (U+273B) ✽ (U+273D)
    internal static readonly HashSet<char> ClaudeSpinnerGlyphs =
        ['\u00B7', '\u2722', '\u2733', '\u2736', '\u273B', '\u273D'];

    /// <summary>
    /// Scans screen lines to classify Claude Code's state.
    /// </summary>
    /// <param name="getLineText">Delegate that returns the text content of a screen row.</param>
    /// <param name="cursorRow">The current cursor row position.</param>
    /// <param name="screenRows">Total number of visible screen rows.</param>
    public static TerminalStatus Classify(Func<int, string> getLineText, int cursorRow, int screenRows)
    {
        if (cursorRow < 0 || screenRows <= 0) return TerminalStatus.Working;

        // Claude's Ink TUI may place the cursor on the status bar at the screen
        // bottom, far from the actual "❯" prompt. Scan all visible rows, but use
        // targeted checks to avoid false positives from normal output text.
        TerminalStatus? result = null;
        bool foundIdlePrompt = false;
        bool foundWorkingIndicator = false;
        bool foundCompletionSummary = false;
        int completionSummaryRow = -1;
        int idlePromptRow = -1;
        bool foundNumberedOptions = false;
        bool foundLocalAgents = false;
        bool foundInkSelectionCursor = false;
        int foundOption1Row = -1;
        int foundOption2Row = -1;
        int escToCancelRow = -1;
        int lastNonEmptyRow = -1;
        for (int row = 0; row < screenRows; row++)
        {
            var line = getLineText(row);
            var trimmed = line.TrimStart();

            // Claude's idle prompt uses "❯" (U+276F). Safe to scan all rows
            // since quoted user messages use ">" (ASCII), not "❯".
            if (trimmed.StartsWith("\u276F"))
            {
                foundIdlePrompt = true;
                idlePromptRow = row;
            }

            // Also check ">" but only on short lines to avoid matching quoted text
            if (trimmed.StartsWith(">") && trimmed.Length <= 3)
            {
                foundIdlePrompt = true;
                idlePromptRow = row;
            }

            // Working indicator: Claude Code's spinner cycles through these
            // star-like glyphs before the status text (e.g. "✳ Ionizing...")
            // Exclude the completion summary (e.g. "✻ Brewed for 5m 43s") which
            // reuses the same glyphs but shows a duration — the verb varies randomly.
            // The working spinner always contains '…' (ellipsis) e.g. "✻ Sketching… (1m 44s · ↓ 269 tokens)"
            // while the completion summary does not (e.g. "✻ Brewed for 5m 43s.").
            if (trimmed.Length > 0 && ClaudeSpinnerGlyphs.Contains(trimmed[0]))
            {
                // Check both Unicode ellipsis '…' (U+2026) and ASCII "..." — Claude Code
                // may emit either depending on the Ink renderer path.
                // The working spinner always has an ellipsis (e.g. "✻ Sketching…").
                // Lines with a spinner glyph but no ellipsis and a duration are completion
                // summaries. Lines with neither ellipsis nor duration are NOT spinners —
                // they're likely "·" used as a bullet or separator in Claude's output.
                if (trimmed.Contains('…') || trimmed.Contains("..."))
                    foundWorkingIndicator = true;
                else if (HasDurationPattern(trimmed))
                {
                    foundCompletionSummary = true; // e.g. "✻ Churned for 45s" — Claude finished
                    completionSummaryRow = row;
                }
            }

            // Broader detection: Claude's working status bar shows
            // "glyph Verb… (duration · ↓ N tokens · ...)" — detect this even
            // when the leading glyph isn't in our known spinner set (Claude Code
            // may add new spinner characters). The ↓ + ellipsis combo is distinctive.
            if (!foundWorkingIndicator && trimmed.Length > 2
                && !char.IsLetterOrDigit(trimmed[0])
                && trimmed[1] == ' '
                && (trimmed.Contains('…') || trimmed.Contains("..."))
                && trimmed.Contains('\u2193')) // ↓
            {
                foundWorkingIndicator = true;
            }

            // Track numbered options (e.g. "1. Paste from clipboard", "2. Open a new tab")
            // These indicate Claude asked a question with choices.
            // Record the row so we can later check if they're near the idle prompt
            // (Claude places numbered options just above the ❯ prompt).
            // The selected option may have a "> " prefix from Ink's selection cursor.
            var optLine = trimmed;
            if (optLine.StartsWith("> "))
            {
                optLine = optLine.Substring(2);
                // Ink's selection cursor: "> N." on a numbered option line.
                // Claude Code's selection UI always highlights the focused option
                // with "> " — regular numbered lists in output never have it.
                if (optLine.Length > 2 && char.IsDigit(optLine[0]) && optLine[1] == '.')
                    foundInkSelectionCursor = true;
            }
            if (optLine.Length > 2 && optLine[0] == '1' && optLine[1] == '.')
                foundOption1Row = row;
            if (optLine.Length > 2 && optLine[0] == '2' && optLine[1] == '.')
                foundOption2Row = row;

            // Track last non-empty row so we can check for footers near content end
            // (Ink TUI may place the cursor at the status bar far from actual content).
            if (trimmed.Length > 0)
                lastNonEmptyRow = row;

            // Track "Esc to cancel" position — checked below against multiple anchors.
            // Use StartsWith to avoid matching "Esc to cancel" in Claude's response text
            // (e.g. explaining what the shortcut does). The actual footer always starts
            // with "Esc to cancel · ..." on its own line.
            if (trimmed.StartsWith("Esc to cancel"))
                escToCancelRow = row;

            // Tool approval / yes-no prompts (scan near cursor only)
            if (Math.Abs(row - cursorRow) <= 5)
            {
                if (trimmed.Contains("Yes") && trimmed.Contains("No"))
                    result = TerminalStatus.WaitingForInput;

                if (trimmed.Contains("Enter to select"))
                    result = TerminalStatus.WaitingForInput;

                // "Esc to cancel" footer near the cursor
                if (trimmed.StartsWith("Esc to cancel"))
                    result = TerminalStatus.WaitingForInput;
            }

            // "Esc to cancel" footer near the bottom of the screen.
            if (row >= screenRows - 5 && trimmed.StartsWith("Esc to cancel"))
                result = TerminalStatus.WaitingForInput;

            // "ctrl-g to edit" footer appears in plan mode selection UI
            if (row >= screenRows - 5 && trimmed.Contains("ctrl-g to edit"))
                result = TerminalStatus.WaitingForInput;

            // Background agents: Claude's status bar shows "N local agent(s)"
            // when agents are running — tracked separately so the main session
            // can show a distinct cyan indicator instead of green Working.
            if (row >= screenRows - 5 && trimmed.Contains("local agent"))
                foundLocalAgents = true;
        }

        // "Esc to cancel" near the last non-empty row — handles large terminals where
        // the cursor is at the Ink status bar (screen bottom) far from the content,
        // and the content doesn't reach the bottom 5 screen rows.
        if (result != TerminalStatus.WaitingForInput
            && escToCancelRow >= 0 && lastNonEmptyRow >= 0
            && lastNonEmptyRow - escToCancelRow <= 5)
            result = TerminalStatus.WaitingForInput;

        // Only treat numbered options as a question if both "1." and "2." appear
        // near the idle prompt or cursor (within 10 rows). This prevents false positives
        // from numbered lists in regular Claude output.
        // When no idle prompt is found (e.g. plan mode selection UI), fall back to
        // cursor row as anchor.
        // Suppress numbered options that appear ABOVE a completion summary (they're
        // old response text). But options BELOW the summary are from a new interaction
        // (e.g. tool approval after the previous task finished).
        bool completionSuppressesOptions = foundCompletionSummary
            && completionSummaryRow > foundOption1Row;
        if (foundOption1Row >= 0 && foundOption2Row >= 0 && !completionSuppressesOptions
            && foundInkSelectionCursor)
        {
            if (idlePromptRow >= 0)
            {
                // Options must be within 10 rows above the idle prompt
                foundNumberedOptions = idlePromptRow - foundOption1Row is >= 0 and <= 10
                    && idlePromptRow - foundOption2Row is >= 0 and <= 10;
            }
            else
            {
                // No idle prompt (Ink TUI screens like plan mode) — just verify
                // the options are close to each other (not scattered in output)
                foundNumberedOptions = Math.Abs(foundOption1Row - foundOption2Row) <= 5;
            }
        }

        // WaitingForInput takes priority, then idle prompt with numbered
        // options (Claude asked a question), then plain idle prompt, then Working
        if (result != TerminalStatus.WaitingForInput && foundIdlePrompt && !foundWorkingIndicator)
        {
            if (foundNumberedOptions)
                result = TerminalStatus.WaitingForInput;
            else if (foundLocalAgents)
                result = TerminalStatus.AgentsRunning;
            else
                result = TerminalStatus.Idle;
        }

        // Numbered options near cursor without idle prompt (e.g. plan mode selection UI)
        if (result == null && foundNumberedOptions && !foundWorkingIndicator)
            result = TerminalStatus.WaitingForInput;

        // Completion summary + agents but no idle prompt visible
        if (result == null && foundLocalAgents && !foundWorkingIndicator && foundCompletionSummary)
            result = TerminalStatus.AgentsRunning;

        if (result == null)
            result = TerminalStatus.Working;

        return result.Value;
    }

    /// <summary>
    /// Returns true if the line contains a duration like "5m 43s", "30s", "1h 2m", etc.
    /// Used to distinguish Claude's completion summary from the working spinner.
    /// </summary>
    internal static bool HasDurationPattern(string line)
    {
        // Look for digit(s) followed by 's', 'm', or 'h' (time units)
        for (int i = 0; i < line.Length - 1; i++)
        {
            if (char.IsDigit(line[i]))
            {
                char next = line[i + 1];
                if (next is 's' or 'm' or 'h')
                {
                    // Ensure it's not part of a longer word (check char after unit)
                    if (i + 2 >= line.Length || line[i + 2] == ' ' || char.IsDigit(line[i + 2]))
                        return true;
                }
            }
        }
        return false;
    }
}
