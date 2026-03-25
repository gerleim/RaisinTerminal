using Raisin.EventSystem;
using RaisinTerminal.Core.Models;

namespace RaisinTerminal.Core.Terminal;

/// <summary>
/// Wires AnsiParser → TerminalBuffer: interprets ANSI/VT escape sequences
/// and translates them into buffer operations (cursor moves, erases, color changes).
/// </summary>
public class TerminalEmulator
{
    private readonly AnsiParser _parser = new();
    private readonly EventSystem? _events;

    public TerminalBuffer Buffer { get; private set; }

    // Saved main screen buffer for alternate screen switching
    private CellData[,]? _savedScreen;
    private int _savedCursorRow, _savedCursorCol;
    private int _savedScrollOffset;

    // Current SGR attributes
    private byte _fgR = 204, _fgG = 204, _fgB = 204;
    private byte _bgR = 33, _bgG = 33, _bgB = 33;
    private bool _bold, _italic, _underline;
    private bool _reverse, _dim, _strikethrough;

    // Last printed character for REP (CSI b)
    private char _lastPrintedChar;

    // DECSC/DECRC saved cursor state
    private int _decSavedRow, _decSavedCol;
    private byte _decSavedFgR = 204, _decSavedFgG = 204, _decSavedFgB = 204;
    private byte _decSavedBgR = 33, _decSavedBgG = 33, _decSavedBgB = 33;
    private bool _decSavedBold, _decSavedItalic, _decSavedUnderline;
    private bool _decSavedReverse, _decSavedDim, _decSavedStrikethrough;

    // DEC private mode state
    public bool CursorEnabled { get; private set; } = true;
    public bool ApplicationCursorKeys { get; private set; }
    public bool AutoWrap { get; private set; } = true;
    public bool AlternateScreen { get; private set; }
    public bool BracketedPasteMode { get; private set; }

    /// <summary>Raised after a chunk of data has been processed and the buffer is dirty.</summary>
    public event Action? BufferChanged;

    /// <summary>Raised when an OSC sequence sets the window title (OSC 0 or OSC 2).</summary>
    public event Action<string>? TitleChanged;

    /// <summary>Raised when an OSC 7 or OSC 9;9 sequence reports the current working directory.</summary>
    public event Action<string>? WorkingDirectoryChanged;

    public TerminalEmulator(int cols, int rows, EventSystem? events = null)
    {
        _events = events;
        Buffer = new TerminalBuffer(cols, rows);
        _parser.Print += OnPrint;
        _parser.Execute += OnExecute;
        _parser.EscDispatch += OnEscDispatch;
        _parser.CsiDispatch += OnCsiDispatch;
        _parser.OscDispatch += OnOscDispatch;
    }

    public void Feed(ReadOnlySpan<byte> data)
    {
        _parser.Feed(data);
        BufferChanged?.Invoke();
    }

    public void Resize(int cols, int rows)
    {
        Buffer.Resize(cols, rows);
    }

    /// <summary>
    /// Clears the pending auto-wrap state. Per xterm spec, cursor movement commands
    /// cancel any deferred wrap so the next printed character doesn't trigger an
    /// unwanted line feed.
    /// </summary>
    private void ClearPendingWrap()
    {
        if (Buffer.CursorCol >= Buffer.Columns)
            Buffer.CursorCol = Buffer.Columns - 1;
    }

    /// <summary>
    /// Creates a cell for erase operations — uses current SGR background but no text attributes.
    /// </summary>
    private CellData MakeEraseCell()
    {
        return new CellData(' ', _fgR, _fgG, _fgB, _bgR, _bgG, _bgB);
    }

    private void OnPrint(char c)
    {
        if (Buffer.CursorCol >= Buffer.Columns)
        {
            if (AutoWrap)
            {
                Buffer.CursorCol = 0;
                Buffer.LineFeed();
            }
            else
            {
                // No auto-wrap: overwrite last column
                Buffer.CursorCol = Buffer.Columns - 1;
            }
        }
        Buffer.SetCell(Buffer.CursorRow, Buffer.CursorCol,
            new CellData(c, _fgR, _fgG, _fgB, _bgR, _bgG, _bgB, _bold, _italic, _underline, _reverse, _dim, _strikethrough));
        Buffer.CursorCol++;
        _lastPrintedChar = c;
    }

    private void OnExecute(byte b)
    {
        switch (b)
        {
            case 0x0A: // LF
            case 0x0B: // VT
            case 0x0C: // FF
                Buffer.LineFeed();
                break;
            case 0x0D: // CR
                Buffer.CarriageReturn();
                break;
            case 0x08: // BS
                Buffer.Backspace();
                break;
            case 0x09: // HT (tab)
                int nextTab = (Buffer.CursorCol / 8 + 1) * 8;
                Buffer.CursorCol = Math.Min(nextTab, Buffer.Columns - 1);
                break;
            case 0x07: // BEL
                break;
        }
    }

    private void OnEscDispatch(char final)
    {
        switch (final)
        {
            case '7': // DECSC — Save Cursor position and attributes
                _decSavedRow = Buffer.CursorRow;
                _decSavedCol = Buffer.CursorCol;
                _decSavedFgR = _fgR; _decSavedFgG = _fgG; _decSavedFgB = _fgB;
                _decSavedBgR = _bgR; _decSavedBgG = _bgG; _decSavedBgB = _bgB;
                _decSavedBold = _bold; _decSavedItalic = _italic; _decSavedUnderline = _underline;
                _decSavedReverse = _reverse; _decSavedDim = _dim; _decSavedStrikethrough = _strikethrough;
                break;
            case '8': // DECRC — Restore Cursor position and attributes
                Buffer.CursorRow = Math.Min(_decSavedRow, Buffer.Rows - 1);
                Buffer.CursorCol = Math.Min(_decSavedCol, Buffer.Columns - 1);
                _fgR = _decSavedFgR; _fgG = _decSavedFgG; _fgB = _decSavedFgB;
                _bgR = _decSavedBgR; _bgG = _decSavedBgG; _bgB = _decSavedBgB;
                _bold = _decSavedBold; _italic = _decSavedItalic; _underline = _decSavedUnderline;
                _reverse = _decSavedReverse; _dim = _decSavedDim; _strikethrough = _decSavedStrikethrough;
                break;
            case 'M': // RI — Reverse Index (cursor up, scroll down if at top of scroll region)
                if (Buffer.CursorRow == Buffer.ScrollTop)
                    Buffer.ScrollDownRegion(Buffer.ScrollTop, Buffer.ScrollBottom, MakeEraseCell());
                else if (Buffer.CursorRow > 0)
                    Buffer.CursorRow--;
                break;
            case 'D': // IND — Index (cursor down, scroll up if at bottom)
                Buffer.LineFeed();
                break;
            case 'E': // NEL — Next Line (CR + LF)
                Buffer.CarriageReturn();
                Buffer.LineFeed();
                break;
            case 'c': // RIS — Full Reset
                Buffer.Clear();
                Buffer.ScrollTop = 0;
                Buffer.ScrollBottom = Buffer.Rows - 1;
                ResetSgr();
                BracketedPasteMode = false;
                break;
        }
    }

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
                    case 1049: // Alternate Screen Buffer (save cursor + switch + clear)
                        _events?.Log(this, $"DECSET 1049 AlternateScreen={set}", category: "Terminal");
                        if (set)
                        {
                            // Save main screen, cursor, and scroll state
                            _savedCursorRow = Buffer.CursorRow;
                            _savedCursorCol = Buffer.CursorCol;
                            _savedScrollOffset = Buffer.ScrollOffset;
                            _savedScreen = new CellData[Buffer.Rows, Buffer.Columns];
                            for (int r = 0; r < Buffer.Rows; r++)
                                for (int c = 0; c < Buffer.Columns; c++)
                                    _savedScreen[r, c] = Buffer.GetCell(r, c);
                            Buffer.Clear();
                            Buffer.SuppressScrollback = true;
                            AlternateScreen = true;
                        }
                        else
                        {
                            // Restore main screen, cursor, and scroll state
                            if (_savedScreen != null)
                            {
                                int rows = Math.Min(Buffer.Rows, _savedScreen.GetLength(0));
                                int cols = Math.Min(Buffer.Columns, _savedScreen.GetLength(1));
                                for (int r = 0; r < rows; r++)
                                    for (int c = 0; c < cols; c++)
                                        Buffer.SetCell(r, c, _savedScreen[r, c]);
                                Buffer.CursorRow = Math.Min(_savedCursorRow, Buffer.Rows - 1);
                                Buffer.CursorCol = Math.Min(_savedCursorCol, Buffer.Columns - 1);
                                _savedScreen = null;
                            }
                            Buffer.ScrollOffset = _savedScrollOffset;
                            Buffer.SuppressScrollback = false;
                            AlternateScreen = false;
                        }
                        break;
                    case 2004: // Bracketed Paste Mode
                        _events?.Log(this, $"DECSET 2004 BracketedPaste={set}", category: "Terminal");
                        BracketedPasteMode = set;
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
                Buffer.CursorRow = Math.Clamp(Math.Max(1, p0) - 1, 0, Buffer.Rows - 1);
                Buffer.CursorCol = Math.Clamp(Math.Max(1, p1) - 1, 0, Buffer.Columns - 1);
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

    private void OnOscDispatch(string data)
    {
        var semi = data.IndexOf(';');
        if (semi < 0) return;
        var cmd = data[..semi];
        var payload = data[(semi + 1)..];

        switch (cmd)
        {
            case "0" or "2":
                // OSC 0;title ST  or  OSC 2;title ST — window title
                _events?.Log(this, $"OSC {cmd} Title=\"{payload}\"", category: "Terminal");
                TitleChanged?.Invoke(payload);
                break;
            case "7":
                // OSC 7;file:///host/path ST — current working directory (used by bash, zsh, PowerShell)
                if (payload.StartsWith("file:///", StringComparison.OrdinalIgnoreCase))
                {
                    // Strip "file:///" prefix; URI path uses forward slashes
                    var path = Uri.UnescapeDataString(payload[8..]);
                    // On Windows, file:///C:/foo → "C:/foo"; normalize to backslashes
                    path = path.Replace('/', '\\');
                    _events?.Log(this, $"OSC 7 CWD=\"{path}\"", category: "Terminal");
                    WorkingDirectoryChanged?.Invoke(path);
                }
                break;
            case "9":
                // OSC 9;9;path ST — ConEmu/Windows Terminal CWD convention
                if (payload.StartsWith("9;", StringComparison.Ordinal))
                {
                    var path = payload[2..];
                    if (path.Length > 0)
                        WorkingDirectoryChanged?.Invoke(path);
                }
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
            case 3:
                EraseCells(0, 0, Buffer.Rows - 1, Buffer.Columns - 1, fill);
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

    private void HandleSgr(int[] pars)
    {
        if (pars.Length == 0 || (pars.Length == 1 && pars[0] == 0))
        {
            ResetSgr();
            return;
        }

        for (int i = 0; i < pars.Length; i++)
        {
            int p = pars[i];
            switch (p)
            {
                case 0: ResetSgr(); break;
                case 1: _bold = true; break;
                case 2: _dim = true; break;
                case 3: _italic = true; break;
                case 4: _underline = true; break;
                case 7: _reverse = true; break;
                case 9: _strikethrough = true; break;
                case 22: _bold = false; _dim = false; break;
                case 23: _italic = false; break;
                case 24: _underline = false; break;
                case 27: _reverse = false; break;
                case 29: _strikethrough = false; break;

                // Standard foreground colors
                case 30: (_fgR, _fgG, _fgB) = (0, 0, 0); break;
                case 31: (_fgR, _fgG, _fgB) = (205, 49, 49); break;
                case 32: (_fgR, _fgG, _fgB) = (13, 188, 121); break;
                case 33: (_fgR, _fgG, _fgB) = (229, 229, 16); break;
                case 34: (_fgR, _fgG, _fgB) = (36, 114, 200); break;
                case 35: (_fgR, _fgG, _fgB) = (188, 63, 188); break;
                case 36: (_fgR, _fgG, _fgB) = (17, 168, 205); break;
                case 37: (_fgR, _fgG, _fgB) = (204, 204, 204); break;
                case 39: (_fgR, _fgG, _fgB) = (204, 204, 204); break; // default fg

                // Bright foreground colors
                case 90: (_fgR, _fgG, _fgB) = (118, 118, 118); break;
                case 91: (_fgR, _fgG, _fgB) = (241, 76, 76); break;
                case 92: (_fgR, _fgG, _fgB) = (35, 209, 139); break;
                case 93: (_fgR, _fgG, _fgB) = (245, 245, 67); break;
                case 94: (_fgR, _fgG, _fgB) = (59, 142, 234); break;
                case 95: (_fgR, _fgG, _fgB) = (214, 112, 214); break;
                case 96: (_fgR, _fgG, _fgB) = (41, 184, 219); break;
                case 97: (_fgR, _fgG, _fgB) = (229, 229, 229); break;

                // Standard background colors
                case 40: (_bgR, _bgG, _bgB) = (0, 0, 0); break;
                case 41: (_bgR, _bgG, _bgB) = (205, 49, 49); break;
                case 42: (_bgR, _bgG, _bgB) = (13, 188, 121); break;
                case 43: (_bgR, _bgG, _bgB) = (229, 229, 16); break;
                case 44: (_bgR, _bgG, _bgB) = (36, 114, 200); break;
                case 45: (_bgR, _bgG, _bgB) = (188, 63, 188); break;
                case 46: (_bgR, _bgG, _bgB) = (17, 168, 205); break;
                case 47: (_bgR, _bgG, _bgB) = (204, 204, 204); break;
                case 49: (_bgR, _bgG, _bgB) = (33, 33, 33); break; // default bg

                // Bright background colors
                case 100: (_bgR, _bgG, _bgB) = (118, 118, 118); break;
                case 101: (_bgR, _bgG, _bgB) = (241, 76, 76); break;
                case 102: (_bgR, _bgG, _bgB) = (35, 209, 139); break;
                case 103: (_bgR, _bgG, _bgB) = (245, 245, 67); break;
                case 104: (_bgR, _bgG, _bgB) = (59, 142, 234); break;
                case 105: (_bgR, _bgG, _bgB) = (214, 112, 214); break;
                case 106: (_bgR, _bgG, _bgB) = (41, 184, 219); break;
                case 107: (_bgR, _bgG, _bgB) = (229, 229, 229); break;

                // 256-color and true-color
                case 38: // foreground
                    if (i + 1 < pars.Length && pars[i + 1] == 5 && i + 2 < pars.Length)
                    {
                        var (r, g, b) = Color256(pars[i + 2]);
                        (_fgR, _fgG, _fgB) = (r, g, b);
                        i += 2;
                    }
                    else if (i + 1 < pars.Length && pars[i + 1] == 2 && i + 4 < pars.Length)
                    {
                        (_fgR, _fgG, _fgB) = ((byte)pars[i + 2], (byte)pars[i + 3], (byte)pars[i + 4]);
                        i += 4;
                    }
                    break;
                case 48: // background
                    if (i + 1 < pars.Length && pars[i + 1] == 5 && i + 2 < pars.Length)
                    {
                        var (r, g, b) = Color256(pars[i + 2]);
                        (_bgR, _bgG, _bgB) = (r, g, b);
                        i += 2;
                    }
                    else if (i + 1 < pars.Length && pars[i + 1] == 2 && i + 4 < pars.Length)
                    {
                        (_bgR, _bgG, _bgB) = ((byte)pars[i + 2], (byte)pars[i + 3], (byte)pars[i + 4]);
                        i += 4;
                    }
                    break;
            }
        }
    }

    private void ResetSgr()
    {
        _fgR = 204; _fgG = 204; _fgB = 204;
        _bgR = 33; _bgG = 33; _bgB = 33;
        _bold = false; _italic = false; _underline = false;
        _reverse = false; _dim = false; _strikethrough = false;
    }

    private static (byte R, byte G, byte B) Color256(int index)
    {
        if (index < 16)
        {
            // Standard 16 colors
            return index switch
            {
                0 => (0, 0, 0), 1 => (205, 49, 49), 2 => (13, 188, 121), 3 => (229, 229, 16),
                4 => (36, 114, 200), 5 => (188, 63, 188), 6 => (17, 168, 205), 7 => (204, 204, 204),
                8 => (118, 118, 118), 9 => (241, 76, 76), 10 => (35, 209, 139), 11 => (245, 245, 67),
                12 => (59, 142, 234), 13 => (214, 112, 214), 14 => (41, 184, 219), 15 => (229, 229, 229),
                _ => (204, 204, 204)
            };
        }
        if (index < 232)
        {
            // 6x6x6 color cube
            int ci = index - 16;
            int b = ci % 6; ci /= 6;
            int g = ci % 6; ci /= 6;
            int r = ci;
            return ((byte)(r > 0 ? 55 + r * 40 : 0),
                    (byte)(g > 0 ? 55 + g * 40 : 0),
                    (byte)(b > 0 ? 55 + b * 40 : 0));
        }
        // Grayscale ramp
        byte v = (byte)(8 + (index - 232) * 10);
        return (v, v, v);
    }
}
