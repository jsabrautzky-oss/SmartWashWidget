using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

using SmartWashWidget.Interop;
using SmartWashWidget.Models;

using WF = System.Windows.Forms;

namespace SmartWashWidget.Services
{
    /// <summary>
    /// Tray-Icon ohne externe Ressourcen: das Symbol wird programmatisch mit GDI+ gezeichnet
    /// (Bullauge + Trommel + Wellenbogen) und per NotifyIcon in die Taskleiste gelegt.
    /// Ueber UseWindowsForms - kein einziges NuGet-Paket noetig.
    /// </summary>
    public sealed class TrayIconService : IDisposable
    {
        private readonly App _app;
        private readonly WF.NotifyIcon _notify;
        private readonly WF.ContextMenuStrip _menu;

        // nicht readonly: werden erst im Hilfspfad BuildMenu() erzeugt
        private WF.ToolStripMenuItem _showHideItem = null!;
        private WF.ToolStripMenuItem _demoItem = null!;
        private WF.ToolStripMenuItem _lockItem = null!;
        private WF.ToolStripMenuItem _topmostItem = null!;
        private WF.ToolStripMenuItem _blurItem = null!;
        private WF.ToolStripMenuItem _settingsItem = null!;
        private WF.ToolStripMenuItem _reconnectItem = null!;
        private WF.ToolStripMenuItem _quitItem = null!;

        private readonly Dictionary<ConnectionState, Icon> _icons = new Dictionary<ConnectionState, Icon>();
        private readonly List<IntPtr> _iconHandles = new List<IntPtr>();

        private ConnectionState _state = ConnectionState.Demo;
        private bool _disposed;

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool DestroyIcon(IntPtr hIcon);

        public TrayIconService(App app)
        {
            _app = app;

            _menu = BuildMenu();

            _notify = new WF.NotifyIcon
            {
                Text = LocalizationManager.Get("Str_Tray_Tooltip"),
                Visible = true,
                Icon = IconFor(ConnectionState.Demo),
                ContextMenuStrip = _menu
            };

            _notify.DoubleClick += (s, e) => _app.ShowWidget();

            Refresh();
        }

        // ---------------- Oeffentliche API ----------------

        public void Refresh()
        {
            if (_disposed) return;

            bool visible = _app.Widget is { IsVisible: true };

            _showHideItem.Text = LocalizationManager.Get(visible ? "Str_Menu_Hide" : "Str_Menu_Show");
            _blurItem.Text = LocalizationManager.Get(
                _app.Config.Window.AcrylicBlur ? "Str_Menu_BlurOn" : "Str_Menu_BlurOff");
            _blurItem.Checked = _app.Config.Window.AcrylicBlur;
            _demoItem.Text = LocalizationManager.Get("Str_Menu_Demo");
            _demoItem.Checked = _app.DemoMode;
            _lockItem.Text = LocalizationManager.Get("Str_Menu_Lock");
            _lockItem.Checked = _app.Config.Window.PositionLocked;
            _topmostItem.Text = LocalizationManager.Get("Str_Menu_Topmost");
            _topmostItem.Checked = _app.Config.Window.Topmost;
            _settingsItem.Text = LocalizationManager.Get("Str_Menu_Settings");
            _reconnectItem.Text = LocalizationManager.Get("Str_Menu_Reconnect");
            _quitItem.Text = LocalizationManager.Get("Str_Menu_Quit");

            try { _notify.Text = LocalizationManager.Get("Str_Tray_Tooltip"); }
            catch { /* NotifyIcon.Text ist auf 63 Zeichen begrenzt */ }
        }

        public void SetConnection(ConnectionState state)
        {
            if (_disposed) return;
            if (_state == state) return;

            _state = state;
            try { _notify.Icon = IconFor(state); } catch { /* Iconwechsel ist optional */ }
        }

        /// <summary>Kurzer Hinweistext in der Sprechblase (einmalig, uebernimmt keine Daueranzeige).</summary>
        public void Notify(string title, string text)
        {
            if (_disposed) return;

            try
            {
                _notify.ShowBalloonTip(2600, title, text, WF.ToolTipIcon.Info);
            }
            catch
            {
                // Ballontips sind optional - Fehler hier den Nutzer nie storen.
            }
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            try
            {
                _notify.Visible = false;
                _menu.Close();
            }
            catch { /* egal */ }

            try { _notify.Dispose(); } catch { /* egal */ }
            try { _menu.Dispose(); } catch { /* egal */ }

            foreach (Icon icon in _icons.Values)
            {
                try { icon.Dispose(); } catch { /* egal */ }
            }
            _icons.Clear();

            // Icon.FromHandle-leaks vermeiden: die Handles selbst zerstoeren.
            foreach (IntPtr handle in _iconHandles)
            {
                try { DestroyIcon(handle); } catch { /* egal */ }
            }
            _iconHandles.Clear();
        }

        // ---------------- Menue ----------------

        private WF.ContextMenuStrip BuildMenu()
        {
            WF.ContextMenuStrip menu = new WF.ContextMenuStrip
            {
                Renderer = new WF.ToolStripProfessionalRenderer(new DarkColorTable()),
                BackColor = Color.FromArgb(0x12, 0x17, 0x20),
                ForeColor = Color.FromArgb(0xF1, 0xF5, 0xF9),
                Font = new Font("Segoe UI", 9f, FontStyle.Regular, GraphicsUnit.Point),
                ShowImageMargin = false,
                Padding = new WF.Padding(3)
            };

            _showHideItem = new WF.ToolStripMenuItem(LocalizationManager.Get("Str_Menu_Hide"));
            _showHideItem.Click += (s, e) =>
            {
                if (_app.Widget is { IsVisible: true }) _app.HideWidget();
                else _app.ShowWidget();
            };
            menu.Items.Add(_showHideItem);

            menu.Items.Add(new WF.ToolStripSeparator());

            _blurItem = new WF.ToolStripMenuItem(LocalizationManager.Get("Str_Menu_Blur")) { CheckOnClick = true };
            _blurItem.Click += (s, e) => _app.SetAcrylicBlur(_blurItem.Checked);
            menu.Items.Add(_blurItem);

            _demoItem = new WF.ToolStripMenuItem(LocalizationManager.Get("Str_Menu_Demo"));
            _demoItem.Click += (s, e) => _app.ToggleDemoMode();
            menu.Items.Add(_demoItem);

            _lockItem = new WF.ToolStripMenuItem(LocalizationManager.Get("Str_Menu_Lock"));
            _lockItem.Click += (s, e) =>
            {
                _app.Config.Window.PositionLocked = !_app.Config.Window.PositionLocked;
                _app.ApplyWindowSettings();
                _app.SaveConfig();
            };
            menu.Items.Add(_lockItem);

            _topmostItem = new WF.ToolStripMenuItem(LocalizationManager.Get("Str_Menu_Topmost"));
            _topmostItem.Click += (s, e) =>
            {
                _app.Config.Window.Topmost = !_app.Config.Window.Topmost;
                _app.ApplyWindowSettings();
                _app.SaveConfig();
            };
            menu.Items.Add(_topmostItem);

            menu.Items.Add(new WF.ToolStripSeparator());

            _settingsItem = new WF.ToolStripMenuItem(LocalizationManager.Get("Str_Menu_Settings"));
            _settingsItem.Click += (s, e) => _app.OpenSettings();
            menu.Items.Add(_settingsItem);

            _reconnectItem = new WF.ToolStripMenuItem(LocalizationManager.Get("Str_Menu_Reconnect"));
            _reconnectItem.Click += (s, e) =>
            {
                _app.SetDemoMode(false);
                _app.RestartPolling();
            };
            menu.Items.Add(_reconnectItem);

            menu.Items.Add(new WF.ToolStripSeparator());

            _quitItem = new WF.ToolStripMenuItem(LocalizationManager.Get("Str_Menu_Quit"));
            _quitItem.Click += (s, e) => _app.ShutdownWidget();
            menu.Items.Add(_quitItem);

            return menu;
        }

        // ---------------- Symbole ----------------

        private Icon IconFor(ConnectionState state)
        {
            if (_icons.TryGetValue(state, out Icon? cached) && cached is not null)
                return cached;

            Color accent = state switch
            {
                ConnectionState.Error => Color.FromArgb(0xFF, 0x6B, 0x6B),
                ConnectionState.Degraded => Color.FromArgb(0xFF, 0xC5, 0x3D),
                ConnectionState.Live => Color.FromArgb(0x5C, 0xFF, 0xFF),
                ConnectionState.Demo => Color.FromArgb(0x00, 0xE5, 0xFF),
                _ => Color.FromArgb(0x8F, 0xA0, 0xB2)
            };

            Icon created = DrawIcon(accent, state == ConnectionState.Live || state == ConnectionState.Demo);
            _icons[state] = created;
            return created;
        }

        /// <summary>32x32 Bullauge mit Trommelkreuz und leuchtendem Akzentring.</summary>
        private Icon DrawIcon(Color accent, bool lit)
        {
            Bitmap bmp = new Bitmap(32, 32, PixelFormat.Format32bppArgb);
            try
            {
                using (Graphics g = Graphics.FromImage(bmp))
                {
                    g.SmoothingMode = SmoothingMode.AntiAlias;
                    g.Clear(Color.Transparent);

                    using (LinearGradientBrush body = new LinearGradientBrush(
                               new Rectangle(0, 0, 32, 32),
                               Color.FromArgb(0x46, 0x53, 0x64),
                               Color.FromArgb(0x0B, 0x10, 0x17), 90f))
                        g.FillEllipse(body, 1, 1, 30, 30);

                    using (Pen edge = new Pen(Color.FromArgb(0xA0, 0xB4, 0xC4, 0xD4), 1.5f))
                        g.DrawEllipse(edge, 1.6f, 1.6f, 28.8f, 28.8f);

                    if (lit)
                    {
                        using (Pen halo = new Pen(Color.FromArgb(0x55, accent.R, accent.G, accent.B), 5f))
                            g.DrawEllipse(halo, 5f, 5f, 22f, 22f);
                    }

                    using (Pen ring = new Pen(Color.FromArgb(lit ? 0xF2 : 0xAA, accent.R, accent.G, accent.B), 1.9f))
                        g.DrawEllipse(ring, 6.2f, 6.2f, 19.6f, 19.6f);

                    using (SolidBrush inner = new SolidBrush(Color.FromArgb(0xF2, 0x0B, 0x11, 0x19)))
                        g.FillEllipse(inner, 8.4f, 8.4f, 15.2f, 15.2f);

                    using (Pen paddle = new Pen(Color.FromArgb(0xCC, 0xB8, 0xC8, 0xD8), 1.6f))
                    {
                        paddle.StartCap = LineCap.Round;
                        paddle.EndCap = LineCap.Round;

                        for (int i = 0; i < 3; i++)
                        {
                            double a = Math.PI * 2 * i / 3.0 - Math.PI / 2.0;
                            float dx = (float)Math.Cos(a);
                            float dy = (float)Math.Sin(a);
                            g.DrawLine(paddle, 16f + dx * 3.2f, 16f + dy * 3.2f, 16f + dx * 7.0f, 16f + dy * 7.0f);
                        }
                    }

                    // Wellenbogen unten (Wasser) bzw. Hitzestrich darueber
                    using (Pen wave = new Pen(Color.FromArgb(0xD8, accent.R, accent.G, accent.B), 1.7f))
                    {
                        wave.StartCap = LineCap.Round;
                        wave.EndCap = LineCap.Round;
                        g.DrawArc(wave, 9.5f, 15.5f, 13f, 8.5f, 25f, 130f);
                    }
                }

                IntPtr handle = bmp.GetHicon();
                _iconHandles.Add(handle);
                return Icon.FromHandle(handle);
            }
            finally
            {
                bmp.Dispose();
            }
        }

        // ---------------- Dunkle Menuefarben (Win11-Optik) ----------------

        private sealed class DarkColorTable : WF.ProfessionalColorTable
        {
            public override Color ToolStripDropDownBackground => Color.FromArgb(0x12, 0x17, 0x20);
            public override Color ImageMarginGradientBegin => Color.FromArgb(0x12, 0x17, 0x20);
            public override Color ImageMarginGradientMiddle => Color.FromArgb(0x12, 0x17, 0x20);
            public override Color ImageMarginGradientEnd => Color.FromArgb(0x12, 0x17, 0x20);
            public override Color MenuItemSelected => Color.FromArgb(0x2A, 0x36, 0x46);
            public override Color MenuItemSelectedGradientBegin => Color.FromArgb(0x26, 0x31, 0x40);
            public override Color MenuItemSelectedGradientEnd => Color.FromArgb(0x1E, 0x27, 0x34);
            public override Color MenuItemBorder => Color.FromArgb(0x3C, 0x4A, 0x5C);
            public override Color MenuBorder => Color.FromArgb(0x2A, 0x36, 0x46);
            public override Color MenuStripGradientBegin => Color.FromArgb(0x12, 0x17, 0x20);
            public override Color MenuStripGradientEnd => Color.FromArgb(0x12, 0x17, 0x20);
            public override Color SeparatorDark => Color.FromArgb(0x2A, 0x36, 0x46);
            public override Color SeparatorLight => Color.FromArgb(0x14, 0x1A, 0x23);
            public override Color ToolStripBorder => Color.FromArgb(0x2A, 0x36, 0x46);
        }
    }
}
