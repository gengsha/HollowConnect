using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using HollowConnect;
using HollowConnect.Core;
using HutongGames.PlayMaker;
using UnityEngine;

namespace HollowConnect.Smoke;

public sealed partial class ProgressProbe {
    private bool DirectResourceUnionCase => Environment.GetEnvironmentVariable("HOLLOWCONNECT_PROGRESS_AUDIT") == "direct-union";
    private bool LegacyResourceCase => DirectResourceUnionCase || Environment.GetEnvironmentVariable("HOLLOWCONNECT_PROGRESS_AUDIT")?.StartsWith("legacy-full", StringComparison.Ordinal) == true;
    private int remoteResourceUiStarts;

    private void ObserveRemoteResourceUi(On.HutongGames.PlayMaker.Fsm.orig_EnterState orig, Fsm fsm, FsmState state) {
        var name = fsm.GameObject == null ? "" : fsm.GameObject.name;
        if (new[] { "Heart Piece Instant", "Vessel Fragment Instant", "Heart Container UI", "Vessel Fragment UI" }
            .Any(n => name == n || name == n + "(Clone)")) remoteResourceUiStarts++;
        orig(fsm, state);
        ObserveResourceNotification(fsm, state);
    }

    private void PrepareLegacyResources() {
        var pd = PlayerData.instance;
        CoopMod.Statistics = new DeathStatistics();
        remoteResourceUiStarts = 0;
        On.HutongGames.PlayMaker.Fsm.EnterState += ObserveRemoteResourceUi;
        foreach (var source in ResourceCatalog.Sources) {
            if (source.PlayerFlag != null) pd.SetBool(source.PlayerFlag, false);
            if (source.PersistentId != null) SceneData.instance.SaveMyState(new PersistentBoolData {
                id = source.PersistentId, sceneName = source.Scene, activated = false
            });
        }
        // Match a Godseeker host and a fresh story guest. Deliberately omit
        // story flags AND mod receipts: the older full fixture seeded both.
        pd.SetBool("bossRushMode", host);
        pd.SetInt("charmSlots", host ? 11 : 3);
        pd.SetInt("maxHealthBase", host ? 9 : 5); pd.SetInt("maxHealth", host ? 9 : 5); pd.SetInt("health", host ? 9 : 5);
        pd.SetInt("heartPieces", 0); pd.SetInt("MPReserveMax", host ? 99 : 0); pd.SetInt("vesselFragments", 0);
        pd.SetBool("heartPieceMax", host); pd.SetBool("vesselFragmentMax", host);
        pd.SetInt("nailSmithUpgrades", host ? 4 : 0); pd.SetInt("nailDamage", host ? 21 : 5); pd.SetInt("ore", 0);
        ResourceCheck(LocalResourceClaims.Claimed.Count == 0 && ResourceCatalog.Sources.All(s =>
            s.PlayerFlag == null || !pd.GetBool(s.PlayerFlag)), "legacy fixture starts without resource receipts or story flags");
        if (DirectResourceUnionCase) {
            pd.SetBool("bossRushMode", false);
            pd.SetInt("charmSlots", 3); pd.SetInt("maxHealthBase", 5); pd.SetInt("maxHealth", 5); pd.SetInt("health", 5);
            pd.SetInt("heartPieces", host ? 3 : 2); pd.SetInt("MPReserveMax", 0); pd.SetInt("vesselFragments", 2);
            pd.SetBool("heartPieceMax", false); pd.SetBool("vesselFragmentMax", false);
            pd.SetInt("nailSmithUpgrades", 0); pd.SetInt("nailDamage", 5);
            foreach (var id in host ? new[] { "mask:slyShellFrag1", "mask:slyShellFrag2", "mask:slyShellFrag3", "vessel:slyVesselFrag1", "vessel:slyVesselFrag2" } :
                new[] { "mask:slyShellFrag3", "mask:slyShellFrag4", "vessel:slyVesselFrag2", "vessel:dreamReward5" })
                LocalResourceClaims.TryClaim(id, out _);
        }
    }

    private IEnumerator LegacyResourceChecks() {
        try { yield return LegacyResourceChecksCore(); }
        finally { On.HutongGames.PlayMaker.Fsm.EnterState -= ObserveRemoteResourceUi; }
    }
    private IEnumerator LegacyResourceChecksCore() {
        if (DirectResourceUnionCase) { yield return DirectResourceUnionChecks(); yield break; }
        var pd = PlayerData.instance;
        if (host) {
            yield return Until(() => LocalResourceClaims.Claimed.Count == 39 && AuditIdle,
                "full host recovers all collections without story flags", 30);
            ResourceCheck(pd.GetInt("ore") == 0 && pd.GetInt("charmSlots") == 11,
                "import does not repay the full host's spent ore or add notches");
            Flag("host.audit.first-ready");
        }
        yield return Until(() => runtime.Peers.Count == 1 && engine.ClientManager.SceneSnapshotReady, "legacy host and fresh guest connected");
        if (ProgressAuditDisabled) {
            yield return new WaitForSecondsRealtime(2);
            ResourceCheck(!runtime.Shared && LocalResourceClaims.Claimed.Count == (host ? 39 : 0) &&
                pd.GetInt("charmSlots") == (host ? 11 : 3) && pd.GetInt("ore") == 0,
                "disabled sharing keeps imported host collections private");
            ResourceCheck(notificationStarts == 0 && NotificationIdle, "disabled sharing creates no resource notifications");
            yield return ResourceBarrier("legacy-off"); yield break;
        }
        yield return Until(() => LocalResourceClaims.Claimed.Count == 39 && pd.GetInt("charmSlots") == 11 &&
            pd.GetInt("maxHealthBase") == 9 && pd.GetInt("MPReserveMax") == 99 && pd.GetInt("ore") == (host ? 0 : 6) && AuditIdle,
            "fresh guest directly settles eleven notches, nine masks, three vessels and six ore sources", 15);
        ResourceCheck(remoteResourceUiStarts == 0 && HeroController.instance.acceptingInput &&
            !HeroController.instance.controlReqlinquished && !pd.GetBool("disablePause") &&
            pd.GetInt("heartPieces") == 0 && pd.GetInt("vesselFragments") == 0 &&
            pd.GetBool("heartPieceMax") && pd.GetBool("vesselFragmentMax"),
            "entry settles complete masks and soul capacity without spawning collection UI or taking control");
        ResourceCheck(pd.GetInt("nailSmithUpgrades") == (host ? 4 : 0) && pd.GetInt("nailDamage") == (host ? 21 : 5),
            "resource catch-up preserves each player's nail level and damage");
        yield return ExpectResourceNotifications(host ? new Dictionary<ResourceKind, int>() : new Dictionary<ResourceKind, int> {
            [ResourceKind.CharmNotch] = 8, [ResourceKind.MaskShard] = 16, [ResourceKind.VesselFragment] = 9, [ResourceKind.PaleOre] = 6
        }, "full catch-up displays every awarded resource in native lower-left notifications");
        var displayed = notificationStarts;
        yield return ResourceBarrier("legacy-received", 330);
        if (!host) {
            pd.SetInt("ore", 5); pd.SetInt("nailSmithUpgrades", 2); pd.SetInt("nailDamage", 13);
            engine.ClientManager.Disconnect();
            yield return Until(() => !runtime.Connected, "legacy guest disconnects after ore expenditure");
            typeof(Hkmp.Game.Client.ClientManager).GetField("_autoConnect", ResourcePrivate).SetValue(engine.ClientManager, true);
            typeof(Hkmp.Game.Client.ClientManager).GetMethod("Connect", ResourcePrivate).Invoke(engine.ClientManager,
                new object[] { "127.0.0.1", 29650, "progress" + role });
            yield return Until(() => runtime.Connected && runtime.Peers.Count == 1 && engine.ClientManager.SceneSnapshotReady,
                "legacy guest actually reconnects", 30);
            Flag("guest.legacy-rejoined");
        }
        yield return Until(() => Has("guest.legacy-rejoined") && runtime.Peers.Count == 1 && AuditIdle &&
            !Field<bool>(runtime, "progressAuditRequested"), "both complete repeated legacy collection audit", 45);
        yield return new WaitForSecondsRealtime(2);
        ResourceCheck(pd.GetInt("ore") == (host ? 0 : 5) && pd.GetInt("charmSlots") == 11 &&
            pd.GetInt("maxHealthBase") == 9 && pd.GetInt("MPReserveMax") == 99 &&
            pd.GetInt("nailSmithUpgrades") == (host ? 4 : 2) && pd.GetInt("nailDamage") == (host ? 21 : 13),
            "reconnect neither refunds ore nor duplicates permanent totals or copies nail upgrades");
        ResourceCheck(notificationStarts == displayed && NotificationIdle, "reconnect audit never repeats obtained-item notifications");
        if (host) ResourceCheck(CoopMod.Statistics.SharedResources.Claimed.Count == 39,
            "host saves reconstructed authority history for future entrants");
        yield return ResourceBarrier("legacy-complete");
    }

    private IEnumerator DirectResourceUnionChecks() {
        var pd = PlayerData.instance;
        if (host) Flag("host.audit.first-ready");
        yield return Until(() => runtime.Peers.Count == 1 && engine.ClientManager.SceneSnapshotReady &&
            LocalResourceClaims.Claimed.Count == 7 && pd.GetInt("maxHealthBase") == 6 && pd.GetInt("heartPieces") == 0 &&
            pd.GetInt("MPReserveMax") == 33 && pd.GetInt("vesselFragments") == 0 && AuditIdle,
            "complementary fragment sources union into one complete mask and one soul vessel", 15);
        ResourceCheck(remoteResourceUiStarts == 0 && HeroController.instance.acceptingInput &&
            !HeroController.instance.controlReqlinquished && !pd.GetBool("disablePause"),
            "source union settles immediately without native collection UI or control loss");
        yield return ExpectResourceNotifications(new Dictionary<ResourceKind, int> {
            [ResourceKind.MaskShard] = host ? 1 : 2, [ResourceKind.VesselFragment] = 1
        }, "complementary sources notify only the fragments newly received by this player");
        yield return ResourceBarrier("direct-union");
        if (host) {
            ResourceSend("mask:Room_Bretta/Heart Piece"); ResourceSend("vessel:Crossroads_37/Vessel Fragment");
        }
        yield return Until(() => LocalResourceClaims.Claimed.Count == 9 && pd.GetInt("heartPieces") == 1 &&
            pd.GetInt("vesselFragments") == 1 && AuditIdle, "later remote sources retain partial fragments without collection UI", 10);
        yield return ExpectResourceNotifications(new Dictionary<ResourceKind, int> {
            [ResourceKind.MaskShard] = host ? 2 : 3, [ResourceKind.VesselFragment] = 2
        }, "later partial fragments also show native lower-left obtained-item notifications");
        var displayed = notificationStarts;
        ResourceSend("mask:Room_Bretta/Heart Piece"); ResourceSend("vessel:Crossroads_37/Vessel Fragment");
        yield return new WaitForSecondsRealtime(1);
        ResourceCheck(remoteResourceUiStarts == 0 && pd.GetInt("maxHealthBase") == 6 && pd.GetInt("MPReserveMax") == 33 &&
            pd.GetInt("heartPieces") == 1 && pd.GetInt("vesselFragments") == 1 && LocalResourceClaims.Claimed.Count == 9,
            "duplicate source replay leaves full and partial resource totals unchanged");
        ResourceCheck(notificationStarts == displayed && NotificationIdle, "duplicate source replay creates no obtained-item notification");
        yield return ResourceBarrier("direct-union-complete");
    }
}
