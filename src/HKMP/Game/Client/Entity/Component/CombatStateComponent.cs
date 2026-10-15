using System;
using System.Collections.Generic;
using System.Linq;
using Hkmp.Networking.Client;
using Hkmp.Networking.Packet;
using Hkmp.Networking.Packet.Data;
using Hkmp.Util;
using UnityEngine;

namespace Hkmp.Game.Client.Entity.Component;

// Snapshot final native hitbox/visual state, including intermediate parents.
// Each independently managed subtree owns its own snapshot.
internal sealed class CombatStateComponent : EntityComponent {
    private sealed class Parts {
        internal Transform Transform;
        internal Collider2D[] Colliders;
        internal Renderer[] Renderers;
        internal DamageHero[] Damage;
        internal tk2dBaseSprite[] Sprites;
        internal ParticleSystem[] Particles;
        internal MaterialPropertyBlock[] Blocks;
        internal Parts(Transform t) {
            Transform=t;Colliders=t.GetComponents<Collider2D>();Renderers=t.GetComponents<Renderer>();
            Damage=t.GetComponents<DamageHero>();Sprites=t.GetComponents<tk2dBaseSprite>();Particles=t.GetComponents<ParticleSystem>();
            Blocks=new MaterialPropertyBlock[Renderers.Length];for(var i=0;i<Blocks.Length;i++)Blocks[i]=new MaterialPropertyBlock();
        }
    }
    private sealed class Node {internal string Path;internal Parts Host,Client;}
    private Node[] nodes;
    private byte[] previous;
    private float next,rebuild;
    private readonly Dictionary<string,Node> lookup=new();
    private readonly ulong id;
    internal CombatStateComponent(NetClient c,ulong id,HostClientPair<GameObject> objects):base(c,id,objects) {
        this.id=id;MonoBehaviourUtil.Instance.OnUpdateEvent+=Tick;
    }
    private void Build() {
        if(GameObject.Host==null||GameObject.Client==null)return;
        var old=nodes;
        // Retain the original binding when native SetParent removes a child
        // from the hierarchy. Rebuilding by its current path loses its final
        // activation/hitbox state and its counterpart during authority transfer.
        nodes=EntityReferences.Parts(id).Where(p=>p.Host!=null && EntityReferences.Owner(p.Host)?.Id==id)
            .Take(256).Select(p=>new Node{Path=p.Path,Host=new Parts(p.Host.transform),Client=p.Client==null?null:new Parts(p.Client.transform)}).ToArray();
        // Keep a deleted child's terminal state across hierarchy refreshes.
        // Otherwise a refresh can discard the last "gone" update and leave
        // a roar wave or other effect frozen on the replica.
        if(old!=null){var live=new HashSet<string>(nodes.Select(n=>n.Path));nodes=nodes.Concat(old.Where(n=>n.Host.Transform==null&&!live.Contains(n.Path))).Take(256).ToArray();}
        lookup.Clear();
        foreach(var n in nodes)lookup[n.Path]=n;
        rebuild=Time.unscaledTime+2;
    }
    internal void PrepareTakeover() {
        Build();
        if(nodes==null)return;
        foreach(var n in nodes){
            if(n.Host?.Transform==null||n.Client?.Transform==null)continue;
            var source=n.Client.Transform;var target=n.Host.Transform;
            var parentKey=EntityReferences.Capture(source.parent==null?null:source.parent.gameObject);
            var parent=EntityReferences.Resolve(parentKey,false);
            if(parentKey.Length!=0 && parent==null)continue;
            if(parent!=null&&(parent.transform==target||parent.transform.IsChildOf(target)))continue;
            target.SetParent(parent==null?null:parent.transform,false);
            target.localPosition=source.localPosition;target.localEulerAngles=source.localEulerAngles;target.localScale=source.localScale;
            target.gameObject.layer=source.gameObject.layer;
            target.gameObject.SetActive(source.gameObject.activeSelf);
        }
    }
    private static float ReadFloat(Packet p,float limit=100000) {
        var f=p.ReadFloat();if(float.IsNaN(f)||float.IsInfinity(f)||System.Math.Abs(f)>limit)throw new FormatException("Invalid combat number");return f;
    }
    private static void WriteVector(Packet p,Vector3 v){p.Write(v.x);p.Write(v.y);p.Write(v.z);}
    private static Vector3 ReadVector(Packet p)=>new(ReadFloat(p),ReadFloat(p),ReadFloat(p));
    private static void WriteColor(Packet p,Color c){p.Write(c.r);p.Write(c.g);p.Write(c.b);p.Write(c.a);}
    private static Color ReadColor(Packet p)=>new(ReadFloat(p,100),ReadFloat(p,100),ReadFloat(p,100),ReadFloat(p,100));
    private static byte Count<T>(T[] values)=>(byte)System.Math.Min(values.Length,16);
    private void Tick() {
        if(IsControlled||CoopContext.Resetting||GameObject.Host==null||Time.unscaledTime<next)return;
        next=Time.unscaledTime+.05f;if(nodes==null||Time.unscaledTime>=rebuild)Build();if(nodes==null)return;
        var data=new EntityNetworkData{Type=EntityComponentType.CombatState};var p=data.Packet;p.Write((ushort)nodes.Length);
        foreach(var n in nodes){
            var h=n.Host;var t=h.Transform;p.Write(n.Path);p.Write(t!=null);if(t==null)continue;
            p.Write(t.gameObject.activeSelf);p.Write((byte)t.gameObject.layer);
            // An independently registered child is the root of this component,
            // and can detach from its registration parent during native play.
            p.Write(EntityReferences.Capture(t.parent==null?null:t.parent.gameObject));
            if(n.Path.Length!=0){WriteVector(p,t.localPosition);WriteVector(p,t.localEulerAngles);WriteVector(p,t.localScale);}
            else WriteVector(p,t.eulerAngles);
            p.Write(Count(h.Colliders));for(var k=0;k<Count(h.Colliders);k++){
                var c=h.Colliders[k];p.Write(c!=null&&c.enabled);p.Write(c!=null&&c.isTrigger);
                var offset=c==null?Vector2.zero:c.offset;p.Write(offset.x);p.Write(offset.y);
                p.Write((byte)(c is BoxCollider2D?1:c is CircleCollider2D?2:c is CapsuleCollider2D?3:0));
                var size=c is BoxCollider2D box?box.size:c is CapsuleCollider2D cap?cap.size:c is CircleCollider2D cir?new Vector2(cir.radius,cir.radius):Vector2.zero;p.Write(size.x);p.Write(size.y);
            }
            p.Write(Count(h.Renderers));for(var k=0;k<Count(h.Renderers);k++){
                var r=h.Renderers[k];p.Write(r!=null&&r.enabled);p.Write(r is SpriteRenderer);if(r is SpriteRenderer sr)WriteColor(p,sr.color);
                var m=r==null?null:r.sharedMaterial;var flags=(byte)((m!=null&&m.HasProperty("_Color")?1:0)|(m!=null&&m.HasProperty("_TintColor")?2:0));p.Write(flags);
                if((flags&1)!=0)WriteColor(p,m.GetColor("_Color"));if((flags&2)!=0)WriteColor(p,m.GetColor("_TintColor"));
            }
            p.Write(Count(h.Damage));for(var k=0;k<Count(h.Damage);k++){
                var d=h.Damage[k];p.Write(d!=null&&d.enabled);p.Write(d==null?0:d.damageDealt);p.Write(d==null?1:d.hazardType);p.Write(d!=null&&d.shadowDashHazard);
            }
            p.Write(Count(h.Sprites));for(var k=0;k<Count(h.Sprites);k++){
                // Root animation is replayed separately; Update only applies
                // child frame IDs. Unused root frames must not resend hitboxes.
                var s=h.Sprites[k];p.Write(s==null||n.Path.Length==0?-1:s.spriteId);WriteColor(p,s==null?Color.white:s.color);
            }
            p.Write(Count(h.Particles));for(var k=0;k<Count(h.Particles);k++){var ps=h.Particles[k];p.Write(ps!=null&&ps.isPlaying);p.Write(ps!=null&&ps.isEmitting);}
        }
        var bytes=p.ToArray();if(previous!=null&&previous.SequenceEqual(bytes))return;previous=bytes;SendData(data);
    }
    public override void InitializeHost(){previous=null;next=0;rebuild=0;}
    private static byte ReadCount(Packet p){var count=p.ReadByte();if(count>16)throw new FormatException("Too many combat components");return count;}
    public override void Update(EntityNetworkData data,bool snapshot) {
        if(!IsControlled)return;if(nodes==null||Time.unscaledTime>=rebuild)Build();var p=data.Packet;
        var count=p.ReadUShort();if(count>256)throw new FormatException("Too many combat nodes");
        for(var i=0;i<count;i++){
            var path=p.ReadString();if(path.Length>2048)throw new FormatException("Invalid combat path");lookup.TryGetValue(path,out var n);
            if(n==null){var target=EntityReferences.Resolve("e:"+id+"/"+path,true);if(target!=null){n=new Node{Path=path,Client=new Parts(target.transform)};lookup[path]=n;}}
            if(!p.ReadBool()){if(n?.Client?.Transform!=null)n.Client.Transform.gameObject.SetActive(false);continue;}
            var active=p.ReadBool();var layer=p.ReadByte();if(layer>31)throw new FormatException("Invalid combat layer");
            var parentKey=p.ReadString();if(parentKey.Length>2048)throw new FormatException("Invalid combat parent");
            var pos=Vector3.zero;var rot=Vector3.zero;var scale=Vector3.one;if(path.Length!=0){pos=ReadVector(p);rot=ReadVector(p);scale=ReadVector(p);}else rot=ReadVector(p);
            // Dormant native children must remain under the disabled native
            // root until takeover. Detaching and activating both copies creates
            // duplicate terrain colliders and native attack controllers.
            if(path.Length==0)ApplyTransform(n?.Host);
            ApplyTransform(n?.Client);
            // EntityUpdate sends root position/scale, but rotation was optional
            // in the registry. Rotating roots must also rotate every child hitbox.
            void ApplyTransform(Parts b){
                if(b?.Transform==null)return;
                b.Transform.gameObject.layer=layer;
                if(path.Length==0 && ReferenceEquals(b,n?.Host)){b.Transform.eulerAngles=rot;return;}
                var parent=EntityReferences.Resolve(parentKey,true);
                if(parentKey.Length!=0&&parent==null)return;
                if(parent!=null&&(parent.transform==b.Transform||parent.transform.IsChildOf(b.Transform)))return;
                if(path.Length==0){
                    b.Transform.SetParent(parent==null?null:parent.transform,true);
                    b.Transform.eulerAngles=rot;return;
                }
                b.Transform.SetParent(parent==null?null:parent.transform,false);
                b.Transform.localPosition=pos;b.Transform.localEulerAngles=rot;b.Transform.localScale=scale;
                b.Transform.gameObject.SetActive(active);
            }
            var cs=ReadCount(p);for(var k=0;k<cs;k++){
                var enabled=p.ReadBool();var trigger=p.ReadBool();var offset=new Vector2(ReadFloat(p),ReadFloat(p));var kind=p.ReadByte();var size=new Vector2(ReadFloat(p),ReadFloat(p));
                if(kind>3||size.x<0||size.y<0)throw new FormatException("Invalid collider geometry");
                ApplyCollider(n?.Host);ApplyCollider(n?.Client);
                void ApplyCollider(Parts b){if(b==null||k>=b.Colliders.Length||b.Colliders[k]==null)return;var c=b.Colliders[k];c.enabled=enabled;c.isTrigger=trigger;c.offset=offset;if(kind==1&&c is BoxCollider2D box)box.size=size;else if(kind==2&&c is CircleCollider2D cir)cir.radius=size.x;else if(kind==3&&c is CapsuleCollider2D cap)cap.size=size;}
            }
            var rs=ReadCount(p);for(var k=0;k<rs;k++){
                var enabled=p.ReadBool();var sprite=p.ReadBool();var color=sprite?ReadColor(p):Color.white;
                var flags=p.ReadByte();if(flags>3)throw new FormatException("Invalid material flags");var material=(flags&1)!=0?ReadColor(p):Color.white;var tint=(flags&2)!=0?ReadColor(p):Color.white;
                ApplyRenderer(n?.Host);ApplyRenderer(n?.Client);
                void ApplyRenderer(Parts b){if(b==null||k>=b.Renderers.Length||b.Renderers[k]==null)return;var r=b.Renderers[k];r.enabled=enabled;if(sprite&&r is SpriteRenderer sr)sr.color=color;}
                if(n?.Client!=null&&k<n.Client.Renderers.Length&&n.Client.Renderers[k]!=null&&flags!=0){var r=n.Client.Renderers[k];var block=n.Client.Blocks[k];r.GetPropertyBlock(block);if((flags&1)!=0)block.SetColor("_Color",material);if((flags&2)!=0)block.SetColor("_TintColor",tint);r.SetPropertyBlock(block);}
            }
            var ds=ReadCount(p);for(var k=0;k<ds;k++){
                var enabled=p.ReadBool();var amount=p.ReadInt();var hazard=p.ReadInt();var shadow=p.ReadBool();if(amount<0||amount>100||hazard<0||hazard>100)throw new FormatException("Invalid damage");ApplyDamage(n?.Host);ApplyDamage(n?.Client);
                void ApplyDamage(Parts b){if(b==null||k>=b.Damage.Length||b.Damage[k]==null)return;var d=b.Damage[k];d.enabled=enabled;d.damageDealt=amount;d.hazardType=hazard;d.shadowDashHazard=shadow;}
            }
            var ss=ReadCount(p);for(var k=0;k<ss;k++){
                var frame=p.ReadInt();var color=ReadColor(p);if(frame< -1||frame>65535)throw new FormatException("Invalid sprite frame");
                // Root animations use EntityUpdate; child FSMs are disabled.
                if(n?.Client==null||k>=n.Client.Sprites.Length||n.Client.Sprites[k]==null)continue;
                var s=n.Client.Sprites[k];s.color=color;if(path.Length!=0&&frame>=0&&s.Collection!=null&&frame<s.Collection.spriteDefinitions.Length){var a=s.GetComponent<tk2dSpriteAnimator>();if(a!=null)a.enabled=false;s.spriteId=frame;}
            }
            var ps=ReadCount(p);for(var k=0;k<ps;k++){
                var playing=p.ReadBool();var emitting=p.ReadBool();if(n?.Client==null||k>=n.Client.Particles.Length||n.Client.Particles[k]==null)continue;
                var part=n.Client.Particles[k];if(playing&&!part.isPlaying)part.Play(false);else if(!playing&&part.isPlaying)part.Stop(false,ParticleSystemStopBehavior.StopEmittingAndClear);else if(!emitting&&part.isEmitting)part.Stop(false,ParticleSystemStopBehavior.StopEmitting);
            }
        }
    }
    public override void Destroy()=>MonoBehaviourUtil.Instance.OnUpdateEvent-=Tick;
}
