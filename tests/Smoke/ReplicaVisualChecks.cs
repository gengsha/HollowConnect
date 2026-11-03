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
using UnityEngine;
using Object = UnityEngine.Object;

namespace HollowConnect.Smoke;

internal static class ReplicaVisualChecks {
    internal static IEnumerator Run(Action<string> log) {
        void Require(bool ok,string label){if(!ok)throw new Exception(label);log("PASS "+label);}
        var original=Object.FindObjectsOfType<tk2dSpriteAnimator>().SelectMany(a=>a.Library==null?Array.Empty<tk2dSpriteAnimationClip>():a.Library.clips)
            .First(c=>c.frames!=null&&c.frames.Length>=3&&c.frames.All(f=>f.spriteCollection!=null));
        var source=new GameObject("HollowConnect independent visual fixture");source.SetActive(false);
        tk2dSpriteAnimator AddAnimator(GameObject go) {
            var sprite=go.AddComponent<tk2dSprite>();sprite.SetSprite(original.frames[0].spriteCollection,original.frames[0].spriteId);
            var library=go.AddComponent<tk2dSpriteAnimation>();
            library.clips=new[]{new tk2dSpriteAnimationClip { name="fixture-loop",fps=10,wrapMode=tk2dSpriteAnimationClip.WrapMode.Loop,
                frames=Enumerable.Range(0,8).Select(i=>new tk2dSpriteAnimationFrame {spriteCollection=original.frames[i%3].spriteCollection,
                    spriteId=original.frames[i%3].spriteId,triggerEvent=true,eventInfo="fixture"}).ToArray() }};
            var animator=go.AddComponent<tk2dSpriteAnimator>();animator.Library=library;animator.playAutomatically=false;return animator;
        }
        var nativeRoot=AddAnimator(source);
        var child=new GameObject("Visual");child.transform.SetParent(source.transform,false);var native=AddAnimator(child);
        var net=new NetClient(new PacketManager());var id=0xffffffffffffff90UL;
        Entity e=null;
        try {
            e=new Entity(net,id,EntityType.DynamicHazard,source);EntityReferences.Register(e);EntityReferences.Entities[id]=e;e.InitializeClient();e.UpdateIsActive(true);
            var part=EntityReferences.Parts(id).Single(p=>p.Host==child);var replica=part.Client.GetComponent<tk2dSpriteAnimator>();
            var rootReplica=e.Object.Client.GetComponent<tk2dSpriteAnimator>();
            var components=(Dictionary<EntityComponentType,EntityComponent>)typeof(Entity).GetField("_components",BindingFlags.Instance|BindingFlags.NonPublic).GetValue(e);
            var presentation=(PresentationComponent)components[EntityComponentType.Presentation];
            uint revision=0,playback=1;
            PresentationState State(float time,bool playing=true) {
                var state=new PresentationState{Revision=++revision};
                foreach(var path in new[]{"",part.Path}) {
                    var node=new PresentationState.Node{Path=path,Active=true,OwnPose=path.Length!=0,Parent="e:"+id,Position=new(2,0,0)};
                    node.Renderers.Add(new PresentationState.Renderer{Enabled=true});
                    node.Sprites.Add(new PresentationState.Sprite{Clip="fixture-loop",Playback=playback,Wrap=(byte)tk2dSpriteAnimationClip.WrapMode.Loop,
                        Frame=original.frames[1].spriteId,Color=new(.2f,.4f,.6f,1),Enabled=true,Playing=playing,Time=time,Fps=10});
                    state.Nodes.Add(node);
                }
                return state;
            }
            void Apply(PresentationState state,bool snapshot=false)=>presentation.Update(new EntityNetworkData{Type=EntityComponentType.Presentation,Packet=state.Encode()},snapshot);
            var callbacks=0;replica.AnimationCompleted=(_,__)=>callbacks++;replica.AnimationEventTriggered=(_,__,___)=>callbacks++;
            Apply(State(.1f),true);
            Require(replica.enabled&&replica.Playing&&rootReplica.Playing&&replica.CurrentClip.name=="fixture-loop","root and decorative child play authoritative clips");
            Require(replica.AnimationCompleted==null&&replica.AnimationEventTriggered==null&&callbacks==0,"visual seeking suppresses gameplay frame events");
            replica.UpdateAnimation(.02f);var before=replica.ClipTimeSeconds;Apply(State(.11f));
            Require(Math.Abs(replica.ClipTimeSeconds-before)<.001f,"recovery snapshots do not rewind an aligned running clip");
            var stale=State(.5f);playback++;Apply(State(.01f));Apply(stale);
            Require(replica.ClipTimeSeconds<.02f,"new instance of same clip restarts and late retry cannot overwrite it");
            Apply(State(0,false));Require(!replica.Playing&&replica.Sprite.spriteId==original.frames[1].spriteId&&
                !rootReplica.Playing&&rootReplica.Sprite.spriteId==original.frames[1].spriteId,"stopped root and child retain final sprite frames");
            // Simulate an omitted transition, followed by a complete heartbeat.
            var omitted=State(.2f);replica.Stop();rootReplica.Stop();part.Client.SetActive(false);
            Apply(State(.25f));
            Require(part.Client.activeSelf&&replica.Playing&&rootReplica.Playing&&Math.Abs(replica.ClipTimeSeconds-.25f)<.001f,"complete recovery repairs missed clip and child activation without a delta");
            replica.Play("fixture-loop");Require(Math.Abs(replica.ClipTimeSeconds-.25f)<.001f,"replica native Play cannot restart synchronized presentation");
            Apply(omitted);Require(Math.Abs(replica.ClipTimeSeconds-.25f)<.001f,"omitted older transition stays rejected after repair");
            var motion=part.Client.GetComponent<Hkmp.Fsm.PositionInterpolation>();
            motion.SetNewPosition(Vector3.zero,true);motion.SetNewPosition(new Vector3(3,0,0));
            var nativeEvents=0;native.AnimationEventTriggered=(_,__,___)=>nativeEvents++;
            nativeRoot.AnimationEventTriggered=(_,__,___)=>nativeEvents++;
            presentation.PrepareTakeover();
            Require(child.transform.localPosition.x==3&&child.GetComponent<Hkmp.Fsm.PositionInterpolation>()==null,"takeover flushes latest decorative motion without attaching a replica motion controller to native body");
            Require(native.Playing&&nativeRoot.Playing&&Math.Abs(native.ClipTimeSeconds-replica.ClipTimeSeconds)<.001f&&
                native.Sprite.color==replica.Sprite.color&&nativeEvents==0&&native.AnimationEventTriggered!=null,"takeover copies display and phase without reexecuting native frame events");
            var rootMotion=e.Object.Client.GetComponent<Hkmp.Fsm.PositionInterpolation>();
            rootMotion.SetNewPosition(new Vector3(2,3,0),true);rootMotion.SetNewPosition(new Vector3(3,3,0));rootMotion.Flush();
            Require(e.Object.Client.transform.localPosition.x==3,"root takeover flushes newest authoritative motion sample");
            rootMotion.SetNewPosition(Vector3.zero,true);rootMotion.SetNewPosition(Vector3.zero);
            yield return new WaitForSeconds(.05f);rootMotion.SetNewPosition(new Vector3(2,0,0));
            var until=Time.time+.09f;var between=false;
            while(Time.time<until){yield return null;var x=e.Object.Client.transform.localPosition.x;if(x>.05f&&x<1.95f)between=true;}
            Require(between&&Math.Abs(e.Object.Client.transform.localPosition.x-2)<.01f,"actual Unity frames interpolate sparse movement then hold");
        } finally {
            if(e!=null){EntityReferences.Entities.Remove(id);EntityReferences.Forget(e);e.Destroy();}
            Object.Destroy(source);net.UpdateManager.StopUpdates();net.Disconnect();
        }
    }
}
