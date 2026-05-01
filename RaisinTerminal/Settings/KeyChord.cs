using System.Text;
using System.Windows.Input;

namespace RaisinTerminal.Settings;

/// <summary>
/// A keyboard shortcut: a key plus optional Ctrl/Alt/Shift/Win modifiers.
/// Serialized as "Ctrl+Shift+K". An empty chord (Key.None) represents "unbound".
/// </summary>
public readonly record struct KeyChord(Key Key, ModifierKeys Modifiers)
{
    public static readonly KeyChord None = new(Key.None, ModifierKeys.None);

    public bool IsNone => Key == Key.None;

    public override string ToString()
    {
        if (IsNone) return "";

        var sb = new StringBuilder();
        if ((Modifiers & ModifierKeys.Control) != 0) sb.Append("Ctrl+");
        if ((Modifiers & ModifierKeys.Alt) != 0) sb.Append("Alt+");
        if ((Modifiers & ModifierKeys.Shift) != 0) sb.Append("Shift+");
        if ((Modifiers & ModifierKeys.Windows) != 0) sb.Append("Win+");
        sb.Append(Key);
        return sb.ToString();
    }

    public static KeyChord Parse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return None;

        var parts = text.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var mods = ModifierKeys.None;
        Key key = Key.None;

        foreach (var part in parts)
        {
            if (part.Equals("Ctrl", StringComparison.OrdinalIgnoreCase) ||
                part.Equals("Control", StringComparison.OrdinalIgnoreCase))
                mods |= ModifierKeys.Control;
            else if (part.Equals("Alt", StringComparison.OrdinalIgnoreCase))
                mods |= ModifierKeys.Alt;
            else if (part.Equals("Shift", StringComparison.OrdinalIgnoreCase))
                mods |= ModifierKeys.Shift;
            else if (part.Equals("Win", StringComparison.OrdinalIgnoreCase) ||
                     part.Equals("Windows", StringComparison.OrdinalIgnoreCase))
                mods |= ModifierKeys.Windows;
            else if (Enum.TryParse<Key>(part, ignoreCase: true, out var k))
                key = k;
        }

        return new KeyChord(key, mods);
    }
}
