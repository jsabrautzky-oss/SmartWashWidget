using System;
using System.Runtime.InteropServices;

namespace SmartWashWidget.Interop;

/// <summary>
/// Taskleisten-Zugehoerigkeit eines Fensters - live auf dem bestehenden Fensterhandle.
///
/// WPFs eigene Window.ShowInTaskbar-Eigenschaft ist hier bewusst nicht der Weg: Sie ist beim
/// Anlegen des Fensters eingebrannt (XAML ShowInTaskbar="False") und ihre Aenderung baute das
/// Handle neu - damit verlieen Milchglas-Blur, abgerundete Region und die gemerkte Position.
/// Stattdessen wird der extended window style umgestellt:
///   WS_EX_TOOLWINDOW = Fenster gilt als Hilfefenster und bekommt KEINEN Taskleisten-Knopf
///   WS_EX_APPWINDOW  = erzwingt einen Taskleisten-Knopf fuer dieses Top-Level-Fenster
/// SWP_FRAMECHANGED sagt der Shell, dass sie das Fenster neu einordnen soll. Das reicht, um einen
/// vorhandenen Knopf zu entfernen; einen neuen anzulegen vermag die Taskleiste von Windows 11 auf
/// diesem Weg nicht - sie meldet Fenster beim Sichtbarwerden an (dazu MainWindow).
/// Die Shell-eigene ITaskbarList (AddTab/DeleteTab) ist auf Windows 11 nicht mehr registriert
/// (CoCreateInstance -> 0x80040154) und waere damit kein gangbarer Weg.
/// </summary>
public static class TaskbarStyle
{
    private const int GWL_EXSTYLE = -20;

    private const int WS_EX_TOOLWINDOW = 0x00000080;
    private const int WS_EX_APPWINDOW = 0x00040000;

    private const uint SWP_NOSIZE = 0x0001;
    private const uint SWP_NOMOVE = 0x0002;
    private const uint SWP_NOZORDER = 0x0004;
    private const uint SWP_NOACTIVATE = 0x0010;
    private const uint SWP_FRAMECHANGED = 0x0020;

    /// <summary>true, wenn das Fenster gerade als Taskleisten-Knopf sichtbar ist.</summary>
    public static bool IsShown(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero) return false;
        return (ReadExStyle(hwnd) & WS_EX_TOOLWINDOW) == 0;
    }

    /// <summary>
    /// Setzt die Taskleisten-Zugehoerigkeit und gibt true zurueck, wenn der Zielzustand auf dem
    /// Handle auch wirklich steht. Ein fehlgeschlagener Versuch stoert nie.
    /// </summary>
    public static bool Apply(IntPtr hwnd, bool showInTaskbar)
    {
        if (hwnd == IntPtr.Zero) return false;

        try
        {
            int current = ReadExStyle(hwnd);
            int next = showInTaskbar
                ? (current & ~WS_EX_TOOLWINDOW) | WS_EX_APPWINDOW
                : (current | WS_EX_TOOLWINDOW) & ~WS_EX_APPWINDOW;

            // SetWindowLong* meldet den bisherigen Style zurueck - die Diagnose bleibt erhalten,
            // statt den Rueckgabewert achtlos wegzuwerfen.
            int previous = next == current ? current : WriteExStyle(hwnd, next);

            // Der Shell-Stupser: Rahmen neu einordnen lassen, ohne Groesse/Position/Z-Orden zu aendern.
            SetWindowPos(hwnd, IntPtr.Zero, 0, 0, 0, 0,
                SWP_NOMOVE | SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE | SWP_FRAMECHANGED);

            // SWP_FRAMECHANGED allein genuegt der Windows-11-Taskleiste nicht: Sie fuehrt ihre
            // Knopfliste selbst und uebersieht ein nachtraeglich gesetztes WS_EX_APPWINDOW. Entfernen
            // kann sie einen Knopf durch den Stilwechsel, ihn anzulegen gelingt nur, wenn das Fenster
            // sichtbar wird - das uebernimmt MainWindow.ApplyTaskbarVisibility (kurz Hide/Show).

            if (IsShown(hwnd) == showInTaskbar) return true;

            LogQuiet("style stayed " + DescribeExStyle(ReadExStyle(hwnd))
                + ", vorher " + DescribeExStyle(previous));
            return false;
        }
        catch (Exception ex)
        {
            LogQuiet("apply failed: " + ex.Message);
            return false;
        }
    }

    private static string DescribeExStyle(int exStyle)
        => "0x" + exStyle.ToString("X8") + (IsTool(exStyle) ? " (tool window)" : " (app window)");

    private static bool IsTool(int exStyle) => (exStyle & WS_EX_TOOLWINDOW) != 0;

    // 64-bittige Prozesse muessen GetWindowLongPtr/SetWindowLongPtr verwenden: die 32-Bit-APIs
    // schneiden den Wert ab. Gewaehlt wird nach Zeigerbreite, beide Varianten werden nur
    // erreichbar auf dem passenden System aufgerufen.
    private static int ReadExStyle(IntPtr hwnd)
    {
        return IntPtr.Size == 8
            ? GetWindowLongPtr64(hwnd, GWL_EXSTYLE).ToInt32()
            : GetWindowLong32(hwnd, GWL_EXSTYLE);
    }

    /// <summary>Schreibt den extended window style und gibt den bisherigen Wert zurueck (0 = Fehler).</summary>
    private static int WriteExStyle(IntPtr hwnd, int exStyle)
    {
        return IntPtr.Size == 8
            ? SetWindowLongPtr64(hwnd, GWL_EXSTYLE, new IntPtr((long)exStyle)).ToInt32()
            : SetWindowLong32(hwnd, GWL_EXSTYLE, exStyle);
    }

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW", CharSet = CharSet.Unicode)]
    private static extern IntPtr GetWindowLongPtr64(IntPtr hwnd, int index);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongW", CharSet = CharSet.Unicode)]
    private static extern int GetWindowLong32(IntPtr hwnd, int index);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW", CharSet = CharSet.Unicode)]
    private static extern IntPtr SetWindowLongPtr64(IntPtr hwnd, int index, IntPtr newValue);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongW", CharSet = CharSet.Unicode)]
    private static extern int SetWindowLong32(IntPtr hwnd, int index, int newValue);

    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(IntPtr hwnd, IntPtr hwndInsertAfter, int x, int y, int cx, int cy, uint flags);

    /// <summary>Diagnosebesteffe - Logging darf selbst nie storen.</summary>
    private static void LogQuiet(string message)
    {
        try
        {
            System.IO.Directory.CreateDirectory(Services.AppConfigService.DirectoryPath);
            System.IO.File.AppendAllText(
                System.IO.Path.Combine(Services.AppConfigService.DirectoryPath, "error.log"),
                DateTime.Now.ToString("s") + "  [taskbar]  " + message + Environment.NewLine);
        }
        catch
        {
            // absichtlich still
        }
    }
}
