using System.Text;
using RaisinTerminal.Core.Models;

namespace RaisinTerminal.Core.Terminal;

/// <summary>
/// Cleans visual artifacts left by TUI applications (e.g. Claude Code) after exit.
/// ConPTY often rewrites lines at col 0 without erasing trailing old content,
/// leaving autocomplete dropdown text mixed into the exit output.
/// </summary>
public static class TuiArtifactCleaner
{
    /// <summary>
    /// Erases Claude Code artifacts from the terminal buffer after exit.
    /// Scans lines above the cursor for autocomplete ghosts and trailing
    /// artifacts on --resume command lines, then compacts the buffer.
    /// </summary>
    public static void CleanClaudeArtifacts(TerminalEmulator emulator, TerminalBuffer buffer)
    {
        // Erase from cursor to end-of-screen (cleans artifacts at/below prompt)
        emulator.EraseBelow();

        // Scan lines above cursor for artifacts left by ConPTY's incomplete rewrites
        var fill = new CellData(' ', CellData.DefaultFgR, CellData.DefaultFgG, CellData.DefaultFgB,
                                CellData.DefaultBgR, CellData.DefaultBgG, CellData.DefaultBgB);
        int removedCount = 0;
        int firstRemovedRow = -1;

        for (int row = buffer.CursorRow - 1; row >= Math.Max(0, buffer.CursorRow - 20); row--)
        {
            var sb = new StringBuilder(buffer.Columns);
            for (int col = 0; col < buffer.Columns; col++)
                sb.Append(buffer.GetCell(row, col).Character);
            string line = sb.ToString().TrimEnd();
            string trimmed = line.TrimStart();

            // Standalone autocomplete line: "/command   Description"
            if (IsClaudeAutocompleteLine(trimmed))
            {
                for (int c = 0; c < buffer.Columns; c++)
                    buffer.SetCell(row, c, fill);
                removedCount++;
                firstRemovedRow = row;
                continue;
            }

            // Resume command with trailing artifact: claude --resume "name"  Artifact
            if (trimmed.Contains("--resume", StringComparison.Ordinal))
            {
                int lastQuote = trimmed.LastIndexOf('"');
                if (lastQuote > 0)
                {
                    int leadingSpaces = line.Length - line.TrimStart().Length;
                    int eraseCol = leadingSpaces + lastQuote + 1;
                    for (int c = eraseCol; c < buffer.Columns; c++)
                        buffer.SetCell(row, c, fill);
                }
            }
        }

        // Compact: shift rows up to fill gaps left by erased autocomplete lines
        if (removedCount > 0 && firstRemovedRow >= 0)
        {
            int regionEnd = buffer.CursorRow;
            int writeRow = firstRemovedRow;
            for (int readRow = firstRemovedRow; readRow <= regionEnd; readRow++)
            {
                // Skip rows that were erased (now blank)
                bool isBlank = true;
                for (int c = 0; c < buffer.Columns && isBlank; c++)
                {
                    char ch = buffer.GetCell(readRow, c).Character;
                    if (ch != ' ' && ch != '\0') isBlank = false;
                }
                if (isBlank && readRow < buffer.CursorRow)
                    continue;

                if (writeRow != readRow)
                {
                    for (int c = 0; c < buffer.Columns; c++)
                        buffer.SetCell(writeRow, c, buffer.GetCell(readRow, c));
                }
                if (readRow == buffer.CursorRow)
                    buffer.CursorRow = writeRow;
                writeRow++;
            }

            // Clear vacated rows at the bottom of the compacted region
            for (int r = writeRow; r <= regionEnd; r++)
                for (int c = 0; c < buffer.Columns; c++)
                    buffer.SetCell(r, c, fill);
        }
    }

    /// <summary>
    /// Returns true if the trimmed line matches Claude Code's autocomplete pattern:
    /// "/command   Description text" (slash, word, 2+ spaces, then description).
    /// </summary>
    internal static bool IsClaudeAutocompleteLine(string trimmed)
    {
        if (trimmed.Length < 4 || trimmed[0] != '/' || !char.IsLetter(trimmed[1]))
            return false;
        int i = 2;
        while (i < trimmed.Length && (char.IsLetterOrDigit(trimmed[i]) || trimmed[i] == '-'))
            i++;
        if (i >= trimmed.Length) return false;
        int spaces = 0;
        while (i < trimmed.Length && trimmed[i] == ' ') { i++; spaces++; }
        return spaces >= 2 && i < trimmed.Length && char.IsUpper(trimmed[i]);
    }
}
