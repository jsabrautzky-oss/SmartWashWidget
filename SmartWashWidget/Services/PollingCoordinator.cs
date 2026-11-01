using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using SmartWashWidget.Models;

namespace SmartWashWidget.Services;

/// <summary>
/// Adaptiver Hintergrund-Poller: laeuft in einer eigenen async-Schleife und wirft Snapshots
/// per Event. Die UI holt sich die Werte ueber Dispatcher.BeginInvoke, wodurch die
/// 60-FPS-Animation niemals von Netzwerklatenz gebremst wird.
/// Intervall: 20 s bei laufender Maschine, 45 s direkt nach Laufende, 180 s im Standby,
/// Fehler-Backoff 30 s bis 300 s.
/// </summary>
public sealed class PollingCoordinator : IDisposable
{
    public const int RunningIntervalSec = 20;
    public const int FinishedIntervalSec = 45;
    public const int StandbyIntervalSec = 180;
    public const int MinIntervalSec = 5;
    public const int MaxBackoffSec = 300;

    private readonly AppConfigService _config;
    private readonly object _gate = new object();

    private CancellationTokenSource? _cts;
    private Task? _loopTask;
    private SmartThingsClient? _client;

    private readonly Dictionary<MachineKind, RunTracker> _trackers = new Dictionary<MachineKind, RunTracker>
    {
        [MachineKind.Washer] = new RunTracker(),
        [MachineKind.Dryer] = new RunTracker()
    };

    private readonly Dictionary<MachineKind, int> _failures = new Dictionary<MachineKind, int>
    {
        [MachineKind.Washer] = 0,
        [MachineKind.Dryer] = 0
    };

    private MachineRunState _lastState = MachineRunState.Idle;

    public event Action<MachineSnapshot>? SnapshotReady;
    public event Action<MachineKind, string>? DeviceFailed;
    public event Action<string>? ConnectionFailed;

    public bool IsRunning
    {
        get { lock (_gate) return _loopTask is { IsCompleted: false }; }
    }

    public PollingCoordinator(AppConfigService config)
    {
        _config = config;
    }

    public void Start()
    {
        lock (_gate)
        {
            if (_loopTask is { IsCompleted: false }) return;

            if (!_config.HasToken && !_config.HasOAuthCredentials)
            {
                ConnectionFailed?.Invoke(LocalizationManager.Get("Str_Polling_NoAccess"));
                return;
            }

            _cts = new CancellationTokenSource();
            _client = new SmartThingsClient(_config, _config.Config.SmartThings.ApiBase);
            _loopTask = Task.Run(() => RunLoopAsync(_cts.Token));
        }
    }

    public void Stop()
    {
        lock (_gate)
        {
            try { _cts?.Cancel(); } catch { /* egal */ }
        }
    }

    /// <summary>Nach Aenderungen an Token/Device-IDs: komplett neu starten.</summary>
    public void Restart()
    {
        Stop();
        // kurzes Einraeumen, damit der alte Loop sicher beendet ist
        Task.Run(async () =>
        {
            try { if (_loopTask != null) await Task.WhenAny(_loopTask, Task.Delay(1200)); }
            catch { /* egal */ }
            Start();
        });
    }

    private async Task RunLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            var snapshots = new List<MachineSnapshot>();
            string? aggregateError = null;
            int intervalSec = StandbyIntervalSec;

            foreach (MachineKind kind in new[] { MachineKind.Washer, MachineKind.Dryer })
            {
                ct.ThrowIfCancellationRequested();

                string? deviceId = kind == MachineKind.Washer
                    ? _config.Config.SmartThings.WasherDeviceId
                    : _config.Config.SmartThings.DryerDeviceId;

                if (string.IsNullOrWhiteSpace(deviceId))
                    continue;

                SmartThingsClient? client = _client;
                if (client is null) break;

                try
                {
                    // Geratenamen uebergibt die Konfiguration; ohne gespeicherten Namen bleibt das Feld
                    // leer, damit die Karte den lokalisierten Standardnamen (in der aktiven Sprache) setzt.
                    string label = kind == MachineKind.Washer
                        ? _config.Config.SmartThings.WasherLabel ?? string.Empty
                        : _config.Config.SmartThings.DryerLabel ?? string.Empty;

                    MachineSnapshot snap = await client
                        .ReadStatusAsync(deviceId, kind, label, _trackers[kind], ct)
                        .ConfigureAwait(false);

                    _failures[kind] = 0;
                    snapshots.Add(snap);

                    intervalSec = snap.RunState switch
                    {
                        MachineRunState.Running or MachineRunState.Paused => Math.Min(intervalSec, RunningIntervalSec),

                        // "Fertig" soll eine Weile sichtbar bleiben, danach aber zuegig auf Bereit zurueckkehren.
                        MachineRunState.Finished => Math.Min(intervalSec, FinishedIntervalSec),
                        _ => Math.Min(intervalSec, StandbyIntervalSec)
                    };
                }
                catch (OperationCanceledException)
                {
                    return;
                }
                catch (SmartThingsException ex)
                {
                    _trackers[kind].Reset();
                    _failures[kind]++;

                    aggregateError = ex.UserText;
                    DeviceFailed?.Invoke(kind, ex.UserText);

                    intervalSec = _failures[kind] >= 6 ? 1 : intervalSec;
                    if (ex.StatusCode == System.Net.HttpStatusCode.Unauthorized
                        || ex.StatusCode == System.Net.HttpStatusCode.Forbidden)
                    {
                        ConnectionFailed?.Invoke(ex.UserText + " " + LocalizationManager.Get("Str_Polling_StoppedSuffix"));
                        _config.RememberSync(ex.UserText);
                        return;
                    }

                    int backoff = Math.Min(MaxBackoffSec, RunningIntervalSec * (int)Math.Pow(2, Math.Min(_failures[kind], 4)));
                    intervalSec = Math.Min(intervalSec, backoff);
                }
                catch (Exception ex)
                {
                    _trackers[kind].Reset();
                    _failures[kind]++;
                    aggregateError = ex.Message;
                    DeviceFailed?.Invoke(kind, LocalizationManager.Format("Str_Err_Unexpected", ex.Message));
                    intervalSec = Math.Min(intervalSec, 60);
                }
            }

            _lastState = snapshots.Count > 0 && snapshots[0].RunState != MachineRunState.Idle
                ? snapshots[0].RunState
                : _lastState;

            _config.RememberSync(aggregateError);

            foreach (MachineSnapshot snap in snapshots)
                SnapshotReady?.Invoke(snap);

            // Jitter vermeidet Gleichlauf mit anderen Clients.
            int delaySec = Math.Max(MinIntervalSec, intervalSec);
            int jitter = Random.Shared.Next(0, 2500);

            try
            {
                await Task.Delay(TimeSpan.FromSeconds(delaySec) + TimeSpan.FromMilliseconds(jitter), ct)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    public void ResetTrackers()
    {
        foreach (RunTracker t in _trackers.Values) t.Reset();
        foreach (var k in _failures.Keys.ToList()) _failures[k] = 0;
    }

    public void Dispose()
    {
        Stop();
        try { if (_loopTask != null) _loopTask.Wait(TimeSpan.FromSeconds(2)); } catch { /* egal */ }
        _client?.Dispose();
        _cts?.Dispose();
    }
}
