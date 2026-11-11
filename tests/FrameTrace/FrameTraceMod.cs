using Modding;
using UnityEngine;

namespace HollowConnect.FrameTrace;

// Distributed separately from Smoke: no save guards, input simulation, rule
// changes or production project reference belongs in this observer.
public sealed class FrameTraceMod : Mod {
    public FrameTraceMod() : base("HollowConnect Frame Trace") { }
    public override string GetVersion() => "diagnostic-1.0.0";
    public override void Initialize() {
        if (FrameTraceCapture.Instance != null) return;
        var go = new GameObject("HollowConnect frame trace");
        Object.DontDestroyOnLoad(go); go.AddComponent<FrameTraceCapture>();
        Log("F2: start/stop 120-second frame capture; F3: mark a stutter. Output: " + Application.persistentDataPath + "/HollowConnect-FrameTrace");
    }
}
