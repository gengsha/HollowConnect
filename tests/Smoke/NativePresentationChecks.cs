using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Hkmp;
using Hkmp.Game.Client.Entity;
using Hkmp.Game.Client.Entity.Component;
using Hkmp.Networking.Packet.Data;
using MonoMod.RuntimeDetour;
using UnityEngine;

namespace HollowConnect.Smoke;

// Observe actual authoritative wire snapshots at the replica boundary. No
// native boss states or progression actions are replaced by this observer.
internal sealed class PresentationWitness : IDisposable {
    private readonly Hook hook;
    private readonly Func<Entity,bool> select;
    private readonly Action<string> log;
    private readonly Dictionary<ulong,PresentationOrder> orders=new();
    internal readonly HashSet<string> Clips=new(),Leaves=new();
    internal int Snapshots,RootStops,ActiveLeaves,Dropped,Recovered;
    internal string Failure;
    private ulong droppedId;
    private uint droppedPlayback;
    private string droppedClip;
    internal PresentationWitness(Func<Entity,bool> select,Action<string> log,bool drop=false) {
        this.select=select;this.log=log;
        hook=new Hook(typeof(PresentationComponent).GetMethod("Update"),
            new Action<Action<PresentationComponent,EntityNetworkData,bool>,PresentationComponent,EntityNetworkData,bool>((orig,self,data,snapshot)=>{
                var id=(ulong)typeof(PresentationComponent).GetField("id",BindingFlags.Instance|BindingFlags.NonPublic).GetValue(self);
                if(!EntityReferences.Entities.TryGetValue(id,out var e)||!select(e)){orig(self,data,snapshot);return;}
                var state=PresentationState.Decode(data.Packet);
                if(!orders.TryGetValue(id,out var order))orders[id]=order=new PresentationOrder();
                if(!order.Accept(CoopContext.Epoch,state.Revision)){orig(self,data,snapshot);return;}
                var root=state.Nodes.FirstOrDefault(n=>n.Path.Length==0)?.Sprites.FirstOrDefault();
                if(drop&&Dropped==0&&root is {Playing:true}&&root.Clip.IndexOf("roll",StringComparison.OrdinalIgnoreCase)>=0) {
                    Dropped++;droppedId=id;droppedClip=root.Clip;droppedPlayback=root.Playback;
                    log("INJECT omitted one native root presentation transition "+e.Type+" "+root.Clip);return;
                }
                orig(self,data,snapshot);
                try {
                    foreach(var n in state.Nodes) {
                        var target=EntityReferences.Resolve("e:"+id+(n.Path.Length==0?"":"/"+n.Path),true);
                        if(target==null||n.Gone)continue;
                        if(n.OwnPose) {
                            if(target.activeSelf!=n.Active)throw new Exception("activation mismatch "+n.Path);
                            Leaves.Add(target.name);if(n.Active)ActiveLeaves++;
                        }
                        var sprites=target.GetComponents<tk2dBaseSprite>();
                        for(var i=0;i<n.Sprites.Count&&i<sprites.Length;i++) {
                            var v=n.Sprites[i];var a=sprites[i].GetComponent<tk2dSpriteAnimator>();
                            if(a==null||v.Clip.Length==0||a.GetClipByName(v.Clip)==null)continue;
                            if(a.CurrentClip?.name!=v.Clip||a.Playing!=v.Playing)
                                throw new Exception("animation mismatch "+e.Type+"/"+n.Path+" expected="+v.Clip+":"+v.Playing+" actual="+a.CurrentClip?.name+":"+a.Playing);
                            if(!v.Playing&&v.Frame>=0&&sprites[i].spriteId!=v.Frame)throw new Exception("stopped frame mismatch "+n.Path);
                            if(a.AnimationCompleted!=null||a.AnimationEventTriggered!=null)throw new Exception("replica gameplay callback retained "+n.Path);
                            if(n.Path.Length==0){if(Clips.Add(v.Clip))log("NATIVE WIRE CLIP "+e.Type+" "+v.Clip);if(!v.Playing)RootStops++;}
                        }
                    }
                    Snapshots++;
                    if(Dropped>0&&Recovered==0&&id==droppedId&&root!=null&&root.Clip==droppedClip&&root.Playback==droppedPlayback) {
                        Recovered++;log("PASS omitted native animation repaired by complete state for same playback instance");
                    }
                }catch(Exception ex){Failure??=ex.Message;}
            }));
    }
    public void Dispose()=>hook.Dispose();
}

public sealed partial class RescueProbe {
    private IEnumerator NativePresentationChecks() {
        using var noContact=IgnoreExternalCombatDamage();
        var selected=Environment.GetEnvironmentVariable("HOLLOWCONNECT_PRESENTATION_CHECK");
        var beast=selected=="beast";var scene=beast?"GG_God_Tamer":"GG_Painter";
        var type=beast?EntityType.Beast:EntityType.Sheo;
        using var witness=new PresentationWitness(e=>e.Type==type,Log,beast&&!host);
        if(!host)yield return Until(()=>Has("presentation.first"),"authority enters native presentation room");
        BossSequenceController.Reset();PlayerData.instance.SetInt("bossStatueTargetLevel",0);
        BossSceneController.SetupEvent=c=>c.BossLevel=0;
        PlayerData.instance.SetString("dreamReturnScene","Town");PlayerData.instance.SetString("bossReturnEntryGate","left1");
        yield return RuleEnter(scene,"door_dreamEnter");
        var manager=Field<EntityManager>(engine.ClientManager,"_entityManager");
        yield return Until(()=>manager.IsSceneHostDetermined&&manager.IsSceneHost==host&&EntityReferences.Entities.Values.Any(e=>e.Type==type),"native entity schema and authority "+type);
        var entity=EntityReferences.Entities.Values.Single(e=>e.Type==type);
        Check(entity.Object.Host.GetComponent<tk2dSpriteAnimator>()!=null&&entity.Object.Client.GetComponent<tk2dSpriteAnimator>()!=null,"native root animator exists "+type);
        Log("PREFLIGHT "+type+" hero="+HeroController.instance.transform.position+" native="+entity.Object.Host.transform.position+" fsms="+
            string.Join(";",entity.Object.Host.GetComponents<PlayMakerFSM>().Select(f=>f.FsmName+":"+f.ActiveStateName)));
        if(host)Flag("presentation.first");
        Flag(role+".presentation-ready");yield return Until(()=>Has((host?"guest":"host")+".presentation-ready"),"both native presentation observers ready");
        var end=Time.realtimeSinceStartup+45;
        while(Time.realtimeSinceStartup<end) {
            if(witness.Failure!=null)throw new Exception(witness.Failure);
            var hero=HeroController.instance;hero.takeNoDamage=true;
            // Keep the living target on the arena floor in the native AI range.
            var pos=hero.transform.position;var target=host?entity.Object.Host:entity.Object.Client;
            if(target!=null&&Math.Abs(pos.x-target.transform.position.x)>18)hero.transform.position=new Vector3(target.transform.position.x-8,pos.y,pos.z);
            yield return new WaitForSecondsRealtime(.2f);
        }
        if(!host) {
            Check(witness.Snapshots>=10&&witness.Clips.Count>=4,"native root clips and complete recovery stream observed: "+witness.Snapshots+" "+string.Join(",",witness.Clips));
            Check(witness.Leaves.Count>0&&witness.ActiveLeaves>0,"native decorative effects activated and synchronized: "+string.Join(",",witness.Leaves));
            if(beast)Check(witness.Dropped==1&&witness.Recovered==1,"native roll repairs one deliberately omitted transition for same instance");
        }
        Flag(role+".presentation-complete");yield return Until(()=>Has((host?"guest":"host")+".presentation-complete"),"both native observations complete");
        // Stop this observed encounter before shutdown transfers authority.
        foreach(var e in EntityReferences.Entities.Values.ToArray())foreach(var f in e.Object.Host==null?Array.Empty<PlayMakerFSM>():e.Object.Host.GetComponentsInChildren<PlayMakerFSM>(true))f.Fsm.ManualUpdate=true;
    }
}
