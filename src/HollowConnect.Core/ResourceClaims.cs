using System;
using System.Collections.Generic;

namespace HollowConnect.Core;

public enum ResourceKind { CharmNotch, MaskShard, VesselFragment, PaleOre, SimpleKey, Grub, UniqueKey, DreamEssence }

// Identification only. A native flag can be set before the pickup animation
// actually grants a resource; adapters must observe the native grant endpoint.
public sealed class ResourceSource {
    public string Id { get; }
    public ResourceKind Kind { get; }
    public string Scene { get; }
    public string PersistentId { get; }
    public string PlayerFlag { get; }
    public string InventoryFlag { get; }
    public string SpentFlag { get; }
    public string ClaimInt { get; }
    public int Amount { get; }
    internal ResourceSource(string id, ResourceKind kind, string scene, string persistentId, string playerFlag,
        string inventoryFlag = null, string spentFlag = null, string claimInt = null, int amount = 1) {
        Id = id; Kind = kind; Scene = scene; PersistentId = persistentId; PlayerFlag = playerFlag;
        InventoryFlag = inventoryFlag; SpentFlag = spentFlag; ClaimInt = claimInt; Amount = amount;
    }
}

// Fixed native sources, rather than resource balances. Identified from this
// game's scene assets and ShopItemStats; no transient/currency fields belong here.
public static class ResourceCatalog {
    private static readonly Dictionary<string, ResourceSource> sources = Make();
    private static readonly HashSet<string> personalFields = new HashSet<string>(StringComparer.Ordinal) {
        "ore", "charmSlots", "heartPieces", "maxHealth", "maxHealthBase", "heartPieceCollected", "heartPieceMax",
        "vesselFragments", "MPReserveMax", "vesselFragmentCollected", "vesselFragmentMax",
        "nailSmithUpgrades", "nailDamage", "honedNail", "simpleKeys", "grubsCollected", "scenesGrubRescued", "dreamOrbs",
        "hasCityKey", "hasWhiteKey", "hasLoveKey", "hasSlykey", "gaveSlykey", "usedWhiteKey"
    };
    public static bool IsPersonalKeyDoor(string field) => field == "jijiDoorUnlocked" || field == "bathHouseOpened" ||
        field == "openedWaterwaysManhole" || field == "godseekerUnlocked" || field == "openedCityGate" ||
        field == "openedMageDoor" || field == "openedMageDoor_v2" || field == "openedLoveDoor";
    public static IEnumerable<ResourceSource> Sources => sources.Values;
    public static bool PreservesPlayerData(string field) => field != null &&
        (personalFields.Contains(field) || IsPersonalKeyDoor(field) || TryPlayerFlag(field, out _) || IsEssenceClaim(field));
    private static bool IsEssenceClaim(string field) {
        foreach (var source in sources.Values) if (source.ClaimInt == field) return true;
        return false;
    }
    public static bool PreservesPersistent(string scene, string id) {
        if (scene == "Ruins2_11" && id == "Grub Bottle") return true;
        foreach (var source in sources.Values)
            if (source.PersistentId != null && source.Scene == scene && source.PersistentId == id) return true;
        return false;
    }
    public static bool TryGet(string id, out ResourceSource source) {
        source = null;
        return id != null && sources.TryGetValue(id, out source);
    }
    public static bool TryPersistent(string scene, string persistentId, ResourceKind kind, out ResourceSource source) {
        foreach (var candidate in sources.Values) {
            if (candidate.Kind == kind && candidate.Scene == scene && candidate.PersistentId != null && candidate.PersistentId == persistentId) {
                source = candidate; return true;
            }
        }
        source = null; return false;
    }
    public static bool TryPlayerFlag(string flag, out ResourceSource source) {
        if (!string.IsNullOrEmpty(flag)) foreach (var candidate in sources.Values) {
            if (candidate.PlayerFlag == flag || candidate.InventoryFlag == flag) { source = candidate; return true; }
        }
        source = null; return false;
    }
    private static Dictionary<string, ResourceSource> Make() {
        var result = new Dictionary<string, ResourceSource>(StringComparer.Ordinal);
        void Add(string id, ResourceKind kind, string scene, string persistentId = null, string playerFlag = null) =>
            result.Add(id, new ResourceSource(id, kind, scene, persistentId, playerFlag));
        void Pickup(string prefix, ResourceKind kind, string scene, string nativeId) =>
            Add(prefix + ":" + scene + "/" + nativeId, kind, scene, nativeId);
        for (var i = 1; i <= 4; i++) Add("notch:salubraNotch" + i, ResourceKind.CharmNotch, "Room_Charm_Shop", playerFlag: "salubraNotch" + i);
        Add("notch:notchShroomOgres", ResourceKind.CharmNotch, "Fungus2_05", "Shiny Item", "notchShroomOgres");
        Add("notch:notchFogCanyon", ResourceKind.CharmNotch, "Fungus3_28", "Shiny Item", "notchFogCanyon");
        Add("notch:gotGrimmNotch", ResourceKind.CharmNotch, "Grimm_Main_Tent", "Shiny Item", "gotGrimmNotch");
        Add("notch:colosseumBronze", ResourceKind.CharmNotch, "Room_Colosseum_Bronze", "Shiny Item", "colosseumBronzeCompleted");

        for (var i = 1; i <= 4; i++) Add("mask:slyShellFrag" + i, ResourceKind.MaskShard, "Room_shop", playerFlag: "slyShellFrag" + i);
        foreach (var scene in new[] { "Room_Mansion", "Room_Bretta", "Crossroads_09", "Crossroads_13", "Crossroads_38",
            "Fungus1_36", "Fungus2_01", "Fungus2_25", "Mines_32", "Waterways_04b", "Hive_04" })
            Pickup("mask", ResourceKind.MaskShard, scene, "Heart Piece");
        Add("mask:dreamReward7", ResourceKind.MaskShard, "RestingGrounds_07", playerFlag: "dreamReward7");

        for (var i = 1; i <= 2; i++) Add("vessel:slyVesselFrag" + i, ResourceKind.VesselFragment, "Room_shop", playerFlag: "slyVesselFrag" + i);
        foreach (var scene in new[] { "Crossroads_37", "Ruins2_09", "Fungus1_13", "Deepnest_38", "Abyss_04" })
            Pickup("vessel", ResourceKind.VesselFragment, scene, "Vessel Fragment");
        Add("vessel:vesselFragStagNest", ResourceKind.VesselFragment, "Cliffs_03", playerFlag: "vesselFragStagNest");
        Add("vessel:dreamReward5", ResourceKind.VesselFragment, "RestingGrounds_07", playerFlag: "dreamReward5");

        Pickup("ore", ResourceKind.PaleOre, "Crossroads_38", "Shiny Item Ore");
        foreach (var scene in new[] { "Mines_34", "Abyss_17", "Deepnest_32" }) Pickup("ore", ResourceKind.PaleOre, scene, "Shiny Item Stand");
        Add("ore:dreamReward3", ResourceKind.PaleOre, "RestingGrounds_07", playerFlag: "dreamReward3");
        Add("ore:colosseumSilver", ResourceKind.PaleOre, "Room_Colosseum_Silver", "Shiny Item", "colosseumSilverCompleted");
        Add("key:slySimpleKey", ResourceKind.SimpleKey, "Room_shop", playerFlag: "slySimpleKey");
        Pickup("key", ResourceKind.SimpleKey, "Ruins1_17", "Shiny Item");
        Pickup("key", ResourceKind.SimpleKey, "Abyss_20", "Shiny Item Stand");
        Add("key:gotLurkerKey", ResourceKind.SimpleKey, "GG_Lurker", playerFlag: "gotLurkerKey");
        foreach (var pair in new[] { ("hasCityKey", "openedCityGate", "Crossroads_10"), ("hasWhiteKey", "usedWhiteKey", "Room_shop"),
            ("hasLoveKey", "openedLoveDoor", "Fungus3_39"), ("hasSlykey", "gaveSlykey", "Mines_11") })
            result.Add("key:" + pair.Item1, new ResourceSource("key:" + pair.Item1, ResourceKind.UniqueKey, pair.Item3,
                pair.Item1 == "hasWhiteKey" ? null : pair.Item1 == "hasCityKey" ? "Shiny Item" : "Shiny Item Stand", null,
                inventoryFlag: pair.Item1, spentFlag: pair.Item2));
        foreach (var reward in new[] {
            ("RestingGrounds_02", "xeroDefeated", 100), ("Cliffs_02", "aladarSlugDefeated", 100),
            ("Fungus2_32", "elderHuDefeated", 100), ("Fungus3_40", "mumCaterpillarDefeated", 150),
            ("Fungus1_35", "noEyesDefeated", 200), ("Deepnest_40", "galienDefeated", 200),
            ("Deepnest_East_10", "markothDefeated", 250) })
            result.Add("essence:" + reward.Item2, new ResourceSource("essence:" + reward.Item2, ResourceKind.DreamEssence,
                reward.Item1, null, null, claimInt: reward.Item2, amount: reward.Item3));
        foreach (var reward in new[] {
            ("Crossroads_10", "falseKnightOrbsCollected", 300), ("Ruins1_24", "mageLordOrbsCollected", 300),
            ("Abyss_19", "infectedKnightOrbsCollected", 400), ("Waterways_15", "whiteDefenderOrbsCollected", 300),
            ("Room_Bretta_Basement", "greyPrinceOrbsCollected", 300) })
            result.Add("essence:" + reward.Item2, new ResourceSource("essence:" + reward.Item2, ResourceKind.DreamEssence,
                reward.Item1, null, reward.Item2, amount: reward.Item3));
        foreach (var scene in new[] { "Crossroads_03", "Fungus1_21", "Crossroads_31", "Crossroads_48", "Crossroads_05",
            "Ruins1_32", "Mines_04", "Mines_03", "Mines_19", "Mines_31", "Mines_35", "Crossroads_35", "Waterways_04",
            "Waterways_13", "Fungus3_47", "Ruins2_03", "Fungus2_18", "Fungus2_20", "Deepnest_03", "Deepnest_39",
            "Deepnest_Spider_Town", "Fungus1_07", "Fungus1_06", "Abyss_19", "Mines_16", "RestingGrounds_10", "Ruins2_07",
            "Deepnest_East_14", "Deepnest_East_11", "Fungus1_28", "Deepnest_31", "Fungus3_10", "Fungus3_48", "Abyss_17",
            "Fungus3_22", "Fungus1_13", "Ruins_House_01", "Hive_03", "Hive_04", "Mines_24", "Waterways_14", "Deepnest_36" })
            Pickup("grub", ResourceKind.Grub, scene, "Grub Bottle");
        Pickup("grub", ResourceKind.Grub, "Ruins1_05", "Grub Bottle (1)");
        // All three Collector bottles share vanilla's persistent ID. Give
        // each a stable left-to-right receipt instead of collapsing them.
        for (var i = 0; i < 3; i++) Add("grub:Ruins2_11/" + i, ResourceKind.Grub, "Ruins2_11");
        return result;
    }
}

// Persist receipts alongside Deaths in the existing local-settings JSON. This
// type contains no balances or upgrade levels: spending ore never deletes a
// receipt, and merging histories cannot refund it. Native amounts change only
// when TryClaim succeeds for that local save. Server union and each local save
// need separate instances; a server receipt is not itself proof of local grant.
public sealed class ResourceClaims {
    public HashSet<string> Claimed = new HashSet<string>(StringComparer.Ordinal);
    public bool Contains(string sourceId) => ResourceCatalog.TryGet(sourceId, out _) && Claimed != null && Claimed.Contains(sourceId);
    public bool TryClaim(string sourceId, out ResourceSource source) {
        if (!ResourceCatalog.TryGet(sourceId, out source)) return false;
        if (Claimed == null) Claimed = new HashSet<string>(StringComparer.Ordinal);
        return Claimed.Add(sourceId);
    }
    public ResourceClaims Copy() {
        var copy = new ResourceClaims();
        if (Claimed != null) foreach (var id in Claimed) copy.TryClaim(id, out _);
        return copy;
    }
}
