using System;
using System.Collections.Generic;
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
    private static readonly Dictionary<int, bool> CombatPrefabs = new();
    private static bool registered;
    internal static void Register() {
        if(registered)return;registered=true;
        On.ObjectPool.Spawn_GameObject_Transform_Vector3_Quaternion+=Spawn;
        IL.HutongGames.PlayMaker.Actions.CreateObject.OnEnter+=Create;
        UnityEngine.SceneManagement.SceneManager.activeSceneChanged+=Clear;
    }
    internal static void Unregister() {
        if(!registered)return;registered=false;
        On.ObjectPool.Spawn_GameObject_Transform_Vector3_Quaternion-=Spawn;
        IL.HutongGames.PlayMaker.Actions.CreateObject.OnEnter-=Create;
        UnityEngine.SceneManagement.SceneManager.activeSceneChanged-=Clear;
        Pending.Clear();
    }
    private static void Clear(Scene a,Scene b){Pending.Clear();CombatPrefabs.Clear();}
    private static void Queue(GameObject go, GameObject prefab, FsmStateAction action, GameObject source=null) {
        if(go==null || prefab==null || CoopContext.Resetting)return;
        var key=prefab.GetInstanceID();
        if(!CombatPrefabs.TryGetValue(key,out var combat))CombatPrefabs[key]=combat=EntityRegistry.IsHazard(prefab)||EntityRegistry.TryGetSpawnEntry(prefab,out _);
        if(!combat)return;
        if(Pending.Count>=2048)return;
        Pending[go.GetInstanceID()]=new EntitySpawnDetails{GameObject=go,Prefab=prefab,Action=action,Source=source??action?.Fsm?.GameObject,Type=EntitySpawnType.FsmAction};
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
    internal static void Flush() {
        var work=new List<EntitySpawnDetails>(Pending.Values);Pending.Clear();
        foreach(var d in work)if(d.GameObject!=null) {
            try{EntityFsmActions.CallEntitySpawnEvent(d);}
            catch(Exception e){Hkmp.Logging.Logger.Error("Combat spawn registration failed: "+e);}
        }
    }
}
