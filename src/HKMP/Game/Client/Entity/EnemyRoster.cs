using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Newtonsoft.Json;
using UnityEngine;

namespace Hkmp.Game.Client.Entity;

internal static class EnemyRoster {
    private static readonly FieldInfo Tracked = typeof(TrackSpawnedEnemies).GetField("trackedEnemies", BindingFlags.Instance | BindingFlags.NonPublic);
    private sealed class Entry { public string Tracker; public string[] Enemies; }
    internal static string Capture() => JsonConvert.SerializeObject(UnityEngine.Object.FindObjectsOfType<TrackSpawnedEnemies>(true)
        .Where(t => t.gameObject.scene.name == CoopContext.LocalScene || t.gameObject.scene.name.StartsWith(CoopContext.LocalScene + "_"))
        .Select(t => new Entry { Tracker = EntityReferences.Capture(t.gameObject),
            Enemies = ((List<HealthManager>)Tracked.GetValue(t)).Select(h => h == null ? "" : EntityReferences.Capture(h.gameObject)).ToArray() })
        .OrderBy(e => e.Tracker).ToArray());
    internal static bool Apply(string json) {
        if (json.Length > 32768) return true;
        var entries = JsonConvert.DeserializeObject<Entry[]>(json);
        if (entries == null || entries.Length > 128) return true;
        var complete = true;
        foreach (var entry in entries) {
            if (entry?.Enemies == null || entry.Enemies.Length > 1024) continue;
            var tracker = EntityReferences.Resolve(entry.Tracker, false)?.GetComponent<TrackSpawnedEnemies>();
            if (tracker == null) { complete = false; continue; }
            var resolved = entry.Enemies.Select(k => EntityReferences.Resolve(k, false)?.GetComponent<HealthManager>()).ToList();
            if (resolved.Where((h, i) => h == null && entry.Enemies[i].Length != 0).Any()) { complete = false; continue; }
            Tracked.SetValue(tracker, resolved);
        }
        return complete;
    }
}
