namespace Hkmp.Game.Client.Entity.Component;

// Distribute hierarchy refreshes over their existing interval. A late frame
// skips missed slots instead of catching up. Combat sampling keeps its native
// minimum 50 ms spacing, rather than running sooner to meet a phase boundary.
internal static class CombatSampling {
    internal static float Next(float now, ulong id, float interval) {
        var hash = unchecked(id * 11400714819323198485UL);
        hash ^= hash >> 32;
        var phase = (hash % 1009) * (double)interval / 1009;
        var slot = System.Math.Floor((now - phase) / interval) + 1;
        var result = (float)(slot * interval + phase);
        return result > now ? result : now + interval;
    }
}
