using System;
using System.Runtime.InteropServices;

namespace SmartWashWidget.Interop;

/// <summary>
/// user32!SetWindowCompositionAttribute (Accent-Policy) - der einzige Blur-Pfad des Widgets.
///
/// Architektur (bewusst ohne DWMWA_SYSTEMBACKDROP_TYPE / Mica / Mica Alt):
///   MainWindow: WindowStyle=None, AllowsTransparency=True, Background="Transparent".
///   Blur AN   : ACCENT_ENABLE_ACRYLICBLURBEHIND (4)  -> Windows 11 weichzeichnet den Desktop.
///   Blur AUS  : ACCENT_DISABLED (0)                  -> das Fenster ist zu 100 % glasklar.
///
/// Die Abdunkelung kommt ausschliesslich aus einer einzigen XAML-Schicht (RootBorder-Hintergrund).
/// Das Acryl-Gas selbst bleibt immer near-transparent: GradientColor steht fest auf 0x0110141E,
/// also Alpha 0x01. Dadurch erzeugt Windows 11 reinen Blur ohne grauen Zwangs-Schleier.
/// Die Accent-Policy kennt ausserdem kein Inaktiv-Overlay: der Blur bleibt auch ohne Fensterfokus.
/// </summary>
public static class AccentPolicy
{
    // WINCOMPATTRID
    private const int WCA_ACCENT_POLICY = 19;

    // ACCENT_STATE
    public const int ACCENT_DISABLED = 0;
    public const int ACCENT_ENABLE_GRADIENT = 1;
    public const int ACCENT_ENABLE_TRANSPARENTGRADIENT = 2;
    public const int ACCENT_ENABLE_BLURBEHIND = 3;
    public const int ACCENT_ENABLE_ACRYLICBLURBEHIND = 4;

    /// <summary>AccentFlags = 2: alle Rahmenkanten mitzeichnen -> saubere Kante am Blur.</summary>
    public const int ACCENT_FLAG_DRAW_ALL_BORDERS = 2;

    /// <summary>
    /// Feste Deckkraft des Acryl-Gases: Alpha 0x01 (das hart moegliche Minimum, AA = 0 wuerde den
    /// Blur abschalten). Bewusst NIEMALS hoeller gewaehlt - hoehere Alpha-Werte sind der graue
    /// Zwangs-Schleier, den genau dieses Design vermeiden will.
    /// </summary>
    public const byte AccentAlphaFixed = 0x01;

    // Kanael der dunklen Basisfarbe, als RGB = 0x1E1410.
    public const byte BaseR = 0x1E;
    public const byte BaseG = 0x14;
    public const byte BaseB = 0x10;

    /// <summary>
    /// GradientColor der Acryl-Schicht: 0xAABBGGRR = 0x0110141E
    /// (Alpha 0x01, Blau 0x10, Gruen 0x14, Rot 0x1E).
    /// </summary>
    public const int GradientColorAcrylic = 0x0110141E;

    [StructLayout(LayoutKind.Sequential)]
    private struct ACCENT_POLICY
    {
        public int AccentState;
        public int AccentFlags;
        public int GradientColor;
        public int AnimationId;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WINCOMPATTRDATA
    {
        public int Attribute;
        public IntPtr Data;
        public int Size;
    }

    [DllImport("user32.dll")]
    private static extern int SetWindowCompositionAttribute(IntPtr hwnd, ref WINCOMPATTRDATA data);

    /// <summary>
    /// ARGB -> 0xAABBGGRR, wie es die undokumentierte Accent-Policy erwartet.
    /// PackColor(AccentAlphaFixed, BaseR, BaseG, BaseB) == GradientColorAcrylic.
    /// </summary>
    public static int PackColor(byte a, byte r, byte g, byte b)
        => (a << 24) | (b << 16) | (g << 8) | r;

    /// <summary>
    /// ACCENT_ENABLE_ACRYLICBLURBEHIND mit der festen Acryl-Deckkraft AccentAlphaFixed (0x01).
    /// Kein Parameter fuer Alpha: die Toenung regelt allein der RootBorder der MainWindow.
    /// </summary>
    public static bool EnableAcrylicBlur(IntPtr hwnd)
        => SetPolicy(hwnd, ACCENT_ENABLE_ACRYLICBLURBEHIND);

    /// <summary>Accent-Policy zuruecknehmen (ACCENT_DISABLED): kein Blur, keine Farbe.</summary>
    public static bool Disable(IntPtr hwnd)
        => SetPolicy(hwnd, ACCENT_DISABLED);

    /// <summary>Setzt eine komplette Accent-Policy. Gibt zurueck, ob user32 sie akzeptiert hat.</summary>
    public static bool SetPolicy(IntPtr hwnd, int state)
    {
        if (hwnd == IntPtr.Zero) return false;

        ACCENT_POLICY policy = new ACCENT_POLICY
        {
            AccentState = state,
            AccentFlags = state == ACCENT_DISABLED ? 0 : ACCENT_FLAG_DRAW_ALL_BORDERS,
            GradientColor = state == ACCENT_DISABLED ? 0 : GradientColorAcrylic,
            AnimationId = 0
        };

        int size = Marshal.SizeOf<ACCENT_POLICY>();
        IntPtr ptr = Marshal.AllocHGlobal(size);
        try
        {
            Marshal.StructureToPtr(policy, ptr, true);

            WINCOMPATTRDATA data = new WINCOMPATTRDATA
            {
                Attribute = WCA_ACCENT_POLICY,
                Data = ptr,
                Size = size
            };

            // Rueckgabe ist undocumented: 0 == misslungen, alles andere == akzeptiert.
            return SetWindowCompositionAttribute(hwnd, ref data) != 0;
        }
        finally
        {
            Marshal.DestroyStructure<ACCENT_POLICY>(ptr);
            Marshal.FreeHGlobal(ptr);
        }
    }
}
