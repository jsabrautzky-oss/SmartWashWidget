using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

using SmartWashWidget.Models;

namespace SmartWashWidget.Services;

/// <summary>Ergebnis des 1-Klick-Setups inkl. der erkannten Geraete.</summary>
public sealed record OAuthSetupResult(
    List<DiscoveredDevice> Devices,
    DiscoveredDevice? Washer,
    DiscoveredDevice? Dryer);

/// <summary>
/// 1-Klick-Verbindung ueber die offizielle SmartThings-CLI ("CLI-Bridge"):
/// 'smartthings.exe devices -j' oeffnet beim ersten Mal automatisch den offiziellen
/// Samsung-Browser-Login, verwaltet das OAuth-Token samt Auto-Refresh selbst in
/// credentials.json und liefert die komplette Geraeteliste als JSON zurueck.
/// Wir lesen den vom CLI verwalteten Access-Token daraus, legen ihn DPAPI-verschluesselt
/// in unsere config.json (damit der SmartThingsClient ihn nutzt) und erkennen automatisch
/// Waschmaschine und Trockner. Fuer die Laienfaelle ist damit kein Client-ID, kein
/// Client-Secret und keine eigene App-Erstellung noetig.
/// </summary>
public sealed class SmartThingsOAuthSetupService : IDisposable
{
    // Erst die vom Auftrag genannte Datei, dann der aktuelle Name des offiziellen Releases.
    private static readonly string[] CliZipCandidates =
    {
        "https://github.com/SmartThingsCommunity/smartthings-cli/releases/latest/download/smartthings-win-x64.zip",
        "https://github.com/SmartThingsCommunity/smartthings-cli/releases/latest/download/smartthings-windows-x64.zip"
    };

    private static readonly string[] CliExeCandidates =
    {
        "https://github.com/SmartThingsCommunity/smartthings-cli/releases/latest/download/smartthings-win-x64.exe",
        "https://github.com/SmartThingsCommunity/smartthings-cli/releases/latest/download/smartthings-windows-x64.exe"
    };

    private static readonly string[] WasherCapabilities =
    {
        "washerOperatingState", "washerMode", "washerCycle", "custom.washerOperatingState"
    };

    private static readonly string[] DryerCapabilities =
    {
        "dryerOperatingState", "dryerMode", "dryerCycle", "custom.dryerOperatingState"
    };

    private readonly AppConfigService _config;
    private readonly Action<string> _status;
    private readonly HttpClient _http;

    public SmartThingsOAuthSetupService(AppConfigService config, Action<string> status)
    {
        _config = config ?? throw new ArgumentNullException(nameof(config));
        _status = status ?? throw new ArgumentNullException(nameof(status));

        _http = new HttpClient(new HttpClientHandler
        {
            AutomaticDecompression = System.Net.DecompressionMethods.GZip | System.Net.DecompressionMethods.Deflate
        })
        {
            Timeout = TimeSpan.FromSeconds(120)
        };
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("SmartWashWidget/1.0");
    }

    public static string ToolsExePath => Path.Combine(AppConfigService.DirectoryPath, "tools", "smartthings.exe");

    /// <summary>Verbinder per CLI-Bridge: CLI downloaden, "devices -j" ausfuehren (triggert den
    /// Browser-Login beim ersten Mal), CLI-Token uebernehmen und Geraete automatisch zuordnen.</summary>
    public async Task<OAuthSetupResult> ConnectAsync(CancellationToken ct)
    {
        // 0) Haengengebliebene Assistenten aus frueheren Versuchen beenden.
        CleanupStaleCliProcesses();

        // 1) CLI sicherstellen
        if (!File.Exists(ToolsExePath))
        {
            _status(LocalizationManager.Get("Str_OAuth_Download"));
            await EnsureCliAsync(ct).ConfigureAwait(false);
        }

        // 2) Geraeteliste laden - oeffnet beim allerersten Mal den offiziellen Samsung-Browser-Login
        _status(LocalizationManager.Get("Str_OAuth_BrowserLogin"));
        (string stdout, string stderr, int exit) = await RunCliAsync("devices -j", ct).ConfigureAwait(false);

        if (exit != 0)
        {
            string detail = Summarize(stderr, stdout);
            throw new SmartThingsException(
                "samsung setup assistant failed: " + detail,
                detail: detail)
            { TextKey = "Str_OAuth_AssistantFailed", TextArgs = new object?[] { detail } };
        }

        List<DiscoveredDevice> found = ParseDevices(stdout);
        if (found.Count == 0)
            throw new SmartThingsException("no devices found in the smartthings account")
            { TextKey = "Str_OAuth_NoDevices" };

        // 3) CLI-Access-Token in die eigene (DPAPI-)Konfiguration uebernehmen
        string? token = SmartThingsCliAuth.ReadAccessToken();
        if (string.IsNullOrWhiteSpace(token))
            throw new SmartThingsException("could not read the samsung cli access token")
            { TextKey = "Str_OAuth_TokenReadFailed" };

        _config.SetToken(token!);
        _config.Config.SmartThings.UseCliManagedToken = true;
        _config.Save();

        // 4) Waschmaschine & Trockner automatisch erkennen und zuordnen, Demo beenden
        DiscoveredDevice? washer = found.FirstOrDefault(d => d.HasWasherCapability || d.InferredKind == MachineKind.Washer);
        DiscoveredDevice? dryer = found.FirstOrDefault(d => d.HasDryerCapability || d.InferredKind == MachineKind.Dryer);

        if (washer is not null)
        {
            _config.Config.SmartThings.WasherDeviceId = washer.Id;
            _config.Config.SmartThings.WasherLabel = washer.Display;
        }
        if (dryer is not null)
        {
            _config.Config.SmartThings.DryerDeviceId = dryer.Id;
            _config.Config.SmartThings.DryerLabel = dryer.Display;
        }
        _config.Config.DemoMode = false;
        _config.Save();

        _status(LocalizationManager.Get("Str_OAuth_Success"));
        return new OAuthSetupResult(found, washer, dryer);
    }

    // ---------------- CLI herunterladen ----------------

    private async Task EnsureCliAsync(CancellationToken ct)
    {
        if (File.Exists(ToolsExePath)) return;
        Directory.CreateDirectory(Path.GetDirectoryName(ToolsExePath)!);

        // 1) ZIP laden und entpacken.
        foreach (string zipUrl in CliZipCandidates)
        {
            try
            {
                await DownloadZipAndExtractAsync(zipUrl, ct).ConfigureAwait(false);
                if (File.Exists(ToolsExePath)) return;
            }
            catch (SmartThingsException ex)
            {
                // Bereits uebersetzte Fachmeldung (TextKey) weiterreichen.
                _status(LocalizationManager.Format("Str_OAuth_DownloadRetry", ex.UserText));
            }
            catch (Exception ex)
            {
                _status(LocalizationManager.Format("Str_OAuth_DownloadRetry", Short(ex.Message)));
            }
        }

        // 2) Fallback: direkte EXE.
        foreach (string exeUrl in CliExeCandidates)
        {
            try
            {
                _status(LocalizationManager.Get("Str_OAuth_DownloadDirect"));
                await DownloadFileAsync(exeUrl, ToolsExePath, ct).ConfigureAwait(false);
                if (File.Exists(ToolsExePath)) return;
            }
            catch
            {
                // Weitermachen mit naechstem Kandidaten.
            }
        }

        throw new SmartThingsException(
            "could not download the samsung setup assistant")
        { TextKey = "Str_OAuth_DownloadFailed" };
    }

    private async Task DownloadZipAndExtractAsync(string url, CancellationToken ct)
    {
        string zip = Path.Combine(Path.GetTempPath(), "smartthings-cli-" + Guid.NewGuid().ToString("N") + ".zip");
        string dir = Path.Combine(Path.GetTempPath(), "smartthings-cli-" + Guid.NewGuid().ToString("N"));

        try
        {
            await DownloadFileAsync(url, zip, ct).ConfigureAwait(false);
            ZipFile.ExtractToDirectory(zip, dir);

            string? exe = Directory.EnumerateFiles(dir, "smartthings.exe", SearchOption.AllDirectories).FirstOrDefault();
            if (exe is null)
            {
                throw new SmartThingsException("zip does not contain smartthings.exe")
                { TextKey = "Str_OAuth_ZipMissingExe" };
            }

            File.Copy(exe, ToolsExePath, overwrite: true);
        }
        finally
        {
            try { if (File.Exists(zip)) File.Delete(zip); } catch { /* egal */ }
            try { if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true); } catch { /* egal */ }
        }
    }

    private async Task DownloadFileAsync(string url, string destPath, CancellationToken ct)
    {
        using HttpResponseMessage resp = await _http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct)
            .ConfigureAwait(false);
        resp.EnsureSuccessStatusCode();

        using FileStream fs = new FileStream(destPath, FileMode.Create, FileAccess.Write, FileShare.None);
        await resp.Content.CopyToAsync(fs, ct).ConfigureAwait(false);
    }

    // ---------------- CLI ausfuehren ----------------

    private static async Task<(string Stdout, string Stderr, int ExitCode)> RunCliAsync(string arguments, CancellationToken ct)
    {
        var psi = new ProcessStartInfo
        {
            FileName = ToolsExePath,
            Arguments = arguments,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            WorkingDirectory = Path.GetDirectoryName(ToolsExePath) ?? AppConfigService.DirectoryPath
        };

        Process? process;
        try
        {
            process = Process.Start(psi);
        }
        catch (Exception ex)
        {
            throw new SmartThingsException("cannot start the samsung setup assistant: " + Short(ex.Message))
            { TextKey = "Str_OAuth_StartFailed", TextArgs = new object?[] { Short(ex.Message) } };
        }

        if (process is null)
            throw new SmartThingsException("cannot start the samsung setup assistant")
            { TextKey = "Str_OAuth_StartFailedPlain" };

        // Stdin sofort schliessen (EOF), damit ein interaktiver Prompt nicht ewig haengen bleibt.
        try { process.StandardInput.Close(); } catch { /* egal */ }

        Task<(string Stdout, string Stderr)> readTask = ReadOutputsAsync(process, ct);

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(TimeSpan.FromMinutes(4));

        bool exited = await WaitForExitAsync(process, timeoutCts.Token).ConfigureAwait(false);
        if (!exited)
        {
            // Login/Prozess haengt: sauber beenden und nachvollziehbar abbrechen.
            KillTree(process);
            throw new SmartThingsException("samsung setup assistant did not respond in time")
            { TextKey = "Str_OAuth_Timeout" };
        }

        (string stdout, string stderr) = await readTask.ConfigureAwait(false);
        process.WaitForExit();
        return (stdout, stderr, process.ExitCode);
    }

    /// <summary>Wartet auf Prozessende bis zum Token; true = beendet, false = Timeout/Abbruch.</summary>
    private static async Task<bool> WaitForExitAsync(Process process, CancellationToken ct)
    {
        if (process.HasExited) return true;
        try
        {
            await process.WaitForExitAsync(ct).ConfigureAwait(false);
            return true;
        }
        catch (OperationCanceledException) { return false; }
        catch (InvalidOperationException) { return true; }
    }

    private static void KillTree(Process process)
    {
        try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch { /* egal */ }
        try { process.Dispose(); } catch { /* egal */ }
    }

    /// <summary>Beendet Assistenten-Prozesse, die aus frueheren Versuchen haengen geblieben sind.</summary>
    private static void CleanupStaleCliProcesses()
    {
        try
        {
            foreach (Process p in Process.GetProcessesByName("smartthings"))
            {
                try { if (!p.HasExited) p.Kill(entireProcessTree: true); } catch { /* egal */ }
            }
        }
        catch
        {
            // Aufraeumen ist optional.
        }
    }

    /// <summary>Liest stdout und stderr parallel, damit kein Pipe-Puffer den Prozess blockiert.</summary>
    private static async Task<(string Stdout, string Stderr)> ReadOutputsAsync(Process process, CancellationToken ct)
    {
        Task<string> stdoutTask = process.StandardOutput.ReadToEndAsync(ct);
        Task<string> stderrTask = process.StandardError.ReadToEndAsync(ct);
        await Task.WhenAll(stdoutTask, stderrTask).ConfigureAwait(false);
        return (await stdoutTask.ConfigureAwait(false), await stderrTask.ConfigureAwait(false));
    }

    // ---------------- Geraete aus der CLI-Ausgabe parsen ----------------

    private static List<DiscoveredDevice> ParseDevices(string stdout)
    {
        var result = new List<DiscoveredDevice>();
        if (string.IsNullOrWhiteSpace(stdout)) return result;

        using JsonDocument doc = JsonDocument.Parse(stdout, new JsonDocumentOptions
        {
            CommentHandling = JsonCommentHandling.Skip,
            AllowTrailingCommas = true
        });
        if (doc.RootElement.ValueKind != JsonValueKind.Array) return result;

        foreach (JsonElement item in doc.RootElement.EnumerateArray())
        {
            string id = JsonString(item, "deviceId") ?? JsonString(item, "id") ?? string.Empty;
            if (string.IsNullOrWhiteSpace(id)) continue;

            string label = CleanText(JsonString(item, "label") ?? JsonString(item, "name"));
            string? type = JsonString(item, "type") ?? JsonString(item, "deviceType")
                           ?? NestedString(item, "ocf", "ocfDeviceType");

            bool washer = false, dryer = false;
            foreach (string capId in EnumerateCapabilityIds(item))
            {
                if (!washer && MatchesCapability(capId, WasherCapabilities)) washer = true;
                if (!dryer && MatchesCapability(capId, DryerCapabilities)) dryer = true;
            }
            foreach (string cat in EnumerateCategories(item))
            {
                if (!washer && string.Equals(cat, "Washer", StringComparison.OrdinalIgnoreCase)) washer = true;
                if (!dryer && string.Equals(cat, "Dryer", StringComparison.OrdinalIgnoreCase)) dryer = true;
            }

            MachineKind? kind = KindFromText(label) ?? KindFromText(type);
            if (!washer && kind == MachineKind.Washer) washer = true;
            if (!dryer && kind == MachineKind.Dryer) dryer = true;

            result.Add(new DiscoveredDevice(id, label, type, kind, washer, dryer));
        }

        result.Sort((a, b) => string.Compare(a.Display, b.Display, StringComparison.CurrentCultureIgnoreCase));
        return result;
    }

    /// <summary>Capability-IDs aus components[].capabilities[].id.</summary>
    private static IEnumerable<string> EnumerateCapabilityIds(JsonElement deviceItem)
    {
        if (!deviceItem.TryGetProperty("components", out JsonElement comps) || comps.ValueKind != JsonValueKind.Array)
            yield break;

        foreach (JsonElement comp in comps.EnumerateArray())
        {
            if (!comp.TryGetProperty("capabilities", out JsonElement caps) || caps.ValueKind != JsonValueKind.Array)
                continue;

            foreach (JsonElement cap in caps.EnumerateArray())
            {
                string? id = cap.ValueKind == JsonValueKind.String ? cap.GetString() : JsonString(cap, "id");
                if (!string.IsNullOrWhiteSpace(id)) yield return id!;
            }
        }
    }

    /// <summary>Kategorien aus components[].categories[].name.</summary>
    private static IEnumerable<string> EnumerateCategories(JsonElement deviceItem)
    {
        if (!deviceItem.TryGetProperty("components", out JsonElement comps) || comps.ValueKind != JsonValueKind.Array)
            yield break;

        foreach (JsonElement comp in comps.EnumerateArray())
        {
            if (!comp.TryGetProperty("categories", out JsonElement cats) || cats.ValueKind != JsonValueKind.Array)
                continue;

            foreach (JsonElement cat in cats.EnumerateArray())
            {
                string? name = JsonString(cat, "name");
                if (!string.IsNullOrWhiteSpace(name)) yield return name!;
            }
        }
    }

    private static bool MatchesCapability(string candidate, string[] capabilityNames)
    {
        foreach (string name in capabilityNames)
        {
            if (string.Equals(candidate, name, StringComparison.OrdinalIgnoreCase)) return true;
            if (candidate.EndsWith("." + name, StringComparison.OrdinalIgnoreCase)) return true;
        }
        return false;
    }

    private static MachineKind? KindFromText(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        string t = text.ToLowerInvariant();

        if (t.Contains("washer") || t.Contains("washing") || t.Contains("wasch")) return MachineKind.Washer;
        if (t.Contains("dryer") || t.Contains("trockn") || t.Contains("dry ")) return MachineKind.Dryer;
        return null;
    }

    /// <summary>Entfernt versteckte Formatzeichen (Weicher Bindestrich U+00AD, Zero-Width-Zeichen).</summary>
    private static string CleanText(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return string.Empty;

        string t = raw
            .Replace("\u00AD", string.Empty)
            .Replace("\u200B", string.Empty)
            .Replace("\u200C", string.Empty)
            .Replace("\uFEFF", string.Empty);
        return t.Trim();
    }

    private static string? JsonString(JsonElement el, string key)
    {
        if (el.ValueKind != JsonValueKind.Object) return null;
        if (el.TryGetProperty(key, out JsonElement v))
        {
            return v.ValueKind switch
            {
                JsonValueKind.String => v.GetString(),
                JsonValueKind.Number => v.ToString(),
                JsonValueKind.True => "true",
                JsonValueKind.False => "false",
                _ => null
            };
        }
        foreach (JsonProperty prop in el.EnumerateObject())
            if (string.Equals(prop.Name, key, StringComparison.OrdinalIgnoreCase) && prop.Value.ValueKind == JsonValueKind.String)
                return prop.Value.GetString();
        return null;
    }

    private static string? NestedString(JsonElement el, string section, string key)
    {
        if (el.ValueKind != JsonValueKind.Object || !el.TryGetProperty(section, out JsonElement sec))
            return null;
        return JsonString(sec, key);
    }

    private static string Summarize(string? primary, string? secondary = null)
    {
        string raw = string.IsNullOrWhiteSpace(primary) ? (secondary ?? string.Empty) : primary;
        return Short(raw);
    }

    private static string Short(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return LocalizationManager.Get("Str_Err_Unknown");
        string clean = text.Replace("\r", " ").Replace("\n", " ").Trim();
        return clean.Length > 170 ? string.Concat(clean.AsSpan(0, 170), "\u2026") : clean;
    }

    public void Dispose()
    {
        _http.Dispose();
    }
}