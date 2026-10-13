using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using Hkmp.Game.Client.Entity;
using Hkmp.Game.Client.Entity.Action;
using HutongGames.PlayMaker.Actions;
using Hkmp.Networking.Client;
using Hkmp.Networking.Packet;
using Hkmp.Networking.Packet.Data;
using Hkmp.Networking.Packet.Update;
using HollowConnect;
using Modding;
using UnityEngine;
using Object = UnityEngine.Object;

namespace HollowConnect.Smoke;

// Exercise the actual first-pantheon entry, late entry, death transitions and
// repeated attempts. No player save is opened and no boss FSM state is forced.
public sealed class PantheonProbe : MonoBehaviour {
    private string role, root;
    private bool host;
    private CoopRuntime runtime;
    private Hkmp.Game.GameManager engine;
    private EntityManager manager;
    private int attemptNumber;
    private readonly List<ClientPlayerAlreadyInScene> snapshots = new();
    private readonly object logLock = new object();
    private static T Field<T>(object o, string n) {
        for(var type=o.GetType();type!=null;type=type.BaseType) {
            var field=type.GetField(n,BindingFlags.Instance|BindingFlags.NonPublic|BindingFlags.Public|BindingFlags.DeclaredOnly);
            if(field!=null)return (T)field.GetValue(o);
        }
        throw new MissingFieldException(o.GetType().FullName,n);
    }
    private Dictionary<ulong, Entity> Entities => Field<Dictionary<ulong, Entity>>(manager, "_entities");
    private bool Has(string n) => File.Exists(Path.Combine(root, n));
    private void Flag(string n, string value="ready") => File.WriteAllText(Path.Combine(root,n),value);
    private void Mark(string value) { lock(logLock) File.AppendAllText(Path.Combine(root,role+".log"),DateTime.UtcNow.ToString("O")+" "+value+"\n"); }
    private IEnumerator Until(Func<bool> ready,string label,float seconds=45) {
        var end=Time.realtimeSinceStartup+seconds;
        while(!ready() && Time.realtimeSinceStartup<end)yield return null;
        if(!ready())throw new Exception("Timeout: "+label);
        Mark("PASS "+label);
    }
    private bool In(string scene)=>runtime.Playable && GameManager.instance.sceneName==scene;
    private void Enter(string scene)=>GameManager.instance.BeginSceneTransition(new GameManager.SceneLoadInfo {
        SceneName=scene,EntryGateName=scene.StartsWith("GG_")?"door_dreamEnter":"left1",
        Visualization=GameManager.SceneLoadVisualizations.Default,WaitForSceneTransitionCameraFade=true
    });
    private void SafeHero() { HeroController.instance.takeNoDamage=true; }
    private void ConnectLocal(string name) {
        // The in-memory character is already selected, just as on the native
        // hosting path. Do not open asynchronous save-slot menus in this fixture.
        typeof(Hkmp.Game.Client.ClientManager).GetField("_autoConnect",BindingFlags.Instance|BindingFlags.NonPublic).SetValue(engine.ClientManager,true);
        typeof(Hkmp.Game.Client.ClientManager).GetMethod("Connect",BindingFlags.Instance|BindingFlags.NonPublic).Invoke(engine.ClientManager,new object[]{"127.0.0.1",29650,name});
    }
    private GameObject Visible(Entity e)=>manager.IsSceneHost?e.Object.Host:e.Object.Client;
    private IEnumerator NativeEntrance(BossSequence sequence) {
        Enter("GG_Atrium");yield return Until(()=>In("GG_Atrium"),"native atrium");SafeHero();
        yield return new WaitForSecondsRealtime(1);
        var door=Object.FindObjectsOfType<BossSequenceDoor>(true).Single(d=>d.bossSequence!=null&&d.bossSequence.name==sequence.name);
        var fsm=door.challengeFSM;
        var menu=fsm.FsmStates.Single(s=>s.Actions.Any(a=>a is ShowBossDoorChallengeUI));
        // Select the real door menu programmatically. Boss FSMs are not forced;
        // Begin() drives its native UI event and entrance cinematic.
        // Fresh unsaved characters have locked pantheons. Expose only this
        // test door hierarchy so its native wait/animation updates can run.
        for(var t=fsm.transform;t!=null;t=t.parent)t.gameObject.SetActive(true);
        HeroController.instance.transform.position=new Vector3(door.transform.position.x,HeroController.instance.transform.position.y,0);
        fsm.enabled=true;
        yield return null;
        EntityInitializer.InitializeFsm(fsm);fsm.Fsm.Start();fsm.SetState(menu.Name);
        yield return Until(()=>Object.FindObjectOfType<BossDoorChallengeUI>()!=null,"native pantheon selection UI");
        yield return new WaitForSecondsRealtime(1);
        var ui=(BossDoorChallengeUI)typeof(ShowBossDoorChallengeUI).GetField("spawnedUI",BindingFlags.Static|BindingFlags.NonPublic).GetValue(null);
        Mark("NATIVE UI begin subscribers="+(Field<Delegate>(ui,"OnBegin")?.GetInvocationList().Length??0)+" door="+Field<BossSequenceDoor>(ui,"door").name+" fsm-active="+fsm.Fsm.Active+" hierarchy="+fsm.gameObject.activeInHierarchy);
        ui.Begin();
        yield return new WaitForSecondsRealtime(2);
        Mark("NATIVE DOOR state="+fsm.ActiveStateName+" timescale="+Time.timeScale+" hero="+HeroController.instance.transform.position);
        yield return Until(()=>UnityEngine.SceneManagement.SceneManager.GetActiveScene().name=="GG_Boss_Door_Entrance","native door entrance cinematic");
        // New characters must acknowledge the native Godseeker dialogue.
        // Complete text via its public paging API, leaving the entrance FSM
        // and its scene-transition branch in control.
        var end=Time.realtimeSinceStartup+60;var last="";
        while(!In(sequence.GetSceneAt(0))&&Time.realtimeSinceStartup<end) {
            var cine=Object.FindObjectsOfType<PlayMakerFSM>().FirstOrDefault(f=>f.gameObject.scene.name=="GG_Boss_Door_Entrance"&&f.FsmName=="Conversation Control");
            var state=cine==null?"none":cine.ActiveStateName;
            if(state!=last){Mark("CINEMATIC state="+state);last=state;}
            foreach(var box in Object.FindObjectsOfType<DialogueBox>()) {
                if(!string.IsNullOrEmpty(box.currentConversation)&&box.currentPage>0) {
                    box.useTypeWriter=false;box.SpeedupTypewriter();
                    var pages=box.GetComponents<PlayMakerFSM>().FirstOrDefault(f=>f.FsmName=="Dialogue Page Control");
                    if(pages!=null&&(pages.ActiveStateName=="Page End"||pages.ActiveStateName=="Conversation End")) {
                        Mark("NATIVE dialogue input NEXT "+box.currentConversation+"/"+pages.ActiveStateName);
                        pages.SendEvent("NEXT");
                    }
                }
            }
            yield return new WaitForSecondsRealtime(.5f);
        }
        yield return Until(()=>In(sequence.GetSceneAt(0)),"native door loads first boss",1);
    }
    private sealed class Log : Hkmp.Logging.ILogger {
        private readonly PantheonProbe p;
        internal Log(PantheonProbe p){this.p=p;}
        public void Info(string s)=>p.Mark("ENGINE "+s);
        public void Debug(string s){}
        public void Fine(string s){}
        public void Warn(string s)=>p.Mark("ENGINE WARN "+s);
        public void Error(string s){p.Mark("ENGINE ERROR "+s);p.Flag(p.role+".failed",s);}
    }
    private IEnumerator Start() {
        role=Environment.GetEnvironmentVariable("HOLLOWCONNECT_SMOKE_ROLE");root=Environment.GetEnvironmentVariable("HOLLOWCONNECT_SMOKE_ROOT");host=role=="host";
        var stack=new Stack<IEnumerator>();stack.Push(Run());var end=DateTime.UtcNow.AddMinutes(12);
        while(stack.Count!=0) {
            bool moved;object current=null;
            try { if(DateTime.UtcNow>end)throw new TimeoutException("Pantheon probe deadline");moved=stack.Peek().MoveNext();if(moved)current=stack.Peek().Current; }
            catch(Exception e){Mark("FAIL "+e);Flag(role+".failed",e.ToString());yield break;}
            if(!moved){stack.Pop();continue;}if(current is IEnumerator nested)stack.Push(nested);else yield return current;
        }
    }
    private IEnumerator Both(string key) {Flag(role+"."+key);yield return Until(()=>Has((host?"guest":"host")+"."+key),"both "+key);}
    private IEnumerator Finish() {
        yield return Both("probe-checked");
        engine.ClientManager.Disconnect();
        yield return Until(()=>!runtime.Connected,"final client disconnect");
        yield return Both("probe-disconnected");
        if(host){
            yield return Until(()=>engine.ServerManager.Players.Count==0,"server acknowledges final disconnects",10);
            engine.ServerManager.Stop();Flag("server.stopped");
        }
        yield return Until(()=>Has("server.stopped"),"final server stopped");
        if(!Has(role+".failed"))Flag(role+".done");
    }
    private void DelayInitialSnapshot() {
        var net=Field<NetClient>(engine.ClientManager,"_netClient");
        var packetManager=Field<PacketManager>(net,"_packetManager");
        var handlers=Field<Dictionary<ClientUpdatePacketId,ClientPacketHandler>>(packetManager,"_clientUpdatePacketHandlers");
        var original=handlers[ClientUpdatePacketId.PlayerAlreadyInScene];
        handlers[ClientUpdatePacketId.PlayerAlreadyInScene]=data=>{
            var snapshot=(ClientPlayerAlreadyInScene)data;
            snapshots.Add(snapshot);
            if(!snapshot.SceneHost&&snapshot.SceneName.StartsWith("GG_"))StartCoroutine(Deliver());else original(data);
            IEnumerator Deliver(){Mark("DELAY SNAPSHOT "+snapshot.SceneName+" visit="+snapshot.Visit);if(snapshot.SceneName=="GG_Vengefly")Flag(role+".snapshot-ready."+attemptNumber);yield return new WaitForSecondsRealtime(3);original(data);}
        };
    }
    private void DumpRoom(string label) {
        Mark("ROOM "+label+" scene="+Hkmp.CoopContext.LocalScene+" visit="+Hkmp.CoopContext.LocalVisit+" authority="+manager.IsSceneHost+" hero="+HeroController.instance.transform.position+
            " camera="+GameCameras.instance.cameraController.transform.position+" mode="+GameCameras.instance.cameraController.mode);
        foreach(var e in Entities.Values.ToArray()) {
            var go=Visible(e);if(e.Object.Host==null)continue;
            Mark("ENTITY "+e.Id+" "+e.Type+" key="+EntityIdentity.Key(e.Object.Host,e.Type)+" host="+e.Object.Host.activeInHierarchy+" replica="+(e.Object.Client!=null&&e.Object.Client.activeInHierarchy)+
                " pos="+(go==null?"null":go.transform.position.ToString())+" render="+(go==null?"null":string.Join(",",go.GetComponentsInChildren<Renderer>(true).Select(r=>r.name+"/"+r.enabled+"/off="+r.forceRenderingOff)))+
                " state="+string.Join(",",e.Object.Host.GetComponents<PlayMakerFSM>().Select(f=>f.FsmName+"="+f.ActiveStateName)));
        }
    }
    private IEnumerator RoomChecks(string key) {
        yield return Until(()=>manager.IsSceneHostDetermined,"room authority "+key);
        yield return new WaitForSecondsRealtime(3);
        if(Field<IEnumerable>(manager,"_receivedUpdates").Cast<object>().Any(p=>((BaseEntityUpdate)p.GetType().GetField("Update").GetValue(p)).Id==0))
            throw new Exception("Early room-level updates were never applied "+key);
        Mark("PASS early room-level updates drained "+key);
        DumpRoom(key);
        var cameraEntities=Entities.Values.Where(e=>e.Type==EntityType.CameraLockArea).ToArray();
        if(manager.IsSceneHost)Flag("camera."+key,string.Join(",",cameraEntities.Select(e=>e.Id+":"+Visible(e).activeInHierarchy)));
        yield return Until(()=>Has("camera."+key),"camera authority state "+key);
        var expectedCamera=File.ReadAllText(Path.Combine(root,"camera."+key));
        var actualCamera=string.Join(",",cameraEntities.Select(e=>e.Id+":"+Visible(e).activeInHierarchy));
        if(actualCamera!=expectedCamera)throw new Exception("Camera lock state diverged "+key+" expected="+expectedCamera+" actual="+actualCamera);
        Mark("PASS camera lock state converges "+key);
        var live=Entities.Values.Where(e=>(e.Id>>63)==0&&e.Type!=EntityType.FalseKnightHead&&e.Object.Host!=null&&e.Object.Host.GetComponent<HealthManager>() is HealthManager h&&!h.isDead&&h.hp>0).ToArray();
        if(live.Length==0)throw new Exception("No registered living boss: "+key);
        foreach(var e in live) {
            if(!manager.IsSceneHost && (e.Object.Host.activeInHierarchy || e.Object.Client.GetComponents<PlayMakerFSM>().Any(f=>f.enabled)))
                throw new Exception("Replica is running a separate native boss "+key);
            var go=Visible(e);
            if(go==null||!go.activeInHierarchy||!go.GetComponentsInChildren<Renderer>(true).Any(r=>r.enabled&&!r.forceRenderingOff&&r.gameObject.activeInHierarchy))
                throw new Exception("Living boss invisible "+key+" id="+e.Id+" type="+e.Type);
        }
        var players=Field<Dictionary<ushort,Hkmp.Game.Client.ClientPlayerData>>(engine.ClientManager,"_playerData");
        if(!players.Values.Any(p=>p.IsInLocalScene&&p.PlayerObject!=null&&p.PlayerObject.activeInHierarchy&&p.PlayerObject.GetComponentsInChildren<Renderer>(true).Any(r=>r.enabled&&!r.forceRenderingOff&&r.gameObject.activeInHierarchy)))
            throw new Exception("Remote knight invisible "+key);
        if(GameCameras.instance.cameraController.mode==CameraController.CameraMode.FROZEN)throw new Exception("Frozen camera "+key);
        Mark("PASS visible bosses/knight and unfrozen camera "+key);
        if(!manager.IsSceneHost) {
            var snapshot=snapshots.Last(s=>s.Visit==Hkmp.CoopContext.LocalVisit&&s.SceneName==Hkmp.CoopContext.LocalScene);
            typeof(Hkmp.Game.Client.ClientManager).GetMethod("OnPlayerAlreadyInScene",BindingFlags.Instance|BindingFlags.NonPublic).Invoke(engine.ClientManager,new object[]{snapshot});
            if(live.Any(e=>!Visible(e).activeInHierarchy))throw new Exception("Duplicate initial snapshot hides boss "+key);
            Mark("PASS duplicate initial snapshot does not reset boss "+key);
        }
        var peer=players.Values.First(p=>p.IsInLocalScene);var avatar=peer.PlayerObject;
        foreach(var leave in new[]{
            new ClientPlayerLeaveScene{Id=peer.Id,SceneName=Hkmp.CoopContext.LocalScene,Epoch=Hkmp.CoopContext.Epoch,Visit=Hkmp.CoopContext.LocalVisit-1,PlayerVisit=peer.ScenePresence.Visit},
            new ClientPlayerLeaveScene{Id=peer.Id,SceneName=Hkmp.CoopContext.LocalScene,Epoch=Hkmp.CoopContext.Epoch,Visit=Hkmp.CoopContext.LocalVisit,PlayerVisit=peer.ScenePresence.Visit-1}})
            typeof(Hkmp.Game.Client.ClientManager).GetMethod("OnPlayerLeaveScene",BindingFlags.Instance|BindingFlags.NonPublic).Invoke(engine.ClientManager,new object[]{leave});
        typeof(Hkmp.Game.Client.ClientManager).GetMethod("OnPlayerConnect",BindingFlags.Instance|BindingFlags.NonPublic).Invoke(engine.ClientManager,new object[]{new PlayerConnect{Id=peer.Id,Username="duplicate"}});
        if(!peer.IsInLocalScene||peer.PlayerObject!=avatar||!avatar.activeInHierarchy||players[peer.Id]!=peer)throw new Exception("Stale presence removes current teammate "+key);
        Mark("PASS stale room/peer leave and duplicate connect preserve avatar "+key);
        var boss=live.OrderBy(e=>e.Id).First();
        Flag(role+".boss."+key,boss.Id.ToString());
        yield return Both("checked."+key);
        var otherId=ulong.Parse(File.ReadAllText(Path.Combine(root,(host?"guest":"host")+".boss."+key)));
        if(otherId!=boss.Id)throw new Exception("Divergent boss identity "+key+" local="+boss.Id+" other="+otherId);
        if(manager.IsSceneHost) {
            boss.Object.Host.GetComponent<HealthManager>().ApplyExtraDamage(7);
            Flag("hp."+key,boss.Object.Host.GetComponent<HealthManager>().hp.ToString());
        }
        yield return Until(()=>Has("hp."+key),"damage published "+key);
        var hp=int.Parse(File.ReadAllText(Path.Combine(root,"hp."+key)));
        yield return Until(()=>Visible(boss)!=null&&Visible(boss).GetComponent<HealthManager>().hp==hp,"same boss HP "+key);
        yield return Both("hpchecked."+key);
    }
    private IEnumerator HornetNailChecks(string key) {
        var boss=Entities.Values.Single(e=>e.Type==EntityType.Hornet);
        foreach(var actor in new[]{"host","guest"}) {
            var label=key+"."+actor;
            if(manager.IsSceneHost)Flag("nail-base."+label,boss.Object.Host.GetComponent<HealthManager>().hp.ToString());
            yield return Until(()=>Has("nail-base."+label),"authority nail baseline "+label);
            var hp=int.Parse(File.ReadAllText(Path.Combine(root,"nail-base."+label)));
            var end=Time.realtimeSinceStartup+25;
            while(!Has("nail-hit."+label)&&Time.realtimeSinceStartup<end) {
                if(role==actor) {
                    var hero=HeroController.instance;var target=Visible(boss);
                    hero.RegainControl();hero.StartAnimationControl();hero.FaceRight();
                    hero.GetComponent<Rigidbody2D>().gravityScale=0;hero.GetComponent<Rigidbody2D>().velocity=Vector2.zero;
                    hero.transform.position=target.transform.position+new Vector3(-1.5f,0,0);
                    Physics2D.SyncTransforms();hero.Attack(GlobalEnums.AttackDirection.normal);
                }
                yield return new WaitForSecondsRealtime(.45f);
                if(manager.IsSceneHost && boss.Object.Host.GetComponent<HealthManager>().hp<hp)Flag("nail-hit."+label);
            }
            if(!Has("nail-hit."+label))throw new Exception("Actual "+actor+" nail did not damage authoritative Hornet");
            HeroController.instance.GetComponent<Rigidbody2D>().gravityScale=1;
            yield return Both("nail-stop."+label);yield return new WaitForSecondsRealtime(1);
            if(manager.IsSceneHost)Flag("nail-final."+label,boss.Object.Host.GetComponent<HealthManager>().hp.ToString());
            yield return Until(()=>Has("nail-final."+label),"authority accepted real nail "+label);
            var expected=int.Parse(File.ReadAllText(Path.Combine(root,"nail-final."+label)));
            yield return Until(()=>Visible(boss).GetComponent<HealthManager>().hp==expected,"real nail HP converges "+label);
            yield return Both("nail-checked."+label);
        }
    }
    private IEnumerator Run() {
        yield return Until(()=>Object.FindObjectOfType<CoopRuntime>()!=null&&GameManager.instance!=null&&UnityEngine.SceneManagement.SceneManager.GetActiveScene().name=="Menu_Title","menu");
        runtime=Object.FindObjectOfType<CoopRuntime>();engine=Field<Hkmp.Game.GameManager>(ModHooks.GetMod("HKMP"),"_gameManager");manager=Field<EntityManager>(engine.ClientManager,"_entityManager");
        engine.ClientManager.ConnectEvent += DelayInitialSnapshot;
        Hkmp.Logging.Logger.AddLogger(new Log(this));Field<Hkmp.Game.Settings.ModSettings>(engine.ClientManager,"_modSettings").AuthKey=Hkmp.Util.AuthUtil.GenerateAuthKey();
        Application.logMessageReceived += (message, trace, type) => {
            if(type==LogType.Exception) Mark("UNITY EXCEPTION "+message+"\n"+trace);
        };
        CoopMod.Settings.ShareProgress=false;PlayerData.instance.SetString("respawnScene","Town");PlayerData.instance.SetString("respawnMarkerName","RestBench");PlayerData.instance.SetInt("respawnType",1);
        GameManager.instance.profileID=99;GameManager.instance.StartCoroutine(GameManager.instance.RunContinueGame());
        yield return Until(()=>In("Town"),"unsaved character");SafeHero();
        if(host){engine.ServerManager.Start(29650,false);Flag("server.started");}else yield return Until(()=>Has("server.started"),"server ready");
        ConnectLocal("pantheon"+role);
        yield return Until(()=>runtime.Connected,"handshake");yield return Both("connected");
        if(Environment.GetEnvironmentVariable("HOLLOWCONNECT_SAME_ROOM_CHECK")=="1") {
            yield return Until(()=>manager.IsSceneHostDetermined,"connect in already loaded room initializes authority",8);
            yield return Until(()=>Field<Dictionary<ushort,Hkmp.Game.Client.ClientPlayerData>>(engine.ClientManager,"_playerData").Values.Any(p=>p.IsInLocalScene&&p.PlayerObject!=null&&p.PlayerObject.activeInHierarchy),"connect in already loaded room creates teammate",8);
            yield return Both("same-room-initialized");
            engine.ClientManager.Disconnect();yield return Until(()=>!runtime.Connected,"disconnect without scene change");yield return Both("same-room-disconnected");
            if(host) {
                yield return Until(()=>engine.ServerManager.Players.Count==0,"server acknowledged both disconnects",5);
                if(Field<IEnumerable>(engine.ServerManager,"_entityData").Cast<object>().Any())throw new Exception("Empty room retained entity snapshots after all players disconnected");
                Mark("PASS empty disconnected room clears cached entity state");
            }
            yield return Both("same-room-empty");
            ConnectLocal("sameroomretry"+role);
            yield return Until(()=>runtime.Connected&&manager.IsSceneHostDetermined,"same-room reconnect initializes authority",30);
            yield return Until(()=>Field<Dictionary<ushort,Hkmp.Game.Client.ClientPlayerData>>(engine.ClientManager,"_playerData").Values.Any(p=>p.IsInLocalScene&&p.PlayerObject!=null&&p.PlayerObject.activeInHierarchy),"same-room reconnect creates teammate",8);
            Mark("FINAL PASS connect without loading a new room; muted audio");yield return Finish();yield break;
        }
        var sequence=Resources.LoadAll<BossSequence>("GG").Single(s=>s.name=="Boss Sequence Tier 1");
        Mark("SEQUENCE "+string.Join(",",Enumerable.Range(0,sequence.Count).Select(i=>i+"/"+sequence.GetSceneAt(i))));
        var first=Environment.GetEnvironmentVariable("HOLLOWCONNECT_PANTHEON_AUTHORITY")??"guest";
        if(Environment.GetEnvironmentVariable("HOLLOWCONNECT_LIFECYCLE_CHECK")=="1") {
            BossSequenceController.Reset();BossSequenceController.SetupNewSequence(sequence,BossSequenceController.ChallengeBindings.None,"bossDoorStateTier1");
            typeof(BossSequenceController).GetField("bossIndex",BindingFlags.Static|BindingFlags.NonPublic).SetValue(null,3);
            if(role!=first)yield return Until(()=>Has(first+".moss-entered"),"first enters moss");
            Enter(sequence.GetSceneAt(3));yield return Until(()=>In(sequence.GetSceneAt(3)),"moss room");SafeHero();
            if(role==first){yield return Until(()=>manager.IsSceneHostDetermined,"first moss authority");Flag(role+".moss-entered");}
            yield return Both("moss");yield return RoomChecks("lifecycle-moss");
            if(role!=first) {
                var boss=Entities.Values.Single(e=>e.Type==EntityType.MassiveMossCharger);
                var action=boss.GetClientFsms().SelectMany(f=>f.FsmStates).SelectMany(s=>s.Actions).OfType<AudioPlayInState>().First();
                var audioObject=action.Fsm.GetOwnerDefaultTarget(action.gameObject);
                if(audioObject==null||audioObject.GetComponent<AudioSource>()==null)throw new Exception("Native moss loop source unavailable");
                EntityFsmActions.ApplyNetworkDataFromAction(null,action);
                // A native death/unload can destroy an audio target before the
                // network FSM's exit callback. Force that ordering, not AI state.
                Object.Destroy(audioObject.GetComponent<AudioSource>());yield return null;
                Flag("destroyed-loop-source");Mark("FIXTURE native loop audio destroyed before replica exit");
            }else yield return Until(()=>Has("destroyed-loop-source"),"replica loop target destroyed");
            yield return Both("destroyed-loop");
            if(role==first)yield return Defeat(sequence.GetSceneAt(3),sequence.GetSceneAt(4));
            else yield return Until(()=>In(sequence.GetSceneAt(4)),"replica follows native completion",100);
            SafeHero();yield return Both("hornet-entered");yield return RoomChecks("lifecycle-hornet");
            // Native scene switch and same-session rejoin must not retain Moss
            // entities, duplicate scene hooks, or a previous visit's callbacks.
            if(Entities.Values.Any(e=>e.SourceScene==sequence.GetSceneAt(3)))throw new Exception("Prior room entities survived transition");
            engine.ClientManager.Disconnect();yield return Both("lifecycle-disconnected");
            ConnectLocal("liferetry"+role);yield return Until(()=>runtime.Connected,"reconnect after destroyed-loop cleanup");
            yield return Until(()=>manager.IsSceneHostDetermined,"rejoined room authority");
            yield return Both("lifecycle-reconnected");yield return RoomChecks("lifecycle-retry");
            Mark("FINAL PASS destroyed native loop cleanup, Hornet authority and reconnect; muted audio");yield return Finish();yield break;
        }
        for(var attempt=0;attempt<2;attempt++) {
            attemptNumber=attempt;
            var nativeEntrance=Environment.GetEnvironmentVariable("HOLLOWCONNECT_NATIVE_ENTRANCE")=="1";
            BossSequenceController.Reset();
            if(!nativeEntrance)BossSequenceController.SetupNewSequence(sequence,BossSequenceController.ChallengeBindings.None,"bossDoorStateTier1");
            if(role!=first)yield return Until(()=>Has(first+".started."+attempt),"first entrant starts fighting",150);
            if(nativeEntrance)yield return NativeEntrance(sequence);
            else Enter(sequence.GetSceneAt(0));
            yield return Until(()=>In(sequence.GetSceneAt(0)),"first boss entry");SafeHero();
            if(role==first){
                yield return new WaitForSecondsRealtime(3);
                var boss=Entities.Values.Single(e=>e.Type==EntityType.VengeflyKing);
                var camera=Entities.Values.Single(e=>e.Type==EntityType.CameraLockArea);
                if(!nativeEntrance){boss.Object.Host.SetActive(false);camera.Object.Host.SetActive(false);}
                yield return new WaitForSecondsRealtime(.3f);Flag(role+".started."+attempt);
                yield return Until(()=>Has((first=="host"?"guest":"host")+".snapshot-ready."+attempt),"late snapshot captured",nativeEntrance?75:20);
                if(!nativeEntrance){boss.Object.Host.SetActive(true);camera.Object.Host.SetActive(true);}
                if(Environment.GetEnvironmentVariable("HOLLOWCONNECT_PENDING_PEER_CHECK")=="1") {
                    var hero=HeroController.instance;
                    hero.RelinquishControl();hero.GetComponent<Rigidbody2D>().gravityScale=0;hero.GetComponent<Rigidbody2D>().velocity=Vector2.zero;
                    hero.transform.position+=new Vector3(5,3,0);
                    Flag("peer-anchor."+attempt,hero.transform.position.x.ToString(System.Globalization.CultureInfo.InvariantCulture));
                }
                if(Environment.GetEnvironmentVariable("HOLLOWCONNECT_EARLY_TRANSFER")=="1") {
                    if(Environment.GetEnvironmentVariable("HOLLOWCONNECT_EARLY_DISCONNECT")=="1") {
                        engine.ClientManager.Disconnect();yield return Until(()=>!runtime.Connected,"authority disconnects before delayed base snapshot");
                    }else {Enter("Town");yield return Until(()=>In("Town"),"authority leaves before delayed base snapshot");SafeHero();}
                }
            }
            yield return Both("startroom."+attempt);
            if(Environment.GetEnvironmentVariable("HOLLOWCONNECT_EARLY_TRANSFER")=="1") {
                if(role!=first) {
                    yield return Until(()=>manager.IsSceneHost&&manager.IsSceneHostDetermined,"early transfer determines remaining player's authority",8);
                    yield return new WaitForSecondsRealtime(4);
                    if(!manager.IsSceneHost||!manager.IsSceneHostDetermined)throw new Exception("Delayed initial snapshot reverted the transferred authority");
                    Mark("PASS old initial snapshot cannot undo early authority handoff");
                    var boss=Entities.Values.Single(e=>e.Type==EntityType.VengeflyKing);
                    // The fixture intentionally leaves the arena without a
                    // target; verify takeover through state and damage rather
                    // than requiring an AI movement decision.
                    var health=boss.Object.Host.GetComponent<HealthManager>();var hp=health.hp;health.ApplyExtraDamage(7);
                    if(health.hp!=hp-7)throw new Exception("Transferred boss cannot receive damage");
                    Mark("PASS transferred boss continues taking damage");
                }
                yield return Both("handoff");Mark("FINAL PASS early handoff before base snapshot; muted audio");yield return Finish();yield break;
            }
            for(var index=0;index<5;index++) {
                var scene=sequence.GetSceneAt(index);var key=attempt+"."+index;
                yield return Until(()=>In(scene),"native sequence room "+key);SafeHero();
                yield return Both("room."+key);yield return RoomChecks(key);
                if(index==0&&Environment.GetEnvironmentVariable("HOLLOWCONNECT_PENDING_PEER_CHECK")=="1") {
                    if(role!=first) {
                        var target=float.Parse(File.ReadAllText(Path.Combine(root,"peer-anchor."+attempt)),System.Globalization.CultureInfo.InvariantCulture);
                        var peer=Field<Dictionary<ushort,Hkmp.Game.Client.ClientPlayerData>>(engine.ClientManager,"_playerData").Values.Single(p=>p.IsInLocalScene);
                        Mark("PEER ANCHOR target="+target.ToString(System.Globalization.CultureInfo.InvariantCulture)+" actual="+peer.PlayerContainer.transform.position.x.ToString(System.Globalization.CultureInfo.InvariantCulture)+" id="+peer.Id+" visit="+peer.ScenePresence.Visit);
                        yield return Until(()=>Math.Abs(peer.PlayerContainer.transform.position.x-target)<.25f,"stationary teammate latest position survives delayed snapshot",12);
                    }
                    yield return Both("anchor."+attempt);
                    if(Environment.GetEnvironmentVariable("HOLLOWCONNECT_STOP_AFTER_PENDING")=="1") {
                        Mark("FINAL PASS delayed snapshot peer position; muted audio");yield return Finish();yield break;
                    }
                    if(role==first){HeroController.instance.GetComponent<Rigidbody2D>().gravityScale=1;HeroController.instance.RegainControl();}
                }
                if(index==4){
                    if(nativeEntrance){
                        yield return HornetNailChecks(key);
                        if(manager.IsSceneHost)yield return Defeat(scene,sequence.GetSceneAt(5));
                        else yield return Until(()=>In(sequence.GetSceneAt(5)),"Hornet completion reaches spa",100);
                        yield return Both("spa."+attempt);
                        yield return Until(()=>manager.IsSceneHostDetermined&&Field<Dictionary<ushort,Hkmp.Game.Client.ClientPlayerData>>(engine.ClientManager,"_playerData").Values.Any(p=>p.IsInLocalScene&&p.PlayerObject!=null&&p.PlayerObject.activeInHierarchy),"spa has teammate and room authority");
                        yield return Both("spa-ready."+attempt);
                    }
                    break;
                }
                // Hold the late client in the current room while its teammate
                // progresses naturally. This is a completion/entry race, not F8.
                if(role!=first){runtime.Down=true;Flag("held."+key);}
                else {yield return Until(()=>Has("held."+key),"late client holds transition");}
                var next=sequence.GetSceneAt(index+1);
                if(role==first){yield return Defeat(scene,next);SafeHero();yield return new WaitForSecondsRealtime(8);Flag("advance."+key);}
                else {yield return Until(()=>Has("advance."+key),"teammate fights next boss "+key,130);runtime.Down=false;}
            }
            Enter("Town");yield return Until(()=>In("Town"),"leave attempt");SafeHero();yield return Both("left."+attempt);
            if(attempt==0){engine.ClientManager.Disconnect();yield return Until(()=>!runtime.Connected,"disconnect between attempts");yield return Both("disconnected");
                ConnectLocal("retry"+role);
                yield return Until(()=>runtime.Connected,"reconnect between attempts");yield return Both("reconnected");}
        }
        if(Has(role+".failed"))yield break;
        if(AudioListener.volume!=0)throw new Exception("Audio not muted");
        Mark("FINAL PASS repeated delayed first pantheon through Hornet; muted audio");yield return Finish();
    }
    private IEnumerator Defeat(string scene,string next) {
        var end=Time.realtimeSinceStartup+100;var sample=0f;
        while(!In(next)&&Time.realtimeSinceStartup<end) {
            if(In(scene)&&manager.IsSceneHost)foreach(var e in Entities.Values.ToArray()) {
                var go=e.Object.Host;var h=go==null?null:go.GetComponent<HealthManager>();
                if(h==null||h.isDead||!go.activeInHierarchy)continue;
                if(e.Type==EntityType.FalseKnight) {
                    var f=go.GetComponents<PlayMakerFSM>().Single(s=>s.FsmName=="FalseyControl");
                    if(Time.realtimeSinceStartup>=sample){sample=Time.realtimeSinceStartup+2;Mark("DEFEAT FK state="+f.ActiveStateName+" hp="+h.hp+" head="+Entities.Values.Single(s=>s.Type==EntityType.FalseKnightHead).Object.Host.GetComponent<HealthManager>().hp+" hero="+HeroController.instance.transform.position+" anim="+go.GetComponent<tk2dSpriteAnimator>().CurrentClip?.name);}
                    if(f.ActiveStateName=="Idle")h.ApplyExtraDamage(h.hp);
                    else if(f.ActiveStateName.StartsWith("Opened",StringComparison.Ordinal)) {
                        var head=Entities.Values.Single(s=>s.Type==EntityType.FalseKnightHead).Object.Host.GetComponent<HealthManager>();
                        head.ApplyExtraDamage(head.hp);
                    }
                }else if(e.Type!=EntityType.FalseKnightHead)h.ApplyExtraDamage(100000);
            }
            yield return new WaitForSecondsRealtime(.5f);
        }
        yield return Until(()=>In(next),"authority native next boss "+scene,1);
    }
}
