using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using Hkmp.Game.Client.Entity;
using HollowConnect;
using Modding;
using HollowConnect.Core;
using Newtonsoft.Json;
using UnityEngine;
using Object = UnityEngine.Object;

namespace HollowConnect.Smoke;

// Targeted fifth-sequence traversal, preceding rescue and presentation checks; no player saves.
public sealed class P5MotionProbe : MonoBehaviour {
    private string root, role;
    private bool host;
    private CoopRuntime runtime;
    private Hkmp.Game.GameManager engine;
    private EntityManager manager;
    private static T Field<T>(object obj, string key) => (T)obj.GetType().GetField(key, BindingFlags.Instance | BindingFlags.NonPublic).GetValue(obj);
    private static string Scene => UnityEngine.SceneManagement.SceneManager.GetActiveScene().name;
    private void Log(string text) => File.AppendAllText(Path.Combine(root, role + ".log"), DateTime.UtcNow.ToString("O") + " " + text + "\n");
    private void Flag(string name) => File.WriteAllText(Path.Combine(root, name), "ready");
    private bool Has(string name) => File.Exists(Path.Combine(root, name));
    private void Require(bool ok, string label) { if (!ok) throw new Exception(label); Log("PASS " + label); }
    private IEnumerator Until(Func<bool> ready, string label, float seconds = 55) {
        var end = Time.realtimeSinceStartup + seconds;
        while (!ready() && Time.realtimeSinceStartup < end) yield return null;
        Require(ready(), label);
    }
    private bool In(string scene) => Scene == scene && runtime.Playable && !GameManager.instance.IsInSceneTransition;
    private void Enter(string scene) => GameManager.instance.BeginSceneTransition(new GameManager.SceneLoadInfo {
        SceneName = scene, EntryGateName = "door_dreamEnter", Visualization = GameManager.SceneLoadVisualizations.Default, WaitForSceneTransitionCameraFade = true
    });
    private void SavedBoardChecks() {
        var type = typeof(ModHooks).Assembly.GetType("Modding.ModSavegameData");
        var data = Activator.CreateInstance(type, true);
        typeof(ModHooks).GetMethod("OnSaveLocalSettings", BindingFlags.Static | BindingFlags.NonPublic).Invoke(null, new[] { data });
        var json = JsonConvert.SerializeObject(data);
        var path = Path.Combine(root, role + "-test-modded.json"); File.WriteAllText(path, json);
        var expected = CoopMod.Statistics.Copy();
        CoopMod.Instance.OnLoadLocal(null); Require(CoopMod.Statistics.Deaths.Count == 0, "empty save has an independent board");
        var restored = JsonConvert.DeserializeObject(File.ReadAllText(path), type);
        typeof(ModHooks).GetMethod("OnLoadLocalSettings", BindingFlags.Static | BindingFlags.NonPublic).Invoke(null, new[] { restored });
        Require(expected.Deaths.Count == CoopMod.Statistics.Deaths.Count && expected.Deaths.All(p => CoopMod.Statistics.Get(p.Key) == p.Value),
            "actual Modding local-save callbacks preserve names and totals in separate JSON");
    }
    private IEnumerator PreviousRescue() {
        foreach (var victim in new[] { "guest", "host" }) {
            var h = HeroController.instance;
            h.takeNoDamage = true;
            var source = h.transform.position;
            h.GetComponent<Rigidbody2D>().velocity = Vector2.zero;
            Flag(role + ".rescue-" + victim + "-ready");
            yield return Until(() => Has((host ? "guest" : "host") + ".rescue-" + victim + "-ready"), "both ready for preceding rescue " + victim);
            if (role == victim) {
                PlayerData.instance.SetInt("health", 0);
                h.StartCoroutine((IEnumerator)typeof(HeroController).GetMethod("Die", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(h, null));
                yield return Until(() => runtime.Down, "preceding boss down " + victim);
                yield return Until(() => !runtime.Down && !runtime.Returning && PlayerData.instance.health == 1, "preceding boss rescued " + victim);
            } else {
                yield return Until(() => runtime.Peers.Values.Any(p => p.Life == Life.Down), "preceding teammate down received " + victim);
                var peer = runtime.Peers.Single(); var pos = peer.Value.State;
                h.transform.position = new Vector3(pos.X - 1.5f, pos.Y, source.z);
                h.GetComponent<Rigidbody2D>().velocity = Vector2.zero;
                yield return new WaitForSecondsRealtime(.25f);
                Require(runtime.RequestShadeHit(peer.Key, peer.Value.DownSerial), "preceding rescue request " + victim);
                yield return Until(() => runtime.Peers[peer.Key].Life == Life.Alive, "preceding rescue acknowledged " + victim);
            }
            Flag(role + ".rescued-" + victim);
            yield return Until(() => Has((host ? "guest" : "host") + ".rescued-" + victim), "both preceding rescue complete " + victim);
        }
        yield return Until(() => CoopMod.Statistics.Get("p5guest") == 5 && CoopMod.Statistics.Get("p5host") == 8,
            "both actual downs counted once across rescue on both saved boards");
    }
    private IEnumerator RestExit(string scene, string destination) {
        yield return Until(() => In(scene), "native intermediate entry " + scene);
        yield return Until(() => BossSceneController.Instance != null && BossSceneController.Instance.customExitPoint != null, "native custom exit " + scene);
        var gate = BossSceneController.Instance.customExitPoint;
        Require(gate.targetScene == destination, "native exit targets " + destination);
        Log("EXIT " + scene + " index=" + BossSequenceController.BossIndex + " target=" + gate.targetScene + " pos=" + gate.transform.position);
        yield return new WaitForSecondsRealtime(1);
        var h = HeroController.instance; h.takeNoDamage = true;
        h.transform.position = gate.GetComponent<Collider2D>().bounds.center;
        h.GetComponent<Rigidbody2D>().velocity = Vector2.zero; Physics2D.SyncTransforms();
        yield return Until(() => In(destination), "native custom exit reaches " + destination);
    }
    private IEnumerator RehostBoardCheck() {
        engine.ClientManager.Disconnect(); Flag(role + ".rehost-offline");
        yield return Until(() => Has((host ? "guest" : "host") + ".rehost-offline"), "both disconnected before rehosting saved board");
        if (host) {
            yield return Until(() => engine.ServerManager.Players.Count == 0, "old room empty");
            engine.ServerManager.Stop(); yield return null;
            engine.ServerManager.Start(29650, false); Flag("rehost-started");
        } else yield return Until(() => Has("rehost-started"), "fresh room server started");
        typeof(Hkmp.Game.Client.ClientManager).GetField("_autoConnect", BindingFlags.Instance | BindingFlags.NonPublic).SetValue(engine.ClientManager, true);
        typeof(Hkmp.Game.Client.ClientManager).GetMethod("Connect", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(engine.ClientManager, new object[] { "127.0.0.1", 29650, "p5" + role });
        yield return Until(() => runtime.Connected && runtime.Peers.Count == 1, "both clients joined freshly hosted room");
        Require(CoopMod.Statistics.Get("p5host") == 8 && CoopMod.Statistics.Get("p5guest") == 5 && CoopMod.Statistics.Get("离线队友") == 2,
            "saved death board survives full disconnect and a new room without reset or duplicate addition");
    }
    private IEnumerator Start() {
        root = Environment.GetEnvironmentVariable("HOLLOWCONNECT_SMOKE_ROOT"); role = Environment.GetEnvironmentVariable("HOLLOWCONNECT_SMOKE_ROLE"); host = role == "host";
        var stack = new Stack<IEnumerator>(); stack.Push(Run());
        while (stack.Count != 0) {
            object current = null; bool more;
            try { more = stack.Peek().MoveNext(); if (more) current = stack.Peek().Current; }
            catch (Exception e) { Log("FAIL " + e); File.WriteAllText(Path.Combine(root, role + ".failed"), e.ToString()); yield break; }
            if (!more) { stack.Pop(); continue; }
            if (current is IEnumerator nested) stack.Push(nested); else yield return current;
        }
    }
    private IEnumerator Run() {
        yield return Until(() => Object.FindObjectOfType<CoopRuntime>() != null && GameManager.instance != null && Scene == "Menu_Title", "menu ready");
        runtime = Object.FindObjectOfType<CoopRuntime>(); engine = Field<Hkmp.Game.GameManager>(ModHooks.GetMod("HKMP"), "_gameManager");
        manager = Field<EntityManager>(engine.ClientManager, "_entityManager");
        Field<Hkmp.Game.Settings.ModSettings>(engine.ClientManager, "_modSettings").AuthKey = Hkmp.Util.AuthUtil.GenerateAuthKey();
        var pd = PlayerData.instance; pd.SetString("respawnScene", "Town"); pd.SetString("respawnMarkerName", "RestBench"); pd.SetInt("respawnType", 1);
        GameManager.instance.profileID = 99; GameManager.instance.StartCoroutine(GameManager.instance.RunContinueGame());
        yield return Until(() => In("Town"), "unsaved Town entry");
        if (pd.GetBool("atBench")) {
            var bench = Object.FindObjectsOfType<PlayMakerFSM>().Single(f => f.name == "RestBench" && f.FsmName == "Bench Control");
            yield return Until(() => bench.ActiveStateName == "Resting", "initial native bench ready");
            var jump = bench.Fsm.ActiveState.Actions.OfType<HutongGames.PlayMaker.Actions.ListenForJump>().Single(); bench.Fsm.Event(jump.wasPressed);
            yield return Until(() => !pd.GetBool("atBench") && HeroController.instance.acceptingInput, "initial native get-up");
        }
        CoopMod.Instance.OnLoadLocal(new DeathStatistics());
        CoopMod.Statistics.Merge("p5host", 7); CoopMod.Statistics.Merge("p5guest", 4);
        if (!host) CoopMod.Statistics.Merge("离线队友", 2);
        SavedBoardChecks();
        if (host) { CoopMod.Settings.Rescue = true; engine.ServerManager.Start(29650, false); Flag("server.started"); }
        else yield return Until(() => Has("server.started"), "server ready");
        typeof(Hkmp.Game.Client.ClientManager).GetField("_autoConnect", BindingFlags.Instance | BindingFlags.NonPublic).SetValue(engine.ClientManager, true);
        typeof(Hkmp.Game.Client.ClientManager).GetMethod("Connect", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(engine.ClientManager, new object[] { "127.0.0.1", 29650, "p5" + role });
        yield return Until(() => runtime.Connected, "handshake"); Flag(role + ".connected");
        yield return Until(() => Has((host ? "guest" : "host") + ".connected"), "both connected");
        yield return Until(() => CoopMod.Statistics.Get("离线队友") == 2 && CoopMod.Statistics.Get("p5host") == 7 && CoopMod.Statistics.Get("p5guest") == 4,
            "saved histories merge over UDP without doubling copied totals");
        var withRescue = Environment.GetEnvironmentVariable("HOLLOWCONNECT_P5_RESCUE_CHECK") == "1";
        var sequence = Resources.Load<BossSequence>("GG/Boss Sequence Tier 5");
        Require(sequence != null, "native fifth sequence asset");
        var index = Enumerable.Range(0, sequence.Count).First(i => sequence.GetSceneAt(i) == "GG_Watcher_Knights");
        Log("SEQUENCE " + string.Join(",", Enumerable.Range(0, sequence.Count).Select(i => i + "/" + sequence.GetSceneAt(i) + "/load=" + sequence.CanLoad(i))));
        BossSequenceController.SetupNewSequence(sequence, BossSequenceController.ChallengeBindings.None, "bossDoorStateTier5");
        typeof(BossSequenceController).GetField("bossIndex", BindingFlags.Static | BindingFlags.NonPublic).SetValue(null, withRescue ? index - 3 : index);
        On.BossSceneController.EndBossScene += (orig, self) => {
            Log("END BEFORE scene=" + Scene + " index=" + BossSequenceController.BossIndex + " sequence=" + BossSequenceController.IsInSequence + " context=" + BossTravel.CaptureContext());
            orig(self);
            Log("END AFTER scene=" + Scene + " index=" + BossSequenceController.BossIndex + " sequence=" + BossSequenceController.IsInSequence);
        };
        if (!host && !withRescue) yield return Until(() => Has("host.watchers"), "late entry after authority");
        if (withRescue) {
            if(!host)yield return new WaitForSecondsRealtime(.5f);
            Enter("GG_Grimm"); yield return Until(() => In("GG_Grimm") && manager.IsSceneHostDetermined, "preceding Grimm arena");
            HeroController.instance.takeNoDamage = true; Flag(role + ".grimm");
            yield return Until(() => Has((host ? "guest" : "host") + ".grimm") && runtime.Peers.Count == 1, "both preceding Grimm arenas");
            yield return PreviousRescue();
            var endGrimm = Time.realtimeSinceStartup + 60;
            while (Scene == "GG_Grimm" && Time.realtimeSinceStartup < endGrimm) {
                if (manager.IsSceneHost) foreach (var health in Object.FindObjectsOfType<HealthManager>().Where(h => h.name.StartsWith("Grimm Boss") && !h.IsInvincible && h.hp > 0)) health.ApplyExtraDamage(9999);
                yield return new WaitForSeconds(.2f);
            }
            yield return RestExit("GG_Spa", "GG_Unn");
            yield return RestExit("GG_Unn", "GG_Watcher_Knights");
        } else Enter("GG_Watcher_Knights");
        yield return Until(() => In("GG_Watcher_Knights") && manager.IsSceneHostDetermined, "Watcher arena authority ready");
        HeroController.instance.takeNoDamage = true;
        Log("ARENA index=" + BossSequenceController.BossIndex + " authority=" + manager.IsSceneHost + " context=" + BossTravel.CaptureContext());
        Flag(role + ".watchers"); yield return Until(() => Has((host ? "guest" : "host") + ".watchers"), "both Watcher arenas");
        if(Environment.GetEnvironmentVariable("HOLLOWCONNECT_VISUAL_CHECKS")=="1")yield return ReplicaVisualChecks.Run(Log);
        var deadline = Time.realtimeSinceStartup + 95;
        var killed = new HashSet<int>();
        while (Scene == "GG_Watcher_Knights" && Time.realtimeSinceStartup < deadline) {
            HeroController.instance.takeNoDamage = true;
            if (manager.IsSceneHost) foreach (var health in Object.FindObjectsOfType<HealthManager>().Where(h => h.name.StartsWith("Black Knight") && h.hp > 0 && !h.isDead && !h.IsInvincible)) {
                killed.Add(health.GetInstanceID()); Log("NATIVE LETHAL " + health.name + " hp=" + health.hp); health.ApplyExtraDamage(9999);
            }
            yield return new WaitForSeconds(.2f);
        }
        Log("AFTER scene=" + Scene + " index=" + BossSequenceController.BossIndex + " inSequence=" + BossSequenceController.IsInSequence + " killed=" + killed.Count);
        yield return Until(() => In("GG_Uumuu_V"), "fifth sequence continues into native Uumuu arena", 30);
        Require(BossSequenceController.IsInSequence && !BossSequenceController.WasCompleted, "fifth sequence retained after Watchers");
        SavedBoardChecks();
        if(withRescue)yield return RehostBoardCheck();
        yield return ProbeShutdown.Run(engine, root, role);
        Log("FINAL PASS native fifth sequence Watchers -> Uumuu; late entry / preceding rescue as configured; saved board; muted; no native saves"); Flag(role + ".done");
    }
}
