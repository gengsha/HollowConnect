using System;
using System.Collections;
using System.Linq;
using HollowConnect;
using HollowConnect.Core;
using UnityEngine;

namespace HollowConnect.Smoke;

public sealed partial class ProgressProbe {
    private string RoyalCharmScenario => Environment.GetEnvironmentVariable("HOLLOWCONNECT_ROYAL_CHARM");
    private bool RoyalCharmCase => !string.IsNullOrEmpty(RoyalCharmScenario);
    private bool RoyalCharmOwner => RoyalCharmScenario == role;

    private void PrepareRoyalCharm() {
        var pd = PlayerData.instance;
        CoopMod.Statistics = new DeathStatistics();
        for (var i = 1; i <= 40; i++) {
            pd.SetBool("gotCharm_" + i, false);
            pd.SetBool("equippedCharm_" + i, false);
        }
        pd.equippedCharms.Clear();
        var state = RoyalCharmScenario == "stale" ? 4 : RoyalCharmScenario == "halves" ? (host ? 1 : 2) :
            RoyalCharmOwner ? 4 : host ? 0 : 3;
        if (RoyalCharmScenario == "nocharms") state = host ? 4 : 3;
        pd.SetInt("royalCharmState", state);
        pd.SetBool("gotCharm_36", state > 0); pd.SetBool("hasCharm", state > 0);
        pd.SetBool("gotQueenFragment", state == 1 || state >= 3);
        pd.SetBool("gotKingFragment", state >= 2);
        pd.SetInt("charmsOwned", state >= 3 ? 1 : 0);
        // Distinct local loadouts prove that only the royal charm cost changes.
        var other = host ? 2 : 4;
        pd.SetBool("gotCharm_" + other, true);
        pd.SetBool("equippedCharm_" + other, true); pd.EquipCharm(other);
        pd.SetInt("charmSlots", 4);
        pd.SetInt("charmCost_36", RoyalCharmOwner || RoyalCharmScenario == "nocharms" && host ? 0 : 5);
        if (state >= 3) { pd.SetBool("equippedCharm_36", true); pd.EquipCharm(36); }
        pd.CalculateNotchesUsed(); GameManager.instance.RefreshOvercharm();
        Mark("ROYAL FIXTURE scenario=" + RoyalCharmScenario + " state=" + state + " cost=" + pd.GetInt("charmCost_36") +
            " used=" + pd.GetInt("charmSlotsFilled") + " overcharmed=" + pd.GetBool("overcharmed"));
        if (host) Flag("host.audit.first-ready");
    }

    private IEnumerator RoyalCharmChecks() {
        var pd = PlayerData.instance;
        var other = host ? 2 : 4;
        var equippedBefore = pd.equippedCharms.ToArray();
        yield return Until(() => runtime.Peers.Count == 1 && engine.ClientManager.SceneSnapshotReady,
            "royal charm real UDP peers and scene snapshot ready");
        if (RoyalCharmScenario == "nocharms") {
            yield return new WaitForSecondsRealtime(1.5f);
            ResourceCheck(!runtime.ShareCharms && pd.GetInt("royalCharmState") == (host ? 4 : 3) &&
                pd.GetInt("charmCost_36") == (host ? 0 : 5), "disabled charm sharing preserves private Kingsoul and Void Heart costs");
        } else if (RoyalCharmScenario == "halves") {
            yield return Until(() => pd.GetInt("royalCharmState") == 3, "royal halves form Kingsoul over UDP", 6);
            ResourceCheck(pd.GetInt("charmCost_36") == 5 && !pd.GetBool("equippedCharm_36"),
                "complete Kingsoul retains five-notch cost and personal equipment choice");
        } else {
            yield return Until(() => pd.GetInt("royalCharmState") == 4, "Void Heart progression received over UDP", 6);
            yield return Until(() => pd.GetInt("charmCost_36") == 0, "Void Heart costs zero notches including an already-corrupted save", 4);
            ResourceCheck(pd.GetInt("charmSlotsFilled") == pd.GetInt("charmCost_" + other) && !pd.GetBool("overcharmed"),
                "Void Heart repair recalculates occupied notches and removes false overcharm");
            ResourceCheck(pd.equippedCharms.SequenceEqual(equippedBefore), "shared progression preserves the local equipped charm list");
            // Model the native equip operation after receiving an unequipped
            // Void Heart; use the actual game notch calculation afterwards.
            if (!pd.GetBool("equippedCharm_36")) { pd.SetBool("equippedCharm_36", true); pd.EquipCharm(36); }
            pd.CalculateNotchesUsed(); GameManager.instance.RefreshOvercharm();
            ResourceCheck(pd.GetInt("charmSlotsFilled") == pd.GetInt("charmCost_" + other) && !pd.GetBool("overcharmed"),
                "equipping the received Void Heart consumes no extra notches");
            yield return ResourceBarrier("royal-zero-cost");
            // A fresh offer is deliberately absent: unchanged royal state must
            // still repair the old five-notch value on the periodic check.
            pd.SetInt("charmCost_36", 5); pd.CalculateNotchesUsed(); GameManager.instance.RefreshOvercharm();
            yield return Until(() => pd.GetInt("charmCost_36") == 0 && !pd.GetBool("overcharmed"),
                "unchanged Void Heart progression repairs stale cost without a new grant", 3);
            yield return new WaitForSecondsRealtime(1);
            ResourceCheck(pd.equippedCharms.Count(i => i == 36) == 1 && pd.GetInt("charmSlots") == 4 &&
                pd.GetBool("equippedCharm_" + other) && !pd.GetBool("equippedCharm_" + (host ? 4 : 2)),
                "repeated reconciliation keeps one Void Heart and the distinct personal loadout");
        }
        Mark("ROYAL RESULT scenario=" + RoyalCharmScenario + " state=" + pd.GetInt("royalCharmState") +
            " cost=" + pd.GetInt("charmCost_36") + " used=" + pd.GetInt("charmSlotsFilled") +
            " overcharmed=" + pd.GetBool("overcharmed"));
        yield return ResourceBarrier("royal-complete");
    }
}
