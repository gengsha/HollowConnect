using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using Hkmp.Game.Client.Entity;
using HollowConnect;
using Modding;
using UnityEngine;
using Object = UnityEngine.Object;

namespace HollowConnect.Smoke;

// Observe native launches and motion, rather than accepting two equally broken
// copies as proof of a correct attack. No save is loaded or written.
public sealed class GrimmMotionProbe : MonoBehaviour {
    private string role, root, scene;
    private bool host;
    private CoopRuntime runtime;
    private Hkmp.Game.GameManager engine;
    private EntityManager manager;
    private int launches, preserved, moving, peerMatches;
    private float nextTrace;
    private float nextDiagnostic;
    private readonly List<Flight> flights = new();
    private readonly Dictionary<ulong, Vector2> batOrigins = new();
    private readonly HashSet<ulong> flyingBats = new();
    private sealed class Flight {
        public GrimmballControl Control;
        public float Force, Time;
        public Vector2 Position;
        public bool Checked, Moved;
    }
    private static T Field<T>(object o, string n) => (T)o.GetType().GetField(n, BindingFlags.Instance | BindingFlags.NonPublic).GetValue(o);
    private Dictionary<ulong, Entity> Entities => Field<Dictionary<ulong, Entity>>(manager, "_entities");
    private readonly object logLock = new();
    private sealed class Log : Hkmp.Logging.ILogger {
        private readonly GrimmMotionProbe probe;
        internal Log(GrimmMotionProbe probe) { this.probe = probe; }
        public void Info(string s) => probe.Mark("ENGINE " + s);
        public void Fine(string s) { }
        public void Debug(string s) { }
        public void Warn(string s) => probe.Mark("WARN " + s);
        public void Error(string s) => probe.Mark("ERROR " + s);
    }
    private void Mark(string s) { lock (logLock) { using var f = new FileStream(Path.Combine(root, role + ".log"), FileMode.Append, FileAccess.Write, FileShare.ReadWrite); using var w = new StreamWriter(f); w.WriteLine(DateTime.UtcNow.ToString("O") + " " + s); } }
    private void Flag(string n, string s = "ready") => File.WriteAllText(Path.Combine(root, n), s);
    private bool Has(string n) => File.Exists(Path.Combine(root, n));
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
    private void Freeze(float x = 85, float y = 8) {
        var h = HeroController.instance; h.takeNoDamage = true;
        h.GetComponent<Rigidbody2D>().gravityScale = 0; h.GetComponent<Rigidbody2D>().velocity = Vector2.zero;
        h.transform.position = new Vector3(x, y, h.transform.position.z);
    }
    private void Launch(On.FireGrimmBall.orig_OnEnter orig, FireGrimmBall action) {
        orig(action);
        if (manager == null || !manager.IsSceneHost) return;
        var ball = action.storedObject.Value?.GetComponent<GrimmballControl>();
        if (ball == null || !ball.gameObject.activeInHierarchy || Math.Abs(ball.Force) < .001f) return;
        launches++;
        flights.Add(new Flight { Control = ball, Force = ball.Force, Time = Time.realtimeSinceStartup, Position = ball.transform.position });
        Mark("NATIVE LAUNCH force=" + ball.Force + " velocity=" + ball.GetComponent<Rigidbody2D>().velocity);
    }
    private void Observe() {
        if (!In(scene)) return;
        // This motion fixture measures boss/projectile simulation, with an
        // invulnerable stationary target. Keep it inside the native arena:
        // repeated solid contacts otherwise push the zero-gravity test hero
        // through the left boundary and make native AD Pos retry forever.
        Freeze();
        if (Time.realtimeSinceStartup >= nextDiagnostic) {
            nextDiagnostic = Time.realtimeSinceStartup + 3;
            var boss = Entities.Values.FirstOrDefault(e => e.Type == EntityType.Grimm || e.Type == EntityType.NightmareKingGrimm);
            var go = manager.IsSceneHost ? boss?.Object.Host : boss?.Object.Client;
            if (go != null) {
                Mark("DIAGNOSTIC authority=" + manager.IsSceneHost + " pos=" + go.transform.position + " active=" + go.activeInHierarchy + " hp=" + go.GetComponent<HealthManager>()?.hp + " hero=" + HeroController.instance.transform.position + " state=" + string.Join(",", go.GetComponents<PlayMakerFSM>().Select(f => f.FsmName + "=" + f.ActiveStateName)) + " launches=" + launches + " matches=" + peerMatches);
                if (manager.IsSceneHost && !File.Exists(Path.Combine(root,role+"-fsm.txt"))) {
                    File.WriteAllLines(Path.Combine(root,role+"-fsm.txt"),go.GetComponents<PlayMakerFSM>().SelectMany(f=>f.FsmStates.SelectMany(s=>s.Actions.Select(a=>f.FsmName+" / "+s.Name+" / "+a.GetType().Name+" / "+string.Join(",",a.GetType().GetFields(BindingFlags.Instance|BindingFlags.Public).Select(field=>field.Name+"="+DiagnosticValue(f,field.GetValue(a))))))));
                }
            }
        }
        foreach (var f in flights) {
            var ball = f.Control;
            if (ball == null || !ball.gameObject.activeInHierarchy) continue;
            var age = Time.realtimeSinceStartup - f.Time;
            if (!f.Checked && age > .08f && age < .4f) {
                f.Checked = true;
                Require(Math.Abs(ball.Force - f.Force) < .001f && Field<Coroutine>(ball, "fireRoutine") != null,
                    "native fire force and coroutine survive network registration");
                preserved++;
            }
            if (!f.Moved && age > .4f && age < .8f && !Field<bool>(ball, "hit")) {
                f.Moved = true;
                Require((ball.transform.position.x - f.Position.x) * Math.Sign(f.Force) > .2f,
                    "native balloon projectile accelerates horizontally");
                moving++;
            }
        }
        if (Time.realtimeSinceStartup < nextTrace) return;
        nextTrace = Time.realtimeSinceStartup + .08f;
        var path = Path.Combine(root, "motion.state");
        if (manager.IsSceneHost) {
            using var f = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.ReadWrite); using var w = new StreamWriter(f);
            foreach (var e in Entities.Values.ToArray()) {
                var go = e.Object.Host;
                if (go == null || !go.activeInHierarchy || !Projectile(e.Type)) continue;
                var p = go.transform.position;
                w.WriteLine(e.Id + "|" + p.x.ToString("R", CultureInfo.InvariantCulture) + "|" + p.y.ToString("R", CultureInfo.InvariantCulture));
                if (e.Type == EntityType.GrimmBat || e.Type == EntityType.NightmareKingGrimmBat) {
                    if (!batOrigins.TryGetValue(e.Id, out var origin)) batOrigins[e.Id] = origin = p;
                    if (Math.Abs(p.x - origin.x) > 2) flyingBats.Add(e.Id);
                }
            }
        } else if (File.Exists(path)) {
            string[] lines;
            using (var f = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite)) using (var r = new StreamReader(f)) lines = r.ReadToEnd().Split('\n');
            foreach (var line in lines) {
                var parts = line.Trim().Split('|');
                if (parts.Length != 3 || !ulong.TryParse(parts[0], out var id) || !Entities.TryGetValue(id, out var e) || e.Object.Client == null || !e.Object.Client.activeInHierarchy) continue;
                var body = e.Object.Client.GetComponent<Rigidbody2D>();
                RequireQuiet(body == null || body.bodyType == RigidbodyType2D.Kinematic && body.gravityScale == 0 && body.velocity == Vector2.zero,
                    "replica projectile integrates no local physics");
                var p = new Vector2(float.Parse(parts[1], CultureInfo.InvariantCulture), float.Parse(parts[2], CultureInfo.InvariantCulture));
                if (Vector2.Distance(p, e.Object.Client.transform.position) < 2) peerMatches++;
            }
        }
    }
    private static string DiagnosticValue(PlayMakerFSM fsm, object value) {
        if(value is HutongGames.PlayMaker.FsmOwnerDefault owner)return "target:"+fsm.Fsm.GetOwnerDefaultTarget(owner)?.name;
        if(value is HutongGames.PlayMaker.FsmFloat number)return number.Name+":"+number.Value;
        if(value is HutongGames.PlayMaker.FsmGameObject go)return go.Name+":"+go.Value?.name;
        if(value is HutongGames.PlayMaker.FsmEvent ev)return ev.Name;
        return value?.ToString();
    }
    private static bool Projectile(EntityType t) => t == EntityType.GrimmFireball || t == EntityType.GrimmBat || t == EntityType.NightmareKingGrimmBat || t == EntityType.GrimmFirebatPillar;
    private static void RequireQuiet(bool ok, string s) { if (!ok) throw new Exception(s); }
    private IEnumerator Start() {
        role = Environment.GetEnvironmentVariable("HOLLOWCONNECT_SMOKE_ROLE"); root = Environment.GetEnvironmentVariable("HOLLOWCONNECT_SMOKE_ROOT"); host = role == "host";
        scene = Environment.GetEnvironmentVariable("HOLLOWCONNECT_GRIMM_SCENE") ?? "GG_Grimm";
        Hkmp.Logging.Logger.AddLogger(new Log(this));
        On.FireGrimmBall.OnEnter += Launch;
        var stack = new Stack<IEnumerator>(); stack.Push(Run()); var deadline = DateTime.UtcNow.AddMinutes(6);
        while (stack.Count > 0) {
            bool more; object current = null;
            try {
                if (DateTime.UtcNow > deadline) throw new TimeoutException("Grimm motion probe timeout");
                more = stack.Peek().MoveNext(); if (more) current = stack.Peek().Current;
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
        yield return Until(() => In("Town"), "unsaved character ready"); Freeze(20, 10);
        if (host) { engine.ServerManager.Start(29650, false); Flag("server.started"); } else yield return Until(() => Has("server.started"), "server started");
        typeof(Hkmp.Game.Client.ClientManager).GetMethod("Connect", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(engine.ClientManager, new object[] { "127.0.0.1", 29650, "motion" + role });
        yield return Until(() => runtime.Connected, "connected"); Flag(role + ".connected"); yield return Until(() => Has((host ? "guest" : "host") + ".connected"), "both connected");
        // Use the native statue setup; no boss FSM state is forced.
        BossSequenceController.Reset(); BossSceneController.SetupEvent = self => { self.BossLevel = 0; };
        if (host != (Environment.GetEnvironmentVariable("HOLLOWCONNECT_GRIMM_AUTHORITY") != "guest")) {
            var delay = float.TryParse(Environment.GetEnvironmentVariable("HOLLOWCONNECT_GRIMM_ENTRY_DELAY"), out var parsed) ? parsed : 3;
            yield return new WaitForSecondsRealtime(delay);
        }
        Enter(scene); yield return Until(() => In(scene), "arena entered"); Freeze();
        yield return Until(() => manager.IsSceneHostDetermined && Entities.Values.Any(e => e.Type == EntityType.Grimm || e.Type == EntityType.NightmareKingGrimm), "authority ready");
        Mark("AUTHORITY=" + manager.IsSceneHost); Flag(role + ".arena"); yield return Until(() => Has((host ? "guest" : "host") + ".arena"), "both in arena");
        var boss = Entities.Values.Single(e => e.Type == EntityType.Grimm || e.Type == EntityType.NightmareKingGrimm);
        Mark("FIXTURE stationary invulnerable target at arena center (85, 8); no boss FSM state or transform forced");
        var until = Time.realtimeSinceStartup + 38;
        while (Time.realtimeSinceStartup < until) { Observe(); yield return null; }
        if (manager.IsSceneHost) boss.Object.Host.GetComponent<HealthManager>().ApplyExtraDamage(scene == "GG_Grimm" ? 201 : 601);
        until = Time.realtimeSinceStartup + 32;
        while (Time.realtimeSinceStartup < until) { Observe(); yield return null; }
        if (manager.IsSceneHost) {
            Require(launches >= 8 && preserved >= 8 && moving >= 3, "multiple native balloon launches retain horizontal motion");
            Require(flyingBats.Count >= 3, "native firebats travel horizontally");
            Mark("MOTION launches=" + launches + " preserved=" + preserved + " moving=" + moving + " bats=" + flyingBats.Count);
        } else Require(peerMatches >= 20, "replica follows moving native projectiles");
        Flag(role + ".sampled");
        yield return Until(() => Has((host ? "guest" : "host") + ".sampled"), "both motion probes finished");
        if (Environment.GetEnvironmentVariable("HOLLOWCONNECT_GRIMM_HANDOFF") == "1") yield return Handoff();
        yield return ProbeShutdown.Run(engine,root,role);
        yield return new WaitForSecondsRealtime(.3f);
        Mark("FINAL PASS Grimm native motion " + scene); Flag(role + ".done");
    }
    private IEnumerator Handoff() {
        // Exercise departure after a native wave: no artificial FSM state or
        // immediate remake of the room. The survivor must produce new attacks.
        var wasAuthority = manager.IsSceneHost;
        if (wasAuthority) {
            yield return Until(() => !Entities.Values.Any(e => e.Type == EntityType.GrimmFireball && e.Object.Host != null && e.Object.Host.activeInHierarchy), "balloon wave has ended before departure", 25);
            Flag("handoff.started"); Enter("Town"); yield return Until(() => In("Town"), "authority leaves ongoing boss"); Freeze(20, 10);
            yield return Until(() => Has("handoff.survivor-passed"), "survivor continued native attacks", 110);
            // Restore the local native statue setup, then join its current state.
            BossSceneController.SetupEvent = self => { self.BossLevel = 0; };
            Enter(scene); yield return Until(() => In(scene) && manager.IsSceneHostDetermined && !manager.IsSceneHost, "old authority rejoins as replica"); Freeze();
            Flag("handoff.rejoined");
            peerMatches = 0;
            var end = Time.realtimeSinceStartup + 12;
            while (Time.realtimeSinceStartup < end) { Observe(); yield return null; }
            Require(peerMatches >= 20, "returning peer observes survivor projectiles");
            Flag("handoff.replica-passed");
        } else {
            yield return Until(() => Has("handoff.started") && manager.IsSceneHost, "survivor takes authority");
            flights.Clear(); launches = preserved = moving = 0; flyingBats.Clear(); batOrigins.Clear();
            var boss = Entities.Values.Single(e => e.Type == EntityType.Grimm || e.Type == EntityType.NightmareKingGrimm);
            var end = Time.realtimeSinceStartup + 25;
            while (Time.realtimeSinceStartup < end) { Observe(); yield return null; }
            boss.Object.Host.GetComponent<HealthManager>().ApplyExtraDamage(scene == "GG_Grimm" ? 200 : 600);
            end = Time.realtimeSinceStartup + 35;
            while (Time.realtimeSinceStartup < end) { Observe(); yield return null; }
            Require(launches >= 8 && preserved >= 8 && moving >= 3, "post-handoff native balloon motion");
            Require(flyingBats.Count >= 3, "post-handoff native firebat motion");
            Mark("HANDOFF MOTION launches=" + launches + " preserved=" + preserved + " moving=" + moving + " bats=" + flyingBats.Count);
            Flag("handoff.survivor-passed");
            yield return Until(() => Has("handoff.rejoined"), "old authority returned");
            while (!Has("handoff.replica-passed")) { Observe(); yield return null; }
        }
    }
}
