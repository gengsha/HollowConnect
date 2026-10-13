using System;
using System.Collections.Generic;

namespace HollowConnect.Core;

public static class ProgressCatalog {
    // Explicit permanent upgrades: transient states, currency, consumable keys,
    // health, map exploration and equipped charms must never enter this ledger.
    public static readonly Dictionary<string, int> Limits = Make();
    private static Dictionary<string, int> Make() {
        var result = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var key in new[] {
            "hasDash", "hasWalljump", "hasSuperDash", "hasShadowDash", "hasAcidArmour", "hasDoubleJump",
            "hasDreamNail", "hasDreamGate", "dreamNailUpgraded", "hasSpell", "hasNailArt", "hasCyclone",
            "hasDashSlash", "hasUpwardSlash", "hasAllNailArts", "hasLantern", "hasTramPass", "hasKingsBrand",
            "killedFalseKnight", "killedMantisLord", "killedMegaMossCharger", "giantBuzzerDefeated",
            "killedBigFly", "killedHornet", "hornet1Defeated", "hornetOutskirtsDefeated", "killedMageLord",
            "killedDungDefender", "killedInfectedKnight", "killedBlackKnight", "killedHiveKnight", "watcherChandelier",
            "killedTraitorLord", "killedFlukeMother", "collectorDefeated", "lurienDefeated", "monomonDefeated",
            "hegemolDefeated", "openedCityGate", "openedRuins1", "openedRuins2", "openedDeepnest",
            "openedRoyalGardens", "openedRestingGrounds", "openedGreenpath", "openedFungalWastes",
            "openedPalaceGrounds", "openedCrossroads", "openedWaterwaysManhole", "openedTownBuilding",
            "falseKnightDefeated", "falseKnightDreamDefeated", "mawlekDefeated", "giantFlyDefeated",
            "defeatedMantisLords", "mageLordDefeated", "megaMossChargerDefeated", "flukeMotherDefeated",
            "maskBrokenLurien", "maskBrokenMonomon", "maskBrokenHegemol", "openedBlackEggDoor",
            "openedMageDoor", "openedLoveDoor", "openedTramLower", "openedTramRestingGrounds"
        }) result[key] = 1;
        for (var i = 1; i <= 40; i++) result["gotCharm_" + i] = 1;
        result["fireballLevel"] = result["quakeLevel"] = result["screamLevel"] = 2;
        result["nailSmithUpgrades"] = 4;
        return result;
    }
    public static bool IsBoolean(string key) => Limits.TryGetValue(key, out var max) && max == 1;
}

public sealed class ProgressLedger {
    private readonly Dictionary<string, int> values = new Dictionary<string, int>(StringComparer.Ordinal);
    public IReadOnlyDictionary<string, int> Values => values;
    public bool Merge(string key, int value) {
        if (key == null || !ProgressCatalog.Limits.TryGetValue(key, out var max) || value < 0 || value > max) return false;
        values.TryGetValue(key, out var old);
        if (value <= old) return false;
        values[key] = value;
        return true;
    }
    public int Get(string key) => values.TryGetValue(key, out var value) ? value : 0;
}
