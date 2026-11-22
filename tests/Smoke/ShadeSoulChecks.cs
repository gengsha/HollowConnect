using System;
using System.Collections;
using System.Linq;
using System.Reflection;
using Hkmp.Game.Client;
using Hkmp.Game.Client.Entity;
using Hkmp.Networking.Packet.Data;
using HollowConnect.Core;
using HutongGames.PlayMaker.Actions;
using MonoMod.RuntimeDetour;
using UnityEngine;
using Object = UnityEngine.Object;

namespace HollowConnect.Smoke;

public sealed partial class ProgressProbe {
    private IEnumerator ShadeSoulChecks() {
        if (Environment.GetEnvironmentVariable("HOLLOWCONNECT_SHADE_KEY_READY") == "1") {
            // Isolate arena entry from shop UI timing in the spawn regression.
            // The real key door below still requires and consumes the item.
            PlayerData.instance.SetBool("hasWhiteKey", true);
        } else {
            var source=ResourceCatalog.Sources.Single(s=>s.InventoryFlag=="hasWhiteKey");
            if(host) yield return KeyShop(true,"hasWhiteKey");
            yield return Until(()=>LocalResourceClaims.Contains(source.Id) && PlayerData.instance.GetBool("hasWhiteKey"),"native Elegant Key receipt",60);
        }
        yield return ResourceBarrier("shade-key",60);
        var first=Environment.GetEnvironmentVariable("HOLLOWCONNECT_BLACKWAVE_FIRST") ?? "host";
        var entryOrder=Environment.GetEnvironmentVariable("HOLLOWCONNECT_SHADE_ENTRY_ORDER");
        var early=entryOrder=="before-snapshot" && role==first;
        Action commitSnapshot=null;
        using var snapshotDelay=entryOrder!=null?new Hook(typeof(ClientManager).GetMethod("OnPlayerAlreadyInScene",BindingFlags.Instance|BindingFlags.NonPublic),
            (Action<Action<ClientManager,ClientPlayerAlreadyInScene>,ClientManager,ClientPlayerAlreadyInScene>)((orig, client, data)=>{
                if(data.SceneName!="Ruins1_31b") { orig(client,data); return; }
                void Inspect() {
                    var boss=EntityReferences.Entities.Values.SingleOrDefault(e=>e.Type==EntityType.SoulWarrior);
                    var updates=data.ReliableEntityUpdateList.Where(u=>u.Id==boss?.Id).ToArray();
                    Mark("SHADE SNAPSHOT id="+boss?.Id+" authority="+data.SceneHost+" data="+string.Join(";",updates.Select(u=>
                        "fsm="+string.Join(",",u.HostFsmData.Select(f=>f.Key+":"+(f.Value.Types.Contains(EntityHostFsmData.Type.State)?f.Value.CurrentState.ToString():"none")))+
                        " components="+string.Join(",",u.GenericData.Select(d=>d.Type)))));
                    orig(client,data);
                }
                if (early) commitSnapshot ??= Inspect;
                else Inspect();
            })):null;
        using var shadeStates=entryOrder!=null?new Hook(typeof(Entity).GetMethod("OnHostStateChanged",BindingFlags.Instance|BindingFlags.NonPublic),
            (Action<Action<Entity,HutongGames.PlayMaker.FsmState>,Entity,HutongGames.PlayMaker.FsmState>)((orig, entity, state)=>{
                if (entity.Type==EntityType.SoulWarrior && entity.SourceScene=="Ruins1_31b" && state.Name is "Init" or "Sleep" or "Wake")
                    Mark("SHADE NATIVE state="+state.Name+" authority="+entity.IsNativeAuthority+" id="+entity.Id);
                orig(entity,state);
            })):null;
        if(role!=first) yield return Until(()=>Has(first+".shade-door"),"first entrant opens Elegant Key door",90);
        yield return OpenUniqueDoor("Ruins1_31","usedWhiteKey","hasWhiteKey");
        Flag(role+".shade-door");
        yield return ResourceBarrier("shade-doors",90);
        if (entryOrder != null && role != first) yield return Until(()=>Has(first+(entryOrder=="after-death"?".shade-dead":".shade-locked")),
            "second player arrives after the first triggers the arena",45);
        if(role!=first)yield return Until(()=>Has(first+".shade-arena"),"first entrant owns Shade Soul arena",30);
        if (GameManager.instance.sceneName != "Ruins1_31b") {
            var entrances=Object.FindObjectsOfType<TransitionPoint>(true).Where(g=>g.gameObject.scene.name=="Ruins1_31" && g.targetScene=="Ruins1_31b").ToArray();
            var entrance=entrances.Single(g=>g.entryPoint=="right2");
            Mark("SHADE NATIVE GATE "+entrance.name+" -> "+entrance.targetScene+"/"+entrance.entryPoint+" unlocked="+PlayerData.instance.GetBool("usedWhiteKey"));
            TransitionPoint.lastEntered=entrance.entryPoint;
            GameManager.instance.BeginSceneTransition(new GameManager.SceneLoadInfo{SceneName=entrance.targetScene,EntryGateName=entrance.entryPoint,Visualization=GameManager.SceneLoadVisualizations.Default,WaitForSceneTransitionCameraFade=true});
        } else Mark("SHADE native door movement already took the hero into the arena");
        if(early) {
            yield return Until(()=>AliveScene("Ruins1_31b") && commitSnapshot!=null,"native room playable with authority snapshot delayed",35);
            Freeze();
            var controller=Object.FindObjectsOfType<PlayMakerFSM>(true).Single(f=>f.gameObject.scene.name=="Ruins1_31b" && f.FsmName=="Battle Control");
            var bounds=controller.GetComponent<Collider2D>().bounds;
            HeroController.instance.transform.position=new Vector3(bounds.min.x+1.5f,bounds.min.y+2.2f,0);
            Physics2D.SyncTransforms();
            yield return Until(()=>controller.ActiveStateName=="Battling" && !engine.ClientManager.SceneSnapshotReady,"native trigger precedes room authority",10);
            GetComponent<RouteFrameRecorder>()?.Phase("shade-snapshot-wait");
            // Retreat within the room while the gates are shut. The first
            // player must not wake the boss just by standing near it at commit.
            HeroController.instance.transform.position=new Vector3(bounds.min.x-1.5f,bounds.min.y+2.2f,0);
            Physics2D.SyncTransforms();
            yield return new WaitForSecondsRealtime(9);
            commitSnapshot();
            snapshotDelay.Dispose();
            Mark("SHADE delayed the real first-room authority snapshot for nine seconds after native trigger");
        }
        yield return Until(()=>AliveScene("Ruins1_31b") && engine.ClientManager.SceneSnapshotReady,"native Shade Soul room entrance",35);
        Freeze();Flag(role+".shade-arena");
        if (entryOrder != null) { yield return ShadeSoulLateChecks(first,entryOrder,early); yield break; }
        yield return ResourceBarrier("shade-arena",30);
        var bosses=EntityReferences.Entities.Values.Where(e=>e.Type==EntityType.SoulWarrior).ToArray();
        ResourceCheck(bosses.Length==1,"one Soul Warrior behind the Elegant Key door");
        var entity=bosses.Single();
        var controllers=Object.FindObjectsOfType<PlayMakerFSM>(true).Where(f=>f.gameObject.scene.name=="Ruins1_31b" && f.FsmName=="Battle Control").ToArray();
        foreach(var f in controllers){Dump(f);Mark("SHADE CONTROLLER "+f.name+"/"+f.FsmName+"="+f.ActiveStateName+" collider="+f.GetComponent<Collider2D>()?.bounds);}
        foreach(var f in entity.Object.Host.GetComponents<PlayMakerFSM>()){Dump(f);Mark("SHADE BOSS "+f.FsmName+"="+f.ActiveStateName+" authority="+entity.IsNativeAuthority+" active="+entity.Object.Host.activeInHierarchy);}
        var snapshots=Field<System.Collections.Generic.List<FsmSnapshot>>(entity,"_fsmSnapshots");
        var bossIndex=entity.GetClientFsms().FindIndex(f=>f.FsmName=="Mage Knight");
        string State()=>snapshots[bossIndex].CurrentState;
        var trigger=Environment.GetEnvironmentVariable("HOLLOWCONNECT_BLACKWAVE_TRIGGER") ?? "guest";
        if(role==trigger){
            var area=controllers.Single(f=>f.GetComponent<Collider2D>() is {isTrigger:true}).GetComponent<Collider2D>();
            HeroController.instance.transform.position=new Vector3(area.bounds.center.x,area.bounds.min.y+2.2f,0);
            Physics2D.SyncTransforms();
        }
        yield return new WaitForSecondsRealtime(8);
        foreach(var f in controllers)Mark("SHADE AFTER "+f.name+"="+f.ActiveStateName);
        foreach(var f in entity.Object.Host.GetComponents<PlayMakerFSM>())Mark("SHADE AFTER BOSS "+f.FsmName+"="+f.ActiveStateName);
        if(Environment.GetEnvironmentVariable("HOLLOWCONNECT_SHADE_DIAGNOSTIC")=="1")yield break;
        var visible=entity.IsNativeAuthority?entity.Object.Host:entity.Object.Client;
        yield return Until(()=>visible.activeInHierarchy && State()!="Sleep" && !string.IsNullOrEmpty(State()) && visible.GetComponentsInChildren<Renderer>().Any(r=>r.enabled && !r.forceRenderingOff),"visible Soul Warrior wakes on both endpoints",20);
        if(role==first)ResourceCheck(controllers.Single().ActiveStateName=="Battling","scene authority runs the native battle when teammate triggers it");
        // The authority has now verified the peer wake while outside. Enter
        // its own native trigger before measuring normal sustained combat.
        var combatArea=controllers.Single().GetComponent<Collider2D>().bounds;
        HeroController.instance.transform.position=new Vector3(combatArea.center.x+(host?-3:3),combatArea.min.y+2.2f,0);
        HeroController.instance.GetComponent<Rigidbody2D>().velocity=Vector2.zero;Physics2D.SyncTransforms();
        yield return new WaitForSecondsRealtime(1);
        GetComponent<RouteFrameRecorder>()?.Phase("combat");
        var position=visible.transform.position;var travel=0f;var frames=0;var states=new System.Collections.Generic.HashSet<string>();var end=Time.realtimeSinceStartup+30;
        while(Time.realtimeSinceStartup<end){travel=Math.Max(travel,Vector3.Distance(position,visible.transform.position));states.Add(State());frames++;yield return null;}
        Mark("SHADE MOTION travel="+travel+" frames="+frames+" states="+string.Join(",",states)+" hp="+visible.GetComponent<HealthManager>().hp);
        ResourceCheck(travel>2 && frames>100 && states.Count>3,"Soul Warrior moves throughout thirty-second native combat");
        ResourceCheck(EntityReferences.Entities.Values.Any(e=>e.Type==EntityType.Folly && (e.IsNativeAuthority?e.Object.Host:e.Object.Client).activeInHierarchy),"native random-child Folly summon is visible on this endpoint");
        Mark("PASS SHADE SOUL Elegant Key -> Ruins1_31b -> Soul Warrior travel="+travel+" frames="+frames+" states="+string.Join(",",states));
        GetComponent<RouteFrameRecorder>()?.Finish();
    }

    private void DumpShadeRewardObjects(string label) {
        Mark("SHADE REWARD DUMP " + label);
        foreach (var fsm in Object.FindObjectsOfType<PlayMakerFSM>(true).Where(f => f.gameObject.scene.name == "Ruins1_31b")) {
            Mark("SHADE REWARD FSM " + fsm.name + "/" + fsm.FsmName + " state=" + fsm.ActiveStateName + " active=" + fsm.gameObject.activeInHierarchy + " pos=" + fsm.transform.position);
            if (fsm.name == "Ruins Shaman" || fsm.FsmName.IndexOf("Shaman", StringComparison.OrdinalIgnoreCase) >= 0) Dump(fsm);
        }
        Mark("SHADE REWARD PLAYER fireballLevel=" + PlayerData.instance.GetInt("fireballLevel") + " pos=" + HeroController.instance.transform.position + " input=" + HeroController.instance.acceptingInput);
        foreach (var health in Object.FindObjectsOfType<HealthManager>(true).Where(h => h.gameObject.scene.name == "Ruins1_31b"))
            Mark("SHADE REWARD HEALTH " + health.gameObject.name + " hp=" + health.hp + " dead=" + health.isDead + " active=" + health.gameObject.activeInHierarchy + " pos=" + health.transform.position);
        foreach (var point in Object.FindObjectsOfType<TransitionPoint>(true).Where(p => p.gameObject.scene.name == "Ruins1_31b"))
            Mark("SHADE REWARD EXIT " + point.name + " -> " + point.targetScene + "/" + point.entryPoint + " active=" + point.gameObject.activeInHierarchy + " pos=" + point.transform.position);
        foreach (var go in Object.FindObjectsOfType<GameObject>(true).Where(g => g.scene.name == "Ruins1_31b" &&
            (g.name.IndexOf("spell", StringComparison.OrdinalIgnoreCase) >= 0 ||
             g.name.IndexOf("fireball", StringComparison.OrdinalIgnoreCase) >= 0 ||
             g.name.IndexOf("vengeful", StringComparison.OrdinalIgnoreCase) >= 0 ||
             g.name.IndexOf("item", StringComparison.OrdinalIgnoreCase) >= 0 ||
             g.name.IndexOf("reward", StringComparison.OrdinalIgnoreCase) >= 0)))
            Mark("SHADE REWARD OBJECT " + go.name + " active=" + go.activeInHierarchy + " pos=" + go.transform.position);
    }

    private IEnumerator AcquireShadeSoul() {
        var pd = PlayerData.instance;
        var shaman = Object.FindObjectsOfType<PlayMakerFSM>(true).Single(f =>
            f.gameObject.scene.name == "Ruins1_31b" && f.name == "Ruins Shaman" && f.FsmName == "Ruins Shaman");
        var range = shaman.GetComponentsInChildren<Collider2D>(true)
            .Where(c => c.enabled && c.isTrigger)
            .OrderBy(c => Vector2.Distance(c.bounds.center, shaman.transform.position))
            .FirstOrDefault();
        Mark("SHADE ACQUIRE setup shaman=" + shaman.transform.position + " range=" + range?.bounds +
            " fireballLevel=" + pd.GetInt("fireballLevel"));
        KeyStandAt(range, shaman.transform.position);
        yield return new WaitForFixedUpdate();
        yield return Until(() => shaman.ActiveStateName == "In Range" && HeroController.instance.CanTalk(),
            "native Shade Soul shaman interaction range", 12);
        shaman.SetState("Take Control");
        yield return Until(() => shaman.ActiveStateName == "Send Text" && Dialogue.ActiveStateName == "Ready for Input" &&
            Dialogue.FsmVariables.FindFsmGameObject("Requester")?.Value == shaman.gameObject,
            "native Shade Soul pickup prompt", 15);
        ChooseYes();
        yield return Until(() => pd.GetInt("fireballLevel") >= 2,
            "native Shade Soul pickup completes", 45);
        Mark("PASS native Shade Soul pickup fireballLevel=" + pd.GetInt("fireballLevel") +
            " shaman=" + shaman.ActiveStateName + " hero=" + HeroController.instance.transform.position +
            " input=" + HeroController.instance.acceptingInput);
    }

    private IEnumerator ShadeSoulLateChecks(string first, string entryOrder, bool alreadyTriggered) {
        const string scene = "Ruins1_31b";
        var battle = Object.FindObjectsOfType<PlayMakerFSM>(true).Single(f=>f.gameObject.scene.name==scene && f.FsmName=="Battle Control");
        var gates = Object.FindObjectsOfType<Collider2D>(true).Where(c=>c.gameObject.scene.name==scene && !c.isTrigger &&
            c.GetComponentsInParent<PlayMakerFSM>(true).Any(f=>f.FsmName=="BG Control")).ToArray();
        ResourceCheck(gates.Length>0, "located native battle-gate colliders");
        bool Closed()=>gates.Any(c=>c!=null && c.enabled && c.gameObject.activeInHierarchy);
        var entity=EntityReferences.Entities.Values.SingleOrDefault(e=>e.Type==EntityType.SoulWarrior);
        var hero=HeroController.instance;
        var area=battle.GetComponent<Collider2D>().bounds;
        var recorder=GetComponent<RouteFrameRecorder>();
        recorder?.Phase("shade-entry");
        void EnterArena() {
            hero.transform.position=new Vector3(area.center.x,area.min.y+2.2f,0);
            Physics2D.SyncTransforms();
        }
        string Layout()=>"state="+battle.ActiveStateName+" enemies="+battle.FsmVariables.FindFsmInt("Battle Enemies").Value+
            " closed="+Closed()+" hp="+(entity?.Object.Host!=null?entity.Object.Host.GetComponent<HealthManager>().hp:-1)+
            " native="+entity?.IsNativeAuthority+" killedMageKnight="+PlayerData.instance.GetBool("killedMageKnight");
        Mark("SHADE LATE arrival "+Layout()+" hero="+hero.transform.position+" trigger="+area+" inside="+battle.GetComponent<Collider2D>().OverlapPoint(hero.transform.position));
        if (!alreadyTriggered) EnterArena();
        if(role==first) {
            ResourceCheck(entity?.IsNativeAuthority==true,"first entrant owns live native Soul Warrior");
            yield return Until(()=>battle.ActiveStateName=="Battling" && Closed(),"authority native battle locks doors",15);
            if (!alreadyTriggered) Flag(role+".shade-locked");
        }
        if (entryOrder!="after-death") {
            var visible=entity?.IsNativeAuthority==true?entity.Object.Host:entity?.Object.Client;
            string WakeState()=>entity==null?"missing":entity.IsNativeAuthority && visible!=null
                ? visible.GetComponents<PlayMakerFSM>().Single(f=>f.FsmName=="Mage Knight").ActiveStateName
                : Field<System.Collections.Generic.List<FsmSnapshot>>(entity,"_fsmSnapshots")
                    [entity.GetClientFsms().FindIndex(f=>f.FsmName=="Mage Knight")].CurrentState;
            bool AwakeVisible()=>Closed() && visible!=null && visible.activeInHierarchy && visible.GetComponent<HealthManager>().hp>0 &&
                visible.GetComponentsInChildren<Renderer>().Any(r=>r.enabled && !r.forceRenderingOff) &&
                WakeState() is not (null or "Sleep" or "Init" or "") &&
                visible.GetComponent<tk2dSpriteAnimator>()?.CurrentClip!=null;
            Mark("SHADE LATE triggered "+Layout()+" boss="+WakeState());
            var deadline=Time.realtimeSinceStartup+6;
            var observed="";
            while (!AwakeVisible() && Time.realtimeSinceStartup<deadline) {
                var now=Layout()+" boss="+WakeState();
                if (now!=observed) { Mark("SHADE LATE waiting "+now); observed=now; }
                yield return null;
            }
            Mark("SHADE LATE visibility "+Layout()+" hero="+hero.transform.position+" boss="+WakeState()+" active="+visible?.activeInHierarchy+
                " clip="+visible?.GetComponent<tk2dSpriteAnimator>()?.CurrentClip?.name+" pos="+visible?.transform.position+" awake="+AwakeVisible());
            Flag(role+(AwakeVisible()?".shade-awake":".shade-asleep"));
            if (alreadyTriggered) Flag(role+".shade-locked");
            yield return ResourceBarrier("shade-live",20);
            recorder?.Phase("shade-combat");
            yield return new WaitForSecondsRealtime(3);
            if (!AwakeVisible()) Flag(role+".shade-asleep");
            Mark("SHADE LATE combat "+Layout()+" boss="+WakeState()+" awake="+AwakeVisible());
        }
        var killer=first;
        if (entryOrder=="after-handoff") {
            recorder?.Phase("shade-handoff");
            killer=first=="guest"?"host":"guest";
            if (role==first) {
                engine.ClientManager.Disconnect();
                yield return Until(()=>!runtime.Connected,"first guest disconnected from locked arena",15);
                yield return Until(()=>Has(killer+".shade-open"),"remaining player finished and opened gates",35);
                ResourceCheck(!Has(role+".shade-asleep"),"room has a visible living, awake Soul Warrior before handoff");
                yield break;
            }
            yield return Until(()=>entity?.IsNativeAuthority==true,"remaining player takes authority in locked arena",20);
            Mark("SHADE HANDOFF "+Layout());
            yield return Until(()=>entity.Object.Host!=null && entity.Object.Host.activeInHierarchy && entity.Object.Host.GetComponent<HealthManager>().hp>0 &&
                entity.Object.Host.GetComponentsInChildren<Renderer>().Any(r=>r.enabled && !r.forceRenderingOff) &&
                battle.ActiveStateName=="Battling" && Closed(),"remaining player has a visible Boss and native locked gates after takeover",15);
        }
        recorder?.Phase("shade-finish");
        if(role==killer) {
            var health=entity.Object.Host.GetComponent<HealthManager>();
            health.ApplyExtraDamage(health.hp);
            yield return Until(()=>health==null || health.isDead,"authority native Soul Warrior death",15);
            Mark("SHADE LATE death "+Layout());
            Flag(role+".shade-dead");
        }
        yield return new WaitForSecondsRealtime(5);
        Mark("SHADE LATE after trigger "+Layout());
        if (Environment.GetEnvironmentVariable("HOLLOWCONNECT_SHADE_DIAGNOSTIC") == "1") {
            DumpShadeRewardObjects("after-death");
            yield break;
        }
        ResourceCheck(!Closed(),"native exits open once confirmed Soul Warrior is dead");
        Flag(role+".shade-open");
        if (entryOrder!="after-handoff") yield return ResourceBarrier("shade-late-open",25);
        if (Environment.GetEnvironmentVariable("HOLLOWCONNECT_SHADE_ACQUIRE") == "1") {
            yield return AcquireShadeSoul();
            Flag(role+".shade-acquired");
            yield return ResourceBarrier("shade-acquired",45);
        }
        if (entryOrder!="after-death")
            ResourceCheck(!Has(role+".shade-asleep"),"locked room has a visible living, awake Soul Warrior");
    }
}
