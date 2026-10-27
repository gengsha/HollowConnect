using System;
using System.Collections.Generic;

namespace HollowConnect.Core;

// Each native flame location is a receipt, scoped to its Grimmchild round.
// Counts cannot be merged with Max (different locations) or addition (replays).
public static class GrimmFlames {
    private static readonly string[][] locations = {
        new[] { "Mines_10", "Ruins1_28", "Fungus1_10" },
        new[] { "Tutorial_01", "RestingGrounds_06", "Deepnest_East_03" },
        new[] { "Fungus2_30", "Abyss_02", "Hive_03", "Deepnest_Spider_Town" }
    };
    public static string Key(int level) => level >= 1 && level <= 3 ? "grimmFlames" + level : null;
    public static int Level(string key) => key == "grimmFlames1" ? 1 : key == "grimmFlames2" ? 2 : key == "grimmFlames3" ? 3 : 0;
    public static bool IsKey(string key) => Level(key) != 0;
    public static IReadOnlyList<string> Locations(int level) => level >= 1 && level <= 3 ? locations[level - 1] : Array.Empty<string>();
    public static int Location(int level, string scene) {
        var group = Locations(level);
        for (var i = 0; i < group.Count; i++) if (group[i] == scene) return 1 << i;
        return 0;
    }
    public static int Evidence(int level, ICollection<string> scenes, bool brumm) {
        var result = 0; var group = Locations(level);
        for (var i = 0; i < group.Count; i++)
            if (scenes != null && scenes.Contains(group[i]) || level == 3 && i == 3 && brumm) result |= 1 << i;
        return result;
    }
    public static int Count(int receipts) {
        var count = 0;
        for (var bits = receipts & 15; bits != 0; bits >>= 1) count += bits & 1;
        return count;
    }
}
