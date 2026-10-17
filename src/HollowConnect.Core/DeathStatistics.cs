using System;
using System.Collections.Generic;

namespace HollowConnect.Core;

// Stored by the Modding API in this save slot's separate modded JSON file.
public sealed class DeathStatistics {
    public Dictionary<string, uint> Deaths = new Dictionary<string, uint>(StringComparer.Ordinal);
    public ResourceClaims Resources = new ResourceClaims();
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
        return copy;
    }
}
