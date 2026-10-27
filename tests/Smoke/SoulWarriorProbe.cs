using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using Hkmp.Game.Client.Entity;
using HollowConnect;
using HutongGames.PlayMaker;
using Modding;
using UnityEngine;
using Object = UnityEngine.Object;

namespace HollowConnect.Smoke;

// Unsaved native Soul Warrior/Folly spawn, motion and real nail collision checks.
public sealed class SoulWarriorProbe : MonoBehaviour {
    private string root, role, scene, scenario, first;
    private bool host;
    private CoopRuntime runtime;
    private Hkmp.Game.GameManager engine;
    private EntityManager manager;
    private readonly HashSet<int> dumped = new();
    private readonly HashSet<ulong> verifiedDeaths = new();
    private static T Field<T>(object o, string n) => (T)o.GetType().GetField(n, BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public).GetValue(o);
    private Dictionary<ulong, Entity> Entities => Field<Dictionary<ulong, Entity>>(manager, "_entities");
    private void Mark(string s) { using var f = new FileStream(Path.Combine(root, role + ".log"), FileMode.Append, FileAccess.Write, FileShare.ReadWrite); using var w = new StreamWriter(f); w.WriteLine(DateTime.UtcNow.ToString("O") + " " + s); }
    private void Flag(string s) => File.WriteAllText(Path.Combine(root, s), "ready");
    private bool Has(string s) => File.Exists(Path.Combine(root, s));
    private void Require(bool ok, string s) { if (!ok) throw new Exception(s); Mark("PASS " + s); }
    private IEnumerator Until(Func<bool> f, string s, float seconds = 45) {
        var end = Time.realtimeSinceStartup + seconds;
        while (!f() && Time.realtimeSinceStartup < end) yield return null;
        Require(f(), s);
    }
    private bool In(string s) => runtime.Playable && GameManager.instance.sceneName == s;
    private void Enter(string s) => GameManager.instance.BeginSceneTransition(new GameManager.SceneLoadInfo {
        SceneName = s, EntryGateName = s.StartsWith("GG_") ? "door_dreamEnter" : "right1",
        Visualization = GameManager.SceneLoadVisualizations.Default, WaitForSceneTransitionCameraFade = true
    });
    private string Value(Fsm fsm, object value) {
        if (value is FsmOwnerDefault owner) return "target:" + fsm.GetOwnerDefaultTarget(owner)?.name;
        if (value is FsmEventTarget target) return target.target + ":" + (target.gameObject == null ? "" : fsm.GetOwnerDefaultTarget(target.gameObject)?.name) + "/" + target.fsmName?.Value;
        if (value is FunctionCall call) return string.Join(";", call.GetType().GetFields().Select(f => f.Name + "=" + f.GetValue(call)));
        if (value is FsmEvent ev) return ev.Name;
        if (value is NamedVariable variable) return variable.Name + ":" + variable.RawValue;
        return value?.ToString();
    }
    private void Dump(PlayMakerFSM f) {
        if (!dumped.Add(f.GetInstanceID())) return;
        using var w = new StreamWriter(Path.Combine(root, role + "-fsm.txt"), true);
        w.WriteLine("FSM " + f.name + "/" + f.FsmName + " enabled=" + f.enabled + " state=" + f.ActiveStateName);
        foreach (var s in f.FsmStates) {
            w.WriteLine("STATE " + s.Name + " transitions=" + string.Join(",", s.Transitions.Select(t => t.EventName + "->" + t.ToState)));
            foreach (var a in s.Actions) w.WriteLine("  " + a.GetType().Name + " enabled=" + a.Enabled + " " + string.Join(",", a.GetType().GetFields(BindingFlags.Instance | BindingFlags.Public).Select(field => field.Name + "=" + Value(f.Fsm, field.GetValue(a)))));
        }
    }
    private string sceneKind;
    private float nextTrace;
    private readonly HashSet<int> observedObjects = new();
    private void Observe() {
        if (manager == null || !In(scene) || Time.realtimeSinceStartup < nextTrace) return;
        nextTrace = Time.realtimeSinceStartup + (scenario == "diagnostic" ? 1 : 5);
        foreach (var fsm in Object.FindObjectsOfType<PlayMakerFSM>(true).Where(f => f.gameObject.scene.name == scene &&
            (f.name.Contains("Mage Balloon") || f.name.Contains("Mage Knight") || f.name == "Balloon Spawner" || f.FsmName == "Battle Control"))) {
            Dump(fsm);
            var go = fsm.gameObject; var e = EntityReferences.Owner(go); var h = go.GetComponent<HealthManager>();
            Mark("OBJECT id=" + go.GetInstanceID() + " name=" + EntityIdentity.Path(go.transform) + " entity=" + e?.Id + "/" + e?.Type +
                " active=" + go.activeInHierarchy + " fsm=" + fsm.FsmName + ":" + fsm.ActiveStateName + ":" + fsm.enabled +
                " health=" + h?.hp + ":" + h?.IsInvincible + " pos=" + go.transform.position +
                " renderer=" + go.GetComponentsInChildren<Renderer>(true).Count(r => r.enabled && !r.forceRenderingOff));
            if (observedObjects.Add(go.GetInstanceID())) Mark("COMPONENTS " + go.name + " " + string.Join(",", go.GetComponents<Component>().Select(c => c == null ? "missing" : c.GetType().Name)));
        }
    }
    private IEnumerator Start() {
        root = Environment.GetEnvironmentVariable("HOLLOWCONNECT_SMOKE_ROOT"); role = Environment.GetEnvironmentVariable("HOLLOWCONNECT_SMOKE_ROLE"); host = role == "host";
        sceneKind = Environment.GetEnvironmentVariable("HOLLOWCONNECT_FOLLY_SCENE") ?? "p5";
        scenario = Environment.GetEnvironmentVariable("HOLLOWCONNECT_FOLLY_CASE") ?? "diagnostic";
        first = Environment.GetEnvironmentVariable("HOLLOWCONNECT_FOLLY_FIRST") ?? "host";
        Mark("START soul-folly " + sceneKind + " " + scenario);
        On.HeroController.TakeDamage += (orig, hero, source, side, damage, hazard) => { };
        var stack = new Stack<IEnumerator>(); stack.Push(Run()); var deadline = DateTime.UtcNow.AddMinutes(4);
        while (stack.Count > 0) {
            bool more; object current = null;
            try {
                if (DateTime.UtcNow > deadline) throw new TimeoutException("Soul Folly probe timeout");
                Observe(); more = stack.Peek().MoveNext(); if (more) current = stack.Peek().Current;
            } catch (Exception e) { Mark("FAIL " + e); Flag(role + ".failed"); yield break; }
            if (!more) { stack.Pop(); continue; }
            if (current is IEnumerator nested) stack.Push(nested); else yield return current;
        }
    }
    private IEnumerator Run() {
        yield return Until(() => Object.FindObjectOfType<CoopRuntime>() != null && GameManager.instance != null && UnityEngine.SceneManagement.SceneManager.GetActiveScene().name == "Menu_Title", "menu ready");
        runtime = Object.FindObjectOfType<CoopRuntime>(); engine = Field<Hkmp.Game.GameManager>(ModHooks.GetMod("HKMP"), "_gameManager"); manager = Field<EntityManager>(engine.ClientManager, "_entityManager");
        CoopMod.Settings.ShareProgress = Environment.GetEnvironmentVariable("HOLLOWCONNECT_FOLLY_SHARED") != "0";
        Field<Hkmp.Game.Settings.ModSettings>(engine.ClientManager, "_modSettings").AuthKey = Hkmp.Util.AuthUtil.GenerateAuthKey();
        var pd = PlayerData.instance; pd.SetString("respawnScene", "Town"); pd.SetString("respawnMarkerName", "RestBench"); pd.SetInt("respawnType", 1);
        GameManager.instance.profileID = 99; GameManager.instance.StartCoroutine(GameManager.instance.RunContinueGame());
        yield return Until(() => In("Town"), "unsaved character ready");
        if (host) { engine.ServerManager.Start(29650, false); Flag("server.started"); } else yield return Until(() => Has("server.started"), "server started");
        typeof(Hkmp.Game.Client.ClientManager).GetField("_autoConnect", BindingFlags.Instance | BindingFlags.NonPublic).SetValue(engine.ClientManager, true);
        typeof(Hkmp.Game.Client.ClientManager).GetMethod("Connect", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(engine.ClientManager, new object[] { "127.0.0.1", 29650, "folly" + role });
        yield return Until(() => runtime.Connected, "connected"); Flag(role + ".connected"); yield return Until(() => Has((host ? "guest" : "host") + ".connected"), "both connected");
        BossSequenceController.Reset();
        if (sceneKind == "p5") {
            var sequence = Resources.Load<BossSequence>("GG/Boss Sequence Tier 5");
            Require(sequence != null, "native fifth sequence asset");
            var index = Enumerable.Range(0, sequence.Count).First(i => sequence.GetSceneAt(i).StartsWith("GG_Mage_Knight", StringComparison.Ordinal));
            scene = sequence.GetSceneAt(index);
            BossSequenceController.SetupNewSequence(sequence, BossSequenceController.ChallengeBindings.None, "bossDoorStateTier5");
            typeof(BossSequenceController).GetField("bossIndex", BindingFlags.Static | BindingFlags.NonPublic).SetValue(null, index);
            Mark("P5 CONTEXT index=" + index + " scene=" + scene);
        } else scene = sceneKind;
        if (role != first && scenario == "late") yield return Until(() => Has("native.summons"), "native summons exist before late entry");
        else if (role != first) yield return new WaitForSecondsRealtime(.8f);
        Enter(scene); yield return Until(() => In(scene), "native arena entry");
        yield return Until(() => manager.IsSceneHostDetermined && Entities.Count > 0 && engine.ClientManager.SceneSnapshotReady &&
            !GameManager.instance.IsInSceneTransition && HeroController.instance.acceptingInput, "native authority and scene snapshot ready");
        Mark("AUTHORITY requested=" + first + " actual=" + manager.IsSceneHost + " hero=" + HeroController.instance.transform.position);
        Require(manager.IsSceneHost == (role == first), "requested scene authority");
        foreach (var gate in Object.FindObjectsOfType<TransitionPoint>(true).Where(g => g.gameObject.scene.name == scene)) Mark("GATE " + gate.name + " pos=" + gate.transform.position);
        if (sceneKind != "p5") {
            foreach (var c in Object.FindObjectsOfType<Collider2D>(true).Where(c => c.gameObject.scene.name == scene &&
                (c.name.Contains("Battle") || c.name.Contains("battle") || c.name.Contains("Mage"))))
                Mark("ARENA COLLIDER " + EntityIdentity.Path(c.transform) + " " + c.bounds + " active=" + c.gameObject.activeInHierarchy + " trigger=" + c.isTrigger);
            // Enter the actual encounter volume. Do not force the battle or
            // summon FSM into an active state.
            var boss = Entities.Values.FirstOrDefault(e => e.Type == EntityType.SoulWarrior);
            if (boss != null) Hold(boss.Object.Host.transform.position + new Vector3(-6, -1, 0));
        }
        if (scenario == "diagnostic") {
            var end = Time.realtimeSinceStartup + 24;
            while (Time.realtimeSinceStartup < end) { Observe(); yield return null; }
            yield return Barrier("observed");
        } else {
            yield return Until(() => LiveFollies().Length >= 2, "two native summoned Folly become collidable", 35);
            if (role == first && scenario == "late") Flag("native.summons");
            yield return Barrier("summons");
            CheckPool();
            yield return Barrier("pool-checked");
            yield return CheckMotion("initial", true);
            yield return CheckNail("host-nail", "host", true);
            yield return CheckNail("guest-nail", "guest", true);
            if (scenario == "cleanup") {
                yield return Until(() => LiveFollies().Length > 0, "live summon exists before boss death", 15);
                yield return Barrier("before-boss-death");
                if (manager.IsSceneHost) {
                    var boss = Entities.Values.Single(e => e.Type == EntityType.SoulWarrior).Object.Host.GetComponent<HealthManager>();
                    Mark("BOSS DEATH native health boundary hp=" + boss.hp);
                    // This cleanup fixture injects lethal health damage only
                    // to the boss. Folly damage is separately covered above
                    // through real nail collisions from both participants.
                    boss.ApplyExtraDamage(boss.hp);
                    Flag("boss.defeated");
                }
                yield return Until(() => Has("boss.defeated") && LiveFollies().Length == 0, "native boss death clears summoned Folly", 15);
                yield return Barrier("boss-cleanup");
                // A completed health/death callback precedes the reliable
                // multiplayer settlement. Keep the fixture online until the
                // native victory handshake has actually finished.
                yield return Until(() => Field<uint>(runtime, "lastBossVictory") != 0 && Field<double>(runtime, "settlementUntil") == 0,
                    "actual boss settlement completes before fixture disconnect", 20);
                yield return Barrier("settlement-complete");
            }
            if (scenario == "handoff") {
                yield return Barrier("before-handoff");
                if (role == first) {
                    Enter("Town"); yield return Until(() => In("Town"), "authority leaves arena natively");
                    yield return Until(() => Has("handoff.checked"), "remaining participant verified summons after handoff");
                } else {
                    yield return Until(() => manager.IsSceneHost && engine.ClientManager.SceneSnapshotReady, "remaining participant takes authority");
                    yield return Until(() => LiveFollies().Length >= 2, "native summons continue after authority handoff", 25);
                    CheckPool();
                    yield return CheckMotion("handoff", false);
                    yield return CheckNail("handoff-nail", role, false);
                    Flag("handoff.checked");
                }
            }
        }
        yield return ProbeShutdown.Run(engine, root, role);
        Mark("FINAL PASS soul-folly " + scenario + " " + sceneKind); Flag(role + ".done");
    }

    private IEnumerator Barrier(string step) {
        Flag(role + "." + step);
        yield return Until(() => Has((host ? "guest" : "host") + "." + step), "both endpoints " + step);
    }
    private GameObject Visible(Entity e) => manager.IsSceneHost ? e.Object.Host : e.Object.Client;
    private static bool Live(GameObject go) => go != null && go.activeInHierarchy &&
        go.GetComponent<HealthManager>() is { hp: > 0, isDead: false } &&
        go.GetComponents<Collider2D>().Any(c => c.enabled);
    private Entity[] LiveFollies() => Entities.Values.Where(e => e.Type == EntityType.Folly && Live(Visible(e))).OrderBy(e => e.Id).ToArray();
    private void Hold(Vector3 position) {
        var hero = HeroController.instance;
        hero.AffectedByGravity(false); hero.transform.position = position;
        hero.GetComponent<Rigidbody2D>().velocity = Vector2.zero;
        Physics2D.SyncTransforms();
    }
    private void CheckPool() {
        var pool = Entities.Values.Single(e => e.Type == EntityType.CombatSpawner && e.Object.Host.name == "Balloons");
        if (sceneKind == "p5") {
            var spawner = Entities.Values.Single(e => e.Type == EntityType.CombatSpawner && e.Object.Host.name == "Balloon Spawner");
            Require(pool.Object.Host.transform.parent == spawner.Object.Host.transform && pool.Object.Client.transform.parent == spawner.Object.Client.transform,
                "native and replica summon pools have matching parents");
            Require(manager.IsSceneHost ? !spawner.Object.Client.activeInHierarchy : !spawner.Object.Host.activeInHierarchy,
                "only authoritative summon generator runs");
        } else {
            var battle = pool.Object.Host.transform.parent.gameObject;
            Require(battle.name == "Battle Scene v2" && LocalBattleCompletion.IsLocal(battle), "story battle triggers and completion remain local");
            Require(manager.IsSceneHost ? !pool.Object.Client.activeInHierarchy : !pool.Object.Host.activeInHierarchy,
                "only authoritative native summon pool can activate enemies");
            Require(battle.GetComponent<PlayMakerFSM>().ActiveStateName == "Battling", "story summon battle started through native trigger");
        }
        // A terminal snapshot retains the entity ID after Unity destroys its
        // replica. Previously verified nail deaths are not live pool members.
        var follies = Entities.Values.Where(e => e.Type == EntityType.Folly && !verifiedDeaths.Contains(e.Id)).ToArray();
        Require(follies.All(e => e.Object.Host != null && e.Object.Client != null), "unretired Folly retain both pool objects");
        Require(follies.Length >= 2 && follies.All(e => e.Object.Host.transform.IsChildOf(pool.Object.Host.transform) &&
            e.Object.Client.transform.IsChildOf(pool.Object.Client.transform)), "all Folly remain in their corresponding summon pools");
        var live = Object.FindObjectsOfType<HealthManager>().Where(h => h.gameObject.scene.name == scene && h.name.Contains("Mage Balloon") && Live(h.gameObject)).ToArray();
        Mark("LIVE POOL entities=" + string.Join(",", LiveFollies().Select(e => e.Id + ":" + Visible(e).gameObject.scene.name + ":" + Visible(e).GetInstanceID())));
        foreach (var h in live) {
            var owner = EntityReferences.Owner(h.gameObject);
            Mark("LIVE OBJECT object=" + h.GetInstanceID() + " go=" + h.gameObject.GetInstanceID() + " path=" + EntityIdentity.Path(h.transform) +
                " owner=" + owner?.Id + "/" + owner?.Type + " visible=" + (owner == null ? "none" : (Visible(owner) == null ? "destroyed" : Visible(owner).GetInstanceID().ToString())));
        }
        // The earlier spawn wait already proves two summons. Native contact
        // can retire one while the peer reaches this assertion, so validate
        // every currently live object without assuming a fixed wave count.
        Require(live.Length > 0 && live.All(h => EntityReferences.Owner(h.gameObject) is { Type: EntityType.Folly } e && Visible(e) == h.gameObject),
            "every visible collidable Folly is the current network entity with no stray copy");
        Mark("POOL registered=" + follies.Length + " live=" + live.Length + " authority=" + manager.IsSceneHost);
    }
    private IEnumerator Target(string step, bool both) {
        if (manager.IsSceneHost) {
            yield return Until(() => LiveFollies().Length > 0, step + " native target available", 20);
            File.WriteAllText(Path.Combine(root, step + ".target"), LiveFollies()[0].Id.ToString());
        }
        yield return Until(() => Has(step + ".target"), step + " target selected");
        var id = ulong.Parse(File.ReadAllText(Path.Combine(root, step + ".target")));
        yield return Until(() => Entities.TryGetValue(id, out var e) && Live(Visible(e)), step + " same live target ready");
        if (both) yield return Barrier(step + ".target-ready");
    }
    private IEnumerator CheckMotion(string step, bool both) {
        yield return Target(step, both);
        var id = ulong.Parse(File.ReadAllText(Path.Combine(root, step + ".target")));
        var go = Visible(Entities[id]); var start = go.transform.position;
        var bounds = go.GetComponent<Collider2D>().bounds;
        Hold(bounds.center + new Vector3(start.x < 46 ? 7 : -7, 0, 0));
        var travel = 0f; var last = start; var end = Time.realtimeSinceStartup + 3;
        while (Time.realtimeSinceStartup < end) {
            RequireLive(); travel += Vector3.Distance(last, go.transform.position); last = go.transform.position;
            HeroController.instance.GetComponent<Rigidbody2D>().velocity = Vector2.zero;
            yield return null;
        }
        Mark("MOTION step=" + step + " id=" + id + " from=" + start + " to=" + last + " travel=" + travel);
        Require(travel > .5f, step + " summoned Folly moves on this endpoint");
        if (both) yield return Barrier(step + ".motion");
        void RequireLive() { if (!Live(go)) throw new Exception(step + " target disappeared before motion measurement"); }
    }
    private IEnumerator CheckNail(string step, string attacker, bool both) {
        yield return Target(step, both);
        var id = ulong.Parse(File.ReadAllText(Path.Combine(root, step + ".target")));
        var go = Visible(Entities[id]); var health = go.GetComponent<HealthManager>(); var initial = health.hp;
        Mark("NAIL begin step=" + step + " attacker=" + attacker + " id=" + id + " hp=" + initial);
        if (role == attacker) {
            var hero = HeroController.instance; var end = Time.realtimeSinceStartup + 15; var strikes = 0;
            while (Live(go) && Time.realtimeSinceStartup < end) {
                var collider = go.GetComponents<Collider2D>().First(c => c.enabled);
                Hold(collider.bounds.center + new Vector3(-1.4f, -.4f, 0));
                hero.FaceRight(); hero.Attack(GlobalEnums.AttackDirection.normal); Physics2D.SyncTransforms(); strikes++;
                yield return new WaitForSecondsRealtime(.45f);
                Mark("NAIL strike=" + strikes + " hp=" + (health == null ? "destroyed" : health.hp.ToString()) + " active=" + (go != null && go.activeInHierarchy));
            }
            Require(!Live(go), step + " actual nail collision defeats summoned Folly");
            Flag(step + ".struck");
        } else yield return Until(() => Has(step + ".struck"), step + " teammate completed actual nail strikes", 20);
        yield return Until(() => !Live(go), step + " defeated Folly disappears on this endpoint", 8);
        if (manager.IsSceneHost) Require(health == null || health.isDead || health.hp <= 0,
            step + " native authoritative health confirms death");
        if (both) yield return Barrier(step + ".dead");
        verifiedDeaths.Add(id);
    }
}
