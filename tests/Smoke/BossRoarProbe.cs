using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using Hkmp.Game.Client.Entity;
using Hkmp.Game.Client.Entity.Action;
using Hkmp.Networking.Packet.Data;
using HollowConnect;
using HutongGames.PlayMaker;
using HutongGames.PlayMaker.Actions;
using Modding;
using MonoMod.RuntimeDetour;
using Newtonsoft.Json;
using UnityEngine;
using Object = UnityEngine.Object;

namespace HollowConnect.Smoke;

// Native boss/hero roar diagnosis. Never loads or writes a player save.
public sealed class BossRoarProbe : MonoBehaviour {
    private string role, root, scene, scenario;
    private bool host, hitBow;
    private CoopRuntime runtime;
    private Hkmp.Game.GameManager engine;
    private EntityManager manager;
    private readonly HashSet<int> dumped = new();
    private string lastState;
    private float nextSample;
    private bool sawExit, sawLock, heldStart, replayedStart, allowDamage;
    private Hook replayHook;
    private float nextVisual;
    private readonly List<object> visualSamples = new();
    private bool sawWave, visibleWave;
    private float roarExitAt;
    private object VisualTree(GameObject go) => go == null ? null : new {
        name = go.name, parts = go.GetComponentsInChildren<Transform>(true).Select(t => new {
            path = EntityIdentity.Path(t, go.transform), active = t.gameObject.activeSelf, visible = t.gameObject.activeInHierarchy,
            position = t.position.ToString(), renderers = t.GetComponents<Renderer>().Select(r => new { enabled = r.enabled }).ToArray(),
            sprites = t.GetComponents<tk2dBaseSprite>().Select(s => new { frame = s.spriteId, clip = s.GetComponent<tk2dSpriteAnimator>()?.CurrentClip?.name,
                playing = s.GetComponent<tk2dSpriteAnimator>()?.Playing }).ToArray()
        }).ToArray()
    };
    private void ObserveVisuals() {
        if (Time.realtimeSinceStartup < nextVisual) return;
        nextVisual = Time.realtimeSinceStartup + .05f;
        var effects = Object.FindObjectsOfType<PlayMakerFSM>(true).Where(f => f.name.StartsWith("Roar Wave Emitter", StringComparison.Ordinal) && f.gameObject.scene.name == scene).ToArray();
        visibleWave = effects.Any(f => f.GetComponentsInChildren<Renderer>(true).Any(r => r.name.StartsWith("wave ", StringComparison.Ordinal) && r.enabled && r.gameObject.activeInHierarchy));
        sawWave |= visibleWave;
        var boss = Entities.Values.FirstOrDefault(e => e.Type == EntityType.Grimm);
        visualSamples.Add(new { utc = DateTime.UtcNow, authority = manager.IsSceneHost,
            bowObjects = Object.FindObjectsOfType<Animator>(true).Where(a => a.gameObject.scene.name == scene && a.name.Contains("Grimm")).Select(a => new {
                path = EntityIdentity.Path(a.transform), active = a.gameObject.activeSelf, visible = a.gameObject.activeInHierarchy,
                owner = EntityReferences.Owner(a.gameObject)?.Id, tree = VisualTree(a.gameObject) }).ToArray(),
            nativeBoss = VisualTree(boss?.Object.Host), replicaBoss = VisualTree(boss?.Object.Client), effects = effects.Select(f => new {
            id = f.GetInstanceID(), name = f.name, enabled = f.enabled, state = f.ActiveStateName,
            owner = EntityReferences.Owner(f.gameObject)?.Id, replica = EntityReferences.IsReplica(f.gameObject),
            parts = f.GetComponentsInChildren<Transform>(true).Select(t => new {
                path = EntityIdentity.Path(t, f.transform), active = t.gameObject.activeSelf, visible = t.gameObject.activeInHierarchy,
                position = t.position.ToString(), components = t.GetComponents<UnityEngine.Component>().Select(c => c?.GetType().Name).ToArray(),
                renderers = t.GetComponents<Renderer>().Select(r => new { enabled = r.enabled,
                    color = r.sharedMaterial != null && r.sharedMaterial.HasProperty("_Color") ? r.sharedMaterial.color.ToString() : "" }).ToArray()
            }).ToArray()
        }).ToArray() });
    }
    private static T Field<T>(object o, string n) => (T)o.GetType().GetField(n, BindingFlags.Instance | BindingFlags.NonPublic).GetValue(o);
    private Dictionary<ulong, Entity> Entities => Field<Dictionary<ulong, Entity>>(manager, "_entities");
    private void Mark(string s) { using var f = new FileStream(Path.Combine(root, role + ".log"), FileMode.Append, FileAccess.Write, FileShare.ReadWrite); using var w = new StreamWriter(f); w.WriteLine(DateTime.UtcNow.ToString("O") + " " + s); }
    private void Flag(string n, string s = "ready") => File.WriteAllText(Path.Combine(root, n), s);
    private bool Has(string n) => File.Exists(Path.Combine(root, n));
    private void Require(bool ok, string s) {
        if (!ok) {
            var hero = HeroController.instance;
            Mark("DIAGNOSTIC scene=" + GameManager.instance?.sceneName + " input=" + hero?.acceptingInput + " relinquished=" + hero?.controlReqlinquished +
                " roar=" + hero?.gameObject.LocateMyFSM("Roar Lock")?.ActiveStateName + " transition=" + hero?.transitionState + " loading=" + GameManager.instance?.IsInSceneTransition);
            throw new Exception(s);
        }
        Mark("PASS " + s);
    }
    private IEnumerator Until(Func<bool> f, string s, float seconds = 45) {
        var end = Time.realtimeSinceStartup + seconds;
        while (!f() && Time.realtimeSinceStartup < end) yield return null;
        Require(f(), s);
    }
    private bool In(string s) => runtime.Playable && GameManager.instance.sceneName == s;
    private void Enter(string s) => GameManager.instance.BeginSceneTransition(new GameManager.SceneLoadInfo {
        SceneName = s, EntryGateName = s.StartsWith("GG_") ? "door_dreamEnter" : "left1",
        Visualization = GameManager.SceneLoadVisualizations.Default, WaitForSceneTransitionCameraFade = true
    });
    private void Position(float x, float y) {
        var h = HeroController.instance;
        h.transform.position = new Vector3(x, y, h.transform.position.z);
        h.GetComponent<Rigidbody2D>().velocity = Vector2.zero;
    }
    private void Trace(On.HutongGames.PlayMaker.Fsm.orig_ProcessEvent orig, Fsm fsm, FsmEvent ev, FsmEventData data) {
        if (fsm.Name == "Roar Lock" && ev?.Name == "ROAR EXIT") { if (!sawExit) roarExitAt = Time.realtimeSinceStartup; sawExit = true; }
        if (ev != null && ev.Name.Contains("ROAR") && (fsm.Name == "Roar Lock" || fsm.GameObject?.name.Contains("Boss") == true))
            Mark("EVENT " + fsm.GameObject?.name + "/" + fsm.Name + "=" + fsm.ActiveStateName + " event=" + ev.Name +
                " sender=" + data?.SentByFsm?.GameObject?.name + "/" + data?.SentByFsm?.Name);
        orig(fsm, ev, data);
    }
    private IEnumerator DelayStart(Action<EntityNetworkData, FsmStateAction> orig, EntityNetworkData data, FsmStateAction action) {
        yield return Until(() => sawExit, "native roar exit reached before held start");
        yield return new WaitForSecondsRealtime(.2f);
        Mark("FIXTURE replay delayed ROAR ENTER after native ROAR EXIT");
        orig(data, action); replayedStart = true;
    }
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
            foreach (var a in s.Actions) w.WriteLine("  " + a.GetType().Name + " " + string.Join(",", a.GetType().GetFields(BindingFlags.Instance | BindingFlags.Public).Select(field => field.Name + "=" + Value(f.Fsm, field.GetValue(a)))));
        }
    }
    private void Observe() {
        if (manager == null || !In(scene)) return;
        ObserveVisuals();
        var h = HeroController.instance;
        var roar = h.GetComponents<PlayMakerFSM>().Single(f => f.FsmName == "Roar Lock");
        if (roar.ActiveStateName == "Lock Grounded" || roar.ActiveStateName == "Lock Air") sawLock = true;
        Dump(roar);
        foreach (var f in Object.FindObjectsOfType<PlayMakerFSM>().Where(f => f.name.Contains("Roar"))) Dump(f);
        var boss = Entities.Values.FirstOrDefault(e => e.Type == EntityType.Grimm || e.Type == EntityType.TheCollector);
        var go = manager.IsSceneHost ? boss?.Object.Host : boss?.Object.Client;
        if (go == null) return;
        if (scenario == "delayed-start" && manager.IsSceneHost && !hitBow) roar.FsmVariables.FindFsmBool("No Roar").Value = true;
        foreach (var f in go.GetComponentsInChildren<PlayMakerFSM>(true)) Dump(f);
        var states = string.Join(",", go.GetComponents<PlayMakerFSM>().Select(f => f.FsmName + "=" + f.ActiveStateName));
        var current = states + " roar=" + roar.ActiveStateName;
        if (current != lastState || Time.realtimeSinceStartup > nextSample) {
            lastState = current; nextSample = Time.realtimeSinceStartup + 2;
            Mark("STATE authority=" + manager.IsSceneHost + " " + current + " hero=" + h.transform.position +
                " input=" + h.acceptingInput + " relinquished=" + h.controlReqlinquished + " noDamage=" + h.takeNoDamage +
                " paused=" + GameManager.instance.isPaused + " roarVars=" + string.Join(",", roar.FsmVariables.GameObjectVariables.Select(v => v.Name + "=" + v.Value?.name)));
        }
        var hitReady = manager.IsSceneHost && go.GetComponents<PlayMakerFSM>().Any(f => f.ActiveStateName == "GG Bow Wait" || f.ActiveStateName == "Bow");
        if (!hitBow && scene == "GG_Grimm" && hitReady && scenario != "bow-normal") {
            hitBow = true;
            Mark("FIXTURE native nail hit during Bow");
            go.GetComponent<HealthManager>().Hit(new HitInstance { Source = h.gameObject, AttackType = AttackTypes.Nail, DamageDealt = 1, Multiplier = 1, IgnoreInvulnerable = true });
        }
    }
    private IEnumerator Start() {
        role = Environment.GetEnvironmentVariable("HOLLOWCONNECT_SMOKE_ROLE"); root = Environment.GetEnvironmentVariable("HOLLOWCONNECT_SMOKE_ROOT"); host = role == "host";
        scene = Environment.GetEnvironmentVariable("HOLLOWCONNECT_ROAR_SCENE") ?? "GG_Grimm";
        scenario = Environment.GetEnvironmentVariable("HOLLOWCONNECT_ROAR_CASE") ?? "diagnostic";
        Mark("START boss-roar scene=" + scene);
        CoopMod.Settings.ShareProgress = true;
        On.HutongGames.PlayMaker.Fsm.ProcessEvent += Trace;
        // Keep native hero damage flags intact; isolate the post-roar damage
        // assertion from unrelated projectiles killing the unsaved character.
        On.HeroController.TakeDamage += (orig, hero, source, side, damage, hazard) => { if (allowDamage) orig(hero, source, side, damage, hazard); };
        if (scenario == "delayed-start") {
            replayHook = new Hook(typeof(EntityFsmActions).GetMethod("ApplyNetworkDataFromAction", BindingFlags.Static | BindingFlags.Public,
                null, new[] { typeof(EntityNetworkData), typeof(FsmStateAction) }, null),
                (Action<Action<EntityNetworkData, FsmStateAction>, EntityNetworkData, FsmStateAction>)((orig, data, action) => {
                    if (!heldStart && action is SendEventByName cue && cue.sendEvent.Value == "ROAR ENTER") {
                        heldStart = true; Mark("FIXTURE hold reliable ROAR ENTER; other native events continue");
                        StartCoroutine(DelayStart(orig, data, action)); return;
                    }
                    orig(data, action);
                }));
        }
        if (scenario == "missing-exit") {
            replayHook = new Hook(typeof(EntityFsmActions).GetMethod("ApplyNetworkDataFromAction", BindingFlags.Static | BindingFlags.Public,
                null, new[] { typeof(EntityNetworkData), typeof(FsmStateAction) }, null),
                (Action<Action<EntityNetworkData, FsmStateAction>, EntityNetworkData, FsmStateAction>)((orig, data, action) => {
                    if (action is SendEventByName cue && cue.sendEvent.Value == "ROAR EXIT") {
                        Mark("FIXTURE omit reliable ROAR EXIT impulse; current state snapshot continues"); return;
                    }
                    orig(data, action);
                }));
        }
        if (scenario == "visual-missing-end" || scenario == "visual-late-create") {
            replayHook = new Hook(typeof(EntityFsmActions).GetMethod("ApplyNetworkDataFromAction", BindingFlags.Static | BindingFlags.Public,
                null, new[] { typeof(EntityNetworkData), typeof(FsmStateAction) }, null),
                (Action<Action<EntityNetworkData, FsmStateAction>, EntityNetworkData, FsmStateAction>)((orig, data, action) => {
                    if (scenario == "visual-missing-end" && action is SendEventByName cue && cue.sendEvent.Value == "END" && action.State.Name == "Roar End") {
                        Mark("FIXTURE omit roar emitter END impulse; ordered boss state continues"); return;
                    }
                    if (scenario == "visual-late-create" && action is CreateObject create && create.gameObject.Value.name == "Roar Wave Emitter") {
                        heldStart = true; Mark("FIXTURE hold roar emitter creation until after native roar exit");
                        StartCoroutine(DelayStart(orig, data, action)); return;
                    }
                    orig(data, action);
                }));
        }
        var stack = new Stack<IEnumerator>(); stack.Push(Run()); var deadline = DateTime.UtcNow.AddMinutes(4);
        while (stack.Count > 0) {
            bool more; object current = null;
            try {
                if (DateTime.UtcNow > deadline) throw new TimeoutException("Boss roar probe timeout");
                Observe(); more = stack.Peek().MoveNext(); if (more) current = stack.Peek().Current;
            } catch (Exception e) { Mark("FAIL " + e); Flag(role + ".failed", e.ToString()); yield break; }
            if (!more) { stack.Pop(); continue; }
            if (current is IEnumerator nested) stack.Push(nested); else yield return current;
        }
    }
    private IEnumerator Run() {
        yield return Until(() => Object.FindObjectOfType<CoopRuntime>() != null && GameManager.instance != null && UnityEngine.SceneManagement.SceneManager.GetActiveScene().name == "Menu_Title", "menu ready");
        runtime = Object.FindObjectOfType<CoopRuntime>(); engine = Field<Hkmp.Game.GameManager>(ModHooks.GetMod("HKMP"), "_gameManager"); manager = Field<EntityManager>(engine.ClientManager, "_entityManager");
        Field<Hkmp.Game.Settings.ModSettings>(engine.ClientManager, "_modSettings").AuthKey = Hkmp.Util.AuthUtil.GenerateAuthKey();
        var pd = PlayerData.instance; pd.SetString("respawnScene", "Town"); pd.SetString("respawnMarkerName", "RestBench"); pd.SetInt("respawnType", 1);
        GameManager.instance.profileID = 99; GameManager.instance.StartCoroutine(GameManager.instance.RunContinueGame());
        yield return Until(() => In("Town"), "unsaved character ready");
        if (PlayerData.instance.GetBool("atBench")) {
            var bench = Object.FindObjectsOfType<PlayMakerFSM>().Single(f => f.name == "RestBench" && f.FsmName == "Bench Control");
            yield return Until(() => bench.ActiveStateName == "Resting", "initial native bench ready");
            bench.Fsm.Event(bench.Fsm.ActiveState.Actions.OfType<ListenForJump>().Single().wasPressed);
            yield return Until(() => !PlayerData.instance.GetBool("atBench") && !HeroController.instance.GetComponent<Rigidbody2D>().isKinematic, "initial native bench get-up complete");
        }
        if (host) { engine.ServerManager.Start(29650, false); Flag("server.started"); } else yield return Until(() => Has("server.started"), "server started");
        typeof(Hkmp.Game.Client.ClientManager).GetField("_autoConnect", BindingFlags.Instance | BindingFlags.NonPublic).SetValue(engine.ClientManager, true);
        typeof(Hkmp.Game.Client.ClientManager).GetMethod("Connect", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(engine.ClientManager, new object[] { "127.0.0.1", 29650, "roar" + role });
        yield return Until(() => runtime.Connected, "connected"); Flag(role + ".connected"); yield return Until(() => Has((host ? "guest" : "host") + ".connected"), "both connected");
        BossSequenceController.Reset(); BossSceneController.SetupEvent = self => { self.BossLevel = 0; };
        if (scene.StartsWith("GG_Collector", StringComparison.Ordinal)) {
            var sequence = Resources.Load<BossSequence>("GG/Boss Sequence Tier 5");
            Require(sequence != null, "native fifth sequence asset");
            Mark("SEQUENCE " + string.Join(",", Enumerable.Range(0, sequence.Count).Select(i => i + ":" + sequence.GetSceneAt(i))));
            var index = Enumerable.Range(0, sequence.Count).First(i => sequence.GetSceneAt(i) == scene);
            BossSequenceController.SetupNewSequence(sequence, BossSequenceController.ChallengeBindings.None, "bossDoorStateTier5");
            typeof(BossSequenceController).GetField("bossIndex", BindingFlags.Static | BindingFlags.NonPublic).SetValue(null, index);
        }
        var first = Environment.GetEnvironmentVariable("HOLLOWCONNECT_ROAR_FIRST") ?? "host";
        if (role != first) yield return new WaitForSecondsRealtime(scenario == "late" ? 2.5f : .8f);
        Enter(scene); yield return Until(() => In(scene), "arena entered");
        if (scene == "GG_Grimm") Position(80, 8);
        yield return Until(() => manager.IsSceneHostDetermined && Entities.Values.Any(e => e.Type == EntityType.Grimm || e.Type == EntityType.TheCollector), "boss authority ready");
        var initialAuthority = manager.IsSceneHost;
        Require(initialAuthority == (role == first), "requested first entrant owns native boss");
        if (scene.StartsWith("GG_Collector", StringComparison.Ordinal)) Require(BossSequenceController.IsInSequence, "Collector runs in the native fifth pantheon context");
        Flag(role + ".arena"); yield return Until(() => Has((host ? "guest" : "host") + ".arena"), "both in arena");
        if (scenario == "handoff") {
            yield return Until(() => sawLock, "hero locked by native roar before handoff", 12);
            if (initialAuthority) {
                Flag("handoff.started"); Enter("Town");
                yield return Until(() => In("Town"), "old authority leaves roar room");
                yield return Until(() => HeroController.instance.acceptingInput && !HeroController.instance.controlReqlinquished,
                    "native destination entry restores old authority control", 8);
            } else {
                yield return Until(() => Has("handoff.started") && manager.IsSceneHost, "survivor takes authority during roar");
            }
        }
        if (!scenario.Contains("diagnostic") && scenario != "bow-normal") {
            if (scenario != "handoff" || !initialAuthority) yield return Until(() => sawExit, "native roar exit observed", 18);
            if (!manager.IsSceneHost && scenario == "delayed-start") yield return Until(() => replayedStart, "held start delivered after exit", 5);
            yield return new WaitForSecondsRealtime(.5f);
            Require(sawLock || scenario == "delayed-start" && initialAuthority, "expected participant actually entered native roar lock");
            var hero = HeroController.instance;
            var roar = hero.GetComponents<PlayMakerFSM>().Single(f => f.FsmName == "Roar Lock");
            Require(roar.ActiveStateName == "Detect" && hero.acceptingInput && !hero.controlReqlinquished,
                "native roar has ended and hero control is usable");
            yield return Until(() => !runtime.EntryProtected, "native entry protection expires after control recovery", 8);
            var health = PlayerData.instance.GetInt("health");
            var blue = PlayerData.instance.GetInt("healthBlue");
            Mark("DAMAGE before health=" + health + " blue=" + blue + " bossLevel=" + BossSceneController.Instance?.BossLevel +
                " mode=" + hero.damageMode + " invulnerable=" + hero.cState.invulnerable + " invincible=" + PlayerData.instance.GetBool("isInvincible") +
                " bossTransition=" + BossSceneController.IsTransitioning);
            allowDamage = true;
            try { hero.TakeDamage(null, GlobalEnums.CollisionSide.left, 1, 1); } finally { allowDamage = false; }
            Mark("DAMAGE after health=" + PlayerData.instance.GetInt("health") + " blue=" + PlayerData.instance.GetInt("healthBlue"));
            Require(PlayerData.instance.GetInt("health") + PlayerData.instance.GetInt("healthBlue") < health + blue, "hero takes real damage after roar");
            if (!scenario.StartsWith("visual", StringComparison.Ordinal)) { replayHook?.Dispose(); replayHook = null; }
            if (!manager.IsSceneHost) {
                // A late end from this boss must not unlock a later native
                // roar owned by a different local continuation.
                roar.SendEvent("ROAR ENTER");
                Require(roar.ActiveStateName == "Lock Grounded" || roar.ActiveStateName == "Lock Air", "separate native roar owns control");
                var entity = Entities.Values.First(e => e.Type == EntityType.Grimm || e.Type == EntityType.TheCollector);
                var exit = entity.Object.Client.GetComponents<PlayMakerFSM>().SelectMany(f => f.FsmStates).SelectMany(s => s.Actions)
                    .OfType<SendEventByName>().First(a => a.sendEvent.Value == "ROAR EXIT");
                EntityFsmActions.ApplyNetworkDataFromAction(new EntityNetworkData(), (FsmStateAction)exit);
                Require(roar.ActiveStateName == "Lock Grounded" || roar.ActiveStateName == "Lock Air", "stale boss exit preserves separate native roar lock");
                roar.SendEvent("ROAR EXIT");
            }
            Flag(role + ".verified"); yield return Until(() => Has((host ? "guest" : "host") + ".verified"), "both heroes recovered");
        }
        var end = Time.realtimeSinceStartup + (scenario.Contains("diagnostic") || scenario == "bow-normal" || scenario.StartsWith("visual", StringComparison.Ordinal) ? 9 : 1);
        while (Time.realtimeSinceStartup < end) { Observe(); yield return null; }
        File.WriteAllText(Path.Combine(root, role + ".roar-visuals.json"), JsonConvert.SerializeObject(visualSamples));
        if (scene == "GG_Grimm" && !scenario.Contains("diagnostic") && scenario != "handoff") {
            if (scenario == "bow-normal") {
                Require(!sawWave && !sawLock, "normal bow completes without a rage roar");
            } else {
                Require(sawExit && Time.realtimeSinceStartup - roarExitAt >= .8f, "native roar exit precedes visual cleanup assertion");
                Require(sawWave || !manager.IsSceneHost && scenario == "visual-late-create" && heldStart && replayedStart, "real native wave effect exercised");
                Require(!visibleWave, "no roar waves remain at the bow position after native exit");
            }
            Require(Object.FindObjectsOfType<Animator>(true).Where(a => a.gameObject.scene.name == scene && a.name == "Grimm Bow").All(a => !a.gameObject.activeInHierarchy),
                "separate native bow model is hidden after the introduction");
            Mark("PASS roar visual cleanup and native bow model lifecycle");
        }
        yield return ProbeShutdown.Run(engine, root, role);
        Mark("FINAL PASS boss-roar " + scenario + " " + scene); Flag(role + ".done");
    }
}
