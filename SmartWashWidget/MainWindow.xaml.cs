using System;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;

using SmartWashWidget.Controls;
using SmartWashWidget.Interop;
using SmartWashWidget.Models;
using SmartWashWidget.Services;

namespace SmartWashWidget
{
    public partial class MainWindow : Window
    {
        public const double DesignWidth = 348;
        public const double DesignHeight = 546;

        /// <summary>Eckenradius des Glases in Geraeteunabhaengigen Einheiten; identisch zu
        /// RootBorder CornerRadius und dem Clip-Rechteck in MainWindow.xaml.</summary>
        public const double GlassCornerRadiusDip = 8;

        private readonly App _app;
        private readonly MachineCard _washerCard;
        private readonly MachineCard _dryerCard;

        private IntPtr _hwnd = IntPtr.Zero;
        private GlassApplyResult? _glass;

        private bool? _appliedBlur;
        private bool? _appliedRounded;
        private double _appliedScale = double.NaN;
        private bool? _appliedTaskbar;

        private bool _dragging;
        private ContextMenu? _menu;

        // Die Kontextmenuepunkte halten ihren Header und ihren Haken gemeinsam im Feld:
        // ein Sprachwechsel setzt nur die Texte neu, die Steuerung bleibt gleich.
        private MenuItem _menuSettings = null!;
        private MenuItem _menuDemo = null!;
        private MenuItem _menuLock = null!;
        private MenuItem _menuBlur = null!;
        private MenuItem _menuTopmost = null!;
        private MenuItem _menuHide = null!;
        private MenuItem _menuQuit = null!;

        private ConnectionState _connection = ConnectionState.Off;

        /// <summary>Fusszeilentext und (optional) sein Sprachkey - damit die Zeile einen Sprachwechsel uebersteht.</summary>
        private string? _footerText;
        private string? _footerNoteKey;
        private object?[] _footerNoteArgs = Array.Empty<object?>();

        /// <summary>true, wenn die Fusszeile einen direkt gesetzten Text traegt (Fehlermeldung) statt eines Sprachschluessels.</summary>
        private bool _footerIsRaw;

        /// <summary>Das im Hintergrund gefundene neuere Release (null = kein Hinweis sichtbar).</summary>
        private UpdateInfo? _updateInfo;

        public MainWindow(App app)
        {
            _app = app;
            InitializeComponent();

            _washerCard = new MachineCard(MachineKind.Washer);
            _dryerCard = new MachineCard(MachineKind.Dryer);

            CardHost.Children.Add(_washerCard);
            CardHost.Children.Add(_dryerCard);

            _app.Engine.Register(_washerCard);
            _app.Engine.Register(_dryerCard);

            _menu = BuildContextMenu();

            Left = 80;
            Top = 80;
            ApplyWindowSettings();
            UpdateFooterText();
        }

        public MachineCard WasherCard => _washerCard;
        public MachineCard DryerCard => _dryerCard;

        /// <summary>true, wenn das Fenster gerade als Knopf in der Taskleiste steht (der echte Zustand auf dem Handle).</summary>
        public bool IsInTaskbar
            => _hwnd != IntPtr.Zero ? TaskbarStyle.IsShown(_hwnd) : _app.Config.Window.ShowInTaskbar;

        // ---------------- DWM / Glas ----------------

        protected override void OnSourceInitialized(EventArgs e)
        {
            base.OnSourceInitialized(e);

            HwndSource source = (HwndSource?)PresentationSource.FromVisual(this)
                ?? throw new InvalidOperationException("HwndSource nicht verfuegbar.");
            _hwnd = source.Handle;

            RestorePosition();
            ApplyGlass(_app.Config.Window.AcrylicBlur);
            QueueCornerRegion();
            ApplyTaskbarVisibility(live: false);

            // Groessen-, Skala- und DPI-wechseln verschieben die Grenze der abgerundeten Region.
            SizeChanged += (s, e) => QueueCornerRegion();
            DpiChanged += (s, e) => QueueCornerRegion();
        }

        /// <summary>Setzt Milchglas-Blur (Accent-Policy), dunklen Rahmen und runde Ecken neu.</summary>
        public void ApplyGlass(bool acrylicBlur)
        {
            if (_hwnd == IntPtr.Zero) return;

            bool rounded = _app.Config.Window.RoundedCorners;

            _glass = WindowBackdropHelper.Apply(
                _hwnd,
                acrylicBlur,
                dark: true,
                roundCorners: rounded);

            RefreshCornerRegion();

            _appliedBlur = acrylicBlur;
            _appliedRounded = rounded;

            UpdateBackdropText();
        }

        /// <summary>
        /// Rundet die HWND-Region. Noetig, weil die Accent-Policy ihren Blur aufs volle Fensterrechteck
        /// malt: ohne Region ragt der Milchglas-Blur eckig ueber die abgerundeten XAML-Ecken hinaus.
        /// </summary>
        private void RefreshCornerRegion()
        {
            if (_hwnd == IntPtr.Zero) return;

            double scale = _app.Config.Window.ClampScale();
            double device = WindowBackdropHelper.GetDeviceScale(_hwnd);

            int radius = _app.Config.Window.RoundedCorners
                ? (int)Math.Round(GlassCornerRadiusDip * scale * device)
                : 0;

            bool ok = WindowBackdropHelper.ApplyRoundedRegion(_hwnd, radius);

            if (_glass is not null && _glass.RegionApplied != ok)
                _glass = _glass with { RegionApplied = ok };
        }

        /// <summary>Region nach einer Groessen-/Skalaenderung anwenden, sobald WPF neu arrangiert hat.</summary>
        private void QueueCornerRegion()
        {
            if (_hwnd == IntPtr.Zero) return;
            Dispatcher.BeginInvoke(new Action(RefreshCornerRegion), System.Windows.Threading.DispatcherPriority.Background);
        }

        /// <summary>Kurze Fusszeilen-Beschriftung des Glas-Zustands (Key-basiert, Sprache zur Anzeigezeit).</summary>
        public static string DescribeGlass(bool acrylicBlur)
            => LocalizationManager.Get(acrylicBlur ? "Str_Glass_On" : "Str_Glass_Off");

        private void UpdateBackdropText()
        {
            if (BackdropText is null) return;
            BackdropText.Text = DescribeGlass(_app.Config.Window.AcrylicBlur);
        }

        public string? GlassDetail => _glass?.Detail;

        public GlassApplyResult? GlassInfo => _glass;

        /// <summary>true, wenn hinter dem Fenster gerade weichgezeichnet wird.</summary>
        public bool GlassActive => _glass?.GlassActive ?? false;

        // ---------------- Darstellung ----------------

        public void ApplyWindowSettings()
        {
            WindowSettings w = _app.Config.Window;
            double scale = w.ClampScale();

            RootScale.ScaleX = scale;
            RootScale.ScaleY = scale;

            Width = DesignWidth * scale;
            Height = DesignHeight * scale;

            Topmost = w.Topmost;

            // Den nativen Glas-Hintergrund nur bei echter Aenderung neu setzen (vermeidet Flackern).
            if (_hwnd != IntPtr.Zero
                && (_appliedBlur is null || _appliedBlur != w.AcrylicBlur || _appliedRounded != w.RoundedCorners))
            {
                ApplyGlass(w.AcrylicBlur);
            }

            // Neue Groessen -> abgerundete Region nach dem Neu-Layout anpassen.
            if (_appliedScale != scale)
            {
                _appliedScale = scale;
                QueueCornerRegion();
            }

            ApplyTint(w.ClampTint());
            UpdateBackdropText();
            ApplyTaskbarVisibility(live: true);
        }

        /// <summary>
        /// Die "Glas-Toenung" regelt genau EINE XAML-Schicht: das Alpha des RootBorder-Hintergrunds
        /// (#0D111A) von 5 (0 % Slider, fast komplett durchsichtig) bis 235 (100 % Slider).
        /// Die Geraete-Karten bleiben davon unberuehrt und lassen das Glas stets durchscheinen.
        /// </summary>
        private void ApplyTint(double tint)
        {
            byte alpha = WindowBackdropHelper.TintAlphaForSlider(tint);

            TintBrush.Color = Color.FromArgb(
                alpha,
                WindowBackdropHelper.TintColorR,
                WindowBackdropHelper.TintColorG,
                WindowBackdropHelper.TintColorB);
        }

        // ---------------- Taskleiste ----------------

        /// <summary>
        /// "In Taskleiste anzeigen": ordnet das Fenster der Taskleiste zu oder entzieht es ihr -
        /// live auf dem bestehenden Handle, ohne Neustart und ohne Blur/Region/Position zu verlieren.
        ///
        /// live=true (Schalter umgelegt): die Taskleiste von Windows 11 fuehrt ihre Knopfliste
        /// selbst und sieht ein nachtraeglich gesetztes WS_EX_APPWINDOW nicht. Ihr Knopf entsteht,
        /// wenn das Fenster sichtbar wird - also kurz verstecken, Stil umstellen, wieder zeigen.
        /// live=false (beim Start): das Fenster ist noch nicht gezeigt, der Stil wirkt von selbst.
        /// </summary>
        public void ApplyTaskbarVisibility(bool live)
        {
            if (_hwnd == IntPtr.Zero) return;

            EnsureWindowIcon();

            bool show = _app.Config.Window.ShowInTaskbar;
            if (_appliedTaskbar == show) return;

            bool reRegister = live && show && IsVisible;

            if (reRegister) Hide();

            bool applied = TaskbarStyle.Apply(_hwnd, show);

            if (reRegister) Show();

            if (!applied) return;
            _appliedTaskbar = show;

            // Rahmenwechsel und erneutes Zeigen koennen die Accent-Policy verwischen: neu setzen.
            if (_app.Config.Window.AcrylicBlur) AccentPolicy.EnableAcrylicBlur(_hwnd);
            else AccentPolicy.Disable(_hwnd);

            QueueCornerRegion();
        }

        /// <summary>
        /// Symbol fuer Taskleisten-Knopf und Fensterliste: das Symbol der eigenen EXE
        /// (ApplicationIcon, Assets\SmartWashWidget.ico). Ohne Window.Icon zeigt Windows ein
        /// fremdes Standardsymbol - genau das irritiert in der Taskleiste.
        /// </summary>
        private void EnsureWindowIcon()
        {
            if (Icon is not null) return;

            try
            {
                string? exe = Environment.ProcessPath;
                if (string.IsNullOrWhiteSpace(exe) || !File.Exists(exe)) return;

                using System.Drawing.Icon? associated = System.Drawing.Icon.ExtractAssociatedIcon(exe);
                if (associated is null) return;

                Icon = Imaging.CreateBitmapSourceFromHIcon(
                    associated.Handle,
                    Int32Rect.Empty,
                    BitmapSizeOptions.FromEmptyOptions());
            }
            catch
            {
                // Ohne Symbol bleibt es beim Standardsymbol: reine Optik, kein Fehlerfall.
            }
        }

        // ---------------- Position ----------------

        private void RestorePosition()
        {
            WindowSettings w = _app.Config.Window;

            double scale = w.ClampScale();
            double width = DesignWidth * scale;
            double height = DesignHeight * scale;

            double left = w.Left;
            double top = w.Top;

            if (double.IsNaN(left) || double.IsNaN(top))
            {
                // Standardablage: schoener Abstand rechts oben auf dem Primarbildschirm
                left = SystemParameters.WorkArea.Right - width - 34;
                top = SystemParameters.WorkArea.Top + 34;
            }

            Left = ClampLeft(left, width);
            Top = ClampTop(top, height);
        }

        private static double ClampLeft(double left, double width)
        {
            double min = SystemParameters.VirtualScreenLeft;
            double max = min + SystemParameters.VirtualScreenWidth - width;
            return Math.Clamp(left, min, Math.Max(min, max));
        }

        private static double ClampTop(double top, double height)
        {
            double min = SystemParameters.VirtualScreenTop;
            double max = min + SystemParameters.VirtualScreenHeight - height;
            return Math.Clamp(top, min, Math.Max(min, max));
        }

        public void SavePosition()
        {
            _app.Config.Window.Left = Left;
            _app.Config.Window.Top = Top;
            _app.SaveConfig();
        }

        // ---------------- Drag & Drop ----------------

        protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
        {
            base.OnMouseLeftButtonDown(e);

            if (_app.Config.Window.PositionLocked) return;

            try
            {
                _dragging = true;
                DragMove();
            }
            catch
            {
                // DragMove wirft, wenn die Maustaste schon losgelassen wurde.
            }
            finally
            {
                _dragging = false;
                SavePosition();
            }
        }

        protected override void OnMouseRightButtonUp(MouseButtonEventArgs e)
        {
            base.OnMouseRightButtonUp(e);

            if (_menu is null) return;

            _menu.PlacementTarget = RootBorder;
            _menu.Placement = System.Windows.Controls.Primitives.PlacementMode.Relative;
            System.Windows.Point p = e.GetPosition(RootBorder);
            _menu.PlacementRectangle = new Rect(p.X, p.Y, 1, 1);
            _menu.IsOpen = true;
            e.Handled = true;
        }

        protected override void OnLocationChanged(EventArgs e)
        {
            base.OnLocationChanged(e);
            if (!_dragging) SchedulePositionSave();
        }

        private void SchedulePositionSave()
        {
            // Entprellt Speicherstoenne beim DPI-Wechsel / Windows-Aufrummen
            Dispatcher.BeginInvoke(new Action(() =>
            {
                _app.Config.Window.Left = Left;
                _app.Config.Window.Top = Top;
                _app.SaveConfig();
            }), System.Windows.Threading.DispatcherPriority.Background);
        }

        // ---------------- Status / Snapshots ----------------

        public void ShowSnapshot(MachineSnapshot snap)
        {
            if (snap.Kind == MachineKind.Washer) _washerCard.ApplySnapshot(snap);
            else _dryerCard.ApplySnapshot(snap);
        }

        public void ShowDeviceError(MachineKind kind, string message)
        {
            MachineSnapshot err = new MachineSnapshot
            {
                Kind = kind,
                Phase = PhaseCodes.Offline,
                RunState = MachineRunState.Unreachable,
                IsError = true,
                StatusMessage = message,
                Progress = 0
            };
            ShowSnapshot(err);
        }

        /// <summary>Kopfzeile: Zustand als Punkt + Text aus dem Sprachvorrat.</summary>
        public void SetConnectionState(ConnectionState state)
        {
            _connection = state;
            SyncText.Text = LocalizationManager.Get(StateKey(state));

            SyncDot.Fill = state switch
            {
                ConnectionState.Live => FindBrush("OkBrush"),
                ConnectionState.Demo => FindBrush("AccentCyanBrush"),
                ConnectionState.Degraded => FindBrush("WarnBrush"),
                ConnectionState.Error => FindBrush("ErrBrush"),
                _ => FindBrush("TextTertiaryBrush")
            };
        }

        private static string StateKey(ConnectionState state) => state switch
        {
            ConnectionState.Live => "Str_State_Live",
            ConnectionState.Demo => "Str_State_Demo",
            ConnectionState.Degraded => "Str_State_Degraded",
            ConnectionState.Error => "Str_State_Error",
            _ => "Str_State_Unknown"
        };

        public void SetFooter(string text)
        {
            _footerNoteKey = null;
            _footerNoteArgs = Array.Empty<object?>();
            _footerIsRaw = true;
            _footerText = text;
            FooterText.Text = text;
        }

        /// <summary>Fusszeilen-Hinweis aus Sprachschluesseln (uetersetzt sich bei Sprachwechsel neu).</summary>
        public void SetFooterNote(string key, params object?[] args)
        {
            _footerNoteKey = key;
            _footerNoteArgs = args ?? Array.Empty<object?>();
            _footerIsRaw = false;
            UpdateFooterText();
        }

        public void ClearFooterNote()
        {
            _footerNoteKey = null;
            _footerNoteArgs = Array.Empty<object?>();
            _footerIsRaw = false;
            UpdateFooterText();
        }

        /// <summary>
        /// Fusszeile neu bauen: ein Hinweis aus seinem Sprachschluessel, sonst der Programmname
        /// mit der eigenen Version aus &lt;Version&gt; der csproj - die Zahl steht nirgends fest geschrieben.
        /// </summary>
        private void UpdateFooterText()
        {
            string text = _footerNoteKey is null
                ? LocalizationManager.Format("Str_Fmt_FooterVersion", UpdateService.OwnDisplayVersion)
                : LocalizationManager.Format("Str_Note_ConfigIssue",
                      LocalizationManager.Format(_footerNoteKey, _footerNoteArgs));

            _footerText = text;
            FooterText.Text = text;
        }

        public void SetSetupHint(string hint)
        {
            _washerCard.SetSetupHint(hint);
            _dryerCard.SetSetupHint(hint);
        }

        // ---------------- Update-Hinweis (GitHub-Release) ----------------

        /// <summary>
        /// Akzent-Pille in der Kopfzeile: zeigt die neu gefundene Version. Der Text wird aus dem
        /// Sprachvorrat gebaut (Str_Fmt_UpdateBadge), damit er bei Sprachwechsel neu entsteht.
        /// </summary>
        public void ShowUpdate(UpdateInfo info)
        {
            if (info is null) return;

            _updateInfo = info;
            UpdateBadgeText.Text = LocalizationManager.Format("Str_Fmt_UpdateBadge", info.Display);
            UpdateBadge.Visibility = Visibility.Visible;
        }

        /// <summary>Update-Hinweis ausblenden (z. B. nachdem der Nutzer die Release-Seite geoeffnet hat).</summary>
        public void HideUpdate()
        {
            _updateInfo = null;
            UpdateBadge.Visibility = Visibility.Collapsed;
        }

        private void UpdateBadge_Click(object sender, RoutedEventArgs e) => _app.OpenLatestRelease();


        /// <summary>
        /// Sprachwechsel nachziehen: Karten, Kopfzeile, Fusszeile, Glas-Beschriftung und
        /// Kontextmenue bekommen die Texte der jetzt aktiven Sprache.
        /// </summary>
        public void RefreshLanguage(string previousLanguage)
        {
            _washerCard.RefreshLanguage(previousLanguage);
            _dryerCard.RefreshLanguage(previousLanguage);

            SetConnectionState(_connection);
            UpdateBackdropText();

            if (_updateInfo is not null)
                UpdateBadgeText.Text = LocalizationManager.Format("Str_Fmt_UpdateBadge", _updateInfo.Display);

            // Ein direkt gesetzter Text (Fehlermeldung) wird ueber den Rueckwaertsspeicher der
            // alten Sprache geholt; die Versions-Fusszeile wird neu gerechnet (Version + aktive Sprache).
            if (_footerIsRaw)
                FooterText.Text = LocalizationManager.Relocalize(_footerText, previousLanguage);
            else
                UpdateFooterText();

            ApplyMenuTexts();
        }

        private Brush FindBrush(string key)
        {
            object found = TryFindResource(key);
            return found as Brush ?? Brushes.Gray;
        }

        // ---------------- Buttons / Kontextmenue ----------------

        private ContextMenu BuildContextMenu()
        {
            ContextMenu menu = new ContextMenu();

            _menuSettings = new MenuItem { Header = LocalizationManager.Get("Str_Menu_Settings") };
            _menuSettings.Click += (s, e) => _app.OpenSettings();
            menu.Items.Add(_menuSettings);

            _menuDemo = new MenuItem
            {
                Header = LocalizationManager.Get("Str_Menu_Demo"),
                IsCheckable = true,
                IsChecked = _app.Config.DemoMode
            };
            _menuDemo.Click += (s, e) => _app.SetDemoMode(_menuDemo.IsChecked);
            menu.Items.Add(_menuDemo);

            _menuLock = new MenuItem
            {
                Header = LocalizationManager.Get("Str_Menu_Lock"),
                IsCheckable = true,
                IsChecked = _app.Config.Window.PositionLocked
            };
            _menuLock.Click += (s, e) =>
            {
                _app.Config.Window.PositionLocked = _menuLock.IsChecked;
                _app.ApplyWindowSettings();
                _app.SaveConfig();
            };
            menu.Items.Add(_menuLock);

            menu.Items.Add(new Separator());

            _menuBlur = new MenuItem
            {
                Header = LocalizationManager.Get("Str_Menu_Blur"),
                IsCheckable = true,
                IsChecked = _app.Config.Window.AcrylicBlur
            };
            _menuBlur.Click += (s, e) => _app.SetAcrylicBlur(_menuBlur.IsChecked);
            menu.Items.Add(_menuBlur);

            _menuTopmost = new MenuItem
            {
                Header = LocalizationManager.Get("Str_Menu_Topmost"),
                IsCheckable = true,
                IsChecked = _app.Config.Window.Topmost
            };
            _menuTopmost.Click += (s, e) =>
            {
                _app.Config.Window.Topmost = _menuTopmost.IsChecked;
                _app.ApplyWindowSettings();
                _app.SaveConfig();
            };
            menu.Items.Add(_menuTopmost);

            menu.Items.Add(new Separator());

            _menuHide = new MenuItem { Header = LocalizationManager.Get("Str_Menu_Hide") };
            _menuHide.Click += (s, e) => _app.HideWidget();
            menu.Items.Add(_menuHide);

            _menuQuit = new MenuItem { Header = LocalizationManager.Get("Str_Menu_Quit") };
            _menuQuit.Click += (s, e) => _app.ShutdownWidget();
            menu.Items.Add(_menuQuit);

            return menu;
        }

        /// <summary>Header-Texte des Kontextmenues in der aktuell aktiven Sprache setzen.</summary>
        private void ApplyMenuTexts()
        {
            if (_menu is null) return;

            _menuSettings.Header = LocalizationManager.Get("Str_Menu_Settings");
            _menuDemo.Header = LocalizationManager.Get("Str_Menu_Demo");
            _menuLock.Header = LocalizationManager.Get("Str_Menu_Lock");
            _menuBlur.Header = LocalizationManager.Get("Str_Menu_Blur");
            _menuTopmost.Header = LocalizationManager.Get("Str_Menu_Topmost");
            _menuHide.Header = LocalizationManager.Get("Str_Menu_Hide");
            _menuQuit.Header = LocalizationManager.Get("Str_Menu_Quit");
        }

        public void RefreshMenuChecks()
        {
            if (_menu is null) return;

            ApplyMenuTexts();

            _menuDemo.IsChecked = _app.Config.DemoMode;
            _menuLock.IsChecked = _app.Config.Window.PositionLocked;
            _menuTopmost.IsChecked = _app.Config.Window.Topmost;
            _menuBlur.IsChecked = _app.Config.Window.AcrylicBlur;
        }

        private void DemoButton_Click(object sender, RoutedEventArgs e) => _app.ToggleDemoMode();
        private void SettingsButton_Click(object sender, RoutedEventArgs e) => _app.OpenSettings();
        private void HideButton_Click(object sender, RoutedEventArgs e) => _app.HideWidget();
        private void CloseButton_Click(object sender, RoutedEventArgs e) => _app.ShutdownWidget();

        protected override void OnClosed(EventArgs e)
        {
            _app.Engine.Unregister(_washerCard);
            _app.Engine.Unregister(_dryerCard);
            SavePosition();
            base.OnClosed(e);
        }
    }
}
