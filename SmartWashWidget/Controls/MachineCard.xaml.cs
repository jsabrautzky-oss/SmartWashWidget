using System;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;

using SmartWashWidget.Models;
using SmartWashWidget.Services;

namespace SmartWashWidget.Controls;

public partial class MachineCard : UserControl, IAnimated
{
    /// <summary>
    /// Quantisierung des Fortschrittsbalkens (1/512 ~ 0,2 %): geschrieben wird nur bei echter
    /// Aenderung. Ein Balken, der pro Frame "ein bisschen" breiter wird, wuerde das transparente
    /// Fenster bei jedem Frame neu zeichnen und die GPU zwingen, den Acryl-Blur neu zu rechnen.
    /// </summary>
    private const double ProgressLevels = 512.0;

    /// <summary>Abhalb dieser Restdistanz gilt der Balken als am Ziel (Annaehern endet exakt).</summary>
    private const double ProgressSnap = 0.0004;

    /// <summary>Textabgleich im animierten Betrieb: 2 Hz reichen fuer mitlaufende Prozentwerte.</summary>
    private const double TextEverySec = 0.5;

    private static readonly SolidColorBrush PillPaused = Frozen(Color.FromArgb(0x22, 0xFF, 0xC5, 0x3D));
    private static readonly SolidColorBrush PillFinished = Frozen(Color.FromArgb(0x22, 0x4C, 0xD9, 0x64));
    private static readonly SolidColorBrush PillIdle = Frozen(Color.FromArgb(0x1A, 0xFF, 0xFF, 0xFF));
    private static readonly SolidColorBrush PillOffline = Frozen(Color.FromArgb(0x26, 0xFF, 0x6B, 0x6B));

    private readonly MachineKind _kind;
    private readonly DrumVisual _drum;
    private readonly Color _accentColor;
    private readonly SolidColorBrush _accent;
    private readonly SolidColorBrush _accentSoft;
    private readonly SolidColorBrush _pillRunning;

    private double _displayProgress;
    private double _targetProgress;
    private bool _progressAtRest = true;
    private double _textAccum;

    private MachineSnapshot _snap = MachineSnapshot.Idle(MachineKind.Washer);
    private string _setupHint = string.Empty;

    /// <summary>Status-/Fehlermeldung des Snapshots (in der Sprache ihrer Entstehung).</summary>
    private string? _statusMessage;

    public MachineCard(MachineKind kind)
    {
        _kind = kind;
        _accentColor = kind == MachineKind.Washer
            ? Color.FromRgb(0x00, 0xE5, 0xFF)
            : Color.FromRgb(0xFF, 0x91, 0x00);

        _accent = new SolidColorBrush(_accentColor);
        _accentSoft = new SolidColorBrush(Color.FromArgb(0x88, _accentColor.R, _accentColor.G, _accentColor.B));
        _pillRunning = new SolidColorBrush(Color.FromArgb(0x2E, _accentColor.R, _accentColor.G, _accentColor.B));
        _accent.Freeze();
        _accentSoft.Freeze();
        _pillRunning.Freeze();

        _snap = MachineSnapshot.Idle(kind);

        InitializeComponent();

        DeviceName.Text = LocalizationManager.DeviceName(kind);

        _drum = new DrumVisual(kind);
        DrumHost.Content = _drum;

        Progress.Foreground = _accent;

        LinearGradientBrush stripe = new LinearGradientBrush
        {
            StartPoint = new Point(0, 0),
            EndPoint = new Point(1, 0)
        };
        stripe.GradientStops.Add(new GradientStop(Color.FromArgb(0xCC, _accentColor.R, _accentColor.G, _accentColor.B), 0));
        stripe.GradientStops.Add(new GradientStop(Color.FromArgb(0x33, _accentColor.R, _accentColor.G, _accentColor.B), 0.55));
        stripe.GradientStops.Add(new GradientStop(Color.FromArgb(0x00, _accentColor.R, _accentColor.G, _accentColor.B), 1));
        stripe.Freeze();
        AccentStripe.Background = stripe;

        PhaseIcon.Stroke = _accentSoft;
        PhaseIcon.StrokeThickness = 1.5;
        PhaseIcon.Data = BuildPhaseIconGeometry(kind);

        UpdateStatusVisual();
        UpdateTexts();
    }

    public MachineKind Kind => _kind;
    public DrumVisual Drum => _drum;

    /// <summary>Balken in Bewegung oder Trommel zeichnend -> diese Karte braucht Frames.</summary>
    public bool NeedsFrames => !_progressAtRest || _drum.NeedsFrames;

    /// <summary>Die Karte selbst veraendert nur den Balken; die Trommel entscheidet mit.</summary>
    public bool AnimatesVisual => NeedsFrames;

    // ---------------- Eingaben ----------------

    public void ApplySnapshot(MachineSnapshot snap)
    {
        _snap = snap;
        _statusMessage = snap.StatusMessage;

        double target = Math.Clamp(snap.Progress, 0.0, 1.0);
        if (target != _targetProgress)
        {
            _targetProgress = target;
            _progressAtRest = false;   // der Balken bewegt sich wieder
        }

        _drum.ApplyState(snap);
        UpdateStatusVisual();

        // Snapshots sind der einzige Treiber der Anzeige (es gibt keinen lokalen Sekundenzaehler):
        // Texte werden deshalb sofort und ausschliesslich diff-geprueft gesetzt.
        UpdateTexts();
    }

    public void SetSetupHint(string hint)
    {
        _setupHint = hint ?? string.Empty;
        UpdateTexts();
    }

    /// <summary>
    /// Nach einem Sprachwechsel: bereits sichtbare Hinweise/Meldungen in die neue Sprache
    /// ziehen und alle Texte neu setzen (machbar, weil alle Keys in beiden Sprachen existieren).
    /// </summary>
    public void RefreshLanguage(string previousLanguage)
    {
        _setupHint = LocalizationManager.Relocalize(_setupHint, previousLanguage);
        _statusMessage = LocalizationManager.Relocalize(_statusMessage, previousLanguage);

        SetText(DeviceName, LocalizationManager.DeviceName(_kind));
        UpdateStatusVisual();
        UpdateTexts();
    }

    // ---------------- Frame-Loop ----------------

    public void Animate(double dt, double time)
    {
        if (!_progressAtRest)
        {
            // Fortschritt wird geglaettet, damit die Balken nie ruckartig springen.
            double k = 1.0 - Math.Exp(-dt * 2.6);
            _displayProgress += (_targetProgress - _displayProgress) * k;

            if (Math.Abs(_targetProgress - _displayProgress) < ProgressSnap)
                _displayProgress = _targetProgress;

            double quantized = Math.Round(Math.Clamp(_displayProgress, 0.0, 1.0) * ProgressLevels) / ProgressLevels;

            if (quantized != Progress.Value)
                Progress.Value = quantized;

            // Mitlaufende Prozentanzeige, aber nur 2 Hz und nur, solange der Balken wandert.
            _textAccum += dt;
            if (_textAccum >= TextEverySec || _displayProgress == _targetProgress)
            {
                _textAccum = 0;
                UpdateTexts();
            }

            _progressAtRest = _displayProgress == _targetProgress && quantized == Progress.Value;
        }

        _drum.Animate(dt, time);
    }

    // ---------------- Anzeige ----------------

    private void UpdateStatusVisual()
    {
        switch (_snap.RunState)
        {
            case MachineRunState.Running:
                SetText(StatusText, LocalizationManager.Get("Str_Status_Running"));
                SetFill(StatusDot, _accent);
                SetBackground(StatusPill, _pillRunning);
                break;

            case MachineRunState.Paused:
                SetText(StatusText, LocalizationManager.Get("Str_Status_Paused"));
                SetFill(StatusDot, FindBrush("WarnBrush"));
                SetBackground(StatusPill, PillPaused);
                break;

            case MachineRunState.Finished:
                SetText(StatusText, LocalizationManager.Get("Str_Status_Finished"));
                SetFill(StatusDot, FindBrush("OkBrush"));
                SetBackground(StatusPill, PillFinished);
                break;

            case MachineRunState.Idle:
                SetText(StatusText, LocalizationManager.Get("Str_Status_Idle"));
                SetFill(StatusDot, FindBrush("TextTertiaryBrush"));
                SetBackground(StatusPill, PillIdle);
                break;

            case MachineRunState.Unreachable:
                SetText(StatusText, LocalizationManager.Get("Str_Status_Offline"));
                SetFill(StatusDot, FindBrush("ErrBrush"));
                SetBackground(StatusPill, PillOffline);
                break;

            default:
                SetText(StatusText, LocalizationManager.Get("Str_Status_Unknown"));
                SetFill(StatusDot, FindBrush("TextTertiaryBrush"));
                SetBackground(StatusPill, PillIdle);
                break;
        }
    }

    private void UpdateTexts()
    {
        SetText(TimeText, FormatRemaining(_snap.Remaining));

        // Die Phase kommt als ASCII-Code aus dem Snapshot und wird hier uebersetzt.
        SetText(PhaseText, LocalizationManager.PhaseName(_snap.Phase));

        // Der Prozentwert folgt dem geglaetteten Balken, nie dem rohen API-Wert.
        int percent = (int)Math.Round(Math.Clamp(_displayProgress, 0.0, 1.0) * 100.0);
        SetText(PercentText, LocalizationManager.Format("Str_Fmt_Percent", percent));

        string deviceName = LocalizationManager.DeviceName(_kind);
        SetText(DeviceName, deviceName);

        if (_snap.IsLive)
        {
            string label = string.IsNullOrWhiteSpace(_snap.DeviceLabel) ? deviceName : _snap.DeviceLabel;
            SetText(DeviceSub, LocalizationManager.Format("Str_Fmt_SubSmartThings", label));
        }
        else if (_snap.IsError)
        {
            SetText(DeviceSub, deviceName);
        }
        else
        {
            SetText(DeviceSub, LocalizationManager.Format("Str_Fmt_SubDemo", deviceName));
        }

        // Hinweiszeile: ein echtes Device-Problem schlagen, sonst der Einrichtungs-/Demohinweis.
        string hint;
        bool problem = false;

        if (_snap.IsError && !string.IsNullOrWhiteSpace(_statusMessage))
        {
            hint = _statusMessage!;
            problem = true;
        }
        else if (!string.IsNullOrWhiteSpace(_setupHint))
        {
            hint = _setupHint;
        }
        else if (_snap.NoDeviceConfigured)
        {
            hint = LocalizationManager.Get("Str_Hint_NoDevice");
        }
        else if (!string.IsNullOrWhiteSpace(_statusMessage))
        {
            hint = _statusMessage!;
        }
        else
        {
            hint = string.Empty;
        }

        SetForeground(HintText, problem ? FindBrush("ErrBrush") : FindBrush("TextTertiaryBrush"));
        SetText(HintText, string.IsNullOrWhiteSpace(hint) ? LocalizationManager.Get("Str_Hint_Connected") : hint);
    }

    // ---------------- Diff-Check-Helfer ----------------
    //
    // Jeder Schreibzugriff auf Text, Fuellung oder Wert erzeugt ein Neuzeichnen der
    // Fensterflaeche. Bei AllowsTransparency + DWM-Acryl-Blur bedeutet das: die GPU berechnet
    // den Blur-Shader ueber der ganzen Glasscheibe neu. Deshalb wird NUR geschrieben, wenn sich
    // der Wert gegenueber dem vorherigen Anzeigestand tatsaechlich geaendert hat.

    private static void SetText(TextBlock view, string text)
    {
        if (view.Text == text) return;
        view.Text = text;
    }

    private static void SetFill(Shape view, Brush brush)
    {
        if (brush is null || ReferenceEquals(view.Fill, brush)) return;
        view.Fill = brush;
    }

    private static void SetBackground(Border view, Brush brush)
    {
        if (brush is null || ReferenceEquals(view.Background, brush)) return;
        view.Background = brush;
    }

    private static void SetForeground(TextBlock view, Brush brush)
    {
        if (brush is null || ReferenceEquals(view.Foreground, brush)) return;
        view.Foreground = brush;
    }

    private static SolidColorBrush Frozen(Color color)
    {
        SolidColorBrush brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }

    /// <summary>Restzeit als "hh:mm h" - die Einheit kommt aus dem aktiven Sprachvorrat.</summary>
    private static string FormatRemaining(TimeSpan? remaining)
    {
        if (remaining is null) return LocalizationManager.Get("Str_TimeUnknown");

        TimeSpan t = remaining.Value;
        if (t < TimeSpan.Zero) t = TimeSpan.Zero;

        int hours = (int)Math.Floor(t.TotalHours);
        int minutes = t.Minutes;

        if (hours > 99) return WithUnit("99:59");
        return WithUnit(string.Format(CultureInfo.InvariantCulture, "{0:00}:{1:00}", hours, minutes));
    }

    private static string WithUnit(string clock)
        => LocalizationManager.Format("Str_Fmt_TimeRemaining", clock, LocalizationManager.Get("Str_TimeUnit"));

    private Brush FindBrush(string key)
    {
        object found = TryFindResource(key);
        return found as Brush ?? Brushes.Gray;
    }

    private static PathGeometry BuildPhaseIconGeometry(MachineKind kind)
    {
        PathGeometry geo = new PathGeometry();

        if (kind == MachineKind.Washer)
        {
            // kleine Welle
            PathFigure fig = new PathFigure { StartPoint = new Point(0, 7), IsFilled = false };
            fig.Segments.Add(new BezierSegment(new Point(2.2, 3.2), new Point(4.2, 10.4), new Point(6.4, 6.6), true));
            fig.Segments.Add(new BezierSegment(new Point(8.4, 3.2), new Point(10.2, 10.2), new Point(12, 6.2), true));
            geo.Figures.Add(fig);
        }
        else
        {
            // Flamme / Hitzesignal
            PathFigure fig = new PathFigure { StartPoint = new Point(6.2, 1), IsFilled = false };
            fig.Segments.Add(new BezierSegment(new Point(10.4, 5.2), new Point(9.2, 8.4), new Point(6.2, 11.4), true));
            fig.Segments.Add(new BezierSegment(new Point(3.2, 8.4), new Point(2.6, 5.6), new Point(4.6, 3.2), true));
            fig.Segments.Add(new BezierSegment(new Point(5.4, 5.4), new Point(5.2, 6.6), new Point(6.2, 7.4), true));
            geo.Figures.Add(fig);
        }

        geo.Freeze();
        return geo;
    }
}
