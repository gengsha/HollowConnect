using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Hkmp.Game.Client.Entity;
using Hkmp.Game.Client.Entity.Component;
using Hkmp.Networking.Client;
using Hkmp.Networking.Packet;
using Hkmp.Networking.Packet.Data;
using Hkmp.Networking.Packet.Update;
using UnityEngine;
using Object = UnityEngine.Object;

namespace HollowConnect.Smoke;

internal static class ReplicaVisualChecks {
    internal static IEnumerator Run(Action<string> log) {
        void Require(bool ok,string label){if(!ok)throw new Exception(label);log("PASS "+label);}
        var original=Object.FindObjectsOfType<tk2dSpriteAnimator>().SelectMany(a=>a.Library==null?Array.Empty<tk2dSpriteAnimationClip>():a.Library.clips)
            .First(c=>c.frames!=null&&c.frames.Length>=3&&c.frames.All(f=>f.spriteCollection!=null));
        var source=new GameObject("HollowConnect independent visual fixture");source.SetActive(false);
        var child=new GameObject("Visual");child.transform.SetParent(source.transform,false);
        var sprite=child.AddComponent<tk2dSprite>();sprite.SetSprite(original.frames[0].spriteCollection,original.frames[0].spriteId);
        var library=child.AddComponent<tk2dSpriteAnimation>();
        var clip=new tk2dSpriteAnimationClip { name="fixture-loop",fps=60,wrapMode=tk2dSpriteAnimationClip.WrapMode.Loop,frames=original.frames.Take(3).ToArray() };
        library.clips=new[]{clip};var native=child.AddComponent<tk2dSpriteAnimator>();native.Library=library;
        var net=new NetClient(new PacketManager());var id=0xffffffffffffff90UL;
        Entity e=null;
        try {
            e=new Entity(net,id,EntityType.DynamicHazard,source);EntityReferences.Register(e);EntityReferences.Entities[id]=e;e.InitializeClient();e.UpdateIsActive(true);
            var part=EntityReferences.Parts(id).Single(p=>p.Host==child);var replica=part.Client.GetComponent<tk2dSpriteAnimator>();
            var components=(Dictionary<EntityComponentType,EntityComponent>)typeof(Entity).GetField("_components",BindingFlags.Instance|BindingFlags.NonPublic).GetValue(e);
            var combat=components[EntityComponentType.CombatState];
            void Apply(float time,bool animated=true,bool snapshot=false){
                var p=new Packet();p.Write((ushort)1);p.Write(part.Path);p.Write(true);p.Write(true);p.Write((byte)0);p.Write(EntityReferences.Capture(source));
                Vector(Vector3.zero);Vector(Vector3.zero);Vector(Vector3.one);
                p.Write((byte)0);p.Write((byte)0);p.Write((byte)0);p.Write((byte)1);p.Write(original.frames[0].spriteId);
                p.Write(1f);p.Write(1f);p.Write(1f);p.Write(1f);p.Write(animated);
                if(animated){p.Write(clip.name);p.Write(time);p.Write(60f);}p.Write((byte)0);
                combat.Update(new EntityNetworkData {Type=EntityComponentType.CombatState,Packet=new Packet(CombatStatePayload.Encode(p).ToArray())},snapshot);
                void Vector(Vector3 v){p.Write(v.x);p.Write(v.y);p.Write(v.z);}
            }
            var callbacks=0;replica.AnimationCompleted=(_,__)=>callbacks++;replica.AnimationEventTriggered=(_,__,___)=>callbacks++;
            Apply(.1f,true,true);
            Require(replica.enabled&&replica.Playing&&replica.CurrentClip.name==clip.name,"child clip starts on native animator with authoritative phase");
            Require(replica.AnimationCompleted==null&&replica.AnimationEventTriggered==null&&callbacks==0,"replica visual playback cannot execute cloned gameplay callbacks");
            replica.UpdateAnimation(.02f);var before=replica.ClipTimeSeconds;Apply(.11f);
            Require(Math.Abs(replica.ClipTimeSeconds-before)<.001f,"repeated child snapshots do not rewind a running clip");
            replica.UpdateAnimation(.02f);Require(replica.ClipTimeSeconds>before,"child animation advances between network samples");
            Apply(.01f);Require(replica.ClipTimeSeconds<.02f,"same child clip restarts only on authoritative time reset");
            Apply(0,false);Require(!replica.Playing&&replica.Sprite.spriteId==original.frames[0].spriteId,"authoritative static child frame stops local playback");
            Apply(.2f,true,true);Require(replica.Playing&&Math.Abs(replica.ClipTimeSeconds-.2f)<.001f,"late snapshot restores current child animation phase");
            var rootMotion=e.Object.Client.GetComponent<Hkmp.Fsm.PositionInterpolation>();
            rootMotion.SetNewPosition(new Vector3(2,3,0),true);rootMotion.SetNewPosition(new Vector3(3,3,0));
            rootMotion.Flush();Require(e.Object.Client.transform.localPosition.x==3,"takeover flushes newest authoritative motion sample");
            rootMotion.SetNewPosition(Vector3.zero,true);
            rootMotion.SetNewPosition(Vector3.zero);
            yield return new WaitForSeconds(.05f);
            rootMotion.SetNewPosition(new Vector3(2,0,0));
            var until=Time.time+.09f;var between=false;
            while(Time.time<until){yield return null;var x=e.Object.Client.transform.localPosition.x;if(x>.05f&&x<1.95f)between=true;}
            Require(between&&Math.Abs(e.Object.Client.transform.localPosition.x-2)<.01f,"actual Unity frames interpolate sparse movement and then hold at destination");
            var childMotion=part.Client.GetComponent<Hkmp.Fsm.PositionInterpolation>();
            childMotion.SetNewPosition(Vector3.zero,true);childMotion.SetNewPosition(Vector3.zero);
            yield return new WaitForSeconds(.05f);childMotion.SetNewPosition(new Vector3(2,0,0));
            ((CombatStateComponent)combat).PrepareTakeover();
            Require(child.transform.localPosition.x==2,"child takeover restores newest sample instead of delayed rendering position");
        } finally {
            if(e!=null){EntityReferences.Entities.Remove(id);EntityReferences.Forget(e);e.Destroy();}
            Object.Destroy(source);net.UpdateManager.StopUpdates();net.Disconnect();
        }
    }
}
