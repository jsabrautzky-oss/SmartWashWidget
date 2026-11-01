using System;
using System.IO;

using Microsoft.Win32;

namespace SmartWashWidget.Services;

/// <summary>
/// "Mit Windows starten" ueber den Run-Schluessel des angemeldeten Benutzers
/// (HKCU\Software\Microsoft\Windows\CurrentVersion\Run). Kein Dienst, kein Task-Planer,
/// keine Administrator-Rechte: Der Eintrag gehoert dem Nutzer und ist jederzeit ueber den
/// Task-Manager (Autostart) wieder abschaltbar.
/// </summary>
public static class AutostartService
{
    /// <summary>Name des Registry-Werts (erscheint so im Task-Manager unter "Autostart").</summary>
    public const string ValueName = "SmartWashWidget";

    /// <summary>Run-Schluessel des aktuellen Benutzers - HKCU, ohne Administrator rights.</summary>
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";

    /// <summary>Pfad der EXE, die gerade laeuft (funktioniert auch bei Single-File-Publish).</summary>
    public static string? ExePath
    {
        get
        {
            string? path = Environment.ProcessPath;
            return string.IsNullOrWhiteSpace(path) ? null : path.Trim();
        }
    }

    /// <summary>
    /// Liest den wirklichen Registry-Zustand (die einzige Wahrheit fuer diesen Schalter).
    /// Ein Eintrag, dessen EXE nicht mehr existiert - etwa nach dem Umziehen einer
    /// Portables-Version - wird dabei auf den aktuellen Pfad geschrieben: Der Autostart
    /// ueberlebt so das Verschieben, statt still ins Leere zu zeigen.
    /// </summary>
    public static bool IsEnabled()
    {
        try
        {
            using (RegistryKey? key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: false))
            {
                object? raw = key?.GetValue(ValueName);
                if (raw is null) return false;

                if (raw is string value) RepairStaleEntry(value);
                return true;
            }
        }
        catch (Exception ex)
        {
            LogQuiet("read failed: " + Describe(ex));
            return false;
        }
    }

    /// <summary>
    /// Setzt den gewuenschten Zustand: true = Run-Eintrag zeigt auf die aktuell laufende EXE,
    /// false = Eintrag wird entfernt. Gibt nur true, wenn der Zustand danach wirklich erreicht ist.
    /// </summary>
    public static bool SetEnabled(bool enabled)
    {
        try
        {
            if (enabled)
            {
                string? exe = ExePath;
                if (exe is null || !File.Exists(exe))
                {
                    LogQuiet("write skipped: no accessible exe path");
                    return false;
                }

                using (RegistryKey? key = Registry.CurrentUser.CreateSubKey(RunKeyPath))
                {
                    if (key is null)
                    {
                        LogQuiet("write failed: run key is not accessible");
                        return false;
                    }

                    key.SetValue(ValueName, Quote(exe), RegistryValueKind.String);
                }

                LogQuiet("autostart on -> " + exe);
                return IsEnabled();
            }

            using (RegistryKey? key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: true))
            {
                if (key?.GetValue(ValueName) is not null)
                    key.DeleteValue(ValueName, throwOnMissingValue: false);
            }

            LogQuiet("autostart off");
            return !IsEnabled();
        }
        catch (Exception ex)
        {
            LogQuiet((enabled ? "write" : "remove") + " failed: " + Describe(ex));
            return false;
        }
    }

    /// <summary>
    /// Der Eintrag zeigt auf eine EXE, die es nicht mehr gibt. Umgeschrieben wird nur in diesem
    /// Fall: Zeigt der Eintrag auf eine noch existierende Fassung (z. B. die installierte Version,
    /// waehrend gerade die Portables laeuft), bleibt er absichtlich unangetastet.
    /// </summary>
    private static void RepairStaleEntry(string registeredValue)
    {
        string? registered = Unquote(registeredValue);
        if (registered is null || File.Exists(registered)) return;

        string? current = ExePath;
        if (current is null || !File.Exists(current)) return;

        try
        {
            using (RegistryKey? key = Registry.CurrentUser.CreateSubKey(RunKeyPath))
            {
                key?.SetValue(ValueName, Quote(current), RegistryValueKind.String);
            }

            LogQuiet("stale entry \"" + registered + "\" replaced by " + current);
        }
        catch (Exception ex)
        {
            LogQuiet("repair failed: " + Describe(ex));
        }
    }

    /// <summary>Der Pfad wird in Anfuehrungszeichen gesetzt - er darf Leerzeichen enthalten.</summary>
    private static string Quote(string path) => "\"" + path.Trim() + "\"";

    private static string? Unquote(string? raw)
    {
        string value = (raw ?? string.Empty).Trim();

        if (value.Length >= 2 && value[0] == '"' && value[^1] == '"')
            value = value.Substring(1, value.Length - 2);

        return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    }

    /// <summary>Kuerzeste aussagekraeftige Fehlerkette (Rechte, Gruppenrichtlinie, Registry).</summary>
    private static string Describe(Exception ex)
    {
        System.Text.StringBuilder sb = new();
        Exception? cur = ex;

        for (int i = 0; i < 3 && cur is not null; i++)
        {
            if (i > 0) sb.Append(" <- ");
            sb.Append(cur.GetType().Name).Append(": ").Append(cur.Message);
            cur = cur.InnerException;
        }

        return sb.ToString();
    }

    /// <summary>Diagnosebesteffe - Logging darf selbst nie storen.</summary>
    private static void LogQuiet(string message)
    {
        try
        {
            Directory.CreateDirectory(AppConfigService.DirectoryPath);
            File.AppendAllText(
                Path.Combine(AppConfigService.DirectoryPath, "error.log"),
                DateTime.Now.ToString("s") + "  [autostart]  " + message + Environment.NewLine);
        }
        catch
        {
            // absichtlich still
        }
    }
}
