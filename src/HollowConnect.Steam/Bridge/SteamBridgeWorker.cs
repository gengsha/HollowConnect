// Adapted from Muurrcc/AHK-asyncronus-hollow-knight, commit 622735cb788dc718c8de0ec5c0ab4522366bcf2f.
// LGPL-2.1; see LICENSE and ../THIRD-PARTY.md. HollowConnect adaptations (2026).
using System;
using System.Threading;
using Hkmp.Logging;

namespace HollowConnect.Steam.Bridge;

/// <summary>
/// Background thread that pumps an <see cref="ISteamBridgePump"/> until stopped, then disposes it on the same
/// thread. Exceptions never escape the thread.
/// </summary>
internal sealed class SteamBridgeWorker {
    /// <summary>
    /// Maximum time to wait for new data between pumps.
    /// </summary>
    private const int WaitMicroseconds = 1000;

    /// <summary>
    /// Number of consecutive pump exceptions after which the worker gives up.
    /// </summary>
    private const int MaxConsecutiveErrors = 50;

    private readonly ISteamBridgePump _pump;
    private readonly Thread _thread;
    private volatile bool _stopRequested;

    /// <summary>
    /// Optional action that is invoked on the worker thread after every pump (e.g. to observe state changes).
    /// Exceptions are caught and logged.
    /// </summary>
    public Action AfterPump { get; set; }

    /// <summary>
    /// Whether the thread is still running.
    /// </summary>
    public bool IsRunning => _thread.IsAlive;

    public SteamBridgeWorker(string name, ISteamBridgePump pump) {
        _pump = pump;
        _thread = new Thread(Run) {
            Name = name,
            IsBackground = true
        };
    }

    /// <summary>
    /// Start the thread.
    /// </summary>
    public void Start() {
        _thread.Start();
    }

    /// <summary>
    /// Request the thread to stop and wait for it (the pump is disposed by the thread).
    /// </summary>
    /// <param name="timeoutMs">Maximum time to wait.</param>
    /// <returns>True if the thread stopped in time.</returns>
    public bool Stop(int timeoutMs = 2000) {
        _stopRequested = true;
        if (!_thread.IsAlive) {
            return true;
        }

        if (Thread.CurrentThread == _thread) {
            return false;
        }

        return _thread.Join(timeoutMs);
    }

    private void Run() {
        var consecutiveErrors = 0;
        try {
            while (!_stopRequested) {
                try {
                    _pump.Pump();
                    AfterPump?.Invoke();
                    _pump.Wait(WaitMicroseconds);
                    consecutiveErrors = 0;
                } catch (Exception e) {
                    consecutiveErrors++;
                    Logger.Error($"Steam bridge worker '{_thread.Name}': exception:\n{e}");
                    if (consecutiveErrors >= MaxConsecutiveErrors) {
                        Logger.Error($"Steam bridge worker '{_thread.Name}': too many errors, stopping");
                        break;
                    }

                    Thread.Sleep(10);
                }
            }
        } finally {
            try {
                _pump.Dispose();
            } catch (Exception e) {
                Logger.Error($"Steam bridge worker '{_thread.Name}': exception while disposing:\n{e}");
            }

            SteamBridgeSleep.ReleaseThreadTimer();
        }
    }
}

