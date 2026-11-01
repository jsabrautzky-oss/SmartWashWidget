using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace SmartWashWidget.Interop;

/// <summary>Ergebnis eines Glas-Aufrufs, fuer die Statusanzeige im Einstellungsfenster.</summary>
public sealed record GlassApplyResult(
    bool DarkModeApplied,
    bool CornerApplied,
    bool RegionApplied,
    bool BlurApplied,
    int HResult,
    string? Detail)
{
    /// <summary>Der Desktop wird hinter dem Fenster weichgezeichnet (Accent-Policy-Acryl).</summary>
    public bool GlassActive => BlurApplied;

    /// <summary>Gesandte Acryl-Deckkraft (0, wenn kein Blur aktiv ist).</summary>
    public byte AccentAlpha => BlurApplied ? AccentPolicy.AccentAlphaFixed : (byte)0;
}

/// <summary>
/// Glas-Hintergrund eines Widgets-Fensters - ausschliesslich ueber user32!SetWindowCompositionAttribute.
///
/// Bewusst OHNE DWMWA_SYSTEMBACKDROP_TYPE (38), ohne Mica / Mica Alt / DWMSBT_*:
/// Mica und Mica Alt sind von Natur aus nahezu undurchsichtiges Grau und zeigen nie echte
/// Transparenz; DWMs eigenes Acrylic schaltet ohne Fensterfokus auf dasselbe Grau um.
///
/// Stattdessen:
///   Blur AN  -> AccentPolicy.EnableAcrylicBlur(): ACCENT_ENABLE_ACRYLICBLURBEHIND (4),
///               AccentFlags = 2, GradientColor = 0x0110141E (Alpha fest 0x01).
///   Blur AUS -> AccentPolicy.Disable(): ACCENT_DISABLED (0). Weil die MainWindow
///               AllowsTransparency="True" nutzt, ist das Fenster dann komplett durchsichtig.
///
/// Die Abdunkelung (Slider "Glas-Toenung") ist NUR der Alpha-Wert des RootBorder-Hintergrunds
/// in MainWindow.xaml - keine zweite, sich aufaddierende Schicht.
/// </summary>
public static class WindowBackdropHelper
{
    // DWMWA_* (dwmapi.h)
    public const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;
    public const int DWMWA_USE_IMMERSIVE_DARK_MODE_BEFORE_20H1 = 19;
    public const int DWMWA_WINDOW_CORNER_PREFERENCE = 33;

    // DWMWCP_*
    private const int DWMWCP_DONOTROUND = 1;
    public const int DWMWCP_ROUND = 2;

    private const int ATTR_SIZE = sizeof(int);

    // ---------------- Slider "Glas-Toenung" -> Alpha der einen XAML-Schicht ----------------

    /// <summary>RootBorder-Alpha bei 0 % Slider: 5 -> fast komplett durchsichtig.</summary>
    public const byte TintAlphaMin = 5;

    /// <summary>RootBorder-Alpha bei 100 % Slider: 235 -> fast undurchsichtig dunkel.</summary>
    public const byte TintAlphaMax = 235;

    /// <summary>Basisfarbe der Toenung: RGB = 0D / 11 / 1A (XAML-Farbe #0D111A).</summary>
    public const byte TintColorR = 0x0D;
    public const byte TintColorG = 0x11;
    public const byte TintColorB = 0x1A;

    /// <summary>
    /// Einziges Ergebnis des Sliders: Alpha des RootBorder-Hintergrunds.
    /// 0.00 -> 5, 1.00 -> 235, dazwischen linear.
    /// </summary>
    public static byte TintAlphaForSlider(double tint)
    {
        double t = Math.Clamp(tint, 0.0, 1.0);
        return (byte)Math.Round(TintAlphaMin + t * (TintAlphaMax - TintAlphaMin));
    }

    /// <summary>Echte Build-Nummer (via RtlGetVersion durch .NET), z. B. 22621 / 26100.</summary>
    public static int OsBuild => Environment.OSVersion.Version.Build;

    public static bool IsWindows11 => OsBuild >= 22000;

    /// <summary>
    /// Setzt dunklen Rahmen, hardware-runde Ecken und den Milchglas-Blur auf ein bestehendes Fenster.
    /// Kann jederzeit live angewendet werden (kein Fensterneubau noetig).
    /// </summary>
    public static GlassApplyResult Apply(IntPtr hwnd, bool acrylicBlur, bool dark = true, bool roundCorners = true)
    {
        if (hwnd == IntPtr.Zero)
            return new GlassApplyResult(false, false, false, false, unchecked((int)0x80070057), "HWND ist leer.");

        bool darkOk = false;
        bool cornerOk = false;
        bool regionOk = false;
        bool blurOk = false;
        int lastHr = 0;
        var notes = new List<string>();

        // 1) DWMWA_USE_IMMERSIVE_DARK_MODE = 20 -> 1  (dunkle Rahmen/Beschriftung)
        if (dark)
        {
            int immersive = 1;
            int hr = DwmSetWindowAttribute(hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE, ref immersive, ATTR_SIZE);
            if (hr != 0)
            {
                // Fallback fuer aeltere Builds
                hr = DwmSetWindowAttribute(hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE_BEFORE_20H1, ref immersive, ATTR_SIZE);
            }
            darkOk = hr == 0;
            if (!darkOk)
            {
                lastHr = hr;
                notes.Add("Dark-Mode-Attribut abgelehnt (0x" + hr.ToString("X8") + ").");
            }
        }

        // 2) DWMWA_WINDOW_CORNER_PREFERENCE = 33 -> 2 (DWMWCP_ROUND)
        //    Rundet den DWM-Rahmen. Auf einem rahmenlosen Fenster bewirkt das allein fuer den
        //    Accent-Blur nichts (gemessen) - deshalb folgt in Schritt 2b zusaetzlich die GDI-Region.
        int corner = roundCorners ? DWMWCP_ROUND : DWMWCP_DONOTROUND;
        int hrCorner = DwmSetWindowAttribute(hwnd, DWMWA_WINDOW_CORNER_PREFERENCE, ref corner, ATTR_SIZE);
        cornerOk = hrCorner == 0;
        if (!cornerOk)
        {
            lastHr = hrCorner;
            notes.Add("Runde Ecken abgelehnt (0x" + hrCorner.ToString("X8") + ").");
        }

        // 2b) Die abgerundete FENSTER-REGION (SetWindowRgn) setzt der Aufrufer selbst:
        //     DWMWA_WINDOW_CORNER_PREFERENCE rundet nur den DWM-Rahmen, nicht die Accent-Flaeche.
        //     Ohne Region ragt der Milchglas-Blur eckig ueber die XAML-Ecken hinaus.

        // 3) Milchglas-Blur (Accent-Policy) - der einzige Blur-Pfad, fokusunabhaengig.
        if (!IsWindows11)
        {
            notes.Add("Windows 11 (Build >= 22000) erforderlich, gefunden: Build " + OsBuild + ".");
        }
        else if (acrylicBlur)
        {
            blurOk = AccentPolicy.EnableAcrylicBlur(hwnd);
            if (blurOk)
            {
                notes.Add("Milchglas-Blur aktiv: ACCENT_ENABLE_ACRYLICBLURBEHIND, AccentFlags 2, "
                    + "GradientColor 0x" + AccentPolicy.GradientColorAcrylic.ToString("X8")
                    + " (Alpha 0x" + AccentPolicy.AccentAlphaFixed.ToString("X2")
                    + ") - bleibt auch ohne Fensterfokus aktiv.");
            }
            else
            {
                notes.Add("user32!SetWindowCompositionAttribute wurde abgelehnt - "
                    + "Windows-Transparenzeffekte deaktiviert oder Build zu alt?");
            }
        }
        else
        {
            AccentPolicy.Disable(hwnd);
            blurOk = false;
            notes.Add("Milchglas-Blur aus: ACCENT_DISABLED - das Fenster ist glasklar durchsichtig "
                + "(AllowsTransparency=True auf der MainWindow).");
        }

        string? detail = notes.Count > 0 ? string.Join(" ", notes) : null;
        return new GlassApplyResult(darkOk, cornerOk, regionOk, blurOk, lastHr, detail);
    }

    /// <summary>
    /// Clippet das Fenster auf eine abgerundete Region (GDI-Region auf dem HWND).
    ///
    /// Warum noetig: die Accent-Policy malt ihren Acryl-Blur auf die komplette Fensterrechteck-Region.
    /// DWMWA_WINDOW_CORNER_PREFERENCE rundet nur den (hier nicht vorhandenen) DWM-Rahmen, nicht die
    /// Accent-Flaeche - ohne diese Region ragt der Blur eckig ueber die abgerundeten XAML-Ecken hinaus.
    /// radiusPx <= 0 nimmt die Region zurueck (dann wieder das volle Rechteck).
    /// </summary>
    public static bool ApplyRoundedRegion(IntPtr hwnd, int widthPx, int heightPx, int radiusPx)
    {
        if (hwnd == IntPtr.Zero) return false;

        // radiusPx = 0: Region aufheben -> Windows-Standard (volles Rechteck).
        if (radiusPx <= 0)
            return SetWindowRgn(hwnd, IntPtr.Zero, true) != 0;

        if (widthPx <= 2 || heightPx <= 2) return false;

        int d = Math.Max(2, radiusPx * 2);   // CreateRoundRectRgn erwartet die Durchmesser der Eck-Ellipse
        IntPtr rgn = CreateRoundRectRgn(0, 0, widthPx + 1, heightPx + 1, d, d);
        if (rgn == IntPtr.Zero) return false;

        // Bei Erfolg gehoert die Region dem Fenster (nicht selbst DestroyObject), bei Misserfolg nicht.
        int res = SetWindowRgn(hwnd, rgn, true);
        if (res == 0)
        {
            DeleteObject(rgn);
            return false;
        }
        return true;
    }

    /// <summary>Region auf die aktuelle Clientflaeche des Fensters anwenden.</summary>
    public static bool ApplyRoundedRegion(IntPtr hwnd, int radiusPx)
    {
        if (hwnd == IntPtr.Zero) return false;

        if (radiusPx <= 0)
            return SetWindowRgn(hwnd, IntPtr.Zero, true) != 0;

        if (!TryClientSize(hwnd, out int cw, out int ch)) return false;
        return ApplyRoundedRegion(hwnd, cw, ch, radiusPx);
    }

    /// <summary>
    /// Ratio Geraetepixel / DIP fuer dieses Fenster (1.0 bei 96 dpi, 1.25 bei 125 %).
    /// Laedt den Eckenradius korrekt skaliert werden, ohne die Fensterbreite raten zu muessen.
    /// </summary>
    public static double GetDeviceScale(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero) return 1.0;

        uint dpi = GetDpiForWindow(hwnd);
        if (dpi < 96) dpi = 96;
        return dpi / 96.0;
    }

    /// <summary>Prueft, ob der DWM ueberhaupt Transparenz-/Backdrop-Effekte erlaubt (keine Garantie fuer Blur).</summary>
    public static bool DwmCompositionEnabled()
    {
        try
        {
            if (DwmIsCompositionEnabled(out bool composition) == 0)
                return composition;
            return false;
        }
        catch
        {
            return false;
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    /// <summary>Clientgroesse in Geraetepixeln (rahmenloses Fenster = Fensterflaeche).</summary>
    public static bool TryClientSize(IntPtr hwnd, out int widthPx, out int heightPx)
    {
        widthPx = 0;
        heightPx = 0;
        if (hwnd == IntPtr.Zero) return false;

        RECT rc;
        if (!GetClientRect(hwnd, out rc)) return false;

        widthPx = rc.Right - rc.Left;
        heightPx = rc.Bottom - rc.Top;
        return widthPx > 0 && heightPx > 0;
    }

    [DllImport("user32.dll")]
    private static extern bool GetClientRect(IntPtr hwnd, out RECT rect);

    // user32 ab Windows 10 1607: DPI genau dieses Fensters (PerMonitor-V2-konform).
    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr hwnd);

    // SetWindowRgn uebernimmt im Erfolgsfall den Besitz an der Region.
    [DllImport("user32.dll")]
    private static extern int SetWindowRgn(IntPtr hwnd, IntPtr hRgn, bool bRedraw);

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateRoundRectRgn(int left, int top, int right, int bottom, int widthEllipse, int heightEllipse);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteObject(IntPtr hObject);

    [DllImport("dwmapi.dll", PreserveSig = true)]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int attrValue, int attrSize);

    [DllImport("dwmapi.dll", PreserveSig = true)]
    private static extern int DwmIsCompositionEnabled(out bool enabled);
}
