using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using Hkmp.Fsm;
using Hkmp.Networking.Client;
using Hkmp.Networking.Packet;
using Hkmp.Networking.Packet.Data;
using Hkmp.Util;
using UnityEngine;
using Logger = Hkmp.Logging.Logger;

namespace Hkmp.Game.Client.Entity.Component;

// Presentation is a recoverable state stream. Native AI/hitboxes remain in
// CombatState; moving animation clocks never dirty that much larger payload.
internal sealed class PresentationComponent : EntityComponent {
    private sealed class Parts {
        internal Transform Transform;
        internal Renderer[] Renderers;
        internal tk2dBaseSprite[] Sprites;
        internal tk2dSpriteAnimator[] Animators;
        internal ParticleSystem[] Particles;
        internal MaterialPropertyBlock[] Blocks;
        internal uint[] Playback;
        internal string[] Clips;
        internal tk2dSpriteAnimationClip[] EffectiveClips;
        internal PositionInterpolation Motion;
        internal Parts(GameObject go) {
            if(go==null)return;
            Transform=go.transform;Refresh();
        }
        internal void Refresh() {
            if(Transform==null)return;
            var go=Transform.gameObject;
            var renderers=go.GetComponents<Renderer>();var sprites=go.GetComponents<tk2dBaseSprite>();var particles=go.GetComponents<ParticleSystem>();
            var animators=sprites.Select(s=>s.GetComponent<tk2dSpriteAnimator>()).ToArray();
            if(Renderers!=null&&Renderers.SequenceEqual(renderers)&&Sprites.SequenceEqual(sprites)&&Particles.SequenceEqual(particles)&&Animators.SequenceEqual(animators))return;
            Renderers=renderers;Sprites=sprites;
            Animators=Sprites.Select(s=>s.GetComponent<tk2dSpriteAnimator>()).ToArray();Particles=go.GetComponents<ParticleSystem>();
            Blocks=Renderers.Select(_=>new MaterialPropertyBlock()).ToArray();Playback=new uint[Sprites.Length];Clips=new string[Sprites.Length];EffectiveClips=new tk2dSpriteAnimationClip[Sprites.Length];
        }
    }
    private sealed class Binding { internal string Path;internal bool OwnPose;internal Parts Host,Client; }
    private struct MaterialColors { internal byte Flags;internal PresentationState.Tint Color,Tint; }
    private static readonly Dictionary<Material,MaterialColors> MaterialCache=new();
    private static int materialFrame=-1;
    private static readonly ConditionalWeakTable<NetClient,PresentationBudget> Budgets=new();
    private readonly PresentationBudget budget;
    private readonly ulong id;
    private readonly FinalBossCorpseContinuation finalCorpse;
    private readonly Dictionary<string,Binding> bindings=new(StringComparer.Ordinal);
    private readonly Dictionary<int,uint> playback=new();
    private readonly HashSet<tk2dSpriteAnimator> watched=new();
    private readonly On.tk2dSpriteAnimator.hook_Play_tk2dSpriteAnimationClip_float_float playHook;
    private readonly PresentationOrder order=new();
    private PresentationState sample=new(),previous;
    private readonly PresentationState localSample=new();
    private Binding[] nodes=Array.Empty<Binding>();
    private uint revision,playbackSerial;
    private float next,rebuild,refresh,dormantRefresh;
    private bool applying,reportedLimit,wasVisible;
    internal PresentationComponent(NetClient client,ulong entityId,HostClientPair<GameObject> objects,FinalBossCorpseContinuation corpse)
        :base(client,entityId,objects) {
        id=entityId;finalCorpse=corpse;budget=Budgets.GetValue(client,_=>new PresentationBudget());
        playHook=OnPlay;MonoBehaviourUtil.Instance.OnUpdateEvent+=Tick;
    }
    private bool Personal(string path)=>finalCorpse?.IsPersonalPath(path)==true;
    private void Build() {
        if(GameObject.Host==null||GameObject.Client==null)return;
        var selected=new List<Binding>();
        foreach(var part in EntityReferences.Parts(id)) {
            if(part.Host==null||part.Client==null||EntityReferences.Owner(part.Host)?.Id!=id||Personal(part.Path))continue;
            var components=part.Host.GetComponents<UnityEngine.Component>();
            if(!components.Any(c=>c is Renderer||c is tk2dBaseSprite||c is ParticleSystem))continue;
            if(selected.Count==PresentationState.MaxNodes){if(!reportedLimit){reportedLimit=true;Logger.Warn($"Presentation node limit reached: {id} {GameObject.Host.name}");}break;}
            if(!bindings.TryGetValue(part.Path,out var b)||b.Host.Transform!=part.Host.transform||b.Client.Transform!=part.Client.transform) {
                b=new Binding{Path=part.Path,OwnPose=part.Path.Length!=0&&CombatStateComponent.IsDecoration(part.Host),Host=new Parts(part.Host),Client=new Parts(part.Client)};
                bindings[part.Path]=b;
            }else{b.Host.Refresh();b.Client.Refresh();}
            selected.Add(b);
        }
        foreach(var old in nodes)if(old.Host.Transform==null&&!selected.Any(n=>n.Path==old.Path)&&selected.Count<PresentationState.MaxNodes)selected.Add(old);
        nodes=selected.ToArray();
        var needed=new HashSet<tk2dSpriteAnimator>(nodes.Where(n=>n.Host.Transform!=null).SelectMany(n=>n.Host.Animators.Concat(n.Client.Animators)).Where(a=>a!=null));
        foreach(var a in watched.Where(a=>!needed.Contains(a)).ToArray()){EntityHooks.ForgetAnimator(a,null,playHook);watched.Remove(a);}
        foreach(var a in needed)if(watched.Add(a))EntityHooks.WatchAnimator(a,null,playHook);
        foreach(var b in selected.Where(b=>b.Client.Transform!=null))foreach(var a in b.Client.Animators)if(a!=null){a.playAutomatically=false;a.AnimationCompleted=null;a.AnimationEventTriggered=null;}
        rebuild=CombatSampling.Next(Time.unscaledTime,id,2);
    }
    private void OnPlay(On.tk2dSpriteAnimator.orig_Play_tk2dSpriteAnimationClip_float_float orig,tk2dSpriteAnimator self,tk2dSpriteAnimationClip clip,float time,float fps) {
        // A detached final corpse has become this player's native ending.
        // Its own FSM must regain animation and completion events immediately,
        // including before the next hierarchy refresh removes this observer.
        if(finalCorpse?.KeepClientPart(self.gameObject)==true){orig(self,clip,time,fps);return;}
        if(IsControlled&&!applying&&EntityReferences.IsReplica(self.gameObject))return;
        var starts=clip!=null&&(self.CurrentClip!=clip||!self.Playing||time!=0);
        orig(self,clip,time,fps);
        if(!IsControlled&&!applying&&starts&&clip!=null)playback[self.GetInstanceID()]=++playbackSerial;
    }
    private static PresentationState.Vector Vec(Vector3 v)=>new(v.x,v.y,v.z);
    private static Vector3 Vec(PresentationState.Vector v)=>new(v.X,v.Y,v.Z);
    private static PresentationState.Tint Tint(Color c)=>new(c.r,c.g,c.b,c.a);
    private static Color Tint(PresentationState.Tint c)=>new(c.R,c.G,c.B,c.A);
    private static MaterialColors Colors(Material material) {
        // Authorities usually share sprite materials across hundreds of
        // pooled entities. Read a shared material once per simulation frame.
        if(materialFrame!=Time.frameCount){MaterialCache.Clear();materialFrame=Time.frameCount;}
        if(MaterialCache.TryGetValue(material,out var colors))return colors;
        if(material.HasProperty("_Color")){colors.Flags|=2;colors.Color=Tint(material.GetColor("_Color"));}
        if(material.HasProperty("_TintColor")){colors.Flags|=4;colors.Tint=Tint(material.GetColor("_TintColor"));}
        MaterialCache[material]=colors;return colors;
    }
    private static void Resize<T>(List<T> items,int count,Func<T> create) {
        while(items.Count<count)items.Add(create());
        if(items.Count>count)items.RemoveRange(count,items.Count-count);
    }
    internal PresentationState Capture(bool native = true) {
        // Reuse the pending and last-sent models. Serializing every unchanged
        // subtree just to compare bytes allocates heavily in projectile rooms.
        var state=native?sample:localSample;var index=0;
        foreach(var b in nodes) {
            if(Personal(b.Path))continue;
            if(index==state.Nodes.Count)state.Nodes.Add(new PresentationState.Node());
            var n=state.Nodes[index++];var h=native?b.Host:b.Client;var t=h.Transform;
            n.Path=b.Path;n.Gone=t==null;if(t==null)continue;
            n.Active=t.gameObject.activeSelf;n.OwnPose=b.OwnPose;
            if(n.OwnPose){n.Parent=EntityReferences.Capture(t.parent==null?null:t.parent.gameObject);n.Position=Vec(t.localPosition);n.Rotation=Vec(t.localEulerAngles);n.Scale=Vec(t.localScale);}
            Resize(n.Renderers,System.Math.Min(h.Renderers.Length,PresentationState.MaxComponents),()=>new PresentationState.Renderer());
            for(var i=0;i<n.Renderers.Count;i++) {
                var r=h.Renderers[i];var item=n.Renderers[i];item.Enabled=r!=null&&r.enabled;item.Colors=0;
                if(r is SpriteRenderer sr){item.Colors|=1;item.Color=Tint(sr.color);}
                var m=r==null?null:r.sharedMaterial;
                if(m!=null){var colors=Colors(m);item.Colors|=colors.Flags;item.Material=colors.Color;item.TintColor=colors.Tint;}
            }
            Resize(n.Sprites,System.Math.Min(h.Sprites.Length,PresentationState.MaxComponents),()=>new PresentationState.Sprite());
            for(var i=0;i<n.Sprites.Count;i++) {
                var sprite=h.Sprites[i];var animator=h.Animators[i];var clip=animator==null?null:animator.CurrentClip;var item=n.Sprites[i];
                item.Frame=sprite==null?-1:sprite.spriteId;item.Color=Tint(sprite==null?Color.white:sprite.color);
                item.Enabled=animator!=null&&animator.enabled;item.Clip="";item.Playback=0;item.Wrap=0;item.Playing=false;item.Time=0;item.Fps=0;
                if(clip!=null) {
                    item.Clip=clip.name;item.Wrap=(byte)clip.wrapMode;item.Playing=animator.enabled&&animator.Playing&&clip.fps>0&&clip.wrapMode!=tk2dSpriteAnimationClip.WrapMode.RandomFrame&&clip.wrapMode!=tk2dSpriteAnimationClip.WrapMode.RandomLoop;
                    item.Time=System.Math.Max(0,animator.ClipTimeSeconds);item.Fps=System.Math.Max(0,animator.ClipFps);
                    if(native)playback.TryGetValue(animator.GetInstanceID(),out item.Playback);else item.Playback=h.Playback[i];
                }
            }
            n.Particles.Clear();
            for(var i=0;i<h.Particles.Length&&i<PresentationState.MaxComponents;i++) {
                var particle=h.Particles[i];n.Particles.Add((byte)((particle!=null&&particle.isPlaying?1:0)|(particle!=null&&particle.isEmitting?2:0)));
            }
        }
        if(state.Nodes.Count>index)state.Nodes.RemoveRange(index,state.Nodes.Count-index);
        return state;
    }
    private void Tick() {
        if(CoopContext.Resetting||GameObject.Host==null||Time.unscaledTime<next)return;
        next=CombatSampling.Next(Time.unscaledTime,id,.05f);
        if(Time.unscaledTime>=rebuild)Build();
        if(IsControlled||nodes.Length==0)return;
        var visible=GameObject.Host.activeInHierarchy;
        if(!visible)foreach(var b in nodes) {
            // Detached effects can remain visible after their owner sleeps.
            // They must retain normal capture and recovery while visible.
            if(b.Host.Transform!=null&&b.Host.Transform.gameObject.activeInHierarchy){visible=true;break;}
        }
        if(!visible&&!wasVisible&&previous!=null&&Time.unscaledTime<dormantRefresh)return;
        wasVisible=visible;
        if(!visible)dormantRefresh=CombatSampling.Next(Time.unscaledTime,id,.85f);
        var state=Capture();
        if(state.SamePresentation(previous)&&(!visible||Time.unscaledTime<refresh))return;
        state.Revision=revision+1;Packet encoded;
        try{encoded=state.Encode();}
        catch(FormatException e){if(!reportedLimit){reportedLimit=true;Logger.Warn($"Presentation snapshot limit: {id} {GameObject.Host.name}: {e.Message}");}return;}
        if(!budget.Spend(Time.unscaledTime,encoded.Length))return;
        revision=state.Revision;sample=previous??new PresentationState();previous=state;
        // Static poses already have reliable delivery and a late-join cache.
        // Keep a slower repair for them; only running visuals need live clocks.
        var running=state.Nodes.Any(n=>!n.Gone&&(n.Sprites.Any(sprite=>sprite.Playing)||n.Particles.Any(p=>(p&1)!=0)));
        refresh=CombatSampling.Next(Time.unscaledTime,id,running?.85f:5f);
        SendData(new EntityNetworkData{Type=EntityComponentType.Presentation,Packet=encoded});
    }
    public override void InitializeHost(){previous=null;next=0;refresh=0;dormantRefresh=0;wasVisible=false;order.Clear();}
    public override void Update(EntityNetworkData data,bool snapshot) {
        if(!IsControlled)return;
        var state=PresentationState.Decode(data.Packet);
        if(!order.Accept(CoopContext.Epoch,state.Revision))return;
        if(Time.unscaledTime>=rebuild)Build();
        applying=true;
        try {foreach(var n in state.Nodes)Apply(n,false,snapshot);}
        finally {applying=false;}
    }
    private void Apply(PresentationState.Node n,bool native,bool snapshot) {
        if(Personal(n.Path))return;
        if(!bindings.TryGetValue(n.Path,out var binding)) {
            var host=EntityReferences.Resolve("e:"+id+(n.Path.Length==0?"":"/"+n.Path),false);
            var client=EntityReferences.Resolve("e:"+id+(n.Path.Length==0?"":"/"+n.Path),true);
            if(host==null||client==null)return;
            binding=new Binding{Path=n.Path,Host=new Parts(host),Client=new Parts(client)};bindings[n.Path]=binding;
        }
        binding.OwnPose=n.OwnPose;
        var b=native?binding.Host:binding.Client;var t=b.Transform;if(t==null)return;
        if(n.Gone){t.gameObject.SetActive(false);return;}
        if(n.OwnPose&&n.Path.Length!=0) {
            var parent=EntityReferences.Resolve(n.Parent,!native);
            if((n.Parent.Length==0||parent!=null)&&(parent==null||parent.transform!=t&&!parent.transform.IsChildOf(t))) {
                var p=parent==null?null:parent.transform;var changed=t.parent!=p;
                if(changed){b.Motion?.Flush();t.SetParent(p,false);}
                if(native)t.localPosition=Vec(n.Position);
                else {
                    b.Motion??=t.GetComponent<PositionInterpolation>()??t.gameObject.AddComponent<PositionInterpolation>();
                    b.Motion.Smooth=true;b.Motion.SetNewPosition(Vec(n.Position),snapshot||changed||!n.Active);
                }
                t.localEulerAngles=Vec(n.Rotation);t.localScale=Vec(n.Scale);t.gameObject.SetActive(n.Active);
            }
        }
        for(var i=0;i<n.Renderers.Count&&i<b.Renderers.Length;i++) {
            var r=b.Renderers[i];if(r==null)continue;var v=n.Renderers[i];r.enabled=v.Enabled;
            if((v.Colors&1)!=0&&r is SpriteRenderer sr)sr.color=Tint(v.Color);
            if((v.Colors&6)!=0){var block=b.Blocks[i];r.GetPropertyBlock(block);if((v.Colors&2)!=0)block.SetColor("_Color",Tint(v.Material));if((v.Colors&4)!=0)block.SetColor("_TintColor",Tint(v.TintColor));r.SetPropertyBlock(block);}
        }
        for(var i=0;i<n.Sprites.Count&&i<b.Sprites.Length;i++) {
            var s=b.Sprites[i];if(s==null)continue;var v=n.Sprites[i];s.color=Tint(v.Color);var a=b.Animators[i];
            if(a!=null) {
                var clip=v.Clip.Length==0?null:a.GetClipByName(v.Clip);
                if(clip!=null&&(byte)clip.wrapMode!=v.Wrap) {
                    var effective=b.EffectiveClips[i];
                    if(effective==null||effective.name!=clip.name||(byte)effective.wrapMode!=v.Wrap)
                        b.EffectiveClips[i]=effective=new tk2dSpriteAnimationClip{name=clip.name,fps=clip.fps,frames=clip.frames,loopStart=clip.loopStart,wrapMode=(tk2dSpriteAnimationClip.WrapMode)v.Wrap};
                    clip=effective;
                }
                if(!native){a.AnimationCompleted=null;a.AnimationEventTriggered=null;a.playAutomatically=false;}
                // Seeking an animation can emit its frame events immediately.
                // Takeover retains native callbacks, but suppresses them while
                // copying the already executed presentation to the native body.
                var completed=a.AnimationCompleted;
                var triggered=a.AnimationEventTriggered;
                a.AnimationCompleted=null;a.AnimationEventTriggered=null;
                try {
                a.playAutomatically=false;
                var changed=b.Playback[i]!=v.Playback||b.Clips[i]!=v.Clip;
                if(v.Playing&&clip!=null&&clip.frames!=null&&clip.frames.Length!=0) {
                    a.enabled=true;
                    var duration=clip.frames.Length/System.Math.Max(1,v.Fps);
                    var drift=a.CurrentClip==null?float.PositiveInfinity:System.Math.Abs(a.ClipTimeSeconds-v.Time);
                    if(clip.wrapMode==tk2dSpriteAnimationClip.WrapMode.Loop&&duration>0)drift=(float)System.Math.Abs(System.Math.IEEERemainder(drift,duration));
                    if(changed||a.CurrentClip!=clip||!a.Playing||drift>.18f){a.Stop();a.Play(clip,v.Time,v.Fps);}
                    a.ClipFps=v.Fps;
                } else {
                    if(clip!=null&&(changed||a.CurrentClip!=clip))a.Play(clip,v.Time,v.Fps);
                    a.Stop();a.enabled=v.Enabled;
                }
                b.Playback[i]=v.Playback;b.Clips[i]=v.Clip;
                } finally {
                    if(native){a.AnimationCompleted=completed;a.AnimationEventTriggered=triggered;}
                }
            }
            if(!v.Playing&&v.Frame>=0&&s.Collection!=null&&v.Frame<s.Collection.spriteDefinitions.Length)s.spriteId=v.Frame;
        }
        for(var i=0;i<n.Particles.Count&&i<b.Particles.Length;i++) {
            var p=b.Particles[i];if(p==null)continue;var v=n.Particles[i];
            if((v&1)!=0&&!p.isPlaying)p.Play(false);else if((v&1)==0&&p.isPlaying)p.Stop(false,ParticleSystemStopBehavior.StopEmittingAndClear);else if((v&2)==0&&p.isEmitting)p.Stop(false,ParticleSystemStopBehavior.StopEmitting);
        }
    }
    internal void PrepareTakeover() {
        Build();
        foreach(var b in nodes)b.Client.Motion?.Flush();
        var state=Capture(false);
        applying=true;
        try {foreach(var node in state.Nodes)Apply(node,true,true);}
        finally {applying=false;}
    }
    public override void Destroy() {
        MonoBehaviourUtil.Instance.OnUpdateEvent-=Tick;
        foreach(var a in watched)EntityHooks.ForgetAnimator(a,null,playHook);
        watched.Clear();bindings.Clear();playback.Clear();
    }
}
