using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;

namespace IconGen;

/// <summary>
/// Kontrollblatt: Master in 256 px, dazu die echten Kleinstgroessen 16/24/32/48/64
/// auf dunkler UND heller Flaeche (Desktop, Taskleiste, dunkler Taskleisten-Modus)
/// und darunter dieselben Groessen 3-fach verlustfrei vergroessert.
/// </summary>
internal static class SheetWriter
{
    private const int Pad = 16;
    private const int Gap = 14;

    public static void Write(string path, Dictionary<int, Bitmap> icons)
    {
        List<(int Size, int Scale)> master = new List<(int, int)> { (256, 1) };
        List<(int, int)> native = new List<(int, int)> { (16, 1), (24, 1), (32, 1), (48, 1), (64, 1), (128, 1) };
        List<(int, int)> zoomed = new List<(int, int)> { (16, 3), (24, 3), (32, 3), (48, 3) };

        Size darkCard = CardSize(master);
        Size lightNative = CardSize(native);
        Size zoomCard = CardSize(zoomed);

        int margin = 24;
        const int caption = 26;

        int rowOne = darkCard.Width + Gap + zoomCard.Width;
        int rowTwo = lightNative.Width * 2 + Gap;

        int width = margin * 2 + Math.Max(rowOne, rowTwo);
        int height = margin * 2 + darkCard.Height + caption + Gap
                     + Math.Max(zoomCard.Height, lightNative.Height) + caption;

        using (Bitmap sheet = new Bitmap(width, height, PixelFormat.Format32bppArgb))
        {
            using (Graphics g = Graphics.FromImage(sheet))
            {
                g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;

                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                g.Clear(Color.FromArgb(0x15, 0x1B, 0x24));

                int x = margin, y = margin;

                x = DrawCard(g, icons, master, x, y, Color.FromArgb(0x20, 0x28, 0x34),
                             Color.FromArgb(0x3A, 0x46, 0x56), Color.FromArgb(0x9E, 0xAC, 0xBC), "Master 256 px") + Gap;
                DrawCard(g, icons, zoomed, x, y, Color.FromArgb(0x20, 0x28, 0x34),
                         Color.FromArgb(0x3A, 0x46, 0x56), Color.FromArgb(0x9E, 0xAC, 0xBC),
                         "16 / 24 / 32 / 48 px - 3-fach unveraendert vergroessert");

                y = margin + darkCard.Height + caption + Gap;
                x = margin;

                x = DrawCard(g, icons, native, x, y, Color.FromArgb(0xF1, 0xF4, 0xF8),
                             Color.FromArgb(0xC9, 0xD2, 0xDC), Color.FromArgb(0x3A, 0x46, 0x56),
                             "16 / 24 / 32 / 48 / 64 / 128 px auf hellem Grund") + Gap;
                DrawCard(g, icons, native, x, y, Color.FromArgb(0x0B, 0x10, 0x17),
                         Color.FromArgb(0x33, 0x3E, 0x4C), Color.FromArgb(0x9E, 0xAC, 0xBC),
                         "dieselben Groessen auf dunklem Grund");
            }

            sheet.Save(path, ImageFormat.Png);
        }
    }

    private static Size CardSize(List<(int Size, int Scale)> items)
    {
        int contentW = 0, contentH = 0;

        foreach ((int size, int scale) in items)
        {
            contentW += size * scale + Gap;
            contentH = Math.Max(contentH, size * scale);
        }

        return new Size(contentW - Gap + 2 * Pad, contentH + 2 * Pad);
    }

    private static int DrawCard(Graphics g, Dictionary<int, Bitmap> icons, List<(int Size, int Scale)> items,
                                int x, int y, Color back, Color border, Color text, string caption)
    {
        Size card = CardSize(items);

        int contentH = 0;
        foreach ((int size, int scale) in items)
            contentH = Math.Max(contentH, size * scale);

        using (GraphicsPath rounded = RoundedRect(x, y, card.Width, card.Height, 10))
        {
            using (SolidBrush fill = new SolidBrush(back))
                g.FillPath(fill, rounded);

            using (Pen pen = new Pen(border, 1f))
                g.DrawPath(pen, rounded);
        }

        int cursor = x + Pad;

        foreach ((int size, int scale) in items)
        {
            if (!icons.TryGetValue(size, out Bitmap? bitmap))
                continue;

            int drawW = size * scale;
            int top = y + Pad + (contentH - drawW) / 2;

            g.InterpolationMode = scale > 1 ? InterpolationMode.NearestNeighbor : InterpolationMode.HighQualityBicubic;
            g.PixelOffsetMode = scale > 1 ? PixelOffsetMode.Half : PixelOffsetMode.HighQuality;

            g.DrawImage(bitmap, new Rectangle(cursor, top, drawW, drawW),
                        new Rectangle(0, 0, size, size), GraphicsUnit.Pixel);

            cursor += drawW + Gap;
        }

        using (Font font = new Font("Segoe UI", 8.25f, FontStyle.Regular, GraphicsUnit.Point))
        using (SolidBrush brush = new SolidBrush(text))
            g.DrawString(caption, font, brush, x, y + card.Height + 7);

        return x + card.Width;
    }

    private static GraphicsPath RoundedRect(int x, int y, int width, int height, int radius)
    {
        int d = radius * 2;

        GraphicsPath path = new GraphicsPath();
        path.AddArc(x, y, d, d, 180, 90);
        path.AddArc(x + width - d, y, d, d, 270, 90);
        path.AddArc(x + width - d, y + height - d, d, d, 0, 90);
        path.AddArc(x, y + height - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }
}
