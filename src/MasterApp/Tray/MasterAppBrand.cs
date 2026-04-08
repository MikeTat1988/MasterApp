using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;

namespace MasterApp.Tray;

internal static class MasterAppBrand
{
    public static Icon CreateTrayIcon()
    {
        using var bitmap = new Bitmap(64, 64);
        using var graphics = Graphics.FromImage(bitmap);
        graphics.SmoothingMode = SmoothingMode.AntiAlias;
        graphics.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
        graphics.Clear(Color.Transparent);

        using var outerBrush = new SolidBrush(Color.FromArgb(24, 29, 39));
        using var innerBrush = new SolidBrush(Color.FromArgb(60, 86, 214));
        using var textBrush = new SolidBrush(Color.FromArgb(245, 247, 250));
        using var font = new Font("Segoe UI", 26, FontStyle.Bold, GraphicsUnit.Pixel);

        graphics.FillEllipse(outerBrush, 0, 0, 64, 64);
        graphics.FillEllipse(innerBrush, 6, 6, 52, 52);

        var format = new StringFormat
        {
            Alignment = StringAlignment.Center,
            LineAlignment = StringAlignment.Center
        };

        graphics.DrawString("M", font, textBrush, new RectangleF(0, -1, 64, 64), format);
        return Icon.FromHandle(bitmap.GetHicon());
    }
}
