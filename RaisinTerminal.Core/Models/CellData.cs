namespace RaisinTerminal.Core.Models;

/// <summary>
/// Represents a single terminal cell: character + display attributes.
/// </summary>
public readonly record struct CellData(
    char Character,
    byte ForegroundR = CellData.DefaultFgR, byte ForegroundG = CellData.DefaultFgG, byte ForegroundB = CellData.DefaultFgB,
    byte BackgroundR = CellData.DefaultBgR, byte BackgroundG = CellData.DefaultBgG, byte BackgroundB = CellData.DefaultBgB,
    bool Bold = false, bool Italic = false, bool Underline = false,
    bool Reverse = false, bool Dim = false, bool Strikethrough = false)
{
    public const byte DefaultFgR = 204, DefaultFgG = 204, DefaultFgB = 204;
    public const byte DefaultBgR = 33, DefaultBgG = 33, DefaultBgB = 33;

    public static readonly CellData Empty = new(' ');
}
