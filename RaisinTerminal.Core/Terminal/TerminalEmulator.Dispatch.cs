using Raisin.EventSystem;
using RaisinTerminal.Core.Models;

namespace RaisinTerminal.Core.Terminal;

public partial class TerminalEmulator
{
    // Saved main screen buffer for alternate screen switching
    private CellData[,]? _savedScreen;
    private bool[]? _savedWrapped;
    private int _savedCursorRow, _savedCursorCol;

    // DECSC/DECRC saved cursor state
    private int _decSavedRow, _decSavedCol;
    private byte _decSavedFgR = CellData.DefaultFgR, _decSavedFgG = CellData.DefaultFgG, _decSavedFgB = CellData.DefaultFgB;
    private byte _decSavedBgR = CellData.DefaultBgR, _decSavedBgG = CellData.DefaultBgG, _decSavedBgB = CellData.DefaultBgB;
    private bool _decSavedBold, _decSavedItalic, _decSavedUnderline;
    private bool _decSavedReverse, _decSavedDim, _decSavedStrikethrough;

    // Last printed character for REP (CSI b)
    private char _lastPrintedChar;

    private void SaveMainScreen()
    {
        _savedScreen = new CellData[Buffer.Rows, Buffer.Columns];
        _savedWrapped = new bool[Buffer.Rows];
        for (int r = 0; r < Buffer.Rows; r++)
        {
            _savedWrapped[r] = Buffer.IsScreenLineWrapped(r);
            for (int c = 0; c < Buffer.Columns; c++)
                _savedScreen[r, c] = Buffer.GetCell(r, c);
        }
    }

    private void RestoreMainScreen(bool restoreCursor)
    {
        if (_savedScreen == null) return;

        int rows = Math.Min(Buffer.Rows, _savedScreen.GetLength(0));
        int cols = Math.Min(Buffer.Columns, _savedScreen.GetLength(1));
        for (int r = 0; r < rows; r++)
        {
            if (_savedWrapped != null && r < _savedWrapped.Length)
                Buffer.SetLineWrapped(r, _savedWrapped[r]);
            for (int c = 0; c < cols; c++)
                Buffer.SetCell(r, c, _savedScreen[r, c]);
        }
        if (restoreCursor)
        {
            Buffer.CursorRow = Math.Min(_savedCursorRow, Buffer.Rows - 1);
            Buffer.CursorCol = Math.Min(_savedCursorCol, Buffer.Columns - 1);
        }
        _savedScreen = null;
        _savedWrapped = null;
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
}
