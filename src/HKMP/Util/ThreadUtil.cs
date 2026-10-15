using System;
using System.Collections.Generic;
using System.Diagnostics;
using UnityEngine;

namespace Hkmp.Util;

/// <summary>
/// Class for utilities regarding threading.
/// </summary>
internal class ThreadUtil : MonoBehaviour {
    /// <summary>
    /// Object to lock asynchronous access.
    /// </summary>
    private static readonly object Lock = new object();

    /// <summary>
    /// List of actions that need to be run on the Unity main thread.
    /// </summary>
    private static readonly Queue<Action> ActionsToRun = new();
    private static readonly long FrameBudget = Stopwatch.Frequency / 500; // 2 ms

    /// <summary>
    /// Instantiate this static class.
    /// </summary>
    public static void Instantiate() {
        var threadUtilObject = new GameObject();
        threadUtilObject.AddComponent<ThreadUtil>();
        DontDestroyOnLoad(threadUtilObject);
    }

    /// <summary>
    /// Runs the given action on the main thread of Unity.
    /// </summary>
    /// <param name="action">The action to run.</param>
    public static void RunActionOnMainThread(Action action) {
        lock (Lock) {
            ActionsToRun.Enqueue(action);
        }
    }

    public void Update() => RunPendingActions();

    internal static void RunPendingActions() {
        int count;
        lock (Lock) count = ActionsToRun.Count;
        var start = Stopwatch.GetTimestamp();
        for (var i = 0; i < count; i++) {
            Action action;
            lock (Lock) action = ActionsToRun.Dequeue();
            // One callback contains a complete received datagram. Yield only
            // between callbacks, preserving its parent/child/action ordering.
            action.Invoke();
            if (Stopwatch.GetTimestamp() - start >= FrameBudget) break;
        }
    }
}
