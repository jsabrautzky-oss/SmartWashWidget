using System;

namespace SmartWashWidget.Models;

/// <summary>Verbindungszustand, den die Kopfzeile des Widgets anzeigt.</summary>
public enum ConnectionState
{
    /// <summary>Kein Poller aktiv.</summary>
    Off = 0,

    /// <summary>Simulierte Daten (Demo-Modus).</summary>
    Demo = 1,

    /// <summary>Live-Daten von api.smartthings.com.</summary>
    Live = 2,

    /// <summary>Live, aber zuletzt gab es Fehler bei einem Geraet.</summary>
    Degraded = 3,

    /// <summary>Zugang abgelehnt / Netzwerk weg.</summary>
    Error = 4
}
