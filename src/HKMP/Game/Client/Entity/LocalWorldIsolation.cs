using Hkmp.Animation;
using UnityEngine;

namespace Hkmp.Game.Client.Entity;

// Classify before Entity construction: rejecting a replica later would already
// have stopped the native enemy or mechanism. Spawn inheritance requires a real network
// owner, never proximity to an arbitrary HealthManager in the same room.
internal static class LocalWorldIsolation {
    internal static bool Allows(EntityRegistryEntry entry, bool bossSpawn = false) => entry != null && entry.Networked &&
        !EntityRegistry.IsLocalOnlyBarrier(entry.Type) &&
        (!CoopContext.LocalWorldOnly || bossSpawn || entry.BossSupport || BossDefeatSignals.IsBoss(entry.Type));

    internal static bool IsRemote(GameObject source) => source != null && source.GetComponentInParent<RemoteVisualOpacity>() != null;
    internal static bool Blocks(GameObject target, GameObject source) => CoopContext.LocalWorldOnly && IsRemote(source) &&
        EntityReferences.Owner(target) == null;
}
