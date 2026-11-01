using System;

namespace SmartWashWidget.Models;

/// <summary>Die beiden Geraete-Typen des Widgets.</summary>
public enum MachineKind
{
    Washer = 0,
    Dryer = 1
}

/// <summary>Vereinheitlichter Maschinenzustand (aus SmartThings oder Demo-Simulation).</summary>
public enum MachineRunState
{
    Unknown = 0,
    Running = 1,
    Paused = 2,
    Idle = 3,
    Finished = 4,
    Unreachable = 5
}

public static class MachineKindExtensions
{
    /// <summary>
    /// Key des Anzeigenamens im aktiven Sprachdictionary (Waschmaschine / Washer).
    /// Der Text selbst kommt immer erst bei der Anzeige, damit ein Sprachwechsel
    /// ohne Neustart wirkt.
    /// </summary>
    public static string DisplayNameKey(this MachineKind kind) => kind switch
    {
        MachineKind.Washer => "Str_Device_Washer",
        MachineKind.Dryer => "Str_Device_Dryer",
        _ => "Str_Device_Generic"
    };

    /// <summary>Technischer Capability-Name der SmartThings-API (bleibt ungeaendert).</summary>
    public static string CapabilityName(this MachineKind kind) => kind switch
    {
        MachineKind.Washer => "washerOperatingState",
        MachineKind.Dryer => "dryerOperatingState",
        _ => "operatingState"
    };
}
