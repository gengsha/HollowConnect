using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using Hkmp.Game.Client.Entity;
using Hkmp.Game.Client.Entity.Action;
using Hkmp.Game.Client.Entity.Component;
using Hkmp.Networking.Client;
using Hkmp.Networking.Packet.Data;
using HutongGames.PlayMaker;
using HutongGames.PlayMaker.Actions;
using HollowConnect;
using Modding;
using UnityEngine;
using Object = UnityEngine.Object;

namespace HollowConnect.Smoke;

// Private disposable clients; no user save is opened or written.
public sealed class GodhomeProbe : MonoBehaviour {
    private string role, root;
    private bool host;
    private Hkmp.Game.GameManager engine;
    private CoopRuntime runtime;
    private EntityManager manager;
    private int fades;
    private int fireballs, bats, pillars, pillarEntities, spikeSamples, positionSamples;
    private int nativeAudioFaults;
    private float nextSample;
    private float nextDiagnostic;
    private void Update() {
        if (runtime == null || manager == null || Time.realtimeSinceStartup < nextDiagnostic) return;
        nextDiagnostic = Time.realtimeSinceStartup + 5;
        var room = Field<object>(manager, "_bossRoom");
        Mark("DIAGNOSTIC scene=" + Hkmp.CoopContext.LocalScene + " playable=" + runtime.Playable + " host=" + manager.IsSceneHost +
            " ready=" + Hkmp.CoopContext.BossTransitionReady?.Invoke() + " pending=" + Field<string>(room, "pending") + " latest=" + Field<string>(room, "latest") +
            " unscaled=" + Time.unscaledTime + " realtime=" + Time.realtimeSinceStartup + " tickNext=" + Field<float>(room, "next") +
            " controller=" + (BossSceneController.Instance == null ? "null" : BossSceneController.Instance.gameObject.scene.name + "/" + BossSceneController.Instance.isActiveAndEnabled) +
            " netReady=" + manager.IsSceneHostDetermined);
    }
    private sealed class ProbeLogger : Hkmp.Logging.ILogger {
        private readonly GodhomeProbe probe;
        internal ProbeLogger(GodhomeProbe probe) { this.probe = probe; }
        public void Info(string s) { if (s.StartsWith("Boss room")) probe.Mark("ENGINE " + s); }
        public void Debug(string s) { }
        public void Fine(string s) { }
        public void Warn(string s) => probe.Mark("ENGINE WARN " + s);
        public void Error(string s) => probe.Mark("ENGINE ERROR " + s);
    }
    private readonly Dictionary<ulong, float> firstSeen = new Dictionary<ulong, float>();
    private readonly Dictionary<EntityType, int> matchedPositions = new Dictionary<EntityType, int>();
    private static T Field<T>(object o, string n) => (T)o.GetType().GetField(n, BindingFlags.Instance | BindingFlags.NonPublic).GetValue(o);
    private readonly object logLock = new object();
    private void Mark(string s) {
        lock (logLock) {
            using (var stream = new FileStream(Path.Combine(root, role + ".log"), FileMode.OpenOrCreate, FileAccess.Write, FileShare.ReadWrite)) {
                stream.Seek(0, SeekOrigin.End);
                using (var writer = new StreamWriter(stream)) writer.WriteLine(DateTime.UtcNow.ToString("O") + " " + s);
            }
        }
    }
    private void Flag(string n, string v = "ready") => File.WriteAllText(Path.Combine(root, n), v);
    private bool Has(string n) => File.Exists(Path.Combine(root, n));
    private bool Check(bool value, string label) {
        Mark((value ? "PASS " : "FAIL ") + label);
        if (!value) Flag(role + ".failed", label);
        return value;
    }
    private IEnumerator Until(Func<bool> ready, string label, float timeout = 45) {
        var end = Time.realtimeSinceStartup + timeout;
        while (!ready() && Time.realtimeSinceStartup < end) yield return null;
        Check(ready(), label);
    }
    private bool In(string scene) => runtime.Playable && GameManager.instance.sceneName == scene;
    private void Freeze() {
        var hero = HeroController.instance; hero.takeNoDamage = true;
        hero.GetComponent<Rigidbody2D>().gravityScale = 0; hero.GetComponent<Rigidbody2D>().velocity = Vector2.zero;
    }
    private void Enter(string scene) => GameManager.instance.BeginSceneTransition(new GameManager.SceneLoadInfo {
        SceneName = scene, EntryGateName = scene.StartsWith("GG_") ? "door_dreamEnter" : "left1", Visualization = GameManager.SceneLoadVisualizations.Default,
        WaitForSceneTransitionCameraFade = true
    });
    private Dictionary<ulong, Entity> Entities => Field<Dictionary<ulong, Entity>>(manager, "_entities");
    private static PlayMakerFSM Control(Entity e) => e.Object.Host.GetComponents<PlayMakerFSM>().Single(f => f.FsmName == "Control");
    private void Dump(Entity e) {
        var go = e.Object.Host; var rb = go.GetComponent<Rigidbody2D>();
        Mark("ENTITY " + e.Id + " " + e.Type + " pos=" + go.transform.position + " active=" + go.activeInHierarchy +
            " rb=" + (rb == null ? "null" : rb.bodyType + "/gravity=" + rb.gravityScale + "/v=" + rb.velocity));
        foreach (var fsm in go.GetComponents<PlayMakerFSM>()) {
            Mark("FSM " + fsm.FsmName + "=" + fsm.ActiveStateName);
            foreach (var state in fsm.FsmStates) {
                Mark("STATE " + state.Name + " " + string.Join(",", state.Actions.Select(a => a.GetType().Name)));
                if (state.Name == "Init") foreach (var a in state.Actions.Where(a => a.GetType().Name == "CallStaticMethod")) {
                    foreach (var field in a.GetType().GetFields()) Mark("INIT CALL " + field.Name + "=" + field.GetValue(a));
                }
            }
        }
    }
    private IEnumerator Start() {
        var stack = new Stack<IEnumerator>(); stack.Push(Run());
        var deadline = DateTime.UtcNow.AddMinutes(5);
        while (stack.Count > 0) {
            object current = null; bool moved;
            try {
                if (DateTime.UtcNow > deadline) throw new TimeoutException("Godhome probe exceeded five minutes");
                moved = stack.Peek().MoveNext();
                if (moved) current = stack.Peek().Current;
            } catch (Exception e) {
                Mark("FAIL unhandled probe error: " + e); Flag(role + ".failed", e.ToString()); yield break;
            }
            if (!moved) { stack.Pop(); continue; }
            if (current is IEnumerator nested) stack.Push(nested);
            else yield return current;
        }
    }
    private IEnumerator Run() {
        role = Environment.GetEnvironmentVariable("HOLLOWCONNECT_SMOKE_ROLE"); root = Environment.GetEnvironmentVariable("HOLLOWCONNECT_SMOKE_ROOT"); host = role == "host";
        Hkmp.Logging.Logger.AddLogger(new ProbeLogger(this));
        On.HutongGames.PlayMaker.Actions.AudioPlayerOneShot.OnEnter += (orig, action) => {
            try { orig(action); }
            catch (Exception) {
                nativeAudioFaults++;
                Mark("NATIVE AUDIO FAULT scene=" + GameManager.instance?.sceneName + " object=" + action.Fsm.GameObject.name +
                    " fsm=" + action.Fsm.Name + " state=" + action.State.Name + " prefab=" + action.audioPlayer?.Value?.name +
                    " point=" + action.spawnPoint?.Value?.name);
                throw;
            }
        };
        yield return Until(() => Object.FindObjectOfType<CoopRuntime>() != null && GameManager.instance != null &&
            UnityEngine.SceneManagement.SceneManager.GetActiveScene().name == "Menu_Title", "menu ready");
        if (Has(role + ".failed")) yield break;
        runtime = Object.FindObjectOfType<CoopRuntime>(); engine = Field<Hkmp.Game.GameManager>(ModHooks.GetMod("HKMP"), "_gameManager");
        manager = Field<EntityManager>(engine.ClientManager, "_entityManager");
        Field<Hkmp.Game.Settings.ModSettings>(engine.ClientManager, "_modSettings").AuthKey = Hkmp.Util.AuthUtil.GenerateAuthKey();
        CoopMod.Settings.ShareProgress = false;
        On.GameManager.FadeSceneIn += (orig, gm) => { fades++; orig(gm); };
        var pd = PlayerData.instance; pd.SetString("respawnScene", "Town"); pd.SetString("respawnMarkerName", "RestBench"); pd.SetInt("respawnType", 1);
        GameManager.instance.profileID = 99; GameManager.instance.StartCoroutine(GameManager.instance.RunContinueGame());
        yield return Until(() => In("Town"), "unsaved character ready"); Freeze();
        if (host) { engine.ServerManager.Start(29650, false); Flag("server.started"); }
        else yield return Until(() => Has("server.started"), "server ready");
        typeof(Hkmp.Game.Client.ClientManager).GetField("_autoConnect",BindingFlags.Instance|BindingFlags.NonPublic).SetValue(engine.ClientManager,true);
        typeof(Hkmp.Game.Client.ClientManager).GetMethod("Connect", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(engine.ClientManager, new object[] { "127.0.0.1", 29650, "godhome" + role });
        yield return Until(() => runtime.Connected, "handshake");
        Flag(role + ".connected"); yield return Until(() => Has(host ? "guest.connected" : "host.connected"), "both connected");
        if (host) {
            Enter("Crossroads_01"); yield return Until(() => In("Crossroads_01"), "warp destination ready"); Freeze();
            HeroController.instance.transform.position = new Vector3(20, 10, 0); Flag("host.destination");
            yield return Until(() => Has("guest.warped"), "guest warp tested");
            Enter("Town"); yield return Until(() => In("Town"), "host leaves for reverse warp"); Freeze(); Flag("host.away");
        } else yield return Until(() => Has("host.destination"), "host warp destination");
        if (!host) yield return Warp("Crossroads_01");
        else { yield return Until(() => Has("guest.reverse-ready"), "reverse warp target ready"); yield return Warp("Crossroads_01"); }
        if (!host) { Flag("guest.warped"); yield return Until(() => Has("host.away"), "host leaves"); Flag("guest.reverse-ready"); }
        Flag(role + ".warps-done"); yield return Until(() => Has(host ? "guest.warps-done" : "host.warps-done"), "both warp directions");
        var sequences = Resources.LoadAll<BossSequence>("GG");
        Mark("BUILTIN SEQUENCES " + string.Join(",", sequences.Select(s => s.name + "/" + s.Count)));
        var sequence = sequences.Single(s => s.name == "Boss Sequence Tier 3");
        Mark("SEQUENCE SCENES " + string.Join(",", Enumerable.Range(0, sequence.Count).Select(i => i + "/" + sequence.GetSceneAt(i))));
        BossSequenceController.SetupNewSequence(sequence, BossSequenceController.ChallengeBindings.None, "bossDoorStateTier3");
        var indexField = typeof(BossSequenceController).GetField("bossIndex", BindingFlags.Static | BindingFlags.NonPublic);
        if (Environment.GetEnvironmentVariable("HOLLOWCONNECT_GALIEN_ONLY") == "1") {
            indexField.SetValue(null, 6);
            yield return GalienTransfer();
            yield break;
        }
        indexField.SetValue(null, Enumerable.Range(0, sequence.Count).First(i => sequence.GetSceneAt(i) == "GG_Grimm"));
        Mark("THIRD PANTHEON " + sequence.name + " starting at Grimm index=" + indexField.GetValue(null));
        var serverAuthority = Environment.GetEnvironmentVariable("HOLLOWCONNECT_GODHOME_AUTHORITY") == "host";
        if (host != serverAuthority) yield return new WaitForSecondsRealtime(2);
        Enter("GG_Grimm"); yield return Until(() => In("GG_Grimm"), "Grimm arena entered"); Freeze();
        yield return Until(() => manager.IsSceneHostDetermined && Entities.Values.Any(e => e.Type == EntityType.Grimm), "Grimm authority ready");
        Mark("AUTHORITY=" + manager.IsSceneHost);
        var grimmAuthority = manager.IsSceneHost;
        var grimm = Entities.Values.Single(e => e.Type == EntityType.Grimm); Dump(grimm);
        FsmActionHooks.RegisterFsmStateActionType(typeof(SpawnObjectFromGlobalPool), a => {
            if (a.Fsm.GameObject != grimm.Object.Host || !a.State.Name.StartsWith("Firebat")) return;
            var spawn = (SpawnObjectFromGlobalPool)a;
            Mark("ACTUAL FIREBAT SPAWN " + a.State.Name + " prefab=" + spawn.gameObject.Value?.name + " stored=" + spawn.storeObject.Value?.name +
                " none=" + spawn.storeObject.IsNone + " registry=" + EntityRegistry.TryGetEntry(spawn.storeObject.Value, out _) +
                " bound=" + Field<Dictionary<FsmStateAction, HookedEntityAction>>(grimm, "_hookedActions").ContainsKey(a) +
                " controlled=" + Field<bool>(grimm, "_isControlled"));
        });
        foreach (var asset in Resources.FindObjectsOfTypeAll<GameObject>().Where(g => g.name.IndexOf("Firebat", StringComparison.OrdinalIgnoreCase) >= 0))
            Mark("FIREBAT ASSET " + asset.name + " fsms=" + string.Join(",", asset.GetComponents<PlayMakerFSM>().Select(f => f.FsmName)));
        foreach (var state in Control(grimm).FsmStates.Where(s => s.Name.StartsWith("Firebat"))) {
            foreach (var action in state.Actions.OfType<SpawnObjectFromGlobalPool>())
                Mark("FIREBAT SPAWN " + state.Name + " enabled=" + action.Enabled + " prefab=" + action.gameObject.Value?.name + " fsms=" + string.Join(",", action.gameObject.Value.GetComponents<PlayMakerFSM>().Select(f => f.FsmName)));
        }
        yield return ObserverFaults(grimm);
        Flag(role + ".grimm"); yield return Until(() => Has(host ? "guest.grimm" : "host.grimm"), "both Grimm rooms");
        HeroController.instance.transform.position = new Vector3(90, 8, 0); Freeze();
        yield return Arsenal(35);
        if (manager.IsSceneHost && grimm.Object.Host != null) grimm.Object.Host.GetComponent<HealthManager>().ApplyExtraDamage(201);
        // Grimm waits for the hero to clear his balloon position. Keep the
        // fixture out of that radius without forcing any native FSM state.
        HeroController.instance.transform.position = new Vector3(70, 8, 0); Freeze();
        foreach (var action in (grimm.Object.Host == null ? Array.Empty<FsmStateAction>() : Control(grimm).FsmStates.Single(s => s.Name == "Balloon Check").Actions).OfType<GetDistance>())
            Mark("BALLOON CHECK from=" + action.Fsm.GetOwnerDefaultTarget(action.gameObject)?.name + " to=" + action.target.Value?.name);
        var sampled = new HashSet<ulong>(); var until = Time.realtimeSinceStartup + 22;
        while (Time.realtimeSinceStartup < until && In("GG_Grimm")) {
            foreach (var e in Entities.Values.Where(e => e.Type == EntityType.GrimmFireball || e.Type == EntityType.GrimmBat).ToArray()) {
                if (!sampled.Add(e.Id)) continue;
                Dump(e); Mark("PROJECTILE client rb=" + e.Object.Client.GetComponent<Rigidbody2D>() + " scripts=" + e.Object.Client.GetComponent<GrimmballControl>());
            }
            SampleArsenal(); yield return null;
        }
        if (!grimmAuthority) { runtime.Down = true; Flag(role + ".hold-completion"); }
        else yield return Until(() => Has((host ? "guest" : "host") + ".hold-completion"), "replica blocks completion while downed");
        if (grimm.Object.Host != null) {
            Mark("GRIMM END state=" + Control(grimm).ActiveStateName + " hp=" + grimm.Object.Host.GetComponent<HealthManager>().hp);
            if (manager.IsSceneHost) grimm.Object.Host.GetComponent<HealthManager>().ApplyExtraDamage(10000);
        }
        if (!grimmAuthority) {
            yield return Until(() => Has((host ? "guest" : "host") + ".spa"), "authority completed while replica remains downed", 30);
            Check(In("GG_Grimm"), "downed replica remains in boss room until it can transition");
            runtime.Down = false;
        }
        yield return Until(() => In("GG_Spa"), "native Grimm death advances sequence", 30);
        if (Has(role + ".failed")) yield break;
        Freeze(); Flag(role + ".spa");
        yield return Until(() => Has(host ? "guest.spa" : "host.spa"), "both advanced to native pantheon rest room");
        if (!host) { Enter("GG_Ghost_Galien"); yield return Until(() => In("GG_Ghost_Galien"), "guest enters next native sequence boss"); Freeze(); Flag("guest.galien"); }
        else {
            yield return Until(() => Has("guest.galien"), "guest next boss ready");
            // Reproduce a stranded participant whose local sequence counter
            // diverged, then use the real F8 request/response transport.
            indexField.SetValue(null, 0);
            yield return Warp("GG_Ghost_Galien");
            Check((int)indexField.GetValue(null) == Enumerable.Range(0, sequence.Count).First(i => sequence.GetSceneAt(i) == "GG_Ghost_Galien"), "F8 repairs stale native sequence counter");
            Flag("host.galien-warp");
        }
        if (!host) yield return Until(() => Has("host.galien-warp"), "stranded host catches up to next boss");
        Flag(role + ".galien"); yield return Until(() => Has(host ? "guest.galien" : "host.galien"), "both at next boss");
        if (host) {
            ((BossSequenceController.BossSequenceData)typeof(BossSequenceController).GetField("currentData", BindingFlags.Static | BindingFlags.NonPublic).GetValue(null)).bindings =
                BossSequenceController.ChallengeBindings.Nail | BossSequenceController.ChallengeBindings.Shell;
            indexField.SetValue(null, 0);
            yield return new WaitForSecondsRealtime(3.2f);
            runtime.Selected = runtime.Peers.First(p => p.Value.State?.Scene == "GG_Ghost_Galien").Key;
            var pressed = false; runtime.KeyPressed = key => key == KeyCode.F8 && !pressed && (pressed = true);
            yield return Until(() => pressed && (int)indexField.GetValue(null) == 6 && !BossSequenceController.BoundNail && !BossSequenceController.BoundShell,
                "same-room F8 repairs sequence counter and bindings");
            runtime.KeyPressed = _ => false; Flag("host.same-room");
        } else yield return Until(() => Has("host.same-room"), "same-room context repair observed");
        if (!host) { Enter("GG_Spa"); yield return Until(() => In("GG_Spa"), "guest returns to rest room for reverse test"); Freeze(); Flag("guest.spa-again"); }
        else yield return Until(() => Has("guest.spa-again"), "guest away for reverse godhome warp");
        if (!host) { yield return Warp("GG_Ghost_Galien"); Flag("guest.reverse-godhome"); }
        else yield return Until(() => Has("guest.reverse-godhome"), "guest reverse return verified");
        Mark("ARSENAL fireballs=" + fireballs + " bats=" + bats + " pillars=" + pillars + " pillarEntities=" + pillarEntities + " spikes=" + spikeSamples + " positions=" + positionSamples);
        foreach (var pair in matchedPositions) Mark("MATCHED POSITIONS " + pair.Key + "=" + pair.Value);
        Check(grimmAuthority || fireballs >= 3 && bats >= 1 && pillarEntities >= 1 && spikeSamples >= 1 && positionSamples >= 8,
            "replica observes fireballs, firebats, pillar spawn records, damaging spikes and synchronized positions");
        Check(grimmAuthority || new[] { EntityType.GrimmFireball, EntityType.GrimmBat, EntityType.GrimmFirebatPillar }
            .All(type => matchedPositions.TryGetValue(type, out var count) && count >= 3),
            "each dynamic Grimm projectile type has matching authority positions");
        if (Has(role + ".failed")) yield break;
        if (host) {
            Enter("Town"); yield return Until(() => In("Town"), "ordinary warp target after pantheon"); Freeze(); Flag("host.offline-warp-target");
            yield return Until(() => Has("guest.offline-warp-finished"), "accepted native warp completed despite disconnect");
        } else {
            yield return Until(() => Has("host.offline-warp-target"), "ordinary target ready");
            yield return Until(() => runtime.Peers.Values.Any(p => p.State?.Scene == "Town" && p.State.IsReady), "ordinary target presence");
            runtime.Selected = runtime.Peers.First(p => p.Value.State?.Scene == "Town").Key;
            yield return new WaitForSecondsRealtime(.5f);
            var armed = true; var before = fades;
            void DisconnectAcceptedWarp(On.GameManager.orig_BeginSceneTransition orig, GameManager gm, GameManager.SceneLoadInfo info) {
                orig(gm, info);
                if (!armed || info.EntryGateName != "dreamGate") return;
                armed = false; Field<NetClient>(engine.ClientManager, "_netClient").Disconnect();
            }
            On.GameManager.BeginSceneTransition += DisconnectAcceptedWarp;
            var pressed = false; runtime.KeyPressed = key => key == KeyCode.F8 && !pressed && (pressed = true);
            yield return Until(() => !armed && !runtime.Connected && In("Town"), "in-flight warp arrives after disconnect");
            runtime.KeyPressed = _ => false; On.GameManager.BeginSceneTransition -= DisconnectAcceptedWarp;
            yield return new WaitForSecondsRealtime(2);
            Check(fades > before && !HeroController.instance.controlReqlinquished && !BossSequenceController.IsInSequence &&
                !Resources.FindObjectsOfTypeAll<PlayMakerFSM>().Any(f => f.FsmName == "CameraFade" && f.gameObject.activeInHierarchy && f.ActiveStateName == "FadingOut"),
                "disconnect preserves accepted arrival fade and releases pantheon context");
            Flag("guest.offline-warp-finished");
        }
        if (Has(role + ".failed")) yield break;
        Check(nativeAudioFaults == 0, "native projectile audio survives godhome warps and authority changes");
        if (Has(role + ".failed")) yield break;
        Mark("FINAL PASS");
        Flag(role + ".done");
    }
    private void DumpGalien() {
        foreach (var e in Entities.Values.Where(e => e.Type == EntityType.GalienScythe)) {
            Dump(e);
            foreach (var f in e.Object.Host.GetComponents<PlayMakerFSM>()) {
                foreach (var v in f.FsmVariables.GameObjectVariables) Mark("GALIEN LOCAL " + f.FsmName + "/" + v.Name + "=" + v.Value?.name);
                foreach (var s in f.FsmStates.Where(s => s.Name.IndexOf("init", StringComparison.OrdinalIgnoreCase) >= 0))
                    foreach (var a in s.Actions) foreach (var field in a.GetType().GetFields()) {
                        if (field.FieldType == typeof(FsmString) || field.FieldType == typeof(FsmGameObject))
                            Mark("GALIEN INIT " + f.FsmName + "/" + s.Name + "/" + a.GetType().Name + "/" + field.Name + "=" + field.GetValue(a));
                    }
            }
        }
    }
    private IEnumerator GalienTransfer() {
        if (host) yield return Until(() => Has("guest.galien-initial"), "first Galien authority entered");
        Enter("GG_Ghost_Galien"); yield return Until(() => In("GG_Ghost_Galien") && manager.IsSceneHostDetermined, "native Galien ready"); Freeze();
        DumpGalien(); Flag(role + ".galien-initial");
        yield return Until(() => Has(host ? "guest.galien-initial" : "host.galien-initial"), "both Galien clients");
        yield return new WaitForSecondsRealtime(2);
        if (!host) { Enter("Town"); yield return Until(() => In("Town"), "Galien authority leaves"); Flag("guest.galien-left"); }
        else {
            yield return Until(() => Has("guest.galien-left") && manager.IsSceneHost, "Galien native authority transferred");
            DumpGalien(); yield return new WaitForSecondsRealtime(8);
            var scythe = Entities.Values.Single(e => e.Type == EntityType.GalienScythe);
            Check(scythe.Object.Host.GetComponents<PlayMakerFSM>().Single(f => f.FsmName == "Attack")
                .FsmVariables.GetFsmGameObject("Self").Value == scythe.Object.Host,
                "Galien scythe native Self points to its local host after transfer");
            Check(nativeAudioFaults == 0, "Galien native audio survives authority transfer"); Flag("host.galien-transferred");
        }
        if (!host) yield return Until(() => Has("host.galien-transferred"), "Galien transfer observed");
        if (Has(role + ".failed")) yield break;
        Mark("FINAL PASS"); Flag(role + ".done");
    }
    private IEnumerator Warp(string scene) {
        yield return Until(() => runtime.Peers.Values.Any(p => p.State?.Scene == scene && p.State.IsReady), "warp target presence");
        runtime.Selected = runtime.Peers.First(p => p.Value.State?.Scene == scene).Key;
        yield return new WaitForSecondsRealtime(.5f); // Current room heartbeat reaches the server before F8.
        var before = fades; var pressed = false;
        runtime.KeyPressed = key => key == KeyCode.F8 && !pressed && (pressed = true);
        yield return Until(() => pressed && In(scene), "F8 cross-room arrival"); runtime.KeyPressed = _ => false;
        yield return new WaitForSecondsRealtime(2); Freeze();
        Mark("WARP FADE before=" + before + " after=" + fades + " control=" + HeroController.instance.controlReqlinquished + " camera=" + GameCameras.instance.cameraController.mode);
        foreach (var f in Resources.FindObjectsOfTypeAll<PlayMakerFSM>().Where(f => f.FsmName.IndexOf("Fade", StringComparison.OrdinalIgnoreCase) >= 0 && f.gameObject.activeInHierarchy)) Mark("FADE FSM " + f.name + "/" + f.FsmName + "=" + f.ActiveStateName);
        Check(fades > before && !HeroController.instance.controlReqlinquished && GameCameras.instance.cameraController.mode != CameraController.CameraMode.FROZEN &&
            !Resources.FindObjectsOfTypeAll<PlayMakerFSM>().Any(f => f.FsmName == "CameraFade" && f.gameObject.activeInHierarchy && f.ActiveStateName == "FadingOut"),
            "F8 restores fade, input and camera");
    }
    private IEnumerator Arsenal(float seconds) {
        var end = Time.realtimeSinceStartup + seconds;
        while (Time.realtimeSinceStartup < end) { SampleArsenal(); yield return null; }
    }
    private IEnumerator ObserverFaults(Entity grimm) {
        var control = Control(grimm); var originalFsmName = control.FsmName;
        try {
            control.FsmName = "test-only renamed variant";
            Check(EntityRegistry.TryGetEntry(grimm.Object.Host, out var variant) && variant.Type == EntityType.Grimm,
                "health-bearing boss variant survives a renamed FSM");
            Check(!EntityRegistry.TryGetEntry(new[] {
                new EntityRegistryEntry { BaseObjectName = grimm.Object.Host.name, Type = EntityType.Grimm, FsmName = "missing1" },
                new EntityRegistryEntry { BaseObjectName = grimm.Object.Host.name, Type = EntityType.FalseKnight, FsmName = "missing2" }
            }, grimm.Object.Host, out _), "ambiguous boss name fallback is rejected");
        } finally { control.FsmName = originalFsmName; }
        var detached = new GameObject("Mantis Lord S"); detached.AddComponent<PlayMakerFSM>().FsmName = "Mantis Lord";
        Check(EntityRegistry.TryGetSpawnEntry(detached, out var lord) && lord.Type == EntityType.MantisLord,
            "detached registered child can be reconstructed as a dynamic root");
        Object.Destroy(detached);
        var pathRoot = new GameObject("probe/root#1%");
        var first = new GameObject("same/name#"); var second = new GameObject("same/name#");
        first.transform.SetParent(pathRoot.transform); second.transform.SetParent(pathRoot.transform); second.SetActive(false);
        Check(EntityReferences.Resolve(EntityReferences.Capture(first), false) == first &&
            EntityReferences.Resolve(EntityReferences.Capture(second), false) == second,
            "scene object references resolve duplicate inactive siblings and escaped names");
        Object.Destroy(pathRoot);
        Check(!EntityRegistry.TryGetEntry(null, out _), "missing registry target is ignored");
        var deadObject = new GameObject("test-only destroyed target"); Object.Destroy(deadObject); yield return null;
        Check(!EntityRegistry.TryGetEntry(deadObject, out _), "Unity destroyed registry target is ignored");
        var savedHost = grimm.Object.Host;
        try {
            grimm.Object.Host = deadObject;
            var spawnHook = typeof(EntityManager).GetMethod("OnGameObjectSpawned", BindingFlags.Instance | BindingFlags.NonPublic);
            Check(!(bool)spawnHook.Invoke(manager, new object[] { new EntitySpawnDetails { GameObject = null } }),
                "missing spawn result is not confused with a destroyed registered entity");
        } finally { grimm.Object.Host = savedHost; }
        var scale = new SetScale(); scale.Reset(); scale.Init(Control(grimm).FsmStates[0]);
        scale.gameObject = new FsmOwnerDefault { OwnerOption = OwnerDefaultOption.SpecifyGameObject, GameObject = new FsmGameObject() };
        Check(!EntityFsmActions.GetNetworkDataFromAction(new EntityNetworkData(), scale), "missing SetScale target produces no packet");
        var action = new GetOwner();
        var native = 0; var following = 0;
        FsmActionHooks.RegisterFsmStateActionType(typeof(GetOwner), a => { if (ReferenceEquals(a, action)) throw new InvalidOperationException("TEST ONLY observer fault"); });
        FsmActionHooks.RegisterFsmStateActionType(typeof(GetOwner), a => { if (ReferenceEquals(a, action)) following++; });
        var hook = typeof(FsmActionHooks).GetMethod("OnActionEntered", BindingFlags.Static | BindingFlags.NonPublic);
        hook.Invoke(null, new object[] { new Action<FsmStateAction>(_ => native++), action });
        Check(native == 1 && following == 1, "faulting observer preserves native execution and following observers");
        var nativeFault = new InvalidOperationException("TEST ONLY native fault"); var propagated = false;
        try { hook.Invoke(null, new object[] { new Action<FsmStateAction>(_ => throw nativeFault), action }); }
        catch (TargetInvocationException e) { propagated = ReferenceEquals(e.InnerException, nativeFault); }
        Check(propagated && following == 1, "native action errors retain their original propagation");
        var original = new GameObject("test-only collider host"); var replica = new GameObject("test-only collider client");
        var h = original.AddComponent<BoxCollider2D>(); var c = replica.AddComponent<BoxCollider2D>();
        var component = new ColliderComponent(Field<NetClient>(engine.ClientManager, "_netClient"), 65000,
            new HostClientPair<GameObject> { Host = original, Client = replica }, new HostClientPair<Collider2D> { Host = h, Client = c });
        Object.Destroy(replica); yield return null;
        var data = new EntityNetworkData { Type = EntityComponentType.Collider }; data.Packet.Write(false);
        data.Packet = new Hkmp.Networking.Packet.Packet(data.Packet.ToArray());
        component.Update(data, true);
        Check(!h.enabled, "destroyed replica collider snapshot can still update dormant host");
        component.Destroy(); Object.Destroy(original);
    }
    private void SampleArsenal() {
        if (Time.realtimeSinceStartup < nextSample) return;
        nextSample = Time.realtimeSinceStartup + .1f;
        if (manager.IsSceneHost) {
            var records = Entities.Values.Where(e => e.Object.Host != null && e.Object.Host.activeInHierarchy &&
                (e.Type == EntityType.GrimmFireball || e.Type == EntityType.GrimmBat || e.Type == EntityType.GrimmFirebatPillar || e.Type == EntityType.GrimmSpikes)).Select(e => {
                    var p = e.Object.Host.transform.position;
                    return e.Id + ":" + p.x.ToString(System.Globalization.CultureInfo.InvariantCulture) + ":" + p.y.ToString(System.Globalization.CultureInfo.InvariantCulture);
                }).ToArray();
            using (var stream = new FileStream(Path.Combine(root, "arsenal.state"), FileMode.Create, FileAccess.Write, FileShare.ReadWrite))
            using (var writer = new StreamWriter(stream)) foreach (var record in records) writer.WriteLine(record);
            return;
        }
        pillarEntities = Math.Max(pillarEntities, Entities.Values.Count(e => e.Type == EntityType.GrimmFirebatPillar && e.Object.Client != null));
        foreach (var e in Entities.Values.Where(e => e.Object.Client != null && e.Object.Client.activeInHierarchy)) {
            if (e.Type == EntityType.GrimmFireball) {
                fireballs++;
                if (!firstSeen.TryGetValue(e.Id, out var birth)) firstSeen[e.Id] = birth = Time.realtimeSinceStartup;
                var body=e.Object.Client.GetComponent<Rigidbody2D>();
                if (Time.realtimeSinceStartup - birth > .15f && ((body!=null && (body.bodyType!=RigidbodyType2D.Kinematic || body.gravityScale!=0 || body.velocity!=Vector2.zero)) || e.Object.Client.GetComponent<GrimmballControl>() != null))
                    Check(false, "replica fireball cannot run local physics or collision shrink");
            }
            if (e.Type == EntityType.GrimmBat) bats++;
            if (e.Type == EntityType.GrimmFirebatPillar) pillars++;
            if (e.Type == EntityType.GrimmSpikes && e.Object.Client.GetComponentsInChildren<Collider2D>().Any(c => c.enabled && c.gameObject.activeInHierarchy)) spikeSamples++;
        }
        if (!File.Exists(Path.Combine(root, "arsenal.state"))) return;
        string[] lines;
        using (var stream = new FileStream(Path.Combine(root, "arsenal.state"), FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
        using (var reader = new StreamReader(stream)) lines = reader.ReadToEnd().Split('\n');
        foreach (var line in lines) {
            var parts = line.Split(':');
            if (parts.Length != 3 || !ulong.TryParse(parts[0], out var id) || !Entities.TryGetValue(id, out var e) || e.Object.Client == null || !e.Object.Client.activeInHierarchy) continue;
            var p = new Vector2(float.Parse(parts[1], System.Globalization.CultureInfo.InvariantCulture), float.Parse(parts[2], System.Globalization.CultureInfo.InvariantCulture));
            if (Vector2.Distance(e.Object.Client.transform.position, p) < 3) {
                positionSamples++;
                matchedPositions.TryGetValue(e.Type, out var count); matchedPositions[e.Type] = count + 1;
            }
        }
    }
}
