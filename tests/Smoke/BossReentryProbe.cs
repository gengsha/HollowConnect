using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using Hkmp.Game.Client.Entity;
using HollowConnect;
using HollowConnect.Core;
using HutongGames.PlayMaker.Actions;
using Modding;
using Newtonsoft.Json;
using UnityEngine;
using Object = UnityEngine.Object;

namespace HollowConnect.Smoke;

// Native arena, terminal death, reward and reversed-authority reentry regression.
public sealed class BossReentryProbe : MonoBehaviour {
    private string role, root, authority;
    private bool host;
    private CoopRuntime runtime;
    private Hkmp.Game.GameManager engine;
    private EntityManager manager;
    private readonly object logGate = new();
    private const string Scene = "Crossroads_09", Source = "mask:Crossroads_09/Heart Piece";
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
    private static T Field<T>(object value, string name) => (T)value.GetType().GetField(name, Private).GetValue(value);
    private void Log(string value) { lock (logGate) File.AppendAllText(Path.Combine(root, role + ".log"), DateTime.UtcNow.ToString("O") + " " + value + "\n"); }
    private void Flag(string value) => File.WriteAllText(Path.Combine(root, role + "." + value), "ready");
    private bool Has(string value) => File.Exists(Path.Combine(root, value));
    private IEnumerator Until(Func<bool> condition, string label, float seconds = 40) {
        var end = Time.realtimeSinceStartup + seconds;
        while (!condition() && Time.realtimeSinceStartup < end) yield return null;
        if (!condition()) { Snapshot("timeout-" + label); throw new Exception("Fixture prerequisite: " + label); }
        Log("PASS prerequisite " + label);
    }
    private IEnumerator Together(string step) {
        Flag(step); yield return Until(() => Has((host ? "guest." : "host.") + step), "both " + step);
    }
    private bool In(string scene) => HeroController.instance != null && HeroController.instance.isHeroInPosition &&
        !GameManager.instance.IsInSceneTransition && GameManager.instance.sceneName == scene;
    private void Freeze() {
        var hero = HeroController.instance; hero.takeNoDamage = true; hero.RelinquishControl();
        var body = hero.GetComponent<Rigidbody2D>(); body.gravityScale = 0; body.velocity = Vector2.zero;
    }
    private IEnumerator Room(string scene, string firstRole) {
        if (role != firstRole) yield return Until(() => Has(firstRole + ".entered-" + scene), "selected player first " + scene);
        GameManager.instance.BeginSceneTransition(new GameManager.SceneLoadInfo { SceneName = scene, EntryGateName = "left1",
            Visualization = GameManager.SceneLoadVisualizations.Default, WaitForSceneTransitionCameraFade = true });
        yield return Until(() => In(scene), "native scene " + scene);
        Freeze();
        // Clear the native entrance trigger after the original arrival. Keep
        // all native progression and FSMs intact while waiting for the peer.
        HeroController.instance.transform.position += Vector3.right * 4;
        Physics2D.SyncTransforms();
        yield return Until(() => manager.IsSceneHostDetermined && engine.ClientManager.SceneSnapshotReady, "scene authority ready " + scene);
        Flag("entered-" + scene); yield return Together("ready-" + scene);
    }
    private IEnumerable<PlayMakerFSM> Fs() => Object.FindObjectsOfType<PlayMakerFSM>(true).Where(f => f.gameObject.scene.name == Scene &&
        (f.name == "Battle Scene" || f.name == "Heart Piece" || f.name.Contains("Mawlek") || f.name.Contains("Corpse")));
    private PlayMakerFSM Shard() => Fs().SingleOrDefault(f => f.name == "Heart Piece" && f.FsmName == "Heart Container Control");
    private bool Saved(string id) => SceneData.instance.FindMyState(new PersistentBoolData { sceneName = Scene, id = id })?.activated == true;
    private void Snapshot(string label) {
        var pd = PlayerData.instance;
        var state = new { label, role, authority, scene = GameManager.instance?.sceneName, shared = runtime != null && runtime.Shared,
            connected = runtime != null && runtime.Connected, peers = runtime?.Peers.Count,
            mawlekDefeated = pd.GetBool("mawlekDefeated"), killedMawlek = pd.GetBool("killedMawlek"),
            pieces = pd.GetInt("heartPieces"), maxHealthBase = pd.GetInt("maxHealthBase"),
            claim = CoopMod.Statistics?.Resources?.Contains(Source) == true,
            bodySaved = Saved("Mawlek Body"), bodyCloneSaved = Saved("Mawlek Body(Clone)"), battleSaved = Saved("Battle Scene"), heartSaved = Saved("Heart Piece"),
            fsms = Fs().Select(f => new { name = f.name, fsm = f.FsmName, state = f.ActiveStateName, active = f.gameObject.activeInHierarchy }).ToArray(),
            health = Object.FindObjectsOfType<HealthManager>(true).Where(h => h.gameObject.scene.name == Scene && h.name.Contains("Mawlek"))
                .Select(h => new { h.name, h.hp, h.isDead, h.IsInvincible, active = h.gameObject.activeInHierarchy }).ToArray() };
        File.WriteAllText(Path.Combine(root, role + "." + label + ".json"), JsonConvert.SerializeObject(state, Formatting.Indented));
        Log("OBSERVATION " + JsonConvert.SerializeObject(state));
    }
    private IEnumerator Checks() {
        yield return Until(() => GameManager.instance != null && Object.FindObjectOfType<CoopRuntime>() != null &&
            UnityEngine.SceneManagement.SceneManager.GetActiveScene().name == "Menu_Title", "menu ready");
        runtime = Object.FindObjectOfType<CoopRuntime>(); engine = Field<Hkmp.Game.GameManager>(ModHooks.GetMod("HKMP"), "_gameManager");
        Hkmp.Logging.Logger.AddLogger(new ReentryLogger(Log));
        manager = Field<EntityManager>(engine.ClientManager, "_entityManager");
        Field<Hkmp.Game.Settings.ModSettings>(engine.ClientManager, "_modSettings").AuthKey = Hkmp.Util.AuthUtil.GenerateAuthKey();
        CoopMod.Settings.ShareProgress = Environment.GetEnvironmentVariable("HOLLOWCONNECT_REENTRY_PRIVATE") != "1"; CoopMod.Settings.EntryProtection = false; CoopMod.Settings.BossHealthScaling = false;
        CoopMod.Settings.LinkedHealth = false;
        var pd = PlayerData.instance; pd.SetString("respawnScene", "Town"); pd.SetString("respawnMarkerName", "RestBench"); pd.SetInt("respawnType", 1);
        GameManager.instance.profileID = 99; GameManager.instance.StartCoroutine(GameManager.instance.RunContinueGame());
        yield return Until(() => In("Town"), "fresh unsaved hero");
        if (pd.GetBool("atBench")) {
            var bench = Object.FindObjectsOfType<PlayMakerFSM>().Single(f => f.name == "RestBench" && f.FsmName == "Bench Control");
            yield return Until(() => bench.ActiveStateName == "Resting", "native initial bench ready");
            bench.Fsm.Event(bench.Fsm.ActiveState.Actions.OfType<ListenForJump>().Single().wasPressed);
            yield return Until(() => !pd.GetBool("atBench") && HeroController.instance.acceptingInput, "native bench get-up");
        }
        Freeze(); CoopMod.Statistics = new DeathStatistics();
        pd.SetBool("mawlekDefeated", false); pd.SetBool("killedMawlek", false); pd.SetInt("heartPieces", 0);
        pd.SetInt("maxHealthBase", 5); pd.SetInt("maxHealth", 5); pd.SetInt("health", 5);
        if (host) { engine.ServerManager.Start(29650, false); Flag("server-started"); }
        else yield return Until(() => Has("host.server-started"), "server started");
        typeof(Hkmp.Game.Client.ClientManager).GetField("_autoConnect", Private).SetValue(engine.ClientManager, true);
        typeof(Hkmp.Game.Client.ClientManager).GetMethod("Connect", Private).Invoke(engine.ClientManager, new object[] { "127.0.0.1", 29650, "mawlek" + role });
        yield return Until(() => runtime.Connected && runtime.Shared == CoopMod.Settings.ShareProgress && runtime.Peers.Count == 1, "shared progress real UDP handshake");
        yield return Together("connected");
        yield return Room(Scene, authority);
        if (manager.IsSceneHost != (role == authority)) throw new Exception("Fixture authority differs from selection");
        Snapshot("initial");
        PersistenceIdentityChecks.Run(Log);

        foreach (var f in Fs().Where(f => f.name == "Battle Scene" || f.name == "Heart Piece"))
            File.AppendAllLines(Path.Combine(root, role + ".fsm.txt"), f.FsmStates.Select(s => f.name + "/" + f.FsmName + "/" + s.Name + " : " +
                string.Join(";", s.Actions.Select(a => a.GetType().Name + " " + string.Join(",", a.GetType().GetFields().Select(p => p.Name + "=" + p.GetValue(a)))))));
        var boss = Field<Dictionary<ulong, Entity>>(manager, "_entities").Values.Single(e => e.Type == EntityType.BroodingMawlek);
        var body = role == authority ? boss.Object.Host : boss.Object.Client;
        var nativeItem = boss.Object.Host.GetComponent<PersistentBoolItem>();
        var replicaItem = boss.Object.Client.GetComponent<PersistentBoolItem>();
        Check(nativeItem != null && replicaItem != null && replicaItem.persistentBoolData.id == "Mawlek Body" &&
            replicaItem.persistentBoolData.sceneName == Scene, "replica uses native scene and persistence identity");
        if (Environment.GetEnvironmentVariable("HOLLOWCONNECT_REENTRY_NO_EFFECTS") == "1" && role != authority)
            Object.DestroyImmediate(body.GetComponent<EnemyDeathEffects>());

        var trigger = body.GetComponentsInChildren<Collider2D>(true).SingleOrDefault(c => c.name == "Alert Range New" && c.isTrigger);
        if (trigger == null || !trigger.enabled || !trigger.gameObject.activeInHierarchy)
            throw new Exception("Fixture missing native active Mawlek Alert Range New: " + string.Join(",", body.GetComponentsInChildren<Collider2D>(true).Select(c => c.name + "/" + c.enabled + "/" + c.gameObject.activeInHierarchy)));
        Log("NATIVE ENTRY alert=" + trigger.bounds + " authority=" + manager.IsSceneHost);
        HeroController.instance.transform.position = trigger.bounds.center; Physics2D.SyncTransforms();
        yield return Together("triggered");
        yield return Until(() => body != null && body.activeInHierarchy && body.GetComponent<HealthManager>() is { hp: > 0, IsInvincible: false }, "native Mawlek fight active", 50);
        yield return Until(() => Fs().Any(f => f.name == "Battle Scene" && f.FsmName == "Battle Control" && f.ActiveStateName == "Start"),
            "native local battle starts before boss defeat");
        Snapshot("fighting"); yield return Together("fighting");
        if (role == authority) { var health = body.GetComponent<HealthManager>(); Log("NATIVE DAMAGE hp=" + health.hp); health.ApplyExtraDamage(health.hp + 100); Flag("killed"); }
        else yield return Until(() => Has(authority + ".killed"), "authority native damage");
        yield return new WaitForSecondsRealtime(12);
        Snapshot("after-death");
        Check(Saved("Mawlek Body"), "terminal death saved under native body identity");
        Check(!Saved("Mawlek Body(Clone)"), "no replica-name death record");
        yield return Together("after-death");
        foreach (var actor in new[] { authority, authority == "host" ? "guest" : "host" }) {
            if (!runtime.Shared && actor == authority && role != actor)
                Check(!CoopMod.Statistics.Resources.Contains(Source) && pd.GetInt("heartPieces") == 0,
                    "personal fragment remains unclaimed before own native pickup");
            if (role == actor) {
                var shard = Shard();
                if (shard != null && shard.gameObject.activeInHierarchy && shard.ActiveStateName == "Idle") {
                    HeroController.instance.RegainControl(); HeroController.instance.transform.position = shard.transform.position;
                    HeroController.instance.GetComponent<Rigidbody2D>().velocity = Vector2.zero; Physics2D.SyncTransforms();
                    Log("NATIVE COLLISION shard by " + actor);
                    yield return new WaitForSecondsRealtime(.2f);
                    yield return Until(() => CoopMod.Statistics.Resources.Contains(Source) && pd.GetInt("heartPieces") == 1, "native shard numerical award", 15);
                    var service = typeof(CoopRuntime).GetField("resources", Private).GetValue(runtime);
                    yield return Until(() => HeroController.instance.acceptingInput && !HeroController.instance.controlReqlinquished && !pd.GetBool("disablePause") &&
                        (bool)service.GetType().GetProperty("Idle", Private).GetValue(service), "native shard restores input and service idle", 15);
                } else if (runtime.Shared && CoopMod.Statistics.Resources.Contains(Source) && pd.GetInt("heartPieces") == 1)
                    Log("PASS peer's shared native pickup already awarded exactly one fragment");
                else throw new Exception("Native fragment unavailable for " + actor + ": " + shard?.ActiveStateName);
            }
            yield return Together("collected-" + actor); yield return new WaitForSecondsRealtime(5); Snapshot("after-pickup-" + actor);
            if (!runtime.Shared && actor == authority && role != actor)
                Check(!CoopMod.Statistics.Resources.Contains(Source) && pd.GetInt("heartPieces") == 0 &&
                    Shard() != null && Shard().gameObject.activeInHierarchy && Shard().ActiveStateName == "Idle",
                    "peer pickup leaves own personal fragment visible and unclaimed");
        }
        Check(CoopMod.Statistics.Resources.Contains(Source) && pd.GetInt("heartPieces") == 1,
            "exactly one native fragment and source receipt after both pickups");
        var piecesBeforeReentry = pd.GetInt("heartPieces");
        var receiptBeforeReentry = CoopMod.Statistics.Resources.Contains(Source);
        yield return Room("Town", authority);
        // Remove only fixture synchronization markers, never native state.
        File.Delete(Path.Combine(root, role + ".entered-" + Scene)); File.Delete(Path.Combine(root, role + ".ready-" + Scene));
        yield return Together("before-reentry");
        yield return Room(Scene, authority == "host" ? "guest" : "host"); yield return new WaitForSecondsRealtime(3);
        Snapshot("reentry");
        Check(manager.IsSceneHost == (role != authority), "former replica is scene authority on reentry");
        Check(!Object.FindObjectsOfType<HealthManager>(true).Any(h => h.gameObject.scene.name == Scene &&
            h.name.StartsWith("Mawlek Body", StringComparison.Ordinal) && h.gameObject.activeInHierarchy && !h.isDead && h.hp > 0),
            "no living Mawlek after reversed-authority native reentry");
        Check(Saved("Mawlek Body") && pd.GetInt("heartPieces") == piecesBeforeReentry &&
            CoopMod.Statistics.Resources.Contains(Source) == receiptBeforeReentry,
            "canonical death persists and reentry does not change native fragment or receipt");
        yield return Together("verified-reentry");
        yield return ProbeShutdown.Run(engine, root, role);
        Log("FINAL PASS native boss reentry, canonical persistence and single native reward; shared=" + runtime.Shared); Flag("done");
    }
    private void Check(bool condition, string label) { if (!condition) throw new Exception(label); Log("PASS " + label); }
    private IEnumerator Start() {
        role = Environment.GetEnvironmentVariable("HOLLOWCONNECT_SMOKE_ROLE"); root = Environment.GetEnvironmentVariable("HOLLOWCONNECT_SMOKE_ROOT"); host = role == "host";
        authority = Environment.GetEnvironmentVariable("HOLLOWCONNECT_MAWLEK_AUTHORITY") ?? "host";
        ModHooks.SetPlayerBoolHook += (key, value) => { if (key.IndexOf("mawlek", StringComparison.OrdinalIgnoreCase) >= 0) Log("PD BOOL " + key + "=" + value); return value; };
        var stack = new Stack<IEnumerator>(); stack.Push(Checks());
        while (stack.Count > 0) {
            object value = null; bool more;
            try { more = stack.Peek().MoveNext(); if (more) value = stack.Peek().Current; }
            catch (Exception e) { Log("FAIL " + e); File.WriteAllText(Path.Combine(root, role + ".failed"), e.ToString()); yield break; }
            if (!more) stack.Pop(); else if (value is IEnumerator nested) stack.Push(nested); else yield return value;
        }
    }
}

internal sealed class ReentryLogger : Hkmp.Logging.ILogger {
    private readonly Action<string> log;
    internal ReentryLogger(Action<string> log) { this.log = log; }
    public void Error(string value) => log("ENGINE ERROR " + value);
    public void Warn(string value) => log("ENGINE WARN " + value);
    public void Info(string value) => log("ENGINE INFO " + value);
    public void Fine(string value) { }
    public void Debug(string value) { }
}
