using System.Collections.Generic;
using UnityEngine;

namespace Hkmp.Game.Client.Entity;

// Installing/removing a MonoMod hook rebuilds its native detour chain. Pooled
// projectiles must change dictionary entries, not rebuild five detours per life.
internal static class EntityHooks {
    private static readonly Dictionary<int, On.tk2dSpriteAnimator.hook_Play_tk2dSpriteAnimationClip_float_float> Animators = new();
    private static readonly Dictionary<int, On.ObjectPool.hook_Recycle_GameObject> Recyclers = new();
    private static readonly Dictionary<int, On.HutongGames.PlayMaker.Actions.ActivateGameObject.hook_DoActivateGameObject> Activators = new();
    private static readonly Dictionary<int, On.HealthManager.hook_Die> Deaths = new();
    private static readonly Dictionary<int, On.HealthManager.hook_ApplyExtraDamage> Extras = new();
    private static bool registered;
    internal static void Register() {
        if (registered) return;
        registered = true;
        On.tk2dSpriteAnimator.Play_tk2dSpriteAnimationClip_float_float += Animate;
        On.ObjectPool.Recycle_GameObject += Recycle;
        On.HutongGames.PlayMaker.Actions.ActivateGameObject.DoActivateGameObject += Activate;
        On.HealthManager.Die += Die;
        On.HealthManager.ApplyExtraDamage += Extra;
        On.HutongGames.PlayMaker.Fsm.ProcessEvent += ProcessEvent;
        On.HutongGames.PlayMaker.Fsm.EnterState += EnterState;
        On.HutongGames.PlayMaker.Fsm.BroadcastEvent_FsmEvent_bool += Broadcast;
        On.Recoil.RecoilByDirection += Recoil;
    }
    internal static void Unregister() {
        if (!registered) return;
        registered = false;
        On.tk2dSpriteAnimator.Play_tk2dSpriteAnimationClip_float_float -= Animate;
        On.ObjectPool.Recycle_GameObject -= Recycle;
        On.HutongGames.PlayMaker.Actions.ActivateGameObject.DoActivateGameObject -= Activate;
        On.HealthManager.Die -= Die;
        On.HealthManager.ApplyExtraDamage -= Extra;
        On.HutongGames.PlayMaker.Fsm.ProcessEvent -= ProcessEvent;
        On.HutongGames.PlayMaker.Fsm.EnterState -= EnterState;
        On.HutongGames.PlayMaker.Fsm.BroadcastEvent_FsmEvent_bool -= Broadcast;
        On.Recoil.RecoilByDirection -= Recoil;
        Animators.Clear(); Recyclers.Clear(); Activators.Clear(); Deaths.Clear(); Extras.Clear();
    }
    private static void Put<T>(Dictionary<int, T> table, Object obj, T handler) {
        if (!ReferenceEquals(obj, null)) table[obj.GetInstanceID()] = handler;
    }
    private static void Remove<T>(Dictionary<int, T> table, Object obj) {
        // A destroyed Unity object still has its managed instance ID.
        if (!ReferenceEquals(obj, null)) table.Remove(obj.GetInstanceID());
    }
    internal static void WatchAnimator(tk2dSpriteAnimator host, tk2dSpriteAnimator client, On.tk2dSpriteAnimator.hook_Play_tk2dSpriteAnimationClip_float_float handler) {
        Put(Animators, host, handler); Put(Animators, client, handler);
    }
    internal static void ForgetAnimator(tk2dSpriteAnimator host, tk2dSpriteAnimator client) { Remove(Animators, host); Remove(Animators, client); }
    internal static void WatchRecycle(GameObject client, On.ObjectPool.hook_Recycle_GameObject handler) => Put(Recyclers, client, handler);
    internal static void ForgetRecycle(GameObject client) => Remove(Recyclers, client);
    internal static void WatchActivation(GameObject host, On.HutongGames.PlayMaker.Actions.ActivateGameObject.hook_DoActivateGameObject handler) => Put(Activators, host, handler);
    internal static void ForgetActivation(GameObject host) => Remove(Activators, host);
    internal static void WatchHealth(HealthManager host, HealthManager client, On.HealthManager.hook_Die death, On.HealthManager.hook_ApplyExtraDamage extra) {
        Put(Deaths, host, death); Put(Deaths, client, death); Put(Extras, host, extra); Put(Extras, client, extra);
    }
    internal static void ForgetHealth(HealthManager host, HealthManager client) { Remove(Deaths, host); Remove(Deaths, client); Remove(Extras, host); Remove(Extras, client); }
    private static void Animate(On.tk2dSpriteAnimator.orig_Play_tk2dSpriteAnimationClip_float_float orig, tk2dSpriteAnimator self, tk2dSpriteAnimationClip clip, float time, float fps) {
        if (Animators.TryGetValue(self.GetInstanceID(), out var handler)) handler(orig, self, clip, time, fps);
        else orig(self, clip, time, fps);
    }
    private static void Recycle(On.ObjectPool.orig_Recycle_GameObject orig, GameObject obj) {
        if (!ReferenceEquals(obj, null) && Recyclers.TryGetValue(obj.GetInstanceID(), out var handler)) handler(orig, obj);
        else orig(obj);
    }
    private static void Activate(On.HutongGames.PlayMaker.Actions.ActivateGameObject.orig_DoActivateGameObject orig, HutongGames.PlayMaker.Actions.ActivateGameObject self) {
        var obj = self.Fsm?.GetOwnerDefaultTarget(self.gameObject);
        if (obj != null && Activators.TryGetValue(obj.GetInstanceID(), out var handler)) handler(orig, self);
        else orig(self);
    }
    private static void Die(On.HealthManager.orig_Die orig, HealthManager self, float? direction, AttackTypes type, bool ignore) {
        if (Deaths.TryGetValue(self.GetInstanceID(), out var handler)) handler(orig, self, direction, type, ignore);
        else orig(self, direction, type, ignore);
    }
    private static void Extra(On.HealthManager.orig_ApplyExtraDamage orig, HealthManager self, int amount) {
        if (Extras.TryGetValue(self.GetInstanceID(), out var handler)) handler(orig, self, amount);
        else orig(self, amount);
    }
    private static void EnterState(On.HutongGames.PlayMaker.Fsm.orig_EnterState orig, HutongGames.PlayMaker.Fsm self,
        HutongGames.PlayMaker.FsmState state) {
        var entity = EntityReferences.Owner(self.GameObject);
        if (entity == null) orig(self, state);
        else entity.EnterNativeState(orig, self, state);
    }
    private static void ProcessEvent(On.HutongGames.PlayMaker.Fsm.orig_ProcessEvent orig, HutongGames.PlayMaker.Fsm self,
        HutongGames.PlayMaker.FsmEvent ev, HutongGames.PlayMaker.FsmEventData data) {
        var entity = EntityReferences.Owner(self.GameObject);
        if (entity != null && entity.QueueEntranceEvent(self, ev)) return;
        orig(self, ev, data);
    }
    private static void Broadcast(On.HutongGames.PlayMaker.Fsm.orig_BroadcastEvent_FsmEvent_bool orig,
        HutongGames.PlayMaker.Fsm self, HutongGames.PlayMaker.FsmEvent ev, bool excludeSelf) {
        // Disabled FSMs are removed from PlayMaker's broadcast list. Preserve
        // entrance events while authority is negotiated, without running AI.
        if (ev!=null && ev.Name!="FINISHED" && Entity.WaitingForEntrance.Count!=0) {
            foreach (var entity in new List<Entity>(Entity.WaitingForEntrance))
                entity.QueueEntranceBroadcast(self, ev, excludeSelf);
        }
        orig(self, ev, excludeSelf);
    }
    private static void Recoil(On.Recoil.orig_RecoilByDirection orig, global::Recoil self, int direction, float magnitude) {
        var e=EntityReferences.Owner(self.gameObject);
        // Hit effects remain native. Only the scene authority integrates recoil;
        // moving a replica locally detaches its hitboxes from remote bullets.
        if(e!=null && e.Object.Client!=null && self.transform.IsChildOf(e.Object.Client.transform))return;
        orig(self,direction,magnitude);
    }
}
