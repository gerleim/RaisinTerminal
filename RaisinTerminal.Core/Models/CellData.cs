namespace RaisinTerminal.Core.Models;

/// <summary>
/// Represents a single terminal cell: character + display attributes.
/// </summary>
public readonly record struct CellData(
    char Character,
    byte ForegroundR = 204, byte ForegroundG = 204, byte ForegroundB = 204,
    byte BackgroundR = 33, byte BackgroundG = 33, byte BackgroundB = 33,
    bool Bold = false, bool Italic = false, bool Underline = false,
    bool Reverse = false, bool Dim = false, bool Strikethrough = false)
{
    public static readonly CellData Empty = new(' ');
}
