using System;
using System.Collections.Generic;
using System.Linq;
using Hkmp.Game.Client.Entity.Action;
using Hkmp.Util;
using HutongGames.PlayMaker;
using HutongGames.PlayMaker.Actions;
using Mono.Cecil.Cil;
using MonoMod.Cil;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Hkmp.Game.Client.Entity;

// Capture the actual native spawn, including actions with no store variable.
// Wait until native setup finishes before freezing/cloning the new object.
internal static class CombatSpawns {
    private static readonly Dictionary<int, EntitySpawnDetails> Pending = new();
    private static readonly Queue<int> PendingOrder = new();
    private static readonly long FrameBudget = System.Diagnostics.Stopwatch.Frequency / 500;
    private static readonly Dictionary<int, bool> CombatPrefabs = new();
    private static bool registered;
    internal static void Register() {
        if(registered)return;registered=true;
        On.ObjectPool.Spawn_GameObject_Transform_Vector3_Quaternion+=Spawn;
        IL.HutongGames.PlayMaker.Actions.CreateObject.OnEnter+=Create;
        On.EnemyDeathEffects.EmitCorpse+=Corpse;
        UnityEngine.SceneManagement.SceneManager.activeSceneChanged+=Clear;
    }
    internal static void Unregister() {
        if(!registered)return;registered=false;
        On.ObjectPool.Spawn_GameObject_Transform_Vector3_Quaternion-=Spawn;
        IL.HutongGames.PlayMaker.Actions.CreateObject.OnEnter-=Create;
        On.EnemyDeathEffects.EmitCorpse-=Corpse;
        UnityEngine.SceneManagement.SceneManager.activeSceneChanged-=Clear;
        Pending.Clear(); PendingOrder.Clear();
    }
    private static void Clear(Scene a,Scene b){Pending.Clear();PendingOrder.Clear();CombatPrefabs.Clear();}
    private static void Queue(GameObject go, GameObject prefab, FsmStateAction action, GameObject source=null) {
        if(go==null || prefab==null || CoopContext.Resetting)return;
        var key=prefab.GetInstanceID();
        if(!CombatPrefabs.TryGetValue(key,out var combat))CombatPrefabs[key]=combat=EntityRegistry.IsHazard(prefab)||EntityRegistry.TryGetSpawnEntry(prefab,out _);
        if(!combat)return;
        if(Pending.Count>=2048)return;
        var instance = go.GetInstanceID();
        if (!Pending.ContainsKey(instance)) PendingOrder.Enqueue(instance);
        source ??= action?.Fsm?.GameObject;
        var owner = EntityReferences.Owner(source);
        string receipt = null;
        if (owner?.Type is EntityType.Ooma or EntityType.OomaCorpse or EntityType.OomaCore) {
            // Both links can die before the next flush/remote spawn arrives.
            // Anchor the descriptor to the live encounter, retaining the
            // hitter's receipt independently from Uumuu's chosen target.
            var boss = EntityReferences.Entities?.Values.FirstOrDefault(e => e.Type == EntityType.Uumuu);
            if (boss != null) { source = boss.Object.Host; receipt = owner.BossTargetReceipt?.Value ?? ""; }
        }
        Pending[instance]=new EntitySpawnDetails{GameObject=go,Prefab=prefab,Action=action,Source=source,AttackTargetReceipt=receipt,Type=EntitySpawnType.FsmAction};
    }
    private static GameObject Spawn(On.ObjectPool.orig_Spawn_GameObject_Transform_Vector3_Quaternion orig,GameObject prefab,Transform parent,Vector3 pos,Quaternion rot) {
        var action=FsmActionHooks.CurrentAction;
        var go=orig(prefab,parent,pos,rot);
        try {Queue(go,prefab,action,parent==null?null:parent.gameObject);}
        catch(Exception e){Hkmp.Logging.Logger.Error("Combat pool observer failed: "+e);}
        return go;
    }
    private static void Create(ILContext il) {
        var c=new ILCursor(il);
        c.GotoNext(i=>i.OpCode==OpCodes.Call&&i.Operand is Mono.Cecil.MethodReference m&&m.DeclaringType.FullName=="UnityEngine.Object"&&m.Name=="Instantiate");
        c.Index++;c.Emit(OpCodes.Ldarg_0);
        c.EmitDelegate<Func<GameObject,CreateObject,GameObject>>((go,a)=>{
            try{Queue(go,a.gameObject.Value,a);}catch(Exception e){Hkmp.Logging.Logger.Error("Combat instantiate observer failed: "+e);}return go;
        });
    }
    private static void Corpse(On.EnemyDeathEffects.orig_EmitCorpse orig, EnemyDeathEffects self, float? direction, bool isWatery, bool spellBurn) {
        // Native corpses are pre-instantiated and later activated; neither the
        // pool nor CreateObject observer sees this new combat lifetime. Queue
        // its source before activation can spawn the next link (Ooma core).
        try {
            if (EntityReferences.Owner(self.gameObject)?.Type == EntityType.Ooma) {
                var corpse = Modding.ReflectionHelper.GetField<EnemyDeathEffects, GameObject>(self, "corpse");
                Queue(corpse, Modding.ReflectionHelper.GetField<EnemyDeathEffects, GameObject>(self, "corpsePrefab"), null, self.gameObject);
            }
        } catch(Exception e) { Hkmp.Logging.Logger.Error("Combat corpse observer failed: " + e); }
        orig(self, direction, isWatery, spellBurn);
    }
    internal static void Flush() {
        var count = PendingOrder.Count;
        if (count == 0) return;
        var start = System.Diagnostics.Stopwatch.GetTimestamp();
        for (var i = 0; i < count; i++) {
            var key = PendingOrder.Dequeue();
            if (!Pending.TryGetValue(key, out var d)) continue;
            Pending.Remove(key);
            if (d.GameObject != null) {
                try{EntityFsmActions.CallEntitySpawnEvent(d);}
                catch(Exception e){Hkmp.Logging.Logger.Error("Combat spawn registration failed: "+e);}
            }
            // Finish each native lifetime atomically, then yield between
            // lifetimes. New/reentrant spawns keep FIFO order for next frame.
            if (System.Diagnostics.Stopwatch.GetTimestamp() - start >= FrameBudget) break;
        }
    }
}
