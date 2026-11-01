using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;

using SmartWashWidget.Models;

namespace SmartWashWidget.Controls;

/// <summary>
/// Komplett im Code gezeichnete Bullauge-Trommel (keine Bilddateien, reine Vektoren).
/// Waschmaschine: drehende Trommel + mathematisch berechnete Sinus-Wasserwelle + aufsteigende Luftblaeschen.
/// Trockner: drehende Trommel + pulsierende Hitze-Wellen + warm leuchtende Partikel.
/// Alle Brushes/Geometrien der statischen Ebenen sind eingefroren; pro Frame werden nur
/// Transform, Opacity und die Wellen-Geometrie gesetzt. Glow ueber Radialverlaeufe statt
/// DropShadowEffect - das haelt die 60 FPS auch auf schwacher GPU sicher.
/// </summary>
public sealed class DrumVisual : Grid, IAnimated
{
    public const double DesignSize = 132;

    private const double C = DesignSize / 2;   // 66
    private const double DrumR = 42;           // Radius der sichtbaren Trommel
    private const double InnerR = 40;          // nutzbbarer Innenraum (Wasser / Partikel)
    private const int WaveSteps = 44;

    private static readonly Point CenterPoint = new Point(C, C);
    private static readonly double ReadyY = DesignSize - 20;

    private readonly MachineKind _kind;
    private readonly bool _isWasher;
    private readonly Color _accent;

    private readonly Canvas _root = new Canvas { Width = DesignSize, Height = DesignSize };
    private readonly Canvas _clip = new Canvas { Width = DesignSize, Height = DesignSize };
    private readonly Canvas _rotorCanvas = new Canvas { Width = DesignSize, Height = DesignSize };
    private readonly Canvas _particleCanvas = new Canvas { Width = DesignSize, Height = DesignSize };
    private readonly RotateTransform _rotor = new RotateTransform(0, C, C);

    private readonly Path _glow;
    private readonly Ellipse _bodyRing;
    private readonly Ellipse _rimHighlight;
    private readonly Ellipse _doorGlass;
    private readonly Path _drumBack;
    private readonly Path _paddles;
    private readonly Path _perforation;
    private readonly Path _surface;
    private readonly Path[] _heatBands;
    private readonly Path _glassSheen;
    private readonly Path _readyGlow;
    private readonly Path _readyRing;
    private readonly Path _readyTick;

    private struct Particle
    {
        public Ellipse View;
        public double X, Y, VX, VY, R, Phase, Life, MaxLife;
    }

    private readonly List<Particle> _particles = new List<Particle>(32);
    private readonly Brush _surfaceFill;
    private readonly Brush _particleBrush;

    // Zielwerte werden exponentiell angefahren -> Start/Stop wirkt weich, nicht sprunghaft.
    private double _targetEnergy = 1.0;
    private double _energy = 1.0;
    private double _targetRpm = 16.0;
    private double _rpm = 16.0;
    private double _targetLevel = -8.0;
    private double _level = -8.0;
    private double _angle;
    private bool _readyVisible;
    private bool _completionVisible;
    private double _readyOpacity;
    private bool _surfaceBuilt;

    public DrumVisual(MachineKind kind)
    {
        _kind = kind;
        _isWasher = kind == MachineKind.Washer;

        _accent = _isWasher
            ? Color.FromRgb(0x00, 0xE5, 0xFF)   // Eisblau / Cyan
            : Color.FromRgb(0xFF, 0x91, 0x00);  // Warm-Orange / Amber

        Width = DesignSize;
        Height = DesignSize;
        IsHitTestVisible = false;

        _clip.Clip = new EllipseGeometry(CenterPoint, InnerR - 0.5, InnerR - 0.5);

        _glow = new Path
        {
            Data = new EllipseGeometry(CenterPoint, DesignSize / 2, DesignSize / 2),
            Fill = BuildGlowBrush(_accent, 0.42),
            Opacity = 0.35
        };

        _bodyRing = new Ellipse
        {
            Width = 118,
            Height = 118,
            Fill = Freeze(new LinearGradientBrush(Color.FromRgb(0x3A, 0x45, 0x54), Color.FromRgb(0x11, 0x15, 0x1C), 135)),
            Stroke = Freeze(new LinearGradientBrush(Color.FromRgb(0x8C, 0x99, 0xA8), Color.FromArgb(0x22, 0xFF, 0xFF, 0xFF), 90)),
            StrokeThickness = 2.5
        };

        _rimHighlight = new Ellipse
        {
            Width = 112,
            Height = 112,
            Stroke = Freeze(new LinearGradientBrush(Color.FromArgb(0x66, 0xFF, 0xFF, 0xFF), Color.FromArgb(0x05, 0xFF, 0xFF, 0xFF), 135)),
            StrokeThickness = 1
        };

        _doorGlass = new Ellipse
        {
            Width = 100,
            Height = 100,
            Fill = Freeze(new RadialGradientBrush(Color.FromRgb(0x18, 0x21, 0x2D), Color.FromRgb(0x04, 0x06, 0x0A))
            { GradientOrigin = new Point(0.34, 0.28), RadiusX = 0.72, RadiusY = 0.72 }),
            Stroke = Freeze(new SolidColorBrush(Color.FromArgb(0x38, 0xFF, 0xFF, 0xFF))),
            StrokeThickness = 1.4
        };

        _drumBack = new Path
        {
            Data = new EllipseGeometry(CenterPoint, DrumR, DrumR),
            Fill = Freeze(new RadialGradientBrush(Color.FromRgb(0x22, 0x2A, 0x36), Color.FromRgb(0x0B, 0x0F, 0x15))
            { GradientOrigin = new Point(0.4, 0.35) }),
            Stroke = Freeze(new SolidColorBrush(Color.FromArgb(0x40, 0xC8, 0xD6, 0xE4))),
            StrokeThickness = 1.6
        };

        _paddles = new Path
        {
            Data = BuildPaddleGeometry(),
            Fill = Freeze(new LinearGradientBrush(Color.FromRgb(0xA2, 0xB4, 0xC6), Color.FromRgb(0x2E, 0x38, 0x45), 120)),
            Stroke = Freeze(new SolidColorBrush(Color.FromArgb(0x55, 0x00, 0x00, 0x00))),
            StrokeThickness = 0.8
        };

        _perforation = new Path
        {
            Data = BuildPerforationGeometry(),
            Fill = Freeze(new SolidColorBrush(Color.FromArgb(0x3A, 0xD8, 0xEC, 0xFF)))
        };

        _rotorCanvas.RenderTransform = _rotor;
        _rotorCanvas.Children.Add(_drumBack);
        _rotorCanvas.Children.Add(_paddles);
        _rotorCanvas.Children.Add(_perforation);

        _surfaceFill = BuildSurfaceBrush(_accent);

        _surface = new Path
        {
            Fill = _surfaceFill,
            Stroke = Freeze(new SolidColorBrush(Color.FromArgb(0x88, _accent.R, _accent.G, _accent.B))),
            StrokeThickness = 1.1
        };

        _heatBands = new Path[3];
        for (int i = 0; i < _heatBands.Length; i++)
        {
            _heatBands[i] = new Path
            {
                Stroke = Freeze(new LinearGradientBrush(
                    Color.FromArgb(0x00, 0xFF, 0xB0, 0x4A),
                    Color.FromArgb(0xFF, 0xD6, 0x86, 0x2E), 90)),
                StrokeThickness = i == 1 ? 2.6 : 1.8,
                Opacity = 0.0,
                StrokeStartLineCap = PenLineCap.Round,
                StrokeEndLineCap = PenLineCap.Round
            };
        }

        _glassSheen = new Path
        {
            Data = BuildSheenGeometry(),
            Fill = Freeze(new LinearGradientBrush(Color.FromArgb(0x3A, 0xFF, 0xFF, 0xFF), Color.FromArgb(0x00, 0xFF, 0xFF, 0xFF), 125)),
            IsHitTestVisible = false
        };

        _readyGlow = new Path
        {
            Data = new EllipseGeometry(new Point(C, ReadyY), 11, 11),
            Stroke = Freeze(new SolidColorBrush(Color.FromArgb(0x44, _accent.R, _accent.G, _accent.B))),
            StrokeThickness = 8,
            Opacity = 0.0
        };

        _readyRing = new Path
        {
            Data = new EllipseGeometry(new Point(C, ReadyY), 11, 11),
            Stroke = Freeze(new SolidColorBrush(Color.FromArgb(0xCC, _accent.R, _accent.G, _accent.B))),
            StrokeThickness = 1.6,
            Opacity = 0.0
        };

        _readyTick = new Path
        {
            Data = BuildTickGeometry(C, ReadyY),
            Stroke = Freeze(new SolidColorBrush(Color.FromArgb(0xEE, 0xFF, 0xFF, 0xFF))),
            StrokeThickness = 1.8,
            StrokeStartLineCap = PenLineCap.Round,
            StrokeEndLineCap = PenLineCap.Round,
            StrokeLineJoin = PenLineJoin.Round,
            Opacity = 0.0
        };

        _clip.Children.Add(_rotorCanvas);
        if (_isWasher)
            _clip.Children.Add(_surface);
        else
            foreach (Path band in _heatBands) _clip.Children.Add(band);
        _clip.Children.Add(_particleCanvas);

        CenterOnCanvas(_bodyRing, 118);
        CenterOnCanvas(_rimHighlight, 112);
        CenterOnCanvas(_doorGlass, 100);

        _root.Children.Add(_glow);
        _root.Children.Add(_bodyRing);
        _root.Children.Add(_doorGlass);
        _root.Children.Add(_clip);
        _root.Children.Add(_rimHighlight);
        _root.Children.Add(_glassSheen);
        _root.Children.Add(_readyGlow);
        _root.Children.Add(_readyRing);
        _root.Children.Add(_readyTick);

        Children.Add(_root);

        _particleBrush = Freeze(_isWasher
            ? new RadialGradientBrush(Color.FromArgb(0xE0, 0xE8, 0xFE, 0xFF), Color.FromArgb(0x14, 0x00, 0x9A, 0xC4))
            : new RadialGradientBrush(Color.FromArgb(0xF0, 0xFF, 0xE6, 0xB4), Color.FromArgb(0x10, 0xC0, 0x4A, 0x00)));

        SeedParticles();
    }

    public MachineKind Kind => _kind;

    // ---------------- Zustand ----------------

    public void ApplyState(MachineSnapshot snap)
    {
        string job = (snap.RawJobState ?? string.Empty).Trim().ToLowerInvariant()
            .Replace("_", string.Empty).Replace("-", string.Empty);

        switch (snap.RunState)
        {
            case MachineRunState.Running:
                _targetEnergy = 1.0;
                _readyVisible = false;
                _completionVisible = false;
                _targetRpm = RpmFromJob(job);
                _targetLevel = LevelFromJob(job);
                break;

            case MachineRunState.Paused:
                _targetEnergy = 0.35;
                _targetRpm = 1.5;
                _readyVisible = false;
                _completionVisible = false;
                _targetLevel = 2.0;
                break;

            case MachineRunState.Finished:
                _targetEnergy = 0.10;
                _targetRpm = 0.0;
                _targetLevel = 8.0;
                _readyVisible = true;
                _completionVisible = true;
                break;

            case MachineRunState.Idle:
                _targetEnergy = 0.06;
                _targetRpm = 0.0;
                _targetLevel = 6.0;
                _readyVisible = true;
                _completionVisible = false;
                break;

            case MachineRunState.Unreachable:
                _targetEnergy = 0.03;
                _targetRpm = 0.0;
                _targetLevel = 4.0;
                _readyVisible = false;
                _completionVisible = false;
                break;

            default:
                _targetEnergy = 0.22;
                _targetRpm = 0.0;
                _targetLevel = 4.0;
                _readyVisible = true;
                _completionVisible = false;
                break;
        }
    }

    private double RpmFromJob(string job)
    {
        if (_isWasher)
        {
            return job switch
            {
                "prewash" or "soak" => 7.0,
                "wash" or "standard" or "normal" or "steam" => 17.0,
                "rinse" or "extrarinse" => 13.0,
                "spin" or "finalspin" or "rinsespin" => 46.0,
                "dry" or "tumbledry" => 12.0,
                _ => 15.0
            };
        }

        return job switch
        {
            "cooldown" or "cooling" or "cool" => 8.0,
            "wrinkleprevent" or "wrinklecare" or "anticrease" => 11.0,
            "dehumidify" or "airdry" => 10.0,
            _ => 14.0
        };
    }

    /// <summary>positiver Wert = Oberflaeche unterhalb der Trommelachse (weniger Wasser).</summary>
    private double LevelFromJob(string job)
    {
        if (_isWasher)
        {
            return job switch
            {
                "prewash" or "soak" => -2.0,
                "wash" or "standard" or "normal" or "steam" => -12.0,
                "rinse" or "extrarinse" or "rinsespin" => -6.0,
                "spin" or "finalspin" => 16.0,
                "dry" or "tumbledry" => 20.0,
                "finish" or "completed" or "done" => 14.0,
                "standby" => 10.0,
                _ => -8.0
            };
        }

        return job switch
        {
            "cooldown" or "cooling" or "cool" => 14.0,
            "wrinkleprevent" or "wrinklecare" or "anticrease" => 8.0,
            _ => 2.0
        };
    }

    // ---------------- Frame-Loop ----------------

    public void Animate(double dt, double time)
    {
        double k = 1.0 - Math.Exp(-dt * 2.2);
        double ke = 1.0 - Math.Exp(-dt * 1.35);

        _energy += (_targetEnergy - _energy) * k;
        _rpm += (_targetRpm - _rpm) * k;
        _level += (_targetLevel - _level) * ke;

        if (Math.Abs(_rpm) > 0.01)
            _angle = (_angle + _rpm * 6.0 * dt) % 360.0;
        _rotor.Angle = _angle;

        double pulse = 0.5 + 0.5 * Math.Sin(time * 1.7);
        _glow.Opacity = 0.14 + 0.42 * _energy * (0.72 + 0.28 * pulse);

        // Ruhende Maschine: der Kreis atmet sanft als "Bereit"-Anzeige. Der Haken fuer
        // einen abgeschlossenen Lauf ("Fertig") pulsiert deutlicher und erscheint nur dort.
        double beat = _completionVisible ? (0.55 + 0.45 * Math.Sin(time * 1.90))
                                           : (0.34 + 0.20 * Math.Sin(time * 1.05));
        double readyTarget = _readyVisible ? beat : 0.0;
        _readyOpacity += (readyTarget - _readyOpacity) * ke;
        _readyGlow.Opacity = _readyOpacity * 0.32;
        _readyRing.Opacity = _readyOpacity * 0.95;
        _readyTick.Opacity = _completionVisible ? _readyOpacity * 0.92 : 0.0;

        bool settled = _energy < 0.02 && Math.Abs(_targetLevel - _level) < 0.06;

        if (_isWasher)
        {
            if (settled && _surfaceBuilt)
            {
                // Ruhezustand: Wasser bleibt stehen, Geometrie wird nicht mehr neu gebaut.
            }
            else
            {
                _surface.Data = BuildSurface(time, _energy);
                _surfaceBuilt = true;
            }
        }
        else
        {
            for (int i = 0; i < _heatBands.Length; i++)
            {
                double bandEnergy = _energy * (0.55 + 0.45 * Math.Sin(time * (1.15 + i * 0.42) + i));
                bandEnergy = Math.Max(0.0, bandEnergy);
                _heatBands[i].Opacity = bandEnergy * (i == 1 ? 0.95 : 0.72);
                if (bandEnergy > 0.02)
                    _heatBands[i].Data = BuildHeatBand(time, i);
            }
        }

        UpdateParticles(dt);
    }

    // ---------------- Geometrie: Sinus-Wasserwelle ----------------

    private StreamGeometry BuildSurface(double t, double energy)
    {
        double surfaceY = C + _level;
        double amp = 1.6 + 6.5 * energy;
        double left = C - InnerR;
        double right = C + InnerR;
        double width = right - left;
        double bottom = C + InnerR + 2;

        StreamGeometry geo = new StreamGeometry();
        using (StreamGeometryContext ctx = geo.Open())
        {
            ctx.BeginFigure(new Point(left, bottom), true, true);
            ctx.LineTo(new Point(left, surfaceY + WavePoint(0.0, t, amp)), true, false);

            for (int i = 1; i <= WaveSteps; i++)
            {
                double local = width * i / WaveSteps;
                ctx.LineTo(new Point(left + local, surfaceY + WavePoint(local, t, amp)), true, false);
            }

            ctx.LineTo(new Point(right, bottom), true, false);
        }

        geo.Freeze();
        return geo;
    }

    /// <summary>
    /// Ueberlagerte Sinuswelle: Grundwelle + gegenlaeufige Sekundaerwelle + feine Ripple.
    /// Dadurch entsteht natuerliches "Swappen" statt eines starren Sinus.
    /// </summary>
    private static double WavePoint(double x, double t, double amp)
    {
        double primary = Math.Sin(x * 0.085 - t * 2.35) * amp;
        double secondary = Math.Sin(x * 0.041 + t * 1.35) * amp * 0.55;
        double ripple = Math.Sin(x * 0.22 - t * 4.1) * amp * 0.16;
        return primary + secondary + ripple;
    }

    private static StreamGeometry BuildHeatBand(double t, int index)
    {
        double y = C - 22 + index * 20;
        double amp = 4.5 + 3.0 * index;
        double left = C - InnerR + 5;
        double right = C + InnerR - 5;

        StreamGeometry geo = new StreamGeometry();
        using (StreamGeometryContext ctx = geo.Open())
        {
            ctx.BeginFigure(new Point(left, y), false, false);
            for (int i = 1; i <= 26; i++)
            {
                double local = (right - left) * i / 26;
                double wave = Math.Sin(local * 0.10 - t * (2.0 + index * 0.55) + index * 1.9) * amp
                            + Math.Sin(local * 0.045 + t * 1.1) * amp * 0.4;
                ctx.LineTo(new Point(left + local, y + wave), true, false);
            }
        }

        geo.Freeze();
        return geo;
    }

    private static GeometryGroup BuildPaddleGeometry()
    {
        GeometryGroup group = new GeometryGroup { FillRule = FillRule.Nonzero };

        group.Children.Add(NewPaddle(0));
        group.Children.Add(NewPaddle(120));
        group.Children.Add(NewPaddle(240));

        group.Children.Add(new EllipseGeometry(CenterPoint, 9, 9));
        group.Children.Add(new EllipseGeometry(CenterPoint, 4.2, 4.2));
        group.Freeze();
        return group;
    }

    /// <summary>Ein Trommelmitnehmer, per Geometry.Transform um den Trommelmittelpunkt gedreht.</summary>
    private static RectangleGeometry NewPaddle(double angle)
    {
        RectangleGeometry paddle = new RectangleGeometry(new Rect(C - 5.5, C - 40, 11, 27), 4.5, 4.5);
        paddle.Transform = new RotateTransform(angle, C, C);
        return paddle;
    }

    private static GeometryGroup BuildPerforationGeometry()
    {
        GeometryGroup group = new GeometryGroup { FillRule = FillRule.EvenOdd };

        for (int ring = 0; ring < 2; ring++)
        {
            double radius = ring == 0 ? 34 : 22;
            int count = ring == 0 ? 14 : 8;

            for (int i = 0; i < count; i++)
            {
                double a = Math.PI * 2 * i / count + ring * 0.22;
                group.Children.Add(new EllipseGeometry(
                    new Point(C + Math.Cos(a) * radius, C + Math.Sin(a) * radius),
                    2.1, 2.1));
            }
        }

        group.Freeze();
        return group;
    }

    private static PathGeometry BuildSheenGeometry()
    {
        // Bogfoermiger Glasreflex oben links im Bullauge (Specular-Highlight)
        PathGeometry geo = new PathGeometry();
        PathFigure fig = new PathFigure { StartPoint = new Point(C - 34, C - 22), IsFilled = true };
        fig.Segments.Add(new BezierSegment(new Point(C - 26, C - 44), new Point(C + 6, C - 48), new Point(C + 26, C - 34), true));
        fig.Segments.Add(new BezierSegment(new Point(C + 6, C - 36), new Point(C - 20, C - 32), new Point(C - 34, C - 22), true));
        geo.Figures.Add(fig);
        geo.Freeze();
        return geo;
    }

    private static PathGeometry BuildTickGeometry(double cx, double cy)
    {
        PathGeometry geo = new PathGeometry();
        PathFigure fig = new PathFigure
        {
            StartPoint = new Point(cx - 5.5, cy + 0.5),
            IsClosed = false,
            IsFilled = false
        };
        fig.Segments.Add(new LineSegment(new Point(cx - 1.5, cy + 4.5), true));
        fig.Segments.Add(new LineSegment(new Point(cx + 6.0, cy - 4.5), true));
        geo.Figures.Add(fig);
        geo.Freeze();
        return geo;
    }

    private static void CenterOnCanvas(FrameworkElement element, double size)
    {
        double offset = (DesignSize - size) / 2;
        Canvas.SetLeft(element, offset);
        Canvas.SetTop(element, offset);
    }

    // ---------------- Partikel (Blaeschen / Hitze-Partikel) ----------------

    private void SeedParticles()
    {
        int count = _isWasher ? 16 : 22;

        for (int i = 0; i < count; i++)
        {
            Ellipse view = new Ellipse { Fill = _particleBrush, Opacity = 0.0 };
            _particleCanvas.Children.Add(view);

            Particle p = new Particle { View = view };
            Respawn(ref p, Random.Shared.NextDouble());
            _particles.Add(p);
        }
    }

    private void UpdateParticles(double dt)
    {
        double spawnChance = _energy * dt * (_isWasher ? 3.4 : 5.2);

        for (int i = 0; i < _particles.Count; i++)
        {
            Particle p = _particles[i];

            if (p.MaxLife <= 0)
            {
                Respawn(ref p, 0.0);
                _particles[i] = p;
                continue;
            }

            p.Life += dt;

            if (p.Life >= p.MaxLife)
            {
                p.View.Opacity = 0.0;
                p.Life = p.MaxLife;

                if (Random.Shared.NextDouble() < spawnChance)
                    Respawn(ref p, 0.0);
            }
            else
            {
                if (_isWasher)
                {
                    p.Y += p.VY * dt;
                    p.X += p.VX * dt + Math.Sin(p.Life * 5.2 + p.Phase) * 5.4 * dt;
                }
                else
                {
                    p.Y += p.VY * dt;
                    p.X += Math.Sin(p.Life * 3.1 + p.Phase) * 11.0 * dt;
                }

                double lifeK = p.Life / p.MaxLife;
                double fade = lifeK < 0.15 ? lifeK / 0.15 : (1.0 - lifeK) / 0.85;

                p.View.Width = p.R * 2;
                p.View.Height = p.R * 2;
                p.View.Opacity = Math.Clamp(fade, 0, 1) * (_isWasher ? 0.88 : 0.78);

                Canvas.SetLeft(p.View, p.X - p.R);
                Canvas.SetTop(p.View, p.Y - p.R);
            }

            _particles[i] = p;
        }
    }

    private void Respawn(ref Particle p, double progressOffset)
    {
        Random rnd = Random.Shared;
        double inner = InnerR - 4;

        if (_isWasher)
        {
            double surfaceY = C + _level;
            p.Y = Math.Min(C + inner, surfaceY + rnd.NextDouble() * 14.0 + 2.0);
            p.X = C + (rnd.NextDouble() * 2 - 1) * (inner - 4);
            p.VY = -(7.0 + rnd.NextDouble() * 13.0);
            p.VX = (rnd.NextDouble() * 2 - 1) * 4.0;
            p.R = 1.1 + rnd.NextDouble() * 2.4;
            p.MaxLife = 1.6 + rnd.NextDouble() * 2.4;
        }
        else
        {
            p.Y = C + inner - rnd.NextDouble() * 10.0;
            p.X = C + (rnd.NextDouble() * 2 - 1) * (inner - 6);
            p.VY = -(16.0 + rnd.NextDouble() * 26.0);
            p.VX = 0.0;
            p.R = 0.9 + rnd.NextDouble() * 1.7;
            p.MaxLife = 1.2 + rnd.NextDouble() * 2.1;
        }

        p.Phase = rnd.NextDouble() * Math.PI * 2;
        p.Life = progressOffset * p.MaxLife;
        p.View.Opacity = 0.0;
    }

    // ---------------- Pinsel-Helfer ----------------

    private static LinearGradientBrush BuildSurfaceBrush(Color accent)
    {
        LinearGradientBrush brush = new LinearGradientBrush
        {
            StartPoint = new Point(0, 0),
            EndPoint = new Point(0, 1)
        };
        brush.GradientStops.Add(new GradientStop(Color.FromArgb(0x9E, accent.R, accent.G, accent.B), 0.0));
        brush.GradientStops.Add(new GradientStop(Color.FromArgb(0x78, 0x14, 0x8C, 0xD4), 0.55));
        brush.GradientStops.Add(new GradientStop(Color.FromArgb(0x52, 0x08, 0x3C, 0x66), 1.0));
        brush.Freeze();
        return brush;
    }

    private static RadialGradientBrush BuildGlowBrush(Color accent, double coreAlpha)
    {
        byte core = (byte)Math.Round(255 * coreAlpha);
        byte mid = (byte)Math.Round(255 * coreAlpha * 0.45);

        RadialGradientBrush brush = new RadialGradientBrush
        {
            GradientOrigin = new Point(0.5, 0.5),
            Center = new Point(0.5, 0.5),
            RadiusX = 0.5,
            RadiusY = 0.5
        };
        brush.GradientStops.Add(new GradientStop(Color.FromArgb(core, accent.R, accent.G, accent.B), 0.0));
        brush.GradientStops.Add(new GradientStop(Color.FromArgb(mid, accent.R, accent.G, accent.B), 0.45));
        brush.GradientStops.Add(new GradientStop(Color.FromArgb(0x00, accent.R, accent.G, accent.B), 1.0));
        brush.Freeze();
        return brush;
    }

    private static LinearGradientBrush Freeze(LinearGradientBrush brush)
    {
        brush.Freeze();
        return brush;
    }

    private static RadialGradientBrush Freeze(RadialGradientBrush brush)
    {
        brush.Freeze();
        return brush;
    }

    private static SolidColorBrush Freeze(SolidColorBrush brush)
    {
        brush.Freeze();
        return brush;
    }
}
