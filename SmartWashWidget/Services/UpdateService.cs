using System;
using System.Globalization;
using System.IO;
using System.Net.Http;
using System.Reflection;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace SmartWashWidget.Services;

/// <summary>
/// Ein Release aus der offiziellen GitHub-Releases-API. Enthaelt nur die Felder, die die
/// Anzeige braucht: die aus tag_name gewonnene Version, die Release-Seite und die
/// Download-URL des passendsten Assets.
/// </summary>
public sealed class UpdateInfo
{
    public UpdateInfo(Version version, string display, string releaseUrl, string? downloadUrl)
    {
        Version = version;
        Display = display;
        ReleaseUrl = releaseUrl;
        DownloadUrl = downloadUrl;
    }

    /// <summary>Die aus tag_name gewonnene Version (z. B. 1.1.0.0).</summary>
    public Version Version { get; }

    /// <summary>Version textlich, ohne Revision wenn sie 0 ist (z. B. "1.1.0").</summary>
    public string Display { get; }

    /// <summary>Die Release-Seite bei GitHub (html_url) - fuer den Standard-Browser.</summary>
    public string ReleaseUrl { get; }

    /// <summary>browser_download_url des passendsten Assets (Setup, sonst Portable, sonst erstes Asset).</summary>
    public string? DownloadUrl { get; }
}

/// <summary>Ergebnis eines einzelnen Update-Checks.</summary>
public enum UpdateCheckStatus
{
    /// <summary>Das Release ist identisch oder aelter als die eigene Version.</summary>
    UpToDate,

    /// <summary>Ein neueres Release ist verfuegbar.</summary>
    UpdateAvailable,

    /// <summary>Check nicht moeglich (kein Internet, HTTP-Fehler, unlesbare Antwort).</summary>
    Failed,

    /// <summary>Check abgebrochen (App-Ende) - weder Erfolg noch Fehler.</summary>
    Cancelled
}

/// <summary>Check-Ergebnis inkl. der zuletzt gelesenen Release-Information.</summary>
public sealed record UpdateCheckResult(UpdateCheckStatus Status, UpdateInfo? Info);

/// <summary>
/// Leichtgewichtiger Update-Check gegen die offizielle GitHub-Releases-API dieses Projekts.
///
/// - ein Request pro Check, keine Anmeldung, kein Token, es werden keine Daten hochgeladen
/// - GitHub verlangt zwingend User-Agent und den Accept-Header der GitHub-API,
///   ohne sie antwortet GitHub mit 403
/// - laeuft 5 s nach dem Start und danach alle 4 Stunden in einer PeriodicTimer-Schleife,
///   solange AppConfig.AutoUpdateCheck aktiv ist
/// - Netzwerkstoerungen (kein Internet nach Standby, DNS-, Zertifikats- oder Proxy-Probleme)
///   werden lautlos geschluckt: die App laeuft einfach weiter
/// </summary>
public sealed class UpdateService : IDisposable
{
    /// <summary>API-Endpunkt fuer das neueste Release des Projekts.</summary>
    public const string RepoApiUrl =
        "https://api.github.com/repos/jsabrautzky-oss/SmartWashWidget/releases/latest";

    /// <summary>Ansichtsseite aller Releases (Ausweichziel, wenn html_url fehlt).</summary>
    public const string ReleasesPageUrl =
        "https://github.com/jsabrautzky-oss/SmartWashWidget/releases";

    /// <summary>Erster Check so viele Sekunden nach dem App-Start.</summary>
    public static readonly TimeSpan StartupDelay = TimeSpan.FromSeconds(5);

    /// <summary>Abstand der Hintergrund-Checks nach dem ersten Check.</summary>
    public static readonly TimeSpan Interval = TimeSpan.FromHours(4);

    /// <summary>GitHub antwortet ohne User-Agent mit 403 - dieser Wert ist Pflicht.</summary>
    private const string UserAgent = "SmartWashWidget";

    /// <summary>Der Accept-Header der GitHub-API - ohne ihn gilt die Anfrage als fremd.</summary>
    private const string AcceptHeader = "application/vnd.github+json";

    private static readonly HttpClient Http = CreateHttpClient();

    private readonly AppConfigService _config;
    private readonly object _gate = new object();

    private CancellationTokenSource? _cts;
    private Task? _loopTask;

    private UpdateInfo? _latest;
    private bool _lastCheckFailed;
    private DateTime? _lastCheckUtc;

    /// <summary>Die eigene Version, verglichen mit GitHub (aus &lt;Version&gt; der csproj).</summary>
    public Version CurrentVersion { get; } = ReadAssemblyVersion();

    /// <summary>
    /// Feuert, wenn ein Check ein neueres Release gefunden hat. Kommt aus dem
    /// Hintergrund-Thread - die UI muss sich den Wert per Dispatcher holen.
    /// </summary>
    public event Action<UpdateInfo>? UpdateFound;

    public UpdateService(AppConfigService config)
    {
        _config = config ?? throw new ArgumentNullException(nameof(config));
    }

    /// <summary>Das neueste Release, das bisher erfolgreich gelesen wurde (null = noch keiner).</summary>
    public UpdateInfo? Latest
    {
        get { lock (_gate) return _latest; }
    }

    /// <summary>true, wenn der letzte Check fehlschlug (und seitdem kein Erfolg kam).</summary>
    public bool LastCheckFailed
    {
        get { lock (_gate) return _latest is null && _lastCheckFailed; }
    }

    /// <summary>Zeitpunkt des letzten erfolgreichen Checks (UTC) oder null.</summary>
    public DateTime? LastCheckUtc
    {
        get { lock (_gate) return _lastCheckUtc; }
    }

    /// <summary>true, wenn das letzte gelesene Release neuer ist als die eigene Version.</summary>
    public bool UpdateAvailable
    {
        get
        {
            UpdateInfo? info = Latest;
            return info is not null && info.Version > CurrentVersion;
        }
    }

    /// <summary>Die eigene Version textlich (z. B. "1.0.0").</summary>
    public string CurrentDisplay => ShortVersion(CurrentVersion);

    public bool IsLooping
    {
        get { lock (_gate) return _loopTask is { IsCompleted: false }; }
    }

    // ---------------- Hintergrund-Schleife ----------------

    /// <summary>
    /// Startet die Hintergrund-Schleife (einmal 5 s nach dem Start, danach alle 4 h).
    /// Ist AutoUpdateCheck deaktiviert, bleibt die Schleife billig stehen und prueft erst
    /// wieder, sobald der Nutzer die Option in den Einstellungen einschalaltet.
    /// </summary>
    public void Start()
    {
        lock (_gate)
        {
            if (_loopTask is { IsCompleted: false }) return;

            _cts = new CancellationTokenSource();
            _loopTask = Task.Run(() => RunLoopAsync(_cts.Token));
        }
    }

    /// <summary>Stoppt die Schleife; bereits gefundene Release-Informationen bleiben erhalten.</summary>
    public void Stop()
    {
        lock (_gate)
        {
            try { _cts?.Cancel(); } catch { /* egal */ }
        }
    }

    private async Task RunLoopAsync(CancellationToken ct)
    {
        try
        {
            await Task.Delay(StartupDelay, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        using var timer = new PeriodicTimer(Interval);

        do
        {
            if (_config.Config.AutoUpdateCheck)
                await CheckAsync(ct).ConfigureAwait(false);
        }
        while (await WaitForNextTickAsync(timer, ct).ConfigureAwait(false));
    }

    private static async Task<bool> WaitForNextTickAsync(PeriodicTimer timer, CancellationToken ct)
    {
        try
        {
            return await timer.WaitForNextTickAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return false;
        }
    }

    // ---------------- Einzelner Check ----------------

    /// <summary>
    /// Ein Check: liest tag_name, html_url und browser_download_url des neuesten Releases.
    /// Liefert nie eine Ausnahme - ein misglueckter Check kommt als UpdateCheckStatus.Failed
    /// zurueck, damit weder Poller noch Animation davon beruehrt werden.
    /// </summary>
    public async Task<UpdateCheckResult> CheckAsync(CancellationToken ct = default)
    {
        try
        {
            using HttpResponseMessage response = await Http
                .GetAsync(RepoApiUrl, HttpCompletionOption.ResponseHeadersRead, ct)
                .ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
                return Fail($"HTTP {(int)response.StatusCode} {response.ReasonPhrase}");

            using Stream stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            using JsonDocument doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct).ConfigureAwait(false);

            UpdateInfo? info = ParseRelease(doc.RootElement);
            if (info is null)
                return Fail("release-Antwort ohne tag_name");

            lock (_gate)
            {
                _latest = info;
                _lastCheckFailed = false;
                _lastCheckUtc = DateTime.UtcNow;
            }

            if (info.Version > CurrentVersion)
            {
                UpdateFound?.Invoke(info);
                return new UpdateCheckResult(UpdateCheckStatus.UpdateAvailable, info);
            }

            return new UpdateCheckResult(UpdateCheckStatus.UpToDate, info);
        }
        catch (HttpRequestException)
        {
            // Kein Internet nach Standby / kein DNS / Proxy verbindet GitHub nicht.
            return Fail(string.Empty);
        }
        catch (TaskCanceledException)
        {
            // Zeitueberschreitung des Requests (Timeout des HttpClient). TaskCanceledException
            // muss vor OperationCanceledException stehen, sonst ist sie unerreichbar.
            return Fail(string.Empty);
        }
        catch (OperationCanceledException)
        {
            // App-Ende oder Abbruch von Hand: weder Erfolg noch Fehler.
            return new UpdateCheckResult(UpdateCheckStatus.Cancelled, Latest);
        }
        catch (JsonException ex)
        {
            return Fail("JSON: " + ex.Message);
        }
        catch (Exception ex)
        {
            // Nichts davon darf die App zu Fall bringen. Ueberraschendes (kaputer Header,
            // abgelehnte TLS-Einstellung, initialisierungsfehler) kommt mit Grund in die Diagnose.
            return Fail(Describe(ex));
        }
    }

    /// <summary>Ausnahme textlich, innerste Ursache zuerst - max. drei Glieder der Kette.</summary>
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

    /// <summary>
    /// Merk den Fehlschlag, ohne das zuletzt erfolgreich gelesene Release wegzuwerfen.
    /// Netzwerkstoerungen bleiben voellig still; nur echtes Datenglueltigkeits-Problem
    /// (HTTP-Status, kaputtes JSON) hinterlaesst eine Zeile in error.log.
    /// </summary>
    private UpdateCheckResult Fail(string detail)
    {
        lock (_gate) _lastCheckFailed = true;

        if (!string.IsNullOrEmpty(detail))
            LogQuiet("check " + detail);

        return new UpdateCheckResult(UpdateCheckStatus.Failed, Latest);
    }

    /// <summary>Diagnosebesteffe - Logging darf selbst nie storen.</summary>
    private static void LogQuiet(string message)
    {
        try
        {
            Directory.CreateDirectory(AppConfigService.DirectoryPath);
            File.AppendAllText(
                Path.Combine(AppConfigService.DirectoryPath, "error.log"),
                DateTime.Now.ToString("s") + "  [update]  " + message + Environment.NewLine);
        }
        catch
        {
            // absichtlich still
        }
    }

    // ---------------- JSON lesen ----------------

    private static UpdateInfo? ParseRelease(JsonElement root)
    {
        Version? version = ParseTag(ReadString(root, "tag_name"));
        if (version is null) return null;

        string releaseUrl = ReadString(root, "html_url") ?? ReleasesPageUrl;
        string? download = PickAssetUrl(root);

        return new UpdateInfo(version, ShortVersion(version), releaseUrl, download);
    }

    /// <summary>
     /// Auswahl des Download-Assets: erst das Windows-Setup (.exe), dann die portable ZIP,
     /// sonst das erste Asset mit Download-URL.
    /// </summary>
    private static string? PickAssetUrl(JsonElement root)
    {
        if (!root.TryGetProperty("assets", out JsonElement assets) || assets.ValueKind != JsonValueKind.Array)
            return null;

        string? setup = null;
        string? portable = null;
        string? any = null;

        foreach (JsonElement asset in assets.EnumerateArray())
        {
            if (asset.ValueKind != JsonValueKind.Object) continue;

            string? url = ReadString(asset, "browser_download_url");
            if (string.IsNullOrWhiteSpace(url)) continue;

            string name = (ReadString(asset, "name") ?? string.Empty).Trim();

            if (setup is null && name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) setup = url;
            else if (portable is null && name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)) portable = url;

            any ??= url;
        }

        return setup ?? portable ?? any;
    }

    private static string? ReadString(JsonElement obj, string property)
    {
        if (obj.ValueKind != JsonValueKind.Object) return null;
        if (!obj.TryGetProperty(property, out JsonElement value)) return null;
        if (value.ValueKind != JsonValueKind.String) return null;

        string? text = value.GetString();
        return string.IsNullOrWhiteSpace(text) ? null : text.Trim();
    }

    // ---------------- Versionen ----------------

    /// <summary>
    /// Macht aus einem GitHub-Tag ("v1.1.0", "1.1.0-portable", "v1.1.0-beta2") eine
    /// vergleichbare Version. Liefert null, wenn dem Tag keine Zahlbare entnommen werden kann.
    /// </summary>
    public static Version? ParseTag(string? tag)
    {
        if (string.IsNullOrWhiteSpace(tag)) return null;

        string raw = tag.Trim().TrimStart('v', 'V');

        // Suffixe und Schmuck (" -portable", "+build7", "_setup") abschneiden: nur Ziffern und Punkte.
        int cut = raw.Length;
        for (int i = 0; i < raw.Length; i++)
        {
            char c = raw[i];
            if (c != '.' && (c < '0' || c > '9'))
            {
                cut = i;
                break;
            }
        }

        string cleaned = raw.Substring(0, cut).Trim('.');
        if (cleaned.Length == 0) return null;

        string[] parts = cleaned.Split('.', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0) return null;

        // Ohne eine Zahl an erster Stelle ist der Tag kein Versionssprung (z. B. "latest").
        if (!int.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out int major) || major < 0)
            return null;

        try
        {
            // Volle vier Teile: "1.1" waere sonst aelter als die eigene "1.1.0.0".
            return new Version(major, ReadPart(parts, 1), ReadPart(parts, 2), ReadPart(parts, 3));
        }
        catch (ArgumentOutOfRangeException)
        {
            return null;
        }
        catch (OverflowException)
        {
            return null;
        }
    }

    private static int ReadPart(string[] parts, int index)
    {
        if (index >= parts.Length) return 0;
        return int.TryParse(parts[index], NumberStyles.None, CultureInfo.InvariantCulture, out int value) && value >= 0
            ? value
            : 0;
    }

    private static Version ReadAssemblyVersion()
    {
        Version? version = Assembly.GetExecutingAssembly().GetName().Version;
        return version ?? new Version(1, 0, 0, 0);
    }

    private static string ShortVersion(Version version)
        => version.Revision > 0
            ? version.Major + "." + version.Minor + "." + version.Build + "." + version.Revision
            : version.Major + "." + version.Minor + "." + version.Build;

    // ---------------- HttpClient ----------------

    private static HttpClient CreateHttpClient()
    {
        HttpClient http = new HttpClient(new HttpClientHandler
        {
            UseCookies = false,
            AllowAutoRedirect = true
        })
        {
            Timeout = TimeSpan.FromSeconds(12)
        };

        // Ohne diese beiden Header antwortet GitHub mit 403 (und der Hinweis ist leicht uebersehen).
        // ParseAdd ist noetig: new ProductInfoHeaderValue("SmartWashWidget") gilt in .NET 9 als
        // ungueltiges Format (FormatException) undliess sonst den statischen HttpClient scheitern.
        http.DefaultRequestHeaders.UserAgent.ParseAdd(UserAgent);
        http.DefaultRequestHeaders.Accept.ParseAdd(AcceptHeader);

        return http;
    }

    public void Dispose()
    {
        Stop();

        lock (_gate)
        {
            try { _cts?.Dispose(); } catch { /* egal */ }
            _cts = null;
            _loopTask = null;
        }
    }
}
