using System;
using System.Collections;
using System.Linq;
using Hkmp.Game.Client.Entity;
using HollowConnect.Core;
using HutongGames.PlayMaker.Actions;
using UnityEngine;
using Object = UnityEngine.Object;

namespace HollowConnect.Smoke;

public sealed partial class ProgressProbe {
    private IEnumerator ShadeSoulChecks() {
        var source=ResourceCatalog.Sources.Single(s=>s.InventoryFlag=="hasWhiteKey");
        if(host) yield return KeyShop(true,"hasWhiteKey");
        yield return Until(()=>LocalResourceClaims.Contains(source.Id) && PlayerData.instance.GetBool("hasWhiteKey"),"native Elegant Key receipt",60);
        yield return ResourceBarrier("shade-key",60);
        var first=Environment.GetEnvironmentVariable("HOLLOWCONNECT_BLACKWAVE_FIRST") ?? "host";
        if(role!=first) yield return Until(()=>Has(first+".shade-door"),"first entrant opens Elegant Key door",90);
        yield return OpenUniqueDoor("Ruins1_31","usedWhiteKey","hasWhiteKey");
        Flag(role+".shade-door");
        yield return ResourceBarrier("shade-doors",90);
        if(role!=first)yield return Until(()=>Has(first+".shade-arena"),"first entrant owns Shade Soul arena",30);
        var entrances=Object.FindObjectsOfType<TransitionPoint>(true).Where(g=>g.gameObject.scene.name=="Ruins1_31" && g.targetScene=="Ruins1_31b").ToArray();
        foreach(var gate in entrances)Mark("SHADE GATE CANDIDATE "+gate.name+" -> "+gate.entryPoint+" pos="+gate.transform.position);
        var entrance=entrances.Single(g=>g.entryPoint=="right2");
        Mark("SHADE NATIVE GATE "+entrance.name+" -> "+entrance.targetScene+"/"+entrance.entryPoint+" unlocked="+PlayerData.instance.GetBool("usedWhiteKey"));
        TransitionPoint.lastEntered=entrance.entryPoint;
        GameManager.instance.BeginSceneTransition(new GameManager.SceneLoadInfo{SceneName=entrance.targetScene,EntryGateName=entrance.entryPoint,Visualization=GameManager.SceneLoadVisualizations.Default,WaitForSceneTransitionCameraFade=true});
        yield return Until(()=>AliveScene("Ruins1_31b") && engine.ClientManager.SceneSnapshotReady,"native Shade Soul room entrance",35);
        Freeze();Flag(role+".shade-arena");
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
}
