using System;
using System.Collections.Generic;
using System.Globalization;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

using SmartWashWidget.Models;

namespace SmartWashWidget.Services;

/// <summary>
/// Fehler aus der SmartThings-API, mit HTTP-Statuscode fuer saubere Nutzertexte.
/// Der Base-Message bleibt technisch (error.log), der Nutzertext wird aus dem
/// aktiven Sprachvorrat gebaut - deshalb wirkt ein Sprachwechsel sofort.
/// </summary>
public sealed class SmartThingsException : Exception
{
    public HttpStatusCode? StatusCode { get; }
    public string? ApiDetail { get; }

    /// <summary>Key des Sprachvorrats fuer den Nutzertext (fehlt er, entscheidet der Statuscode).</summary>
    public string? TextKey { get; init; }

    /// <summary>Formatargumente des Nutzertextes (technische Details bleiben unveraendert).</summary>
    public object?[] TextArgs { get; init; } = Array.Empty<object?>();

    public SmartThingsException(string message, HttpStatusCode? status = null, string? detail = null)
        : base(message)
    {
        StatusCode = status;
        ApiDetail = detail;
    }

    /// <summary>Nutzertext in der gerade aktiven Sprache (wird bei jedem Zugriff neu geloest).</summary>
    public string UserText
    {
        get
        {
            if (!string.IsNullOrWhiteSpace(TextKey))
                return LocalizationManager.Format(TextKey!, TextArgs);

            return StatusCode switch
            {
                HttpStatusCode.Unauthorized => LocalizationManager.Get("Str_Err_401"),
                HttpStatusCode.Forbidden => LocalizationManager.Get("Str_Err_403"),
                HttpStatusCode.NotFound => LocalizationManager.Get("Str_Err_404"),
                HttpStatusCode.TooManyRequests => LocalizationManager.Get("Str_Err_429"),
                null => LocalizationManager.Format("Str_Err_Network", Message),
                _ => LocalizationManager.Format("Str_Err_Api", StatusCode.Value, ApiDetail ?? Message),
            };
        }
    }
}

/// <summary>Fortschritts-Schaetzung pro Geraet (SmartThings nennt keine Gesamtlaufzeit).</summary>
public sealed class RunTracker
{
    public DateTimeOffset? RunStartUtc { get; set; }
    public TimeSpan? ObservedMaxRemaining { get; set; }
    public double LastProgress { get; private set; }
    public MachineRunState LastState { get; private set; } = MachineRunState.Unknown;

    /// <summary>Merkt sich einen neuen Laufbeginn (Wechsel von Idle/Finished nach Running).</summary>
    public void NoteState(MachineRunState state)
    {
        if (state == MachineRunState.Running && LastState != MachineRunState.Running)
        {
            RunStartUtc = DateTimeOffset.UtcNow;
            ObservedMaxRemaining = null;
        }
        if (state is MachineRunState.Idle or MachineRunState.Finished or MachineRunState.Unreachable)
        {
            RunStartUtc = null;
            ObservedMaxRemaining = null;
            LastProgress = state == MachineRunState.Finished ? 1.0 : 0.0;
        }
        LastState = state;
    }

    public void NoteRemaining(TimeSpan remaining)
    {
        if (RunStartUtc is null)
            RunStartUtc = DateTimeOffset.UtcNow - remaining;

        if (ObservedMaxRemaining is null || remaining > ObservedMaxRemaining)
            ObservedMaxRemaining = remaining;

        TimeSpan? total = ObservedMaxRemaining;
        if (total is null || total.Value <= TimeSpan.Zero)
            return;

        TimeSpan left = remaining;
        if (left < TimeSpan.Zero) left = TimeSpan.Zero;

        double progress = 1.0 - (left.TotalSeconds / total.Value.TotalSeconds);
        LastProgress = Math.Clamp(progress, LastProgress < 0.02 ? 0.0 : LastProgress, 1.0);
    }

    public void Reset()
    {
        RunStartUtc = null;
        ObservedMaxRemaining = null;
        LastProgress = 0.0;
        LastState = MachineRunState.Unknown;
    }
}

/// <summary>
/// Duennfluessiger SmartThings-Client: nur HttpClient + System.Text.Json, keinerlei NuGet-Abhaengigkeit.
/// Alle Antworten werden fehlertolerant gelesen (Attribute koennen Objekt, Array oder Zahl sein).
/// </summary>
public sealed class SmartThingsClient : IDisposable
{
    public const string DefaultApiBase = "https://api.smartthings.com";
    private const string TokenEndpoint = "https://api.smartthings.com/v1/oauth/token";

    /// <summary>Access-Token wird erneuert, sobald er in weniger dieser Zeitspanne ablaeuft.</summary>
    private static readonly TimeSpan DefaultRefreshMargin = TimeSpan.FromMinutes(10);

    private static readonly string[] WasherCapabilities =
    {
        "washerOperatingState", "washerMode", "washerCycle", "custom.washerOperatingState"
    };

    private static readonly string[] DryerCapabilities =
    {
        "dryerOperatingState", "dryerMode", "dryerCycle", "custom.dryerOperatingState"
    };

    /// <summary>Feste Zeitformate, die manche Samsung-Firmwares statt ISO-8601 senden.</summary>
    private static readonly string[] FallbackTimeFormats =
    {
        "yyyy-MM-dd HH:mm:ss'Z'", "yyyy-MM-dd HH:mm:ss", "yyyy-MM-ddTHH:mm:ss'Z'"
    };

    private readonly AppConfigService _config;
    private readonly HttpClient _http;
    private readonly SemaphoreSlim _refreshLock = new SemaphoreSlim(1, 1);

    /// <summary>
    /// OAuth2-faehiger Client: liest Access-/Refresh-Token aus der DPAPI-Konfiguration und
    /// erneuert den Access-Token bei Bedarf automatisch (thread-sicher).
    /// </summary>
    public SmartThingsClient(AppConfigService config, string? apiBase = null)
    {
        _config = config ?? throw new ArgumentNullException(nameof(config));

        _http = new HttpClient
        {
            BaseAddress = new Uri(NormalizeBase(apiBase)),
            Timeout = TimeSpan.FromSeconds(9)
        };
        _http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        _http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("SmartWashWidget", "1.0"));

        string? token = _config.GetToken();
        if (!string.IsNullOrWhiteSpace(token)) SetAuthHeader(token);
    }

    private void SetAuthHeader(string? token)
    {
        if (string.IsNullOrWhiteSpace(token))
            _http.DefaultRequestHeaders.Authorization = null;
        else
            _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token.Trim());
    }

    // ---------------- OAuth2 Auto-Refresh ----------------

    /// <summary>
    /// Stellt vor jedem API-Request sicher, dass ein frischer Access-Token gesetzt ist.
    /// Bei OAuth2-Konfiguration wird ein abgelaufener (oder fast abgelaufener) Access-Token
    /// automatisch per Refresh-Token erneuert - ganz ohne Nutzereingriff.
    /// </summary>
    public async Task EnsureAccessTokenAsync(CancellationToken ct, TimeSpan? refreshMargin = null)
    {
        // CLI-Bridge: Das Token verwaltet die SmartThings-CLI (credentials.json). Bei Bedarf
        // (kurz vor Ablauf) wird die CLI einmal zur Geraete-Liste aufgefordert, was sie dazu
        // bringt, ihr eigenes OAuth2-Token automatisch zu erneuern.
        if (_config.Config.SmartThings.UseCliManagedToken)
        {
            await EnsureCliManagedAccessAsync(refreshMargin ?? DefaultRefreshMargin, ct).ConfigureAwait(false);
            return;
        }

        // PAT-Fallback-Modus: kein OAuth -> nichts zu erneuern, der Token kommt aus der Konfiguration.
        if (!_config.HasOAuthCredentials) return;

        TimeSpan margin = refreshMargin ?? DefaultRefreshMargin;
        string? token = _config.GetToken();
        DateTime? expiresUtc = _config.GetTokenExpiresAtUtc();
        bool due = token is null || (expiresUtc is not null && DateTime.UtcNow >= expiresUtc.Value - margin);

        if (!due)
        {
            SetAuthHeader(token);
            return;
        }

        await RefreshTokensAsync(ct).ConfigureAwait(false);
    }

    /// <summary>Stellt im CLI-Bridge-Modus einen frischen Token sicher (liest die CLI-
    /// credentials.json und laesst die CLI bei Ablauf ihr Token selbst erneuern).</summary>
    private async Task EnsureCliManagedAccessAsync(TimeSpan margin, CancellationToken ct)
    {
        string? token = _config.GetToken();
        if (string.IsNullOrWhiteSpace(token))
            token = SmartThingsCliAuth.ReadAccessToken();

        DateTime? expiresUtc = SmartThingsCliAuth.ReadExpiryUtc();
        bool due = string.IsNullOrWhiteSpace(token)
            || (expiresUtc is not null && DateTime.UtcNow >= expiresUtc.Value - margin);

        if (due)
        {
            await SmartThingsCliAuth.RefreshByListingDevicesAsync(ct).ConfigureAwait(false);
            token = SmartThingsCliAuth.ReadAccessToken();
        }

        if (string.IsNullOrWhiteSpace(token))
            throw new SmartThingsException(
                "no smartthings access available",
                HttpStatusCode.Unauthorized)
            { TextKey = "Str_Err_NoAccessLive" };

        string? current = _config.GetToken();
        if (!string.Equals(current, token, StringComparison.Ordinal))
        {
            _config.SetToken(token!);
            _config.Save();
        }
        SetAuthHeader(token);
    }

    /// <summary>
    /// Thread-sicherer OAuth2-Token-Tausch: POST /oauth/token mit grant_type=refresh_token.
    /// Neue Access- + Refresh-Tokens werden sofort DPAPI-verschluesselt in der config.json gesichert.
    /// </summary>
    public async Task RefreshTokensAsync(CancellationToken ct)
    {
        await _refreshLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            // Nebenlaeufer hat schon aufgefrischt? Dann nur den Header nachziehen.
            string? alreadyToken = _config.GetToken();
            DateTime? alreadyExpiry = _config.GetTokenExpiresAtUtc();
            if (alreadyToken is not null && alreadyExpiry is not null &&
                DateTime.UtcNow < alreadyExpiry.Value - DefaultRefreshMargin)
            {
                SetAuthHeader(alreadyToken);
                return;
            }

            _config.GetOAuthClientCredentials(out string? clientId, out string? clientSecret);
            string? refreshToken = _config.GetRefreshToken();

            if (string.IsNullOrWhiteSpace(clientId) || string.IsNullOrWhiteSpace(clientSecret) || refreshToken is null)
            {
                throw new SmartThingsException(
                    "automatic token refresh not possible: missing oauth credentials",
                    HttpStatusCode.Unauthorized)
                { TextKey = "Str_Err_RefreshGone" };
            }

            using var request = new HttpRequestMessage(HttpMethod.Post, TokenEndpoint)
            {
                Content = new FormUrlEncodedContent(new Dictionary<string, string>
                {
                    ["grant_type"] = "refresh_token",
                    ["refresh_token"] = refreshToken
                })
            };
            request.Headers.Authorization = new AuthenticationHeaderValue(
                "Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes(clientId + ":" + clientSecret)));
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

            HttpResponseMessage response;
            try
            {
                response = await _http.SendAsync(request, ct).ConfigureAwait(false);
            }
            catch (HttpRequestException ex)
            {
                throw new SmartThingsException("automatic token refresh request failed")
                { TextKey = "Str_Err_RefreshFailed", TextArgs = new object?[] { ex.Message } };
            }

            using (response)
            {
                string body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

                if (!response.IsSuccessStatusCode)
                {
                    if (response.StatusCode == HttpStatusCode.Unauthorized)
                        throw new SmartThingsException(
                            "automatic token refresh rejected by samsung",
                            HttpStatusCode.Unauthorized)
                        { TextKey = "Str_Err_RefreshRejected" };

                    throw new SmartThingsException(
                        "automatic token refresh failed (http " + (int)response.StatusCode + ")",
                        response.StatusCode, TryExtractError(body))
                    { TextKey = "Str_Err_RefreshHttp", TextArgs = new object?[] { (int)response.StatusCode } };
                }

                JsonDocument parsedJson;
                try
                {
                    parsedJson = JsonDocument.Parse(body, new JsonDocumentOptions
                    {
                        CommentHandling = JsonCommentHandling.Skip,
                        AllowTrailingCommas = true
                    });
                }
                catch (JsonException)
                {
                    throw new SmartThingsException("token refresh response was no json", response.StatusCode)
                    { TextKey = "Str_Err_RefreshNoJson" };
                }

                using (parsedJson)
                {
                    string? access = GetString(parsedJson.RootElement, "access_token");
                    string? refresh = GetString(parsedJson.RootElement, "refresh_token");
                    double? expiresIn = ParseNumber(GetString(parsedJson.RootElement, "expires_in"));

                    if (string.IsNullOrWhiteSpace(access) || string.IsNullOrWhiteSpace(refresh))
                        throw new SmartThingsException("token refresh response contained no tokens", response.StatusCode)
                        { TextKey = "Str_Err_RefreshNoTokens" };

                    int seconds = expiresIn is null or <= 0 ? 3600 : (int)Math.Round(expiresIn.Value);
                    _config.SetOAuthTokens(access!, refresh!, DateTime.UtcNow.AddSeconds(seconds));
                }
                _config.Save();
                SetAuthHeader(_config.GetToken());
            }
        }
        finally
        {
            _refreshLock.Release();
        }
    }

    /// <summary>
    /// Hintergrund-Check fuer den App-Ueberbau: erneuert den Access-Token rechtzeitig, wenn er
    /// binnen <paramref name="refreshBeforeExpiry"/> ablaeuft - auch wenn das Widget und die
    /// Maschinen tagelang ausgeschaltet waren.
    /// </summary>
    public static async Task RefreshIfExpiringAsync(AppConfigService config, TimeSpan refreshBeforeExpiry, CancellationToken ct)
    {
        // CLI-Bridge: Das Token lebt in der CLI-credentials.json und wird per CLI-Aufruf erneuert.
        if (config.Config.SmartThings.UseCliManagedToken)
        {
            DateTime? cliExpiry = SmartThingsCliAuth.ReadExpiryUtc();
            if (cliExpiry is null || DateTime.UtcNow < cliExpiry.Value - refreshBeforeExpiry) return;

            await SmartThingsCliAuth.RefreshByListingDevicesAsync(ct).ConfigureAwait(false);
            string? fresh = SmartThingsCliAuth.ReadAccessToken();
            if (!string.IsNullOrWhiteSpace(fresh))
            {
                string? current = config.GetToken();
                if (!string.Equals(current, fresh, StringComparison.Ordinal)) { config.SetToken(fresh!); config.Save(); }
            }
            return;
        }

        if (!config.HasOAuthCredentials || !config.HasOAuthSession) return;

        DateTime? expiresUtc = config.GetTokenExpiresAtUtc();
        if (expiresUtc is null || DateTime.UtcNow < expiresUtc.Value - refreshBeforeExpiry) return;

        using var client = new SmartThingsClient(config, config.Config.SmartThings.ApiBase);
        await client.EnsureAccessTokenAsync(ct, refreshBeforeExpiry).ConfigureAwait(false);
    }

    public static string NormalizeBase(string? apiBase)
    {
        if (string.IsNullOrWhiteSpace(apiBase)) return DefaultApiBase;
        string b = apiBase.Trim().TrimEnd('/');
        return Uri.IsWellFormedUriString(b, UriKind.Absolute) ? b : DefaultApiBase;
    }

    // ---------------- Geraeteliste / Auto-Detect ----------------

    /// <summary>GET /v1/devices (paginiert) und prueft jedes Geraet per Status-Call auf Wasch-/Trockner-Faehigkeiten.</summary>
    public async Task<List<DiscoveredDevice>> DiscoverDevicesAsync(int maxCandidates, CancellationToken ct)
    {
        var candidates = new List<DiscoveredDevice>();

        string? next = "/v1/devices?capacity=200";
        int guard = 0;

        while (!string.IsNullOrWhiteSpace(next) && guard++ < 12)
        {
            using JsonDocument doc = await GetJsonAsync(next, ct).ConfigureAwait(false);

            if (!doc.RootElement.TryGetProperty("items", out JsonElement items) || items.ValueKind != JsonValueKind.Array)
                break;

            foreach (JsonElement item in items.EnumerateArray())
            {
                // Die Geraeteliste liefert "deviceId" (aeltere Antworten nutzten "id").
                string id = GetString(item, "deviceId") ?? GetString(item, "id") ?? GetString(item, "device_id") ?? string.Empty;
                if (string.IsNullOrWhiteSpace(id)) continue;

                string label = CleanText(GetString(item, "label") ?? GetString(item, "name") ?? GetString(item, "device_name"));
                string? type = GetString(item, "deviceTypeName") ?? GetString(item, "deviceType")
                               ?? GetString(item, "type") ?? GetString(item, "device_type");

                // Faehigkeiten stehen verschachtelt in components[].capabilities[].id. Damit lassen sich
                // Waschmaschine und Trockner schon ohne zusaetzlichen Status-Aufruf sicher erkennen.
                bool washer = false, dryer = false;
                foreach (string capId in EnumerateCapabilityIds(item))
                {
                    if (!washer && MatchesCapability(capId, WasherCapabilities)) washer = true;
                    if (!dryer && MatchesCapability(capId, DryerCapabilities)) dryer = true;
                    if (washer && dryer) break;
                }

                candidates.Add(new DiscoveredDevice(
                    id, label, type, KindFromText(label) ?? KindFromText(type), washer, dryer));

                if (candidates.Count >= 400) break;
            }

            next = null;
            if (doc.RootElement.TryGetProperty("links", out JsonElement links) && links.ValueKind == JsonValueKind.Object)
            {
                if (links.TryGetProperty("next", out JsonElement linkObj))
                {
                    if (linkObj.ValueKind == JsonValueKind.Object)
                        next = GetString(linkObj, "href");
                    else if (linkObj.ValueKind == JsonValueKind.String)
                        next = linkObj.GetString();
                }
            }
        }

        // Noch unbestaetigte Geraete live per Status-Call verifizieren - bevorzugt die, deren Name oder
        // Typ schon nach Waschmaschine/Trockner klingt (spart API-Aufrufe in grossen Haushalten).
        var probeOrder = new List<DiscoveredDevice>();
        foreach (DiscoveredDevice dev in candidates)
            if (!dev.HasWasherCapability && !dev.HasDryerCapability && dev.InferredKind is not null)
                probeOrder.Add(dev);
        foreach (DiscoveredDevice dev in candidates)
            if (!dev.HasWasherCapability && !dev.HasDryerCapability && dev.InferredKind is null)
                probeOrder.Add(dev);

        var probed = new Dictionary<string, (bool Washer, bool Dryer)>();
        int probes = 0;
        foreach (DiscoveredDevice dev in probeOrder)
        {
            ct.ThrowIfCancellationRequested();
            if (probes >= Math.Max(maxCandidates, 24)) break;
            probes++;

            bool washer = false, dryer = false;
            try
            {
                using JsonDocument status = await GetJsonAsync("/v1/devices/" + Uri.EscapeDataString(dev.Id) + "/status", ct)
                    .ConfigureAwait(false);
                washer = HasCapability(status.RootElement, WasherCapabilities);
                dryer = HasCapability(status.RootElement, DryerCapabilities);
            }
            catch (SmartThingsException)
            {
                // Geraet nicht lesbar -> Heuristik aus Namen/Typ behalten.
                washer = dev.InferredKind == MachineKind.Washer;
                dryer = dev.InferredKind == MachineKind.Dryer;
            }
            catch (Exception)
            {
                washer = dev.InferredKind == MachineKind.Washer;
                dryer = dev.InferredKind == MachineKind.Dryer;
            }

            probed[dev.Id] = (washer, dryer);
        }

        var verified = new List<DiscoveredDevice>();
        foreach (DiscoveredDevice dev in candidates)
        {
            bool washer = dev.HasWasherCapability;
            bool dryer = dev.HasDryerCapability;

            if (!washer && !dryer && probed.TryGetValue(dev.Id, out (bool Washer, bool Dryer) hit))
            {
                washer = hit.Washer;
                dryer = hit.Dryer;
            }

            MachineKind? kind = washer && !dryer ? MachineKind.Washer
                            : dryer && !washer ? MachineKind.Dryer
                            : dev.InferredKind;

            verified.Add(dev with
            {
                HasWasherCapability = washer,
                HasDryerCapability = dryer,
                InferredKind = kind
            });
        }

        verified.Sort((a, b) => string.Compare(a.Display, b.Display, StringComparison.CurrentCultureIgnoreCase));
        return verified;
    }

    /// <summary>Kurz-Check, ob Token + Endpoint grundsaetzlich funktionieren.</summary>
    public async Task<int> ValidateTokenAsync(CancellationToken ct)
    {
        using JsonDocument doc = await GetJsonAsync("/v1/devices?capacity=200", ct).ConfigureAwait(false);
        int count = 0;
        if (doc.RootElement.TryGetProperty("items", out JsonElement items) && items.ValueKind == JsonValueKind.Array)
            foreach (JsonElement _ in items.EnumerateArray()) count++;
        return count;
    }

    // ---------------- Status eines Geraets ----------------

    public async Task<MachineSnapshot> ReadStatusAsync(
        string deviceId,
        MachineKind kind,
        string? label,
        RunTracker tracker,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(deviceId))
            throw new SmartThingsException("no device id configured") { TextKey = "Str_Err_NoDeviceId" };

        using JsonDocument doc = await GetJsonAsync(
            "/v1/devices/" + Uri.EscapeDataString(deviceId) + "/status", ct).ConfigureAwait(false);

        JsonElement root = doc.RootElement;
        string capName = kind.CapabilityName();

        // Samsung meldet die Betriebs-Capability oft mehrfach: "washerOperatingState" (Zustand,
        // completionTime) und "samsungce.washerOperatingState" (progress, remainingTime, Phasen).
        // Beide Varianten werden zusammengelesen, damit kein Wert verloren geht.
        List<JsonElement> caps = FindCapabilities(root, capName);
        if (caps.Count == 0)
        {
            // Toleranter Fallback: die andere Betriebs-Capability desselben Geraets (Combo-Geraelte).
            string other = (kind == MachineKind.Washer ? "dryerOperatingState" : "washerOperatingState");
            caps = FindCapabilities(root, other);
            if (caps.Count == 0)
                throw new SmartThingsException(
                    "device has no " + capName + " capability display",
                    HttpStatusCode.NotFound)
                { TextKey = "Str_Err_NoCapability", TextArgs = new object?[] { capName } };
        }

        string? Attr(string attributePath)
        {
            foreach (JsonElement holder in caps)
            {
                string? value = GetAttributeValue(holder, attributePath);
                if (!string.IsNullOrWhiteSpace(value)) return value;
            }
            return GetAttributeValue(root, attributePath);
        }

        string? rawState = Attr("machineState") ?? Attr("operatingState.state") ?? Attr("state");
        string? rawJob = Attr("washerJobState")
                         ?? Attr("dryerJobState")
                         ?? Attr("jobState")
                         ?? Attr("operationalState.state");
        string? rawCompletion = Attr("completionTime");

        // Viele Samsung-Firmwares melden die Restzeit zusaetzlich direkt:
        // remainingTime { value, unit: "min" }, remainingTimeStr "02:44" und progress { value, unit: "%" }.
        double? reportedMinutes = ParseNumber(Attr("remainingTime.value"));
        double? reportedProgress = ParseNumber(Attr("progress.value"));
        if (reportedProgress is not null && reportedProgress.Value > 1.0)
            reportedProgress = reportedProgress.Value / 100.0;

        TimeSpan? reportedClock = ParseClock(Attr("remainingTimeStr"));
        if (reportedMinutes is null && reportedClock is not null)
            reportedMinutes = reportedClock.Value.TotalMinutes;

        MachineRunState state = MapMachineState(rawState);
        tracker.NoteState(state);

        DateTimeOffset? completion = ParseIso8601(rawCompletion);
        TimeSpan? remaining = null;

        if (state == MachineRunState.Running || state == MachineRunState.Paused)
        {
            TimeSpan? fromCompletion = null;
            if (completion is not null)
            {
                TimeSpan diff = completion.Value - DateTimeOffset.UtcNow;
                if (diff < TimeSpan.Zero) diff = TimeSpan.Zero;
                fromCompletion = diff;
            }

            // Die gemeldete Restzeit ist vertrauenswuerdiger als ein eventuell veralteter completionTime-Stempel.
            if (reportedMinutes is not null && reportedMinutes.Value >= 0.0)
            {
                remaining = TimeSpan.FromMinutes(Math.Round(reportedMinutes.Value));

                if (fromCompletion is not null &&
                    Math.Abs(remaining.Value.TotalMinutes - fromCompletion.Value.TotalMinutes) > 3.0)
                {
                    completion = DateTimeOffset.UtcNow + remaining.Value;
                }
            }
            else
            {
                remaining = fromCompletion;
            }

            if (remaining is not null)
                tracker.NoteRemaining(remaining.Value);
        }

        double progress = state switch
        {
            MachineRunState.Running => tracker.LastProgress,
            MachineRunState.Paused => tracker.LastProgress,
            MachineRunState.Finished => 1.0,
            MachineRunState.Idle => 0.0,
            _ => tracker.LastProgress
        };

        if (state is MachineRunState.Running or MachineRunState.Paused)
        {
            // Der gemeldete Fortschritt der Maschine schlaegt die eigene Restzeit-Schaetzung:
            // er stimmt auch dann, wenn das Widget erst mittendrin in einen Lauf einsteigt.
            if (reportedProgress is not null)
                progress = Math.Max(progress, Math.Clamp(reportedProgress.Value, 0.0, 1.0));

            // Ohne jede Zeitangabe bleibt nur die Phasenschaetzung als Fortschritts-Proxy.
            if (remaining is null)
            {
                double phaseProgress = EstimatePhaseProgress(kind, rawJob);
                if (phaseProgress > progress) progress = phaseProgress;
            }
        }

        progress = Math.Clamp(progress, 0.0, 1.0);

        if (state == MachineRunState.Running && remaining is not null && remaining.Value <= TimeSpan.FromSeconds(20))
            state = MachineRunState.Finished;

        // Phasen stehen als ASCII-Codes im Snapshot; uebersetzt wird erst bei der Anzeige (MachineCard).
        string phase = state switch
        {
            MachineRunState.Unreachable => PhaseCodes.Offline,
            MachineRunState.Idle => PhaseCodes.Standby,
            MachineRunState.Finished => PhaseCodes.Finish,
            MachineRunState.Paused => PhaseCodes.Pause,
            _ => MapJobPhase(kind, rawJob)
        };

        return new MachineSnapshot
        {
            Kind = kind,
            DeviceId = deviceId,
            DeviceLabel = CleanText(label),
            RunState = state,
            RawMachineState = rawState ?? string.Empty,
            RawJobState = rawJob ?? string.Empty,
            Phase = phase,
            Progress = progress,
            Remaining = remaining,
            CompletionUtc = completion,
            IsLive = true,
            IsError = false,
            StatusMessage = remaining is null && state == MachineRunState.Running
                ? LocalizationManager.Get("Str_Hint_EstimatedRemaining")
                : null,
            FetchedUtc = DateTimeOffset.UtcNow
        };
    }

    // ---------------- HTTP ----------------

    private async Task<JsonDocument> GetJsonAsync(string pathOrUrl, CancellationToken ct)
    {
        for (int attempt = 0; ; attempt++)
        {
            await EnsureAccessTokenAsync(ct).ConfigureAwait(false);

            HttpResponseMessage resp;
            try
            {
                if (Uri.TryCreate(pathOrUrl, UriKind.Absolute, out Uri? abs))
                    resp = await _http.GetAsync(abs, ct).ConfigureAwait(false);
                else
                    resp = await _http.GetAsync(pathOrUrl, ct).ConfigureAwait(false);
            }
            catch (TaskCanceledException)
            {
                throw new SmartThingsException("request timed out") { TextKey = "Str_Err_Timeout" };
            }
            catch (HttpRequestException ex)
            {
                throw new SmartThingsException("cannot reach api.smartthings.com")
                { TextKey = "Str_Err_ApiConnect", TextArgs = new object?[] { ex.Message } };
            }

            using (resp)
            {
                string body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

                // 401 mit OAuth- bzw. CLI-Sitzung: Access-Token evtl. serverseitig abgelaufen ->
                // einmalig auffrischen (CLI-Bridge laesst die CLI ihr Token erneuern) und wiederholen.
                if (resp.StatusCode == HttpStatusCode.Unauthorized && attempt == 0 &&
                    (_config.HasOAuthCredentials || _config.Config.SmartThings.UseCliManagedToken))
                {
                    if (_config.Config.SmartThings.UseCliManagedToken)
                    {
                        await SmartThingsCliAuth.RefreshByListingDevicesAsync(ct).ConfigureAwait(false);
                        string? fresh = SmartThingsCliAuth.ReadAccessToken();
                        if (!string.IsNullOrWhiteSpace(fresh))
                        {
                            string? current = _config.GetToken();
                            if (!string.Equals(current, fresh, StringComparison.Ordinal)) { _config.SetToken(fresh!); _config.Save(); }
                            SetAuthHeader(fresh);
                        }
                    }
                    else
                    {
                        await RefreshTokensAsync(ct).ConfigureAwait(false);
                    }
                    continue;
                }

                if (!resp.IsSuccessStatusCode)
                {
                    string? detail = TryExtractError(body);
                    throw new SmartThingsException(
                        detail ?? "HTTP " + (int)resp.StatusCode,
                        resp.StatusCode,
                        detail);
                }

                try
                {
                    return JsonDocument.Parse(body, new JsonDocumentOptions
                    {
                        CommentHandling = JsonCommentHandling.Skip,
                        AllowTrailingCommas = true
                    });
                }
                catch (JsonException)
                {
                    throw new SmartThingsException("response was not valid json", resp.StatusCode)
                    { TextKey = "Str_Err_BadJson" };
                }
            }
        }
    }

    private static string? TryExtractError(string body)
    {
        try
        {
            using JsonDocument doc = JsonDocument.Parse(body);
            string? ec = GetString(doc.RootElement, "error_code");
            string? em = GetString(doc.RootElement, "error_message");
            if (!string.IsNullOrWhiteSpace(ec) || !string.IsNullOrWhiteSpace(em))
                return (ec ?? "") + " " + (em ?? "");
        }
        catch
        {
            // Absichtlich still: Rohtext ist besser als nichts.
        }

        if (string.IsNullOrWhiteSpace(body)) return null;
        return body.Length > 160 ? body.Substring(0, 160) : body;
    }

    // ---------------- JSON-Lesehelfer (fehlertolerant) ----------------

    private static string? GetString(JsonElement el, string name)
    {
        if (el.ValueKind != JsonValueKind.Object) return null;
        if (!el.TryGetProperty(name, out JsonElement v)) return null;
        return v.ValueKind switch
        {
            JsonValueKind.String => v.GetString(),
            JsonValueKind.Number => v.ToString(),
            JsonValueKind.True => "true",
            JsonValueKind.False => "false",
            _ => null
        };
    }

    /// <summary>
    /// Sammelt eine Capability aus allen Komponenten (nicht nur aus "main") - inklusive praefixierter
    /// Schreibweisen wie "samsungce.washerOperatingState". Exakte Treffer stehen vorne.
    /// </summary>
    private static List<JsonElement> FindCapabilities(JsonElement root, string capability)
    {
        var found = new List<JsonElement>();
        if (root.ValueKind != JsonValueKind.Object) return found;

        if (root.TryGetProperty("components", out JsonElement components) && components.ValueKind == JsonValueKind.Object)
        {
            // "main" zuerst, danach alle anderen Komponenten.
            if (components.TryGetProperty("main", out JsonElement main) && main.ValueKind == JsonValueKind.Object)
                CollectCapabilities(main, capability, found);

            foreach (JsonProperty prop in components.EnumerateObject())
            {
                if (prop.Name == "main" || prop.Value.ValueKind != JsonValueKind.Object) continue;
                CollectCapabilities(prop.Value, capability, found);
            }
        }

        // Notfallpfad: Capability direkt auf der Wurzel.
        CollectCapabilities(root, capability, found);
        return found;
    }

    private static void CollectCapabilities(JsonElement holder, string capability, List<JsonElement> found)
    {
        foreach (JsonProperty prop in holder.EnumerateObject())
        {
            if (prop.Value.ValueKind == JsonValueKind.Object &&
                string.Equals(prop.Name, capability, StringComparison.OrdinalIgnoreCase))
                found.Add(prop.Value);
        }

        foreach (JsonProperty prop in holder.EnumerateObject())
        {
            if (prop.Value.ValueKind == JsonValueKind.Object &&
                prop.Name.EndsWith("." + capability, StringComparison.OrdinalIgnoreCase))
                found.Add(prop.Value);
        }
    }

    private static bool HasCapability(JsonElement root, string[] capabilityNames)
    {
        foreach (string name in capabilityNames)
            if (FindCapabilities(root, name).Count > 0)
                return true;
        return false;
    }

    /// <summary>Capability-IDs aus einer Geraeteliste lesen: components[].capabilities[].id.</summary>
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
                string? id = cap.ValueKind == JsonValueKind.String ? cap.GetString() : GetString(cap, "id");
                if (!string.IsNullOrWhiteSpace(id))
                    yield return id;
            }
        }
    }

    /// <summary>
    /// Capability-Vergleich mit Raum fuer praefixierte Varianten: Samsung liefert
    /// "washerOperatingState" und "samsungce.washerOperatingState".
    /// </summary>
    private static bool MatchesCapability(string candidate, string[] capabilityNames)
    {
        foreach (string name in capabilityNames)
        {
            if (string.Equals(candidate, name, StringComparison.OrdinalIgnoreCase))
                return true;
            if (candidate.EndsWith("." + name, StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }

    /// <summary>
    /// Liest einen Attributwert. SmartThings liefert Attribute als Array
    /// [ { "value": ..., "unit": ... } ], teils auch als direktes Objekt.
    /// </summary>
    private static string? GetAttributeValue(JsonElement holder, string attributePath)
    {
        if (holder.ValueKind != JsonValueKind.Object) return null;

        // Punkt-Pfad unterstuetzen (z. B. "operationalState.state").
        string[] parts = attributePath.Split('.');
        JsonElement current = holder;

        foreach (string part in parts)
        {
            // Manche Antworten wickeln Attributwerte als Array ein: [ { "value": ... } ]
            if (current.ValueKind == JsonValueKind.Array)
            {
                bool first = false;
                foreach (JsonElement el in current.EnumerateArray())
                {
                    current = el;
                    first = true;
                    break;
                }
                if (!first) return null;
            }

            if (current.ValueKind != JsonValueKind.Object) return null;
            if (!current.TryGetProperty(part, out JsonElement found))
            {
                // manche Antworten verschachteln die Capability-Objekte andersherum
                bool ok = false;
                foreach (JsonProperty prop in current.EnumerateObject())
                {
                    if (string.Equals(prop.Name, part, StringComparison.OrdinalIgnoreCase))
                    {
                        found = prop.Value;
                        ok = true;
                        break;
                    }
                }
                if (!ok) return null;
            }
            current = found;
        }

        return ValueOf(current);
    }

    private static string? ValueOf(JsonElement value)
    {
        switch (value.ValueKind)
        {
            case JsonValueKind.String:
                return value.GetString();
            case JsonValueKind.Number:
                return value.ToString();
            case JsonValueKind.True:
                return "true";
            case JsonValueKind.False:
                return "false";
            case JsonValueKind.Null:
            case JsonValueKind.Undefined:
                return null;
            case JsonValueKind.Array:
                foreach (JsonElement item in value.EnumerateArray())
                {
                    if (item.ValueKind == JsonValueKind.Object && item.TryGetProperty("value", out JsonElement inner))
                    {
                        string? s = ValueOf(inner);
                        if (!string.IsNullOrWhiteSpace(s)) return s;
                    }
                    else
                    {
                        string? s = ValueOf(item);
                        if (!string.IsNullOrWhiteSpace(s)) return s;
                    }
                }
                return null;
            case JsonValueKind.Object:
                if (value.TryGetProperty("value", out JsonElement v2))
                    return ValueOf(v2);
                return null;
        }
        return null;
    }

    // ---------------- Wert-Mapping ----------------

    private static MachineRunState MapMachineState(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return MachineRunState.Unknown;

        switch (raw.Trim().ToLowerInvariant())
        {
            case "run":
            case "running":
            case "start":
            case "started":
            case "active":
            case "working":
                return MachineRunState.Running;
            case "pause":
            case "paused":
                return MachineRunState.Paused;
            case "ready":
            case "stop":
            case "stopped":
            case "standby":
            case "idle":
                return MachineRunState.Idle;
            case "finish":
            case "finished":
            case "complete":
            case "completed":
                return MachineRunState.Finished;
            // Aeltere Samsung-Hubs lieferten Zahlen: 0 Standby, 1 Pause, 2 Running, 5 Finished.
            case "0":
                return MachineRunState.Idle;
            case "1":
                return MachineRunState.Paused;
            case "2":
                return MachineRunState.Running;
            case "5":
                return MachineRunState.Finished;
            default:
                return MachineRunState.Unknown;
        }
    }

    /// <summary>
    /// SmartThings-Job-Wert -> ASCII-Phasencode (z. B. "rinseSpin" -> "rinse").
    /// Die Uebersetzung in die Anzeigesprache erfolgt spaeter (MachineCard ueber Str_Phase_&lt;code&gt;),
    /// damit ein Sprachwechsel ohne Neustart wirkt.
    /// </summary>
    private static string MapJobPhase(MachineKind kind, string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return kind == MachineKind.Washer ? "wash" : "timedry";

        string key = raw.Trim().ToLowerInvariant().Replace("_", string.Empty).Replace("-", string.Empty);

        if (kind == MachineKind.Washer)
        {
            switch (key)
            {
                case "prewash":
                case "prewashsoak":
                case "soak":
                    return "prewash";
                case "wash":
                case "standard":
                case "normal":
                case "mainwash":
                case "steam":
                    return "wash";
                case "rinse":
                case "extrarinse":
                case "rinsespin":
                    return "rinse";
                case "spin":
                case "finalspin":
                    return "spin";
                case "dry":
                case "tumbledry":
                    return "dry";
                case "finish":
                case "completed":
                case "done":
                    return PhaseCodes.Finish;
                case "standby":
                    return PhaseCodes.Standby;
            }
        }
        else
        {
            switch (key)
            {
                case "presetdefault":
                case "normal":
                case "timedry":
                case "sensordry":
                case "dry":
                case "high":
                case "medium":
                case "low":
                    return "timedry";
                case "cooldown":
                case "cooling":
                case "cool":
                    return "cooldown";
                case "wrinkleprevent":
                case "wrinklecare":
                case "anticrease":
                case "antiwear":
                    return "anticrease";
                case "dehumidify":
                case "airdry":
                case "airwash":
                    return "dehumidify";
                case "preheat":
                    return "preheat";
                case "finish":
                case "completed":
                case "done":
                    return PhaseCodes.Finish;
                case "standby":
                    return PhaseCodes.Standby;
            }
        }

        // Unbekannte Phase: der normalisierte Rohwert bleibt stehen und wird angezeigt,
        // weil dafuer kein Sprachkey existiert (lesbarer als ein erfundener Name).
        string code = LocalizationManager.NormalizePhase(raw);
        if (code.Length > 0) return code;

        return kind == MachineKind.Washer ? "wash" : "timedry";
    }

    /// <summary>Grob geschaetzter Fortschritt anhand der Phase, wenn keine completionTime vorhanden ist.</summary>
    private static double EstimatePhaseProgress(MachineKind kind, string? rawJob)
    {
        string key = (rawJob ?? string.Empty).Trim().ToLowerInvariant().Replace("_", string.Empty);

        if (kind == MachineKind.Washer)
        {
            return key switch
            {
                "prewash" or "soak" => 0.10,
                "wash" or "standard" or "normal" => 0.35,
                "rinse" or "extrarinse" => 0.62,
                "spin" or "finalspin" or "rinsespin" => 0.85,
                "dry" => 0.95,
                "finish" or "completed" => 1.0,
                _ => 0.25
            };
        }

        return key switch
        {
            "preheat" => 0.08,
            "presetdefault" or "normal" or "timedry" or "sensordry" or "dry" => 0.45,
            "wrinkleprevent" or "wrinklecare" or "anticrease" => 0.80,
            "cooldown" or "cooling" or "cool" => 0.92,
            "finish" or "completed" => 1.0,
            _ => 0.30
        };
    }

    private static MachineKind? KindFromText(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        string t = text.ToLowerInvariant();

        if (t.Contains("washer") || t.Contains("washing") || t.Contains("wasch")) return MachineKind.Washer;
        if (t.Contains("dryer") || t.Contains("trockn") || t.Contains("dry ")) return MachineKind.Dryer;
        return null;
    }

    /// <summary>
    /// Entfernt versteckte Formatzeichen: Samsung sendet Geraetenamen teils mit Weichem Bindestrich
    /// (U+00AD) oder Zero-Width-Zeichen, was in WPF zu seltsamen Abstaenden fuehrt.
    /// </summary>
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

    /// <summary>Liest "hh:mm" bzw. "hh:mm:ss" (z. B. remainingTimeStr "02:44") als Zeitspanne.</summary>
    private static TimeSpan? ParseClock(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;

        string[] parts = raw.Trim().Split(':');
        if (parts.Length < 2 || parts.Length > 4) return null;

        if (!int.TryParse(parts[0].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int hours)) return null;
        if (!int.TryParse(parts[1].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int minutes)) return null;
        if (hours < 0 || minutes < 0) return null;

        int seconds = 0;
        if (parts.Length >= 3 && !int.TryParse(parts[2].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out seconds))
            seconds = 0;
        if (seconds < 0) seconds = 0;

        return new TimeSpan(hours, minutes, seconds);
    }

    /// <summary>Liest eine Zahl aus einem SmartThings-Wert (Komma oder Punkt werden toleriert).</summary>
    private static double? ParseNumber(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;

        string t = raw.Trim().Replace(',', '.');
        return double.TryParse(t, NumberStyles.Float, CultureInfo.InvariantCulture, out double value) && !double.IsNaN(value)
            ? value
            : null;
    }

    private static DateTimeOffset? ParseIso8601(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;

        if (DateTimeOffset.TryParse(
                raw.Trim(),
                CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                out DateTimeOffset parsed))
            return parsed.ToUniversalTime();

        // Manche Firmware varianten senden "2026-10-31 14:05:00Z".
        if (DateTimeOffset.TryParseExact(
                raw.Trim(),
                FallbackTimeFormats,
                CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                out DateTimeOffset exact))
            return exact.ToUniversalTime();

        return null;
    }

    public void Dispose()
    {
        _http.Dispose();
    }
}
