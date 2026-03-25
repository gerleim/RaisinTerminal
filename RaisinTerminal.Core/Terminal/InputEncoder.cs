using System.Text;

namespace RaisinTerminal.Core.Terminal;

/// <summary>
/// Encodes keyboard and mouse events into VT escape sequences for the terminal.
/// </summary>
public static class InputEncoder
{
    public static byte[] EncodeKey(ConsoleKey key, bool ctrl = false, bool alt = false, bool shift = false)
    {
        // xterm modifier parameter: 1 + (shift?1:0) + (alt?2:0) + (ctrl?4:0)
        int mod = 1 + (shift ? 1 : 0) + (alt ? 2 : 0) + (ctrl ? 4 : 0);
        bool hasModifier = mod > 1;

        // Keys using CSI {letter} format — modified form: CSI 1;{mod} {letter}
        var arrowFinal = key switch
        {
            ConsoleKey.UpArrow => 'A',
            ConsoleKey.DownArrow => 'B',
            ConsoleKey.RightArrow => 'C',
            ConsoleKey.LeftArrow => 'D',
            ConsoleKey.Home => 'H',
            ConsoleKey.End => 'F',
            _ => (char)0
        };
        if (arrowFinal != 0)
            return Encoding.UTF8.GetBytes(hasModifier ? $"\x1B[1;{mod}{arrowFinal}" : $"\x1B[{arrowFinal}");

        // Keys using CSI {code} ~ format — modified form: CSI {code};{mod} ~
        var tildeCode = key switch
        {
            ConsoleKey.Insert => "2",
            ConsoleKey.Delete => "3",
            ConsoleKey.PageUp => "5",
            ConsoleKey.PageDown => "6",
            ConsoleKey.F5 => "15",
            ConsoleKey.F6 => "17",
            ConsoleKey.F7 => "18",
            ConsoleKey.F8 => "19",
            ConsoleKey.F9 => "20",
            ConsoleKey.F10 => "21",
            ConsoleKey.F11 => "23",
            ConsoleKey.F12 => "24",
            _ => null
        };
        if (tildeCode != null)
            return Encoding.UTF8.GetBytes(hasModifier ? $"\x1B[{tildeCode};{mod}~" : $"\x1B[{tildeCode}~");

        // F1–F4 use SS3 format — modified form: CSI 1;{mod} P/Q/R/S
        var ss3Final = key switch
        {
            ConsoleKey.F1 => 'P',
            ConsoleKey.F2 => 'Q',
            ConsoleKey.F3 => 'R',
            ConsoleKey.F4 => 'S',
            _ => (char)0
        };
        if (ss3Final != 0)
            return Encoding.UTF8.GetBytes(hasModifier ? $"\x1B[1;{mod}{ss3Final}" : $"\x1BO{ss3Final}");

        // Non-modified simple keys
        var sequence = key switch
        {
            ConsoleKey.Tab => shift ? "\x1B[Z" : "\t",
            ConsoleKey.Enter => "\r",
            ConsoleKey.Escape => "\x1B",
            ConsoleKey.Backspace => "\x7F",
            _ => (string?)null
        };

        if (sequence != null)
            return Encoding.UTF8.GetBytes(sequence);

        // Ctrl+letter
        if (ctrl && key >= ConsoleKey.A && key <= ConsoleKey.Z)
            return [(byte)(key - ConsoleKey.A + 1)];

        return [];
    }

    public static byte[] EncodeText(string text) => Encoding.UTF8.GetBytes(text);
}
