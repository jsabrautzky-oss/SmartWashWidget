using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;

using SmartWashWidget.Controls;
using SmartWashWidget.Interop;
using SmartWashWidget.Models;
using SmartWashWidget.Services;

namespace SmartWashWidget
{
    /// <summary>
    /// Kompositionsroot: Konfiguration, Einzelinstanz-Schutz, Frame-Loop, Demo-Simulation,
    /// SmartThings-Polling, Tray-Icon und die beiden Fenster.
    /// </summary>
    public partial class App : Application
    {
        private const string MutexName = "Local\\SmartWashWidget.SingleInstance.v1";

        /// <summary>Ein einziger Frame-Loop fuer Trommeln, Wellen, Partikel und Demo-Tick.</summary>
        public AnimationEngine Engine { get; } = new AnimationEngine();

        public DemoSimulation Demo { get; } = new DemoSimulation();

        private readonly AppConfigService _cfg = new AppConfigService();

        private Mutex? _instanceMutex;
        private bool _ownsMutex;
        private bool _shuttingDown;

        private MainWindow? _main;
        private SettingsWindow? _settings;
        private TrayIconService? _tray;
        private PollingCoordinator? _polling;
        private DemoPump? _demoPump;
        private UpdateService? _update;

        private bool _openSettingsAtStartup;

        /// <summary>Beendet den Hintergrund-Check fuer die OAuth2-Token-Erneuerung.</summary>
        private readonly CancellationTokenSource _oauthRefreshCts = new CancellationTokenSource();

        public App()
        {
            ShutdownMode = ShutdownMode.OnLastWindowClose;
        }

        // ---------------- Oeffentliche Zugriffe ----------------

        /// <summary>Die aktiven Einstellungen (Kurzzugriff fuer Fenster).</summary>
        public WidgetConfig Config => _cfg.Config;

        /// <summary>Der Konfigurationsdienst inkl. Token-(De)Serialisierung.</summary>
        public AppConfigService ConfigService => _cfg;

        public MainWindow? Widget => _main;
        public SettingsWindow? Settings => _settings;
        public TrayIconService? Tray => _tray;

        /// <summary>Der GitHub-Update-Checker (null, solange er noch nicht erzeugt wurde).</summary>
        public UpdateService? Updates => _update;

        public bool DemoMode => Config.DemoMode;

        public double DemoTempo => Math.Clamp(Config.DemoTempo, 1.0, 24.0);

        public string? GlassDetail => _main?.GlassDetail;

        /// <summary>true, wenn hinter dem Widget gerade weichgezeichnet wird.</summary>
        public bool GlassActive => _main?.GlassActive ?? false;

        // ---------------- Start ----------------

        protected override void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);

            DispatcherUnhandledException += OnUnhandledException;

            if (!TryAcquireSingleInstance())
            {
                // Bereits eine Instanz laeuft -> still beenden, kein zweites Widget auf dem Desktop.
                Shutdown();
                return;
            }

            if (HasFlag(e.Args, "--reset"))
                _cfg.Reset();

            ApplyArguments(e.Args);

            // Sprachvorrat waehlen, bevor das erste Fenster Texte aus ihm zieht.
            LocalizationManager.Initialize(Config.Language);
            LocalizationManager.LanguageChanged += OnLanguageChanged;

            _main = new MainWindow(this);
            _main.Show();

            if (_cfg.ConfigurationErrorKey is not null)
                _main.SetFooterNote(_cfg.ConfigurationErrorKey, _cfg.ConfigurationErrorArgs);

            _tray = new TrayIconService(this);

            bool hasToken = _cfg.HasToken;

            if (Config.DemoMode || !hasToken)
            {
                Config.DemoMode = true;
                StartDemo(LocalizationManager.Get(hasToken
                    ? "Str_Hint_DemoActive"
                    : "Str_Hint_DemoNoToken"));
            }
            else
            {
                StartLive();
            }

            _main.ApplyWindowSettings();
            _main.RefreshMenuChecks();
            _tray.Refresh();

            // OAuth2: Access-Token im Hintergrund erneuern, bevor er ablaueft - auch wenn das
            // Widget tagelang ausgeschaltet war (Refresh passiert beim naechsten App-Start).
            StartOAuthBackgroundRefresh();

            // Update-Check: 5 s nach dem Start das erste Mal, danach alle 4 h im Hintergrund.
            StartUpdateChecks();

            // Autostart-Spiegelung: der Run-Eintrag des Benutzers ist die Wahrheit, config.json
            // fuehrt nur, was Windows beim naechsten Anmelden wirklich starten wuerde.
            Config.RunAtStartup = AutostartService.IsEnabled();

            // "--settings" oeffnet das Einstellungsfenster direkt nach dem Start.
            if (_openSettingsAtStartup)
            {
                _openSettingsAtStartup = false;
                OpenSettings();
            }
        }

        private void ApplyArguments(string[] args)
        {
            for (int i = 0; i < args.Length; i++)
            {
                string raw = args[i].Trim();
                string flag = raw.ToLowerInvariant();

                // "--lang de", "--lang=de": Sprache beim Start vorgeben (persistiert wie gewoehnlich).
                if (flag == "--lang" || flag.StartsWith("--lang="))
                {
                    string value = flag.StartsWith("--lang=")
                        ? raw.Substring(raw.IndexOf('=') + 1).Trim()
                        : (i + 1 < args.Length ? args[i + 1].Trim() : string.Empty);

                    Config.Language = AppConfigService.SanitizeLanguage(value);
                    if (flag == "--lang") i++;
                    continue;
                }

                switch (flag)
                {
                    case "--demo":
                        Config.DemoMode = true;
                        break;
                    case "--live":
                        if (_cfg.HasToken) Config.DemoMode = false;
                        break;
                    case "--settings":
                        _openSettingsAtStartup = true;
                        break;
                }
            }
        }

        private static bool HasFlag(string[] args, string flag)
        {
            foreach (string a in args)
                if (string.Equals(a.Trim(), flag, StringComparison.OrdinalIgnoreCase))
                    return true;
            return false;
        }

        // ---------------- Demo-Modus ----------------

        private void StartDemo(string hint)
        {
            StopPolling();

            if (_demoPump is null)
            {
                _demoPump = new DemoPump(this);
                Engine.Register(_demoPump);
            }
            else
            {
                Engine.Wake();
            }

            if (_main is not null)
            {
                _main.SetSetupHint(hint);
                SetConnection(ConnectionState.Demo);
                _main.ShowSnapshot(Demo.Washer());
                _main.ShowSnapshot(Demo.Dryer());
            }
        }

        /// <summary>Veroeffentlicht den aktuellen Demo-Zustand auf beiden Karten.</summary>
        private void PublishDemo()
        {
            _main?.ShowSnapshot(Demo.Washer());
            _main?.ShowSnapshot(Demo.Dryer());
        }

        // ---------------- Live-Modus ----------------

        private void StartLive()
        {
            if (!_cfg.HasToken)
            {
                // Defensive Sicherheit: ohne Token kann live nichts laufen.
                Config.DemoMode = true;
                StartDemo(LocalizationManager.Get("Str_Hint_NoTokenDemo"));
                return;
            }

            if (_polling is null)
            {
                _polling = new PollingCoordinator(_cfg);
                _polling.SnapshotReady += OnSnapshot;
                _polling.DeviceFailed += OnDeviceFailed;
                _polling.ConnectionFailed += OnConnectionFailed;
            }

            if (_demoPump is not null)
            {
                Engine.Unregister(_demoPump);
                _demoPump = null;
            }

            _polling.ResetTrackers();
            _polling.Start();

            _main?.SetSetupHint(string.Empty);
            SetConnection(ConnectionState.Live);

            // Frisch gesetzter Token, aber noch keine Device-IDs bekannt?
            // Geraete im Hintergrund suchen und automatisch zuordnen - die Animation laeuft weiter.
            if (Config.SmartThings.AutoDiscover &&
                string.IsNullOrWhiteSpace(Config.SmartThings.WasherDeviceId) &&
                string.IsNullOrWhiteSpace(Config.SmartThings.DryerDeviceId))
            {
                _ = AutoConfigureAsync();
            }
        }

        /// <summary>
        /// Live-Start ohne bekannte Device-IDs: Geraetesuche + automatische Zuordnung im Hintergrund.
        /// </summary>
        private async Task AutoConfigureAsync()
        {
            try
            {
                List<DiscoveredDevice> found = await DiscoverDevicesAsync(24);

                if (found.Count == 0)
                {
                    UI(() => _main?.SetSetupHint(LocalizationManager.Get("Str_Discover_None")));
                    return;
                }

                if (AutoAssign(found))
                {
                    UI(() =>
                    {
                        _main?.SetSetupHint(string.Empty);
                        _main?.ClearFooterNote();
                        RestartPolling();
                    });
                }
                else
                {
                    UI(() => _main?.SetSetupHint(LocalizationManager.Get("Str_Discover_Manual")));
                }
            }
            catch (SmartThingsException ex)
            {
                UI(() => OnConnectionFailed(ex.UserText));
            }
            catch (Exception ex)
            {
                UI(() => OnConnectionFailed(LocalizationManager.Format("Str_Discover_Failed", ex.Message)));
            }
        }

        private void StopPolling()
        {
            if (_polling is null) return;
            _polling.SnapshotReady -= OnSnapshot;
            _polling.DeviceFailed -= OnDeviceFailed;
            _polling.ConnectionFailed -= OnConnectionFailed;
            _polling.Dispose();
            _polling = null;
        }

        // ---------------- OAuth2-Hintergrund-Refresh ----------------

        /// <summary>
        /// Laeuft einmal pro Stunde und erneuert den SmartThings-Access-Token rechtzeitig vor
        /// Ablauf (Refresh beginnt, wenn unter 12 h Restlaufzeit bleiben). Damit bleibt die
        /// Verbindung dauerhaft aktiv - auch ohne offenes Widget oder laufende Maschinen.
        /// </summary>
        private void StartOAuthBackgroundRefresh()
        {
            _ = Task.Run(() => OAuthRefreshLoopAsync(_oauthRefreshCts.Token));
        }

        private async Task OAuthRefreshLoopAsync(CancellationToken ct)
        {
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    await SmartThingsClient.RefreshIfExpiringAsync(_cfg, TimeSpan.FromHours(12), ct).ConfigureAwait(false);
                }
                catch
                {
                    // Ein fehlgeschlagener Refresh ist unkritisch: Der naechste Check versucht es erneut.
                    // (Die Live-Anfragen im Poller koennen die Sitzung ebenfalls jederzeit auffrischen.)
                }

                try
                {
                    await Task.Delay(TimeSpan.FromHours(1), ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
            }
        }

        // ---------------- Update-Check (GitHub-Releases) ----------------

        /// <summary>
        /// Holt sich den Update-Pruefer (und haengt seinen Fund an die Fenster weiter).
        /// </summary>
        private UpdateService EnsureUpdateService()
        {
            if (_update is null)
            {
                _update = new UpdateService(_cfg);
                _update.UpdateFound += OnUpdateFound;
            }

            return _update;
        }

        /// <summary>
        /// Startet die Hintergrund-Schleife: 5 s nach dem App-Start der erste Check, danach alle
        /// 4 h. Die Option AutoUpdateCheck wird bei jedem Tick neu gelesen - wer sie in den
        /// Einstellungen einschalaltet, wird also ohne Neustart wieder ueberprueft.
        /// </summary>
        private void StartUpdateChecks() => EnsureUpdateService().Start();

        /// <summary>
        /// Ein Check hat ein neueres Release gefunden. Kommt aus dem Hintergrund-Thread und
        /// wandert ueber den Dispatcher in Widget-Badge und Einstellungsfenster.
        /// </summary>
        private void OnUpdateFound(UpdateInfo info)
        {
            if (info is null) return;

            UI(() =>
            {
                _main?.ShowUpdate(info);
                _settings?.RefreshUpdateStatus();
            });
        }

        /// <summary>
        /// Einzelner Check von Hand (Button "Nach Updates suchen"). Liefert das Ergebnis -
        /// Netzwerkstoerungen kommen als UpdateCheckStatus.Failed zurueck, nie als Ausnahme.
        /// </summary>
        public async Task<UpdateCheckResult> CheckForUpdatesNowAsync()
        {
            UpdateService update = EnsureUpdateService();
            return await update.CheckAsync(CancellationToken.None);
        }

        /// <summary>
        /// Option "Automatisch nach Updates suchen" an/aus: speichern, und beim Einschalten sofort
        /// einmal pruefen (sonst wuerde der naechste Tick bis zu 4 Stunden auf sich warten lassen).
        /// </summary>
        public void SetAutoUpdateCheck(bool enabled)
        {
            if (Config.AutoUpdateCheck == enabled) return;

            Config.AutoUpdateCheck = enabled;
            SaveConfig();

            EnsureUpdateService();

            if (enabled)
                _ = CheckOnceAsync();
        }

        /// <summary>Sofort-Check aus den Einstellungen: Badge und Statuszeile nachziehen.</summary>
        private async Task CheckOnceAsync()
        {
            UpdateCheckResult result = await CheckForUpdatesNowAsync();

            UI(() =>
            {
                if (result.Status == UpdateCheckStatus.UpdateAvailable && result.Info is not null)
                    _main?.ShowUpdate(result.Info);

                _settings?.RefreshUpdateStatus();
            });
        }

        /// <summary>
        /// Klick auf das Widget-Badge: oeffnet die Release-Seite des gefundenen Releases im
        /// Standard-Browser (UseShellExecute, damit Windows den angemeldeten Browser waehlt).
        /// </summary>
        public void OpenLatestRelease()
        {
            UpdateInfo? info = _update?.Latest;
            string target = info?.ReleaseUrl ?? UpdateService.ReleasesPageUrl;

            try
            {
                Process.Start(new ProcessStartInfo(target) { UseShellExecute = true });
            }
            catch
            {
                // Kein Browser zugeordnet oder URL abgelehnt: das Widget darf daran nicht scheitern.
            }
        }

        public void RestartPolling()
        {
            if (Config.DemoMode) return;
            if (_polling is null) { StartLive(); return; }
            _polling.Restart();
        }

        private void OnSnapshot(MachineSnapshot snap)
        {
            UI(() =>
            {
                _main?.ShowSnapshot(snap);
                SetConnection(ConnectionState.Live);
                _main?.SetSetupHint(string.Empty);
            });
        }

        private void OnDeviceFailed(MachineKind kind, string message)
        {
            UI(() =>
            {
                _main?.ShowDeviceError(kind, message);
                SetConnection(ConnectionState.Degraded);
            });
        }

        private void OnConnectionFailed(string message)
        {
            UI(() =>
            {
                SetConnection(ConnectionState.Error);
                _main?.SetSetupHint(message);
                _main?.SetFooter(message);
            });
        }

        /// <summary>Kopfzeile im Widget und Tray-Symbol gemeinsam umfaerben (Text aus dem Sprachvorrat).</summary>
        private void SetConnection(ConnectionState state)
        {
            _main?.SetConnectionState(state);
            _tray?.SetConnection(state);
        }

        // ---------------- Einstellungen / Modus umschalten ----------------

        /// <summary>
        /// Sprache umschalten ("auto", "de" oder "en"): wirkt sofort auf Widgets, Tray,
        /// Einstellungsfenster und alle kuenftigen Meldungen - ohne Neustart. Der Code wird gespeichert.
        /// </summary>
        public void SetLanguage(string? code)
        {
            string stored = AppConfigService.SanitizeLanguage(code);

            Config.Language = stored;
            SaveConfig();

            // Der Wechsel tauscht den Sprachvorrat und feuert LanguageChanged: alle Fenster ziehen nach.
            LocalizationManager.SetLanguage(stored);
        }

        /// <summary>Reaktion auf den Sprachwechsel: Karten, Kopf-/Fusszeile, Menues, Tray, Einstellungen.</summary>
        private void OnLanguageChanged(string previousLanguage)
        {
            _main?.RefreshLanguage(previousLanguage);
            _tray?.Refresh();
            _settings?.ReflectState();
        }

        public void SaveConfig() => _cfg.Save();

        public void ApplyWindowSettings()
        {
            _main?.ApplyWindowSettings();
            _main?.RefreshMenuChecks();
            _settings?.ReflectState();
            _tray?.Refresh();
        }

        public void OpenSettings()
        {
            if (_main is null) return;

            if (_settings is null)
                _settings = new SettingsWindow(this);

            if (!_settings.IsVisible)
                _settings.Show();

            _settings.ReflectState();
            _settings.Activate();
        }

        /// <summary>
        /// Einmal geschlossene WPF-Fenster lassen sich nicht erneut anzeigen. SettingsWindow
        /// meldet sich hier ab, App verwirft den Cache - der naechste OpenSettings-Aufruf
        /// baut ein frisches Fenster (die Einstellungen kommen aus der Konfiguration).
        /// </summary>
        public void SettingsClosed(SettingsWindow window)
        {
            if (ReferenceEquals(_settings, window))
                _settings = null;
        }

        public void ToggleDemoMode() => SetDemoMode(!Config.DemoMode);

        public void SetDemoMode(bool enabled)
        {
            if (!enabled && !_cfg.HasToken)
            {
                Config.DemoMode = true;
                StartDemo(LocalizationManager.Get("Str_Hint_NoSmartThingsToken"));
                SaveConfig();
                ApplyWindowSettings();
                return;
            }

            Config.DemoMode = enabled;

            if (enabled)
                StartDemo(LocalizationManager.Get("Str_Hint_DemoActive"));
            else
                StartLive();

            SaveConfig();
            _main?.RefreshMenuChecks();
            _settings?.ReflectState();
            _tray?.Refresh();
        }

        /// <summary>Milchglas-Blur (Acrylic) an oder aus - live auf das sichtbare Fenster.</summary>
        public void SetAcrylicBlur(bool on)
        {
            if (Config.Window.AcrylicBlur == on)
                return;

            Config.Window.AcrylicBlur = on;
            ApplyWindowSettings();
            SaveConfig();

            _main?.RefreshMenuChecks();
            _settings?.ReflectState();
            _tray?.Refresh();
        }

        public void ToggleAcrylicBlur() => SetAcrylicBlur(!Config.Window.AcrylicBlur);

        // ---------------- Sichtbarkeit ----------------

        public void ShowWidget()
        {
            if (_main is null) return;
            _main.Show();
            Engine.IsPaused = false;
            _tray?.Refresh();
        }

        public void HideWidget()
        {
            if (_main is null) return;
            _main.Hide();
            Engine.IsPaused = true;
            _tray?.Refresh();
            _tray?.Notify(LocalizationManager.Get("Str_AppName"), LocalizationManager.Get("Str_Tray_BalloonText"));
        }

        public void ShutdownWidget()
        {
            if (_shuttingDown) return;
            _shuttingDown = true;

            try { _oauthRefreshCts.Cancel(); } catch { /* egal */ }

            try { _update?.Dispose(); } catch { /* egal */ }
            _update = null;

            try { _tray?.Dispose(); } catch { /* egal */ }
            _tray = null;

            StopPolling();

            try { Engine.Dispose(); } catch { /* egal */ }
            try { _main?.Close(); } catch { /* egal */ }
        }

        // ---------------- SmartThings-Helfer fuer das Einstellungsfenster ----------------

        public async Task<List<DiscoveredDevice>> DiscoverDevicesAsync(int maxCandidates)
        {
            if (!_cfg.HasToken && !_cfg.HasOAuthCredentials)
                throw new SmartThingsException("no smartthings access stored")
                { TextKey = "Str_Err_NoAccess" };

            using SmartThingsClient client = new SmartThingsClient(_cfg, _cfg.Config.SmartThings.ApiBase);
            List<DiscoveredDevice> found = await client.DiscoverDevicesAsync(maxCandidates, CancellationToken.None);

            _cfg.Save();
            return found;
        }

        public async Task<int> TestConnectionAsync()
        {
            if (!_cfg.HasToken && !_cfg.HasOAuthCredentials)
                throw new SmartThingsException("no smartthings access stored")
                { TextKey = "Str_Err_NoAccess" };

            using SmartThingsClient client = new SmartThingsClient(_cfg, _cfg.Config.SmartThings.ApiBase);
            int n = await client.ValidateTokenAsync(CancellationToken.None);
            _cfg.RememberSync(null);
            _cfg.Save();
            return n;
        }

        /// <summary>Bevoelzerte Zuordnung, wenn genau eine Waschmaschine / ein Trockner erkannt wurde.</summary>
        public bool AutoAssign(List<DiscoveredDevice> devices)
        {
            DiscoveredDevice? washer = null;
            DiscoveredDevice? dryer = null;

            foreach (DiscoveredDevice d in devices)
            {
                if (washer is null && (d.HasWasherCapability || d.InferredKind == MachineKind.Washer))
                    washer = d;
                if (dryer is null && (d.HasDryerCapability || d.InferredKind == MachineKind.Dryer))
                    dryer = d;
            }

            bool changed = false;

            if (washer is not null)
            {
                _cfg.Config.SmartThings.WasherDeviceId = washer.Id;
                _cfg.Config.SmartThings.WasherLabel = washer.Display;
                changed = true;
            }
            if (dryer is not null)
            {
                _cfg.Config.SmartThings.DryerDeviceId = dryer.Id;
                _cfg.Config.SmartThings.DryerLabel = dryer.Display;
                changed = true;
            }

            if (changed) _cfg.Save();
            return changed;
        }

        // ---------------- Kleinigkeiten ----------------

        private void UI(Action action)
        {
            if (_main is null) return;
            if (_main.Dispatcher.CheckAccess()) action();
            else _main.Dispatcher.BeginInvoke(action, DispatcherPriority.Normal);
        }

        private bool TryAcquireSingleInstance()
        {
            try
            {
                _instanceMutex = new Mutex(true, MutexName, out bool createdNew);
                _ownsMutex = createdNew;

                if (!createdNew)
                {
                    _instanceMutex.Dispose();
                    _instanceMutex = null;
                    return false;
                }
                return true;
            }
            catch
            {
                _instanceMutex = null;
                _ownsMutex = false;
                return true; // Mutex nicht moeglich -> Instanz trotzdem starten
            }
        }

        private void OnUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
        {
            try
            {
                Directory.CreateDirectory(AppConfigService.DirectoryPath);
                File.AppendAllText(
                    Path.Combine(AppConfigService.DirectoryPath, "error.log"),
                    DateTime.Now.ToString("s") + "  " + e.Exception + Environment.NewLine);
            }
            catch
            {
                // Logging darf niemals selbst zum Absturz fuehren.
            }

            e.Handled = true;
        }

        protected override void OnExit(ExitEventArgs e)
        {
            ShutdownWidget();

            try { _cfg.Save(); } catch { /* egal */ }

            if (_ownsMutex && _instanceMutex is not null)
            {
                try { _instanceMutex.ReleaseMutex(); } catch { /* egal */ }
                try { _instanceMutex.Dispose(); } catch { /* egal */ }
                _instanceMutex = null;
            }

            base.OnExit(e);
        }

        /// <summary>Treibende Hand der Demo-Simulation: Demo-Tick und Snapshot-Veroeffentlichung pro Frame.</summary>
        private sealed class DemoPump : IAnimated
        {
            private readonly App _app;
            private double _publishAccum = 1.0;

            public DemoPump(App app) => _app = app;

            public void Animate(double deltaSeconds, double totalTime)
            {
                // Demo-Tempo skaliert die simulierte Zeit, pro Frame aufgeteilt (Tick klaelt >0.4 s ab).
                double total = deltaSeconds * _app.DemoTempo;
                while (total > 0.40)
                {
                    _app.Demo.Tick(0.40);
                    total -= 0.40;
                }
                if (total > 0) _app.Demo.Tick(total);

                _publishAccum += deltaSeconds;
                if (_publishAccum >= 0.45)
                {
                    _publishAccum = 0;
                    _app.PublishDemo();
                }
            }

            /// <summary>
            /// Die Demo-Zeit muss weiterlaufen (auch waehrend der "Fertig"-Haltezeit). Ob dafuer
            /// Bildrate noetig ist, entscheiden die Karten selbst: eine ruhende Trommel meldet
            /// NeedsFrames = false, der Loop faehrt dann mit IdleFrameRate statt ActiveFrameRate.
            /// </summary>
            public bool NeedsFrames => true;

            /// <summary>Der Pump malt selbst keine Pixel -> keine Zeichenarbeit, keine Bildrate.</summary>
            public bool AnimatesVisual => false;
        }
    }
}
