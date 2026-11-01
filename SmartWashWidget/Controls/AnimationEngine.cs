using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Windows;
using System.Windows.Media;

namespace SmartWashWidget.Controls;

/// <summary>
/// Ein einziger Frame-Loop fuer das gesamte Widget - GEDROSSELT und im Ruhezustand
/// vollstaendig abgekoppelt.
///
/// Warum das die GPU-Last bestimmt: Das Widget-Fenster ist transparent
/// (AllowsTransparency="True") und liegt auf DWM-Acryl-Blur (SetWindowCompositionAttribute).
/// Jedes Neuzeichnen der Fensterflaeche zwingt die GPU, den Blur-Shader ueber der Glasscheibe
/// neu zu berechnen. Ein Loop auf tatsaechlicher Monitor-Bildrate (60/120/144 Hz) war deshalb
/// der Haupttreiber der permanenten 3D-GPU-Last von 5-16 %.
///
/// Der Loop ersetzt die Bildwiederholfrequenz durch eine feste Zeitschritt-Steuerung - das ist
/// das genaue Gegenstueck von Timeline.DesiredFrameRate (das nur auf Storyboards wirkt):
///   - animierter Laufbetrieb : maksimum AnimationEngine.ActiveFrameRate FPS
///   - Logik-Tick / Ausklaeuen : AnimationEngine.IdleFrameRate FPS
///   - nichts bewegt sich      : CompositionTarget.Rendering wird abgekoppelt, es wird gar
///                               nicht mehr neu gezeichnet (GPU-Last ~0 %), bis ein neuer
///                               Snapshot eintrifft oder eine Maschine startet (Wake()).
///
/// dt bleibt die wirklich vergangene Zeit, dadurch ist jede Bewegung bildratenunabhaengig.
/// Der Loop pausiert zusaetzlich, wenn das Widget ausgeblendet ist.
/// </summary>
public sealed class AnimationEngine : IDisposable
{
    /// <summary>Bildraten-Obergrenze, solange ein Ziel wirklich Pixel veraendert (Laufbetrieb).</summary>
    public const int ActiveFrameRate = 20;

    /// <summary>Bildrate fuer reine Zeit-/Logik-Ticks und Uebergangsphasen ohne Zeichenarbeit.</summary>
    public const int IdleFrameRate = 10;

    /// <summary>So lange muss alles ruhig stehen, damit der Loop abgekoppelt wird.</summary>
    public const double DetachAfterSec = 1.0;

    private const double ActiveStep = 1.0 / ActiveFrameRate;
    private const double IdleStep = 1.0 / IdleFrameRate;

    private readonly List<IAnimated> _targets = new List<IAnimated>();
    private readonly Stopwatch _stopwatch = Stopwatch.StartNew();

    private double _lastStepSeconds;
    private double _quietSeconds;
    private bool _visualBusy;
    private bool _hooked;
    private bool _paused;

    /// <summary>Globale Animationszeit in Sekunden (monoton, fuer Phasen von Wellen/Partikeln).</summary>
    public double Time { get; private set; }

    /// <summary>Tatsaechlich erreichte Bildrate des Loops (Diagnose, kein Steuerungswert).</summary>
    public double FrameRate { get; private set; } = ActiveFrameRate;

    /// <summary>true, solange der Loop an CompositionTarget.Rendering haengt.</summary>
    public bool IsAttached => _hooked;

    public bool IsPaused
    {
        get => _paused;
        set
        {
            if (_paused == value) return;
            _paused = value;

            if (value)
            {
                Unhook();
                return;
            }

            _visualBusy = true;
            _quietSeconds = 0;
            _lastStepSeconds = _stopwatch.Elapsed.TotalSeconds - ActiveStep;
            Hook();
        }
    }

    public void Register(IAnimated target)
    {
        if (target is null) return;
        if (!_targets.Contains(target)) _targets.Add(target);
        Hook();
    }

    public void Unregister(IAnimated target)
    {
        _targets.Remove(target);
    }

    /// <summary>
    /// Weckt einen abgekoppelten Loop wieder auf: nach einem neuen Snapshot, einem Phasen-
    /// wechsel oder einem Moduswechsel. Ohne diesen Anstoss wuerde ein ruhendes Widget nie
    /// wieder anfangen zu leben.
    /// </summary>
    public void Wake()
    {
        _quietSeconds = 0;
        _lastStepSeconds = _stopwatch.Elapsed.TotalSeconds - ActiveStep;
        Hook();
    }

    public void Start() => Wake();

    private void Hook()
    {
        if (_hooked || _paused || _targets.Count == 0) return;
        CompositionTarget.Rendering += OnRendering;
        _hooked = true;
    }

    private void Unhook()
    {
        if (!_hooked) return;
        CompositionTarget.Rendering -= OnRendering;
        _hooked = false;
    }

    private void OnRendering(object? sender, EventArgs e)
    {
        double now = _stopwatch.Elapsed.TotalSeconds;

        // Zeitschritt statt Monitor-Bildrate: Frames, die zu frueh kommen, werden uebersprungen.
        // Es wird nichts geaendert -> nichts neu gezeichnet -> der Acryl-Blur bleibt in Ruhe.
        double step = _visualBusy ? ActiveStep : IdleStep;
        if (now - _lastStepSeconds + 0.0005 < step) return;

        double dt = now - _lastStepSeconds;
        _lastStepSeconds = now;

        // Nach Sperren / Debugger-Pausen nie mit riesigem Sprung weiterrechnen.
        if (dt < 0) dt = 0;
        if (dt > 0.25) dt = 0.25;

        Time += dt;

        if (dt > 0.0001)
            FrameRate = FrameRate * 0.9 + (1.0 / dt) * 0.1;

        bool needsFrames = false;
        bool visualBusy = false;

        for (int i = 0; i < _targets.Count; i++)
        {
            try
            {
                _targets[i].Animate(dt, Time);

                if (_targets[i].AnimatesVisual) visualBusy = true;
                if (_targets[i].NeedsFrames) needsFrames = true;
            }
            catch
            {
                // Eine defekte Karte darf niemals den gesamten Frame-Loop killen.
            }
        }

        _visualBusy = visualBusy;

        if (needsFrames)
        {
            _quietSeconds = 0;
            return;
        }

        // Alles steht: noch kurz nachlaufen lassen (letztes Ausklaeuen), dann abkoppeln.
        _quietSeconds += dt;
        if (_quietSeconds >= DetachAfterSec) Unhook();
    }

    public void Dispose()
    {
        Unhook();
        _targets.Clear();
    }
}
