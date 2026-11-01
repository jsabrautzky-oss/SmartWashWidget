using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace IconGen;

internal static partial class IconArt
{
    // ---------------- Farb- und Linien-Helfer ----------------

    public static Color AccentColor() => Color.FromArgb(0x00, 0xE5, 0xFF);

    private static Color WithAlpha(int alpha, Color color)
        => Color.FromArgb(Math.Clamp(alpha, 0, 255), color.R, color.G, color.B);

    private static int Clamp(double alpha)
        => (int)Math.Round(Math.Clamp(alpha, 0.0, 255.0));

    /// <summary>
    /// Linienstaerke: proportional zur Icongroesse, aber mit Untergrenze in Ziel-Pixeln,
    /// damit feine Konturen bei 16/24 px nicht weggefiltert werden.
    /// </summary>
    private static float Line(int size, double frac, double minFinalPx)
        => (float)(Math.Max(frac * size, minFinalPx) * SS);

    private static GraphicsPath Ellipse(float cx, float cy, float radius)
    {
        GraphicsPath path = new GraphicsPath();
        path.AddEllipse(cx - radius, cy - radius, radius * 2, radius * 2);
        return path;
    }

    private static GraphicsPath RoundedRect(float x, float y, float width, float height, float radius)
    {
        radius = Math.Min(radius, Math.Min(width, height) / 2f);
        float d = radius * 2f;

        GraphicsPath path = new GraphicsPath();
        path.AddArc(x, y, d, d, 180, 90);
        path.AddArc(x + width - d, y, d, d, 270, 90);
        path.AddArc(x + width - d, y + height - d, d, d, 0, 90);
        path.AddArc(x, y + height - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }

    private static double NormalizeDeg(double deg)
    {
        deg %= 360.0;
        return deg < 0 ? deg + 360.0 : deg;
    }

    // ---------------- Herunterrechnen: 8x8-Blockmittelwert mit praemultipliziertem Alpha ----------------

    private static Bitmap Downsample(Bitmap source, int size)
    {
        int render = source.Width;
        int block = render / size;

        byte[] src = PixelReader.Read(source);
        byte[] dst = new byte[size * size * 4];

        for (int y = 0; y < size; y++)
        {
            for (int x = 0; x < size; x++)
            {
                int sa = 0, sr = 0, sg = 0, sb = 0;
                int originY = y * block, originX = x * block;

                for (int dy = 0; dy < block; dy++)
                {
                    int idx = (originY + dy) * render * 4 + originX * 4;

                    for (int dx = 0; dx < block; dx++, idx += 4)
                    {
                        int a = src[idx + 3];
                        if (a == 0) continue;

                        // Praemultipliziert mitteln: sonstziehen semitransparente Kanten
                        // einen grauschwarzen Saum um das runde Symbol.
                        sa += a;
                        sr += (src[idx + 2] * a) >> 8;
                        sg += (src[idx + 1] * a) >> 8;
                        sb += (src[idx] * a) >> 8;
                    }
                }

                int o = (y * size + x) * 4;
                int cells = block * block;
                int meanA = sa / cells;

                if (meanA <= 1)
                {
                    dst[o] = 0; dst[o + 1] = 0; dst[o + 2] = 0; dst[o + 3] = 0;
                    continue;
                }

                dst[o] = Unpremultiply(sb, meanA, cells);
                dst[o + 1] = Unpremultiply(sg, meanA, cells);
                dst[o + 2] = Unpremultiply(sr, meanA, cells);
                dst[o + 3] = (byte)Math.Min(255, meanA);
            }
        }

        Bitmap target = new Bitmap(size, size, PixelFormat.Format32bppArgb);
        PixelReader.Write(target, dst);
        return target;
    }

    private static byte Unpremultiply(int sumPremultiplied, int meanAlpha, int cells)
    {
        double value = (double)sumPremultiplied * 255.0 / (cells * meanAlpha);
        return (byte)Math.Clamp((int)Math.Round(value), 0, 255);
    }
}
