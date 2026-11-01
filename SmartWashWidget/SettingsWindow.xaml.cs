using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;

using SmartWashWidget.Interop;
using SmartWashWidget.Models;
using SmartWashWidget.Services;

namespace SmartWashWidget
{
    /// <summary>
    /// Einstellungsfenster im Glas-Stil des Widgets.
    /// Samsung SmartThings wird hier laienfreundlich per 1-Klick-OAuth2 eingerichtet:
    /// Statusanzeige, ein grosser Verbinden-Button und zwei einfache Geraete-Dropdowns.
    /// Saemtliche technischen Felder stecken eingeklappt hinter "Erweiterte Experten-Einstellungen".
    /// </summary>
    public partial class SettingsWindow : Window
    {
        private readonly App _app;
        private readonly DispatcherTimer _saveTimer;

        private bool _suppress;   // verhindert Rueckkopplung, solange ReflectState() die Regler setzt
        private bool _busy;       // ein Setup-/API-Aufruf laeuft gerade
        private bool _updateBusy; // ein manuell angestossener Update-Check laeuft gerade
        private bool _parsed;     // erst nach abgeschlossenem XAML-Pars sind Regler-/Speicherlogik aktiv
        private bool _catalogLoaded;
        private IntPtr _hwnd = IntPtr.Zero;
        private GlassApplyResult? _glass;

        private readonly List<DiscoveredDevice> _washers = new List<DiscoveredDevice>();
        private readonly List<DiscoveredDevice> _dryers = new List<DiscoveredDevice>();

        /// <summary>Device-IDs, die nur aus der Konfiguration ergaenzt wurden (kein Netztreffer).</summary>
        private readonly HashSet<string> _configuredIds = new HashSet<string>(StringComparer.Ordinal);

        public SettingsWindow(App app)
        {
            _app = app;

            InitializeComponent();

            _saveTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(450) };
            _saveTimer.Tick += (s, e) =>
            {
                _saveTimer.Stop();
                CommitSliders();
            };

            _parsed = true;
        }

        // ---------------- Glas ----------------

        protected override void OnSourceInitialized(EventArgs e)
        {
            base.OnSourceInitialized(e);

            HwndSource source = (HwndSource?)PresentationSource.FromVisual(this)
                ?? throw new InvalidOperationException("HwndSource nicht verfuegbar.");
            _hwnd = source.Handle;

            ApplyGlass(_app.Config.Window.AcrylicBlur);
        }

        /// <summary>
        /// Der Blur wird auf diesem Fenster nur zurueckhaltend eingesetzt: das Einstellungsfenster
        /// braucht eine lesbare, dunkle Grundflaeche. Milchglas an/aus gilt hauptsaechlich der MainWindow.
        /// </summary>
        private void ApplyGlass(bool acrylicBlur)
        {
            if (_hwnd == IntPtr.Zero) return;

            _glass = WindowBackdropHelper.Apply(
                _hwnd,
                acrylicBlur,
                dark: true,
                roundCorners: _app.Config.Window.RoundedCorners);

            // Gleiche runde Region wie im Widget (Radius = ShellBorder CornerRadius "8").
            double device = WindowBackdropHelper.GetDeviceScale(_hwnd);
            int radius = _app.Config.Window.RoundedCorners
                ? (int)Math.Round(MainWindow.GlassCornerRadiusDip * device)
                : 0;

            bool regionOk = WindowBackdropHelper.ApplyRoundedRegion(_hwnd, radius);
            if (_glass is not null)
                _glass = _glass with { RegionApplied = regionOk };
        }

        private void Header_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (e.ChangedButton != MouseButton.Left) return;

            try { DragMove(); }
            catch { /* DragMove wirft, wenn die Taste schon losgelassen wurde */ }
        }

        // ---------------- Zustand spiegeln ----------------

        public void ReflectState()
        {
            _suppress = true;

            try
            {
                WidgetConfig cfg = _app.Config;

                BlurToggle.IsChecked = cfg.Window.AcrylicBlur;
                BackdropStatusText.Text = BlurDescription(cfg.Window.AcrylicBlur);

                TintSlider.Value = Math.Clamp(cfg.Window.TintAlpha, TintSlider.Minimum, TintSlider.Maximum);
                TintValue.Text = LocalizationManager.Format(
                    "Str_Fmt_Percent", (int)Math.Round(TintSlider.Value * 100));

                ScaleSlider.Value = Math.Clamp(cfg.Window.Scale, ScaleSlider.Minimum, ScaleSlider.Maximum);
                ScaleValue.Text = LocalizationManager.Format(
                    "Str_Fmt_Percent", (int)Math.Round(ScaleSlider.Value * 100));

                LockToggle.IsChecked = cfg.Window.PositionLocked;
                TopmostToggle.IsChecked = cfg.Window.Topmost;
                CornerToggle.IsChecked = cfg.Window.RoundedCorners;

                DemoToggle.IsChecked = cfg.DemoMode;

                TempoSlider.Value = Math.Clamp(cfg.DemoTempo, TempoSlider.Minimum, TempoSlider.Maximum);
                TempoValue.Text = LocalizationManager.Format("Str_Fmt_Tempo", TempoSlider.Value.ToString("0.#"));

                // Das Sprach-Dropdown spiegelt die gespeicherte Sprache ("auto", "de" oder "en").
                LanguageCombo.SelectedIndex = LanguageIndexOf(cfg.Language);

                // Update-Option spiegeln (die Checkbox selbst, ohne ihren Handler auszuloesen).
                AutoUpdateCheck.IsChecked = cfg.AutoUpdateCheck;

                // Autostart: der Run-Eintrag des Benutzers ist die Wahrheit (und wird dabei
                // ggf. auf einen verschobenen EXE-Pfad berichtigt) - config.json fuehrt nur nach.
                bool autostart = AutostartService.IsEnabled();
                AutostartToggle.IsChecked = autostart;
                cfg.RunAtStartup = autostart;

                // Taskleisten-Schalter spiegelt den echten Zustand auf dem Widget-Fenster.
                TaskbarToggle.IsChecked = _app.Widget?.IsInTaskbar ?? cfg.Window.ShowInTaskbar;

                // ---- Samsung SmartThings: Status + Buttonbeschriftung ----
                AppConfigService svc = _app.ConfigService;
                bool hasOAuth = svc.HasOAuthCredentials;
                bool cliManaged = svc.Config.SmartThings.UseCliManagedToken;
                bool session = svc.HasOAuthSession;

                if (session || (cliManaged && svc.HasToken))
                {
                    SetOAuthStatus("OkBrush", "Str_OAuth_ConnectedTitle", "Str_OAuth_ConnectedDetail");
                    ConnectButton.Content = LocalizationManager.Get("Str_Connect_Redo");
                }
                else if (hasOAuth)
                {
                    SetOAuthStatus("WarnBrush", "Str_OAuth_SessionMissingTitle", "Str_OAuth_SessionMissingDetail");
                    ConnectButton.Content = LocalizationManager.Get("Str_Connect_Button");
                }
                else if (svc.HasToken)
                {
                    SetOAuthStatus("WarnBrush", "Str_OAuth_ManualTitle", "Str_OAuth_ManualDetail");
                    ConnectButton.Content = LocalizationManager.Get("Str_Connect_Button");
                }
                else
                {
                    SetOAuthStatus("TextTertiaryBrush", "Str_OAuth_NoneTitle", "Str_OAuth_NoneDetail");
                    ConnectButton.Content = LocalizationManager.Get("Str_Connect_Button");
                }

                // Technische Felder (Experten) aus der Konfiguration spiegeln.
                OAuthClientIdBox.Text = svc.GetOAuthClientId() ?? string.Empty;
                OAuthClientSecretBox.Password = svc.GetOAuthClientSecret() ?? string.Empty;
                OAuthClientSecretReveal.Text = OAuthClientSecretBox.Password;
                FallbackTokenBox.Password = svc.GetToken() ?? string.Empty;
                FallbackTokenReveal.Text = FallbackTokenBox.Password;
                WasherDeviceIdBox.Text = cfg.SmartThings.WasherDeviceId ?? string.Empty;
                DryerDeviceIdBox.Text = cfg.SmartThings.DryerDeviceId ?? string.Empty;

                ConnectionStatusText.Text = LocalizationManager.Get(
                    _app.DemoMode ? "Str_System_DemoMode" : "Str_System_LiveMode");

                GlassStatusText.Text = DescribeGlass();

                LastSyncText.Text = LocalizationManager.Format(
                    "Str_Fmt_LastSync", DescribeLastSync(cfg.SmartThings.LastSyncUtc));

                ConfigPathText.Text = LocalizationManager.Format(
                    "Str_Fmt_ConfigPath", AppConfigService.ConfigPath);

                HeaderStatus.Text = LocalizationManager.Format(
                    "Str_Fmt_HeaderStatus",
                    LocalizationManager.Get("Str_Settings_Subtitle"),
                    LocalizationManager.Get("Str_Settings_Build"),
                    WindowBackdropHelper.OsBuild,
                    LocalizationManager.Get(_glass?.BlurApplied == true
                        ? "Str_Settings_HasBlur"
                        : "Str_Settings_NoBlur"));

                // Die Dropdowns neu aufbauen, damit auch ihre Platzhalter uebersetzt sind.
                if (WasherCombo.Items.Count > 0 || DryerCombo.Items.Count > 0)
                    RenderDeviceCombos();
            }
            finally
            {
                _suppress = false;
            }

            SyncSecretReveal();
            UpdateConnectButtonState();
            RefreshUpdateStatus();

            // Dropdowns sofort (synchron) aus der gespeicherten Auswahl fuellen, damit sie nie leer wirken.
            if (IsConnected() && WasherCombo.Items.Count == 0)
                SeedCombosFromConfig();

            // Halbfertiges Fenster: Geraeteliste im Hintergrund nachladen, damit die
            // Dropdowns echte Namen zeigen und die gespeicherte Auswahl vorbelegt ist.
            if (!_catalogLoaded && IsConnected())
            {
                _catalogLoaded = true;
                _ = LoadDeviceCatalogAsync();
            }
        }

        /// <summary>Verbindungszeile: Punkt, Titel und Detailtext aus dem aktiven Sprachvorrat.</summary>
        private void SetOAuthStatus(string brushKey, string titleKey, string detailKey)
        {
            OAuthStatusDot.Fill = Brush(brushKey);
            OAuthStatusTitle.Text = LocalizationManager.Get(titleKey);
            OAuthStatusDetail.Text = LocalizationManager.Get(detailKey);
        }

        // ---------------- Sprache ----------------

        /// <summary>Index des Sprach-Dropdowns zum gespeicherten Sprachcode ("auto", "de" oder "en").</summary>
        private static int LanguageIndexOf(string? code)
        {
            switch ((code ?? LocalizationManager.Auto).Trim().ToLowerInvariant())
            {
                case "de": return 1;
                case "en": return 2;
                default: return 0;
            }
        }

        /// <summary>Sprachwahl im Einstellungsfenster: wirkt sofort und wird gespeichert.</summary>
        private void LanguageCombo_Changed(object sender, SelectionChangedEventArgs e)
        {
            if (_suppress) return;
            if (LanguageCombo.SelectedItem is not ComboBoxItem item) return;

            string code = (item.Tag as string) ?? LocalizationManager.Auto;
            if (string.Equals(code, _app.Config.Language, StringComparison.OrdinalIgnoreCase)) return;

            // App speichert den Code, tauscht den Sprachvorrat und alle Fenster ziehen nach.
            _app.SetLanguage(code);
        }

        private void SyncSecretReveal()
        {
            if (RevealSecretsCheck is null) return;
            bool reveal = RevealSecretsCheck.IsChecked == true;
            OAuthClientSecretBox.Visibility = reveal ? Visibility.Collapsed : Visibility.Visible;
            OAuthClientSecretReveal.Visibility = reveal ? Visibility.Visible : Visibility.Collapsed;
            FallbackTokenBox.Visibility = reveal ? Visibility.Collapsed : Visibility.Visible;
            FallbackTokenReveal.Visibility = reveal ? Visibility.Visible : Visibility.Collapsed;
        }

        private void UpdateConnectButtonState()
        {
            if (_busy) return;

            ConnectButton.Content = LocalizationManager.Get(
                IsConnected() ? "Str_Connect_Redo" : "Str_Connect_Button");

            ConnectButton.IsEnabled = true;
        }

        private string DescribeGlass()
        {
            GlassApplyResult? r = _glass;

            if (r is null)
                return LocalizationManager.Get("Str_Glass_NotApplied");

            // Die abgelehnten Anteile tragen ihren Trennpunkt bereits im Sprachvorrat.
            string dark = r.DarkModeApplied
                ? string.Empty
                : " " + LocalizationManager.Get("Str_Glass_DarkRejected");

            string corners = r.CornerApplied
                ? string.Empty
                : " " + LocalizationManager.Get("Str_Glass_CornersRejected");

            string text = LocalizationManager.Format(
                "Str_Fmt_GlassStatus",
                LocalizationManager.Get("Str_Glass_Prefix"),
                MainWindow.DescribeGlass(_app.Config.Window.AcrylicBlur),
                LocalizationManager.Get(r.BlurApplied ? "Str_Glass_BlurActive" : "Str_Glass_NoBlur"),
                dark,
                corners);

            // Technische Details (DWM-Rueckmeldungen) bleiben unuebersetzt.
            string? detail = !string.IsNullOrWhiteSpace(r.Detail) ? r.Detail : _app.GlassDetail;

            if (!string.IsNullOrWhiteSpace(detail))
                text += "\n" + detail;

            return text;
        }

        private static string DescribeLastSync(string? rawUtc)
        {
            if (string.IsNullOrWhiteSpace(rawUtc)) return LocalizationManager.Get("Str_Sync_None");

            if (!DateTimeOffset.TryParse(
                    rawUtc.Trim(),
                    System.Globalization.CultureInfo.InvariantCulture,
                    System.Globalization.DateTimeStyles.AdjustToUniversal,
                    out DateTimeOffset stamp))
                return rawUtc;

            TimeSpan age = DateTimeOffset.UtcNow - stamp.ToUniversalTime();

            // Die Uhrzeit selbst bleibt zifferngleich, nur die Klammer dahinter ist uebersetzt.
            string clock = stamp.ToLocalTime().ToString("HH:mm:ss", System.Globalization.CultureInfo.InvariantCulture);

            if (age.TotalSeconds < 45)
                return LocalizationManager.Format("Str_Sync_JustNow", clock);

            if (age.TotalMinutes < 60)
                return LocalizationManager.Format("Str_Sync_MinutesAgo", clock, (int)age.TotalMinutes);

            if (age.TotalHours < 24)
                return LocalizationManager.Format("Str_Sync_HoursAgo", clock, (int)age.TotalHours);

            string datePattern = LocalizationManager.ActiveLanguage == "de" ? "dd.MM HH:mm" : "MM.dd HH:mm";
            return LocalizationManager.Format(
                "Str_Sync_DaysAgo",
                stamp.ToLocalTime().ToString(datePattern, System.Globalization.CultureInfo.InvariantCulture),
                (int)age.TotalDays);
        }

        /// <summary>Klare Ein-/Aus-Beschreibung fuer den Einstellungs-Text unter dem Schalter.</summary>
        private static string BlurDescription(bool acrylicBlur)
            => LocalizationManager.Get(acrylicBlur ? "Str_Blur_StatusOn" : "Str_Blur_StatusOff");

        // ---------------- Update-Check ----------------

        /// <summary>
        /// Checkbox "Automatisch nach Updates suchen": speichern und sofort an die App weitergeben.
        /// Beim Einschalten prueft die App gleich einmal, statt bis zu 4 h auf den naechsten Tick zu warten.
        /// </summary>
        private void AutoUpdateCheck_Changed(object sender, RoutedEventArgs e)
        {
            if (_suppress || !_parsed) return;

            _app.SetAutoUpdateCheck(AutoUpdateCheck.IsChecked == true);
        }

        /// <summary>Button "Nach Updates suchen": ein einzelner Check von Hand, mit Statuszeile.</summary>
        private async void UpdateCheck_Click(object sender, RoutedEventArgs e)
        {
            if (_updateBusy) return;

            _updateBusy = true;
            UpdateCheckButton.IsEnabled = false;
            UpdateStatusText.Text = LocalizationManager.Get("Str_Update_Busy");
            UpdateStatusText.Foreground = Brush("TextTertiaryBrush");

            try
            {
                await _app.CheckForUpdatesNowAsync();
            }
            catch
            {
                // Der Pruefer wirft nie - sollte trotzdem etwas schiefgehen, bleibt das Fenster nutzbar.
            }

            _updateBusy = false;
            UpdateCheckButton.IsEnabled = true;
            RefreshUpdateStatus();
        }

        /// <summary>
        /// Statuszeile und Checkbox aus dem Stand des Update-Pruefers: gefundenes Release gegen die
        /// eigene Version (&lt;Version&gt; der csproj) vergleichen, Sprachtexte immer aktuell holen.
        /// </summary>
        public void RefreshUpdateStatus()
        {
            if (UpdateStatusText is null) return;

            AutoUpdateCheck.IsChecked = _app.Config.AutoUpdateCheck;

            UpdateService? svc = _app.Updates;
            UpdateInfo? info = svc?.Latest;

            if (svc is not null && info is not null && info.Version > svc.CurrentVersion)
            {
                UpdateStatusText.Text = LocalizationManager.Format("Str_Fmt_UpdateAvailable", info.Display);
                UpdateStatusText.Foreground = Brush("AccentCyanBrush");
            }
            else if (info is not null)
            {
                UpdateStatusText.Text = LocalizationManager.Format(
                    "Str_Fmt_UpdateUpToDate", svc?.CurrentDisplay ?? info.Display);
                UpdateStatusText.Foreground = Brush("OkBrush");
            }
            else if (svc?.LastCheckFailed == true)
            {
                UpdateStatusText.Text = LocalizationManager.Get("Str_Update_Failed");
                UpdateStatusText.Foreground = Brush("WarnBrush");
            }
            else
            {
                UpdateStatusText.Text = LocalizationManager.Get("Str_Update_NotChecked");
                UpdateStatusText.Foreground = Brush("TextTertiaryBrush");
            }
        }

        // ---------------- Autostart & Taskleiste ----------------

        /// <summary>
        /// "Mit Windows starten": schreibt bzw. loescht den Run-Eintrag des angemeldeten
        /// Benutzers (HKCU\...\CurrentVersion\Run, Pfad der aktuell laufenden EXE). Ein
        /// Fehlschlag (Rechte, Gruppenrichtlinie) setzt den Schalter auf den echten Zustand zurueck.
        /// </summary>
        private void AutostartToggle_Changed(object sender, RoutedEventArgs e)
        {
            if (_suppress || !_parsed) return;

            bool wanted = AutostartToggle.IsChecked == true;
            bool actual = AutostartService.SetEnabled(wanted) ? wanted : AutostartService.IsEnabled();

            _app.Config.RunAtStartup = actual;
            _app.SaveConfig();

            if (actual != wanted)
            {
                _suppress = true;
                AutostartToggle.IsChecked = actual;
                _suppress = false;
            }
        }

        /// <summary>
        /// "In Taskleiste anzeigen": wirkt sofort auf dem Widget-Fenster, ohne dass das Handle
        /// neu gebaut wuerde - Milchglas-Blur, abgerundete Region und Position bleiben erhalten.
        /// </summary>
        private void TaskbarToggle_Changed(object sender, RoutedEventArgs e)
        {
            if (_suppress || !_parsed) return;

            _app.Config.Window.ShowInTaskbar = TaskbarToggle.IsChecked == true;
            _app.ApplyWindowSettings();
            _app.SaveConfig();

            TaskbarToggle.IsChecked = _app.Widget?.IsInTaskbar ?? _app.Config.Window.ShowInTaskbar;

            // Das Widget wurde fuer den Taskleisten-Knopf kurz versteckt und wieder gezeigt - dabei
            // zieht es die Aktivierung an. Zurueck zu den Einstellungen, damit der Schalter bedienbar bleibt.
            Activate();
        }

        // ---------------- Glas-Regler ----------------

        private void BlurToggle_Changed(object sender, RoutedEventArgs e)
        {
            if (_suppress || !_parsed) return;

            bool on = BlurToggle.IsChecked == true;

            BackdropStatusText.Text = BlurDescription(on);

            // Glas-Aenderung sofort (nicht debounced), damit der Effekt live sichtbar wird.
            _app.SetAcrylicBlur(on);
            ApplyGlass(on);

            GlassStatusText.Text = DescribeGlass();
        }

        // Die Slider-Handler feuern schon waehrend des XAML-Pars (Minimum-Koerzung aus dem
        // Standardwert 0). Deshalb ist alles null-/zustandssicher abgesichert.
        private void TintSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (TintValue is not null)
                TintValue.Text = LocalizationManager.Format("Str_Fmt_Percent", (int)Math.Round(e.NewValue * 100));
            QueueSave();
        }

        private void ScaleSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (ScaleValue is not null)
                ScaleValue.Text = LocalizationManager.Format("Str_Fmt_Percent", (int)Math.Round(e.NewValue * 100));
            QueueSave();
        }

        private void TempoSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (TempoValue is not null)
                TempoValue.Text = LocalizationManager.Format("Str_Fmt_Tempo", e.NewValue.ToString("0.#"));
            QueueSave();
        }

        private void QueueSave()
        {
            if (_suppress || _saveTimer is null || !_parsed) return;

            _saveTimer.Stop();
            _saveTimer.Start();
        }

        private void CommitSliders()
        {
            if (_suppress || !_parsed) return;
            if (TintSlider is null || ScaleSlider is null || TempoSlider is null) return;

            _app.Config.Window.TintAlpha = Math.Round(TintSlider.Value, 3);
            _app.Config.Window.Scale = Math.Round(ScaleSlider.Value, 3);
            _app.Config.DemoTempo = Math.Round(TempoSlider.Value, 2);

            _app.SaveConfig();
            _app.ApplyWindowSettings();
        }

        private void LockToggle_Changed(object sender, RoutedEventArgs e)
        {
            if (_suppress || !_parsed) return;

            _app.Config.Window.PositionLocked = LockToggle.IsChecked == true;
            _app.ApplyWindowSettings();
            _app.SaveConfig();
        }

        private void TopmostToggle_Changed(object sender, RoutedEventArgs e)
        {
            if (_suppress || !_parsed) return;

            _app.Config.Window.Topmost = TopmostToggle.IsChecked == true;
            Topmost = TopmostToggle.IsChecked == true;
            _app.ApplyWindowSettings();
            _app.SaveConfig();
        }

        private void CornerToggle_Changed(object sender, RoutedEventArgs e)
        {
            if (_suppress || !_parsed) return;

            _app.Config.Window.RoundedCorners = CornerToggle.IsChecked == true;
            _app.ApplyWindowSettings();
            ApplyGlass(_app.Config.Window.AcrylicBlur);
            _app.SaveConfig();
        }

        private void DemoToggle_Changed(object sender, RoutedEventArgs e)
        {
            if (_suppress || !_parsed) return;

            _app.SetDemoMode(DemoToggle.IsChecked == true);

            // Ohne Zugang bleibt Demo zwingend an - spiegeln.
            DemoToggle.IsChecked = _app.Config.DemoMode;
        }

        // ---------------- 1-Klick-OAuth2-Setup ----------------

        private async void Connect_Click(object sender, RoutedEventArgs e)
        {
            if (_busy) return;

            _busy = true;
            SetConnectBusy(true);
            SetConnectStatus(LocalizationManager.Get("Str_OAuth_Starting"), false);
            _wasConnectedAtConnectStart = IsConnected();
            StartConnectWatchdog();

            try
            {
                using var setup = new SmartThingsOAuthSetupService(
                    _app.ConfigService,
                    message => SafeStatus(message));

                // Komplett im Hintergrund laufen lassen, damit keine UI-Kontext-Abhaengigkeit
                // die Verbindung blockieren kann; die Statusmeldungen kommen per BeginInvoke.
                OAuthSetupResult result = await Task.Run(() => setup.ConnectAsync(CancellationToken.None))
                    .ConfigureAwait(true);

                StopConnectWatchdog();

                _catalogLoaded = true;
                PopulateDeviceCombos(result);
                _app.SetDemoMode(false);
                _app.SaveConfig();

                SetConnectStatus(LocalizationManager.Get("Str_OAuth_Success"), false);
                OAuthProgressText.Foreground = Brush("OkBrush");
            }
            catch (OperationCanceledException)
            {
                SetConnectStatus(LocalizationManager.Get("Str_OAuth_Cancelled"), true);
            }
            catch (SmartThingsException ex)
            {
                SetConnectStatus(LocalizationManager.Format("Str_OAuth_NotFinished", ex.UserText), true);
            }
            catch (Exception ex)
            {
                SetConnectStatus(LocalizationManager.Format("Str_OAuth_NotFinished", Short(ex.Message)), true);
            }
            finally
            {
                StopConnectWatchdog();
                _busy = false;
                SetConnectBusy(false);
                ReflectState();
            }
        }

        /// <summary>
        /// Sicherheitsnetz: Sobald die Verbindung faktisch in der config gespeichert ist
        /// (Assistent fertig, Token da), aber der UI-Fortsetzungsschritt aus irgendeinem Grund
        /// haengen bleibt, wird die Oberflaeche hier selbst fertiggestellt. Verhindert, dass
        /// das Fenster dauerhaft auf der Beschriftung Str_Connect_Busy stehen bleibt.
        /// </summary>
        private bool _wasConnectedAtConnectStart;
        private DispatcherTimer? _connectWatchdog;

        private void StartConnectWatchdog()
        {
            StopConnectWatchdog();
            _connectWatchdog = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
            _connectWatchdog.Tick += (s, e) =>
            {
                if (!_busy) { StopConnectWatchdog(); return; }

                // Nur fuer den Fall "vorher nicht verbunden": Sobald die Verbindung gespeichert
                // ist, aber die UI-Status-Aktualisierung haengen bliebe, selbst fertigstellen.
                if (!_wasConnectedAtConnectStart
                    && IsConnected()
                    && _app.ConfigService.Config.SmartThings.UseCliManagedToken)
                {
                    StopConnectWatchdog();
                    _busy = false;
                    SetConnectBusy(false);
                    SetConnectStatus(
                        LocalizationManager.Get("Str_OAuth_RefreshingUi"), false);
                    ReflectState();
                }
            };
            _connectWatchdog.Start();
        }

        private void StopConnectWatchdog()
        {
            if (_connectWatchdog is null) return;
            _connectWatchdog.Stop();
            _connectWatchdog = null;
        }

        /// <summary>Statusmeldung des Setup-Dienstes sicher und nicht-blockierend zum UI-Thread schicken.</summary>
        private void SafeStatus(string message)
        {
            try
            {
                // BeginInvoke: blockiert keinen Hintergrund-Thread und kann so nie einen
                // Zombie-Deadlock mit dem UI-Thread ausloesen.
                Dispatcher.BeginInvoke(new Action(() =>
                {
                    if (_busy) SetConnectStatus(message, false);
                }), DispatcherPriority.Normal);
            }
            catch
            {
                // Fenster ist gerade geschlossen - Status ist dann egal.
            }
        }

        private void SetConnectBusy(bool busy)
        {
            ConnectButton.IsEnabled = !busy;
            ConnectButton.Content = busy
                ? LocalizationManager.Get("Str_Connect_Busy")
                : LocalizationManager.Get(SessionActive() ? "Str_Connect_Redo" : "Str_Connect_Button");
            WasherCombo.IsEnabled = !busy;
            DryerCombo.IsEnabled = !busy;
            ExpertSaveButton.IsEnabled = !busy;
            DisconnectButton.IsEnabled = !busy;
        }

        private bool IsConnected()
            => _app.ConfigService.HasOAuthSession
               || (_app.ConfigService.Config.SmartThings.UseCliManagedToken && _app.ConfigService.HasToken);

        private bool SessionActive() => IsConnected();

        private void SetConnectStatus(string message, bool isError)
        {
            OAuthProgressText.Text = message;
            OAuthProgressText.Foreground = isError ? Brush("WarnBrush") : Brush("TextTertiaryBrush");
        }

        // ---------------- Geraete-Dropdowns ----------------

        private async Task LoadDeviceCatalogAsync()
        {
            try
            {
                List<DiscoveredDevice> found = await _app.DiscoverDevicesAsync(24).ConfigureAwait(true);
                PopulateDeviceCombos(new OAuthSetupResult(found, null, null));
                LogNote("DeviceCatalog ok: " + found.Count + " Geraete -> WasherCombo=" + WasherCombo.Items.Count
                        + " DryerCombo=" + DryerCombo.Items.Count);
            }
            catch (Exception ex)
            {
                // Nicht still schlucken: Fehler loggen und aus der gespeicherten Auswahl auffrischen.
                LogDiagnostic("DeviceCatalog", ex);
                SeedCombosFromConfig();
            }
        }

        private void PopulateDeviceCombos(OAuthSetupResult result)
        {
            _suppress = true;
            try
            {
                _washers.Clear();
                _dryers.Clear();
                _configuredIds.Clear();

                foreach (DiscoveredDevice d in result.Devices)
                {
                    if (d.HasWasherCapability || d.InferredKind == MachineKind.Washer) _washers.Add(d);
                    if (d.HasDryerCapability || d.InferredKind == MachineKind.Dryer) _dryers.Add(d);
                }

                EnsureConfiguredDevice(_washers, MachineKind.Washer,
                    _app.Config.SmartThings.WasherDeviceId, _app.Config.SmartThings.WasherLabel);
                EnsureConfiguredDevice(_dryers, MachineKind.Dryer,
                    _app.Config.SmartThings.DryerDeviceId, _app.Config.SmartThings.DryerLabel);

                RenderDeviceCombos();
            }
            finally
            {
                _suppress = false;
            }
        }

        /// <summary>Fuellt die Dropdowns synchron aus den gespeicherten Geraete-IDs (kein Netz noetig).</summary>
        private void SeedCombosFromConfig()
        {
            _suppress = true;
            try
            {
                _washers.Clear();
                _dryers.Clear();
                _configuredIds.Clear();

                EnsureConfiguredDevice(_washers, MachineKind.Washer,
                    _app.Config.SmartThings.WasherDeviceId, _app.Config.SmartThings.WasherLabel);
                EnsureConfiguredDevice(_dryers, MachineKind.Dryer,
                    _app.Config.SmartThings.DryerDeviceId, _app.Config.SmartThings.DryerLabel);

                RenderDeviceCombos();
            }
            finally
            {
                _suppress = false;
            }
        }

        private void RenderDeviceCombos()
        {
            WasherCombo.Items.Clear();
            DryerCombo.Items.Clear();

            WasherCombo.Items.Add(new DeviceChoice(null, LocalizationManager.Get("Str_Combo_NoWasher")));
            foreach (DiscoveredDevice d in _washers)
                WasherCombo.Items.Add(new DeviceChoice(d.Id, ChoiceDisplay(d, MachineKind.Washer)));

            DryerCombo.Items.Add(new DeviceChoice(null, LocalizationManager.Get("Str_Combo_NoDryer")));
            foreach (DiscoveredDevice d in _dryers)
                DryerCombo.Items.Add(new DeviceChoice(d.Id, ChoiceDisplay(d, MachineKind.Dryer)));

            SelectInCombo(WasherCombo, _app.Config.SmartThings.WasherDeviceId);
            SelectInCombo(DryerCombo, _app.Config.SmartThings.DryerDeviceId);
        }

        /// <summary>
        /// Anzeigename eines Dropdown-Eintrags: echte Geraetenamen bleiben so, wie Samsung sie sendet;
        /// nur die aus der Konfiguration ergaenzten Eintraege bekommen den Namen der aktiven Sprache.
        /// </summary>
        private string ChoiceDisplay(DiscoveredDevice device, MachineKind kind)
        {
            if (device.Id is not null && _configuredIds.Contains(device.Id))
                return LocalizationManager.DeviceName(kind);

            return CleanName(device.Display);
        }

        /// <summary>
        /// Entfernt unsichtbare Formatzeichen (weicher Bindestrich U+00AD, Zero-Width-Zeichen),
        /// die Samsung teils in Geraetenamen sendet und die im Label als Sonderzeichen erscheinen.
        /// </summary>
        private static string CleanName(string? name)
        {
            if (string.IsNullOrEmpty(name)) return string.Empty;

            return name
                .Replace("\u00AD", string.Empty)
                .Replace("\u200B", string.Empty)
                .Replace("\u200C", string.Empty)
                .Replace("\uFEFF", string.Empty)
                .Trim();
        }

        /// <summary>
        /// Haelt ein bereits zugewiesenes Geraet im Dropdown, auch ohne Netztreffer.
        /// Der Anzeigename kommt aus dem Sprachvorrat, wenn die Konfiguration keinen Namen kennt.
        /// </summary>
        private void EnsureConfiguredDevice(List<DiscoveredDevice> list, MachineKind kind, string? deviceId, string? label)
        {
            if (string.IsNullOrWhiteSpace(deviceId)) return;

            foreach (DiscoveredDevice d in list)
                if (string.Equals(d.Id, deviceId, StringComparison.Ordinal))
                    return;

            string display = string.IsNullOrWhiteSpace(label)
                ? LocalizationManager.DeviceName(kind)
                : label!;

            list.Add(new DiscoveredDevice(
                deviceId!,
                display,
                null,
                kind,
                kind == MachineKind.Washer,
                kind == MachineKind.Dryer));

            _configuredIds.Add(deviceId!);
        }

        private static void LogDiagnostic(string stage, Exception ex)
        {
            try
            {
                Directory.CreateDirectory(AppConfigService.DirectoryPath);
                File.AppendAllText(
                    Path.Combine(AppConfigService.DirectoryPath, "error.log"),
                    DateTime.Now.ToString("s") + "  [settings:" + stage + "]  " + ex + Environment.NewLine);
            }
            catch
            {
                // Logging darf nie stoeren.
            }
        }

        private static void LogNote(string message)
        {
            try
            {
                Directory.CreateDirectory(AppConfigService.DirectoryPath);
                File.AppendAllText(
                    Path.Combine(AppConfigService.DirectoryPath, "error.log"),
                    DateTime.Now.ToString("s") + "  [settings]  " + message + Environment.NewLine);
            }
            catch
            {
                // Logging darf nie stoeren.
            }
        }

        private static void SelectInCombo(ComboBox combo, string? deviceId)
        {
            if (string.IsNullOrWhiteSpace(deviceId)) { combo.SelectedIndex = 0; return; }

            foreach (DeviceChoice item in combo.Items)
            {
                if (string.Equals(item.DeviceId, deviceId, StringComparison.Ordinal))
                {
                    combo.SelectedItem = item;
                    return;
                }
            }
            combo.SelectedIndex = 0;
        }

        private void WasherCombo_Changed(object sender, SelectionChangedEventArgs e)
        {
            if (_suppress || !_parsed || _busy) return;
            SaveComboSelection(MachineKind.Washer, WasherCombo);
        }

        private void DryerCombo_Changed(object sender, SelectionChangedEventArgs e)
        {
            if (_suppress || !_parsed || _busy) return;
            SaveComboSelection(MachineKind.Dryer, DryerCombo);
        }

        private void SaveComboSelection(MachineKind kind, ComboBox combo)
        {
            DeviceChoice? choice = combo.SelectedItem as DeviceChoice;
            string? id = choice?.DeviceId;

            if (kind == MachineKind.Washer)
            {
                _app.Config.SmartThings.WasherDeviceId = id;
                _app.Config.SmartThings.WasherLabel = id is null ? null : choice?.Display;
            }
            else
            {
                _app.Config.SmartThings.DryerDeviceId = id;
                _app.Config.SmartThings.DryerLabel = id is null ? null : choice?.Display;
            }

            _app.SaveConfig();
            if (!_app.DemoMode) _app.RestartPolling();

            WasherDeviceIdBox.Text = _app.Config.SmartThings.WasherDeviceId ?? string.Empty;
            DryerDeviceIdBox.Text = _app.Config.SmartThings.DryerDeviceId ?? string.Empty;
        }

        // ---------------- Experten-Einstellungen ----------------

        private void RevealSecrets_Changed(object sender, RoutedEventArgs e)
        {
            bool reveal = RevealSecretsCheck.IsChecked == true;
            if (reveal)
            {
                OAuthClientSecretReveal.Text = OAuthClientSecretBox.Password;
                FallbackTokenReveal.Text = FallbackTokenBox.Password;
            }
            else
            {
                OAuthClientSecretBox.Password = OAuthClientSecretReveal.Text;
                FallbackTokenBox.Password = FallbackTokenReveal.Text;
            }
            SyncSecretReveal();
        }

        private void ExpertSave_Click(object sender, RoutedEventArgs e)
        {
            if (_busy) return;

            try
            {
                AppConfigService svc = _app.ConfigService;
                string clientId = OAuthClientIdBox.Text.Trim();
                string clientSecret = CurrentSecretText(OAuthClientSecretBox, OAuthClientSecretReveal).Trim();
                string fallback = CurrentSecretText(FallbackTokenBox, FallbackTokenReveal).Trim();

                bool haveOAuth = !string.IsNullOrWhiteSpace(clientId) && !string.IsNullOrWhiteSpace(clientSecret);
                bool haveFallback = !string.IsNullOrWhiteSpace(fallback);

                if (haveOAuth)
                {
                    svc.SetOAuthClientCredentials(clientId, clientSecret);
                    ExpertStatusText.Text = LocalizationManager.Get("Str_Expert_OAuthSaved");
                }

                if (haveFallback)
                {
                    // Wechsel auf den manuellen Fallback-Token: OAuth-Sitzung wird dabei verworfen.
                    svc.ClearOAuth();
                    svc.SetToken(fallback);
                    ExpertStatusText.Text = LocalizationManager.Get("Str_Expert_FallbackSaved");
                }

                if (!string.IsNullOrWhiteSpace(WasherDeviceIdBox.Text))
                {
                    _app.Config.SmartThings.WasherDeviceId = WasherDeviceIdBox.Text.Trim();
                    _app.Config.SmartThings.WasherLabel = _app.Config.SmartThings.WasherLabel
                        ?? WasherDeviceIdBox.Text.Trim();
                }
                if (!string.IsNullOrWhiteSpace(DryerDeviceIdBox.Text))
                {
                    _app.Config.SmartThings.DryerDeviceId = DryerDeviceIdBox.Text.Trim();
                    _app.Config.SmartThings.DryerLabel = _app.Config.SmartThings.DryerLabel
                        ?? DryerDeviceIdBox.Text.Trim();
                }

                _app.SaveConfig();

                if (haveFallback || haveOAuth || !string.IsNullOrWhiteSpace(WasherDeviceIdBox.Text) || !string.IsNullOrWhiteSpace(DryerDeviceIdBox.Text))
                {
                    _app.SetDemoMode(false);
                    _app.RestartPolling();
                }

                SetExpertStatus(LocalizationManager.Get("Str_Expert_Saved"), false);
                ReflectState();
            }
            catch (Exception ex)
            {
                SetExpertStatus(LocalizationManager.Format("Str_Expert_NotSaved", Short(ex.Message)), true);
            }
        }

        private void Disconnect_Click(object sender, RoutedEventArgs e)
        {
            if (_busy) return;

            _app.ConfigService.ClearToken();
            _app.SaveConfig();
            _app.SetDemoMode(true);

            _catalogLoaded = true;
            _suppress = true;
            try
            {
                WasherCombo.Items.Clear();
                DryerCombo.Items.Clear();
                _washers.Clear();
                _dryers.Clear();
                _configuredIds.Clear();
            }
            finally
            {
                _suppress = false;
            }

            SetExpertStatus(LocalizationManager.Get("Str_Expert_Disconnected"), false);
            OAuthProgressText.Text = LocalizationManager.Get("Str_OAuth_Disconnected");
            OAuthProgressText.Foreground = Brush("TextTertiaryBrush");
            ReflectState();
        }

        private void SetExpertStatus(string message, bool isError)
        {
            ExpertStatusText.Text = message;
            ExpertStatusText.Foreground = isError ? Brush("WarnBrush") : Brush("OkBrush");
        }

        /// <summary>Liest ein Geheimnis aus dem gerade sichtbaren Feld (Passwortfeld oder freigelegtem Textfeld).</summary>
        private static string CurrentSecretText(PasswordBox passwordBox, TextBox revealBox)
        {
            bool revealed = revealBox.Visibility == Visibility.Visible;
            return revealed ? revealBox.Text : passwordBox.Password;
        }

        // ---------------- Anzeige / Hilfen ----------------

        private Brush Brush(string key) => (TryFindResource(key) as Brush) ?? Brushes.LightGray;

        private static string Short(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return LocalizationManager.Get("Str_Err_Unknown");

            string clean = text.Replace("\r", " ").Replace("\n", " ").Trim();
            return clean.Length > 170 ? string.Concat(clean.AsSpan(0, 170), "\u2026") : clean;
        }

        private void Close_Click(object sender, RoutedEventArgs e) => Close();

        protected override void OnClosed(EventArgs e)
        {
            StopConnectWatchdog();
            _saveTimer.Stop();
            try { CommitSliders(); } catch { /* Speichern darf beim Schliessen nicht stoeren */ }
            _app.SettingsClosed(this);
            base.OnClosed(e);
        }

        /// <summary>Ein Auswahleintrag in den Geraete-Dropdowns (laienfreundlich, nur Name sichtbar).</summary>
        public sealed class DeviceChoice
        {
            public DeviceChoice(string? deviceId, string display)
            {
                DeviceId = deviceId;
                Display = display;
            }

            public string? DeviceId { get; }
            public string Display { get; }

            /// <summary>Wird vom (dunklen) Dropdown-Template fuer die Anzeige genutzt.</summary>
            public override string ToString() => Display;
        }
    }
}