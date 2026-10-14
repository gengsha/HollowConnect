using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Hkmp.Networking.Packet.Data;
using HutongGames.PlayMaker;
using UnityEngine;

namespace Hkmp.Game.Client.Entity;

// Use the actual source action and prefab, rather than a growing boss-specific
// list of guessed FSM component indices. Nothing executes on the replica here.
internal static class SpawnDescriptor {
    private static readonly Dictionary<Type, FieldInfo[]> PrefabFields = new();
    private static readonly Dictionary<(string, EntityType), GameObject> ResourcePrefabs = new();
    internal static void Clear() => ResourcePrefabs.Clear();
    internal static IEnumerable<GameObject> Prefabs(FsmStateAction action) {
        if (action == null) yield break;
        var type = action.GetType();
        if (!PrefabFields.TryGetValue(type, out var fields))
            PrefabFields[type] = fields = type.GetFields(BindingFlags.Instance | BindingFlags.Public)
                .Where(f => f.Name == "gameObject" || f.Name.IndexOf("prefab", StringComparison.OrdinalIgnoreCase) >= 0).ToArray();
        foreach (var field in fields) {
            var value = field.GetValue(action);
            var go = value as GameObject ?? (value as FsmGameObject)?.Value;
            if (go != null) yield return go;
        }
    }
    internal static string PrefabName(FsmStateAction action, EntityType type) =>
        Prefabs(action).FirstOrDefault(p => type == EntityType.DynamicHazard ? EntityRegistry.IsHazard(p) : EntityRegistry.TryGetSpawnEntry(p, out var e) && e.Type == type)?.name ?? "";
    internal static GameObject Create(EntitySpawn data, Entity source) {
        var fsms = source.Object.Host != null ? source.Object.Host.GetComponentsInChildren<PlayMakerFSM>(true) : Array.Empty<PlayMakerFSM>();
        var owner=EntityReferences.Walk(source.Object.Host,data.FsmPath);
        var fsm = owner==null?null:owner.GetComponents<PlayMakerFSM>().FirstOrDefault(f => f.FsmName == data.Fsm);
        var state = fsm?.FsmStates.FirstOrDefault(s => s.Name == data.State);
        var action = state != null && data.ActionIndex >= 0 && data.ActionIndex < state.Actions.Length ? state.Actions[data.ActionIndex] : null;
        bool Match(GameObject p) => p != null && (data.Prefab.Length == 0 || p.name == data.Prefab) &&
            (data.SpawnedType == EntityType.DynamicHazard ? EntityRegistry.IsHazard(p) : EntityRegistry.TryGetSpawnEntry(p, out var entry) && entry.Type == data.SpawnedType);
        var candidates = Prefabs(action).Where(Match).Distinct().ToArray();
        if (candidates.Length != 1) {
            candidates = fsms.SelectMany(f => f.FsmStates).SelectMany(s => s.Actions).SelectMany(Prefabs).Where(Match).Distinct().ToArray();
        }
        if(candidates.Length!=1 && source.Object.Host!=null) {
            var spawner=source.Object.Host.GetComponent<EnemySpawner>();
            if(spawner!=null && Match(spawner.enemyPrefab))candidates=new[]{spawner.enemyPrefab};
        }
        if(candidates.Length!=1 && source.Object.Host!=null) {
            candidates=source.Object.Host.GetComponentsInChildren<SpawnJarControl>(true)
                .Select(j=>Modding.ReflectionHelper.GetField<SpawnJarControl,GameObject>(j,"enemyToSpawn")).Where(Match).Distinct().ToArray();
        }
        if (candidates.Length != 1 && data.Prefab.Length != 0) {
            var key = (data.Prefab, data.SpawnedType);
            if (ResourcePrefabs.TryGetValue(key, out var cached) && cached != null && !cached.scene.IsValid() && Match(cached))
                candidates = new[] { cached };
            else {
                candidates = Resources.FindObjectsOfTypeAll<GameObject>().Where(p => !p.scene.IsValid() && Match(p)).ToArray();
                // Pooled hazards can arrive in large bursts. Reuse only a
                // unique native asset; misses and ambiguity still retry/fail.
                if (candidates.Length == 1) ResourcePrefabs[key] = candidates[0];
                else ResourcePrefabs.Remove(key);
            }
        }
        // Ambiguity is an explicit failure; spawning the wrong hazard is worse.
        if (candidates.Length != 1) return null;
        var staging=new GameObject("HollowConnect spawn staging");staging.SetActive(false);
        var go=UnityEngine.Object.Instantiate(candidates[0],staging.transform,false);
        go.SetActive(false);go.transform.SetParent(null,true);
        go.transform.position=new Vector3(data.X,data.Y,data.Z);
        UnityEngine.SceneManagement.SceneManager.MoveGameObjectToScene(go,UnityEngine.SceneManagement.SceneManager.GetActiveScene());
        UnityEngine.Object.Destroy(staging);return go;
    }
    internal static void Bind(EntitySpawn data, Entity source, Entity spawned) {
        foreach (var client in new[] { false, true }) {
            var root = client ? source.Object.Client : source.Object.Host;
            if (root == null) continue;
            var owner=EntityReferences.Walk(root,data.FsmPath);
            var fsm = owner==null?null:owner.GetComponents<PlayMakerFSM>().FirstOrDefault(f => f.FsmName == data.Fsm);
            var state = fsm?.FsmStates.FirstOrDefault(s => s.Name == data.State);
            if (state == null || data.ActionIndex < 0 || data.ActionIndex >= state.Actions.Length) continue;
            var field = state.Actions[data.ActionIndex].GetType().GetField("storeObject");
            if (field?.GetValue(state.Actions[data.ActionIndex]) is FsmGameObject store && !store.IsNone)
                store.Value = client ? spawned.Object.Client : spawned.Object.Host;
        }
    }
}
