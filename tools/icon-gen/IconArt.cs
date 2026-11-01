using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace IconGen;

/// <summary>
/// Einziges Master-Design fuer alle Icon-Groessen: dasselbe runde Bullauge wie die
/// Widget-Trommel (Trommelmitnehmer, Sinus-Wasserwelle, aufsteigende Partikel, Glow),
/// nur proportional auf 0..1 normalisiert.
///
/// Jede Groesse wird 8-fach ueberabgetastet gezeichnet und danach per exaktem 8x8-Blockmittelwert
/// mit praemultipliziertem Alpha heruntergerechnet - dadurch keine dunklen Saumkanten
/// an der Kreis-Kante und keine Flimmerkanten an duennen Linien.
/// </summary>
internal static partial class IconArt
{
    /// <summary>Ueberastungsfaktor: gezeichnet wird 8x so gross wie das Ziel.</summary>
    private const int SS = 8;

    // Proportionen der Widget-Trommel (DesignSize 132), normalisiert auf die Iconkante.
    private const double BodyR = 0.440;    // 118/2/132 = 0.447, hier mit Luft fuer den Glow
    private const double RimR = 0.422;     // 112/2/132
    private const double GlassR = 0.379;   // 100/2/132
    private const double DrumR = 0.318;    // 42/132
    private const double InnerR = 0.300;   // 40/132, nutzbarer Innenraum fuer Wasser/Partikel

    // Wasserflaeche: negativer Wert = ueber der Trommelachse (viel Wasser, wie im Waschgang).
    private const double Level = -0.035;

    // Drehwinkel der Mitnehmer: diagonal, damit das Bild in jedem Zustand "in Bewegung" wirkt.
    private const double PaddleTilt = 176.0;

    // Glow-Ring: beginnt direkt hinter der Blende und faellt nach aussen quadratisch ab.
    private const double GlowInner = 0.400;
    private const double GlowOuter = 0.500;
    private const double GlowStrength = 0.30;

    /// <summary>
    /// Partikel-Set von Hand gesetzt (statt zufaellig), damit das Symbol jedes Mal gleich
    /// aussieht. dx/dy in Einheiten des Innenradius, rFrac = Radius als Anteil der Iconkante.
    /// </summary>
    private readonly record struct Bubble(double Dx, double Dy, double RFrac, int Alpha);

    private static readonly Bubble[] Bubbles = new Bubble[]
    {
        new Bubble(-0.42, 0.52, 0.024, 0x9A),
        new Bubble(-0.12, 0.70, 0.020, 0x8C),
        new Bubble(0.26, 0.60, 0.016, 0x94),
        new Bubble(0.52, 0.40, 0.022, 0x80),
        new Bubble(-0.58, 0.28, 0.014, 0x86),
        new Bubble(0.06, 0.34, 0.012, 0x96),
        new Bubble(0.40, 0.74, 0.015, 0x6E),
        new Bubble(-0.28, 0.18, 0.013, 0x7A),
        new Bubble(-0.48, -0.08, 0.012, 0x66),
        new Bubble(0.14, -0.30, 0.010, 0x5C),
        new Bubble(0.56, 0.06, 0.011, 0x6A),
        new Bubble(-0.06, -0.52, 0.008, 0x4E),
        new Bubble(0.34, -0.44, 0.007, 0x42),
        new Bubble(-0.66, 0.52, 0.010, 0x62),
        new Bubble(0.68, 0.42, 0.009, 0x58),
        new Bubble(0.20, 0.84, 0.012, 0x5E),
    };

    // ---------------- Ausgabe ----------------

    /// <summary>Zeichnet das Symbol in der Zielgroesse (ARGB, ausserhalb des Kreises transparent).</summary>
    public static Bitmap RenderIcon(int size)
    {
        int render = size * SS;

        using (Bitmap big = new Bitmap(render, render, PixelFormat.Format32bppArgb))
        {
            using (Graphics g = Graphics.FromImage(big))
                DrawFrame(g, render, size);

            return Downsample(big, size);
        }
    }

    // ---------------- Aufbau der Ebenen ----------------

    private static void DrawFrame(Graphics g, int render, int size)
    {
        float R = render;
        float cx = R / 2f, cy = R / 2f;

        g.SmoothingMode = SmoothingMode.HighQuality;
        g.PixelOffsetMode = PixelOffsetMode.HighQuality;
        g.CompositingQuality = CompositingQuality.HighQuality;
        g.InterpolationMode = InterpolationMode.HighQualityBicubic;
        g.Clear(Color.Transparent);

        // Kleine Groellen bekommen mehr Kontrast und kraeftigere Linien, sonst verschwindet die Detailzeichnung.
        double boost = size <= 24 ? 1.40 : size <= 48 ? 1.16 : 1.0;
        bool fine = size >= 48;

        RectangleF full = new RectangleF(0, 0, R, R);

        DrawGlow(g, cx, cy, R, size, boost);
        DrawBody(g, cx, cy, R, size, full);
        DrawGlass(g, cx, cy, R, size, boost);
        DrawDrum(g, cx, cy, R, size, boost);

        if (fine) DrawRim(g, cx, cy, R, size, full);

        DrawRotor(g, cx, cy, R, size, full);
        DrawWater(g, cx, cy, R, size, boost);
        DrawBubbles(g, cx, cy, R, size, boost);

        if (size >= 32) DrawSheen(g, cx, cy, R);
    }

    /// <summary>
    /// Weicher Akzent-Glow rings um das Gehaeuse. GDI+ kennt keinen Radialverlauf mit
    /// Innengrenze, deshalb wird der Gluehpunkt einmalig analytisch als 512-px-Ebene
    /// berechnet und auf die aktuelle Groesse hochskaliert (Quellalpha, kein Schatten).
    /// </summary>
    private static void DrawGlow(Graphics g, float cx, float cy, float R, int size, double boost)
    {
        Bitmap layer = GlowLayer();

        // Der Glow tragt nach aussel weich ab. Gerade bei kleinen Groessen wuerde er
        // sonst den Hintergrund zulaufen lassen und den Rand grau wirken lassen.
        double soften = size <= 24 ? 0.62 : size <= 32 ? 0.84 : 1.0;

        using (ImageAttributes attr = new ImageAttributes())
        {
            attr.SetColorMatrix(new ColorMatrix { Matrix33 = (float)(boost * soften) });

            g.DrawImage(layer,
                new Rectangle(0, 0, (int)R, (int)R),
                0, 0, layer.Width, layer.Height,
                GraphicsUnit.Pixel, attr);
        }
    }

    private static Bitmap? _glowLayer;

    private static Bitmap GlowLayer()
    {
        if (_glowLayer is not null)
            return _glowLayer;

        const int n = 512;
        Color accent = AccentColor();
        double c = (n - 1) / 2.0;
        byte[] buffer = new byte[n * n * 4];

        for (int y = 0; y < n; y++)
        {
            for (int x = 0; x < n; x++)
            {
                double dx = (x - c) / c;
                double dy = (y - c) / c;
                double d = Math.Sqrt(dx * dx + dy * dy);

                // Kern unterhalb der Blende bleibt frei, ausserhalb faellt der Glow quadratisch ab.
                double t = (d - GlowInner) / (GlowOuter - GlowInner);
                if (t <= 0.0 || t >= 1.0)
                    continue;

                double k = (1.0 - t) * (1.0 - t);
                int alpha = Clamp(255 * GlowStrength * k);
                if (alpha <= 0)
                    continue;

                int o = (y * n + x) * 4;
                buffer[o] = accent.B;
                buffer[o + 1] = accent.G;
                buffer[o + 2] = accent.R;
                buffer[o + 3] = (byte)alpha;
            }
        }

        Bitmap layer = new Bitmap(n, n, PixelFormat.Format32bppArgb);
        PixelReader.Write(layer, buffer);

        _glowLayer = layer;
        return layer;
    }

    /// <summary>Gehaeuse-Ring (Bullauge-Blende) mit diagonalem Metallverlauf.</summary>
    private static void DrawBody(Graphics g, float cx, float cy, float R, int size, RectangleF full)
    {
        float r = (float)(BodyR * R);

        // Kleinstgroessen: der Ring ist hier nur anderthalb Pixel breit. Sein heller
        // Reflex wuerde das Gehaeuse sonst flaechig grau erscheinen lassen.
        bool tiny = size <= 32;
        Color top = tiny ? Color.FromArgb(0x33, 0x3E, 0x4C) : Color.FromArgb(0x49, 0x56, 0x66);
        Color bottom = tiny ? Color.FromArgb(0x0A, 0x0D, 0x13) : Color.FromArgb(0x0F, 0x13, 0x1A);
        byte highlight = tiny ? (byte)0x7E : (byte)0xD0;

        using (GraphicsPath body = Ellipse(cx, cy, r))
        using (LinearGradientBrush fill = new LinearGradientBrush(full, top, bottom, 135f))
            g.FillPath(fill, body);

        using (GraphicsPath body = Ellipse(cx, cy, r))
        using (LinearGradientBrush stroke = new LinearGradientBrush(full,
                   WithAlpha(highlight, Color.FromArgb(0x9E, 0xAC, 0xBA)), WithAlpha(0x30, Color.White), 90f))
        using (Pen pen = new Pen(stroke, Line(size, 0.020, 1.15)))
            g.DrawPath(pen, body);
    }

    /// <summary>Duenner Lichtreflex auf der Blende (nur ab 48 px, sonst zu feines Rauschen).</summary>
    private static void DrawRim(Graphics g, float cx, float cy, float R, int size, RectangleF full)
    {
        using (GraphicsPath rim = Ellipse(cx, cy, (float)(RimR * R)))
        using (LinearGradientBrush stroke = new LinearGradientBrush(full,
                   WithAlpha(0x5E, Color.White), WithAlpha(0x08, Color.White), 135f))
        using (Pen pen = new Pen(stroke, Line(size, 0.008, 0.8)))
            g.DrawPath(pen, rim);
    }

    /// <summary>Dunkles Bullaugenglas mit Lichtpool oben links (GradientOrigin des Widgets).</summary>
    private static void DrawGlass(Graphics g, float cx, float cy, float R, int size, double boost)
    {
        float r = (float)(GlassR * R);

        using (GraphicsPath glass = Ellipse(cx, cy, r))
        {
            using (SolidBrush deep = new SolidBrush(Color.FromArgb(0xFF, 0x04, 0x07, 0x0C)))
                g.FillPath(deep, glass);

            using (PathGradientBrush pool = new PathGradientBrush(glass))
            {
                pool.CenterPoint = new PointF(cx - 0.075f * R, cy - 0.085f * R);
                pool.FocusScales = new PointF(0.30f, 0.30f);
                pool.CenterColor = WithAlpha(Clamp(0xC8 * boost), Color.FromArgb(0x26, 0x32, 0x41));
                pool.SurroundColors = new Color[] { Color.FromArgb(0x00, 0x04, 0x07, 0x0C) };
                pool.WrapMode = WrapMode.Clamp;
                g.FillPath(pool, glass);
            }

            using (Pen pen = new Pen(WithAlpha(0x46, Color.White), Line(size, 0.011, 1.0)))
                g.DrawPath(pen, glass);
        }
    }

    /// <summary>Trommelrueckwand: dunkle Mulde mit hellem Rand.</summary>
    private static void DrawDrum(Graphics g, float cx, float cy, float R, int size, double boost)
    {
        float r = (float)(DrumR * R);

        using (GraphicsPath drum = Ellipse(cx, cy, r))
        {
            using (SolidBrush deep = new SolidBrush(Color.FromArgb(0xFF, 0x0A, 0x0E, 0x14)))
                g.FillPath(deep, drum);

            using (PathGradientBrush pool = new PathGradientBrush(drum))
            {
                pool.CenterPoint = new PointF(cx - 0.055f * R, cy - 0.065f * R);
                pool.FocusScales = new PointF(0.34f, 0.34f);
                pool.CenterColor = WithAlpha(Clamp(0xA8 * boost), Color.FromArgb(0x2A, 0x34, 0x42));
                pool.SurroundColors = new Color[] { Color.FromArgb(0x00, 0x0A, 0x0E, 0x14) };
                pool.WrapMode = WrapMode.Clamp;
                g.FillPath(pool, drum);
            }

            using (Pen pen = new Pen(WithAlpha(0x4A, Color.FromArgb(0xC8, 0xD6, 0xE4)), Line(size, 0.012, 1.0)))
                g.DrawPath(pen, drum);
        }
    }

    private static void DrawRotor(Graphics g, float cx, float cy, float R, int size, RectangleF full)
    {
        DrawPaddles(g, cx, cy, R, size, full);
        if (size >= 48) DrawPerforation(g, cx, cy, R, size);
        DrawHub(g, cx, cy, R, size, full);
    }

    /// <summary>Drei Trommelmitnehmer, wie im Widget als abgerundete Rechtecke auf 120-Grad-Abstand.</summary>
    private static void DrawPaddles(Graphics g, float cx, float cy, float R, int size, RectangleF full)
    {
        double mid = 0.2005 * R;       // Mitte des Mitnehmers (Radius 0.098..0.303)
        double halfLen = 0.1025 * R;

        // Kleine Groessen: Mitnehmer breiter zeichnen, sonst verschwinden sie im dunklen Trommelinneren.
        double wideScale = size <= 24 ? 1.45 : size <= 32 ? 1.30 : size <= 48 ? 1.14 : 1.0;
        double halfWide = 0.0417 * R * wideScale;
        double corner = 0.034 * R * wideScale;

        Color pale = size <= 32 ? Color.FromArgb(0xA8, 0xB8, 0xCC) : Color.FromArgb(0xC6, 0xD4, 0xE4);

        using (LinearGradientBrush fill = new LinearGradientBrush(full,
                   pale, Color.FromArgb(0x33, 0x3D, 0x4A), 135f))
        using (Pen edge = new Pen(Color.FromArgb(0x50, 0, 0, 0), Line(size, 0.006, 0.7)))
        {
            for (int i = 0; i < 3; i++)
            {
                double angle = PaddleTilt + i * 120.0;

                GraphicsPath paddle = RoundedRect(
                    (float)(cx - halfWide), (float)(cy - mid - halfLen),
                    (float)(2 * halfWide), (float)(2 * halfLen), (float)corner);

                using (Matrix m = new Matrix())
                {
                    m.Translate(cx, cy);
                    m.Rotate((float)angle);
                    m.Translate(-cx, -cy);
                    paddle.Transform(m);
                }

                g.FillPath(fill, paddle);
                if (size >= 48) g.DrawPath(edge, paddle);

                paddle.Dispose();
            }
        }
    }

    private static void DrawHub(Graphics g, float cx, float cy, float R, int size, RectangleF full)
    {
        using (LinearGradientBrush fill = new LinearGradientBrush(full,
                   Color.FromArgb(0xC0, 0xCE, 0xDE), Color.FromArgb(0x30, 0x3A, 0x47), 135f))
        {
            g.FillEllipse(fill, cx - (float)(0.068 * R), cy - (float)(0.068 * R),
                          (float)(0.136 * R), (float)(0.136 * R));
        }

        using (SolidBrush bore = new SolidBrush(Color.FromArgb(0xF0, 0x14, 0x1C, 0x26)))
            g.FillEllipse(bore, cx - (float)(0.032 * R), cy - (float)(0.032 * R),
                          (float)(0.064 * R), (float)(0.064 * R));

        if (size >= 64)
        {
            using (Pen pen = new Pen(WithAlpha(0x50, Color.White), Line(size, 0.004, 0.6)))
                g.DrawEllipse(pen, cx - (float)(0.032 * R), cy - (float)(0.032 * R),
                              (float)(0.064 * R), (float)(0.064 * R));
        }
    }

    /// <summary>Loeberringe der Trommel (ab 48 px - darunter wird das nur Flecken).</summary>
    private static void DrawPerforation(Graphics g, float cx, float cy, float R, int size)
    {
        using (SolidBrush dots = new SolidBrush(WithAlpha(0x2A, Color.FromArgb(0xD8, 0xEC, 0xFF))))
        {
            for (int ring = 0; ring < 2; ring++)
            {
                double radius = ring == 0 ? 0.256 * R : 0.166 * R;
                int count = ring == 0 ? 14 : 8;
                float dot = (float)Math.Max(0.9, 0.016 * size) * SS;

                for (int i = 0; i < count; i++)
                {
                    double a = Math.PI * 2 * i / count + ring * 0.22;
                    double x = cx + Math.Cos(a) * radius;
                    double y = cy + Math.Sin(a) * radius;
                    g.FillEllipse(dots, (float)(x - dot), (float)(y - dot), dot * 2, dot * 2);
                }
            }
        }
    }
}
