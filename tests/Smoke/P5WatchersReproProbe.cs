using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using Hkmp.Game.Client.Entity;
using HollowConnect;
using HollowConnect.Core;
using Modding;
using Newtonsoft.Json;
using UnityEngine;
using UnityEngine.SceneManagement;
using Object = UnityEngine.Object;

namespace HollowConnect.Smoke;

// Reproduce on a pinned release without changing its behavior. Damage is injected
// through HealthManager; all deaths, custom exits and completion remain native.
public sealed partial class P5WatchersReproProbe : MonoBehaviour {
    private string root, role, peer, first, previousFirst, startScene;
    private int delayMs, deaths;
    private CoopRuntime runtime;
    private Hkmp.Game.GameManager engine;
    private EntityManager manager;
    private string lastState;
    private float nextTrace;
    private readonly List<MonoMod.RuntimeDetour.Hook> traces = new();
    private static string Scene => UnityEngine.SceneManagement.SceneManager.GetActiveScene().name;
    private const BindingFlags InstanceFlags = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;
    private const BindingFlags StaticFlags = BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public;
    private static T Field<T>(object obj, string name) => (T)obj.GetType().GetField(name, InstanceFlags).GetValue(obj);
    private void Log(string value) => File.AppendAllText(Path.Combine(root, role + ".log"), DateTime.UtcNow.ToString("O") + " " + value + "\n");
    private bool Has(string name) => File.Exists(Path.Combine(root, name));
    private void Flag(string name, string value = "ready") {
        var path = Path.Combine(root, name);
        if (File.Exists(path)) return;
        var staged = path + "." + role + ".tmp";
        File.WriteAllText(staged, value); File.Move(staged, path);
    }
    private void Require(bool ok, string label) { if (!ok) throw new Exception(label); Log("PASS " + label); }
    private IEnumerator Until(Func<bool> ready, string label, float seconds = 60) {
        var end = Time.realtimeSinceStartup + seconds;
        while (!ready() && Time.realtimeSinceStartup < end) yield return null;
        Require(ready(), label);
    }
    private bool In(string scene) => Scene == scene && runtime.Playable && !GameManager.instance.IsInSceneTransition;
    private void Enter(string scene) => GameManager.instance.BeginSceneTransition(new GameManager.SceneLoadInfo {
        SceneName = scene, EntryGateName = scene == "GG_Atrium_Roof" ? "bot1" : "door_dreamEnter", Visualization = GameManager.SceneLoadVisualizations.Default,
        WaitForSceneTransitionCameraFade = true
    });
    private static string Callbacks(BossSceneController controller) {
        var value = controller == null ? null : typeof(BossSceneController).GetField("OnBossSceneComplete", InstanceFlags).GetValue(controller) as Delegate;
        return value == null ? "none" : string.Join(",", value.GetInvocationList().Select(d => d.Method.DeclaringType?.FullName + ":" + d.Method.Name));
    }
    private string State() {
        var c = BossSceneController.Instance;
        var next = c == null ? null : c.GetComponents<PlayMakerFSM>().FirstOrDefault(f => f.FsmName == "Dream Enter Next Scene");
        var h = HeroController.instance;
        return "scene=" + Scene + " visit=" + Hkmp.CoopContext.LocalVisit + " epoch=" + Hkmp.CoopContext.Epoch +
            " authority=" + (manager != null && manager.IsSceneHost) + " determined=" + (manager != null && manager.IsSceneHostDetermined) +
            " sequence=" + BossTravel.CurrentSequenceName + " index=" + BossSequenceController.BossIndex +
            " guard=" + BossTravel.HasTransientContext + " setup=" + (BossSceneController.SetupEvent != null) +
            " controller=" + (c == null ? "none" : c.GetInstanceID().ToString()) +
            " ended=" + (c != null && Field<bool>(c, "endedScene")) + " callbacks=" + Callbacks(c) +
            " target=" + next?.FsmVariables.FindFsmString("To Scene")?.Value +
            " input=" + (h != null && h.acceptingInput) + " relinquished=" + (h != null && h.controlReqlinquished) +
            " transitioning=" + (h != null && h.cState.transitioning) + " dead=" + (h != null && h.cState.dead) +
            " down=" + (runtime != null && runtime.Down) + " deaths=" + deaths;
    }
    private void Update() {
        if (runtime == null) return;
        if (HeroController.instance != null) HeroController.instance.takeNoDamage = true;
        if (Time.realtimeSinceStartup < nextTrace) return;
        nextTrace = Time.realtimeSinceStartup + .1f;
        var state = State();
        if (state != lastState) { Log("STATE " + state); lastState = state; }
        if (Scene == "GG_Watcher_Knights") Flag(role + ".watcher-active", DateTime.UtcNow.ToString("O"));
        if (Has(peer + ".failed") && !Has(role + ".failed")) DumpFailure(new Exception("Peer failed; capture the other endpoint before shutdown"));
    }
    private void InstallTraces() {
        On.BossSceneController.EndBossScene += EndTrace;
        On.GameManager.BeginSceneTransitionRoutine += LoadTrace;
        On.HeroController.Die += DeathTrace;
        traces.Add(new MonoMod.RuntimeDetour.Hook(typeof(BossTravel).GetMethod("ApplyContext", StaticFlags),
            (Func<Func<string, bool>, string, bool>)((orig, context) => {
                var m = JsonConvert.DeserializeObject<Message>(context);
                Log("APPLY BEFORE remoteScene=" + m.Scene + " remoteSequence=" + m.BossSequence + " remoteIndex=" + m.BossIndex + " " + State());
                var result = orig(context); Log("APPLY AFTER result=" + result + " " + State()); return result;
            })));
        traces.Add(new MonoMod.RuntimeDetour.Hook(typeof(BossTravel).GetMethod("RestoreTransientContext", StaticFlags),
            (Action<Action>)(orig => {
                var had = BossTravel.HasTransientContext;
                if (had) Log("RESTORE BEFORE " + State());
                orig();
                if (had) Log("RESTORE AFTER " + State());
            })));
    }
    private void EndTrace(On.BossSceneController.orig_EndBossScene orig, BossSceneController self) {
        var context = BossTravel.CaptureContext();
        Log("END BEFORE " + State() + " context=" + context);
        orig(self); Log("END AFTER " + State());
        if (ContextChecksEnabled && self.gameObject.scene.name == "GG_Watcher_Knights" && Field<bool>(self, "endedScene") && completionChecks.Add(self.GetInstanceID()))
            StartCoroutine(CheckCompletedContext(context));
    }
    private IEnumerator LoadTrace(On.GameManager.orig_BeginSceneTransitionRoutine orig, GameManager gm, GameManager.SceneLoadInfo info) {
        Log("NATIVE LOAD target=" + info.SceneName + " gate=" + info.EntryGateName + " " + State());
        return orig(gm, info);
    }
    private IEnumerator DeathTrace(On.HeroController.orig_Die orig, HeroController hero) { deaths++; return orig(hero); }
    private void OnDestroy() {
        On.BossSceneController.EndBossScene -= EndTrace;
        On.GameManager.BeginSceneTransitionRoutine -= LoadTrace;
        On.HeroController.Die -= DeathTrace;
        foreach (var trace in traces) trace.Dispose();
    }
    private void DumpFailure(Exception error) {
        Log("FAIL " + error + " " + State());
        var c = BossSceneController.Instance;
        var snapshot = new {
            error = error.ToString(), state = State(), context = BossTravel.CaptureContext(),
            controllerWait = c == null ? null : new {
                c.CanTransition, isTransitioning = BossSceneController.IsTransitioning, c.DreamReturnEvent,
                bossesLeft = Field<int>(c, "bossesLeft"), ended = Field<bool>(c, "endedScene"),
                fsms = c.GetComponents<PlayMakerFSM>().Select(f => new { f.FsmName, f.ActiveStateName,
                    strings = f.FsmVariables.StringVariables.Select(v => new { v.Name, v.Value }).ToArray() }).ToArray()
            },
            health = Object.FindObjectsOfType<HealthManager>(true).Where(h => h.name.StartsWith("Black Knight")).Select(h => new { h.name, h.hp, h.isDead, h.IsInvincible, active = h.gameObject.activeInHierarchy }).ToArray()
        };
        File.WriteAllText(Path.Combine(root, role + ".failure-state.json"), JsonConvert.SerializeObject(snapshot, Formatting.Indented));
        Flag(role + ".failed", error.ToString());
    }
    private IEnumerator Start() {
        root = Environment.GetEnvironmentVariable("HOLLOWCONNECT_SMOKE_ROOT"); role = Environment.GetEnvironmentVariable("HOLLOWCONNECT_SMOKE_ROLE"); peer = role == "host" ? "guest" : "host";
        first = Environment.GetEnvironmentVariable("HOLLOWCONNECT_P5_REPRO_FIRST") ?? "guest";
        previousFirst = Environment.GetEnvironmentVariable("HOLLOWCONNECT_P5_REPRO_PREVIOUS_FIRST") ?? first;
        startScene = Environment.GetEnvironmentVariable("HOLLOWCONNECT_P5_REPRO_START") ?? "GG_Watcher_Knights";
        delayMs = int.Parse(Environment.GetEnvironmentVariable("HOLLOWCONNECT_P5_REPRO_DELAY_MS") ?? "500", CultureInfo.InvariantCulture);
        Log("START release reproduction first=" + first + " previousFirst=" + previousFirst + " start=" + startScene + " delayMs=" + delayMs + " no deaths/rescue/F8; native health deaths and exits");
        var stack = new Stack<IEnumerator>(); stack.Push(Run());
        while (stack.Count != 0) {
            object current = null; bool more;
            try { more = stack.Peek().MoveNext(); if (more) current = stack.Peek().Current; }
            catch (Exception e) { DumpFailure(e); yield break; }
            if (!more) { stack.Pop(); continue; }
            if (current is IEnumerator nested) stack.Push(nested); else yield return current;
        }
    }
    private IEnumerator Schedule(string label) {
        Flag(role + ".ready-" + label);
        yield return Until(() => Has(peer + ".ready-" + label), "both scheduled " + label);
        if (role == "host") Flag("schedule-" + label, DateTime.UtcNow.AddSeconds(2).ToString("O"));
        yield return Until(() => Has("schedule-" + label), "schedule published " + label);
        var time = DateTime.Parse(File.ReadAllText(Path.Combine(root, "schedule-" + label)), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
        var scheduledFirst = label == "exit-GG_Unn" || label == "initial" && startScene == "GG_Watcher_Knights" ? first : previousFirst;
        if (role != scheduledFirst) time = time.AddMilliseconds(delayMs);
        while (DateTime.UtcNow < time) yield return null;
        Log("SCHEDULE FIRE " + label + " " + State());
    }
    private IEnumerator RestExit(string scene, string target) {
        yield return Until(() => In(scene) && BossSceneController.Instance != null && BossSceneController.Instance.customExitPoint != null, "native rest room " + scene);
        var exit = BossSceneController.Instance.customExitPoint;
        Require(exit.targetScene == target, "native custom exit targets " + target);
        yield return Schedule("exit-" + scene);
        var hero = HeroController.instance;
        hero.transform.position = exit.GetComponent<Collider2D>().bounds.center;
        hero.GetComponent<Rigidbody2D>().velocity = Vector2.zero; Physics2D.SyncTransforms();
        yield return Until(() => In(target), "native exit reaches " + target);
    }
    private IEnumerator NativeReturnContext(BossSequence sequence) {
        yield return Schedule("native-door"); Enter("GG_Atrium_Roof");
        yield return Until(() => Scene == "GG_Atrium_Roof", "native fifth-door scene loaded");
        Log("ROOF GATES " + string.Join(",", Object.FindObjectsOfType<TransitionPoint>(true).Select(g => g.name + "/active=" + g.gameObject.activeInHierarchy)));
        yield return Until(() => In("GG_Atrium_Roof"), "native fifth-door roof");
        yield return new WaitForSecondsRealtime(1);
        var door = Object.FindObjectsOfType<BossSequenceDoor>(true).Single(d => d.bossSequence != null && d.bossSequence.name == sequence.name);
        var fsm = door.challengeFSM;
        var menu = fsm.FsmStates.Single(s => s.Actions.Any(a => a is ShowBossDoorChallengeUI));
        for (var t = fsm.transform; t != null; t = t.parent) t.gameObject.SetActive(true);
        HeroController.instance.transform.position = new Vector3(door.transform.position.x, HeroController.instance.transform.position.y, 0);
        fsm.enabled = true; yield return null;
        EntityInitializer.InitializeFsm(fsm); fsm.Fsm.Start(); fsm.SetState(menu.Name);
        yield return Until(() => Object.FindObjectOfType<BossDoorChallengeUI>() != null, "native fifth-door menu");
        yield return new WaitForSecondsRealtime(1);
        var ui = (BossDoorChallengeUI)typeof(ShowBossDoorChallengeUI).GetField("spawnedUI", StaticFlags).GetValue(null);
        Log("NATIVE FIFTH DOOR name=" + door.name + " returnGate=" + door.dreamReturnGate.name);
        ui.Begin();
        var deadline = Time.realtimeSinceStartup + 75;
        while (!In(sequence.GetSceneAt(0)) && Time.realtimeSinceStartup < deadline) {
            foreach (var box in Object.FindObjectsOfType<DialogueBox>()) {
                if (string.IsNullOrEmpty(box.currentConversation) || box.currentPage <= 0) continue;
                box.useTypeWriter = false; box.SpeedupTypewriter();
                var pages = box.GetComponents<PlayMakerFSM>().FirstOrDefault(f => f.FsmName == "Dialogue Page Control");
                if (pages != null && (pages.ActiveStateName == "Page End" || pages.ActiveStateName == "Conversation End")) pages.SendEvent("NEXT");
            }
            yield return new WaitForSecondsRealtime(.2f);
        }
        Require(In(sequence.GetSceneAt(0)), "native fifth-door cinematic reaches first boss");
        Log("NATIVE RETURN CONTEXT scene=" + PlayerData.instance.GetString("dreamReturnScene") + " gate=" + PlayerData.instance.GetString("bossReturnEntryGate"));
        Require(PlayerData.instance.GetString("dreamReturnScene") == "GG_Atrium_Roof", "native fifth-door return points to roof");
    }
    private IEnumerator KillBoss(string scene, string prefix) {
        yield return Until(() => In(scene) && manager.IsSceneHostDetermined, "arena ready " + scene);
        Flag(role + ".ready-kill-" + scene);
        yield return Until(() => Has(peer + ".ready-kill-" + scene), "both combatants in " + scene);
        Log("ARENA " + State() + " context=" + BossTravel.CaptureContext());
        if (ContextChecksEnabled && scene == "GG_Watcher_Knights") CheckLiveContext();
        yield return new WaitForSecondsRealtime(6);
        var deadline = Time.realtimeSinceStartup + 100;
        var nextHit = Time.realtimeSinceStartup;
        var killed = new HashSet<int>();
        while (Scene == scene && Time.realtimeSinceStartup < deadline) {
            if (manager.IsSceneHost && Time.realtimeSinceStartup >= nextHit) {
                foreach (var health in Object.FindObjectsOfType<HealthManager>().Where(h => h.name.StartsWith(prefix) && h.hp > 0 && !h.isDead && !h.IsInvincible)) {
                    killed.Add(health.GetInstanceID()); Log("NATIVE LETHAL " + health.name + " hp=" + health.hp); health.ApplyExtraDamage(9999);
                }
                nextHit = Time.realtimeSinceStartup + 2;
            }
            yield return null;
        }
        Log("AFTER COMBAT killed=" + killed.Count + " " + State());
        Require(Scene != scene, "native combat exits " + scene);
    }
    private IEnumerator Run() {
        yield return Until(() => Object.FindObjectOfType<CoopRuntime>() != null && GameManager.instance != null && Scene == "Menu_Title", "menu ready");
        runtime = Object.FindObjectOfType<CoopRuntime>(); engine = Field<Hkmp.Game.GameManager>(ModHooks.GetMod("HKMP"), "_gameManager");
        manager = Field<EntityManager>(engine.ClientManager, "_entityManager");
        Field<Hkmp.Game.Settings.ModSettings>(engine.ClientManager, "_modSettings").AuthKey = Hkmp.Util.AuthUtil.GenerateAuthKey();
        runtime.KeyPressed = _ => false;
        var pd = PlayerData.instance; pd.SetString("respawnScene", "Town"); pd.SetString("respawnMarkerName", "RestBench"); pd.SetInt("respawnType", 1);
        GameManager.instance.profileID = 99; GameManager.instance.StartCoroutine(GameManager.instance.RunContinueGame());
        yield return Until(() => In("Town"), "unsaved Town entry");
        if (pd.GetBool("atBench")) {
            var bench = Object.FindObjectsOfType<PlayMakerFSM>().Single(f => f.name == "RestBench" && f.FsmName == "Bench Control");
            yield return Until(() => bench.ActiveStateName == "Resting", "initial bench resting");
            bench.Fsm.Event(bench.Fsm.ActiveState.Actions.OfType<HutongGames.PlayMaker.Actions.ListenForJump>().Single().wasPressed);
            yield return Until(() => !pd.GetBool("atBench") && HeroController.instance.acceptingInput, "native bench get-up");
        }
        CoopMod.Instance.OnLoadLocal(new DeathStatistics());
        CoopMod.Settings.ShareProgress = true; CoopMod.Settings.Rescue = true; CoopMod.Settings.BossVictoryRescue = true;
        if (role == "host") { engine.ServerManager.Start(29650, false); Flag("server.started"); }
        else yield return Until(() => Has("server.started"), "server ready");
        typeof(Hkmp.Game.Client.ClientManager).GetField("_autoConnect", InstanceFlags).SetValue(engine.ClientManager, true);
        typeof(Hkmp.Game.Client.ClientManager).GetMethod("Connect", InstanceFlags).Invoke(engine.ClientManager, new object[] { "127.0.0.1", 29650, "p5" + role });
        yield return Until(() => runtime.Connected && runtime.Peers.Count == 1, "both connected");
        InstallTraces();
        var sequence = Resources.Load<BossSequence>("GG/Boss Sequence Tier 5");
        Require(sequence != null, "native fifth sequence asset");
        if (Environment.GetEnvironmentVariable("HOLLOWCONNECT_P5_REPRO_NATIVE_RETURN") == "1") yield return NativeReturnContext(sequence);
        var index = Enumerable.Range(0, sequence.Count).First(i => sequence.GetSceneAt(i) == startScene);
        Log("SEQUENCE " + string.Join(",", Enumerable.Range(0, sequence.Count).Select(i => i + "/" + sequence.GetSceneAt(i) + "/load=" + sequence.CanLoad(i))));
        BossSequenceController.SetupNewSequence(sequence, BossSequenceController.ChallengeBindings.None, "bossDoorStateTier5");
        typeof(BossSequenceController).GetField("bossIndex", StaticFlags).SetValue(null, index);
        yield return Schedule("initial"); Enter(startScene);
        if (startScene == "GG_Grimm") { yield return KillBoss("GG_Grimm", "Grimm Boss"); yield return RestExit("GG_Spa", "GG_Unn"); }
        if (startScene != "GG_Watcher_Knights") yield return RestExit("GG_Unn", "GG_Watcher_Knights");
        yield return KillBoss("GG_Watcher_Knights", "Black Knight");
        yield return Until(() => In("GG_Uumuu_V") && HeroController.instance.acceptingInput && !HeroController.instance.controlReqlinquished,
            "native Uumuu entry restores control", 25);
        Require(BossSequenceController.IsInSequence && BossSequenceController.BossIndex == 32 && !BossSequenceController.WasCompleted,
            "fifth sequence remains at Uumuu index 32");
        Require(deaths == 0 && !runtime.Down, "no death or downed event");
        if (ContextChecksEnabled) Require(Has(role + ".completion-checked"), "completion context guard checked before next room");
        Flag(role + ".observed", State());
        yield return ProbeShutdown.Run(engine, root, role);
        Log("FINAL PASS native fifth sequence Watchers -> Uumuu; " + State()); Flag(role + ".done");
    }
}
