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
    private bool[]? _savedWrapped;
    private int _savedCursorRow, _savedCursorCol;
    private int _savedScrollOffset;

    // Current SGR attributes
    private byte _fgR = CellData.DefaultFgR, _fgG = CellData.DefaultFgG, _fgB = CellData.DefaultFgB;
    private byte _bgR = CellData.DefaultBgR, _bgG = CellData.DefaultBgG, _bgB = CellData.DefaultBgB;
    private bool _bold, _italic, _underline;
    private bool _reverse, _dim, _strikethrough;

    // Last printed character for REP (CSI b)
    private char _lastPrintedChar;

    // Suppresses scrollback during TUI redraws (sync output + ED 2)
    private bool _syncRedrawSuppressScrollback;

    // Standard 16-color ANSI palette (shared by HandleSgr and Color256)
    private static readonly (byte R, byte G, byte B)[] Ansi16Colors =
    [
        (0, 0, 0),         // 0  Black
        (205, 49, 49),     // 1  Red
        (13, 188, 121),    // 2  Green
        (229, 229, 16),    // 3  Yellow
        (36, 114, 200),    // 4  Blue
        (188, 63, 188),    // 5  Magenta
        (17, 168, 205),    // 6  Cyan
        (204, 204, 204),   // 7  White
        (118, 118, 118),   // 8  Bright Black
        (241, 76, 76),     // 9  Bright Red
        (35, 209, 139),    // 10 Bright Green
        (245, 245, 67),    // 11 Bright Yellow
        (59, 142, 234),    // 12 Bright Blue
        (214, 112, 214),   // 13 Bright Magenta
        (41, 184, 219),    // 14 Bright Cyan
        (229, 229, 229),   // 15 Bright White
    ];

    // DECSC/DECRC saved cursor state
    private int _decSavedRow, _decSavedCol;
    private byte _decSavedFgR = CellData.DefaultFgR, _decSavedFgG = CellData.DefaultFgG, _decSavedFgB = CellData.DefaultFgB;
    private byte _decSavedBgR = CellData.DefaultBgR, _decSavedBgG = CellData.DefaultBgG, _decSavedBgB = CellData.DefaultBgB;
    private bool _decSavedBold, _decSavedItalic, _decSavedUnderline;
    private bool _decSavedReverse, _decSavedDim, _decSavedStrikethrough;

    // DEC private mode state
    public bool CursorEnabled { get; private set; } = true;
    public bool ApplicationCursorKeys { get; private set; }
    public bool AutoWrap { get; private set; } = true;
    public bool AlternateScreen { get; private set; }
    public bool BracketedPasteMode { get; private set; }

    /// <summary>
    /// DEC mode 2026: synchronized output. When true, the application is in the
    /// middle of a screen update and the host should defer rendering until reset.
    /// </summary>
    public bool SynchronizedOutput { get; private set; }

    /// <summary>
    /// When true, logs all CSI/ESC operations to the event system for debugging.
    /// </summary>
    public bool AnsiLogging { get; set; }

    /// <summary>
    /// When set, printed characters and newlines are written to the transcript log.
    /// </summary>
    public SessionTranscriptLogger? TranscriptLogger { get; set; }

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
    /// Erases from the current cursor position to end-of-screen (equivalent to ED 0).
    /// Used to clean up visual artifacts left by TUI applications that exit without
    /// proper cleanup (e.g., ConPTY may not relay all erase sequences from inline-
    /// rendering TUI frameworks like ink).
    /// </summary>
    public void EraseBelow()
    {
        var fill = new CellData(' ', CellData.DefaultFgR, CellData.DefaultFgG, CellData.DefaultFgB,
                                CellData.DefaultBgR, CellData.DefaultBgG, CellData.DefaultBgB);
        EraseCells(Buffer.CursorRow, Buffer.CursorCol, Buffer.Rows - 1, Buffer.Columns - 1, fill);
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

    private void LogAnsi(string message)
    {
        if (AnsiLogging)
            _events?.Log(this, message, category: "ANSI");
    }

    private void OnPrint(char c)
    {
        if (Buffer.CursorCol >= Buffer.Columns)
        {
            if (AutoWrap)
            {
                Buffer.CursorCol = 0;
                Buffer.LineFeed();
                Buffer.SetLineWrapped(Buffer.CursorRow, true);
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
        TranscriptLogger?.WriteText(c);
    }

    private void OnExecute(byte b)
    {
        switch (b)
        {
            case 0x0A: // LF
            case 0x0B: // VT
            case 0x0C: // FF
                LogAnsi($"LF cursor=({Buffer.CursorRow},{Buffer.CursorCol}) scroll={Buffer.ScrollTop}-{Buffer.ScrollBottom}");
                Buffer.LineFeed();
                TranscriptLogger?.WriteTextNewline();
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
                LogAnsi($"RI cursor=({Buffer.CursorRow},{Buffer.CursorCol}) scroll={Buffer.ScrollTop}-{Buffer.ScrollBottom}");
                if (Buffer.CursorRow == Buffer.ScrollTop)
                    Buffer.ScrollDownRegion(Buffer.ScrollTop, Buffer.ScrollBottom, MakeEraseCell());
                else if (Buffer.CursorRow > 0)
                    Buffer.CursorRow--;
                break;
            case 'D': // IND — Index (cursor down, scroll up if at bottom)
                LogAnsi($"IND cursor=({Buffer.CursorRow},{Buffer.CursorCol}) scroll={Buffer.ScrollTop}-{Buffer.ScrollBottom}");
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
                    case 47:   // Alternate Screen Buffer (switch only, no cursor save/clear)
                    case 1047: // Alternate Screen Buffer (switch + clear on enter)
                        _events?.Log(this, $"DECSET {mode} AlternateScreen={set}", category: "Terminal");
                        if (set && !AlternateScreen)
                        {
                            _savedScreen = new CellData[Buffer.Rows, Buffer.Columns];
                            _savedWrapped = new bool[Buffer.Rows];
                            for (int r = 0; r < Buffer.Rows; r++)
                            {
                                _savedWrapped[r] = Buffer.IsScreenLineWrapped(r);
                                for (int c = 0; c < Buffer.Columns; c++)
                                    _savedScreen[r, c] = Buffer.GetCell(r, c);
                            }
                            _savedScrollOffset = Buffer.ScrollOffset;
                            if (mode == 1047) Buffer.Clear();
                            Buffer.SuppressScrollback = true;
                            AlternateScreen = true;
                        }
                        else if (!set && AlternateScreen)
                        {
                            if (_savedScreen != null)
                            {
                                int rows = Math.Min(Buffer.Rows, _savedScreen.GetLength(0));
                                int cols = Math.Min(Buffer.Columns, _savedScreen.GetLength(1));
                                for (int r = 0; r < rows; r++)
                                {
                                    if (_savedWrapped != null && r < _savedWrapped.Length)
                                        Buffer.SetLineWrapped(r, _savedWrapped[r]);
                                    for (int c = 0; c < cols; c++)
                                        Buffer.SetCell(r, c, _savedScreen[r, c]);
                                }
                                _savedScreen = null;
                                _savedWrapped = null;
                            }
                            Buffer.ScrollOffset = _savedScrollOffset;
                            Buffer.SuppressScrollback = false;
                            AlternateScreen = false;
                        }
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
                            _savedWrapped = new bool[Buffer.Rows];
                            for (int r = 0; r < Buffer.Rows; r++)
                            {
                                _savedWrapped[r] = Buffer.IsScreenLineWrapped(r);
                                for (int c = 0; c < Buffer.Columns; c++)
                                    _savedScreen[r, c] = Buffer.GetCell(r, c);
                            }
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
                                {
                                    if (_savedWrapped != null && r < _savedWrapped.Length)
                                        Buffer.SetLineWrapped(r, _savedWrapped[r]);
                                    for (int c = 0; c < cols; c++)
                                        Buffer.SetCell(r, c, _savedScreen[r, c]);
                                }
                                Buffer.CursorRow = Math.Min(_savedCursorRow, Buffer.Rows - 1);
                                Buffer.CursorCol = Math.Min(_savedCursorCol, Buffer.Columns - 1);
                                _savedScreen = null;
                                _savedWrapped = null;
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
                    case 2026: // Synchronized Output (DEC mode 2026)
                        SynchronizedOutput = set;
                        if (!set && _syncRedrawSuppressScrollback)
                        {
                            // End of a sync block that contained a full-screen clear:
                            // restore normal scrollback behaviour.
                            _syncRedrawSuppressScrollback = false;
                            if (!AlternateScreen)
                                Buffer.SuppressScrollback = false;
                        }
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
                LogAnsi($"CUP ({Math.Clamp(Math.Max(1, p0) - 1, 0, Buffer.Rows - 1)},{Math.Clamp(Math.Max(1, p1) - 1, 0, Buffer.Columns - 1)})");
                Buffer.CursorRow = Math.Clamp(Math.Max(1, p0) - 1, 0, Buffer.Rows - 1);
                Buffer.CursorCol = Math.Clamp(Math.Max(1, p1) - 1, 0, Buffer.Columns - 1);
                break;
            case 'J': // ED - Erase in Display
                LogAnsi($"ED {p0} cursor=({Buffer.CursorRow},{Buffer.CursorCol}) scroll={Buffer.ScrollTop}-{Buffer.ScrollBottom}");
                EraseInDisplay(p0);
                break;
            case 'K': // EL - Erase in Line
                LogAnsi($"EL {p0} cursor=({Buffer.CursorRow},{Buffer.CursorCol})");
                EraseInLine(p0);
                break;
            case 'L': // IL - Insert Lines
                LogAnsi($"IL {Math.Max(1, p0)} cursor=({Buffer.CursorRow},{Buffer.CursorCol}) scroll={Buffer.ScrollTop}-{Buffer.ScrollBottom}");
                InsertLines(Math.Max(1, p0));
                break;
            case 'M': // DL - Delete Lines
                LogAnsi($"DL {Math.Max(1, p0)} cursor=({Buffer.CursorRow},{Buffer.CursorCol}) scroll={Buffer.ScrollTop}-{Buffer.ScrollBottom}");
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
                    LogAnsi($"SU {n} scroll={Buffer.ScrollTop}-{Buffer.ScrollBottom}");
                    var fill = MakeEraseCell();
                    for (int i = 0; i < n; i++)
                        Buffer.ScrollUpRegion(Buffer.ScrollTop, Buffer.ScrollBottom, fill);
                }
                break;
            case 'T': // SD - Scroll Down (only when no private marker)
                if (privateMarker == 0)
                {
                    int n = Math.Max(1, p0);
                    LogAnsi($"SD {n} scroll={Buffer.ScrollTop}-{Buffer.ScrollBottom}");
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
                    LogAnsi($"DECSTBM reset to 0-{Buffer.Rows - 1}");
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
                    LogAnsi($"DECSTBM {top}-{bottom}");
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
                // When a full-screen clear arrives inside synchronized output,
                // a TUI app is redrawing its frame. Suppress scrollback to
                // prevent duplicate content from accumulating.
                if (SynchronizedOutput && !AlternateScreen)
                {
                    _syncRedrawSuppressScrollback = true;
                    Buffer.SuppressScrollback = true;
                }
                EraseCells(0, 0, Buffer.Rows - 1, Buffer.Columns - 1, fill);
                break;
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
                case 30: case 31: case 32: case 33: case 34: case 35: case 36:
                    (_fgR, _fgG, _fgB) = Ansi16Colors[p - 30]; break;
                case 37: (_fgR, _fgG, _fgB) = (CellData.DefaultFgR, CellData.DefaultFgG, CellData.DefaultFgB); break;
                case 39: (_fgR, _fgG, _fgB) = (CellData.DefaultFgR, CellData.DefaultFgG, CellData.DefaultFgB); break; // default fg

                // Bright foreground colors
                case 90: case 91: case 92: case 93: case 94: case 95: case 96: case 97:
                    (_fgR, _fgG, _fgB) = Ansi16Colors[p - 82]; break;

                // Standard background colors
                case 40: case 41: case 42: case 43: case 44: case 45: case 46: case 47:
                    (_bgR, _bgG, _bgB) = Ansi16Colors[p - 40]; break;
                case 49: (_bgR, _bgG, _bgB) = (CellData.DefaultBgR, CellData.DefaultBgG, CellData.DefaultBgB); break; // default bg

                // Bright background colors
                case 100: case 101: case 102: case 103: case 104: case 105: case 106: case 107:
                    (_bgR, _bgG, _bgB) = Ansi16Colors[p - 92]; break;

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
        _fgR = CellData.DefaultFgR; _fgG = CellData.DefaultFgG; _fgB = CellData.DefaultFgB;
        _bgR = CellData.DefaultBgR; _bgG = CellData.DefaultBgG; _bgB = CellData.DefaultBgB;
        _bold = false; _italic = false; _underline = false;
        _reverse = false; _dim = false; _strikethrough = false;
    }

    private static (byte R, byte G, byte B) Color256(int index)
    {
        if (index < 16)
            return Ansi16Colors[index];
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
