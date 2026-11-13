using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using Newtonsoft.Json;
using UnityEngine;

namespace HollowConnect.Smoke;

// Opt-in, test-only recording. No file I/O or per-frame log formatting while
// sampling. Keep loading windows separate from the stable combat window.
public sealed class RouteFrameRecorder : MonoBehaviour {
    private struct Frame {
        public int index, gc;
        public double wallMs, unityMs;
    }
    private sealed class Window {
        public string scene, phase;
        public readonly List<Frame> frames = new(8192);
    }
    private readonly List<Window> windows = new();
    private Window current;
    private string root, role, phase = "route";
    private long previous;
    private int collections;
    private float nextCheck;
    private bool finished;
    private void Awake() {
        root = Environment.GetEnvironmentVariable("HOLLOWCONNECT_SMOKE_ROOT");
        role = Environment.GetEnvironmentVariable("HOLLOWCONNECT_SMOKE_ROLE");
        Application.runInBackground = true; QualitySettings.vSyncCount = 0;
        Application.targetFrameRate = 120;
        previous = Stopwatch.GetTimestamp(); collections = GC.CollectionCount(0);
    }
    internal void Phase(string name) { phase = name; current = null; }
    private void Update() {
        if (finished) return;
        var now = Stopwatch.GetTimestamp(); var gc = GC.CollectionCount(0);
        var manager = GameManager.instance;
        var scene = manager == null ? "startup" : manager.sceneName;
        var stage = manager != null && manager.IsInSceneTransition ? "loading" : phase;
        if (current == null || current.scene != scene || current.phase != stage) {
            current = new Window { scene = scene, phase = stage }; windows.Add(current);
        }
        // Skip the boundary frame and cap memory for a stalled fixture.
        else if (current.frames.Count < 60000) current.frames.Add(new Frame {
            index = Time.frameCount, wallMs = (now - previous) * 1000.0 / Stopwatch.Frequency,
            unityMs = Time.unscaledDeltaTime * 1000.0, gc = gc - collections
        });
        previous = now; collections = gc;
        if (Time.realtimeSinceStartup < nextCheck) return;
        nextCheck = Time.realtimeSinceStartup + 1;
        if (File.Exists(Path.Combine(root, role + ".done")) || File.Exists(Path.Combine(root, role + ".failed"))) Finish();
    }
    internal void Finish() {
        if (finished) return; finished = true;
        var results = windows.Where(w => w.frames.Count > 0).Select(w => {
            var sorted = w.frames.Select(f => f.wallMs).OrderBy(v => v).ToArray();
            double P(double p) => sorted[(int)((sorted.Length - 1) * p)];
            return new { w.scene, w.phase, samples = sorted.Length, averageFps = 1000 / sorted.Average(),
                onePercentLowFps = 1000 / sorted.Skip((int)(sorted.Length * .99)).Average(),
                p50Ms = P(.5), p95Ms = P(.95), p99Ms = P(.99), maxMs = sorted.Last(),
                over16ms = sorted.Count(v => v > 16.667), over33ms = sorted.Count(v => v > 33.333),
                over50ms = sorted.Count(v => v > 50), collections = w.frames.Sum(f => f.gc), w.frames };
        }).ToArray();
        File.WriteAllText(Path.Combine(root, role + ".route-frames.json"), JsonConvert.SerializeObject(new {
            targetFps = Application.targetFrameRate, vSync = QualitySettings.vSyncCount,
            rendered = Environment.GetEnvironmentVariable("HOLLOWCONNECT_RENDERED_TEST") == "1",
            environment = "same machine, two isolated clients, 800x600", windows = results
        }));
    }
}
