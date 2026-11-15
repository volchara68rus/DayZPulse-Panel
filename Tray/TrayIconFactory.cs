using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;

namespace DayZPulse.Tray;

/// <summary>Создаёт значок для области уведомлений прямо в памяти, без внешних файлов.</summary>
public static class TrayIconFactory
{
    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool DestroyIcon(IntPtr handle);

    public static Icon Create(Color background, Color accent, bool showDot, string glyph = "")
    {
        using var bitmap = new Bitmap(32, 32);
        using (var graphics = Graphics.FromImage(bitmap))
        {
            graphics.SmoothingMode = SmoothingMode.AntiAlias;
            graphics.Clear(Color.Transparent);

            using var body = new SolidBrush(background);
            graphics.FillEllipse(body, 1, 1, 30, 30);

            using var ring = new Pen(accent, 3f);
            graphics.DrawArc(ring, 5, 5, 22, 22, 200, 300);

            using var dot = new SolidBrush(accent);
            graphics.FillEllipse(dot, 12, 12, 8, 8);

            if (showDot)
            {
                using var badge = new SolidBrush(Color.FromArgb(80, 220, 100));
                graphics.FillEllipse(badge, 22, 22, 8, 8);
            }

            if (!string.IsNullOrEmpty(glyph))
            {
                using var font = new Font("Segoe UI", 14, FontStyle.Bold, GraphicsUnit.Pixel);
                using var text = new SolidBrush(accent);
                var size = graphics.MeasureString(glyph, font);
                graphics.DrawString(glyph, font, text, (32 - size.Width) / 2f, (32 - size.Height) / 2f);
            }
        }

        var handle = bitmap.GetHicon();
        try
        {
            using var temporary = Icon.FromHandle(handle);
            return (Icon)temporary.Clone();
        }
        finally
        {
            DestroyIcon(handle);
        }
    }
}
