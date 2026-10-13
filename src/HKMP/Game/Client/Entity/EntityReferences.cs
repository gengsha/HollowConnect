using System.Collections.Generic;
using System;
using System.Linq;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Hkmp.Game.Client.Entity;

internal static class EntityReferences {
    internal static Dictionary<ulong, Entity> Entities;
    private static readonly Dictionary<int, Entity> Owners = new();
    // Native FSMs detach children without changing the GameObject variable.
    // Its original entity-relative identity must survive that reparenting.
    internal sealed class Part {
        internal Entity Entity;
        internal string Path;
        internal GameObject Host, Client;
    }
    private static readonly Dictionary<int, Part> PartsByObject = new();
    private static readonly Dictionary<ulong, Dictionary<string, Part>> PartsByEntity = new();
    internal static void Register(Entity e) {
        if (e.Object.Host != null) Owners[e.Object.Host.GetInstanceID()] = e;
        if (e.Object.Client != null) Owners[e.Object.Client.GetInstanceID()] = e;
        var parts = new Dictionary<string, Part>();
        if (e.Object.Host != null && e.Object.Client != null) {
            foreach (var t in e.Object.Host.GetComponentsInChildren<Transform>(true)) {
                var path = EntityIdentity.Path(t, e.Object.Host.transform);
                var client = Walk(e.Object.Client, path);
                if (client == null) continue;
                var part = new Part { Entity = e, Path = path, Host = t.gameObject, Client = client };
                parts[path] = part;
                PartsByObject[t.gameObject.GetInstanceID()] = part;
                PartsByObject[client.GetInstanceID()] = part;
            }
        }
        PartsByEntity[e.Id] = parts;
    }
    internal static void Forget(Entity e) {
        if (!ReferenceEquals(e.Object.Host, null)) Owners.Remove(e.Object.Host.GetInstanceID());
        if (!ReferenceEquals(e.Object.Client, null)) Owners.Remove(e.Object.Client.GetInstanceID());
        if (PartsByEntity.TryGetValue(e.Id, out var parts)) {
            foreach (var part in parts.Values) {
                foreach (var go in new[] { part.Host, part.Client }) {
                    if (ReferenceEquals(go, null)) continue;
                    var key = go.GetInstanceID();
                    if (PartsByObject.TryGetValue(key, out var current) && current.Entity == e) PartsByObject.Remove(key);
                }
            }
            PartsByEntity.Remove(e.Id);
        }
    }
    internal static IEnumerable<Part> Parts(ulong id) {
        if (!PartsByEntity.TryGetValue(id, out var parts)) return Enumerable.Empty<Part>();
        // Room teardown deliberately removes the live entity table first.
        // The retained bindings must still be available for detached cleanup.
        var e = parts.Values.FirstOrDefault()?.Entity;
        if (e == null) return parts.Values;
        if (e.Object.Host != null && e.Object.Client != null) {
            foreach (var t in e.Object.Host.GetComponentsInChildren<Transform>(true)) {
                if (parts.Count >= 512) break;
                if (PartsByObject.ContainsKey(t.gameObject.GetInstanceID()) || Owner(t.gameObject) != e) continue;
                var path = EntityIdentity.Path(t, e.Object.Host.transform);
                if (parts.TryGetValue(path, out var previous) && previous.Host != null) continue;
                var client = Walk(e.Object.Client, path);
                if (client == null) continue;
                if (previous != null) {
                    foreach (var old in new[] { previous.Host, previous.Client })
                        if (!ReferenceEquals(old, null) && PartsByObject.TryGetValue(old.GetInstanceID(), out var binding) && binding == previous)
                            PartsByObject.Remove(old.GetInstanceID());
                }
                var part = new Part { Entity = e, Path = path, Host = t.gameObject, Client = client };
                parts[path] = part;PartsByObject[t.gameObject.GetInstanceID()] = part;PartsByObject[client.GetInstanceID()] = part;
            }
        }
        return parts.Values;
    }
    internal static void Clear() {Owners.Clear();PartsByObject.Clear();PartsByEntity.Clear();Entity.WaitingForEntrance.Clear();}
    internal static Entity Owner(GameObject go) {
        if (go == null) return null;
        for (var t = go.transform; t != null; t = t.parent) {
            if (Owners.TryGetValue(t.gameObject.GetInstanceID(), out var e)) return e;
            if (PartsByObject.TryGetValue(t.gameObject.GetInstanceID(), out var part)) return part.Entity;
        }
        return null;
    }
    internal static string Capture(GameObject go) {
        if (go == null) return "";
        if (!go.scene.IsValid()) return "@local"; // Serialized prefab, retain the local asset reference.
        if (HeroController.instance != null && go == HeroController.instance.gameObject) return "hero";
        if (PartsByObject.TryGetValue(go.GetInstanceID(), out var part))
            return "e:" + part.Entity.Id + (part.Path.Length == 0 ? "" : "/" + part.Path);
        var entity = Owner(go);
        if (entity != null) {
            var ancestor = entity.Object.Host != null && go.transform.IsChildOf(entity.Object.Host.transform) ? entity.Object.Host : entity.Object.Client;
            var path = EntityIdentity.Path(go.transform, ancestor.transform);
            return "e:" + entity.Id + (path.Length == 0 ? "" : "/" + path);
        }
        return "p:" + go.scene.name + "\n" + EntityIdentity.Path(go.transform);
    }
    internal static GameObject Resolve(string key, bool client) {
        if (string.IsNullOrEmpty(key)) return null;
        if (key == "hero") return HeroController.instance?.gameObject;
        if (key.StartsWith("e:")) {
            var slash = key.IndexOf('/');
            if (!ulong.TryParse(slash < 0 ? key.Substring(2) : key.Substring(2, slash - 2), out var id) || Entities == null || !Entities.TryGetValue(id, out var e)) return null;
            if (PartsByEntity.TryGetValue(id, out var parts) && parts.TryGetValue(slash < 0 ? "" : key.Substring(slash + 1), out var part))
                return client ? part.Client : part.Host;
            var root = client ? e.Object.Client : e.Object.Host;
            return slash < 0 ? root : Walk(root, key.Substring(slash + 1));
        }
        if (!key.StartsWith("p:")) return null;
        var split = key.IndexOf('\n'); if (split < 0) return null;
        var scene = UnityEngine.SceneManagement.SceneManager.GetSceneByName(key.Substring(2, split - 2));
        if (!scene.IsValid() || !scene.isLoaded) return null;
        // Walk the named path directly. Scanning every loaded object and
        // rebuilding each root's path is quadratic in a projectile-heavy room.
        var result = Walk(null, key.Substring(split + 1), scene);
        // Legacy/path references to a managed hierarchy must select the visible
        // replica's corresponding descendant, never the dormant native child.
        var owner = client ? Owner(result) : null;
        return owner == null ? result : Walk(owner.Object.Client, EntityIdentity.Path(result.transform, owner.Object.Host.transform));
    }
    internal static GameObject Walk(GameObject root, string path, Scene scene = default) {
        if (path.Length == 0) return root;
        var parts = path.Split('/');
        GameObject current = root;
        foreach (var part in parts) {
            var ordinalAt = part.LastIndexOf('#');
            if (ordinalAt < 0 || !int.TryParse(part.Substring(ordinalAt + 1), out var ordinal) || ordinal < 0) return null;
            var name = Uri.UnescapeDataString(part.Substring(0, ordinalAt));
            if (current == null && !scene.IsValid()) return null;
            IEnumerable<GameObject> candidates = current == null ? scene.GetRootGameObjects() :
                Enumerable.Range(0, current.transform.childCount).Select(i => current.transform.GetChild(i).gameObject);
            current = candidates.Where(o => o.name == name).Skip(ordinal).FirstOrDefault();
            if (current == null) return null;
        }
        return current;
    }
    // Disabling the replica root does not disable children already detached by
    // a networked SetParent action. Retire them before the native FSM resumes.
    internal static void DisableDetachedParts(Entity e, bool client, bool destroy = false) {
        var root = client ? e.Object.Client : e.Object.Host;
        foreach (var part in Parts(e.Id).ToArray()) {
            var go = client ? part.Client : part.Host;
            if (go == null || go == root || Owner(go) != e || (root != null && go.transform.IsChildOf(root.transform))) continue;
            go.SetActive(false);
            if (destroy) UnityEngine.Object.Destroy(go);
        }
    }
}
