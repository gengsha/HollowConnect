using System;
using System.Collections.Generic;

namespace HollowConnect.Core;

public enum ResourceKind { CharmNotch, MaskShard, VesselFragment, PaleOre }

// Identification only. A native flag can be set before the pickup animation
// actually grants a resource; adapters must observe the native grant endpoint.
public sealed class ResourceSource {
    public string Id { get; }
    public ResourceKind Kind { get; }
    public string Scene { get; }
    public string PersistentId { get; }
    public string PlayerFlag { get; }
    internal ResourceSource(string id, ResourceKind kind, string scene, string persistentId, string playerFlag) {
        Id = id; Kind = kind; Scene = scene; PersistentId = persistentId; PlayerFlag = playerFlag;
    }
}

// Fixed native sources, rather than resource balances. Identified from this
// game's scene assets and ShopItemStats; no transient/currency fields belong here.
public static class ResourceCatalog {
    private static readonly Dictionary<string, ResourceSource> sources = Make();
    private static readonly HashSet<string> personalFields = new HashSet<string>(StringComparer.Ordinal) {
        "ore", "charmSlots", "heartPieces", "maxHealth", "maxHealthBase", "heartPieceCollected", "heartPieceMax",
        "vesselFragments", "MPReserveMax", "vesselFragmentCollected", "vesselFragmentMax",
        "nailSmithUpgrades", "nailDamage", "honedNail"
    };
    public static IEnumerable<ResourceSource> Sources => sources.Values;
    public static bool PreservesPlayerData(string field) => field != null &&
        (personalFields.Contains(field) || TryPlayerFlag(field, out _));
    public static bool PreservesPersistent(string scene, string id) {
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
            if (candidate.PlayerFlag == flag) { source = candidate; return true; }
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
