using System.Windows;
using System.Windows.Media;

namespace RaisinTerminal.Controls;

public partial class TerminalCanvas
{
    private static bool TryDrawBlockChar(DrawingContext dc, char ch, Brush brush, double x, double y, double w, double h)
    {
        switch (ch)
        {
            case '▀': // ▀ UPPER HALF BLOCK
                dc.DrawRectangle(brush, null, new Rect(x, y, w, h / 2));
                return true;
            case '▁': // ▁ LOWER ONE EIGHTH BLOCK
                dc.DrawRectangle(brush, null, new Rect(x, y + h * 7 / 8, w, h / 8));
                return true;
            case '▂': // ▂ LOWER ONE QUARTER BLOCK
                dc.DrawRectangle(brush, null, new Rect(x, y + h * 3 / 4, w, h / 4));
                return true;
            case '▃': // ▃ LOWER THREE EIGHTHS BLOCK
                dc.DrawRectangle(brush, null, new Rect(x, y + h * 5 / 8, w, h * 3 / 8));
                return true;
            case '▄': // ▄ LOWER HALF BLOCK
                dc.DrawRectangle(brush, null, new Rect(x, y + h / 2, w, h / 2));
                return true;
            case '▅': // ▅ LOWER FIVE EIGHTHS BLOCK
                dc.DrawRectangle(brush, null, new Rect(x, y + h * 3 / 8, w, h * 5 / 8));
                return true;
            case '▆': // ▆ LOWER THREE QUARTERS BLOCK
                dc.DrawRectangle(brush, null, new Rect(x, y + h / 4, w, h * 3 / 4));
                return true;
            case '▇': // ▇ LOWER SEVEN EIGHTHS BLOCK
                dc.DrawRectangle(brush, null, new Rect(x, y + h / 8, w, h * 7 / 8));
                return true;
            case '█': // █ FULL BLOCK
                dc.DrawRectangle(brush, null, new Rect(x, y, w, h));
                return true;
            case '▉': // ▉ LEFT SEVEN EIGHTHS BLOCK
                dc.DrawRectangle(brush, null, new Rect(x, y, w * 7 / 8, h));
                return true;
            case '▊': // ▊ LEFT THREE QUARTERS BLOCK
                dc.DrawRectangle(brush, null, new Rect(x, y, w * 3 / 4, h));
                return true;
            case '▋': // ▋ LEFT FIVE EIGHTHS BLOCK
                dc.DrawRectangle(brush, null, new Rect(x, y, w * 5 / 8, h));
                return true;
            case '▌': // ▌ LEFT HALF BLOCK
                dc.DrawRectangle(brush, null, new Rect(x, y, w / 2, h));
                return true;
            case '▍': // ▍ LEFT THREE EIGHTHS BLOCK
                dc.DrawRectangle(brush, null, new Rect(x, y, w * 3 / 8, h));
                return true;
            case '▎': // ▎ LEFT ONE QUARTER BLOCK
                dc.DrawRectangle(brush, null, new Rect(x, y, w / 4, h));
                return true;
            case '▏': // ▏ LEFT ONE EIGHTH BLOCK
                dc.DrawRectangle(brush, null, new Rect(x, y, w / 8, h));
                return true;
            case '▐': // ▐ RIGHT HALF BLOCK
                dc.DrawRectangle(brush, null, new Rect(x + w / 2, y, w / 2, h));
                return true;
            case '░': // ░ LIGHT SHADE (25%)
                dc.PushOpacity(0.25);
                dc.DrawRectangle(brush, null, new Rect(x, y, w, h));
                dc.Pop();
                return true;
            case '▒': // ▒ MEDIUM SHADE (50%)
                dc.PushOpacity(0.5);
                dc.DrawRectangle(brush, null, new Rect(x, y, w, h));
                dc.Pop();
                return true;
            case '▓': // ▓ DARK SHADE (75%)
                dc.PushOpacity(0.75);
                dc.DrawRectangle(brush, null, new Rect(x, y, w, h));
                dc.Pop();
                return true;
            case '▔': // ▔ UPPER ONE EIGHTH BLOCK
                dc.DrawRectangle(brush, null, new Rect(x, y, w, h / 8));
                return true;
            case '▕': // ▕ RIGHT ONE EIGHTH BLOCK
                dc.DrawRectangle(brush, null, new Rect(x + w * 7 / 8, y, w / 8, h));
                return true;
            case '▖': // ▖ QUADRANT LOWER LEFT
                dc.DrawRectangle(brush, null, new Rect(x, y + h / 2, w / 2, h / 2));
                return true;
            case '▗': // ▗ QUADRANT LOWER RIGHT
                dc.DrawRectangle(brush, null, new Rect(x + w / 2, y + h / 2, w / 2, h / 2));
                return true;
            case '▘': // ▘ QUADRANT UPPER LEFT
                dc.DrawRectangle(brush, null, new Rect(x, y, w / 2, h / 2));
                return true;
            case '▙': // ▙ QUADRANT UPPER LEFT AND LOWER LEFT AND LOWER RIGHT
                dc.DrawRectangle(brush, null, new Rect(x, y, w / 2, h)); // left full
                dc.DrawRectangle(brush, null, new Rect(x + w / 2, y + h / 2, w / 2, h / 2)); // lower right
                return true;
            case '▚': // ▚ QUADRANT UPPER LEFT AND LOWER RIGHT
                dc.DrawRectangle(brush, null, new Rect(x, y, w / 2, h / 2));
                dc.DrawRectangle(brush, null, new Rect(x + w / 2, y + h / 2, w / 2, h / 2));
                return true;
            case '▛': // ▛ QUADRANT UPPER LEFT AND UPPER RIGHT AND LOWER LEFT
                dc.DrawRectangle(brush, null, new Rect(x, y, w, h / 2)); // top full
                dc.DrawRectangle(brush, null, new Rect(x, y + h / 2, w / 2, h / 2)); // lower left
                return true;
            case '▜': // ▜ QUADRANT UPPER LEFT AND UPPER RIGHT AND LOWER RIGHT
                dc.DrawRectangle(brush, null, new Rect(x, y, w, h / 2)); // top full
                dc.DrawRectangle(brush, null, new Rect(x + w / 2, y + h / 2, w / 2, h / 2)); // lower right
                return true;
            case '▝': // ▝ QUADRANT UPPER RIGHT
                dc.DrawRectangle(brush, null, new Rect(x + w / 2, y, w / 2, h / 2));
                return true;
            case '▞': // ▞ QUADRANT UPPER RIGHT AND LOWER LEFT
                dc.DrawRectangle(brush, null, new Rect(x + w / 2, y, w / 2, h / 2));
                dc.DrawRectangle(brush, null, new Rect(x, y + h / 2, w / 2, h / 2));
                return true;
            case '▟': // ▟ QUADRANT UPPER RIGHT AND LOWER LEFT AND LOWER RIGHT
                dc.DrawRectangle(brush, null, new Rect(x + w / 2, y, w / 2, h)); // right full
                dc.DrawRectangle(brush, null, new Rect(x, y + h / 2, w / 2, h / 2)); // lower left
                return true;
            // Box Drawing: light horizontal line
            case '─': // ─
            case '╴': // ╴ left half
            case '╶': // ╶ right half
            {
                double cy = Math.Round(y + h / 2) + 0.5; // snap to pixel center for crisp 1px line
                var pen = new Pen(brush, 1);
                pen.Freeze();
                double left = (ch == '╶') ? x + w / 2 : x;
                double right = (ch == '╴') ? x + w / 2 : x + w;
                dc.DrawLine(pen, new Point(left, cy), new Point(right, cy));
                return true;
            }
            // Box Drawing: light vertical line
            case '│': // │
            {
                double cx = Math.Round(x + w / 2) + 0.5; // snap to pixel center for crisp 1px line
                var pen = new Pen(brush, 1);
                pen.Freeze();
                dc.DrawLine(pen, new Point(cx, y), new Point(cx, y + h));
                return true;
            }
            default:
                return false;
        }
    }
}
