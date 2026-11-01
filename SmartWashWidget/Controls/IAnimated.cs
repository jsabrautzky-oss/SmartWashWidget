using System;

namespace SmartWashWidget.Controls;

/// <summary>Wird pro Frame vom AnimationEngine mit der echten Framezeit aufgerufen.</summary>
public interface IAnimated
{
    void Animate(double deltaSeconds, double totalTime);

    /// <summary>
    /// true, solange dieses Ziel noch Arbeit braucht (Animation, Demo-Zeit, Ausklaeuen).
    /// Braucht KEIN einziges Ziel Frames mehr, koppelt der AnimationEngine seinen Loop ab -
    /// das Widget zeichnet dann gar nicht mehr neu und zwingt die GPU nicht, den
    /// DWM-Acryl-Blur hinter dem transparenten Fenster neu zu berechnen.
    /// </summary>
    bool NeedsFrames { get; }

    /// <summary>
    /// true, wenn dieses Ziel dabei auch wirklich Pixel veraendert (Trommel, Wellen, Balken).
    /// Reine Zeit-/Logik-Pumps halten den Loop zwar wach, verlangen aber keine Bildrate:
    /// sie melden hier false und werden mit der billigeren Idle-Bildrate gefahren.
    /// </summary>
    bool AnimatesVisual { get; }
}
