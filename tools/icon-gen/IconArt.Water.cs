using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;

namespace IconGen;

internal static partial class IconArt
{
    // ---------------- Wasser: Sinusflaeche + geschlossene Kreisunterkante ----------------

    /// <summary>
    /// Wasserfuellung ohne Clipping: die Oberflaeche endet exakt auf dem Innenkreis,
    /// der Boden folgt der Kreisarcade. Das haelt die Kante suav (Regions-Clipping ist
    /// in GDI+ nicht anti-aliastet und wuerfe Treppenstufen).
    /// </summary>
    private static void DrawWater(Graphics g, float cx, float cy, float R, int size, double boost)
    {
        float rc = (float)(InnerR * R);
        double amp = Math.Max(0.75, 0.024 * size) * SS;
        double surfY = cy + Level * R;

        double yL = surfY + Wave(0.0, size) * amp;
        double yR = surfY + Wave(1.0, size) * amp;
        double xL = cx - Math.Sqrt(rc * rc - (yL - cy) * (yL - cy));
        double xR = cx + Math.Sqrt(rc * rc - (yR - cy) * (yR - cy));

        int steps = size <= 32 ? 12 : size <= 64 ? 20 : 30;

        PointF[] surface = new PointF[steps + 1];
        for (int i = 0; i <= steps; i++)
        {
            double xi = (double)i / steps;
            double x = xL + (xR - xL) * xi;
            double y = surfY + Wave(xi, size) * amp;
            surface[i] = new PointF((float)x, (float)y);
        }

        double startDeg = NormalizeDeg(Math.Atan2(yR - cy, xR - cx) * 180.0 / Math.PI);
        double endDeg = NormalizeDeg(Math.Atan2(yL - cy, xL - cx) * 180.0 / Math.PI);
        double sweep = endDeg - startDeg;
        if (sweep < 0) sweep += 360.0;

        float top = (float)(surfY - amp);

        using (GraphicsPath body = new GraphicsPath())
        {
            body.AddLines(surface);
            body.AddArc(cx - rc, cy - rc, rc * 2, rc * 2, (float)startDeg, (float)sweep);
            body.CloseFigure();

            using (LinearGradientBrush fill = new LinearGradientBrush(
                       new RectangleF(0, top, R, (float)(cy + rc) - top),
                       AccentColor(), AccentColor(), 90f))
            {
                ColorBlend blend = new ColorBlend();
                blend.Colors = new Color[]
                {
                    WithAlpha(Clamp(0x9A * boost), AccentColor()),
                    Color.FromArgb(Clamp(0x7C * boost), 0x14, 0x8C, 0xD4),
                    Color.FromArgb(Clamp(0x58 * boost), 0x08, 0x3C, 0x66),
                };
                blend.Positions = new float[] { 0f, 0.5f, 1f };
                fill.InterpolationColors = blend;

                g.FillPath(fill, body);
            }
        }

        using (GraphicsPath crest = new GraphicsPath())
        {
            crest.AddLines(surface);

            using (Pen pen = new Pen(WithAlpha(Clamp(0x8C * boost), AccentColor()), Line(size, 0.009, 1.0)))
            {
                pen.StartCap = LineCap.Round;
                pen.EndCap = LineCap.Round;
                g.DrawPath(pen, crest);
            }
        }
    }

    /// <summary>
    /// Ueberlagerte Welle wie im Widget: Grundwelle + gegenlaeufige Sekundaerwelle + Ripple.
    /// xi laeuft von 0 (linker Rand) bis 1 (rechter Rand).
    /// </summary>
    private static double Wave(double xi, int size)
    {
        double primary = Math.Sin(xi * 6.80 + 2.15);
        double secondary = 0.5 * Math.Sin(xi * 3.28 + 0.35);
        double ripple = size >= 48 ? 0.16 * Math.Sin(xi * 17.6 + 1.1) : 0.0;
        return primary + secondary + ripple;
    }

    // ---------------- Partikel: steigende Luftblasen ----------------

    private static void DrawBubbles(Graphics g, float cx, float cy, float R, int size, double boost)
    {
        int count = size <= 24 ? 4 : size <= 48 ? 7 : size <= 128 ? 11 : Bubbles.Length;
        float rc = (float)(InnerR * R);
        double surfY = cy + Level * R;

        List<Bubble> picked = new List<Bubble>(Bubbles);
        picked.Sort(static (a, b) => b.RFrac.CompareTo(a.RFrac));

        Color pale = Color.FromArgb(0xEE, 0xFB, 0xFF);

        for (int i = 0; i < count; i++)
        {
            Bubble b = picked[i];

            double x = cx + b.Dx * rc;
            double y = cy + b.Dy * rc;
            float rr = (float)(Math.Max(0.9, b.RFrac * size) * SS);

            // Oberhalb der Wasseroberflaeche duennen die Partikel aus (Spritzwasser, kein dichter Kern).
            double fade = y < surfY ? 0.62 : 1.0;

            using (GraphicsPath view = Ellipse((float)x, (float)y, rr))
            using (PathGradientBrush soft = new PathGradientBrush(view))
            {
                soft.CenterPoint = new PointF((float)(x - rr * 0.18), (float)(y - rr * 0.18));
                soft.CenterColor = WithAlpha(Clamp(0x16 * boost * fade), pale);
                soft.SurroundColors = new Color[] { WithAlpha(Clamp(b.Alpha * boost * fade), pale) };
                soft.WrapMode = WrapMode.Clamp;
                g.FillPath(soft, view);
            }

            if (rr >= 2.2f * SS)
            {
                float spec = rr * 0.30f;
                using (SolidBrush glint = new SolidBrush(WithAlpha(Clamp(0x9C * boost * fade), Color.White)))
                    g.FillEllipse(glint, (float)(x - spec * 1.1) - spec, (float)(y - spec * 1.4) - spec, spec * 2, spec * 2);
            }
        }
    }

    // ---------------- Reflex auf dem Bullaugenglas ----------------

    private static void DrawSheen(Graphics g, float cx, float cy, float R)
    {
        PointF start = new PointF(0.242f * R, 0.333f * R);
        PointF end = new PointF(0.697f * R, 0.242f * R);

        using (GraphicsPath sheen = new GraphicsPath())
        {
            sheen.AddBezier(start,
                new PointF(0.288f * R, 0.167f * R),
                new PointF(0.545f * R, 0.136f * R),
                end);
            sheen.AddBezier(end,
                new PointF(0.545f * R, 0.227f * R),
                new PointF(0.348f * R, 0.258f * R),
                start);
            sheen.CloseFigure();

            using (LinearGradientBrush fill = new LinearGradientBrush(
                       new RectangleF(0, 0.10f * R, R, 0.32f * R),
                       WithAlpha(0x48, Color.White), WithAlpha(0x00, Color.White), 90f))
                g.FillPath(fill, sheen);
        }
    }
}
