using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace Hkmp.Game.Client.Entity;

// Scene IDs must not depend on which other enemies a save happened to load.
// Dynamic IDs occupy a separate namespace and are never reused, including pools.
internal static class EntityIdentity {
    internal static ulong Hash(string key) {
        unchecked {
            ulong hash = 14695981039346656037UL;
            foreach (var c in key) { hash ^= c; hash *= 1099511628211UL; }
            return (hash & 0x7fffffffffffffffUL) | 1UL;
        }
    }
    internal static ulong Dynamic() => Hash(Guid.NewGuid().ToString("N")) | 0x8000000000000000UL;
    internal static ulong Static(GameObject go, EntityType type) {
        // Reconnecting does not reload the native scene. FSMs may have moved
        // an existing child to scene root while its replica stayed parented.
        // Its first registration identity belongs to this native lifetime,
        // not to its mutable current hierarchy or a connection's lookup table.
        var anchor = go.GetComponent<StaticEntityIdentity>();
        if (anchor == null) anchor = go.AddComponent<StaticEntityIdentity>();
        if (anchor.SourceInstance != go.GetInstanceID() || anchor.SceneHandle != go.scene.handle || anchor.Type != type) {
            // Unity Instantiate may copy a metadata component. A different
            // native object/scene must never inherit the source object's ID.
            anchor.SourceInstance = go.GetInstanceID();
            anchor.SceneHandle = go.scene.handle;
            anchor.Type = type;
            anchor.Key = Key(go, type);
        }
        return Hash(anchor.Key);
    }
    internal static string Path(Transform t, Transform root = null) {
        var parts = new List<string>();
        while (t != null && t != root) {
            // Only identically named siblings affect the ordinal. A different
            // optional object must not shift all subsequent entity identities.
            var ordinal = 0;
            if (t.parent != null) {
                for (var i = 0; i < t.GetSiblingIndex(); i++)
                    if (t.parent.GetChild(i).name == t.name) ordinal++;
            } else {
                foreach (var go in t.gameObject.scene.GetRootGameObjects()) {
                    if (go.transform == t) break;
                    if (go.name == t.name) ordinal++;
                }
            }
            parts.Add(Uri.EscapeDataString(t.name) + "#" + ordinal); t = t.parent;
        }
        parts.Reverse(); return string.Join("/", parts);
    }
    internal static string Key(GameObject go, EntityType type) =>
        go.scene.name + "/" + Path(go.transform) + ":" + type + ":" +
        string.Join("|", go.GetComponents<PlayMakerFSM>().Select(f => f.FsmName).OrderBy(n => n, StringComparer.Ordinal));
}

// Metadata only: no Update, global object table or saved player data. Unity
// removes it with the native GameObject, but it survives a network disconnect.
internal sealed class StaticEntityIdentity : MonoBehaviour {
    [NonSerialized] internal int SourceInstance;
    [NonSerialized] internal int SceneHandle;
    [NonSerialized] internal EntityType Type;
    [NonSerialized] internal string Key;
}
