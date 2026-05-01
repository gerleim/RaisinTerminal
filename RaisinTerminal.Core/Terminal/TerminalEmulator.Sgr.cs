using RaisinTerminal.Core.Models;

namespace RaisinTerminal.Core.Terminal;

public partial class TerminalEmulator
{
    // Current SGR attributes
    private byte _fgR = CellData.DefaultFgR, _fgG = CellData.DefaultFgG, _fgB = CellData.DefaultFgB;
    private byte _bgR = CellData.DefaultBgR, _bgG = CellData.DefaultBgG, _bgB = CellData.DefaultBgB;
    private bool _bold, _italic, _underline;
    private bool _reverse, _dim, _strikethrough;

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
