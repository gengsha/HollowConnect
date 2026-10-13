using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using Hkmp.Game.Client.Entity;
using HollowConnect;
using Modding;
using UnityEngine;
using Object = UnityEngine.Object;

namespace HollowConnect.Smoke;

// Tests only. The arena trigger and native HealthManager damage drive every
// phase; this fixture never sets a boss FSM state or reads/writes a player save.
public sealed class BossProbe : MonoBehaviour {
    private string role, root;
    private bool host;
    private Hkmp.Game.GameManager engine;
    private CoopRuntime runtime;
    private int nativeVelocityTicks;
    private bool StayAuthority => Environment.GetEnvironmentVariable("HOLLOWCONNECT_SMOKE_BOSS_CONTROL") == "stay";
    private bool LateGuest => Environment.GetEnvironmentVariable("HOLLOWCONNECT_SMOKE_BOSS_LATE") == "1";
    private GameObject detachedReplicaTerrain;
    private MonoMod.RuntimeDetour.Hook takeoverTrace;
    private MonoMod.RuntimeDetour.Hook makeHostTrace;
    private void TraceParts(ulong id, string label) {
        foreach(var p in EntityReferences.Parts(id).Where(p=>p.Host!=null && p.Host.name.Contains("Terrain")))
            Mark("PART "+label+" path="+p.Path+" owner="+EntityReferences.Owner(p.Host)?.Id+" host="+EntityIdentity.Path(p.Host.transform)+"/"+p.Host.activeSelf+" client="+(p.Client==null?"null":EntityIdentity.Path(p.Client.transform)+"/"+p.Client.activeSelf));
    }
    private static T Field<T>(object o, string n) => (T)o.GetType().GetField(n, BindingFlags.Instance | BindingFlags.NonPublic).GetValue(o);
    private void Mark(string s) => File.AppendAllText(Path.Combine(root, role + ".log"), DateTime.UtcNow.ToString("O") + " " + s + "\n");
    private void Flag(string n, string v = "ready") => File.WriteAllText(Path.Combine(root, n), v);
    private bool Has(string n) => File.Exists(Path.Combine(root, n));
    private bool Check(bool value, string label) {
        Mark((value ? "PASS " : "FAIL ") + label);
        if (!value) Flag(role + ".failed");
        return value;
    }
    private IEnumerator Until(Func<bool> ready, string label, float timeout = 45) {
        var end = Time.realtimeSinceStartup + timeout;
        while (!ready() && Time.realtimeSinceStartup < end) yield return null;
        Check(ready(), label);
    }
    private bool In(string scene) => HeroController.instance != null && HeroController.instance.isHeroInPosition &&
        !GameManager.instance.IsInSceneTransition && GameManager.instance.sceneName == scene;
    private void Freeze() {
        var hero = HeroController.instance; hero.takeNoDamage = true; hero.RelinquishControl();
        var body = hero.GetComponent<Rigidbody2D>(); body.gravityScale = 0; body.velocity = Vector2.zero;
    }
    private void Enter(string scene) => GameManager.instance.BeginSceneTransition(new GameManager.SceneLoadInfo {
        SceneName = scene, EntryGateName = "left1", Visualization = GameManager.SceneLoadVisualizations.Default, WaitForSceneTransitionCameraFade = true
    });
    private static PlayMakerFSM Control(Entity e) => e.Object.Host.GetComponents<PlayMakerFSM>().Single(f => f.FsmName == "FalseyControl");
    private static FsmSnapshot Snapshot(Entity e) {
        var fsms = Field<HostClientPair<List<PlayMakerFSM>>>(e, "_fsms").Host;
        return Field<List<FsmSnapshot>>(e, "_fsmSnapshots")[fsms.FindIndex(f => f.FsmName == "FalseyControl")];
    }
    private static int PhaseCount(Entity e) {
        var f = Control(e);
        return Snapshot(e).Ints[Array.FindIndex(f.FsmVariables.IntVariables, v => v.Name == "Stunned Amount")];
    }
    private void ArenaPose(Entity boss, string label, bool native = false) {
        var obj = host || StayAuthority || native ? boss.Object.Host : boss.Object.Client;
        Mark(label + " position=" + obj.transform.position + " scale=" + obj.transform.lossyScale);
        foreach (var c in Object.FindObjectsOfType<Collider2D>(true).Where(c =>
            c.name == "FK Terrain Block" || c.name == "Battle Gate" || c.name == "Break Floor"))
            Mark("ARENA " + label + " " + c.name + " path=" + EntityIdentity.Path(c.transform) + " id=" + c.GetInstanceID() +
                " active=" + c.gameObject.activeInHierarchy + " self=" + c.gameObject.activeSelf + " pos=" + c.transform.position +
                " bounds=" + c.bounds + " enabled=" + c.enabled + " trigger=" + c.isTrigger);
        var fsm = Control(boss);
        foreach (var v in fsm.FsmVariables.GameObjectVariables)
            if (v.Value != null && v.Value.name.Contains("Terrain")) Mark("TERRAIN REFERENCE " + v.Name + "=" + EntityIdentity.Path(v.Value.transform));
        if (!File.Exists(Path.Combine(root, role + "-fsm.txt"))) {
            File.WriteAllLines(Path.Combine(root, role + "-fsm.txt"), fsm.FsmStates.SelectMany(s => s.Actions.Select(a =>
                s.Name + " / " + a.GetType().Name + " / " + string.Join(",", a.GetType().GetFields(BindingFlags.Instance | BindingFlags.Public).Select(f => f.Name + "=" + f.GetValue(a))))));
        }
    }
    private IEnumerator Start() {
        role = Environment.GetEnvironmentVariable("HOLLOWCONNECT_SMOKE_ROLE"); root = Environment.GetEnvironmentVariable("HOLLOWCONNECT_SMOKE_ROOT"); host = role == "host";
        if(Environment.GetEnvironmentVariable("HOLLOWCONNECT_SMOKE_TAKEOVER_TRACE")=="1") {
            takeoverTrace=new MonoMod.RuntimeDetour.Hook(typeof(Hkmp.Game.Client.Entity.Component.CombatStateComponent).GetMethod("PrepareTakeover",BindingFlags.Instance|BindingFlags.NonPublic),
                (Action<Action<Hkmp.Game.Client.Entity.Component.CombatStateComponent>,Hkmp.Game.Client.Entity.Component.CombatStateComponent>)((orig,component)=> {
                    var id=Field<ulong>(component,"id");TraceParts(id,"BEFORE PREPARE");orig(component);TraceParts(id,"AFTER PREPARE");
                }));
            makeHostTrace=new MonoMod.RuntimeDetour.Hook(typeof(Entity).GetMethod("MakeHost"),
                (Action<Action<Entity>,Entity>)((orig,entity)=> {TraceParts(entity.Id,"BEFORE MAKEHOST");orig(entity);TraceParts(entity.Id,"AFTER MAKEHOST");}));
            On.HutongGames.PlayMaker.Actions.ActivateGameObject.DoActivateGameObject += (orig,action)=> {
                var go=action.Fsm.GetOwnerDefaultTarget(action.gameObject);
                if(go!=null&&go.name.Contains("Terrain"))Mark("ACTIVATE terrain="+go.GetInstanceID()+" native="+(EntityReferences.Owner(go)?.Object.Host==go)+" fsm="+action.Fsm.Name+" state="+action.State.Name+" active="+action.activate.Value);
                orig(action);
            };
        }
        On.HutongGames.PlayMaker.Actions.SetVelocity2d.OnFixedUpdate += (orig,action)=> {
            orig(action);
            if(action.Fsm!=null&&action.Fsm.Name=="FalseyControl")nativeVelocityTicks++;
        };
        yield return Until(() => Object.FindObjectOfType<CoopRuntime>() != null && GameManager.instance != null &&
            UnityEngine.SceneManagement.SceneManager.GetActiveScene().name == "Menu_Title", "menu ready");
        if (Has(role + ".failed")) yield break;
        runtime = Object.FindObjectOfType<CoopRuntime>(); engine = Field<Hkmp.Game.GameManager>(ModHooks.GetMod("HKMP"), "_gameManager");
        Field<Hkmp.Game.Settings.ModSettings>(engine.ClientManager, "_modSettings").AuthKey = Hkmp.Util.AuthUtil.GenerateAuthKey();
        CoopMod.Settings.ShareProgress = false;
        var pd = PlayerData.instance; pd.SetString("respawnScene", "Town"); pd.SetString("respawnMarkerName", "RestBench"); pd.SetInt("respawnType", 1);
        GameManager.instance.profileID = 99; GameManager.instance.StartCoroutine(GameManager.instance.RunContinueGame());
        yield return Until(() => In("Town"), "unsaved character ready");
        if (Has(role + ".failed")) yield break;
        Freeze();
        if (host) { engine.ServerManager.Start(29650, false); Flag("server.started"); }
        else yield return Until(() => Has("server.started"), "server ready");
        typeof(Hkmp.Game.Client.ClientManager).GetField("_autoConnect",BindingFlags.Instance|BindingFlags.NonPublic).SetValue(engine.ClientManager,true);
        typeof(Hkmp.Game.Client.ClientManager).GetMethod("Connect", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(engine.ClientManager, new object[] { "127.0.0.1", 29650, "boss" + role });
        yield return Until(() => runtime.Connected, "boss test handshake");
        if (Has(role + ".failed")) yield break;
        Flag(role + ".connected"); yield return Until(() => Has(host ? "guest.connected" : "host.connected"), "both connected");
        if (!host && LateGuest) {
            yield return Until(() => Has("host.head-damaged"), "late join waits for already-exposed native boss");
            yield return new WaitForSecondsRealtime(2.2f);
        } else if (!host) yield return new WaitForSecondsRealtime(2);
        Enter("Crossroads_10"); yield return Until(() => In("Crossroads_10"), "native False Knight room"); Freeze();
        var manager = Field<EntityManager>(engine.ClientManager, "_entityManager");
        yield return Until(() => manager.IsSceneHostDetermined, "boss authority initialized");
        var entities = Field<Dictionary<ulong, Entity>>(manager, "_entities");
        var boss = entities.Values.Single(e => e.Type == EntityType.FalseKnight);
        var head = entities.Values.Single(e => e.Type == EntityType.FalseKnightHead);
        Flag(role + ".room");
        if (!host || !LateGuest) yield return Until(() => Has(host ? "guest.room" : "host.room"), "both in arena");
        if (host) {
            var trigger = Object.FindObjectsOfType<Collider2D>().First(c => c.isTrigger && c.gameObject.name == "Battle Scene");
            HeroController.instance.transform.position = trigger.bounds.center;
            yield return new WaitForSecondsRealtime(1);
            foreach (var e in entities.Values.Where(e => e.Type != EntityType.FalseKnight && e.Type != EntityType.FalseKnightHead &&
                e.Object.Host != null && e.Object.Host.activeInHierarchy && e.Object.Host.GetComponent<HealthManager>() != null).ToArray())
                e.Object.Host.GetComponent<HealthManager>().ApplyExtraDamage(1000);
            var control = Control(boss); var hm = boss.Object.Host.GetComponent<HealthManager>();
            yield return Until(() => control.ActiveStateName == "Idle", "arena trigger starts native boss AI", 20);
            hm.ApplyExtraDamage(hm.hp);
            yield return Until(() => control.ActiveStateName == "Opened", "native armor damage exposes head without terminal death", 20);
            if (!Check(boss.Object.Host.activeInHierarchy && !hm.isDead && hm.hp == 65, "armor phase remains alive and resets phase health")) yield break;
            var headHealth = head.Object.Host.GetComponent<HealthManager>(); headHealth.ApplyExtraDamage(7);
            if (!Check(headHealth.hp == 33 && !headHealth.isDead, "exposed head takes partial native damage")) yield break;
            Mark("AUTHORITY exposed position="+boss.Object.Host.transform.position+" scale="+boss.Object.Host.transform.lossyScale);
            ArenaPose(boss, "BEFORE HANDOFF");
            Flag("authority-pose",boss.Object.Host.transform.position.x.ToString(System.Globalization.CultureInfo.InvariantCulture)+","+boss.Object.Host.transform.position.y.ToString(System.Globalization.CultureInfo.InvariantCulture));
            Flag("host.head-damaged");
            yield return Until(() => Has("guest.phase-observed"), "guest observed exposed phase and health");
            if(float.TryParse(Environment.GetEnvironmentVariable("HOLLOWCONNECT_SMOKE_HANDOFF_DELAY"),out var handoffDelay))yield return new WaitForSecondsRealtime(handoffDelay);
            if (StayAuthority) {
                // Control for the terrain diagnostic: identical native armor/head
                // damage, without any authority transfer or forced FSM/transform.
                headHealth.ApplyExtraDamage(headHealth.hp);
                yield return Until(() => control.FsmVariables.GetFsmInt("Stunned Amount").Value == 1 &&
                    control.FsmVariables.GetFsmInt("Rages").Value == 1, "same authority advances native head phase", 20);
                var controlPosition = boss.Object.Host.transform.position;
                yield return Until(() => Vector2.Distance(boss.Object.Host.transform.position, controlPosition) > .3f,
                    "same authority moves after native head phase", 20);
                ArenaPose(boss, "SAME AUTHORITY RESULT");
                Flag("control.complete");
                if (Has(role + ".failed")) yield break;
                yield return ProbeShutdown.Run(engine, root, role);
                Mark("FINAL PASS native False Knight same-authority terrain control"); Flag(role + ".done"); yield break;
            }
            Mark("HANDOFF REQUEST state="+control.ActiveStateName+" elapsed exposure may expire during native fade");
            Enter("Town"); yield return Until(() => In("Town"), "old authority leaves after exposed phase observation"); Freeze(); Flag("host.left");
            yield return Until(() => Has("guest.continued"), "new authority continues native phase and combat");
            if (Has("guest.failed")) { Flag(role + ".failed"); yield break; }
            Enter("Crossroads_10"); yield return Until(() => In("Crossroads_10"), "old authority rejoins ongoing boss fight"); Freeze();
            boss = entities.Values.Single(e => e.Type == EntityType.FalseKnight);
            yield return Until(() => boss.Object.Client != null && boss.Object.Client.activeInHierarchy &&
                boss.Object.Client.GetComponent<HealthManager>().hp == 60 && PhaseCount(boss) == 1,
                "returning client receives continued armor health and phase count");
            Flag("host.returned");
        } else {
            HeroController.instance.transform.position = new Vector3(20, 35, 0);
            yield return Until(() => Has("host.head-damaged") && head.Object.Client.GetComponent<HealthManager>().hp == 33 &&
                Snapshot(boss).CurrentState == "Opened" && boss.Object.Client.activeInHierarchy && !boss.Object.Client.GetComponent<HealthManager>().isDead,
                "guest receives exposed boss phase and head health");
            if (Has(role + ".failed")) yield break;
            Mark("REPLICA exposed position="+boss.Object.Client.transform.position+" scale="+boss.Object.Client.transform.lossyScale);
            if (!StayAuthority) {
                var clientControl = boss.Object.Client.GetComponents<PlayMakerFSM>().Single(f => f.FsmName == "FalseyControl");
                var clientTerrain = clientControl.FsmVariables.GetFsmGameObject("Terrain Block").Value;
                detachedReplicaTerrain = clientTerrain;
                var nativeTerrain = Control(boss).FsmVariables.GetFsmGameObject("Terrain Block").Value;
                var terrainKey = EntityReferences.Capture(clientTerrain);
                if (!Check(clientTerrain != null && EntityReferences.Owner(clientTerrain) == boss &&
                    EntityReferences.Resolve(terrainKey, true) == clientTerrain && EntityReferences.Resolve(terrainKey, false) == nativeTerrain,
                    "detached child keeps stable identity and both native/replica references")) yield break;
            }
            if (!StayAuthority) ArenaPose(boss, "BEFORE HANDOFF");
            Flag("guest.phase-observed");
            if (StayAuthority) {
                yield return Until(() => Has("control.complete"), "same authority control completes", 50);
                if (Has("host.failed")) { Flag(role + ".failed"); yield break; }
                yield return ProbeShutdown.Run(engine, root, role);
                Mark("FINAL PASS native False Knight same-authority terrain control"); Flag(role + ".done"); yield break;
            }
            yield return Until(() => Has("host.left") && manager.IsSceneHost, "authority transfers after exposed phase observation", 10);
            var control = Control(boss); var hm = boss.Object.Host.GetComponent<HealthManager>();
            Mark("HANDOFF PHASE native="+control.ActiveStateName+" snapshot="+Snapshot(boss).CurrentState);
            Mark("TAKEOVER position="+boss.Object.Host.transform.position+" scale="+boss.Object.Host.transform.lossyScale+" old-authority="+File.ReadAllText(Path.Combine(root,"authority-pose")));
            ArenaPose(boss, "AFTER HANDOFF", true);
            var terrain = control.FsmVariables.GetFsmGameObject("Terrain Block").Value;
            var expectedHeadHealth=33;
            if(LateGuest && control.ActiveStateName!="Opened") {
                // Native Opened has a five-second Wait. A genuine late entry
                // plus scene fade can outlive it; inactive terrain is then
                // correct, not a failed takeover. Verify natural recovery and
                // drive a second exposure with native damage, never SetState.
                if(!Check(terrain!=null&&!terrain.activeInHierarchy&&
                    !Object.FindObjectsOfType<Collider2D>().Any(c=>c.name=="FK Terrain Block"&&c.enabled),
                    "elapsed exposure has no active terrain or stale replica collider"))yield break;
                if(!Check(head.Object.Host.GetComponent<HealthManager>().hp==33&&hm.hp==65&&!hm.isDead,
                    "timed-out exposure still preserves last authoritative armor and head health"))yield break;
                var recoveredPosition=boss.Object.Host.transform.position;
                yield return Until(()=>Vector2.Distance(boss.Object.Host.transform.position,recoveredPosition)>.3f && !hm.IsInvincible,
                    "native boss resumes combat when exposure expires before takeover",20);
                if(Has(role+".failed"))yield break;
                hm.ApplyExtraDamage(hm.hp);
                yield return Until(()=>control.ActiveStateName=="Opened"&&head.Object.Host.activeInHierarchy,
                    "native armor damage exposes a second head after timed-out late transfer",20);
                if(Has(role+".failed"))yield break;
                var reopened=head.Object.Host.GetComponent<HealthManager>();expectedHeadHealth=reopened.hp-7;reopened.ApplyExtraDamage(7);
                Mark("REOPENED native head health="+reopened.hp+" expected="+expectedHeadHealth+" armor="+hm.hp);
                terrain=control.FsmVariables.GetFsmGameObject("Terrain Block").Value;
            }
            if (!Check(terrain != null && terrain.activeInHierarchy &&
                Object.FindObjectsOfType<Collider2D>().Count(c => c.name == "FK Terrain Block" && c.enabled) == 1,
                "detached native terrain reference owns the only active collider after handoff")) yield break;
            var headHealth = head.Object.Host.GetComponent<HealthManager>();
            if (!Check(control.ActiveStateName == "Opened" && headHealth.hp == expectedHeadHealth && headHealth.hp>0 && hm.hp == 65 && !hm.isDead &&
                boss.Object.Host.activeInHierarchy && head.Object.Host.activeInHierarchy, "handoff preserves live exposed phase and both health values")) yield break;
            headHealth.ApplyExtraDamage(headHealth.hp);
            yield return Until(() => control.FsmVariables.GetFsmInt("Stunned Amount").Value == 1 && control.FsmVariables.GetFsmInt("Rages").Value == 1 &&
                control.ActiveStateName != "Opened" && control.ActiveStateName != "Open Uuup", "native head defeat advances phase after transfer", 20);
            ArenaPose(boss, "AFTER HEAD PHASE", true);
            if (!Check(!Object.FindObjectsOfType<Collider2D>().Any(c => c.name == "FK Terrain Block" && c.enabled),
                "native head recovery removes detached terrain without replica collider residue")) yield break;
            var pos = boss.Object.Host.transform.position;
            var traceEnd=Time.realtimeSinceStartup+15;var traceNext=0f;
            while(Time.realtimeSinceStartup<traceEnd && Vector2.Distance(boss.Object.Host.transform.position,pos)<=.3f) {
                if(Time.realtimeSinceStartup>=traceNext){traceNext=Time.realtimeSinceStartup+1;
                    var body=boss.Object.Host.GetComponent<Rigidbody2D>();var anim=boss.Object.Host.GetComponent<tk2dSpriteAnimator>();
                    Mark("PHASE TICK state="+control.ActiveStateName+" pos="+boss.Object.Host.transform.position+" body="+body.bodyType+"/"+body.velocity+"/gravity="+body.gravityScale+" simulated="+body.simulated+" constraints="+body.constraints+" timescale="+Time.timeScale+" fixed="+Time.fixedDeltaTime+" physics="+Physics2D.simulationMode+" hero="+HeroController.instance.transform.position+" fixed-ticks="+nativeVelocityTicks+" handlefixed="+control.Fsm.HandleFixedUpdate+" proxies="+string.Join(",",boss.Object.Host.GetComponents<PlayMakerFixedUpdate>().Select(p=>p.enabled+"/"+string.Join("|",p.TargetFSMs.Select(f=>f==null?"null":f.name+":"+f.FsmName+":"+f.Active))))+" anim="+anim.enabled+"/"+anim.CurrentClip?.name+"/"+anim.CurrentFrame+"/playing="+anim.Playing+" actions="+string.Join(",",control.Fsm.ActiveState.ActiveActions.Select(a=>a.GetType().Name)));
                    foreach(var velocity in control.Fsm.ActiveState.Actions.OfType<HutongGames.PlayMaker.Actions.SetVelocity2d>()) {
                        var target=velocity.Fsm.GetOwnerDefaultTarget(velocity.gameObject);var targetBody=target==null?null:target.GetComponent<Rigidbody2D>();
                        Mark("VELOCITY TARGET native="+(target==boss.Object.Host)+" path="+(target==null?"null":target.name)+" body="+(targetBody==null?"null":targetBody.bodyType+"/"+targetBody.simulated+"/"+targetBody.velocity));
                    }
                    var contacts=new ContactPoint2D[16];var contactCount=body.GetContacts(contacts);
                    for(var ci=0;ci<contactCount;ci++) {
                        var contact=contacts[ci];
                        Mark("PHYSICS CONTACT "+contact.collider.name+"/"+contact.otherCollider.name+" normal="+contact.normal+" point="+contact.point+" separation="+contact.separation);
                    }
                    foreach(var a in control.Fsm.ActiveState.Actions) Mark("PHASE ACTION "+a.GetType().Name+" fields="+string.Join(",",a.GetType().GetFields(BindingFlags.Instance|BindingFlags.Public).Select(f=>f.Name+"="+f.GetValue(a))));
                }yield return null;
            }
            yield return Until(() => Vector2.Distance(boss.Object.Host.transform.position, pos) > .3f, "boss AI moves after phase handoff", 1);
            if (Has(role + ".failed")) yield break;
            hm.ApplyExtraDamage(5);
            if (!Check(hm.hp == 60 && !hm.isDead, "continued armor damage remains live after transfer")) yield break;
            Flag("guest.continued"); yield return Until(() => Has("host.returned"), "returning client sees continued boss");
        }
        if (Has(role + ".failed")) yield break;
        yield return ProbeShutdown.Run(engine,root,role);
        yield return null; yield return null;
        if (!host && !Check(detachedReplicaTerrain == null, "disconnect destroys the detached replica even after live entity table retirement")) yield break;
        Mark("FINAL PASS native False Knight: arena trigger, armor collapse, exposed head health, phase handoff, next phase AI and returning client convergence");
        Flag(role + ".done");
    }
}
