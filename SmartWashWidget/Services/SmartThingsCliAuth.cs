using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace SmartWashWidget.Services;

/// <summary>
/// Bruecke zur offiziellen SmartThings-CLI ("CLI-Bridge"):
/// Die CLI verwaltet ihr eigenes OAuth2-Token (inkl. automatischem Refresh-Token) in
/// %LOCALAPPDATA%\@smartthings\cli\Data\credentials.json. Wir lesen den Access-Token
/// daraus aus und lassen die CLI bei Bedarf (401 bzw. kurz vor Ablauf) einmal "devices -j"
/// aufrufen - das erneuert das CLI-Token dann selbststaendig, ganz ohne eigene Client-ID
/// und ohne dass der Nutzer etwas tun muss.
/// </summary>
public static class SmartThingsCliAuth
{
    public static string ToolsExePath => Path.Combine(AppConfigService.DirectoryPath, "tools", "smartthings.exe");

    public static string CredentialsPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "@smartthings", "cli", "Data", "credentials.json");

    /// <summary>Liest den Access-Token aus dem Default-Profil der CLI-credentials.json.</summary>
    public static string? ReadAccessToken()
    {
        try
        {
            if (!File.Exists(CredentialsPath)) return null;

            using JsonDocument doc = JsonDocument.Parse(File.ReadAllText(CredentialsPath));
            string? token = FindProfileValue(doc.RootElement, "accessToken");
            return string.IsNullOrWhiteSpace(token) ? null : token;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Ablaufzeitpunkt des CLI-Tokens (UTC) oder null wenn unbekannt.</summary>
    public static DateTime? ReadExpiryUtc()
    {
        try
        {
            if (!File.Exists(CredentialsPath)) return null;

            using JsonDocument doc = JsonDocument.Parse(File.ReadAllText(CredentialsPath));
            string? raw = FindProfileValue(doc.RootElement, "expires");
            if (string.IsNullOrWhiteSpace(raw)) return null;

            if (DateTimeOffset.TryParse(
                    raw, System.Globalization.CultureInfo.InvariantCulture,
                    System.Globalization.DateTimeStyles.AssumeUniversal | System.Globalization.DateTimeStyles.AdjustToUniversal,
                    out DateTimeOffset stamp))
                return stamp.ToUniversalTime().UtcDateTime;

            return null;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Laesst die CLI einmal "devices -j" ausfuehren. Das erneuert dabei das CLI-eigene
    /// OAuth2-Token automatisch (und wuerde beim allerersten Mal den Browser-Login oeffnen).
    /// </summary>
    public static async Task RefreshByListingDevicesAsync(CancellationToken ct)
    {
        if (!File.Exists(ToolsExePath))
            throw new SmartThingsException(
                "samsung setup assistant executable is missing")
            { TextKey = "Str_OAuth_AssistantMissing" };

        var psi = new ProcessStartInfo
        {
            FileName = ToolsExePath,
            Arguments = "devices -j",
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            WorkingDirectory = Path.GetDirectoryName(ToolsExePath) ?? AppConfigService.DirectoryPath
        };

        Process process;
        try
        {
            process = Process.Start(psi)
                ?? throw new SmartThingsException("cannot start the samsung setup assistant")
                { TextKey = "Str_OAuth_StartFailedPlain" };
        }
        catch (SmartThingsException) { throw; }
        catch (Exception ex)
        {
            throw new SmartThingsException("cannot start the samsung setup assistant: " + ex.Message)
            { TextKey = "Str_OAuth_StartFailed", TextArgs = new object?[] { ex.Message } };
        }

        try { process.StandardInput.Close(); } catch { /* egal */ }

        Task<string> stdoutTask = process.StandardOutput.ReadToEndAsync(CancellationToken.None);
        Task<string> stderrTask = process.StandardError.ReadToEndAsync(CancellationToken.None);

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(TimeSpan.FromMinutes(3));

        try
        {
            bool exited = true;
            try
            {
                await process.WaitForExitAsync(timeoutCts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                exited = false;
            }

            if (!exited)
            {
                // Hintergrund-Refresh: still aufgeben statt laestig zu scheitern.
                try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch { /* egal */ }
                return;
            }

            await Task.WhenAll(stdoutTask, stderrTask).ConfigureAwait(false);
        }
        finally
        {
            try { process.Dispose(); } catch { /* egal */ }
        }
    }

    private static string? FindProfileValue(JsonElement root, string key)
    {
        if (root.ValueKind != JsonValueKind.Object) return null;

        // Beliebiges Profil (ueblicherweise "default:api.smartthings.com") durchsuchen.
        foreach (JsonProperty profile in root.EnumerateObject())
        {
            if (profile.Value.ValueKind != JsonValueKind.Object) continue;
            foreach (JsonProperty prop in profile.Value.EnumerateObject())
            {
                if (string.Equals(prop.Name, key, StringComparison.OrdinalIgnoreCase) &&
                    prop.Value.ValueKind == JsonValueKind.String)
                    return prop.Value.GetString();
            }
        }
        return null;
    }
}