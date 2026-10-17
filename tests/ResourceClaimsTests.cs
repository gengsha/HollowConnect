using System;
using System.Collections.Generic;
using System.Linq;
using HollowConnect.Core;

internal static class ResourceClaimsTests {
    private static void Require(bool ok, string reason) { if (!ok) throw new Exception(reason); }
    internal static void Register(Action<string, Action> test) {
        test("resource catalog covers 8 notches, 16 mask shards, 9 vessel fragments and 6 ore sources", () => {
            var all = ResourceCatalog.Sources.ToArray();
            Require(all.Length == 39 && all.Select(s => s.Id).Distinct(StringComparer.Ordinal).Count() == 39, "native source identity collision");
            foreach (var pair in new[] { (ResourceKind.CharmNotch, 8), (ResourceKind.MaskShard, 16), (ResourceKind.VesselFragment, 9), (ResourceKind.PaleOre, 6) })
                Require(all.Count(s => s.Kind == pair.Item1) == pair.Item2, "incorrect source count for " + pair.Item1);
            Require(all.All(s => s.Id.Length <= 128 && s.Scene.Length <= 128), "source exceeds existing message bounds");
        });
        test("resource union gives each save one award for simultaneous claims from the same place", () => {
            var server = new ResourceClaims(); var host = new ResourceClaims(); var guest = new ResourceClaims();
            var source = "ore:Mines_34/Shiny Item Stand"; var hostOre = 0; var guestOre = 0;
            if (host.TryClaim(source, out _)) hostOre++;
            if (guest.TryClaim(source, out _)) guestOre++;
            var announcements = 0;
            foreach (var duplicate in new[] { source, source, source }) if (server.TryClaim(duplicate, out _)) announcements++;
            foreach (var duplicate in new[] { source, source, source }) {
                if (host.TryClaim(duplicate, out _)) hostOre++;
                if (guest.TryClaim(duplicate, out _)) guestOre++;
            }
            Require(announcements == 1 && hostOre == 1 && guestOre == 1, "concurrent native pickup and grant counted twice");
        });
        test("distinct resource sources survive reordered grants and fragment conversion", () => {
            var local = new ResourceClaims(); var shards = 0; var masks = 5;
            var sources = ResourceCatalog.Sources.Where(s => s.Kind == ResourceKind.MaskShard).Take(5).Select(s => s.Id).ToArray();
            foreach (var source in sources.Reverse().Concat(sources)) if (local.TryClaim(source, out _)) {
                if (++shards == 4) { shards = 0; masks++; }
            }
            Require(masks == 6 && shards == 1, "fragment reset lost progress or accepted a replay");
            var vessels = new ResourceClaims(); var fragments = 0; var reserve = 0;
            var vesselSources = ResourceCatalog.Sources.Where(s => s.Kind == ResourceKind.VesselFragment).Select(s => s.Id).ToArray();
            foreach (var source in vesselSources.Concat(vesselSources.Reverse())) if (vessels.TryClaim(source, out _)) {
                if (++fragments == 3) { fragments = 0; reserve += 33; }
            }
            Require(reserve == 99 && fragments == 0, "vessel conversion was not exactly once per source");
        });
        test("saved ore receipts prevent reconnect from refunding personally spent ore", () => {
            var server = new ResourceClaims(); var host = new ResourceClaims(); var guest = new ResourceClaims();
            var oreSources = ResourceCatalog.Sources.Where(s => s.Kind == ResourceKind.PaleOre).Take(3).Select(s => s.Id).ToArray();
            var hostOre = 0; var guestOre = 0; var hostNail = 1; var guestNail = 1;
            foreach (var source in oreSources) {
                server.TryClaim(source, out _);
                if (host.TryClaim(source, out _)) hostOre++;
                if (guest.TryClaim(source, out _)) guestOre++;
            }
            hostOre -= 1; hostNail++;
            Require(guestOre == 3 && guestNail == 1, "one player's upgrade changed the other's resources");
            var reopened = host.Copy();
            foreach (var source in server.Claimed) if (reopened.TryClaim(source, out _)) hostOre++;
            Require(hostOre == 2 && hostNail == 2, "reconnect refunded ore or changed nail level");
        });
        test("late entrant receives missing sources while imported native receipts remain spent", () => {
            var sources = ResourceCatalog.Sources.Where(s => s.Kind == ResourceKind.PaleOre).Take(3).Select(s => s.Id).ToArray();
            var local = new ResourceClaims(); local.TryClaim(sources[0], out _); // Already acquired, then spent offline.
            var ore = 0;
            foreach (var source in sources.Reverse().Concat(sources)) if (local.TryClaim(source, out _)) ore++;
            Require(ore == 2, "late join replayed an already consumed source");
        });
        test("source receipts reject aggregate balances and malformed persisted claims", () => {
            var claims = new ResourceClaims();
            foreach (var invalid in new[] { null, "", "ore", "nailSmithUpgrades", "charmSlots", "heartPieces", "vesselFragments", "ore:made-up-source" })
                Require(!claims.TryClaim(invalid, out _), "accepted resource balance or unknown source");
            claims.Claimed = new HashSet<string> { "ore:made-up-source", "ore:dreamReward3" };
            var copy = claims.Copy();
            Require(copy.Claimed.Count == 1 && copy.Contains("ore:dreamReward3"), "invalid persisted source survived validation");
            claims.Claimed = null;
            Require(claims.Copy().Claimed.Count == 0 && claims.TryClaim("ore:dreamReward3", out _), "missing old-save field was not initialized");
            copy.Claimed.Clear();
            Require(claims.Contains("ore:dreamReward3"), "copied receipt storage shares mutable state");
        });
        test("native reward lookup keeps unrelated scene pickups separate", () => {
            Require(ResourceCatalog.TryPersistent("Crossroads_38", "Shiny Item Ore", ResourceKind.PaleOre, out var ore) && ore.Kind == ResourceKind.PaleOre, "missing native ore identity");
            Require(ResourceCatalog.TryPersistent("Crossroads_38", "Heart Piece", ResourceKind.MaskShard, out var mask) && mask.Id != ore.Id, "same scene incorrectly merges different pickups");
            Require(!ResourceCatalog.TryPersistent("Crossroads_38", "Shiny Item Rancid Egg", ResourceKind.PaleOre, out _), "unrelated shiny inferred from its generic FSM");
            Require(ResourceCatalog.TryPlayerFlag("slyShellFrag1", out var first) && ResourceCatalog.TryPlayerFlag("slyShellFrag2", out var second) && first.Id != second.Id, "shop tiers collapsed to one source");
        });
        test("resource adapter preserves only local source receipts and resource nail values", () => {
            foreach (var key in new[] { "ore", "charmSlots", "heartPieces", "vesselFragments", "nailSmithUpgrades", "nailDamage", "salubraNotch1", "dreamReward3" })
                Require(ResourceCatalog.PreservesPlayerData(key), "full-save replay could overwrite " + key);
            foreach (var key in new[] { null, "geo", "mapCrossroads", "hasDash", "openedCrossroads" })
                Require(!ResourceCatalog.PreservesPlayerData(key), "unrelated save field intercepted " + key);
            Require(ResourceCatalog.PreservesPersistent("Deepnest_38", "Vessel Fragment") &&
                !ResourceCatalog.PreservesPersistent("Crossroads_38", "Shiny Item Rancid Egg"), "resource persistent filter too broad or too narrow");
            Require(!ProgressCatalog.Limits.ContainsKey("nailSmithUpgrades"), "personal nail upgrade remains in the shared ledger");
        });
        test("slot settings copy sanitizes resource receipts without sharing mutable collections", () => {
            var original = new DeathStatistics(); original.Increment("host");
            original.Resources.Claimed.Add("ore:dreamReward3"); original.Resources.Claimed.Add("unknown-source");
            var restored = original.Copy();
            Require(restored.Get("host") == 1 && restored.Resources.Claimed.SetEquals(new[] { "ore:dreamReward3" }), "existing slot container lost deaths or kept invalid resource");
            restored.Resources.Claimed.Clear();
            Require(original.Resources.Contains("ore:dreamReward3"), "slot copy aliases resource claims");
            Require(new DeathStatistics { Resources = null }.Copy().Resources.Claimed.Count == 0, "legacy slot without resource data is not initialized");
        });
    }
}
