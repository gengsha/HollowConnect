using System;
using System.Collections;
using System.IO;
using System.Linq;
using System.Reflection;
using HollowConnect;
using Modding;
using UnityEngine;
using Object = UnityEngine.Object;

namespace HollowConnect.Smoke;

// Narrow cutscene-exit boundary fixture, not a replay of the full ending.
public sealed partial class EndingHudProbe : MonoBehaviour {
    private string root, role;
    private CoopRuntime runtime;
    private Hkmp.Game.GameManager engine;
    private static T Field<T>(object o, string n) => (T)o.GetType().GetField(n, BindingFlags.Instance | BindingFlags.NonPublic).GetValue(o);
    private void Mark(string s) => File.AppendAllText(Path.Combine(root, role + ".log"), DateTime.UtcNow.ToString("O") + " " + s + "\n");
    private void Flag(string s) => File.WriteAllText(Path.Combine(root, s), "ready");
    private bool Has(string s) => File.Exists(Path.Combine(root, s));
    private void Check(bool ok, string s) { if (!ok) throw new Exception(s); Mark("PASS " + s); }
    private IEnumerator Until(Func<bool> ready, string s, float seconds = 35) {
        var deadline = Time.realtimeSinceStartup + seconds;
        while (!ready() && Time.realtimeSinceStartup < deadline) yield return null;
        Check(ready(), s);
    }
    private IEnumerator Start() {
        root = Environment.GetEnvironmentVariable("HOLLOWCONNECT_SMOKE_ROOT"); role = Environment.GetEnvironmentVariable("HOLLOWCONNECT_SMOKE_ROLE");
        var stack = new System.Collections.Generic.Stack<IEnumerator>(); stack.Push(Run());
        while (stack.Count > 0) {
            bool more; object current = null;
            try { more = stack.Peek().MoveNext(); if (more) current = stack.Peek().Current; }
            catch (Exception e) { Mark("FAIL " + e); Flag(role + ".failed"); yield break; }
            if (!more) { stack.Pop(); continue; }
            if (current is IEnumerator nested) stack.Push(nested); else yield return current;
        }
    }
    private IEnumerator Run() {
        yield return Until(() => Object.FindObjectOfType<CoopRuntime>() != null && GameManager.instance != null && GameManager.instance.sceneName == "Menu_Title", "menu ready");
        runtime = Object.FindObjectOfType<CoopRuntime>(); engine = Field<Hkmp.Game.GameManager>(ModHooks.GetMod("HKMP"), "_gameManager");
        Field<Hkmp.Game.Settings.ModSettings>(engine.ClientManager, "_modSettings").AuthKey = Hkmp.Util.AuthUtil.GenerateAuthKey();
        var pd = PlayerData.instance;
        // The room captures save mode at host start; prepare Steel Soul
        // before connecting instead of mutating an active room's character.
        if (Environment.GetEnvironmentVariable("HOLLOWCONNECT_ENDING_STEEL") == "1") pd.SetInt("permadeathMode", 1);
        pd.SetString("respawnScene", "Town"); pd.SetString("respawnMarkerName", "RestBench"); pd.SetInt("respawnType", 1);
        GameManager.instance.profileID = 99; GameManager.instance.StartCoroutine(GameManager.instance.RunContinueGame());
        yield return Until(() => runtime.Playable && GameManager.instance.HasFinishedEnteringScene && GameManager.instance.sceneName == "Town", "unsaved native bench entry");
        yield return EndingBenchGetUp();
        if (role == "host") { engine.ServerManager.Start(29650, false); Flag("server.started"); }
        else yield return Until(() => Has("server.started"), "server started");
        typeof(Hkmp.Game.Client.ClientManager).GetField("_autoConnect", BindingFlags.Instance | BindingFlags.NonPublic).SetValue(engine.ClientManager, true);
        typeof(Hkmp.Game.Client.ClientManager).GetMethod("Connect", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(engine.ClientManager, new object[] { "127.0.0.1", 29650, "hud" + role });
        yield return Until(() => runtime.Connected, "connected"); Flag(role + ".connected");
        yield return Until(() => Has((role == "host" ? "guest" : "host") + ".connected"), "both connected");
        if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable("HOLLOWCONNECT_RADIANCE_ENDING_HUD"))) {
            yield return RadianceEndingHudChecks();
            yield return ProbeShutdown.Run(engine, root, role);
            Mark("FINAL PASS Radiance ending HUD " + Environment.GetEnvironmentVariable("HOLLOWCONNECT_RADIANCE_ENDING_HUD"));
            Flag(role + ".done"); yield break;
        }
        yield return EndingRoomChecks();
        yield return ProbeShutdown.Run(engine, root, role);
        Mark("FINAL PASS native ending disconnect, host close and HUD return"); Flag(role + ".done");
    }
}
