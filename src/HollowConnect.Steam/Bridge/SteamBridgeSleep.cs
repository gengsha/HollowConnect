// Adapted from Muurrcc/AHK-asyncronus-hollow-knight, commit 622735cb788dc718c8de0ec5c0ab4522366bcf2f.
// LGPL-2.1; see LICENSE and ../THIRD-PARTY.md. HollowConnect adaptations (2026).
using System;
using System.Runtime.InteropServices;
using System.Threading;

namespace HollowConnect.Steam.Bridge;

/// <summary>
/// Short sleeps for the bridge worker threads. Steam networking messages cannot be waited on, so the workers poll;
/// <see cref="Thread.Sleep(int)"/> on Windows sleeps ~15.6 ms at the default timer resolution, which would add
/// that much latency per hop. On Windows 10 1803+ a high resolution waitable timer is used instead (it only affects
/// the calling thread, not the global timer resolution of the process). Elsewhere it falls back to Thread.Sleep.
/// </summary>
internal static class SteamBridgeSleep {
    private const uint CreateWaitableTimerHighResolution = 0x00000002;
    private const uint TimerAllAccess = 0x1F0003;
    private const uint Infinite = 0xFFFFFFFF;

    [ThreadStatic]
    private static IntPtr _timer;

    [ThreadStatic]
    private static bool _timerUnavailable;

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr CreateWaitableTimerExW(IntPtr attributes, string name, uint flags, uint access);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetWaitableTimer(
        IntPtr timer,
        ref long dueTime,
        int period,
        IntPtr completionRoutine,
        IntPtr argument,
        bool resume
    );

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint WaitForSingleObject(IntPtr handle, uint milliseconds);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr handle);

    /// <summary>
    /// Sleep for approximately the given time.
    /// </summary>
    /// <param name="microseconds">The time to sleep.</param>
    public static void Sleep(int microseconds) {
        if (microseconds <= 0) {
            Thread.Yield();
            return;
        }

        if (!_timerUnavailable && TrySleepHighResolution(microseconds)) {
            return;
        }

        Thread.Sleep(System.Math.Max(1, microseconds / 1000));
    }

    private static bool TrySleepHighResolution(int microseconds) {
        try {
            if (_timer == IntPtr.Zero) {
                if (Environment.OSVersion.Platform != PlatformID.Win32NT) {
                    _timerUnavailable = true;
                    return false;
                }

                _timer = CreateWaitableTimerExW(IntPtr.Zero, null, CreateWaitableTimerHighResolution, TimerAllAccess);
                if (_timer == IntPtr.Zero) {
                    // Older Windows without high resolution timers
                    _timerUnavailable = true;
                    return false;
                }
            }

            // Negative: relative time in 100 ns units
            var dueTime = -(long) microseconds * 10L;
            if (!SetWaitableTimer(_timer, ref dueTime, 0, IntPtr.Zero, IntPtr.Zero, false)) {
                ReleaseThreadTimer();
                _timerUnavailable = true;
                return false;
            }

            WaitForSingleObject(_timer, Infinite);
            return true;
        } catch (Exception) {
            // DllNotFoundException / EntryPointNotFoundException on other platforms
            _timerUnavailable = true;
            return false;
        }
    }

    /// <summary>
    /// Release the timer of the calling thread (call when a worker thread ends).
    /// </summary>
    public static void ReleaseThreadTimer() {
        if (_timer == IntPtr.Zero) {
            return;
        }

        try {
            CloseHandle(_timer);
        } catch (Exception) {
            // Ignore
        }

        _timer = IntPtr.Zero;
    }
}

