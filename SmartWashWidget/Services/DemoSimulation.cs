using System;

using SmartWashWidget.Models;

namespace SmartWashWidget.Services;

/// <summary>Code = ASCII-Schluessel (fuer Animation, Snapshot und Uebersetzung).</summary>
internal readonly record struct DemoPhase(string Code, int DurationSec);

/// <summary>
/// Live-Demo-Modus: simuliert realistische Laeufe inkl. Phasen, Restzeit und Fortschritt.
/// Laeuft im UI-Thread zusammen mit der Animation (dt-basiert, daher bildratenunabhaengig).
/// Standardmaessig aktiv, solange kein SmartThings-Token hinterlegt ist.
/// </summary>
public sealed class DemoSimulation
{
    private static readonly DemoPhase[] WasherPhases =
    {
        new DemoPhase("prewash", 105),
        new DemoPhase("wash", 540),
        new DemoPhase("rinse", 300),
        new DemoPhase("spin", 240)
    };

    private static readonly DemoPhase[] DryerPhases =
    {
        new DemoPhase("timedry", 900),
        new DemoPhase("anticrease", 120),
        new DemoPhase("cooldown", 210)
    };

    private const int FinishHoldSec = 14;

    private sealed class Cycle
    {
        public readonly DemoPhase[] Phases;
        public readonly int TotalSec;
        public int ElapsedSec;

        public Cycle(DemoPhase[] phases)
        {
            Phases = phases;
            foreach (DemoPhase p in phases) TotalSec += p.DurationSec;
        }

        public (DemoPhase Phase, double Progress, TimeSpan Remaining) Sample()
        {
            int acc = 0;
            DemoPhase current = Phases[Phases.Length - 1];

            foreach (DemoPhase p in Phases)
            {
                if (ElapsedSec < acc + p.DurationSec)
                {
                    current = p;
                    break;
                }
                acc += p.DurationSec;
            }

            double progress = TotalSec <= 0 ? 1.0 : Math.Clamp((double)ElapsedSec / TotalSec, 0.0, 1.0);
            int left = Math.Max(0, TotalSec - ElapsedSec);
            return (current, progress, TimeSpan.FromSeconds(left));
        }
    }

    private readonly Cycle _washer = new Cycle(WasherPhases);
    private readonly Cycle _dryer = new Cycle(DryerPhases);

    private double _washerAcc;
    private double _dryerAcc;
    private double _finishWasher;
    private double _finishDryer;

    public MachineSnapshot Washer() => Build(MachineKind.Washer, _washer, _finishWasher > 0);

    public MachineSnapshot Dryer() => Build(MachineKind.Dryer, _dryer, _finishDryer > 0);

    /// <summary>Wird pro Animationsframe mit der echten Framezeit aufgerufen.</summary>
    public void Tick(double deltaSeconds)
    {
        if (deltaSeconds <= 0) return;
        if (deltaSeconds > 0.5) deltaSeconds = 0.5;

        _washerAcc += deltaSeconds;
        while (_washerAcc >= 1.0)
        {
            _washerAcc -= 1.0;
            Advance(_washer, ref _finishWasher);
        }

        _dryerAcc += deltaSeconds;
        while (_dryerAcc >= 1.0)
        {
            _dryerAcc -= 1.0;
            Advance(_dryer, ref _finishDryer);
        }
    }

    private static void Advance(Cycle cycle, ref double finishHold)
    {
        if (finishHold > 0)
        {
            finishHold -= 1;
            if (finishHold <= 0)
            {
                finishHold = 0;
                cycle.ElapsedSec = 0;
            }
            return;
        }

        cycle.ElapsedSec++;
        if (cycle.ElapsedSec >= cycle.TotalSec)
        {
            cycle.ElapsedSec = cycle.TotalSec;
            finishHold = FinishHoldSec;
        }
    }

    private static MachineSnapshot Build(MachineKind kind, Cycle cycle, bool finishing)
    {
        var sample = cycle.Sample();

        MachineRunState state = finishing || sample.Progress >= 1.0
            ? MachineRunState.Finished
            : MachineRunState.Running;

        return new MachineSnapshot
        {
            Kind = kind,
            DeviceId = "demo-" + (kind == MachineKind.Washer ? "washer" : "dryer"),
            DeviceLabel = string.Empty,
            RunState = state,
            RawMachineState = state == MachineRunState.Finished ? "finish" : "run",
            // ASCII-Codes: Animation, Trommel und Uebersetzung steuern/lesen ausschliesslich danach
            RawJobState = state == MachineRunState.Finished ? "finish" : sample.Phase.Code,
            Phase = state == MachineRunState.Finished ? PhaseCodes.Finish : sample.Phase.Code,
            Progress = sample.Progress,
            Remaining = finishing ? TimeSpan.Zero : sample.Remaining,
            CompletionUtc = DateTimeOffset.UtcNow + (finishing ? TimeSpan.Zero : sample.Remaining),
            IsLive = false,
            IsError = false,
            StatusMessage = LocalizationManager.Get("Str_Hint_DemoActive"),
            FetchedUtc = DateTimeOffset.UtcNow
        };
    }
}
