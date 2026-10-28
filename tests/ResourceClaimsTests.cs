using System;
using System.Collections.Generic;
using System.Linq;
using HollowConnect.Core;
using HollowConnect;
using System.Reflection;

internal static class ResourceClaimsTests {
    private static void Require(bool ok, string reason) { if (!ok) throw new Exception(reason); }
    internal static void Register(Action<string, Action> test) {
        test("unique keys preserve consumed ownership separately from shared source receipts", () => {
            var keys = ResourceCatalog.Sources.Where(s => s.Kind == ResourceKind.UniqueKey).ToArray();
            Require(keys.Length == 4 && keys.Select(s => s.InventoryFlag).Distinct().Count() == 4, "missing unique key");
            var local = new ResourceClaims();
            foreach (var key in keys) {
                Require(!string.IsNullOrEmpty(key.SpentFlag) && ResourceCatalog.PreservesPlayerData(key.InventoryFlag), "key can replay through old saves");
                Require(local.TryClaim(key.Id, out _) && !local.TryClaim(key.Id, out _), "consumed key can be reissued");
                Require(key.PersistentId == null || ResourceCatalog.PreservesPersistent(key.Scene, key.PersistentId),
                    "full-save replay can erase an unclaimed native key pickup");
            }
            foreach (var door in new[] { "openedCityGate", "openedMageDoor", "openedLoveDoor" })
                Require(!ProgressCatalog.Limits.ContainsKey(door) && ResourceCatalog.IsPersonalKeyDoor(door), "unique-key door bypasses personal use");
            Require(ProgressCatalog.IsPickup("hasTramPass") && ProgressCatalog.IsPickup("hasKingsBrand"), "permanent passes lost existing sharing");
        });
        test("dream boss essence has twelve independent receipts without tree or aggregate currency claims", () => {
            var rewards = ResourceCatalog.Sources.Where(s => s.Kind == ResourceKind.DreamEssence).ToArray();
            Require(rewards.Length == 12 && rewards.Sum(s => s.Amount) == 2700, "dream reward catalog incomplete");
            Require(rewards.Count(s => s.ClaimInt != null) == 7 && rewards.Count(s => s.PlayerFlag != null) == 5, "native acquisition proofs missing");
            var claims = new ResourceClaims(); var essence = 0;
            foreach (var reward in rewards.Concat(rewards.Reverse())) if (claims.TryClaim(reward.Id, out _)) essence += reward.Amount;
            Require(essence == 2700 && !claims.TryClaim("essence:dreamPlant", out _) && !claims.TryClaim("dreamOrbs", out _), "tree or replay grants essence");
            Require(ResourceCatalog.PreservesPlayerData("dreamOrbs"), "legacy save replay can share trees");
            Require(rewards.All(s => ResourceCatalog.PreservesPlayerData(s.ClaimInt ?? s.PlayerFlag)),
                "legacy full-save can invent a boss reward receipt from another player's claim history");
        });
        test("stag station union reconstructs native conversation count without requiring optional destinations", () => {
            var opened = new HashSet<string>(StagStations.Original.Take(4));
            Require(StagStations.Count(opened.Contains) == 4, "partial route unlocked Nest conversation");
            foreach (var key in StagStations.Original.Skip(4)) opened.Add(key);
            Require(StagStations.Count(opened.Contains) == 9, "split station unlocks did not combine");
            opened.Add("openedStagNest"); opened.Add("openedHiddenStation");
            Require(StagStations.Count(opened.Contains) == 9, "optional destinations inflated original station count");
        });
        test("grub receipts distinguish collector bottles and survive reordered reconnect grants", () => {
            var grubs = ResourceCatalog.Sources.Where(s => s.Kind == ResourceKind.Grub).ToArray();
            Require(grubs.Length == 46 && grubs.Count(s => s.Scene == "Ruins2_11") == 3, "missing independent grub IDs");
            var claims = new ResourceClaims(); var count = 0;
            foreach (var id in grubs.Select(s => s.Id).Concat(grubs.Reverse().Select(s => s.Id)))
                if (claims.TryClaim(id, out _)) count++;
            var reopened = new DeathStatistics { Resources = claims }.Copy().Resources;
            foreach (var grub in grubs) if (reopened.TryClaim(grub.Id, out _)) count++;
            Require(count == 46 && reopened.Claimed.Count == 46, "duplicate grub award after reconnect");
            Require(ResourceCatalog.PreservesPlayerData("grubsCollected") && ResourceCatalog.PreservesPlayerData("scenesGrubRescued") &&
                ResourceCatalog.PreservesPersistent("Ruins2_11", "Grub Bottle"), "old full-save replay bypasses grub receipts");
        });
        test("colosseum shared completion restores its own registration without unlocking unpaid trials", () => {
            var flags = new HashSet<string> { "colosseumBronzeCompleted", "colosseumBronzeOpened", "colosseumSilverCompleted" };
            Require(ColosseumProgress.RepairRegistrations(flags.Contains, key => flags.Add(key)) == 1, "missing silver registration not repaired");
            Require(flags.Contains("colosseumSilverOpened") && !flags.Contains("colosseumGoldOpened") && !flags.Contains("colosseumGoldCompleted"), "repair paid the next trial or invented completion");
            Require(ColosseumProgress.RepairRegistrations(flags.Contains, key => flags.Add(key)) == 0, "repair was not idempotent");
        });
        test("colosseum registration repair does not invent prerequisites or completion rewards", () => {
            var flags = new HashSet<string> { "colosseumSilverCompleted" };
            ColosseumProgress.RepairRegistrations(flags.Contains, key => flags.Add(key));
            Require(flags.SetEquals(new[] { "colosseumSilverCompleted", "colosseumSilverOpened" }), "repair changed other trials or resource flags");
            flags.Clear(); flags.Add("colosseumSilverOpened");
            Require(ColosseumProgress.RepairRegistrations(flags.Contains, key => flags.Add(key)) == 0 && flags.Count == 1, "registration invented completion");
            flags.Clear(); flags.Add("colosseumGoldCompleted");
            Require(ColosseumProgress.RepairRegistrations(flags.Contains, key => flags.Add(key)) == 1 && flags.Contains("colosseumGoldOpened"), "gold completion stayed locked");
        });
        test("simple key sources catch up late entrants without refunding personal spending", () => {
            var ids = ResourceCatalog.Sources.Where(s => s.Kind == ResourceKind.SimpleKey).Select(s => s.Id).ToArray();
            Require(ids.Length == 4, "missing key source");
            var host = new ResourceClaims(); var hostKeys = 0;
            foreach (var id in ids) if (host.TryClaim(id, out _)) hostKeys++;
            hostKeys -= 3;
            var late = new ResourceClaims(); var lateKeys = 0;
            foreach (var id in host.Copy().Claimed) if (late.TryClaim(id, out _)) lateKeys++;
            Require(hostKeys == 1 && lateKeys == 4, "late entrant copied another player's spent balance");
            lateKeys -= 2;
            var reloaded = new DeathStatistics { Resources = late }.Copy().Resources;
            foreach (var id in ids.Reverse().Concat(ids)) if (reloaded.TryClaim(id, out _)) lateKeys++;
            Require(lateKeys == 2 && reloaded.Claimed.Count == 4, "reconnect refunded spent keys or duplicated a source");
            var oldSlot = new ResourceClaims(); oldSlot.TryClaim(ids[0], out _);
            var oldBalance = 0;
            foreach (var id in ids) if (oldSlot.TryClaim(id, out _)) oldBalance++;
            Require(oldBalance == 3, "previously obtained and spent local source paid twice");
        });
        test("legacy completed collections never invent simple key acquisition history", () => {
            var claims = new ResourceClaims();
            ResourceReconciliation.ImportCompletedCollections(claims, 11, 16, 9, 0, 4);
            Require(claims.Claimed.Count == 39 && ResourceCatalog.Sources.Where(s => s.Kind == ResourceKind.SimpleKey)
                .All(s => !claims.Contains(s.Id)), "full ore or permanent upgrades fabricated key receipts");
            ResourceCatalog.TryPlayerFlag("gotLurkerKey", out var lurker);
            Require(lurker?.Kind == ResourceKind.SimpleKey && ResourceCatalog.PreservesPlayerData("slySimpleKey") &&
                ResourceCatalog.PreservesPlayerData("gotLurkerKey") && ResourceCatalog.PreservesPlayerData("simpleKeys") &&
                ResourceCatalog.PreservesPersistent("Ruins1_17", "Shiny Item") &&
                ResourceCatalog.PreservesPersistent("Abyss_20", "Shiny Item Stand"), "save replay can overwrite key ownership");
        });
        test("full legacy collections recover all sources without refunding spent ore", () => {
            var host = new ResourceClaims();
            Require(ResourceReconciliation.ImportCompletedCollections(host, 11, 16, 9, 0, 4) == 39,
                "Godseeker totals without story receipts did not recover all sources");
            Require(ResourceReconciliation.NextRepair(host, 11, 16, 9) == null, "complete host was awarded again");
            var guest = new ResourceClaims(); var ore = 0;
            foreach (var id in host.Claimed) if (guest.TryClaim(id, out var source) && source.Kind == ResourceKind.PaleOre) ore++;
            Require(ore == 6, "fresh guest did not receive six distinct ore sources");
            ore -= 3;
            var reopened = guest.Copy();
            Require(ResourceReconciliation.ImportCompletedCollections(reopened, 11, 16, 9, ore, 3) == 0,
                "reconnect reimported a spent source");
            foreach (var id in host.Claimed) if (reopened.TryClaim(id, out var source) && source.Kind == ResourceKind.PaleOre) ore++;
            Require(ore == 3, "reconnect refunded the guest's upgrade cost");
        });
        test("legacy ore collection includes every native upgrade cost and remaining inventory", () => {
            var spent = new[] { 0, 0, 1, 3, 6 };
            for (var level = 0; level <= 4; level++) {
                var claims = new ResourceClaims();
                claims.TryClaim("ore:dreamReward3", out _);
                Require(ResourceReconciliation.ImportCompletedCollections(claims, 3, 0, 0, 6 - spent[level], level) == 5,
                    "full ore collection not recovered at nail level " + level);
                Require(claims.Claimed.Count == 6, "existing source was duplicated");
            }
        });
        test("partial or invalid legacy totals never invent source identities", () => {
            foreach (var amounts in new[] { new[] { 10, 15, 8, 0, 0 }, new[] { 3, 0, 0, 2, 3 },
                new[] { 12, 17, 10, 7, 0 }, new[] { 3, 0, 0, 6, 5 }, new[] { 3, 0, 0, -1, 4 } }) {
                var claims = new ResourceClaims();
                Require(ResourceReconciliation.ImportCompletedCollections(claims, amounts[0], amounts[1], amounts[2], amounts[3], amounts[4]) == 0 &&
                    claims.Claimed.Count == 0, "partial or invalid collection invented receipts");
            }
        });
        test("royal charm halves union in either order and void heart never regresses", () => {
            for (var first = 0; first <= 4; first++) for (var second = 0; second <= 4; second++) {
                var ledger = new ProgressLedger(); ledger.Merge("royalCharmState", first); ledger.Merge("royalCharmState", second);
                var reverse = new ProgressLedger(); reverse.Merge("royalCharmState", second); reverse.Merge("royalCharmState", first);
                Require(ledger.Get("royalCharmState") == reverse.Get("royalCharmState"), "half union depends on order");
                Require(!ledger.Merge("royalCharmState", first), "repeated half duplicates progress");
            }
            Require(ProgressCatalog.Combine("royalCharmState", 1, 2) == 3 && ProgressCatalog.Combine("royalCharmState", 4, 2) == 4, "royal charm state downgraded or lost a half");
            Require(ProgressCatalog.CharmCount(_ => true, 0) == 39 && ProgressCatalog.CharmCount(_ => true, 2) == 39 &&
                ProgressCatalog.CharmCount(_ => true, 3) == 40 && ProgressCatalog.CharmCount(_ => true, 4) == 40, "native charm count treats an incomplete Kingsoul as complete");
        });
        test("join audit repairs only missing nonconsumable totals and never refunds ore", () => {
            var claims = new ResourceClaims();
            claims.TryClaim("notch:notchShroomOgres", out _);
            claims.TryClaim("ore:dreamReward3", out _);
            Require(ResourceReconciliation.NextRepair(claims, 3, 0, 0)?.Id == "notch:notchShroomOgres", "missed arena notch not repaired");
            Require(ResourceReconciliation.NextRepair(claims, 4, 0, 0) == null, "repeated audit duplicates notch or spent ore");
            Require(ResourceReconciliation.NextRepair(claims, 11, 16, 9) == null, "full progress was changed");
            Require(ResourceReconciliation.NextRepair(new ResourceClaims(), 3, 0, 0) == null, "unclaimed reward invented");
        });
        test("join audit fragment repair respects conversions and full source union", () => {
            var claims = new ResourceClaims();
            foreach (var source in ResourceCatalog.Sources) claims.TryClaim(source.Id, out _);
            var slots = 3; var masks = 0; var vessels = 0; var repairs = 0;
            ResourceSource next;
            while ((next = ResourceReconciliation.NextRepair(claims, slots, masks, vessels)) != null && repairs++ < 40) {
                if (next.Kind == ResourceKind.CharmNotch) slots++;
                else if (next.Kind == ResourceKind.MaskShard) masks++;
                else if (next.Kind == ResourceKind.VesselFragment) vessels++;
                else throw new Exception("audit issued a consumable repair");
            }
            Require(slots == 11 && masks == 16 && vessels == 9 && repairs == 33, "full nonconsumable entitlement mismatch");
            Require(ResourceReconciliation.NextRepair(claims, 11, 16, 9) == null, "complete totals did not remain stable");
            Require(ResourceReconciliation.NextRepair(claims, 11, 15, 9)?.Kind == ResourceKind.MaskShard, "converted mask plus partial shard not counted");
        });
        test("native pickup history migrates separately from shared ownership and copies safely", () => {
            var legacy = new DeathStatistics();
            Require(legacy.Copy().NativePickups == null, "legacy pickup history was marked initialized before vanilla load");
            legacy.NativePickups = new Dictionary<string, int> { ["hasDash"] = 1, ["fireballLevel"] = 2, ["hasCharm"] = 1,
                ["hasDoubleJump"] = 0, ["geo"] = 50, ["killedFalseKnight"] = 1 };
            legacy.SharedProgress["hasDoubleJump"] = 1;
            var copy = legacy.Copy();
            Require(copy.NativePickups.Count == 3 && copy.NativePickups["hasCharm"] == 1 && !copy.NativePickups.ContainsKey("hasDoubleJump"), "shared or invalid progress seeded a local interaction");
            copy.NativePickups["hasDash"] = 0;
            Require(legacy.NativePickups["hasDash"] == 1 && copy.SharedProgress["hasDoubleJump"] == 1, "pickup history aliases or erased shared ownership");
        });
        test("server guest receipt persists without marking the host locally awarded", () => {
            var old = CoopMod.Statistics;
            try {
                CoopMod.Statistics = new DeathStatistics();
                var server = new CoopServerAddon();
                var claim = typeof(CoopServerAddon).GetMethod("ClaimHostResource", BindingFlags.Instance | BindingFlags.NonPublic);
                const string source = "notch:colosseumBronze";
                Require((bool)claim.Invoke(server, new object[] { source }), "guest offer rejected");
                Require(CoopMod.Statistics.SharedResources.Contains(source), "authority receipt was not persisted");
                Require(!CoopMod.Statistics.Resources.Contains(source), "server offer falsely marked host paid");
                var copy = CoopMod.Statistics.Copy();
                Require(copy.SharedResources.Contains(source) && !copy.Resources.Contains(source), "save copy conflates authority with local receipt");
                Require(copy.Resources.TryClaim(source, out _) && !copy.Resources.TryClaim(source, out _), "host cannot receive once after authority claim");
                Require(!(bool)claim.Invoke(server, new object[] { source }), "replayed offer accepted twice");
            } finally { CoopMod.Statistics = old; }
        });
        test("legacy host receipts seed authority history without aliasing local awards", () => {
            const string id = "ore:dreamReward3";
            var old = new DeathStatistics { SharedResources = null };
            old.Resources.TryClaim(id, out _);
            var copy = old.Copy();
            Require(copy.SharedResources.Contains(id), "old receipt absent from authority history");
            copy.SharedResources.Claimed.Clear();
            Require(copy.Resources.Contains(id) && old.Resources.Contains(id), "authority migration aliases local receipts");
        });
        test("resource catalog covers permanent upgrades, ore and four simple key sources", () => {
            var all = ResourceCatalog.Sources.ToArray();
            Require(all.Length == 105 && all.Select(s => s.Id).Distinct(StringComparer.Ordinal).Count() == 105, "native source identity collision");
            foreach (var pair in new[] { (ResourceKind.CharmNotch, 8), (ResourceKind.MaskShard, 16), (ResourceKind.VesselFragment, 9), (ResourceKind.PaleOre, 6), (ResourceKind.SimpleKey, 4), (ResourceKind.Grub, 46), (ResourceKind.UniqueKey, 4), (ResourceKind.DreamEssence, 12) })
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
        test("host shared progress and resource receipts survive a slot copy without cross-slot aliases", () => {
            var host = new DeathStatistics();
            host.SharedProgress["hasDash"] = 1;
            host.Resources.TryClaim("ore:Mines_34/Shiny Item Stand", out _);
            var other = host.Copy();
            Require(other.SharedProgress.TryGetValue("hasDash", out var dash) && dash == 1, "host progress was not persisted");
            Require(other.Resources.Contains("ore:Mines_34/Shiny Item Stand"), "host source receipt was not persisted");
            other.SharedProgress["hasDash"] = 0;
            other.Resources.Claimed.Clear();
            Require(host.SharedProgress["hasDash"] == 1 && host.Resources.Contains("ore:Mines_34/Shiny Item Stand"), "slot copies share host ledgers");
        });
    }
}
