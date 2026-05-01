using Raisin.EventSystem;
using RaisinTerminal.Core.Models;

namespace RaisinTerminal.Core.Terminal;

public partial class TerminalEmulator
{
    private void OnCsiDispatch(char final, int[] pars, byte[] intermediates, byte privateMarker)
    {
        // DEC private mode sequences (ESC[?...h to set, ESC[?...l to reset)
        if (privateMarker == (byte)'?')
        {
            bool set = final == 'h';
            if (final != 'h' && final != 'l') return;

            foreach (int mode in pars)
            {
                switch (mode)
                {
                    case 1:    // DECCKM — Application Cursor Keys
                        _events?.Log(this, $"DECSET 1 ApplicationCursorKeys={set}", category: "Terminal");
                        ApplicationCursorKeys = set;
                        break;
                    case 7:    // DECAWM — Auto-Wrap Mode
                        AutoWrap = set;
                        break;
                    case 12:   // Cursor blink (att610) — ignored, we handle blink in the view
                        break;
                    case 25:   // DECTCEM — Show/Hide Cursor
                        CursorEnabled = set;
                        break;
                    case 47:   // Alternate Screen Buffer (switch only, no cursor save/clear)
                    case 1047: // Alternate Screen Buffer (switch + clear on enter)
                        _events?.Log(this, $"DECSET {mode} AlternateScreen={set}", category: "Terminal");
                        if (set && !AlternateScreen)
                        {
                            SaveMainScreen();
                            if (mode == 1047) Buffer.Clear();
                            Buffer.SuppressScrollback = true;
                            AlternateScreen = true;
                            AlternateScreenEntered?.Invoke();
                        }
                        else if (!set && AlternateScreen)
                        {
                            RestoreMainScreen(restoreCursor: false);
                            Buffer.ScrollTop = 0;
                            Buffer.ScrollBottom = Buffer.Rows - 1;
                            Buffer.SuppressScrollback = false;
                            AlternateScreen = false;
                            AlternateScreenExited?.Invoke();
                        }
                        break;
                    case 1049: // Alternate Screen Buffer (save cursor + switch + clear)
                        _events?.Log(this, $"DECSET 1049 AlternateScreen={set}", category: "Terminal");
                        if (set)
                        {
                            _savedCursorRow = Buffer.CursorRow;
                            _savedCursorCol = Buffer.CursorCol;
                            SaveMainScreen();
                            Buffer.Clear();
                            Buffer.SuppressScrollback = true;
                            AlternateScreen = true;
                            AlternateScreenEntered?.Invoke();
                        }
                        else
                        {
                            RestoreMainScreen(restoreCursor: true);
                            Buffer.ScrollTop = 0;
                            Buffer.ScrollBottom = Buffer.Rows - 1;
                            Buffer.SuppressScrollback = false;
                            AlternateScreen = false;
                            AlternateScreenExited?.Invoke();
                        }
                        break;
                    case 2004: // Bracketed Paste Mode
                        _events?.Log(this, $"DECSET 2004 BracketedPaste={set}", category: "Terminal");
                        BracketedPasteMode = set;
                        break;
                    case 2026: // Synchronized Output (DEC mode 2026)
                        SynchronizedOutput = set;
                        if (set)
                        {
                            // Start of a new sync block.
                        }
                        // Scrollback restoration is handled exclusively by the
                        // post-sync grace period in TerminalSessionViewModel.
                        // Restoring here on sync-OFF when the block lacked ED 2
                        // is too early: Claude Code's Ink TUI interleaves
                        // incremental sync blocks (spinner updates, no ED 2)
                        // between full-redraw frames (with ED 2).  Restoring
                        // scrollback after an incremental block lets stray LFs
                        // between frames leak screen content into the scrollback,
                        // causing duplicate lines when the next full frame redraws
                        // the same content.
                        break;
                }
            }
            return;
        }

        // Ignore other private-marker sequences (e.g. CSI < ... u for Kitty keyboard protocol)
        if (privateMarker != 0)
            return;

        int p0 = pars.Length > 0 ? pars[0] : 0;
        int p1 = pars.Length > 1 ? pars[1] : 0;

        switch (final)
        {
            case 'A': // CUU - Cursor Up
                Buffer.CursorRow = Math.Max(0, Buffer.CursorRow - Math.Max(1, p0));
                ClearPendingWrap();
                break;
            case 'B': // CUD - Cursor Down
                Buffer.CursorRow = Math.Min(Buffer.Rows - 1, Buffer.CursorRow + Math.Max(1, p0));
                ClearPendingWrap();
                break;
            case 'C': // CUF - Cursor Forward
                Buffer.CursorCol = Math.Min(Buffer.Columns - 1, Buffer.CursorCol + Math.Max(1, p0));
                break;
            case 'D': // CUB - Cursor Back
                Buffer.CursorCol = Math.Max(0, Buffer.CursorCol - Math.Max(1, p0));
                break;
            case 'E': // CNL - Cursor Next Line
                Buffer.CursorCol = 0;
                Buffer.CursorRow = Math.Min(Buffer.Rows - 1, Buffer.CursorRow + Math.Max(1, p0));
                break;
            case 'F': // CPL - Cursor Previous Line
                Buffer.CursorCol = 0;
                Buffer.CursorRow = Math.Max(0, Buffer.CursorRow - Math.Max(1, p0));
                break;
            case 'G': // CHA - Cursor Horizontal Absolute
                Buffer.CursorCol = Math.Clamp(Math.Max(1, p0) - 1, 0, Buffer.Columns - 1);
                break;
            case 'H': // CUP - Cursor Position
            case 'f':
                {
                    int newRow = Math.Clamp(Math.Max(1, p0) - 1, 0, Buffer.Rows - 1);
                    int newCol = Math.Clamp(Math.Max(1, p1) - 1, 0, Buffer.Columns - 1);
                    // Cursor home (CUP 1;1) while Claude owns the terminal is a TUI
                    // redraw signal: same rationale as ED 2 in EraseInDisplay. Claude
                    // Code's spinner-phase redraws use [H + per-line [K rewrite without
                    // ever emitting [2J or DEC 2026 sync, so we'd otherwise miss them
                    // and let trailing LFs leak duplicate rows into scrollback.
                    if (newRow == 0 && newCol == 0 && !AlternateScreen
                        && ClaudeRedrawSuppression)
                    {
                        // New frame: discard previous frame's deferred overflow so
                        // only the latest frame's rows survive (prevents duplicates).
                        Buffer.ClearDeferredScrollback();

                        if (!_syncRedrawSuppressScrollback)
                        {
                            if (_resizeGrace)
                            {
                                // Post-resize grace: ConPTY reflow — don't re-enable
                            }
                            else if (_skipNextCursorHomeSuppress)
                            {
                                _skipNextCursorHomeSuppress = false;
                            }
                            else
                            {
                                _syncRedrawSuppressScrollback = true;
                                Buffer.SuppressScrollback = true;
                            }
                        }
                    }
                    Buffer.CursorRow = newRow;
                    Buffer.CursorCol = newCol;
                }
                break;
            case 'J': // ED - Erase in Display
                EraseInDisplay(p0);
                break;
            case 'K': // EL - Erase in Line
                EraseInLine(p0);
                break;
            case 'L': // IL - Insert Lines
                InsertLines(Math.Max(1, p0));
                break;
            case 'M': // DL - Delete Lines
                DeleteLines(Math.Max(1, p0));
                break;
            case 'P': // DCH - Delete Characters
                DeleteCharacters(Math.Max(1, p0));
                break;
            case 'X': // ECH - Erase Characters (don't move cursor)
                EraseCharacters(Math.Max(1, p0));
                break;
            case 'S': // SU - Scroll Up
                {
                    int n = Math.Max(1, p0);
                    var fill = MakeEraseCell();
                    for (int i = 0; i < n; i++)
                        Buffer.ScrollUpRegion(Buffer.ScrollTop, Buffer.ScrollBottom, fill);
                }
                break;
            case 'T': // SD - Scroll Down (only when no private marker)
                if (privateMarker == 0)
                {
                    int n = Math.Max(1, p0);
                    var fill = MakeEraseCell();
                    for (int i = 0; i < n; i++)
                        Buffer.ScrollDownRegion(Buffer.ScrollTop, Buffer.ScrollBottom, fill);
                }
                break;
            case '@': // ICH - Insert Characters
                InsertCharacters(Math.Max(1, p0));
                break;
            case 'b': // REP - Repeat last printed character
                {
                    if (_lastPrintedChar != '\0')
                    {
                        int n = Math.Max(1, p0);
                        for (int i = 0; i < n; i++)
                            OnPrint(_lastPrintedChar);
                    }
                }
                break;
            case 'd': // VPA - Line Position Absolute
                Buffer.CursorRow = Math.Clamp(Math.Max(1, p0) - 1, 0, Buffer.Rows - 1);
                ClearPendingWrap();
                break;
            case 'm': // SGR - Select Graphic Rendition
                HandleSgr(pars);
                break;
            case 'r': // DECSTBM - Set Scrolling Region
                if (p0 == 0 && p1 == 0)
                {
                    // Reset to full screen
                    Buffer.ScrollTop = 0;
                    Buffer.ScrollBottom = Buffer.Rows - 1;
                }
                else
                {
                    int top = Math.Max(1, p0) - 1;
                    int bottom = (p1 == 0 ? Buffer.Rows : p1) - 1;
                    top = Math.Clamp(top, 0, Buffer.Rows - 1);
                    bottom = Math.Clamp(bottom, 0, Buffer.Rows - 1);
                    if (top < bottom)
                    {
                        Buffer.ScrollTop = top;
                        Buffer.ScrollBottom = bottom;
                    }
                }
                // Move cursor to home position
                Buffer.CursorRow = 0;
                Buffer.CursorCol = 0;
                break;
            case 's': // SCP - Save Cursor Position
                _decSavedRow = Buffer.CursorRow;
                _decSavedCol = Buffer.CursorCol;
                break;
            case 'u': // RCP - Restore Cursor Position
                Buffer.CursorRow = Math.Min(_decSavedRow, Buffer.Rows - 1);
                Buffer.CursorCol = Math.Min(_decSavedCol, Buffer.Columns - 1);
                break;
            case 'h': // SM - Set Mode (ignored)
            case 'l': // RM - Reset Mode (ignored)
                break;
            case 'n': // DSR - Device Status Report (ignored)
                break;
        }
    }

    private void EraseInDisplay(int mode)
    {
        var fill = MakeEraseCell();
        switch (mode)
        {
            case 0: // cursor to end
                EraseCells(Buffer.CursorRow, Buffer.CursorCol, Buffer.Rows - 1, Buffer.Columns - 1, fill);
                break;
            case 1: // start to cursor
                EraseCells(0, 0, Buffer.CursorRow, Buffer.CursorCol, fill);
                break;
            case 2: // entire screen
                // When a full-screen clear arrives inside synchronized output —
                // or while Claude is doing in-place TUI redraws (ClaudeRedrawSuppression) —
                // a TUI app is redrawing its frame. Suppress scrollback to prevent
                // overflow rows from accumulating as duplicates in history.
                if (!AlternateScreen && (SynchronizedOutput || ClaudeRedrawSuppression))
                {
                    if (ClaudeRedrawSuppression)
                        Buffer.ClearDeferredScrollback();

                    if (ClaudeRedrawSuppression && !SynchronizedOutput
                        && (_resizeGrace || _skipNextCursorHomeSuppress))
                    {
                        if (!_resizeGrace)
                            _skipNextCursorHomeSuppress = false;
                    }
                    else
                    {
                        _syncRedrawSuppressScrollback = true;
                        Buffer.SuppressScrollback = true;
                    }
                }
                EraseCells(0, 0, Buffer.Rows - 1, Buffer.Columns - 1, fill);
                break;
            case 3: // erase saved lines (scrollback)
                // Only honor outside synchronized output: inside a DEC 2026 sync
                // block the TUI is redrawing a frame, and tools like Claude Code
                // emit ESC[3J every tick — wiping user scrollback in that case
                // breaks scroll-back. Outside sync (e.g. a shell `clear`), drop
                // saved lines per xterm semantics.
                if (!SynchronizedOutput)
                    Buffer.ClearScrollback();
                break;
        }
    }

    private void EraseInLine(int mode)
    {
        var fill = MakeEraseCell();
        switch (mode)
        {
            case 0: // cursor to end of line
                EraseCells(Buffer.CursorRow, Buffer.CursorCol, Buffer.CursorRow, Buffer.Columns - 1, fill);
                break;
            case 1: // start of line to cursor
                EraseCells(Buffer.CursorRow, 0, Buffer.CursorRow, Buffer.CursorCol, fill);
                break;
            case 2: // entire line
                EraseCells(Buffer.CursorRow, 0, Buffer.CursorRow, Buffer.Columns - 1, fill);
                break;
        }
    }

    private void EraseCells(int startRow, int startCol, int endRow, int endCol, CellData fillCell)
    {
        for (int r = startRow; r <= endRow && r < Buffer.Rows; r++)
        {
            int cs = r == startRow ? startCol : 0;
            int ce = r == endRow ? endCol : Buffer.Columns - 1;
            for (int c = cs; c <= ce && c < Buffer.Columns; c++)
                Buffer.SetCell(r, c, fillCell);
        }
    }

    private void EraseCharacters(int count)
    {
        var fill = MakeEraseCell();
        int row = Buffer.CursorRow;
        int col = Buffer.CursorCol;
        for (int i = 0; i < count && col + i < Buffer.Columns; i++)
            Buffer.SetCell(row, col + i, fill);
    }

    private void InsertLines(int count)
    {
        var fill = MakeEraseCell();
        int row = Buffer.CursorRow;
        int bottom = Buffer.ScrollBottom;
        for (int n = 0; n < count && row <= bottom; n++)
        {
            // Shift lines down from bottom within scroll region
            for (int r = bottom; r > row; r--)
                for (int c = 0; c < Buffer.Columns; c++)
                    Buffer.SetCell(r, c, Buffer.GetCell(r - 1, c));
            for (int c = 0; c < Buffer.Columns; c++)
                Buffer.SetCell(row, c, fill);
        }
    }

    private void DeleteLines(int count)
    {
        var fill = MakeEraseCell();
        int row = Buffer.CursorRow;
        int bottom = Buffer.ScrollBottom;
        for (int n = 0; n < count; n++)
        {
            for (int r = row; r < bottom; r++)
                for (int c = 0; c < Buffer.Columns; c++)
                    Buffer.SetCell(r, c, Buffer.GetCell(r + 1, c));
            for (int c = 0; c < Buffer.Columns; c++)
                Buffer.SetCell(bottom, c, fill);
        }
    }

    private void DeleteCharacters(int count)
    {
        var fill = MakeEraseCell();
        int row = Buffer.CursorRow;
        int col = Buffer.CursorCol;
        for (int i = col; i < Buffer.Columns; i++)
        {
            int src = i + count;
            Buffer.SetCell(row, i, src < Buffer.Columns ? Buffer.GetCell(row, src) : fill);
        }
    }

    private void InsertCharacters(int count)
    {
        var fill = MakeEraseCell();
        int row = Buffer.CursorRow;
        int col = Buffer.CursorCol;
        for (int i = Buffer.Columns - 1; i >= col + count; i--)
            Buffer.SetCell(row, i, Buffer.GetCell(row, i - count));
        for (int i = col; i < col + count && i < Buffer.Columns; i++)
            Buffer.SetCell(row, i, fill);
    }
}
