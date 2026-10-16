using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using HutongGames.PlayMaker;
using HollowConnect;
using Modding;
using UnityEngine;
using Object = UnityEngine.Object;

namespace HollowConnect.Smoke;

// Disposable in-memory characters only; ProbeMod intercepts all SaveGame overloads.
public sealed class ColosseumProbe : MonoBehaviour {
    private string root, role;
    private bool host;
    private CoopRuntime runtime;
    private Hkmp.Game.GameManager engine;
    private string trial, testCase, first;
    private readonly object logLock = new();
    private static string ActiveScene => UnityEngine.SceneManagement.SceneManager.GetActiveScene().name;
    private static T Field<T>(object obj, string key) => (T)obj.GetType().GetField(key, BindingFlags.Instance | BindingFlags.NonPublic).GetValue(obj);
    private void Log(string text) { lock (logLock) File.AppendAllText(Path.Combine(root, role + ".log"), DateTime.UtcNow.ToString("O") + " " + text + "\n"); }
    private void Flag(string name) => File.WriteAllText(Path.Combine(root, name), "ready");
    private bool Has(string name) => File.Exists(Path.Combine(root, name));
    private void Require(bool ok, string name) { if (!ok) throw new Exception(name); Log("PASS " + name); }
    private IEnumerator Until(Func<bool> ready, string name, float seconds = 50) {
        var end = Time.realtimeSinceStartup + seconds;
        while (!ready() && Time.realtimeSinceStartup < end) yield return null;
        if (!ready() && HeroController.instance != null) {
            Log("TIMEOUT " + name + " scene=" + ActiveScene + " hero=" + HeroController.instance.transform.position + " input=" + HeroController.instance.acceptingInput);
            foreach (var fsm in Object.FindObjectsOfType<PlayMakerFSM>().Where(f => f.name == "RestBench"))
                Log("TIMEOUT BENCH " + fsm.FsmName + "=" + fsm.ActiveStateName);
        }
        Require(ready(), name);
    }
    private bool In(string scene) => ActiveScene == scene && HeroController.instance != null && HeroController.instance.isHeroInPosition &&
        !GameManager.instance.IsInSceneTransition && HeroController.instance.acceptingInput && !HeroController.instance.cState.transitioning;
    private void Enter(string scene, string gate) => GameManager.instance.BeginSceneTransition(new GameManager.SceneLoadInfo {
        SceneName = scene, EntryGateName = gate, Visualization = GameManager.SceneLoadVisualizations.Default, WaitForSceneTransitionCameraFade = true
    });
    private string Describe(object value) {
        if (value == null) return "null";
        if (value is FsmEvent evt) return evt.Name;
        if (value is FunctionCall call) return call.FunctionName + "(" + call.ParameterType + ")";
        if (value is FsmOwnerDefault owner) return owner.OwnerOption + ":" + Describe(owner.GameObject);
        if (value is FsmEventTarget target) return target.target + ":" + Describe(target.gameObject) + ":" + target.fsmName;
        if (value is FsmGameObject go) return Describe(go.Value);
        if (value is FsmObject fo) return Describe(fo.Value);
        if (value is UnityEngine.Object obj) return obj == null ? "destroyed" : obj.GetType().Name + ":" + obj.name;
        if (value is Array array) return "[" + string.Join(",", array.Cast<object>().Select(Describe)) + "]";
        return value.ToString();
    }
    private void Dump(string label) {
        var lines = new List<string> { "SCENE " + ActiveScene + " " + label + " checkpoint=" + PlayerData.instance.GetString("respawnScene") + "/" + PlayerData.instance.GetString("respawnMarkerName") };
        foreach (var gate in Object.FindObjectsOfType<TransitionPoint>()) lines.Add("GATE " + gate.name + " pos=" + gate.transform.position + " target=" + gate.targetScene + "/" + gate.entryPoint);
        foreach (var fsm in Resources.FindObjectsOfTypeAll<PlayMakerFSM>().Where(f => f.gameObject.scene.name == ActiveScene &&
            (label != "trial" || f.name.StartsWith("Colosseum Manager")))) {
            lines.Add("FSM " + fsm.name + "/" + fsm.FsmName + " enabled=" + fsm.enabled + " active=" + fsm.gameObject.activeInHierarchy + " state=" + fsm.ActiveStateName + " pos=" + fsm.transform.position);
            lines.Add(" GLOBALS " + string.Join(",", fsm.Fsm.GlobalTransitions.Select(t => t.EventName + ":" + t.ToState)));
            foreach (var state in fsm.FsmStates) {
                lines.Add(" STATE " + state.Name + " transitions=" + string.Join(",", state.Transitions.Select(t => t.EventName + ":" + t.ToState)));
                foreach (var action in state.Actions) lines.Add("  ACTION " + action.GetType().Name + " " + string.Join(";", action.GetType().GetFields(BindingFlags.Instance | BindingFlags.Public).Select(f => f.Name + "=" + Describe(f.GetValue(action)))));
            }
            foreach (var v in fsm.FsmVariables.StringVariables) lines.Add(" STRING " + v.Name + "=" + v.Value);
            foreach (var v in fsm.FsmVariables.BoolVariables) lines.Add(" BOOL " + v.Name + "=" + v.Value);
        }
        File.WriteAllLines(Path.Combine(root, role + "-" + label + ".inspect.txt"), lines);
        Log(lines[0]);
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
        trial = Environment.GetEnvironmentVariable("HOLLOWCONNECT_COLOSSEUM_TRIAL") ?? "Bronze";
        testCase = Environment.GetEnvironmentVariable("HOLLOWCONNECT_COLOSSEUM_CASE") ?? "inspect";
        first = Environment.GetEnvironmentVariable("HOLLOWCONNECT_COLOSSEUM_FIRST") ?? "host";
        Log("colosseum normal entrance probe; muted; unsaved; actual production DLL hashes recorded");
        On.HutongGames.PlayMaker.Fsm.EnterState += (orig, fsm, state) => {
            if (fsm.GameObject != null && (fsm.GameObject.name.StartsWith("Colosseum Manager") || fsm.GameObject.name == trial + " Trial Board" ||
                testCase == "lifecycle" && fsm.GameObject.name == "RestBench"))
                Log("NATIVE STATE " + fsm.GameObject.name + "/" + fsm.Name + " -> " + state.Name);
            orig(fsm, state);
        };
        ModHooks.SetPlayerStringHook += (key, value) => {
            if (key == "respawnScene" || key == "respawnMarkerName") Log("SET " + key + "=" + value + " scene=" + ActiveScene + " stack=" + Environment.StackTrace);
            return value;
        };
        yield return Until(() => GameManager.instance != null && Object.FindObjectOfType<CoopRuntime>() != null && ActiveScene == "Menu_Title", "menu ready");
        runtime = Object.FindObjectOfType<CoopRuntime>(); engine = Field<Hkmp.Game.GameManager>(ModHooks.GetMod("HKMP"), "_gameManager");
        Field<Hkmp.Game.Settings.ModSettings>(engine.ClientManager, "_modSettings").AuthKey = Hkmp.Util.AuthUtil.GenerateAuthKey();
        CoopMod.Settings.ShareProgress = Environment.GetEnvironmentVariable("HOLLOWCONNECT_COLOSSEUM_SHARE") != "0";
        CoopMod.Settings.Rescue = true; CoopMod.Settings.EntryProtection = true;
        var pd = PlayerData.instance; var initialBench = host ? "Town" : "Crossroads_47";
        pd.SetString("respawnScene", initialBench); pd.SetString("respawnMarkerName", "RestBench"); pd.SetInt("respawnType", 1);
        pd.SetInt("geo", 10000); pd.SetBool("hasWalljump", true); pd.SetBool("hasDoubleJump", true);
        if (testCase != "inspect") {
            pd.SetBool("colosseumBronzeOpened", true);
            pd.SetBool("colosseumBronzeCompleted", trial != "Bronze");
            pd.SetBool("colosseumSilverOpened", trial != "Bronze");
            pd.SetBool("colosseumSilverCompleted", trial == "Gold");
            pd.SetBool("colosseumGoldOpened", trial == "Gold");
        }
        GameManager.instance.profileID = 99; GameManager.instance.StartCoroutine(GameManager.instance.RunContinueGame());
        yield return Until(() => ActiveScene == initialBench && HeroController.instance != null && HeroController.instance.isHeroInPosition && !GameManager.instance.IsInSceneTransition, "fresh personal bench loaded");
        if (pd.GetBool("atBench")) {
            var bench = Object.FindObjectsOfType<PlayMakerFSM>().Single(f => f.name == "RestBench" && f.FsmName == "Bench Control");
            yield return Until(() => bench.ActiveStateName == "Resting", "initial bench resting");
            yield return Press(InputHandler.Instance.inputActions.jump);
        }
        yield return Until(() => In(initialBench), "initial bench get up");
        HeroController.instance.takeNoDamage = true;
        if (testCase != "native") {
        if (host) { engine.ServerManager.Start(29650, false); Flag("server.started"); }
        else yield return Until(() => Has("server.started"), "server started");
        yield return new WaitForSecondsRealtime(1);
        yield return Connect(); Flag(role + ".connected");
        yield return Until(() => Has((host ? "guest" : "host") + ".connected"), "both connected");
        } else Log("CONTROL disconnected native entrance; co-op inactive");
        if (testCase.StartsWith("bounds")) {
            yield return FsmContinuationChecks.SnapshotBounds(Log, testCase == "bounds-baseline");
            if (testCase != "bounds-baseline") yield return FsmContinuationChecks.StringSnapshots(Log);
            yield return ProbeShutdown.Run(engine, root, role);
            Log("FINAL PASS isolated FSM bounds " + testCase); Flag(role + ".done"); yield break;
        }
        Enter("Room_Colosseum_02", "top1"); yield return Until(() => In("Room_Colosseum_02"), "rest area entered");
        yield return new WaitForSecondsRealtime(2); Dump("rest");
        if (testCase == "bench") {
            var rest = Object.FindObjectsOfType<PlayMakerFSM>().Single(f => f.name == "RestBench" && f.FsmName == "Bench Control");
            HeroController.instance.transform.position = rest.transform.position + new Vector3(-1, 1.5f, 0);
            HeroController.instance.GetComponent<Rigidbody2D>().velocity = Vector2.zero;
            yield return Until(() => rest.ActiveStateName == "In Range" && HeroController.instance.cState.onGround, "rest area bench range");
            yield return Press(InputHandler.Instance.inputActions.up);
            yield return Until(() => rest.ActiveStateName == "Resting", "rest area bench actually sat");
            Require(pd.GetString("respawnScene") == "Room_Colosseum_02", "native bench sets individual checkpoint");
            yield return Press(InputHandler.Instance.inputActions.jump);
            yield return Until(() => In("Room_Colosseum_02") && !pd.GetBool("atBench"), "rest area bench get up");
        }
        if (role != first) yield return Until(() => Has(first + ".hall"), "first player enters hall");
        Enter("Room_Colosseum_01", "bot1"); yield return Until(() => In("Room_Colosseum_01"), "registration hall entered");
        yield return new WaitForSecondsRealtime(2); Dump("registration"); Flag(role + ".hall");
        if (testCase != "inspect") yield return NormalEntry();
        if (testCase == "lifecycle") yield return CheckpointLifecycle();
        if (testCase != "native") yield return ProbeShutdown.Run(engine, root, role);
        Log("FINAL PASS colosseum " + testCase); Flag(role + ".done");
    }
    private PlayMakerFSM Dialogue => Object.FindObjectsOfType<PlayMakerFSM>(true).Single(f => f.name == "Text YN" && f.FsmName == "Dialogue Page Control");
    private IEnumerator Connect() {
        typeof(Hkmp.Game.Client.ClientManager).GetField("_autoConnect", BindingFlags.Instance | BindingFlags.NonPublic).SetValue(engine.ClientManager, true);
        typeof(Hkmp.Game.Client.ClientManager).GetMethod("Connect", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(engine.ClientManager, new object[] { "127.0.0.1", 29650, "colosseum" + role });
        yield return Until(() => runtime.Connected, "real UDP connected");
    }
    private void RequireCheckpoint() {
        var pd = PlayerData.instance;
        Require(pd.GetString("respawnScene") == "Room_Colosseum_02" && pd.GetString("respawnMarkerName") == "RestBench" &&
            pd.GetInt("respawnType") == 1 && !pd.GetBool("respawnFacingRight"), "each entrant has all four native colosseum checkpoint fields");
        Require(pd.mapZone == GameManager.instance.sm.mapZone, "native checkpoint map zone matches trial area");
    }
    private IEnumerator NormalEntry(string readyMarker = "arena-ready") {
        var board = Object.FindObjectsOfType<PlayMakerFSM>().Single(f => f.name == trial + " Trial Board" && f.FsmName == "npc_control");
        var conversation = board.GetComponents<PlayMakerFSM>().Single(f => f.FsmName == "Conversation Control");
        var hero = HeroController.instance;
        hero.transform.position = board.transform.position + new Vector3(-1, 2, 0);
        hero.GetComponent<Rigidbody2D>().velocity = Vector2.zero;
        yield return Until(() => board.ActiveStateName == "In Range" && hero.cState.onGround, "native trial board range and ground");
        yield return Press(InputHandler.Instance.inputActions.up);
        yield return Until(() => conversation.ActiveStateName == "Send Text" && Dialogue.ActiveStateName == "Ready for Input", "native trial registration prompt");
        var list = Dialogue.FsmVariables.GetFsmGameObject("UI List").Value.GetComponents<PlayMakerFSM>().Single(f => f.FsmName == "ui_list");
        list.FsmVariables.GetFsmInt("Current Item").Value = 1; Dialogue.SendEvent("UI SELECTION MADE");
        yield return Until(() => hero.acceptingInput && !PlayerData.instance.GetBool("disablePause"), "registration returns native control");
        var gate = Object.FindObjectsOfType<TransitionPoint>().Single(g => g.name == "right1");
        Log("REGISTERED gate=" + gate.targetScene + "/" + gate.entryPoint + " checkpoint=" + PlayerData.instance.GetString("respawnScene"));
        Require(gate.targetScene == "Room_Colosseum_" + trial, "board configured actual trial entrance");
        if (role != first) {
            yield return Until(() => Has(first + "." + readyMarker), "first player trial entrance complete");
            yield return new WaitForSecondsRealtime(testCase == "late" ? 12 : 3);
        }
        hero.transform.position = new Vector3(56, 9.5f, 0); hero.GetComponent<Rigidbody2D>().velocity = Vector2.zero;
        yield return WalkThroughGate();
        yield return Until(() => In("Room_Colosseum_" + trial), "actual registered trial gate entered");
        if (testCase != "native") {
            yield return Until(() => engine.ClientManager.SceneSnapshotReady, "trial room snapshot ready");
            var manager = Field<Hkmp.Game.Client.Entity.EntityManager>(engine.ClientManager, "_entityManager");
            yield return Until(() => manager.IsSceneHostDetermined, "trial authority determined");
            Require(manager.IsSceneHost == (role == first), "actual scene authority matches entrance order");
        }
        yield return new WaitForSecondsRealtime(2);
        Log("CHECKPOINT BEFORE DEATH " + PlayerData.instance.GetString("respawnScene") + "/" + PlayerData.instance.GetString("respawnMarkerName") + " type=" + PlayerData.instance.GetInt("respawnType"));
        RequireCheckpoint();
        Dump("trial"); Flag(role + "." + readyMarker);
        yield return Until(() => Has((host ? "guest" : "host") + "." + readyMarker), "both players trial observations recorded");
        if (testCase is "return" or "wipe") yield return PersonalReturns(testCase == "wipe");
    }
    private IEnumerator CheckpointLifecycle() {
        var manager = Field<Hkmp.Game.Client.Entity.EntityManager>(engine.ClientManager, "_entityManager");
        var pd = PlayerData.instance;
        // A repeated initialization on an existing entity must not reapply its
        // entrance checkpoint over a subsequently selected personal checkpoint.
        if (role != first) {
            pd.SetString("respawnScene", "Town"); pd.SetString("respawnMarkerName", "ProbeMarker");
            pd.SetInt("respawnType", 2); pd.SetBool("respawnFacingRight", true);
            foreach (var entity in Field<Dictionary<ulong, Hkmp.Game.Client.Entity.Entity>>(manager, "_entities").Values.Where(e => e.Type == Hkmp.Game.Client.Entity.EntityType.ColosseumManager))
                entity.InitializeClient();
            Require(pd.GetString("respawnScene") == "Town" && pd.GetString("respawnMarkerName") == "ProbeMarker" &&
                pd.GetInt("respawnType") == 2 && pd.GetBool("respawnFacingRight"), "repeated replica initialization preserves a later individual checkpoint");
            pd.SetString("respawnScene", "Room_Colosseum_02"); pd.SetString("respawnMarkerName", "RestBench");
            pd.SetInt("respawnType", 1); pd.SetBool("respawnFacingRight", false);
        }
        foreach (var actor in new[] { first, first == "host" ? "guest" : "host" }) {
            if (role == actor) {
                engine.ClientManager.Disconnect(); Flag(actor + ".lifecycle-offline");
                yield return Until(() => Has(actor + ".lifecycle-takeover"), "survivor becomes actual trial authority");
                yield return Connect();
                yield return Until(() => engine.ClientManager.SceneSnapshotReady && manager.IsSceneHostDetermined && !manager.IsSceneHost,
                    "same-room reconnect receives replica snapshot");
                RequireCheckpoint(); Flag(actor + ".lifecycle-reconnected");
            } else {
                yield return Until(() => Has(actor + ".lifecycle-offline") && manager.IsSceneHostDetermined && manager.IsSceneHost,
                    "real disconnect transfers trial authority");
                RequireCheckpoint(); Flag(actor + ".lifecycle-takeover");
                yield return Until(() => Has(actor + ".lifecycle-reconnected"), "peer reconnect completed");
            }
        }
        Enter("Town", "left1"); yield return Until(() => In("Town"), "left trial for new individual bench");
        var bench = Object.FindObjectsOfType<PlayMakerFSM>().Single(f => f.name == "RestBench" && f.FsmName == "Bench Control");
        HeroController.instance.transform.position = bench.transform.position + new Vector3(-1, 1.5f, 0);
        HeroController.instance.GetComponent<Rigidbody2D>().velocity = Vector2.zero;
        // The guest starts outside Town. Approaching its centre triggers the
        // first Dirtmouth title, which sends benches to Inert. Observe that
        // complete native cycle before pressing Up, without unlocking input.
        if (!host) {
            yield return Until(() => bench.ActiveStateName == "Inert", "first Town title actually started");
            yield return Until(() => bench.ActiveStateName != "Inert", "first Town title actually finished");
        }
        yield return Until(() => bench.ActiveStateName == "In Range" && HeroController.instance.cState.onGround, "new individual bench range");
        yield return Press(InputHandler.Instance.inputActions.up);
        yield return Until(() => bench.ActiveStateName == "Resting", "actually sat at new Town bench");
        yield return Press(InputHandler.Instance.inputActions.jump);
        yield return Until(() => In("Town") && !pd.GetBool("atBench"), "new Town bench get up");
        yield return new WaitForSecondsRealtime(4);
        Require(pd.GetString("respawnScene") == "Town", "retired trial cannot overwrite later native bench");
        Flag(role + ".new-bench"); yield return Until(() => Has((host ? "guest" : "host") + ".new-bench"), "both new personal benches confirmed");
        Enter("Room_Colosseum_02", "top1"); yield return Until(() => In("Room_Colosseum_02"), "reentry rest area");
        if (role != first) yield return Until(() => Has(first + ".reentry-hall"), "first reentry hall");
        Enter("Room_Colosseum_01", "bot1"); yield return Until(() => In("Room_Colosseum_01"), "reentry registration hall");
        Flag(role + ".reentry-hall"); yield return NormalEntry("reentry-arena-ready");
        Require(pd.GetInt("geo") == 10000 && !pd.GetBool("soulLimited"), "checkpoint lifecycle has no death penalty");
    }
    private IEnumerator PersonalReturns(bool teamWipe) {
        var peer = host ? "guest" : "host";
        if (!teamWipe && role == first) yield return Until(() => Has(peer + ".returned"), "non-authority completed personal return", 85);
        var pd = PlayerData.instance; var checkpoint = pd.GetString("respawnScene");
        if (teamWipe) runtime.KeyPressed = _ => false;
        pd.SetInt("health", 0);
        HeroController.instance.StartCoroutine((IEnumerator)typeof(HeroController).GetMethod("Die", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(HeroController.instance, null));
        yield return Until(() => runtime.Down || teamWipe && runtime.Resetting, "native lethal death enters co-op down or team wipe");
        if (!teamWipe) {
            var pressed = false; runtime.KeyPressed = key => key == KeyCode.F6 && !pressed && (pressed = true);
        }
        yield return Until(() => !runtime.Down && !runtime.Returning && !runtime.Resetting && ActiveScene == checkpoint && HeroController.instance.isHeroInPosition &&
            !GameManager.instance.IsInSceneTransition, "formal death reaches recorded checkpoint", 75);
        runtime.KeyPressed = _ => false;
        Require(pd.GetInt("geo") == 0 && pd.GetInt("geoPool") == 10000 && pd.GetBool("soulLimited"), "one native personal death penalty");
        Require(pd.GetString("shadeScene") == "Room_Colosseum_02", "native colosseum shade stays in rest area");
        var bench = Object.FindObjectsOfType<PlayMakerFSM>().Single(f => f.name == "RestBench" && f.FsmName == "Bench Control");
        yield return Until(() => bench.ActiveStateName == "Resting", "returned native bench resting");
        yield return Press(InputHandler.Instance.inputActions.jump);
        yield return Until(() => !pd.GetBool("atBench") && HeroController.instance.acceptingInput && !HeroController.instance.GetComponent<Rigidbody2D>().isKinematic, "return native get up and input");
        Log("OBSERVED PERSONAL RETURN " + ActiveScene + " checkpoint=" + checkpoint); Flag(role + ".returned");
        yield return Until(() => Has(peer + ".returned"), "both actual personal returns observed", 85);
        if (teamWipe) Require(Hkmp.CoopContext.Epoch == 1, "full-team death completes exactly one retry round without F6");
    }
    private IEnumerator WalkThroughGate() {
        // Inject horizontal intent after native input sampling. Movement,
        // collisions and the actual TransitionPoint remain native.
        using (new MonoMod.RuntimeDetour.Hook(typeof(HeroController).GetMethod("LookForInput", BindingFlags.Instance | BindingFlags.NonPublic),
            new Action<Action<HeroController>, HeroController>((orig, self) => { orig(self); if (self.acceptingInput) self.move_input = 1f; })))
            yield return Until(() => GameManager.instance.IsInSceneTransition, "native gate starts scene transition", 10);
    }
    private IEnumerator Press(InControl.PlayerAction action) {
        var frame = Time.frameCount + 1;
        using (new MonoMod.RuntimeDetour.Hook(typeof(InControl.OneAxisInputControl).GetProperty("WasPressed").GetGetMethod(),
            new Func<Func<InControl.OneAxisInputControl, bool>, InControl.OneAxisInputControl, bool>((orig, self) => ReferenceEquals(self, action) ? Time.frameCount == frame : orig(self))))
        using (new MonoMod.RuntimeDetour.Hook(typeof(InControl.OneAxisInputControl).GetProperty("IsPressed").GetGetMethod(),
            new Func<Func<InControl.OneAxisInputControl, bool>, InControl.OneAxisInputControl, bool>((orig, self) => ReferenceEquals(self, action) ? Time.frameCount >= frame : orig(self))))
            yield return new WaitForSecondsRealtime(.15f);
    }
}
