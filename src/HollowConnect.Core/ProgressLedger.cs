using System;
using System.Collections.Generic;

namespace HollowConnect.Core;

public static class ProgressCatalog {
    public static bool IsMapPurchase(string key) => key != null && mapPurchases.Contains(key);
    private static readonly HashSet<string> mapPurchases = new HashSet<string>(StringComparer.Ordinal) {
        "hasMap", "hasQuill", "mapCrossroads", "mapGreenpath", "mapFogCanyon", "mapRoyalGardens",
        "mapFungalWastes", "mapCity", "mapWaterways", "mapMines", "mapDeepnest", "mapCliffs",
        "mapOutskirts", "mapRestingGrounds", "mapAbyss"
    };
    public static bool IsPickup(string key) => key != null && (IsCharm(key) || pickupKeys.Contains(key));
    private static readonly HashSet<string> pickupKeys = new HashSet<string>(StringComparer.Ordinal) {
        "hasDash", "hasWalljump", "hasSuperDash", "hasShadowDash", "hasAcidArmour", "hasDoubleJump",
        "hasDreamNail", "hasDreamGate", "dreamNailUpgraded", "hasSpell", "hasCharm", "hasNailArt", "hasCyclone",
        "hasDashSlash", "hasUpwardSlash", "hasAllNailArts", "hasLantern", "hasTramPass", "hasKingsBrand",
        "fireballLevel", "quakeLevel", "screamLevel", "royalCharmState", "gotQueenFragment", "gotKingFragment"
    };
    // Explicit permanent upgrades: transient states, currency, consumable keys,
    // health, map exploration and equipped charms must never enter this ledger.
    public static readonly Dictionary<string, int> Limits = Make();
    private static Dictionary<string, int> Make() {
        var result = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var key in new[] {
            "hasDash", "hasWalljump", "hasSuperDash", "hasShadowDash", "hasAcidArmour", "hasDoubleJump",
            "hasDreamNail", "hasDreamGate", "dreamNailUpgraded", "hasSpell", "hasCharm", "hasNailArt", "hasCyclone",
            "hasDashSlash", "hasUpwardSlash", "hasAllNailArts", "hasLantern", "hasTramPass", "hasKingsBrand",
            "killedFalseKnight", "killedMantisLord", "killedMegaMossCharger", "giantBuzzerDefeated",
            "killedBigFly", "killedHornet", "hornet1Defeated", "hornetOutskirtsDefeated", "killedMageLord",
            // Dung Defender has two native story flags in addition to the
            // legacy kill flag. SceneAdditiveLoadConditional uses these flags
            // to choose the live/defeated dream encounter when Waterways_05
            // is entered again.
            "killedDungDefender", "defeatedDungDefender", "dungDefenderEncounterReady",
            "killedInfectedKnight", "killedBlackKnight", "killedHiveKnight", "watcherChandelier",
            "killedTraitorLord", "killedFlukeMother", "collectorDefeated", "lurienDefeated", "monomonDefeated",
            "hegemolDefeated", "openedCityGate", "openedRuins1", "openedRuins2", "openedDeepnest",
            "openedRoyalGardens", "openedRestingGrounds", "openedGreenpath", "openedFungalWastes",
            "openedPalaceGrounds", "openedCrossroads", "openedTown", "openedStagNest", "openedHiddenStation",
            "openedTownBuilding",
            "falseKnightDefeated", "falseKnightDreamDefeated", "mawlekDefeated", "giantFlyDefeated",
            // One-time dream victories can repair an older participant's missing
            // completion record. Essence claims and repeatable dream win counts
            // remain personal and must not enter the permanent-progress union.
            "infectedKnightDreamDefeated", "mageLordDreamDefeated",
            "defeatedMantisLords", "mageLordDefeated", "megaMossChargerDefeated", "flukeMotherDefeated",
            "maskBrokenLurien", "maskBrokenMonomon", "maskBrokenHegemol", "openedBlackEggDoor",
            "openedMageDoor", "openedLoveDoor", "openedTramLower", "openedTramRestingGrounds"
        }) result[key] = 1;
        foreach (var key in mapPurchases) result[key] = 1;
        for (var i = 1; i <= 40; i++) result["gotCharm_" + i] = 1;
        result["fireballLevel"] = result["quakeLevel"] = result["screamLevel"] = 2;
        result["royalCharmState"] = 4;
        result["gotQueenFragment"] = result["gotKingFragment"] = 1;
        result["grimmFlames1"] = result["grimmFlames2"] = 7;
        result["grimmFlames3"] = 15;
        return result;
    }
    public static bool IsBoolean(string key) => Limits.TryGetValue(key, out var max) && max == 1;
    public static bool IsCharm(string key) => key != null && key.StartsWith("gotCharm_", StringComparison.Ordinal) && Limits.ContainsKey(key);
    public static bool IsCharmProgress(string key) => IsCharm(key) || key == "hasCharm" || key == "royalCharmState" ||
        key == "gotQueenFragment" || key == "gotKingFragment" || GrimmFlames.IsKey(key);
    // The two Kingsoul halves are alternatives 1/2, then complete=3 and
    // Void Heart=4. Two different halves must combine into the whole charm.
    public static int Combine(string key, int first, int second) => GrimmFlames.IsKey(key) ? first | second : key == "royalCharmState" && first < 4 && second < 4
        ? first | second : Math.Max(first, second);
    // Match vanilla CountCharms: charm 36 only counts once both halves exist.
    public static int CharmCount(Func<string, bool> flag, int royalState) {
        var count = 0;
        for (var i = 1; i <= 40; i++) if (i == 36 ? royalState > 2 : flag("gotCharm_" + i)) count++;
        return count;
    }
    public static bool CanShare(string key, bool shared, bool charms) => key != null && shared && Limits.ContainsKey(key) &&
        (charms || !IsCharmProgress(key));
}

public sealed class ProgressLedger {
    private readonly Dictionary<string, int> values = new Dictionary<string, int>(StringComparer.Ordinal);
    public IReadOnlyDictionary<string, int> Values => values;
    public bool Merge(string key, int value) {
        if (key == null || !ProgressCatalog.Limits.TryGetValue(key, out var max) || value < 0 || value > max) return false;
        values.TryGetValue(key, out var old);
        var merged = ProgressCatalog.Combine(key, old, value);
        if (merged == old) return false;
        values[key] = merged;
        return true;
    }
    public int Get(string key) => values.TryGetValue(key, out var value) ? value : 0;
    public ProgressLedger Copy() {
        var copy = new ProgressLedger();
        foreach (var pair in values) copy.Merge(pair.Key, pair.Value);
        return copy;
    }
    public void Import(IEnumerable<KeyValuePair<string, int>> entries) {
        if (entries == null) return;
        foreach (var pair in entries) Merge(pair.Key, pair.Value);
    }
}
