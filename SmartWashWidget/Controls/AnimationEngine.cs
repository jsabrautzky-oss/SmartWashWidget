using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Windows;
using System.Windows.Media;

namespace SmartWashWidget.Controls;

/// <summary>
/// Ein einziger, display-synchroner Frame-Loop fuer das gesamte Widget.
/// CompositionTarget.Rendering laeuft mit der tatsaechlichen Bildwiederholfrequenz (60/120/144 Hz),
/// DeltaTime macht die Bewegung unabhaengig von der Bildrate. Der Loop pausiert, wenn das Widget
/// ausgeblendet ist - dann ist die CPU-Last praktisch null.
/// </summary>
public sealed class AnimationEngine : IDisposable
{
    private readonly List<IAnimated> _targets = new List<IAnimated>();
    private readonly Stopwatch _stopwatch = Stopwatch.StartNew();

    private double _lastSeconds;
    private bool _hooked;
    private bool _paused;

    /// <summary>Globale Animationszeit in Sekunden (monoton, fuer Phasen von Wellen/Partikeln).</summary>
    public double Time { get; private set; }

    public double FrameRate { get; private set; } = 60.0;

    public bool IsPaused
    {
        get => _paused;
        set
        {
            if (_paused == value) return;
            _paused = value;
            if (value) Unhook(); else Hook();
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

    public void Start() => Hook();

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
        double dt = now - _lastSeconds;
        _lastSeconds = now;

        // Nach Sperren / Debugger-Pausen nie mit riesigem Sprung weiterrechnen.
        if (dt < 0) dt = 0;
        if (dt > 0.10) dt = 0.10;

        Time += dt;

        if (dt > 0.0001)
            FrameRate = FrameRate * 0.9 + (1.0 / dt) * 0.1;

        for (int i = 0; i < _targets.Count; i++)
        {
            try
            {
                _targets[i].Animate(dt, Time);
            }
            catch
            {
                // Eine defekte Karte darf niemals den gesamten Frame-Loop killen.
            }
        }
    }

    public void Dispose()
    {
        Unhook();
        _targets.Clear();
    }
}
