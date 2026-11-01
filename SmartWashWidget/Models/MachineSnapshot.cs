using System;

namespace SmartWashWidget.Models;

/// <summary>
/// Ein einzelner Poll-Snapshot eines SmartThings-Geraets (oder der Demo-Simulation).
/// Alle Zeiten sind UTC.
/// </summary>
public sealed class MachineSnapshot
{
    public MachineKind Kind { get; init; }
    public string DeviceId { get; init; } = string.Empty;
    public string DeviceLabel { get; init; } = string.Empty;

    public MachineRunState RunState { get; init; } = MachineRunState.Unknown;

    /// <summary>Roher SmartThings-Wert, z. B. "run" | "stop" | "pause".</summary>
    public string RawMachineState { get; init; } = string.Empty;

    /// <summary>Roher Job-State, z. B. "wash" | "rinse" | "spin" | "cool_down".</summary>
    public string RawJobState { get; init; } = string.Empty;

    /// <summary>
    /// ASCII-Phasencode ("wash", "rinse", "spin", "cool_down" ...). Uebersetzt wird erst
    /// bei der Anzeige (MachineCard), damit ein Sprachwechsel sofort wirkt.
    /// </summary>
    public string Phase { get; init; } = PhaseCodes.Standby;

    /// <summary>Fortschritt 0..1 (glaettet die UI weiter).</summary>
    public double Progress { get; init; }

    /// <summary>Restzeit, null wenn unbekannt.</summary>
    public TimeSpan? Remaining { get; init; }

    /// <summary>Absolute Fertigstellung (UTC), null wenn nicht uebermittelt.</summary>
    public DateTimeOffset? CompletionUtc { get; init; }

    /// <summary>Live per API erfasst (true) oder simuliert (false).</summary>
    public bool IsLive { get; init; }

    /// <summary>true, wenn der letzte Abruf erfolglos war (Geraet offline/Token fehlerhaft).</summary>
    public bool IsError { get; init; }

    /// <summary>true, wenn fuer diesen Geraetetype nichts konfiguriert ist (Hinweis "kein Geraet").</summary>
    public bool NoDeviceConfigured { get; init; }

    /// <summary>Kurze, nutzerfreundliche Fehlermeldung (oder Hinweis).</summary>
    public string? StatusMessage { get; init; }

    public DateTimeOffset FetchedUtc { get; init; } = DateTimeOffset.UtcNow;

    public static MachineSnapshot Idle(MachineKind kind, string? message = null)
        => new MachineSnapshot
        {
            Kind = kind,
            Phase = PhaseCodes.Standby,
            NoDeviceConfigured = true,
            StatusMessage = message,
            RunState = MachineRunState.Unknown
        };
}

/// <summary>
/// ASCII-Phasencodes, die im Snapshot stehen (unabhaengig von der Sprache).
/// Die Uebersetzung erfolgt in Resources/Strings.de.xaml und Strings.en.xaml
/// ueber die Keys Str_Phase_&lt;code&gt;.
/// </summary>
public static class PhaseCodes
{
    public const string Standby = "standby";
    public const string Finish = "finish";
    public const string Pause = "pause";
    public const string Offline = "offline";
}

/// <summary>Erkanntes SmartThings-Geraet aus der Geraeteliste.</summary>
public sealed record DiscoveredDevice(
    string Id,
    string Label,
    string? DeviceType,
    MachineKind? InferredKind,
    bool HasWasherCapability,
    bool HasDryerCapability)
{
    public string Display => string.IsNullOrWhiteSpace(Label) ? (DeviceType ?? Id) : Label;

    public string ShortId
    {
        get
        {
            if (Id.Length <= 8) return Id;
            return string.Concat(Id.AsSpan(0, 8), "...");
        }
    }
}
