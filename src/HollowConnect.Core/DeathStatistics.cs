using System;
using System.Collections.Generic;

namespace HollowConnect.Core;

// Stored by the Modding API in this save slot's separate modded JSON file.
public sealed class DeathStatistics {
    public Dictionary<string, uint> Deaths = new Dictionary<string, uint>(StringComparer.Ordinal);
    public ResourceClaims Resources = new ResourceClaims();
    // Authority history is separate from this player's actual receipts.
    // A guest's offer must survive a host save without marking the host paid.
    public ResourceClaims SharedResources = new ResourceClaims();
    // Local interactions are distinct from shared ownership: a remote ability
    // must not make an untouched local pickup skip its original cutscene.
    // Null migrates once from the loaded vanilla slot before any shared grant.
    public Dictionary<string, int> NativePickups;
    // The host is the authority for shared progress.  Keep the monotonic
    // source ledger beside the existing per-slot statistics so a server
    // restart or a later joiner sees the same grants.  This is intentionally a
    // dictionary of validated progress values rather than a copy of the
    // player's whole PlayerData object.
    public Dictionary<string, int> SharedProgress = new Dictionary<string, int>(StringComparer.Ordinal);
    public uint Get(string name) => name != null && Deaths.TryGetValue(name, out var count) ? count : 0;
    public bool Merge(string name, uint count) {
        if (string.IsNullOrWhiteSpace(name) || name.Length > 128 || count > int.MaxValue ||
            Deaths.TryGetValue(name, out var old) && count <= old) return false;
        Deaths[name] = count; return true;
    }
    public uint Increment(string name) {
        var count = Get(name);
        if (count < int.MaxValue) Merge(name, ++count);
        return Get(name);
    }
    public DeathStatistics Copy() {
        var copy = new DeathStatistics();
        if (Deaths != null) foreach (var pair in Deaths) copy.Merge(pair.Key, pair.Value);
        copy.Resources = Resources?.Copy() ?? new ResourceClaims();
        copy.SharedResources = SharedResources?.Copy() ?? new ResourceClaims();
        if (NativePickups != null) {
            copy.NativePickups = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (var pair in NativePickups)
                if (ProgressCatalog.IsPickup(pair.Key) && ProgressCatalog.Limits.TryGetValue(pair.Key, out var limit) && pair.Value > 0 && pair.Value <= limit)
                    copy.NativePickups[pair.Key] = pair.Value;
        }
        // Older slots only had local receipts. They are safe to seed into the
        // authority history, but never infer a local award from that history.
        foreach (var id in copy.Resources.Claimed) copy.SharedResources.TryClaim(id, out _);
        if (SharedProgress != null) foreach (var pair in SharedProgress)
            if (ProgressCatalog.Limits.TryGetValue(pair.Key, out var limit) && pair.Value >= 0 && pair.Value <= limit)
                copy.SharedProgress[pair.Key] = pair.Value;
        return copy;
    }
}
