using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace SmartWashWidget.Services;

/// <summary>Fenster-/Darstellungseinstellungen (alle Masse in WPF-Geraeteunabhaengigen Einheiten).</summary>
public sealed class WindowSettings
{
    public double Left { get; set; } = double.NaN;
    public double Top { get; set; } = double.NaN;

    /// <summary>Gesamtskalierung des Widgets: 0.70 ... 1.50.</summary>
    public double Scale { get; set; } = 1.0;

    /// <summary>Alpha der Glas-Toenung: 0.00 (freie Durchsicht) ... 1.00 (stark abgedunkelt).</summary>
    public double TintAlpha { get; set; } = 0.0;

    /// <summary>
    /// Milchglas-Blur (Acrylic) ueber die Accent-Policy: true = Desktop wird weichgezeichnet,
    /// false = ACCENT_DISABLED und das Fenster ist dank AllowsTransparency=True glasklar durchsichtig.
    /// Mica / Mica Alt gibt es bewusst nicht mehr (die sind near-undurchsichtiges Grau).
    /// </summary>
    public bool AcrylicBlur { get; set; } = true;

    /// <summary>
    /// Rueckwaertskompatibel: alte config.json-Kannten haben "backdrop" mit den Modi
    /// acrylic / mica / micaalt / none. Alles ausser "none" bedeutet Blur an.
    /// Wird nie zurueckgeschrieben (Getter liefert null).
    /// </summary>
    [JsonPropertyName("backdrop")]
    public string? LegacyBackdropMode
    {
        get => null;
        set
        {
            if (string.IsNullOrWhiteSpace(value)) return;
            AcrylicBlur = !string.Equals(value.Trim(), "none", StringComparison.OrdinalIgnoreCase);
        }
    }

    public bool Topmost { get; set; } = true;
    public bool PositionLocked { get; set; }
    public bool RoundedCorners { get; set; } = true;

    /// <summary>
    /// true = das Widget zusaendlich als Knopf in der Taskleiste (WS_EX_APPWINDOW statt
    /// WS_EX_TOOLWINDOW auf dem Fensterhandle). Wirkt sofort - ohne Neustart.
    /// </summary>
    public bool ShowInTaskbar { get; set; } = false;

    public double ClampScale() => Math.Clamp(Scale, 0.70, 1.50);

    /// <summary>Glas-Toenung 0.00 (freie Durchsicht) ... 1.00 (stark abgedunkelt).</summary>
    public double ClampTint() => Math.Clamp(TintAlpha, 0.0, 1.0);
}

/// <summary>SmartThings-Zugang. Alle Secrets liegen ausschliesslich DPAPI-verschluesselt vor.</summary>
public sealed class SmartThingsSettings
{
    /// <summary>Base64 der per ProtectedData (CurrentUser + feste Entropie) versiegelten Access-Token-Bytes
    /// (OAuth2-Access-Token oder manueller Personal Access Token als Fallback).</summary>
    public string? TokenProtected { get; set; }

    /// <summary>Base64 des DPAPI-versiegelten OAuth2-Refresh-Tokens (ermoeglicht Auto-Refresh ohne Neu-Login).</summary>
    public string? RefreshTokenProtected { get; set; }

    /// <summary>Base64 des DPAPI-versiegelten OAuth-Client-ID.</summary>
    public string? OAuthClientIdProtected { get; set; }

    /// <summary>Base64 des DPAPI-versiegelten OAuth-Client-Secrets.</summary>
    public string? OAuthClientSecretProtected { get; set; }

    /// <summary>App-ID der erstellten OAuth-App (nicht geheim - fuer die Pflege der Redirect-URIs).</summary>
    public string? OAuthAppId { get; set; }

    /// <summary>
    /// true, wenn das Access-Token von der offiziellen SmartThings-CLI verwaltet wird
    /// (CLI-Bridge): Das Token liegt in deren credentials.json und wird dort automatisch
    /// erneuert - wir spiegeln es DPAPI-verschluesselt in die eigene config, damit der
    /// SmartThingsClient es nutzen kann.
    /// </summary>
    public bool UseCliManagedToken { get; set; }

    /// <summary>ISO-8601 (UTC) Zeitpunkt, bis zu dem der aktuelle Access-Token gueltig ist.</summary>
    public string? TokenExpiresAtUtc { get; set; }

    public string? WasherDeviceId { get; set; }
    public string? DryerDeviceId { get; set; }
    public string? WasherLabel { get; set; }
    public string? DryerLabel { get; set; }

    public bool AutoDiscover { get; set; } = true;

    public string? ApiBase { get; set; }

    public string? LastSyncUtc { get; set; }
    public string? LastError { get; set; }

    /// <summary>Nur eine Lesehilfe, wird nicht in config.json geschrieben (sonst get-only-Konflikt beim Laden).</summary>
    [JsonIgnore]
    public bool HasToken => !string.IsNullOrWhiteSpace(TokenProtected);
}

/// <summary>Gesamte Konfiguration (config.json in %LocalAppData%\SmartWashWidget).</summary>
public sealed class WidgetConfig
{
    public const int CurrentVersion = 1;

    public int ConfigVersion { get; set; } = CurrentVersion;

    public WindowSettings Window { get; set; } = new();
    public SmartThingsSettings SmartThings { get; set; } = new();

    /// <summary>Live-Demo-Modus: true, solange kein Token hinterlegt ist oder der Nutzer ihn explizit anschaltet.</summary>
    public bool DemoMode { get; set; } = true;

    /// <summary>Demo-Tempo: 1 = reale Zeit, bis 24 = geraffte Demo-Zeit.</summary>
    public double DemoTempo { get; set; } = 4.0;

    /// <summary>
    /// Anzeigesprache: "auto" (Systemsprache), "de" oder "en". Der Wert wirkt sofort -
    /// der LocalizationManager tauscht damit alle Texte ohne Neustart aus.
    /// </summary>
    public string Language { get; set; } = "auto";

    /// <summary>
    /// Automatischer Update-Check gegen die offizielle GitHub-Releases-API: einmal 5 s nach
    /// dem App-Start, danach alle 4 Stunden im Hintergrund. Fehlt der Wert in einer alten
    /// config.json, greift dieser Standardwert true.
    /// </summary>
    public bool AutoUpdateCheck { get; set; } = true;

    /// <summary>
    /// "Mit Windows starten": Run-Eintrag des angemeldeten Benutzers
    /// (HKCU\Software\Microsoft\Windows\CurrentVersion\Run). Der Registry-Wert ist die Wahrheit,
    /// dieser Wert ist seine Spiegelung in config.json - abgeglichen von AutostartService.
    /// Fehlt der Wert in einer alten config.json, greift dieser Standardwert false.
    /// </summary>
    public bool RunAtStartup { get; set; } = false;
}

/// <summary>Laedt/sichert die Konfiguration atomar und verschluesselt den SmartThings-Token.</summary>
public sealed class AppConfigService
{
    // Zusaetzliche Entropie: verhindert, dass ein beliebiges anderes Programm den Blob entpackt.
    // Der Blob bleibt dennoch strikt an das aktuelle Windows-Benutzerkonto gebunden (DPAPI).
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("SmartWashWidget.TokenScope.v1");

    private static readonly JsonSerializerOptions JsonOpts = new JsonSerializerOptions
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase), new DoubleConverter() },
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true
    };

    /// <summary>
    /// double-Werte, die "nicht gesetzt" bedeuten (NaN, z. B. Fensterposition), werden als
    /// JSON-null geschrieben und beim Laden wieder zu NaN - System.Text.Json kann NaN nicht
    /// direkt schreiben (sonst wirft das Speichern der Ersterzeugung einen Fehler).
    /// </summary>
    private sealed class DoubleConverter : JsonConverter<double>
    {
        public override double Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            if (reader.TokenType == JsonTokenType.Null) return double.NaN;
            return reader.GetDouble();
        }

        public override void Write(Utf8JsonWriter writer, double value, JsonSerializerOptions options)
        {
            if (double.IsNaN(value) || double.IsInfinity(value)) writer.WriteNullValue();
            else writer.WriteNumberValue(value);
        }
    }

    public static string DirectoryPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "SmartWashWidget");

    public static string ConfigPath => Path.Combine(DirectoryPath, "config.json");

    public WidgetConfig Config { get; private set; }

    /// <summary>true, wenn die config.json beim Start noch nicht existierte (Erste-Start-Fall).</summary>
    public bool FirstRun { get; private set; }

    /// <summary>Falls das Laden/Schreiben fehl schlug - fuer die Statusanzeige, nie als Absturz.</summary>
    public string? ConfigurationError =>
        _configurationErrorKey is null ? null : LocalizationManager.Format(_configurationErrorKey, _configurationErrorArgs);

    /// <summary>Key des Sprachvorrats fuer die Konfigurationsmeldung (der Config-Load laeuft vor jeder Uebersetzung).</summary>
    public string? ConfigurationErrorKey => _configurationErrorKey;

    /// <summary>Formatargumente der Konfigurationsmeldung (technische Details bleiben unveraendert).</summary>
    public object?[] ConfigurationErrorArgs => _configurationErrorArgs;

    private string? _configurationErrorKey;
    private object?[] _configurationErrorArgs = Array.Empty<object?>();

    /// <summary>Merk die Konfigurationsmeldung als Sprach-Key (Uebersetzung erfolgt erst bei der Anzeige).</summary>
    private void NoteConfigurationError(string key, params object?[] args)
    {
        _configurationErrorKey = key;
        _configurationErrorArgs = args;
    }

    private void ClearConfigurationError()
    {
        _configurationErrorKey = null;
        _configurationErrorArgs = Array.Empty<object?>();
    }

    public AppConfigService()
    {
        Config = Load();
    }

    public bool HasToken => Config.SmartThings.HasToken;

    private WidgetConfig Load()
    {
        try
        {
            Directory.CreateDirectory(DirectoryPath);

            if (!File.Exists(ConfigPath))
            {
                FirstRun = true;
                var fresh = new WidgetConfig();
                // Erste Startphase: Demo-Modus immer AN (es gibt ja noch keinen Zugang).
                fresh.DemoMode = true;
                WriteConfig(fresh);
                return fresh;
            }

            FirstRun = false;
            string raw = File.ReadAllText(ConfigPath);
            var loaded = JsonSerializer.Deserialize<WidgetConfig>(raw, JsonOpts);
            if (loaded is null)
            {
                NoteConfigurationError("Str_Cfg_Unreadable");
                return new WidgetConfig();
            }

            if (loaded.ConfigVersion != WidgetConfig.CurrentVersion)
                loaded.ConfigVersion = WidgetConfig.CurrentVersion;

            loaded.Language = SanitizeLanguage(loaded.Language);
            loaded.Window.Scale = loaded.Window.ClampScale();
            loaded.Window.TintAlpha = loaded.Window.ClampTint();
            return loaded;
        }
        catch (Exception ex)
        {
            NoteConfigurationError("Str_Cfg_LoadFailed", ex.Message);
            LogProblem("Load", ex);
            return new WidgetConfig();
        }
    }

    /// <summary>Nimmt nur Sprachcodes an, die der LocalizationManager auch wirklich kennt.</summary>
    public static string SanitizeLanguage(string? raw)
    {
        string code = (raw ?? string.Empty).Trim().ToLowerInvariant();
        if (code == LocalizationManager.Auto || code == "de" || code == "en") return code;
        return LocalizationManager.Auto;
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(DirectoryPath);
            WriteConfig(Config);
            ClearConfigurationError();
        }
        catch (Exception ex)
        {
            NoteConfigurationError("Str_Cfg_SaveFailed", ex.Message);
            LogProblem("Save", ex);
        }
    }

    /// <summary>Diagnose in error.log (besteffe - Logging darf selbst nie storen).</summary>
    private static void LogProblem(string stage, Exception ex)
    {
        try
        {
            Directory.CreateDirectory(DirectoryPath);
            File.AppendAllText(
                Path.Combine(DirectoryPath, "error.log"),
                DateTime.Now.ToString("s") + "  [config:" + stage + "]  " + ex + Environment.NewLine);
        }
        catch
        {
            // absichtlich still
        }
    }

    /// <summary>Setzt die Konfiguration zurueck (Token und Gerate-Zuordnungen fallen weg).</summary>
    public void Reset()
    {
        try
        {
            if (File.Exists(ConfigPath)) File.Delete(ConfigPath);
        }
        catch (Exception ex)
        {
            NoteConfigurationError("Str_Cfg_ResetFailed", ex.Message);
        }

        Config = new WidgetConfig();
        FirstRun = true;
        Save();
    }

    /// <summary>Atomares Schreiben: erst tmp, dann File.Replace (kein halbschriebener Zustand).</summary>
    private static void WriteConfig(WidgetConfig cfg)
    {
        string json = JsonSerializer.Serialize(cfg, JsonOpts);
        string tmp = ConfigPath + ".tmp";
        File.WriteAllText(tmp, json);

        if (File.Exists(ConfigPath))
            File.Replace(tmp, ConfigPath, null);
        else
            File.Move(tmp, ConfigPath);
    }

    // ---------- DPAPI (alles, was geheim ist) ----------

    private static string Protect(string plain)
    {
        byte[] bytes = ProtectedData.Protect(
            Encoding.UTF8.GetBytes(plain), Entropy, DataProtectionScope.CurrentUser);
        return Convert.ToBase64String(bytes);
    }

    private static string? Unprotect(string? protectedBase64)
    {
        if (string.IsNullOrWhiteSpace(protectedBase64)) return null;
        byte[] blob = Convert.FromBase64String(protectedBase64);
        byte[] plain = ProtectedData.Unprotect(blob, Entropy, DataProtectionScope.CurrentUser);
        try
        {
            return Encoding.UTF8.GetString(plain);
        }
        finally
        {
            Array.Clear(plain, 0, plain.Length);
        }
    }

    /// <summary>Entschluesselt, schluckt aber Fehler still (fuer sekundaere Felder).</summary>
    private static string? SafeUnprotect(string? protectedBase64)
    {
        try { return Unprotect(protectedBase64); }
        catch { return null; }
    }

    // ---------- Access-Token (PAT-Fallback und OAuth2-Access-Token teilen sich dieses Feld) ----------

    public void SetToken(string? plainToken)
    {
        if (string.IsNullOrWhiteSpace(plainToken))
        {
            Config.SmartThings.TokenProtected = null;
            return;
        }

        string token = plainToken.Trim();
        // Grobe Plausibilitaetspruefung: ein PAT ist lang (GUID o. ae.), OAuth-Tokens sind es erst recht.
        if (token.Length < 16)
            throw new FormatException(LocalizationManager.Get("Str_Cfg_TokenTooShort"));

        Config.SmartThings.TokenProtected = Protect(token);
    }

    /// <summary>Gibt den Klartext-Access-Token zurueck oder null, wenn nichts hinterlegt / Entpacken fehl schlug.</summary>
    public string? GetToken()
    {
        if (string.IsNullOrWhiteSpace(Config.SmartThings.TokenProtected))
            return null;

        try
        {
            string token = Unprotect(Config.SmartThings.TokenProtected) ?? string.Empty;
            return string.IsNullOrWhiteSpace(token) ? null : token;
        }
        catch (CryptographicException)
        {
            NoteConfigurationError("Str_Cfg_TokenUnprotect");
            Config.SmartThings.TokenProtected = null;
            return null;
        }
        catch (Exception ex)
        {
            NoteConfigurationError("Str_Cfg_TokenError", ex.Message);
            Config.SmartThings.TokenProtected = null;
            return null;
        }
    }

    // ---------- OAuth2-Zugangsdaten ----------

    public bool HasOAuthCredentials =>
        !string.IsNullOrWhiteSpace(Config.SmartThings.OAuthClientIdProtected)
        && !string.IsNullOrWhiteSpace(Config.SmartThings.OAuthClientSecretProtected);

    /// <summary>Vollstaendige OAuth2-Sitzung (Access- + Refresh-Token vorhanden) = dauerhafte Verbindung.</summary>
    public bool HasOAuthSession =>
        HasToken
        && HasOAuthCredentials
        && !string.IsNullOrWhiteSpace(Config.SmartThings.RefreshTokenProtected);

    public void SetOAuthClientCredentials(string clientId, string clientSecret)
    {
        Config.SmartThings.OAuthClientIdProtected = Protect(clientId.Trim());
        Config.SmartThings.OAuthClientSecretProtected = Protect(clientSecret.Trim());
    }

    public string? GetOAuthClientId() => SafeUnprotect(Config.SmartThings.OAuthClientIdProtected);

    public string? GetOAuthClientSecret() => SafeUnprotect(Config.SmartThings.OAuthClientSecretProtected);

    public void GetOAuthClientCredentials(out string? clientId, out string? clientSecret)
    {
        clientId = GetOAuthClientId();
        clientSecret = GetOAuthClientSecret();
    }

    public string? GetOAuthAppId() => Config.SmartThings.OAuthAppId;

    public void SetOAuthAppId(string appId)
    {
        if (!string.IsNullOrWhiteSpace(appId))
            Config.SmartThings.OAuthAppId = appId.Trim();
    }

    /// <summary>Speichert Access- + Refresh-Token und den Ablaufzeitpunkt eines erfolgreichen Token-Tauschs.</summary>
    public void SetOAuthTokens(string accessToken, string refreshToken, DateTime expiresAtUtc)
    {
        Config.SmartThings.TokenProtected = Protect(accessToken.Trim());
        Config.SmartThings.RefreshTokenProtected = Protect(refreshToken.Trim());
        Config.SmartThings.TokenExpiresAtUtc = expiresAtUtc.ToUniversalTime().ToString("o");
    }

    public string? GetRefreshToken() => SafeUnprotect(Config.SmartThings.RefreshTokenProtected);

    /// <summary>Ablaufzeitpunkt des Access-Tokens (UTC) oder null, wenn unbekannt.</summary>
    public DateTime? GetTokenExpiresAtUtc()
    {
        string? raw = Config.SmartThings.TokenExpiresAtUtc;
        if (string.IsNullOrWhiteSpace(raw)) return null;

        if (DateTime.TryParse(
                raw, System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.RoundtripKind | System.Globalization.DateTimeStyles.AdjustToUniversal,
                out DateTime dt))
            return dt.ToUniversalTime();

        if (DateTimeOffset.TryParse(
                raw, System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.AdjustToUniversal,
                out DateTimeOffset dto))
            return dto.ToUniversalTime().UtcDateTime;

        return null;
    }

    /// <summary>Verwirft OAuth-Zugangsdaten + Tokens (Access-Token bleibt erhalten - nur fuer volle Trennung nutzen).</summary>
    public void ClearOAuth()
    {
        Config.SmartThings.OAuthClientIdProtected = null;
        Config.SmartThings.OAuthClientSecretProtected = null;
        Config.SmartThings.RefreshTokenProtected = null;
        Config.SmartThings.TokenExpiresAtUtc = null;
    }

    public void ClearToken()
    {
        Config.SmartThings.TokenProtected = null;
        ClearOAuth();
        Config.SmartThings.UseCliManagedToken = false;
        Config.SmartThings.WasherDeviceId = null;
        Config.SmartThings.DryerDeviceId = null;
        Config.SmartThings.WasherLabel = null;
        Config.SmartThings.DryerLabel = null;
        Config.SmartThings.LastError = null;
        Config.SmartThings.LastSyncUtc = null;
    }

    public void RememberSync(string? error)
    {
        Config.SmartThings.LastSyncUtc = DateTimeOffset.UtcNow.ToString("o");
        Config.SmartThings.LastError = string.IsNullOrWhiteSpace(error) ? null : error;
    }
}
