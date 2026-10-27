using System;
using System.Linq;

namespace HollowConnect.Core;

// Reconcile non-consumable totals against validated source receipts. Ore is
// deliberately excluded: a receipt must never refund a personal expenditure.
public static class ResourceReconciliation {
    // A complete vanilla collection identifies every source without guessing
    // which subset a partial balance came from. Godseeker/legacy saves can
    // have these totals with none of the story pickup flags. Import as already
    // awarded locally, so spent ore is advertised to peers but never refunded.
    public static int ImportCompletedCollections(ResourceClaims receipts, int slots, int maskPieces,
        int vesselPieces, int ore, int nailUpgrades) {
        if (receipts == null) return 0;
        var spentOre = nailUpgrades == 0 || nailUpgrades == 1 ? 0 :
            nailUpgrades == 2 ? 1 : nailUpgrades == 3 ? 3 : nailUpgrades == 4 ? 6 : -1;
        var allOre = spentOre >= 0 && ore >= 0 && ore <= 6 && ore + spentOre == 6;
        var imported = 0;
        foreach (var source in ResourceCatalog.Sources) {
            var complete = source.Kind == ResourceKind.CharmNotch ? slots == 11 :
                source.Kind == ResourceKind.MaskShard ? maskPieces == 16 :
                source.Kind == ResourceKind.VesselFragment ? vesselPieces == 9 : source.Kind == ResourceKind.PaleOre && allOre;
            if (complete && receipts.TryClaim(source.Id, out _)) imported++;
        }
        return imported;
    }

    public static ResourceSource NextRepair(ResourceClaims receipts, int slots, int maskPieces, int vesselPieces) {
        if (receipts == null) return null;
        foreach (var kind in new[] { ResourceKind.CharmNotch, ResourceKind.MaskShard, ResourceKind.VesselFragment }) {
            var sources = ResourceCatalog.Sources.Where(s => s.Kind == kind && receipts.Contains(s.Id)).ToArray();
            var actual = kind == ResourceKind.CharmNotch ? Math.Max(3, slots) - 3 :
                kind == ResourceKind.MaskShard ? Math.Max(0, maskPieces) : Math.Max(0, vesselPieces);
            if (actual < sources.Length) return sources[actual];
        }
        return null;
    }
}
