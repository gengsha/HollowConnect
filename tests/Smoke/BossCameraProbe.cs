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

// Unsaved native P5 entry. Observe camera ownership without overriding it.
public sealed class BossCameraProbe : MonoBehaviour {
    private string root, role, scene, scenario, first;
    private bool host;
    private CoopRuntime runtime;
    private Hkmp.Game.GameManager engine;
    private EntityManager manager;
    private readonly HashSet<int> dumped = new();
    private string previous;
    private float nextSample;
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
        SceneName = s, EntryGateName = s.StartsWith("GG_") ? "door_dreamEnter" : "left1",
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
    private void Observe() {
        if (runtime == null || manager == null || !In(scene)) return;
        var camera = GameCameras.instance.cameraController;
        var areas = Object.FindObjectsOfType<CameraLockArea>(true).Where(a => a.gameObject.scene.name == scene).ToArray();
        foreach (var area in areas) foreach (var fsm in area.GetComponents<PlayMakerFSM>()) Dump(fsm);
        foreach (var entity in Entities.Values.Where(e => e.Type == EntityType.FalseKnight))
            if (entity.Object.Host != null) foreach (var fsm in entity.Object.Host.GetComponents<PlayMakerFSM>()) Dump(fsm);
        var current = "authority=" + manager.IsSceneHost + " mode=" + camera.mode + " current=" + Field<CameraLockArea>(camera, "currentLockArea")?.name +
            " locks=" + string.Join(",", camera.lockZoneList.Select(a => a == null ? "null" : a.name)) +
            " areas=" + string.Join(",", areas.Select(a => a.name + ":" + a.gameObject.activeInHierarchy + ":" + string.Join("/", a.GetComponents<PlayMakerFSM>().Select(f => f.ActiveStateName))));
        if (current != previous || Time.realtimeSinceStartup >= nextSample) {
            previous = current; nextSample = Time.realtimeSinceStartup + 2;
            Mark("CAMERA " + current + " position=" + camera.transform.position + " hero=" + HeroController.instance.transform.position +
                " input=" + HeroController.instance.acceptingInput + " relinquished=" + HeroController.instance.controlReqlinquished);
        }
    }
    private IEnumerator Start() {
        root = Environment.GetEnvironmentVariable("HOLLOWCONNECT_SMOKE_ROOT"); role = Environment.GetEnvironmentVariable("HOLLOWCONNECT_SMOKE_ROLE"); host = role == "host";
        scene = Environment.GetEnvironmentVariable("HOLLOWCONNECT_CAMERA_SCENE") ?? "GG_Failed_Champion";
        scenario = Environment.GetEnvironmentVariable("HOLLOWCONNECT_CAMERA_CASE") ?? "diagnostic";
        first = Environment.GetEnvironmentVariable("HOLLOWCONNECT_CAMERA_FIRST") ?? "host";
        Mark("START boss-camera " + scene + " " + scenario);
        On.HeroController.TakeDamage += (orig, hero, source, side, damage, hazard) => { };
        On.HutongGames.PlayMaker.Fsm.ProcessEvent += (orig, fsm, ev, data) => {
            if (fsm.GameObject != null && fsm.GameObject.GetComponent<CameraLockArea>() != null)
                Mark("CAMERA EVENT " + fsm.GameObject.name + "/" + fsm.Name + " state=" + fsm.ActiveStateName + " event=" + ev?.Name + " sender=" + data?.SentByFsm?.GameObject?.name + "/" + data?.SentByFsm?.Name);
            orig(fsm, ev, data);
        };
        var stack = new Stack<IEnumerator>(); stack.Push(Run()); var deadline = DateTime.UtcNow.AddMinutes(4);
        while (stack.Count > 0) {
            bool more; object current = null;
            try {
                if (DateTime.UtcNow > deadline) throw new TimeoutException("Boss camera probe timeout");
                Observe(); more = stack.Peek().MoveNext(); if (more) current = stack.Peek().Current;
            } catch (Exception e) { Mark("FAIL " + e); Flag(role + ".failed"); yield break; }
            if (!more) { stack.Pop(); continue; }
            if (current is IEnumerator nested) stack.Push(nested); else yield return current;
        }
    }
    private IEnumerator Run() {
        yield return Until(() => Object.FindObjectOfType<CoopRuntime>() != null && GameManager.instance != null && UnityEngine.SceneManagement.SceneManager.GetActiveScene().name == "Menu_Title", "menu ready");
        runtime = Object.FindObjectOfType<CoopRuntime>(); engine = Field<Hkmp.Game.GameManager>(ModHooks.GetMod("HKMP"), "_gameManager"); manager = Field<EntityManager>(engine.ClientManager, "_entityManager");
        CoopMod.Settings.ShareProgress = true;
        Field<Hkmp.Game.Settings.ModSettings>(engine.ClientManager, "_modSettings").AuthKey = Hkmp.Util.AuthUtil.GenerateAuthKey();
        var pd = PlayerData.instance; pd.SetString("respawnScene", "Town"); pd.SetString("respawnMarkerName", "RestBench"); pd.SetInt("respawnType", 1);
        GameManager.instance.profileID = 99; GameManager.instance.StartCoroutine(GameManager.instance.RunContinueGame());
        yield return Until(() => In("Town"), "unsaved character ready");
        if (host) { engine.ServerManager.Start(29650, false); Flag("server.started"); } else yield return Until(() => Has("server.started"), "server started");
        typeof(Hkmp.Game.Client.ClientManager).GetField("_autoConnect", BindingFlags.Instance | BindingFlags.NonPublic).SetValue(engine.ClientManager, true);
        typeof(Hkmp.Game.Client.ClientManager).GetMethod("Connect", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(engine.ClientManager, new object[] { "127.0.0.1", 29650, "camera" + role });
        yield return Until(() => runtime.Connected, "connected"); Flag(role + ".connected"); yield return Until(() => Has((host ? "guest" : "host") + ".connected"), "both connected");
        BossSequenceController.Reset();
        var sequence = Resources.Load<BossSequence>("GG/Boss Sequence Tier 5");
        Require(sequence != null, "native fifth sequence asset");
        var index = Enumerable.Range(0, sequence.Count).First(i => sequence.GetSceneAt(i) == scene);
        BossSequenceController.SetupNewSequence(sequence, BossSequenceController.ChallengeBindings.None, "bossDoorStateTier5");
        typeof(BossSequenceController).GetField("bossIndex", BindingFlags.Static | BindingFlags.NonPublic).SetValue(null, index);
        Mark("P5 CONTEXT index=" + index + " scene=" + scene);
        if (role != first && scenario == "late") yield return Until(() => Has(first + ".native-intro-ended"), "authority finished intro before late entry");
        else if (role != first) yield return new WaitForSecondsRealtime(.8f);
        Enter(scene); yield return Until(() => In(scene), "native arena entry");
        yield return Until(() => manager.IsSceneHostDetermined && Entities.Count > 0, "native authority determined");
        Require(manager.IsSceneHost == (role == first) && BossSequenceController.IsInSequence, "requested authority in native P5 context");
        if (role == first && scenario == "late") {
            yield return Until(() => Object.FindObjectsOfType<CameraLockArea>(true).Any(a => a.gameObject.scene.name == scene && a.name.Contains("Intro") && !a.gameObject.activeInHierarchy), "authority native intro has ended");
            yield return new WaitForSecondsRealtime(2);
            Flag(role + ".native-intro-ended");
        }
        yield return new WaitForSecondsRealtime(16);
        Observe();
        Flag(role + ".observed"); yield return Until(() => Has((host ? "guest" : "host") + ".observed"), "both camera observations complete");
        if (scenario != "diagnostic") {
            yield return CheckCamera();
            yield return LocalBarrierEventChecks.Run(Require);
            if (scenario == "handoff") {
                Flag(role + ".before-handoff");
                yield return Until(() => Has((host ? "guest" : "host") + ".before-handoff"), "both checked camera before authority departure");
                if (role == first) { Enter("Town"); yield return Until(() => In("Town"), "authority leaves arena natively"); }
                else {
                    yield return Until(() => manager.IsSceneHost, "remaining participant takes authority");
                    yield return new WaitForSecondsRealtime(3);
                    yield return CheckCamera();
                }
            }
        }
        yield return ProbeShutdown.Run(engine, root, role);
        Mark("FINAL PASS boss-camera " + scenario + " " + scene); Flag(role + ".done");
    }
    private IEnumerator CheckCamera() {
        var camera = GameCameras.instance.cameraController;
        var hero = HeroController.instance;
        yield return Until(() => hero.acceptingInput && !hero.controlReqlinquished && camera.mode != CameraController.CameraMode.FROZEN, "native hero and camera ready");
        var areas = Object.FindObjectsOfType<CameraLockArea>(true).Where(a => a.gameObject.scene.name == scene).ToArray();
        var intro = areas.Where(a => a.GetComponents<PlayMakerFSM>().Any(f => f.FsmStates.SelectMany(s => s.Transitions).Any(t => t.EventName == "INTRO OFF" || t.EventName == "BATTLE STARTED"))).ToArray();
        Require(intro.Length > 0 && intro.All(a => !a.gameObject.activeInHierarchy && !camera.lockZoneList.Contains(a)), "native intro locks deactivate and release camera");
        Require(camera.lockZoneList.Any(a => a != null && a.gameObject.scene.name == scene && !intro.Contains(a)), "normal native arena camera remains active");
        if (scene != "GG_Failed_Champion") yield break;
        Require(Entities.Values.Where(e => e.Type == EntityType.FalseKnight).Any(e => e.Object.Host.GetComponents<PlayMakerFSM>().Any(f => f.FsmVariables.FindFsmBool("HollowConnect Intro Released")?.Value == true)), "versioned intro completion retained after release state");
        var start = hero.transform.position;
        // Move the actor within the floor; the actual camera controller must
        // follow it. Never set camera position/mode or disable lock objects.
        hero.transform.position = new Vector3(49, 27.4f, start.z);
        hero.GetComponent<Rigidbody2D>().velocity = Vector2.zero;
        yield return new WaitForSecondsRealtime(2);
        var left = camera.transform.position;
        hero.transform.position = new Vector3(68, 27.4f, start.z);
        hero.GetComponent<Rigidbody2D>().velocity = Vector2.zero;
        yield return Until(() => camera.transform.position.x > left.x + 4, "native camera follows actor across arena", 5);
        var view = camera.cam.WorldToViewportPoint(hero.transform.position);
        Require(view.x > 0 && view.x < 1 && view.y > 0 && view.y < 1, "actor stays visible in native arena camera");
        hero.transform.position = start;
        hero.GetComponent<Rigidbody2D>().velocity = Vector2.zero;
    }
}
