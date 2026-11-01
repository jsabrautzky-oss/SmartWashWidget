using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;

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
            _footerText = text;
            FooterText.Text = text;
        }

        /// <summary>Fusszeilen-Hinweis aus Sprachschluesseln (uetersetzt sich bei Sprachwechsel neu).</summary>
        public void SetFooterNote(string key, params object?[] args)
        {
            _footerNoteKey = key;
            _footerNoteArgs = args ?? Array.Empty<object?>();
            UpdateFooterText();
        }

        public void ClearFooterNote()
        {
            _footerNoteKey = null;
            _footerNoteArgs = Array.Empty<object?>();
            UpdateFooterText();
        }

        private void UpdateFooterText()
        {
            string text = _footerNoteKey is null
                ? LocalizationManager.Get("Str_FooterVersion")
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

            if (_footerNoteKey is not null)
                UpdateFooterText();
            else
                FooterText.Text = LocalizationManager.Relocalize(_footerText, previousLanguage);

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
