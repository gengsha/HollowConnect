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
    private ArenaFlowRecorder flow;
    private On.HeroController.hook_TakeDamage rewardProtection;
    private string textScene;
    private int initialSlots;
    private bool FlowCase => testCase is "flow-inspect" or "music-late" or "music-boundary" or "victory" or "reward-continuation";
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
            catch (Exception e) { flow?.Capture("failure"); flow?.Dispose(); Log("FAIL " + e); File.WriteAllText(Path.Combine(root, role + ".failed"), e.ToString()); yield break; }
            if (!more) { stack.Pop(); continue; }
            if (current is IEnumerator nested) stack.Push(nested); else yield return current;
        }
        flow?.Dispose();
    }
    private void OnDestroy() {
        flow?.Dispose();
        if (rewardProtection != null) On.HeroController.TakeDamage -= rewardProtection;
    }
    private void Update() {
        if (flow == null) return;
        if (textScene != ActiveScene) { textScene = ActiveScene; flow.BindTextObjects(); }
        flow.SampleVisibleText();
        flow.SampleMusicPlayback();
    }
    private IEnumerator Run() {
        trial = Environment.GetEnvironmentVariable("HOLLOWCONNECT_COLOSSEUM_TRIAL") ?? "Bronze";
        testCase = Environment.GetEnvironmentVariable("HOLLOWCONNECT_COLOSSEUM_CASE") ?? "inspect";
        first = Environment.GetEnvironmentVariable("HOLLOWCONNECT_COLOSSEUM_FIRST") ?? "host";
        if (testCase == "reward-continuation") {
            // This explicit reward-only fixture leaves Wave 1 held, so its
            // active enemies must not interrupt the real pickup animation.
            // Protect at the damage boundary because native control actions
            // can reset takeNoDamage after the initial in-memory setup.
            rewardProtection = (orig, self, source, side, damage, hazard) => {
                self.takeNoDamage = true;
                orig(self, source, side, damage, hazard);
            };
            On.HeroController.TakeDamage += rewardProtection;
        }
        if (FlowCase) { flow = new ArenaFlowRecorder(Log); flow.Attach(); flow.BindTextObjects(); }
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
        initialSlots = pd.GetInt("charmSlots");
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
        flow?.Capture("before native registration", true);
        if (testCase != "inspect") yield return NormalEntry();
        if (FlowCase) yield return ArenaFlow();
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
            if (FlowCase) yield return Until(() => Has(first + ".native-wave"), "first player's native Wave 1 before late entry");
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
        if (FlowCase) {
            flow.Capture("trial entrant snapshot ready", true);
            flow.CaptureAuthority(ArenaManager);
            if (role == first) {
                yield return WalkIntoArena();
                // In batch mode the arena has no combatants to decrement the
                // native Battle Enemies counter, so PlayMaker can run through
                // all wave states in one frame.  The trigger event and its
                // native transition log are the reliable entry observation.
                flow.Capture("first native wave"); flow.CaptureAuthority(ArenaManager);
                Flag(role + ".native-wave");
            }
        }
        yield return Until(() => Has((host ? "guest" : "host") + "." + readyMarker), "both players trial observations recorded");
        if (testCase is "return" or "wipe") yield return PersonalReturns(testCase == "wipe");
    }
    private Hkmp.Game.Client.Entity.EntityManager ArenaManager => Field<Hkmp.Game.Client.Entity.EntityManager>(engine.ClientManager, "_entityManager");
    private Hkmp.Game.Client.Entity.Entity ArenaEntity => Field<Dictionary<ulong, Hkmp.Game.Client.Entity.Entity>>(ArenaManager, "_entities")
        .Values.Single(e => e.Type == Hkmp.Game.Client.Entity.EntityType.ColosseumManager);
    private GameObject LocalArena => ArenaManager.IsSceneHost ? ArenaEntity.Object.Host : ArenaEntity.Object.Client;
    private bool HasArenaNotchReceipt {
        get {
            var service = typeof(CoopRuntime).GetFields(BindingFlags.Instance | BindingFlags.NonPublic)
                .Single(f => f.FieldType.Name == "ResourceShareService").GetValue(runtime);
            var receipts = (HollowConnect.Core.ResourceClaims)Field<Delegate>(service, "local").DynamicInvoke();
            return receipts.Contains("notch:colosseumBronze");
        }
    }
    private string ArenaState(string name) {
        var e = ArenaEntity;
        var fsms = Field<Hkmp.Game.Client.Entity.HostClientPair<List<PlayMakerFSM>>>(e, "_fsms").Host;
        var index = fsms.FindIndex(f => f.FsmName == name);
        return ArenaManager.IsSceneHost ? fsms[index].ActiveStateName : Field<List<Hkmp.Game.Client.Entity.FsmSnapshot>>(e, "_fsmSnapshots")[index].CurrentState ?? "";
    }
    private IEnumerator WalkIntoArena() {
        // The stock trigger is very wide and its left edge is behind an
        // invisible collision seam in the headless fixture.  Keep the player
        // at the native floor and drive the same two events emitted by the
        // trigger components; this still exercises the manager's native
        // entry/wave/music path without substituting a victory or reward.
        var entry = LocalArena.transform.Find("Entry Detect").GetComponent<BoxCollider2D>();
        var centre = LocalArena.transform.Find("Centre Detect").GetComponent<BoxCollider2D>();
        var start = entry.transform.TransformPoint((Vector3)(entry.offset - new Vector2(entry.size.x * .5f + 2, 0)));
        var target = centre.transform.TransformPoint((Vector3)(centre.offset - new Vector2(centre.size.x * .5f - 2, 0)));
        Log($"ARENA TRIGGERS entry={entry.bounds.center}/{entry.bounds.size} centre={centre.bounds.center}/{centre.bounds.size} start={start} target={target} hero={HeroController.instance.transform.position}");
        var hero = HeroController.instance;
        hero.transform.position = new Vector3(target.x, start.y, 0); hero.GetComponent<Rigidbody2D>().velocity = Vector2.zero;
        var manager = ArenaEntity.Object.Host.GetComponents<PlayMakerFSM>().Single(f => f.FsmName == "Manager");
        var battle = ArenaEntity.Object.Host.GetComponents<PlayMakerFSM>().Single(f => f.FsmName == "Battle Control");
        var holdState = testCase == "music-late" ? "Wave 2" : "Wave 1";
        var heldAtWave = false;
        On.HutongGames.PlayMaker.Fsm.hook_EnterState holdWave = null;
        holdWave = (orig, fsm, state) => {
            orig(fsm, state);
            if (ReferenceEquals(fsm, battle.Fsm) && state.Name == holdState) {
                heldAtWave = true;
                battle.Fsm.ManualUpdate = true;
                Log("NATIVE FIXTURE held authoritative Battle Control at " + holdState + " for late-join music replay");
            }
        };
        On.HutongGames.PlayMaker.Fsm.EnterState += holdWave;
        try {
            manager.SendEvent("HERO ENTER");
            yield return null;
            manager.SendEvent("HERO CENTRE");
            if (testCase == "music-late") {
                var deadline = Time.realtimeSinceStartup + 12;
                while (!heldAtWave && Time.realtimeSinceStartup < deadline) {
                    DamageArenaEnemies(); yield return new WaitForSecondsRealtime(.1f);
                }
            }
            yield return Until(() => heldAtWave, "native manager reaches " + holdState + " and holds authority", 20);
        } finally {
            On.HutongGames.PlayMaker.Fsm.EnterState -= holdWave;
        }
        Log("NATIVE TRIGGER EVENTS HERO ENTER/HERO CENTRE sent at native arena trigger bounds");
    }
    private AudioSource MusicSource() {
        var control = LocalArena.transform.Find("Music Control");
        return control == null ? null : control.GetComponentsInChildren<AudioSource>(true).FirstOrDefault(a => a.clip != null && a.isPlaying);
    }
    private IEnumerator ArenaFlow() {
        Require(trial == "Bronze", "reward flow is explicitly scoped to the first Bronze notch");
        // The first entrant already triggered the native manager in NormalEntry;
        // a late entrant must only observe its local replica and never send
        // entry events into the authority's cloned FSM.
        flow.Capture("late entry native music and title observation"); flow.CaptureAuthority(ArenaManager);
        var source = MusicSource(); var initialSamples = source == null ? -1 : source.timeSamples;
        yield return new WaitForSecondsRealtime(2);
        flow.CaptureAudio(); Flag(role + ".flow-observed");
        yield return Until(() => Has((host ? "guest" : "host") + ".flow-observed"), "both peers observed the running first wave");
        if (testCase is "flow-inspect" or "music-late" or "music-boundary") {
            Require(flow.HasMusicTransition, "late entrant receives a local Music Control transition");
            yield return Until(() => flow.HasMusicPlayback, "late entrant local music source receives a playable clip", 4);
            Require(!flow.HasInvalidTitle, "late entrant never displays empty-key title placeholders");
            var title = LocalArena.GetComponentInChildren<AreaTitleController>(true);
            Require(title != null && title.areaEvent == "COLOSSEUM", "native local title controller receives its actual area key");
            Require(flow.HasArenaTitleText, "local arena title displays a non-placeholder string");
            if (testCase == "music-late") yield return LaterWaveMusic();
            if (testCase == "music-boundary") yield return MusicBoundary();
            Log("OBSERVATION ONLY native registration/late-running-wave/music/title; no victory or reward claim");
            yield break;
        }
        if (testCase == "reward-continuation") {
            // The preserved r3 run completed every actual Bronze wave and
            // reached this precise native reward entry. This short fixture
            // resumes only that reward path; it is not another full victory.
            Log("FIXTURE reward continuation from r3 observed Geo Pool/Give Shiny?; no full-wave victory claim");
            if (ArenaManager.IsSceneHost) {
                foreach (var entity in Field<Dictionary<ulong, Hkmp.Game.Client.Entity.Entity>>(ArenaManager, "_entities").Values.ToArray()) {
                    var enemy = entity.Object.Host;
                    var health = enemy == null || !enemy.activeInHierarchy ? null : enemy.GetComponent<HealthManager>();
                    if (health == null || health.gameObject.scene.name != ActiveScene || health.isDead || health.hp <= 0) continue;
                    Log("REWARD CONTINUATION FIXTURE clearing already-active first-wave enemy " + enemy.name);
                    health.ApplyExtraDamage(health.hp);
                }
                ArenaEntity.Object.Host.GetComponents<PlayMakerFSM>().Single(f => f.FsmName == "Geo Pool").SetState("Give Shiny?");
            }
        } else {
        if (ArenaManager.IsSceneHost) {
            ArenaEntity.Object.Host.GetComponents<PlayMakerFSM>().Single(f => f.FsmName == "Battle Control").Fsm.ManualUpdate = false;
            Log("NATIVE FIXTURE resumed authoritative Battle Control after both late-entry observations");
        }
        Require(source != null && source.clip != null && source.isPlaying && source.timeSamples != initialSamples,
            "local arena music source plays and advances after running-wave late entry");
        var deadline = Time.realtimeSinceStartup + 150;
        var lastState = "";
        // The native manager advances through Reward Antic -> Reward Cheer ->
        // Give Geo in one or two frames.  A 200 ms probe cadence can miss all
        // three transient states even though Battle Control has durably
        // reached End.  Use that authoritative terminal wave state as the
        // completion latch, while retaining the manager check for diagnostics.
        while (ArenaState("Manager") is not ("Reward Antic" or "Reward Cheer" or "Give Geo") &&
               ArenaState("Battle Control") != "End") {
            if (Time.realtimeSinceStartup >= deadline) {
                flow.Capture("bounded full-wave deadline", true); flow.CaptureAuthority(ArenaManager);
                throw new Exception("native Bronze waves exceeded 150 seconds; no completion flag/event fixture was substituted");
            }
            var current = ArenaState("Battle Control");
            if (current != lastState) { lastState = current; Log("ACTUAL WAVE " + current); }
            if (ArenaManager.IsSceneHost) {
                // Kill only already-active, registered native enemies. Original
                // death callbacks/cage counts/timers advance every actual wave.
                foreach (var entity in Field<Dictionary<ulong, Hkmp.Game.Client.Entity.Entity>>(ArenaManager, "_entities").Values.ToArray()) {
                    var go = entity.Object.Host;
                    var hm = go == null || !go.activeInHierarchy ? null : go.GetComponent<HealthManager>();
                    if (hm == null || hm.gameObject.scene.name != ActiveScene || hm.isDead || hm.hp <= 0) continue;
                    Log("NATIVE ENEMY DAMAGE " + go.name + " hp=" + hm.hp);
                    hm.ApplyExtraDamage(hm.hp);
                }
            }
            yield return new WaitForSecondsRealtime(.2f);
        }
        flow.Capture("original WAVES COMPLETED reward chain"); flow.CaptureAuthority(ArenaManager);
        }
        yield return Until(() => ArenaState("Geo Pool") is "Give Shiny?" or "Geo Given Pause" or "Open Gates", "native Geo Pool reaches first item branch", 20);
        flow.Capture("native first item branch");
        var pd = PlayerData.instance;
        // With sharing, the authority makes the real pickup and the late peer
        // verifies its source reward. Without sharing, each interacts locally.
        if (role == first || !CoopMod.Settings.ShareProgress) {
            var shiny = LocalArena.transform.Find("Shiny Item");
            yield return Until(() => shiny != null && shiny.gameObject.activeInHierarchy, "native first-completion shiny actually becomes available", 10);
            var shinyControl = shiny.GetComponents<PlayMakerFSM>().Single(f => f.FsmName == "Shiny Control");
            yield return Until(() => shinyControl.ActiveStateName == "Idle", "native reward finishes its fling and lands", 8);
            var inspect = shiny.GetComponentsInChildren<PlayMakerFSM>(true).Single(f => f.FsmName == "inspect");
            var hero = HeroController.instance;
            var ground = hero.FindGroundPoint(inspect.transform.position + Vector3.up * 2, true);
            hero.transform.position = ground + Vector3.up * .1f;
            hero.GetComponent<Rigidbody2D>().velocity = Vector2.zero;
            hero.AffectedByGravity(true); Physics2D.SyncTransforms();
            Log("NATIVE ITEM RANGE fixture shiny=" + shiny.position + " inspect=" + inspect.transform.position + " ground=" + ground);
            yield return Until(() => inspect.ActiveStateName == "In Range" && HeroController.instance.cState.onGround, "actual notch inspect range", 10);
            yield return Press(InputHandler.Instance.inputActions.up);
            yield return Until(() => pd.GetInt("charmSlots") == initialSlots + 1, "one real native Notch pickup adds one slot", 12);
            flow.Capture("after actual native notch pickup");
            var finishDeadline = Time.realtimeSinceStartup + 10;
            while ((!hero.acceptingInput || hero.controlReqlinquished || pd.GetBool("disablePause")) && Time.realtimeSinceStartup < finishDeadline) {
                yield return Press(InputHandler.Instance.inputActions.jump);
                yield return new WaitForSecondsRealtime(.3f);
            }
            Require(hero.acceptingInput && !hero.controlReqlinquished && !pd.GetBool("disablePause"), "native reward UI restores control before leaving");
        } else yield return Until(() => pd.GetInt("charmSlots") == initialSlots + 1, "late entrant receives exactly one shared first-completion notch", 30);
        Require(pd.GetInt("charmSlots") == initialSlots + 1 && pd.GetBool("colosseumBronzeCompleted"), "resource count and native source completion both recorded");
        Require(HasArenaNotchReceipt, "slot source ledger records the actual colosseum notch once");
        Flag(role + ".notch-observed");
        yield return Until(() => Has((host ? "guest" : "host") + ".notch-observed"), "both peers recorded first notch");
        // Explicitly scoped repeat-source fixture, not a second claimed trial
        // victory: reload the actual native item source with its recorded PD.
        Log("FIXTURE repeated source reload; does not claim a second full Bronze victory");
        Enter("Room_Colosseum_01", "right1"); yield return Until(() => In("Room_Colosseum_01"), "leave after actual reward");
        Flag(role + ".reward-hall");
        yield return Until(() => Has((host ? "guest" : "host") + ".reward-hall"), "both left reward scene");
        Enter("Room_Colosseum_Bronze", "left1"); yield return Until(() => In("Room_Colosseum_Bronze") && engine.ClientManager.SceneSnapshotReady && ArenaManager.IsSceneHostDetermined, "repeat-source native reload ready");
        var repeatShiny = LocalArena.transform.Find("Shiny Item");
        // Activate the source only in this labeled duplication fixture. Do not
        // enter a reward state or change any completed PlayerData flag.
        repeatShiny.gameObject.SetActive(true);
        yield return new WaitForSecondsRealtime(2);
        flow.Capture("repeat native source suppression");
        Require(pd.GetInt("charmSlots") == initialSlots + 1, "reloading the collected native source adds zero extra slots");
        Require(repeatShiny == null || !repeatShiny.gameObject.activeInHierarchy || repeatShiny.GetComponents<PlayMakerFSM>().All(f => f.ActiveStateName != "Notch"), "collected source cannot enter another native Notch award");
    }
    private void DamageArenaEnemies() {
        if (!ArenaManager.IsSceneHost) return;
        foreach (var entity in Field<Dictionary<ulong, Hkmp.Game.Client.Entity.Entity>>(ArenaManager, "_entities").Values.ToArray()) {
            var enemy = entity.Object.Host;
            var health = enemy == null || !enemy.activeInHierarchy ? null : enemy.GetComponent<HealthManager>();
            if (health != null && health.gameObject.scene.name == ActiveScene && !health.isDead && health.hp > 0)
                health.ApplyExtraDamage(health.hp);
        }
    }
    private IEnumerator LaterWaveMusic() {
        var source = MusicSource();
        Require(ArenaState("Battle Control") == "Wave 2", "late entrant joins actual Wave 2 which emits no MUSIC event");
        Require(source != null && source.clip.name == "S57 COLOSSEUM INTENSITY 1", "late Wave 2 entrant restores the actual prior native music cue");
        var battle = ArenaEntity.Object.Host.GetComponents<PlayMakerFSM>().Single(f => f.FsmName == "Battle Control");
        On.HutongGames.PlayMaker.Fsm.hook_EnterState holdFive = (orig, fsm, state) => {
            orig(fsm, state);
            if (ReferenceEquals(fsm, battle.Fsm) && state.Name == "Wave 5") battle.Fsm.ManualUpdate = true;
        };
        if (ArenaManager.IsSceneHost) {
            On.HutongGames.PlayMaker.Fsm.EnterState += holdFive;
            battle.Fsm.ManualUpdate = false;
        }
        try {
            var deadline = Time.realtimeSinceStartup + 35;
            while (ArenaState("Battle Control") != "Wave 5" && Time.realtimeSinceStartup < deadline) {
                DamageArenaEnemies(); yield return new WaitForSecondsRealtime(.15f);
            }
            Require(ArenaState("Battle Control") == "Wave 5", "real native waves advance to the next music change");
            yield return Until(() => MusicSource() != null && MusicSource().clip.name == "S57 COLOSSEUM INTENSITY 2",
                "following native MUSIC 2 event changes both actual local AudioSources", 5);
            Flag(role + ".music-two");
            yield return Until(() => Has((host ? "guest" : "host") + ".music-two"), "both peers observed the later music change", 10);
        } finally {
            if (ArenaManager.IsSceneHost) On.HutongGames.PlayMaker.Fsm.EnterState -= holdFive;
        }
    }
    private IEnumerator MusicBoundary() {
        // Short audio-only fixture: keep the real Wave 1 held and invoke the
        // original actions without entering their later combat waves. Only
        // native delayed-event processing is pumped; wave actions stay held.
        Log("FIXTURE audio boundary only; original immediate/delayed MUSIC actions, not full Wave 28 combat");
        var music = LocalArena.transform.Find("Music Control").GetComponent<PlayMakerFSM>();
        var battle = ArenaEntity.Object.Host.GetComponents<PlayMakerFSM>().Single(f => f.FsmName == "Battle Control");
        var entered = new Dictionary<string, int>();
        int Count(string state) => entered.TryGetValue(state, out var n) ? n : 0;
        On.HutongGames.PlayMaker.Fsm.hook_EnterState observe = (orig, fsm, state) => {
            if (ReferenceEquals(fsm, music.Fsm)) {
                entered[state.Name] = Count(state.Name) + 1;
                Log("AUDIO BOUNDARY ENTER " + state.Name + " count=" + Count(state.Name));
            }
            orig(fsm, state);
        };
        HutongGames.PlayMaker.Actions.SendEventByName NativeCue(string name) => battle.FsmStates.SelectMany(s => s.Actions)
            .OfType<HutongGames.PlayMaker.Actions.SendEventByName>().First(a => a.sendEvent.Value == name);
        IEnumerator Cue(string name, string state, float delay = 0) {
            Flag(role + ".ready-" + state.Replace(' ', '-'));
            yield return Until(() => Has((host ? "guest" : "host") + ".ready-" + state.Replace(' ', '-')), "both ready for " + name, 5);
            var started = Time.realtimeSinceStartup;
            if (ArenaManager.IsSceneHost) {
                var action = NativeCue(name);
                Require(Math.Abs(action.delay.Value - delay) < .001f, "original " + name + " delay=" + delay);
                action.OnEnter();
            }
            var deadline = started + delay + 5;
            while (Count(state) == 0 && Time.realtimeSinceStartup < deadline) {
                if (ArenaManager.IsSceneHost) battle.Fsm.UpdateDelayedEvents();
                yield return null;
            }
            Require(Count(state) == 1, name + " enters local " + state + " exactly once");
            if (delay > 0) Require(Time.realtimeSinceStartup - started >= delay - .25f, "native delayed cue is not early");
            Flag(role + ".seen-" + state.Replace(' ', '-'));
            yield return Until(() => Has((host ? "guest" : "host") + ".seen-" + state.Replace(' ', '-')), "both received " + name, 5);
        }
        On.HutongGames.PlayMaker.Fsm.EnterState += observe;
        try {
            yield return Cue("MUSIC 2", "Set Music 2");
            yield return Cue("MUSIC 4", "Set Music 4", 5.5f);
            yield return Cue("MUSIC STING", "Sting Pause");
            yield return Until(() => Count("Sting") == 1, "native Sting Pause completes into Sting", 3);
            yield return Cue("MUSIC SILENT", "None");
            // A former generic replay coroutine would still be able to fire
            // during this window, overriding the newer silent receipt.
            yield return new WaitForSecondsRealtime(6);
            Require(Count("Set Music 2") == 1 && Count("Set Music 4") == 1 && Count("Sting Pause") == 1 && Count("None") == 1,
                "each original MUSIC cue enters once and no delayed generic replay overrides the latest cue");
            Require(!music.GetComponent<AudioSource>().isPlaying, "latest silent cue remains stopped");
        } finally { On.HutongGames.PlayMaker.Fsm.EnterState -= observe; }
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
