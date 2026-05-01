using System.Text;
using System.Windows.Input;
using RaisinTerminal.Core.Terminal;
using RaisinTerminal.ViewModels;

namespace RaisinTerminal.Controls;

public record struct InputLineContext(string Text, int ScreenRow, int InputStartCol, bool IsClaude);

public class InputLineEditor
{
    private TerminalSessionViewModel? _session;
    private TerminalCanvas? _canvas;

    private int? _anchorIdx;
    private int? _cursorIdx;

    public bool HasSelection => _canvas?.SelectionStart != null;

    public void Attach(TerminalSessionViewModel session, TerminalCanvas canvas)
    {
        _session = session;
        _canvas = canvas;
    }

    public void Detach()
    {
        _session = null;
        _canvas = null;
        _anchorIdx = null;
        _cursorIdx = null;
    }

    public static long ScreenRowToAbsoluteRow(TerminalBuffer buffer, int canvasRows, int screenRow)
    {
        int viewOffset = buffer.Rows - Math.Min(buffer.Rows, canvasRows);
        return buffer.TotalLinesScrolled + viewOffset + screenRow;
    }

    public InputLineContext? GetContext()
    {
        if (_session?.Emulator?.Buffer == null) return null;
        var buffer = _session.Emulator.Buffer;

        if (_session.Emulator.AlternateScreen)
        {
            if (string.IsNullOrEmpty(_session.ClaudeSessionName)) return null;
            return FindClaudeInputLine(buffer);
        }

        var text = _session.CurrentInputLine.ToString();
        if (text.Length == 0 || text.Contains('\n')) return null;

        int row = buffer.CursorRow;
        int endCol = buffer.Columns - 1;
        while (endCol >= 0 && buffer.GetCell(row, endCol).Character == ' ')
            endCol--;
        if (endCol < 0) return null;

        int inputStartCol = endCol - text.Length + 1;
        if (inputStartCol < 0) return null;

        return new InputLineContext(text, row, inputStartCol, false);
    }

    private static InputLineContext? FindClaudeInputLine(TerminalBuffer buffer)
    {
        for (int row = buffer.CursorRow; row >= Math.Max(0, buffer.CursorRow - 5); row--)
        {
            for (int col = 0; col < Math.Min(10, buffer.Columns); col++)
            {
                if (buffer.GetCell(row, col).Character != '❯') continue;

                int inputStartCol = col + 2;
                int endCol = buffer.Columns - 1;
                while (endCol >= inputStartCol && buffer.GetCell(row, endCol).Character == ' ')
                    endCol--;

                if (endCol < inputStartCol)
                    return new InputLineContext("", row, inputStartCol, true);

                var sb = new StringBuilder();
                for (int c = inputStartCol; c <= endCol; c++)
                    sb.Append(buffer.GetCell(row, c).Character);

                return new InputLineContext(sb.ToString(), row, inputStartCol, true);
            }
        }
        return null;
    }

    public bool SelectAll()
    {
        if (_canvas == null) return false;
        var ctx = GetContext();
        if (ctx == null || ctx.Value.Text.Length == 0) return _session != null;

        var buffer = _session!.Emulator!.Buffer;
        long absRow = ScreenRowToAbsoluteRow(buffer, _canvas.Rows, ctx.Value.ScreenRow);
        int endCol = ctx.Value.InputStartCol + ctx.Value.Text.Length - 1;
        _canvas.SelectionStart = (absRow, ctx.Value.InputStartCol);
        _canvas.SelectionEnd = (absRow, endCol);
        _canvas.Invalidate();
        return true;
    }

    public (int Start, int End)? GetSelectionRange(InputLineContext? ctxOverride = null)
    {
        if (_session == null || _canvas == null ||
            _canvas.SelectionStart == null || _canvas.SelectionEnd == null)
            return null;

        var buffer = _session.Emulator?.Buffer;
        if (buffer == null) return null;

        var ctx = ctxOverride ?? GetContext();
        if (ctx == null || ctx.Value.Text.Length == 0) return null;

        long absRow = ScreenRowToAbsoluteRow(buffer, _canvas.Rows, ctx.Value.ScreenRow);
        int inputStartCol = ctx.Value.InputStartCol;
        int endCol = inputStartCol + ctx.Value.Text.Length - 1;

        var s = _canvas.SelectionStart.Value;
        var e = _canvas.SelectionEnd.Value;
        if (s.Row > e.Row || (s.Row == e.Row && s.Col > e.Col))
            (s, e) = (e, s);

        if (s.Row != absRow || e.Row != absRow)
            return null;

        int selStart = Math.Max(s.Col, inputStartCol);
        int selEnd = Math.Min(e.Col, endCol);
        if (selStart > selEnd)
            return null;

        return (selStart - inputStartCol, selEnd - inputStartCol);
    }

    public bool DeleteSelection(string? replacementText = null)
    {
        var ctx = GetContext();
        var range = GetSelectionRange(ctx);
        if (range == null || ctx == null) return false;

        var (startIdx, endIdx) = range.Value;
        var currentText = ctx.Value.Text;
        var targetText = currentText.Substring(0, startIdx)
                       + (replacementText ?? "")
                       + currentText.Substring(endIdx + 1);

        if (ctx.Value.IsClaude)
        {
            ReplaceClaudeInputLine(targetText);
        }
        else
        {
            var buffer = _session!.Emulator!.Buffer;
            int inputStartCol = ctx.Value.InputStartCol;
            int selRightCol = inputStartCol + endIdx + 1;

            int cursorCol = _cursorIdx != null
                ? inputStartCol + _cursorIdx.Value
                : buffer.CursorCol;
            int delta = selRightCol - cursorCol;
            var arrowKey = delta > 0
                ? InputEncoder.EncodeKey(ConsoleKey.RightArrow)
                : InputEncoder.EncodeKey(ConsoleKey.LeftArrow);
            int arrowCount = Math.Abs(delta);

            int charsToDelete = endIdx - startIdx + 1;
            var backspaces = new byte[charsToDelete];
            Array.Fill(backspaces, (byte)0x7F);
            var replacement = replacementText != null
                ? InputEncoder.EncodeText(replacementText)
                : Array.Empty<byte>();

            var combined = new byte[arrowKey.Length * arrowCount + backspaces.Length + replacement.Length];
            int offset = 0;
            for (int i = 0; i < arrowCount; i++)
            {
                Buffer.BlockCopy(arrowKey, 0, combined, offset, arrowKey.Length);
                offset += arrowKey.Length;
            }
            Buffer.BlockCopy(backspaces, 0, combined, offset, backspaces.Length);
            offset += backspaces.Length;
            Buffer.BlockCopy(replacement, 0, combined, offset, replacement.Length);

            if (combined.Length > 0)
                _session.WriteUserInput(combined);

            _session.CurrentInputLine.Clear();
            _session.CurrentInputLine.Append(targetText);
            _session.InputUndo.Record(targetText, replacementText != null ? "replace" : "delete");
        }

        ClearSelection();
        return true;
    }

    public void ReplaceInputLine(string targetText)
    {
        if (_session == null) return;
        var currentText = _session.CurrentInputLine.ToString();

        int commonLen = 0;
        int minLen = Math.Min(currentText.Length, targetText.Length);
        while (commonLen < minLen && currentText[commonLen] == targetText[commonLen])
            commonLen++;

        int charsToDelete = currentText.Length - commonLen;
        string charsToType = targetText.Substring(commonLen);

        var endKey = InputEncoder.EncodeKey(ConsoleKey.End);
        var backspaces = new byte[charsToDelete];
        Array.Fill(backspaces, (byte)0x7F);
        var replacement = InputEncoder.EncodeText(charsToType);

        var combined = new byte[endKey.Length + backspaces.Length + replacement.Length];
        Buffer.BlockCopy(endKey, 0, combined, 0, endKey.Length);
        Buffer.BlockCopy(backspaces, 0, combined, endKey.Length, backspaces.Length);
        Buffer.BlockCopy(replacement, 0, combined, endKey.Length + backspaces.Length, replacement.Length);
        if (combined.Length > 0)
            _session.WriteUserInput(combined);

        _session.CurrentInputLine.Clear();
        _session.CurrentInputLine.Append(targetText);
    }

    private void ReplaceClaudeInputLine(string targetText)
    {
        if (_session == null) return;
        byte ctrlE = 0x05;
        byte ctrlU = 0x15;
        var newText = InputEncoder.EncodeText(targetText);
        var combined = new byte[2 + newText.Length];
        combined[0] = ctrlE;
        combined[1] = ctrlU;
        Buffer.BlockCopy(newText, 0, combined, 2, newText.Length);
        _session.WriteUserInput(combined);
    }

    public bool HandleShiftArrow(Key key, bool ctrl)
    {
        if (_canvas == null) return false;
        var ctx = GetContext();
        if (ctx == null) return false;

        var buffer = _session!.Emulator!.Buffer;
        var currentText = ctx.Value.Text;
        if (currentText.Length == 0) return false;
        int inputStartCol = ctx.Value.InputStartCol;

        int cursorIdx;
        if (_cursorIdx != null)
        {
            cursorIdx = _cursorIdx.Value;
        }
        else
        {
            int rawIdx = buffer.CursorCol - inputStartCol;
            cursorIdx = rawIdx >= currentText.Length - 1
                ? currentText.Length
                : Math.Max(0, rawIdx);
        }

        if (_anchorIdx == null)
            _anchorIdx = cursorIdx;

        int newIdx = key switch
        {
            Key.Left => ctrl
                ? FindPreviousWordBoundary(currentText, cursorIdx)
                : Math.Max(0, cursorIdx - 1),
            Key.Right => ctrl
                ? FindNextWordBoundary(currentText, cursorIdx)
                : Math.Min(currentText.Length, cursorIdx + 1),
            Key.Home => 0,
            Key.End => currentText.Length,
            _ => cursorIdx
        };

        _cursorIdx = newIdx;
        int anchorIdx = _anchorIdx.Value;
        long absRow = ScreenRowToAbsoluteRow(buffer, _canvas.Rows, ctx.Value.ScreenRow);

        int selStartIdx = Math.Min(anchorIdx, newIdx);
        int selEndIdx = Math.Max(anchorIdx, newIdx) - 1;

        if (selStartIdx > selEndIdx)
        {
            _canvas.SelectionStart = null;
            _canvas.SelectionEnd = null;
        }
        else
        {
            _canvas.SelectionStart = (absRow, inputStartCol + selStartIdx);
            _canvas.SelectionEnd = (absRow, inputStartCol + selEndIdx);
        }
        _canvas.Invalidate();

        if (!ctx.Value.IsClaude)
        {
            ConsoleKey consoleKey = key switch
            {
                Key.Left => ConsoleKey.LeftArrow,
                Key.Right => ConsoleKey.RightArrow,
                Key.Home => ConsoleKey.Home,
                Key.End => ConsoleKey.End,
                _ => ConsoleKey.LeftArrow
            };
            _session.WriteUserInput(InputEncoder.EncodeKey(consoleKey, ctrl: ctrl));
        }
        return true;
    }

    public void ClearSelection()
    {
        if (_canvas != null)
        {
            _canvas.SelectionStart = null;
            _canvas.SelectionEnd = null;
        }
        _anchorIdx = null;
        _cursorIdx = null;
    }

    internal static int FindPreviousWordBoundary(string text, int pos)
    {
        if (pos <= 0) return 0;
        pos--;
        while (pos > 0 && text[pos] == ' ') pos--;
        while (pos > 0 && text[pos - 1] != ' ') pos--;
        return pos;
    }

    internal static int FindNextWordBoundary(string text, int pos)
    {
        if (pos >= text.Length) return text.Length;
        while (pos < text.Length && text[pos] != ' ') pos++;
        while (pos < text.Length && text[pos] == ' ') pos++;
        return pos;
    }
}
