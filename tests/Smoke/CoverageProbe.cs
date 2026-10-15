using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using Hkmp.Game.Client.Entity;
using Hkmp.Game.Client.Entity.Component;
using Hkmp.Networking.Packet.Data;
using HollowConnect;
using HutongGames.PlayMaker;
using Modding;
using MonoMod.RuntimeDetour;
using UnityEngine;
using Object = UnityEngine.Object;

namespace HollowConnect.Smoke;

// Private in-memory characters; ProbeMod suppresses every save overload.
public sealed partial class CoverageProbe : MonoBehaviour {
    private string root, role;
    private bool host;
    private CoopRuntime runtime;
    private EntityManager manager;
    private Hkmp.Game.GameManager engine;
    private readonly Dictionary<EntityType,int> matched=new();
    private int shieldEvaluated,shieldMatched;
    private int visibleNeedleSamples,pureMeleeSamples;
    private float nextTrace;
    private readonly List<IDisposable> performanceHooks = new();
    private readonly Dictionary<string, int> spawnCounts = new();
    private double registrationMs, retirementMs, replicaSpawnMs;
    private int registrations, retirements, replicaSpawns;
    private readonly List<double> prefabTimes = new(), processTimes = new();
    private readonly Dictionary<string, (int Calls, double Ms, long Bytes)> costs = new();
    private void Cost(string key, long start, long bytes = 0) {
        costs.TryGetValue(key, out var old);
        costs[key] = (old.Calls + 1, old.Ms + (System.Diagnostics.Stopwatch.GetTimestamp() - start) * 1000.0 / System.Diagnostics.Stopwatch.Frequency, old.Bytes + bytes);
    }
    private void ObservePerformance() {
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
        double Milliseconds(long start) => (System.Diagnostics.Stopwatch.GetTimestamp() - start) * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
        performanceHooks.Add(new Hook(typeof(EntityManager).GetMethod("OnGameObjectSpawned", flags),
            (Func<Func<EntityManager, EntitySpawnDetails, bool>, EntityManager, EntitySpawnDetails, bool>)((orig, m, d) => {
                var before = Entities.Count; var start = System.Diagnostics.Stopwatch.GetTimestamp();
                var result = orig(m, d); registrationMs += Milliseconds(start);
                if (Entities.Count > before) {
                    registrations++;
                    var key = (d.Prefab == null ? d.GameObject?.name : d.Prefab.name) + " from=" + d.Action?.Fsm?.GameObject?.name + "/" + d.Action?.State?.Name;
                    spawnCounts.TryGetValue(key, out var count); spawnCounts[key] = count + 1;
                }
                return result;
            })));
        performanceHooks.Add(new Hook(typeof(EntityManager).GetMethod("RetireLifetime", flags),
            (Action<Action<EntityManager, Entity>, EntityManager, Entity>)((orig, m, e) => {
                var start = System.Diagnostics.Stopwatch.GetTimestamp(); orig(m, e);
                retirementMs += Milliseconds(start); retirements++;
            })));
        performanceHooks.Add(new Hook(typeof(EntityManager).GetMethod("SpawnEntity"),
            (Action<Action<EntityManager, EntitySpawn>, EntityManager, EntitySpawn>)((orig, m, s) => {
                var start = System.Diagnostics.Stopwatch.GetTimestamp(); orig(m, s);
                replicaSpawnMs += Milliseconds(start); replicaSpawns++;
            })));
        performanceHooks.Add(new Hook(typeof(SpawnDescriptor).GetMethod("Create", BindingFlags.Static | BindingFlags.NonPublic),
            (Func<Func<EntitySpawn, Entity, GameObject>, EntitySpawn, Entity, GameObject>)((orig, s, e) => {
                var start = System.Diagnostics.Stopwatch.GetTimestamp(); var go = orig(s, e);
                prefabTimes.Add(Milliseconds(start)); return go;
            })));
        performanceHooks.Add(new Hook(typeof(EntityProcessor).GetMethod("Process", Type.EmptyTypes),
            (Func<Func<EntityProcessor, EntityProcessor>, EntityProcessor, EntityProcessor>)((orig, p) => {
                var start = System.Diagnostics.Stopwatch.GetTimestamp(); var result = orig(p);
                processTimes.Add(Milliseconds(start)); return result;
            })));
        if (Environment.GetEnvironmentVariable("HOLLOWCONNECT_COVERAGE_PROFILE") != "1") return;
        performanceHooks.Add(new Hook(typeof(Entity).GetMethod("OnUpdate", flags),
            (Action<Action<Entity>, Entity>)((orig, e) => {
                var start = System.Diagnostics.Stopwatch.GetTimestamp(); orig(e); Cost("entity-tick/" + e.Type, start);
            })));
        performanceHooks.Add(new Hook(typeof(CombatStateComponent).GetMethod("Tick", flags),
            (Action<Action<CombatStateComponent>, CombatStateComponent>)((orig, c) => {
                var start = System.Diagnostics.Stopwatch.GetTimestamp(); orig(c); Cost("combat-capture", start);
            })));
        performanceHooks.Add(new Hook(typeof(CombatStateComponent).GetMethod("Update"),
            (Action<Action<CombatStateComponent, EntityNetworkData, bool>, CombatStateComponent, EntityNetworkData, bool>)((orig, c, d, snapshot) => {
                var start = System.Diagnostics.Stopwatch.GetTimestamp(); orig(c, d, snapshot); Cost("combat-apply", start, d.Packet.Length);
            })));
        performanceHooks.Add(new Hook(typeof(Hkmp.Networking.Client.ClientUpdateManager).GetMethod("AddEntityData"),
            (Action<Action<Hkmp.Networking.Client.ClientUpdateManager, ulong, EntityNetworkData>, Hkmp.Networking.Client.ClientUpdateManager, ulong, EntityNetworkData>)((orig, m, id, d) => {
                var start = System.Diagnostics.Stopwatch.GetTimestamp(); orig(m, id, d);
                Cost("send/" + d.Type + "/" + (Entities.TryGetValue(id, out var e) ? e.Type.ToString() : "unknown"), start, d.Packet.Length);
            })));
    }
    private void Trace(int round) {
        if(Time.realtimeSinceStartup<nextTrace)return;nextTrace=Time.realtimeSinceStartup+.1f;
        var path=Path.Combine(root,"positions."+round+".txt");
        if(manager.IsSceneHost) {
            var lines=new List<string>();
            foreach(var e in Entities.Values.ToArray()){
                var go=e.Object.Host;if(go==null||!go.activeInHierarchy)continue;
                foreach(var t in go.GetComponentsInChildren<Transform>(true).Where(t=>EntityReferences.Owner(t.gameObject)==e)){
                    var pvHit=e.Type==EntityType.PureVessel && t.gameObject.activeInHierarchy && t.GetComponent<DamageHero>()!=null && t.GetComponents<Collider2D>().Any(c=>c.enabled);
                    if(t!=go.transform&&!(e.Type==EntityType.MarkothShield&&t.GetComponent<Collider2D>()!=null)&&!pvHit)continue;
                    var p=t.position;lines.Add(e.Id+"|"+EntityIdentity.Path(t,go.transform)+"|"+p.x.ToString("R",System.Globalization.CultureInfo.InvariantCulture)+"|"+p.y.ToString("R",System.Globalization.CultureInfo.InvariantCulture));
                }
            }
            using(var f=new FileStream(path,FileMode.Create,FileAccess.Write,FileShare.ReadWrite))using(var w=new StreamWriter(f))foreach(var line in lines)w.WriteLine(line);
        }else{
            if(!File.Exists(path))return;
            string[] lines;try{using(var f=new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.ReadWrite))using(var r=new StreamReader(f))lines=r.ReadToEnd().Split('\n');}catch(IOException){return;}
            foreach(var line in lines){var parts=line.Trim().Split('|');if(parts.Length!=4||!ulong.TryParse(parts[0],out var id)||!Entities.TryGetValue(id,out var e)||e.Object.Client==null)continue;
                if(!float.TryParse(parts[2],System.Globalization.NumberStyles.Float,System.Globalization.CultureInfo.InvariantCulture,out var x)||!float.TryParse(parts[3],System.Globalization.NumberStyles.Float,System.Globalization.CultureInfo.InvariantCulture,out var y))continue;
                var go=EntityReferences.Walk(e.Object.Client,parts[1]);if(go==null||!go.activeInHierarchy)continue;
                var distance=Vector2.Distance(go.transform.position,new Vector2(x,y));
                if(e.Type==EntityType.MarkothShield && parts[1].Length!=0){shieldEvaluated++;if(distance<3)shieldMatched++;}
                if(distance<3 && go.GetComponent<DamageHero>() is DamageHero damage && damage.enabled && damage.damageDealt>0 && go.GetComponents<Collider2D>().Any(c=>c.enabled)) {
                    if(go.name.IndexOf("Needle",StringComparison.OrdinalIgnoreCase)>=0 && go.GetComponentsInChildren<Renderer>().Any(r=>r.enabled&&!r.forceRenderingOff))visibleNeedleSamples++;
                    if(e.Type==EntityType.PureVessel && parts[1].Length!=0 && (go.name.StartsWith("Slash")||go.name.StartsWith("Dash")))pureMeleeSamples++;
                }
                if(distance<3){matched.TryGetValue(e.Type,out var n);matched[e.Type]=n+1;}
            }
        }
    }
    private readonly object logLock = new object();
    private static T Field<T>(object o,string n) => (T)o.GetType().GetField(n,BindingFlags.Instance|BindingFlags.NonPublic).GetValue(o);
    private Dictionary<ulong,Entity> Entities => Field<Dictionary<ulong,Entity>>(manager,"_entities");
    private void Mark(string s) { lock(logLock) { using(var f=new FileStream(Path.Combine(root,role+".log"),FileMode.Append,FileAccess.Write,FileShare.ReadWrite)) using(var w=new StreamWriter(f)) w.WriteLine(DateTime.UtcNow.ToString("o")+" "+s); } }
    private void Flag(string s) => File.WriteAllText(Path.Combine(root,s),"ready");
    private bool Has(string s) => File.Exists(Path.Combine(root,s));
    private IEnumerator Until(Func<bool> f,string label) { var end=Time.realtimeSinceStartup+50; while(!f()&&Time.realtimeSinceStartup<end) yield return null; if(!f()) throw new Exception("Timeout "+label); Mark("PASS "+label); }
    private bool In(string scene)=>runtime.Playable&&GameManager.instance.sceneName==scene;
    private void Freeze() { var h=HeroController.instance; h.takeNoDamage=true; h.AffectedByGravity(false); h.GetComponent<Rigidbody2D>().velocity=Vector2.zero; }
    private void Enter(string scene)=>GameManager.instance.BeginSceneTransition(new GameManager.SceneLoadInfo{SceneName=scene,EntryGateName=Environment.GetEnvironmentVariable("HOLLOWCONNECT_COVERAGE_ENTRY_GATE") ?? (scene.StartsWith("GG_")?"door_dreamEnter":"left1"),Visualization=GameManager.SceneLoadVisualizations.Default,WaitForSceneTransitionCameraFade=true});
    private sealed class Log : Hkmp.Logging.ILogger {
        private readonly CoverageProbe p; internal Log(CoverageProbe p){this.p=p;}
        public void Info(string s){} public void Debug(string s){} public void Fine(string s){}
        public void Warn(string s)=>p.Mark("WARN "+s);
        public void Error(string s){p.Mark("ERROR "+s);p.Flag(p.role+".failed");}
    }
    private string Describe(GameObject go) => go==null?"null":go.name+"@"+go.scene.name+"/"+(go.scene.IsValid()?EntityIdentity.Path(go.transform):"prefab")+" active="+go.activeInHierarchy;
    private void Dump(GameObject go) {
        if(go==null) return;
        Mark("OBJECT "+Describe(go)+" pos="+go.transform.position+" components="+string.Join(",",go.GetComponents<Component>().Where(c=>c!=null).Select(c=>c.GetType().Name)));
        foreach(var f in go.GetComponents<PlayMakerFSM>()) {
            Mark("FSM "+f.FsmName+"="+f.ActiveStateName+" global="+string.Join(",",f.Fsm.GlobalTransitions.Select(t=>t.EventName+"->"+t.ToState)));
            Mark("STRINGS " + string.Join(",", f.FsmVariables.StringVariables.Select(v => v.Name + "=" + v.Value)));
            foreach(var v in f.FsmVariables.GameObjectVariables) Mark("VAR "+v.Name+"="+Describe(v.Value));
            foreach(var s in f.FsmStates) {
                Mark("STATE "+s.Name+" "+string.Join(",",s.Actions.Select(a=>a.GetType().Name))+" transitions="+string.Join(",",s.Transitions.Select(t=>t.EventName+"->"+t.ToState)));
                foreach(var a in s.Actions) {
                    foreach(var fld in a.GetType().GetFields(BindingFlags.Instance|BindingFlags.Public)) {
                        var val=fld.GetValue(a); var obj=val as GameObject??(val as FsmGameObject)?.Value;
                        if(obj!=null) Mark("REF "+s.Name+"/"+Array.IndexOf(s.Actions,a)+"/"+a.GetType().Name+"/"+fld.Name+"="+Describe(obj));
                    }
                }
            }
        }
        foreach(Transform t in go.transform) Dump(t.gameObject);
    }
    private IEnumerator Start() {
        root=Environment.GetEnvironmentVariable("HOLLOWCONNECT_SMOKE_ROOT"); role=Environment.GetEnvironmentVariable("HOLLOWCONNECT_SMOKE_ROLE");host=role=="host";
        var stack=new Stack<IEnumerator>();stack.Push(Run());
        while(stack.Count>0) {
            object current=null; bool moved;
            try { moved=stack.Peek().MoveNext();if(moved)current=stack.Peek().Current; }
            catch(Exception e){Mark("FAIL "+e);Flag(role+".failed");yield break;}
            if(!moved){stack.Pop();continue;}if(current is IEnumerator next)stack.Push(next);else yield return current;
        }
    }
    private IEnumerator Run() {
        yield return Until(()=>Object.FindObjectOfType<CoopRuntime>()!=null&&GameManager.instance!=null&&UnityEngine.SceneManagement.SceneManager.GetActiveScene().name=="Menu_Title","menu");
        runtime=Object.FindObjectOfType<CoopRuntime>();engine=Field<Hkmp.Game.GameManager>(ModHooks.GetMod("HKMP"),"_gameManager");manager=Field<EntityManager>(engine.ClientManager,"_entityManager");
        Hkmp.Logging.Logger.AddLogger(new Log(this));
        var performance = Environment.GetEnvironmentVariable("HOLLOWCONNECT_COVERAGE_PERFORMANCE") == "1";
        if (performance) ObservePerformance();
        On.HutongGames.PlayMaker.Actions.DestroyObject.OnEnter += (orig,a)=>{ Mark("NATIVE DESTROY "+Describe(a.gameObject.Value)+" from="+a.Fsm?.GameObject?.name+"/"+a.Fsm?.Name+"/"+a.State?.Name); orig(a); };
        On.HutongGames.PlayMaker.Fsm.ProcessEvent += (orig,f,e,d)=>{ if(f.GameObject!=null&&f.GameObject.name.Contains("Mega Jellyfish"))Mark("UUMUU EVENT "+f.GameObject.name+"/"+f.Name+"/"+e?.Name+" active="+f.Active+" state="+f.ActiveStateName);orig(f,e,d); };
        Field<Hkmp.Game.Settings.ModSettings>(engine.ClientManager,"_modSettings").AuthKey=Hkmp.Util.AuthUtil.GenerateAuthKey();
        CoopMod.Settings.ShareProgress=false;
        PlayerData.instance.SetString("respawnScene","Town");PlayerData.instance.SetString("respawnMarkerName","RestBench");PlayerData.instance.SetInt("respawnType",1);
        GameManager.instance.profileID=99;GameManager.instance.StartCoroutine(GameManager.instance.RunContinueGame());
        yield return Until(()=>In("Town"),"unsaved Town");Freeze();
        if(host){engine.ServerManager.Start(29650,false);Flag("server.started");}else yield return Until(()=>Has("server.started"),"server");
        typeof(Hkmp.Game.Client.ClientManager).GetField("_autoConnect",BindingFlags.Instance|BindingFlags.NonPublic).SetValue(engine.ClientManager,true);
        typeof(Hkmp.Game.Client.ClientManager).GetMethod("Connect",BindingFlags.Instance|BindingFlags.NonPublic).Invoke(engine.ClientManager,new object[]{"127.0.0.1",29650,"coverage"+role});
        yield return Until(()=>runtime.Connected,"connect");Flag(role+".connected");yield return Until(()=>Has((host?"guest":"host")+".connected"),"both connected");
        var scenes=(Environment.GetEnvironmentVariable("HOLLOWCONNECT_COVERAGE_SCENES")??"GG_Hornet_1,GG_Nailmasters,GG_Mantis_Lords,GG_Uumuu,GG_Ghost_Markoth,GG_Collector,GG_Hollow_Knight,GG_Painter,GG_Nosk,GG_Ghost_No_Eyes,GG_Soul_Tyrant,GG_White_Defender,GG_Grimm").Split(',');
        var seconds=float.Parse(Environment.GetEnvironmentVariable("HOLLOWCONNECT_COVERAGE_SECONDS")??"20",System.Globalization.CultureInfo.InvariantCulture);
        for(var round=0;round<scenes.Length;round++) {
            var scene=scenes[round];
            BossSequenceController.Reset(); PlayerData.instance.SetInt("bossStatueTargetLevel",0);
            var sequences=Resources.LoadAll<BossSequence>("GG");
            var sequence=sequences.FirstOrDefault(s=>Enumerable.Range(0,s.Count).Any(i=>s.GetSceneAt(i)==scene));
            if(sequence!=null) {
                var tier=System.Text.RegularExpressions.Regex.Match(sequence.name,@"Tier (\d+)$");
                if(!tier.Success)throw new Exception("Unknown native sequence name: "+sequence.name);
                BossSequenceController.SetupNewSequence(sequence,BossSequenceController.ChallengeBindings.None,"bossDoorStateTier"+tier.Groups[1].Value);
                typeof(BossSequenceController).GetField("bossIndex",BindingFlags.Static|BindingFlags.NonPublic).SetValue(null,Enumerable.Range(0,sequence.Count).First(i=>sequence.GetSceneAt(i)==scene));
            }
            var guestAuthority=Environment.GetEnvironmentVariable("HOLLOWCONNECT_COVERAGE_AUTHORITY")=="guest";
            if(host==guestAuthority)yield return new WaitForSecondsRealtime(2);
            Enter(scene);yield return Until(()=>In(scene),"enter "+scene);
            if(performance) {
                var hero=HeroController.instance;hero.takeNoDamage=true;hero.AffectedByGravity(true);hero.IgnoreInputWithoutReset();
                yield return Until(()=>hero.cState.onGround,"native performance target lands " + scene);
            } else Freeze();
            yield return Until(()=>manager.IsSceneHostDetermined,"authority "+scene);
            Flag(role+".room."+round);yield return Until(()=>Has((host?"guest":"host")+".room."+round),"both room "+scene);
            Mark("ROOM "+scene+" authority="+manager.IsSceneHost+" entities="+Entities.Count);
            if (Environment.GetEnvironmentVariable("HOLLOWCONNECT_DREAM_PLATFORM_ASSERTIONS") == "1") yield return DreamPlatformAssertions(round);
            if (performance && Environment.GetEnvironmentVariable("HOLLOWCONNECT_COVERAGE_PROFILE") == "1") {
                foreach (var group in Entities.Values.GroupBy(e => e.Type)) Mark("TYPE " + group.Key + " count=" + group.Count());
                foreach (var e in Entities.Values.Where(e => e.Type is EntityType.CrystalCrawler or EntityType.CrystallisedHusk or EntityType.CrystalTurret or EntityType.SoulTwister or EntityType.SoulWarrior)) Dump(e.Object.Host);
                costs.Clear();
            }
            if (performance && Environment.GetEnvironmentVariable("HOLLOWCONNECT_COVERAGE_NEAR_TARGET") == "1") {
                var target = Entities.Values.FirstOrDefault(e => e.Type is EntityType.CrystalCrawler or EntityType.CrystallisedHusk or EntityType.SoulTwister or EntityType.SoulWarrior);
                if (target == null) throw new Exception("No native ranged target in " + scene);
                Freeze(); HeroController.instance.transform.position = target.Object.Host.transform.position + new Vector3(4, 0, 0);
                Mark("NATIVE TARGET " + target.Type + " hero=" + HeroController.instance.transform.position);
            }
            if (Environment.GetEnvironmentVariable("HOLLOWCONNECT_COVERAGE_SOUL_PHASE2") == "1") {
                var phase1 = Entities.Values.Single(e => e.Type is EntityType.SoulMaster or EntityType.SoulTyrant);
                var phase2 = Entities.Values.Single(e => e.Type is EntityType.SoulMasterPhase2 or EntityType.SoulTyrantPhase2);
                if (manager.IsSceneHost) {
                    yield return new WaitForSecondsRealtime(3);
                    var health = phase1.Object.Host.GetComponent<HealthManager>();
                    Mark("NATIVE PHASE DAMAGE " + phase1.Type + " hp=" + health.hp);
                    health.ApplyExtraDamage(health.hp);
                }
                yield return Until(() => (manager.IsSceneHost ? phase2.Object.Host : phase2.Object.Client).activeInHierarchy, "native Soul phase 2 activated " + scene);
                if (performance && manager.IsSceneHost) Dump(phase2.Object.Host);
            }
            if(round==0 && Environment.GetEnvironmentVariable("HOLLOWCONNECT_COMBAT_ASSERTIONS")=="1")yield return CombatAssertions();
            if(scene=="GG_Hollow_Knight" && Environment.GetEnvironmentVariable("HOLLOWCONNECT_NATIVE_CONTACT_ASSERTIONS")=="1")yield return PureVesselContact(round);
            if(host && scene=="GG_Uumuu") foreach(var go in Object.FindObjectsOfType<PlayMakerFSM>(true).Where(f=>f.gameObject.scene.name==scene&&EntityReferences.Owner(f.gameObject)==null).Select(f=>f.gameObject).Distinct())Dump(go);
            if(performance && manager.IsSceneHost && Environment.GetEnvironmentVariable("HOLLOWCONNECT_COVERAGE_SOUL_PHASE2") != "1")
                foreach(var e in Entities.Values.Where(e=>e.Type is EntityType.SoulMaster or EntityType.SoulTyrant)) Dump(e.Object.Host);
            if(host && !performance) foreach(var e in Entities.Values.ToArray().Where(e=>e.Object.Host!=null&&e.Object.Host.GetComponent<HealthManager>()!=null)) {Mark("REGISTERED "+e.Id+" "+e.Type);Dump(e.Object.Host);}
            var end=Time.realtimeSinceStartup+seconds;var sample=0f;var frames=0;var start=Time.realtimeSinceStartup;
            var frameTimes = new List<float>(); var phaseStates = new HashSet<string>();
            while(Time.realtimeSinceStartup<end){frames++;if(!performance)Trace(round);else frameTimes.Add(Time.unscaledDeltaTime*1000);if(Time.realtimeSinceStartup>=sample){sample=Time.realtimeSinceStartup+2;
                Mark("SAMPLE "+scene+" entities="+Entities.Count+" active="+Entities.Values.Count(e=>{var o=manager.IsSceneHost?e.Object.Host:e.Object.Client;return o!=null&&o.activeInHierarchy;})+" frames="+frames+" seconds="+(Time.realtimeSinceStartup-start));
                if (performance) {
                    frameTimes.Sort(); Mark("PERF p95ms=" + frameTimes[(int)((frameTimes.Count-1)*.95)] + " maxms=" + frameTimes.Last() +
                        " registrations=" + registrations + " registrationMs=" + registrationMs + " retirements=" + retirements + " retirementMs=" + retirementMs + " replicaSpawns=" + replicaSpawns + " replicaSpawnMs=" + replicaSpawnMs);
                    frameTimes.Clear();
                    foreach (var e in Entities.Values.Where(e => e.Type is EntityType.SoulMasterPhase2 or EntityType.SoulTyrantPhase2 or EntityType.SoulMaster or EntityType.SoulTyrant)) {
                        var go = manager.IsSceneHost ? e.Object.Host : e.Object.Client;
                        if(go==null)continue;
                        Mark("BOSS " + e.Type + " pos=" + go.transform.position + " state=" + string.Join(",", go.GetComponents<PlayMakerFSM>().Select(f => f.FsmName + "=" + f.ActiveStateName)));
                    }
                }
                if(!performance)
                foreach(var e in Entities.Values.ToArray()){var go=manager.IsSceneHost?e.Object.Host:e.Object.Client;if(go==null||(!go.activeInHierarchy&&go.GetComponent<HealthManager>()==null))continue;
                    Mark("LIVE "+e.Id+" "+e.Type+" "+Describe(go)+" pos="+go.transform.position+" state="+string.Join(",",go.GetComponents<PlayMakerFSM>().Select(f=>f.FsmName+"="+f.ActiveStateName))+" colliders="+string.Join(",",go.GetComponentsInChildren<Collider2D>(true).Select(c=>c.gameObject.name+"/"+c.enabled+"/"+c.isTrigger+"/active="+c.gameObject.activeInHierarchy))+" damage="+string.Join(",",go.GetComponentsInChildren<DamageHero>(true).Select(d=>d.gameObject.name+"/"+d.enabled+"/"+d.damageDealt)));
                }
            }
            if(performance && manager.IsSceneHost) foreach(var e in Entities.Values.Where(e=>e.Object.Host!=null && (e.Type is EntityType.SoulMasterPhase2 or EntityType.SoulTyrantPhase2 or EntityType.SoulMaster or EntityType.SoulTyrant or EntityType.CrystalCrawler or EntityType.CrystallisedHusk or EntityType.SoulTwister or EntityType.SoulWarrior)))
                foreach(var f in e.Object.Host.GetComponents<PlayMakerFSM>()) phaseStates.Add(e.Type + "/" + f.ActiveStateName);
            yield return null;}
            if(performance) {
                if(manager.IsSceneHost && (scene=="GG_Soul_Master" || scene=="GG_Soul_Tyrant") && !phaseStates.Any(s=>s.EndsWith("/Quake Down",StringComparison.Ordinal)))
                    throw new Exception("Native Soul slam was not observed " + scene);
                if (manager.IsSceneHost && Environment.GetEnvironmentVariable("HOLLOWCONNECT_COVERAGE_NEAR_TARGET") == "1") {
                    if (scene.StartsWith("Mines_") && !phaseStates.Contains("CrystalCrawler/Beam")) throw new Exception("Native laser attack was not observed");
                    if (scene.StartsWith("Ruins1_") && !spawnCounts.Keys.Any(k => k.StartsWith("Mage Orb", StringComparison.Ordinal))) throw new Exception("Native mage orb spawn was not observed");
                }
                foreach(var pair in spawnCounts)Mark("SPAWN COUNT " + pair.Value + " " + pair.Key);Mark("BOSS STATES " + string.Join(",",phaseStates));spawnCounts.Clear();
                foreach(var times in new[]{(Name:"prefab",Values:prefabTimes),(Name:"process",Values:processTimes)}) {
                    times.Values.Sort();if(times.Values.Count!=0)Mark("COST " + times.Name + " count=" + times.Values.Count + " totalMs=" + times.Values.Sum() + " p95ms=" + times.Values[(int)((times.Values.Count-1)*.95)] + " maxms=" + times.Values.Last());
                    times.Values.Clear();
                }
                foreach (var pair in costs.OrderByDescending(p => p.Value.Ms)) Mark("PROFILE " + pair.Key + " calls=" + pair.Value.Calls + " totalMs=" + pair.Value.Ms + " bytes=" + pair.Value.Bytes);
                costs.Clear();
            }
            Flag(role+".sample."+round);yield return Until(()=>Has((host?"guest":"host")+".sample."+round),"both sampled "+scene);
            foreach(var item in matched.OrderBy(p=>p.Key))Mark("POSITION MATCHES "+scene+" "+item.Key+"="+item.Value);matched.Clear();
            if(!manager.IsSceneHost && scene.StartsWith("GG_Ghost_Markoth")){
                Mark("SHIELD FOLLOW samples="+shieldEvaluated+" matches="+shieldMatched);
                if(shieldEvaluated<10 || shieldMatched<shieldEvaluated*.8)throw new Exception("Shield world position does not follow authority");
            }
            if(!manager.IsSceneHost && scene.StartsWith("GG_Hornet")){
                Mark("NEEDLE visible/active/damaging position matches="+visibleNeedleSamples);
                if(visibleNeedleSamples<3)throw new Exception("No visible damaging native Needle samples on replica");
            }
            if(!manager.IsSceneHost && scene=="GG_Hollow_Knight"){
                Mark("PURE VESSEL active slash/dash hitbox position matches="+pureMeleeSamples);
                if(pureMeleeSamples<3)throw new Exception("No synchronized active native slash/dash hitbox samples on replica");
            }
            shieldEvaluated=shieldMatched=0;
            visibleNeedleSamples=pureMeleeSamples=0;
        }
        foreach(var hook in performanceHooks)hook.Dispose();performanceHooks.Clear();
        yield return ProbeShutdown.Run(engine,root,role);
        Mark("FINAL PASS coverage observations (not all attacks proven)");Flag(role+".done");
    }
    private IEnumerator PureVesselContact(int round) {
        var hero=HeroController.instance;var savedPosition=hero.transform.position;
        var boss=Entities.Values.Single(e=>e.Type==EntityType.PureVessel);
        var body=hero.GetComponent<Rigidbody2D>();var damaged=false;
        On.HeroController.hook_TakeDamage observer=(orig,self,source,side,amount,hazard)=>{
            var before=PlayerData.instance.health;orig(self,source,side,amount,hazard);
            if(source!=null && EntityReferences.Owner(source)==boss && (source.name=="Slash1"||source.name=="Slash2"||source.name=="Slash3"||source.name=="Dash Stab") && PlayerData.instance.health<before)damaged=true;
        };
        On.HeroController.TakeDamage+=observer;
        try {
            hero.takeNoDamage=false;PlayerData.instance.SetBool("isInvincible",false);PlayerData.instance.SetInt("health",9);PlayerData.instance.SetInt("healthBlue",0);
            hero.cState.invulnerable=false;hero.cState.recoiling=false;
            var end=Time.realtimeSinceStartup+50;
            while(!damaged&&Time.realtimeSinceStartup<end) {
                var visible=manager.IsSceneHost?boss.Object.Host:boss.Object.Client;
                var hit=visible.GetComponentsInChildren<Collider2D>().FirstOrDefault(c=>c.enabled&&c.gameObject.activeInHierarchy&&(c.name=="Slash1"||c.name=="Slash2"||c.name=="Slash3"||c.name=="Dash Stab")&&c.GetComponent<DamageHero>() is DamageHero d&&d.enabled&&d.damageDealt>0);
                hero.transform.position=hit!=null?hit.bounds.center:visible.transform.position+new Vector3(2,0,0);
                body.velocity=Vector2.zero;Physics2D.SyncTransforms();Trace(round);yield return null;
            }
        } finally {On.HeroController.TakeDamage-=observer;Freeze();hero.transform.position=savedPosition;}
        if(!damaged)throw new Exception("Native Pure Vessel slash/dash did not damage "+(manager.IsSceneHost?"authority":"replica"));
        Mark("PASS actual HeroBox damage from native Pure Vessel slash/dash "+(manager.IsSceneHost?"authority":"replica"));
        Flag(role+".nativecontact."+round);yield return Until(()=>Has((host?"guest":"host")+".nativecontact."+round),"both native Pure Vessel melee damage");
    }
    private IEnumerator CombatAssertions() {
        var h=HeroController.instance;var heroPosition=h.transform.position;
        var go=new GameObject("Needle_HollowConnectFixture");go.layer=11;go.transform.position=new Vector3(400,400,0);
        go.AddComponent<DamageHero>().damageDealt=0;var body=go.AddComponent<Rigidbody2D>();body.bodyType=RigidbodyType2D.Kinematic;
        var group=new GameObject("Empty parent");group.transform.SetParent(go.transform,false);group.SetActive(false);
        var boxObject=new GameObject("Child hitbox");boxObject.layer=11;boxObject.transform.SetParent(group.transform,false);
        var box=boxObject.AddComponent<BoxCollider2D>();box.isTrigger=true;box.size=Vector2.one;
        var damage=boxObject.AddComponent<DamageHero>();damage.damageDealt=0;
        var renderer=boxObject.AddComponent<SpriteRenderer>();renderer.enabled=false;
        var material=new Material(Shader.Find("Sprites/Default"));renderer.sharedMaterial=material;
        var processor=new EntityProcessor{GameObject=go,IsSceneHost=manager.IsSceneHost,IsSceneHostDetermined=true,LateLoad=true}.Process();
        if(!processor.Success)throw new Exception("fixture registration failed");var e=processor.Entities[0];
        Flag(role+".fixture");yield return Until(()=>Has((host?"guest":"host")+".fixture"),"fixture both registered");
        if(manager.IsSceneHost){go.transform.rotation=Quaternion.Euler(0,0,30);group.SetActive(true);group.transform.localPosition=new Vector3(3,4,0);box.size=new Vector2(3,2);box.offset=new Vector2(.25f,-.5f);damage.damageDealt=1;renderer.enabled=true;renderer.color=new Color(.1f,.2f,.3f,.25f);material.color=new Color(.4f,.5f,.6f,.75f);}
        var visible=manager.IsSceneHost?e.Object.Host:e.Object.Client;
        yield return Until(()=>visible.transform.Find("Empty parent").gameObject.activeInHierarchy&&visible.GetComponentInChildren<DamageHero>().damageDealt==0&&visible.transform.Find("Empty parent/Child hitbox").GetComponent<DamageHero>().damageDealt==1,"nested damage parent activation");
        var actual=visible.transform.Find("Empty parent/Child hitbox").GetComponent<BoxCollider2D>();
        var expectedPosition=new Vector3(400,400,0)+Quaternion.Euler(0,0,30)*new Vector3(3,4,0);
        if(actual.size!=new Vector2(3,2)||actual.offset!=new Vector2(.25f,-.5f)||Vector3.Distance(actual.transform.position,expectedPosition)>.01f||Quaternion.Angle(visible.transform.rotation,Quaternion.Euler(0,0,30))>.01f)throw new Exception("nested hitbox geometry/root rotation mismatch");
        Mark("PASS nested hitbox size/offset/world position and root rotation");
        var actualRenderer=actual.GetComponent<SpriteRenderer>();
        yield return Until(()=>actualRenderer.enabled&&Math.Abs(actualRenderer.color.a-.25f)<.001f,"nested renderer alpha");
        if(!manager.IsSceneHost){var block=new MaterialPropertyBlock();actualRenderer.GetPropertyBlock(block);if(Math.Abs(block.GetColor("_Color").a-.75f)>.001f||Math.Abs(material.color.a-1)>.001f)throw new Exception("Material tint sync changed shared local asset");Mark("PASS material tint through property block without shared-asset mutation");}
        var recoil=e.Object.Client.GetComponent<Recoil>()??e.Object.Client.AddComponent<Recoil>();
        recoil.RecoilByDirection(0,1);
        if(recoil.IsRecoiling || e.Object.Client.GetComponent<Rigidbody2D>().velocity!=Vector2.zero)throw new Exception("Replica integrates native recoil");
        Mark("PASS replica recoil does not add local motion");
        yield return new WaitForSecondsRealtime(4);
        var damaged=false;
        On.HeroController.hook_TakeDamage observer=(orig,self,source,side,amount,hazard)=>{var before=PlayerData.instance.health;orig(self,source,side,amount,hazard);if(source==actual.gameObject&&PlayerData.instance.health<before)damaged=true;};
        On.HeroController.TakeDamage+=observer;
        h.takeNoDamage=false;PlayerData.instance.SetBool("isInvincible",false);PlayerData.instance.SetInt("health",9);h.cState.invulnerable=false;h.cState.recoiling=false;
        h.transform.position=actual.bounds.center;h.GetComponent<Rigidbody2D>().velocity=Vector2.zero;Physics2D.SyncTransforms();
        var deadline=Time.realtimeSinceStartup+6;while(!damaged&&Time.realtimeSinceStartup<deadline)yield return null;
        On.HeroController.TakeDamage-=observer;Freeze();h.transform.position=heroPosition;
        if(!damaged)throw new Exception("native HeroBox did not receive child collider damage");
        Mark("PASS actual HeroBox contact damage "+(manager.IsSceneHost?"authority":"replica"));
        Flag(role+".fixture.damage");yield return Until(()=>Has((host?"guest":"host")+".fixture.damage"),"both physics damage");
        if(manager.IsSceneHost)group.SetActive(false);
        yield return Until(()=>!visible.transform.Find("Empty parent").gameObject.activeInHierarchy,"attack parent disabled");
        Flag(role+".fixture.disabled");yield return Until(()=>Has((host?"guest":"host")+".fixture.disabled"),"both attack parent disabled");
        e.Destroy();Entities.Remove(e.Id);Object.Destroy(go);Object.Destroy(material);
        Flag(role+".fixture.done");yield return Until(()=>Has((host?"guest":"host")+".fixture.done"),"fixture cleanup");
    }
}
