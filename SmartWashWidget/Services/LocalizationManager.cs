using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Windows;

namespace SmartWashWidget.Services;

/// <summary>
/// Zentraler Text-Vorrat fuer Deutsch und Englisch.
///
/// Die beiden ResourceDictionaries Resources/Strings.de.xaml und Resources/Strings.en.xaml
/// enthalten exakt dieselben Keys (Reihenfolge identisch). Dieser Dienst haelt zusaendlich
/// eine reine String-Kopie der aktiven Sprache im Speicher, damit auch Hintergrund-/Poll-/
/// OAuth-Threads ohne Zugriff auf WPF-Ressourcen (und damit thread-safe) uebersetzte Texte
/// bekommen koennen.
///
/// SetLanguage() tauscht das aktive MergedDictionary an Ort und Stelle: alle
/// {DynamicResource Str_...} in den Fenstern ziehen sofort neu - ohne Neustart.
/// Texte, die per Get()/Format() aus Code-Behind stammen, werden bei ihrem naechsten
/// Setzen in der neuen Sprache geliefert (Karten-/Statustexte werden laufend neu gesetzt).
/// </summary>
public static class LocalizationManager
{
    /// <summary>Config-Wert "auto": uebernimmt die Systemsprache (de, sonst en).</summary>
    public const string Auto = "auto";

    /// <summary>Sprache, die ohne jedes Zutun als letzte Absicherung greift.</summary>
    public const string FallbackLanguage = "de";

    private const string WasherKey = "Str_Device_Washer";
    private const string DryerKey = "Str_Device_Dryer";
    private const string GenericKey = "Str_Device_Generic";

    private static readonly object _gate = new object();

    /// <summary>Geladene Sprach-Vorraete (Key -> String), damit Sprachwechsel billig sind.</summary>
    private static readonly Dictionary<string, Dictionary<string, string>> _cache =
        new Dictionary<string, Dictionary<string, string>>(StringComparer.Ordinal);

    /// <summary>Rueckwaertssuche Text -> Key pro Sprache (fuer bereits angezeigte Meldungen).</summary>
    private static readonly Dictionary<string, Dictionary<string, string>> _reverseCache =
        new Dictionary<string, Dictionary<string, string>>(StringComparer.Ordinal);

    /// <summary>Volatile Kopie der aktiven Sprache: Lesen ohne Sperre, Switchen ohne Riss.</summary>
    private static volatile Dictionary<string, string> _active =
        new Dictionary<string, string>(StringComparer.Ordinal);

    /// <summary>Volatile Kopie der Ersatz-Sprache (fallt ein, wenn ein Key fehlt).</summary>
    private static volatile Dictionary<string, string> _alternate =
        new Dictionary<string, string>(StringComparer.Ordinal);

    /// <summary>Das im Application.Resources aktive Sprachdictionary (wird ausgetauscht).</summary>
    private static ResourceDictionary? _mergedDict;

    private static string _language = FallbackLanguage;

    /// <summary"Aktive Zweibuchstaben-Sprache ("de" oder "en").</summary>
    public static string ActiveLanguage
    {
        get { lock (_gate) return _language; }
    }

    /// <summary>true, wenn gerade Englisch aktiv ist.</summary>
    public static bool IsEnglish => ActiveLanguage == "en";

    /// <summary>
    /// Feuert, nachdem alle Vorrate und das XAML-Dictionary umgestellt sind.
    /// Uebergibt die bisherige Sprache, damit Fenster bereits angezeigte Meldungen
    /// (Relocalize) in der neuen Sprache nachreichen koennen.
    /// </summary>
    public static event Action<string>? LanguageChanged;

    // ---------------- Initialisieren / Umschalten ----------------

    /// <summary>
    /// Einmal beim Start aufzurufen, sobald die Application existiert.
    /// Loescht ein eventuell in App.xaml bereits gemergtes Sprachdictionary und setzt die
    /// gewuenschte Sprache, ohne bereits erzeugte Fenster zu storen.
    /// </summary>
    public static void Initialize(string? requested)
    {
        string lang = Resolve(requested);

        lock (_gate)
        {
            _language = lang;
            _active = Load(lang);
            _alternate = Load(Other(lang));
            MergeIntoApplication(lang);
        }

        ApplyCulture(lang);
    }

    /// <summary>Stellt die Sprache zur Laufzeit um (UI-Thread). Liefert die aktive Sprache zurueck.</summary>
    public static string SetLanguage(string? requested)
    {
        string lang = Resolve(requested);
        string previous;

        lock (_gate)
        {
            previous = _language;

            if (lang == _language && _mergedDict is not null)
            {
                ApplyCulture(lang);
                return lang;
            }

            _language = lang;
            Dictionary<string, string> active = Load(lang);
            Dictionary<string, string> alternate = Load(Other(lang));

            // Erst die String-Kopien, dann das XAML: so ziehen DynamicResource und
            // Code-Texte moeglichst in derselben Sprache.
            _alternate = alternate;
            _active = active;
            MergeIntoApplication(lang);
            ApplyCulture(lang);
        }

        LanguageChanged?.Invoke(previous);
        return lang;
    }

    // ---------------- Textzugriff ----------------

    /// <summary>Holt einen Text in der aktiven Sprache; fehlende Keys liefern den Key selbst.</summary>
    public static string Get(string key)
    {
        if (string.IsNullOrEmpty(key)) return string.Empty;

        Dictionary<string, string> active = _active;
        if (active.TryGetValue(key, out string? text) && !string.IsNullOrEmpty(text))
            return text;

        Dictionary<string, string> alternate = _alternate;
        if (alternate.TryGetValue(key, out string? other) && !string.IsNullOrEmpty(other))
            return other;

        return key;
    }

    /// <summary>Liefert den Text oder false, wenn die aktive Sprache keinen Eintrag kennt.</summary>
    public static bool TryGet(string key, out string text)
    {
        text = string.Empty;
        if (string.IsNullOrEmpty(key)) return false;

        Dictionary<string, string> active = _active;
        if (active.TryGetValue(key, out string? found) && !string.IsNullOrEmpty(found))
        {
            text = found;
            return true;
        }

        return false;
    }

    /// <summary>
    /// Holt einen Text, der schon in einer anderen Sprache angezeigt wurde, in die aktive Sprache.
    /// Findet der Rueckwaertsspeicher den Text nicht (z. B. eine Meldung mit teknischem Anhang),
    /// bleibt der Text unveraendert - besser als ihn sprachlos verschwinden zu lassen.
    /// </summary>
    public static string Relocalize(string? previousText, string previousLanguage)
    {
        if (string.IsNullOrEmpty(previousText)) return string.Empty;
        if (previousLanguage == ActiveLanguage) return previousText!;

        Dictionary<string, string> reverse = ReverseIndexOf(previousLanguage);
        if (reverse.TryGetValue(previousText!, out string? key))
            return Get(key);

        return previousText!;
    }

    private static Dictionary<string, string> ReverseIndexOf(string lang)
    {
        lock (_gate)
        {
            if (_reverseCache.TryGetValue(lang, out Dictionary<string, string>? known))
                return known;

            var reverse = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (KeyValuePair<string, string> entry in Load(lang))
            {
                if (!string.IsNullOrEmpty(entry.Value) && !reverse.ContainsKey(entry.Value))
                    reverse[entry.Value] = entry.Key;
            }

            _reverseCache[lang] = reverse;
            return reverse;
        }
    }

    /// <summary>Formatvorlage der aktiven Sprache (Str_Fmt_...), args wie bei string.Format.</summary>
    public static string Format(string key, params object?[] args)
    {
        try
        {
            return string.Format(CultureInfo.CurrentUICulture, Get(key), args);
        }
        catch (FormatException)
        {
            return Get(key);
        }
    }

    /// <summary>Lokalisierter Geraetename (Waschmaschine / Trockner) fuer die Anzeige.</summary>
    public static string DeviceName(Models.MachineKind kind) => Get(DeviceKey(kind));
    /// <summary>Der Key, unter dem der Geraetename in einem Sprachdictionary steht.</summary>
    public static string DeviceKey(Models.MachineKind kind) => kind switch
    {
        Models.MachineKind.Washer => WasherKey,
        Models.MachineKind.Dryer => DryerKey,
        _ => GenericKey
    };

    /// <summary>
    /// Uebersetzt einen ASCII-Phasencode (z. B. "wash", "cool_down" -> "cooldown").
    /// Unbekannte Codes werden unveraendert zurueckgegeben (sie sind die Ausnahme, nicht die Regel).
    /// </summary>
    public static string PhaseName(string? code)
    {
        if (string.IsNullOrWhiteSpace(code)) return Get("Str_Status_Unknown");

        string key = "Str_Phase_" + NormalizePhase(code!);
        return TryGet(key, out string name) ? name : code!;
    }

    /// <summary>Macht aus einem SmartThings-Job-Wert einen Key-tauglichen Phasencode.</summary>
    public static string NormalizePhase(string raw)
    {
        string code = raw.Trim().ToLowerInvariant();

        // SmartThings schreibt "cool_down", "dehumidify_mode" ... -> nur der erste Wortteil zaehlt.
        int cut = code.Length;
        for (int i = 0; i < code.Length; i++)
        {
            char c = code[i];
            if (c < 'a' || c > 'z') { cut = i; break; }
        }
        if (cut == 0) cut = code.Length;

        return code.Substring(0, cut);
    }

    // ---------------- Sprache aufloesen ----------------

    /// <summary>Macht aus "auto"/"de"/"en"/Systemsprache immer eine echte Zweibuchstaben-Sprache.</summary>
    public static string Resolve(string? requested)
    {
        string req = (requested ?? string.Empty).Trim().ToLowerInvariant();

        if (req == "de" || req == "en") return req;
        if (req.Length == 0 || req == Auto) return SystemLanguage();

        // Fehlerhafte Konfig: Systemsprache, damit das Widget nie sprachlos dasteht.
        return SystemLanguage();
    }

    /// <summary>Systemsprache: Deutsch nur bei deutsch eingestelltem Windows, sonst Englisch.</summary>
    public static string SystemLanguage()
    {
        string iso = (CultureInfo.CurrentUICulture.TwoLetterISOLanguageName ?? string.Empty)
            .ToLowerInvariant();
        return iso == "de" ? "de" : "en";
    }

    /// <summary>Die jeweils andere Sprache (fur die Absicherung fehlender Keys).</summary>
    private static string Other(string lang) => lang == "de" ? "en" : "de";

    private static void ApplyCulture(string lang)
    {
        try
        {
            CultureInfo culture = new CultureInfo(lang);
            CultureInfo.CurrentUICulture = culture;
            CultureInfo.DefaultThreadCurrentUICulture = culture;
        }
        catch (CultureNotFoundException)
        {
            // Die Sprache ist dem System unbekannt: bei der Invariant-Kultur bleiben.
        }
    }

    // ---------------- Sprachdictionaries laden und einhaengen ----------------

    private static Dictionary<string, string> Load(string lang)
    {
        lock (_gate)
        {
            if (_cache.TryGetValue(lang, out Dictionary<string, string>? cached))
                return cached;

            var map = new Dictionary<string, string>(StringComparer.Ordinal);

            try
            {
                ResourceDictionary dict = CreateDictionary(lang);
                foreach (DictionaryEntry entry in dict)
                {
                    if (entry.Key is string key && entry.Value is string value)
                        map[key] = value;
                }
            }
            catch (Exception)
            {
                // Sprachdatei nicht lesbar: der Fallback auf die Ersatzsprache fuehrt sich selbst.
            }

            _cache[lang] = map;
            return map;
        }
    }

    private static ResourceDictionary CreateDictionary(string lang)
        => new ResourceDictionary
        {
            Source = new Uri(
                "pack://application:,,,/Resources/Strings." + lang + ".xaml",
                UriKind.Absolute)
        };

    /// <summary>
    /// Sorgt dafuer, dass genau EIN Sprachdictionary in Application.Resources liegt:
    /// App.xaml mergt Strings.de.xaml als Startsprache; hier wird es durch das aktive ersetzt.
    /// </summary>
    private static void MergeIntoApplication(string lang)
    {
        Application? app = Application.Current;
        if (app is null) return;

        var merged = app.Resources.MergedDictionaries;

        int index = -1;
        for (int i = 0; i < merged.Count; i++)
        {
            ResourceDictionary d = merged[i];
            if (ReferenceEquals(d, _mergedDict)) { index = i; break; }

            // Der in App.xaml vorgemergte Sprachvorrat wird ebenfalls erkannt und ersetzt,
            // damit nie zwei Sprachen gleichzeitig aktiv sind.
            if (IsLanguageDictionary(d)) { index = i; break; }
        }

        ResourceDictionary fresh = CreateDictionary(lang);

        if (index >= 0) merged[index] = fresh;
        else merged.Add(fresh);

        _mergedDict = fresh;
    }

    private static bool IsLanguageDictionary(ResourceDictionary dict)
    {
        Uri? source = dict.Source;
        if (source is null) return false;

        string origin = source.OriginalString;
        return origin.Contains("Resources/Strings.", StringComparison.OrdinalIgnoreCase);
    }
}
