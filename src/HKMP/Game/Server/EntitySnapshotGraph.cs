using System.Collections.Generic;
using System.Linq;

namespace Hkmp.Game.Server;

internal static class EntitySnapshotGraph {
    internal static IEnumerable<KeyValuePair<ServerEntityKey, ServerEntityData>> Select(
        IEnumerable<KeyValuePair<ServerEntityKey, ServerEntityData>> entities, string scene) {
        var local = entities.Where(p => p.Key.Scene == scene).ToDictionary(p => p.Key.EntityId);
        var wanted = new HashSet<ulong>(local.Where(p => !p.Value.Value.Retired).Select(p => p.Key));
        var queue = new Queue<ulong>(wanted);
        while (queue.Count != 0) {
            var data = local[queue.Dequeue()].Value;
            if (data.Spawned && data.Spawn != null && local.ContainsKey(data.Spawn.SourceId) && wanted.Add(data.Spawn.SourceId)) queue.Enqueue(data.Spawn.SourceId);
        }
        return wanted.Select(id => local[id]).ToArray();
    }
}
