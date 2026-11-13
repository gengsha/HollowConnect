using System;
using System.Collections;
using System.IO;
using System.Reflection;
using HollowConnect;
using MonoMod.RuntimeDetour;
using Newtonsoft.Json;
using UnityEngine;

namespace HollowConnect.Smoke;

// Observe real IMGUI submissions while the existing gameplay fixture runs.
// Do not substitute a policy boolean for observing the rendered frame path.
public sealed class HudAudit : MonoBehaviour {
    private Hook labels;
    private string role, root;
    private int routineLabels, soulHudLabels, repaintFrames;
    private const string Marker = "HC_HUD_LOG_ONLY_GIVE_UP";
    private void Awake() {
        role = Environment.GetEnvironmentVariable("HOLLOWCONNECT_SMOKE_ROLE"); root = Environment.GetEnvironmentVariable("HOLLOWCONNECT_SMOKE_ROOT");
        labels = new Hook(typeof(GUI).GetMethod("Label", new[] { typeof(Rect), typeof(string), typeof(GUIStyle) }),
            (Action<Action<Rect, string, GUIStyle>, Rect, string, GUIStyle>)((orig, rect, text, style) => {
                if (text != null && text.Contains(Marker)) routineLabels++;
                if (rect.x == 20 && rect.y >= 125 && rect.width == 350) soulHudLabels++;
                orig(rect, text, style);
            }));
    }
    private IEnumerator Start() {
        var deadline = Time.realtimeSinceStartup + 100;
        CoopRuntime runtime;
        while ((runtime = FindObjectOfType<CoopRuntime>()) == null || !runtime.Connected) {
            if (Time.realtimeSinceStartup > deadline) { Fail("HUD audit never connected"); yield break; }
            yield return null;
        }
        global::Language.Language.SwitchLanguage(role == "host" ? global::Language.LanguageCode.EN : global::Language.LanguageCode.JA);
        runtime.Notice(Marker);
        yield return new WaitForSecondsRealtime(12);
        var result = new { routineLabels, soulHudLabels, repaintFrames, language = Hkmp.Ui.Localize.Language, marker = Marker };
        File.WriteAllText(Path.Combine(root, role + ".hud-audit.json"), JsonConvert.SerializeObject(result));
        if (routineLabels != 0 || soulHudLabels != 0 || repaintFrames < 10) { Fail("HUD text leaked or no rendered frames: " + JsonConvert.SerializeObject(result)); yield break; }
        File.AppendAllText(Path.Combine(root, role + ".log"), DateTime.UtcNow.ToString("O") + " PASS live HUD audit; routine notices logged; soul HUD has no cross-room names; language=" + Hkmp.Ui.Localize.Language + "\n");
        labels.Dispose(); labels = null;
    }
    private void OnGUI() { if (Event.current.type == EventType.Repaint) repaintFrames++; }
    private void Fail(string reason) => File.WriteAllText(Path.Combine(root, role + ".failed"), reason);
    private void OnDestroy() { labels?.Dispose(); }
}
