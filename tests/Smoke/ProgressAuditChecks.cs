using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HollowConnect;
using HollowConnect.Core;
using HutongGames.PlayMaker;
using HutongGames.PlayMaker.Actions;
using Newtonsoft.Json;
using UnityEngine;
using Object = UnityEngine.Object;

namespace HollowConnect.Smoke;

public sealed partial class ProgressProbe {
    private bool ProgressAuditCase => !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("HOLLOWCONNECT_PROGRESS_AUDIT"));
    private bool ProgressAuditFull => Environment.GetEnvironmentVariable("HOLLOWCONNECT_PROGRESS_AUDIT")?.StartsWith("full", StringComparison.Ordinal) == true;
    private bool ProgressAuditDisabled => Environment.GetEnvironmentVariable("HOLLOWCONNECT_PROGRESS_AUDIT") == "off" ||
        Environment.GetEnvironmentVariable("HOLLOWCONNECT_PROGRESS_AUDIT") == "legacy-full-off";
    private bool ProgressAuditNoCharms => Environment.GetEnvironmentVariable("HOLLOWCONNECT_PROGRESS_AUDIT") == "nocharms";
    private bool ProgressAuditInspect => Environment.GetEnvironmentVariable("HOLLOWCONNECT_PROGRESS_AUDIT_INSPECT") == "1";
    private bool ProgressAuditCharmOnly => Environment.GetEnvironmentVariable("HOLLOWCONNECT_PROGRESS_AUDIT")?.EndsWith("charm", StringComparison.Ordinal) == true;
    private bool ProgressAuditFortyCharms => ProgressAuditFull || Environment.GetEnvironmentVariable("HOLLOWCONNECT_PROGRESS_AUDIT") == "forty-charm";
    private bool ProgressAuditHalves => Environment.GetEnvironmentVariable("HOLLOWCONNECT_PROGRESS_AUDIT") == "halves-charm";
    private const string AuditMask = "mask:Crossroads_13/Heart Piece";
    private bool AuditIdle => (bool)ResourceService.GetType().GetProperty("Idle", ResourcePrivate).GetValue(ResourceService);
    private void PrepareProgressAudit() {
        if (RoyalCharmCase) { PrepareRoyalCharm(); return; }
        if (LegacyResourceCase) { PrepareLegacyResources(); return; }
        var pd = PlayerData.instance;
        // Initial in-memory old-save fixtures, never private disk saves.
        CoopMod.Statistics = new DeathStatistics();
        pd.SetBool("hasDash", host); pd.SetBool("hasDoubleJump", !host);
        pd.SetBool("canDash", host); pd.SetInt("charmSlots", host ? 3 : 4);
        pd.SetBool("gotCharm_20", !host); pd.SetInt("charmsOwned", host ? 0 : 1);
        pd.SetBool("hasCharm", !host);
        if (ProgressAuditFortyCharms && !host) {
            for (var i = 1; i <= 40; i++) pd.SetBool("gotCharm_" + i, true);
            pd.SetInt("royalCharmState", 4); pd.SetBool("gotQueenFragment", true); pd.SetBool("gotKingFragment", true);
            pd.SetInt("charmsOwned", 40);
        }
        if (ProgressAuditHalves || ProgressAuditNoCharms) {
            pd.SetBool("gotCharm_36", true); pd.SetInt("royalCharmState", host ? 1 : 2);
            pd.SetBool("gotQueenFragment", host); pd.SetBool("gotKingFragment", !host);
        }
        pd.SetBool(host ? "notchShroomOgres" : "notchFogCanyon", true);
        pd.SetInt("heartPieces", 0);
        pd.SetInt("nailSmithUpgrades", 1); pd.SetInt("nailDamage", 9); pd.SetInt("ore", 0);
        if (host) {
            CoopMod.Statistics.Resources.TryClaim("notch:notchShroomOgres", out _);
            CoopMod.Statistics.Resources.TryClaim(AuditMask, out _);
        }
        if (ProgressAuditFull && !host) {
            foreach (var source in ResourceCatalog.Sources.Where(s => s.Kind != HollowConnect.Core.ResourceKind.SimpleKey)) {
                CoopMod.Statistics.Resources.TryClaim(source.Id, out _);
                if (source.PlayerFlag != null) pd.SetBool(source.PlayerFlag, true);
                if (source.PersistentId != null) SceneData.instance.SaveMyState(new PersistentBoolData {
                    id = source.PersistentId, sceneName = source.Scene, activated = true
                });
            }
            foreach (var pair in ProgressCatalog.Limits.Where(p => ProgressCatalog.IsPickup(p.Key))) {
                if (pair.Value == 1) pd.SetBool(pair.Key, true); else pd.SetInt(pair.Key, pair.Value);
            }
            pd.SetInt("maxHealthBase", 9); pd.SetInt("maxHealth", 9); pd.SetInt("health", 9); pd.SetInt("heartPieces", 0);
            pd.SetInt("MPReserveMax", 99); pd.SetInt("vesselFragments", 0); pd.SetInt("charmSlots", 11); pd.SetInt("charmsOwned", 40);
            pd.SetInt("nailSmithUpgrades", 4); pd.SetInt("nailDamage", 21);
        }
    }
    private IEnumerator ProgressAuditChecks() {
        if (RoyalCharmCase) { yield return RoyalCharmChecks(); yield break; }
        if (LegacyResourceCase) { yield return LegacyResourceChecks(); yield break; }
        var pd = PlayerData.instance;
        if (host) {
            if (!ProgressAuditDisabled) {
                yield return Until(() => pd.GetInt("charmSlots") == 4 && pd.GetInt("heartPieces") == 1 && AuditIdle && !Field<bool>(runtime, "progressAuditRequested"),
                    "initial host audit repairs claimed-but-missing old notch and native mask fragment");
                // Inject another deficit only after the first audit is done.
                // The subsequent actual guest join must request a new audit.
                pd.SetInt("charmSlots", 3);
            }
            Flag("host.audit.first-ready");
        }
        yield return Until(() => runtime.Peers.Count == 1 && engine.ClientManager.SceneSnapshotReady, "both real UDP peers ready for join audit");
        if (ProgressAuditDisabled) {
            yield return new WaitForSecondsRealtime(1);
            ResourceCheck(!runtime.Shared && pd.GetBool("hasDash") == host && pd.GetBool("hasDoubleJump") != host &&
                pd.GetInt("charmSlots") == (host ? 3 : 4), "disabled sharing neither unions abilities nor repairs local resources");
            yield return ResourceBarrier("audit-disabled"); yield break;
        }
        var expectedSlots = ProgressAuditFull ? 11 : 5;
        yield return Until(() => pd.GetBool("hasDash") && pd.GetBool("hasDoubleJump") && pd.GetInt("charmSlots") == expectedSlots &&
            AuditIdle && !Field<bool>(runtime, "progressAuditRequested"), "join unions complementary abilities and repairs both resource sources", 90);
        if (ProgressAuditFull) {
            yield return Until(() => pd.GetInt("maxHealthBase") == 9 && pd.GetInt("MPReserveMax") == 99 &&
                LocalResourceClaims.Claimed.Count == 39 && AuditIdle, "full entrant shares all 39 sources and native fragment conversions", 240);
            ResourceCheck(pd.GetInt("charmsOwned") == 40 && pd.GetInt("fireballLevel") == 2 && pd.GetInt("quakeLevel") == 2 &&
                pd.GetInt("screamLevel") == 2, "full entrant unions charms and maximum spell levels");
            ResourceCheck(pd.GetInt("ore") == (host ? 6 : 0) && pd.GetInt("nailSmithUpgrades") == (host ? 1 : 4),
                "full entrant's consumed ore is not refunded and personal nail is retained");
        } else yield return Until(() => pd.GetInt("heartPieces") == 1 && AuditIdle, "shared first mask shard native UI completed");
        if (ProgressAuditNoCharms) {
            yield return new WaitForSecondsRealtime(1);
            ResourceCheck(!runtime.ShareCharms && pd.GetBool("gotCharm_20") != host && pd.GetBool("hasCharm") != host &&
                pd.GetInt("charmsOwned") == (host ? 0 : 1), "disabled charm sharing retains private ownership and inventory unlock while abilities and resources union");
            ResourceCheck(pd.GetInt("royalCharmState") == (host ? 1 : 2) && pd.GetBool("gotQueenFragment") == host &&
                pd.GetBool("gotKingFragment") != host, "disabled charm sharing keeps royal charm halves private");
            yield return ResourceBarrier("audit-nocharms"); yield break;
        }
        ResourceCheck(pd.GetBool("gotCharm_20") && pd.GetBool("hasCharm") && pd.GetInt("charmsOwned") == (ProgressAuditFortyCharms ? 40 : ProgressAuditHalves ? 2 : 1),
            "join unions charm ownership and native unlock without adding duplicate aggregate count");
        if (ProgressAuditHalves) ResourceCheck(pd.GetInt("royalCharmState") == 3 && pd.GetBool("gotQueenFragment") && pd.GetBool("gotKingFragment"),
            "complementary royal charm halves form complete Kingsoul on both clients");
        ResourceCheck(CoopMod.Statistics.NativePickups.TryGetValue(host ? "hasDash" : "hasDoubleJump", out var native) && native == 1 &&
            (ProgressAuditFull && !host || !CoopMod.Statistics.NativePickups.ContainsKey(host ? "hasDoubleJump" : "hasDash")),
            "shared abilities retain separate local pickup history");
        yield return ResourceBarrier("audit-initial", ProgressAuditFull ? 300 : 12);
        if (!ProgressAuditCharmOnly) {
            if (!ProgressAuditFull && !ProgressAuditInspect) yield return AuditDuplicateMask();
            yield return AuditNativeWings(expectedSlots);
        }
        yield return AuditNativeCharm();
        var copy = JsonConvert.DeserializeObject<DeathStatistics>(JsonConvert.SerializeObject(CoopMod.Statistics)).Copy();
        ResourceCheck(copy.Resources.Claimed.SetEquals(LocalResourceClaims.Claimed) && copy.NativePickups.OrderBy(p => p.Key)
            .SequenceEqual(CoopMod.Statistics.NativePickups.OrderBy(p => p.Key)), "slot JSON preserves source and native-pickup histories separately");
        if (host) ResourceCheck(CoopMod.Statistics.SharedResources.Claimed.SetEquals(LocalResourceClaims.Claimed) &&
            CoopMod.Statistics.SharedProgress["hasDash"] == 1 && CoopMod.Statistics.SharedProgress["hasDoubleJump"] == 1,
            "host save owns the union for future joiners");
        yield return ResourceBarrier("audit-complete");
    }
    private IEnumerator AuditNativeCharm() {
        HeroController.instance.AffectedByGravity(true);
        yield return Room("Crossroads_ShamanTemple");
        var pd = PlayerData.instance;
        if (host) {
            var pickup = Object.FindObjectsOfType<PlayMakerFSM>(true).Single(f => f.gameObject.scene.name == "Crossroads_ShamanTemple" &&
                f.FsmName == "Shiny Control" && f.FsmVariables.FindFsmString("PD Bool Name")?.Value == "gotCharm_20");
            ResourceCheck(pickup.gameObject.activeInHierarchy && pd.GetBool("gotCharm_20") &&
                !CoopMod.Statistics.NativePickups.ContainsKey("gotCharm_20"), "shared charm does not hide untouched native world pickup");
            var before = pd.GetInt("charmsOwned");
            Dump(pickup);
            HeroController.instance.AffectedByGravity(true);
            HeroController.instance.transform.position = pickup.transform.position + Vector3.up;
            HeroController.instance.GetComponent<Rigidbody2D>().velocity = Vector2.zero; Physics2D.SyncTransforms();
            var deadline = Time.realtimeSinceStartup + 30;
            var previous = "";
            while (!CoopMod.Statistics.NativePickups.ContainsKey("gotCharm_20") && Time.realtimeSinceStartup < deadline) {
                if (pickup != null && previous != pickup.ActiveStateName) {
                    previous = pickup.ActiveStateName; Mark("AUDIT CHARM STATE " + previous);
                }
                yield return ResourcePress(InputHandler.Instance.inputActions.up);
                yield return new WaitForSecondsRealtime(.2f);
            }
            yield return Until(() => CoopMod.Statistics.NativePickups.ContainsKey("gotCharm_20"), "actual native duplicate charm pickup writes local receipt", 2);
            deadline = Time.realtimeSinceStartup + 20;
            while ((!HeroController.instance.acceptingInput || HeroController.instance.controlReqlinquished || pd.GetBool("disablePause")) &&
                Time.realtimeSinceStartup < deadline) {
                yield return ResourcePress(InputHandler.Instance.inputActions.jump);
                yield return new WaitForSecondsRealtime(.4f);
            }
            yield return ResourceIdle("duplicate shared charm", 2);
            ResourceCheck(pd.GetInt("charmsOwned") == before, "duplicate charm grants air without incrementing charmsOwned before=" + before + " after=" + pd.GetInt("charmsOwned"));
            Freeze();
            Flag("host.audit.charm-complete");
        } else yield return Until(() => Has("host.audit.charm-complete"), "host original duplicate charm pickup completes", 60);
        yield return ResourceBarrier("audit-native-charm");
    }
    private IEnumerator AuditDuplicateMask() {
        yield return Room("Crossroads_13");
        var pd = PlayerData.instance;
        var pickup = Object.FindObjectsOfType<PlayMakerFSM>(true).Single(f => f.gameObject.scene.name == "Crossroads_13" &&
            f.name == "Heart Piece" && f.FsmName == "Heart Container Control");
        ResourceCheck(pickup.gameObject.activeInHierarchy && LocalResourceClaims.Contains(AuditMask), "already-shared world shard remains visible locally");
        HeroController.instance.transform.position = pickup.transform.position;
        HeroController.instance.GetComponent<Rigidbody2D>().velocity = Vector2.zero; Physics2D.SyncTransforms();
        yield return Until(() => pickup == null || !pickup.gameObject.activeInHierarchy ||
            SceneData.instance.FindMyState(new PersistentBoolData { sceneName = "Crossroads_13", id = "Heart Piece" })?.activated == true,
            "duplicate shard actual collision runs original pickup and receipt", 20);
        yield return ResourceIdle("duplicate already-shared world shard", 25);
        ResourceCheck(pd.GetInt("heartPieces") == 1 && pd.GetInt("maxHealthBase") == 5,
            "duplicate world pickup grants air while native UI restores control");
        yield return ResourceBarrier("audit-duplicate-mask");
    }
    private IEnumerator AuditNativeWings(int expectedSlots) {
        if (ProgressAuditInspect) {
            Modding.Delegates.GetBoolProxy reveal = (key, value) => key == "hasDoubleJump" ? false : value;
            Modding.ModHooks.GetPlayerBoolHook += reveal;
            try {
                yield return Room("Abyss_21");
                foreach (var f in Object.FindObjectsOfType<PlayMakerFSM>(true).Where(f => f.gameObject.scene.name == "Abyss_21")) {
                    Mark("AUDIT WINGS ALL " + f.name + "/" + f.FsmName + " active=" + f.gameObject.activeInHierarchy +
                        " components=" + string.Join(",", f.GetComponents<Component>().Where(c => c != null).Select(c => c.GetType().Name)));
                    if (f.name.IndexOf("jump", StringComparison.OrdinalIgnoreCase) >= 0 || f.name.IndexOf("shiny", StringComparison.OrdinalIgnoreCase) >= 0 ||
                        f.name.IndexOf("wings", StringComparison.OrdinalIgnoreCase) >= 0) Dump(f);
                }
            } finally { Modding.ModHooks.GetPlayerBoolHook -= reveal; }
            yield break;
        }
        yield return Room("Abyss_21");
        var pd = PlayerData.instance;
        foreach (var f in Object.FindObjectsOfType<PlayMakerFSM>(true).Where(f => f.gameObject.scene.name == "Abyss_21" &&
            f.FsmStates.SelectMany(s => s.Actions).OfType<SetPlayerDataBool>().Any(a => a.boolName.Value == "hasDoubleJump"))) {
            Mark("AUDIT WINGS " + f.name + "/" + f.FsmName + " active=" + f.gameObject.activeInHierarchy + " state=" + f.ActiveStateName);
            Dump(f);
        }
        if (host) {
            var pickup = Object.FindObjectsOfType<PlayMakerFSM>(true).Single(f => f.gameObject.scene.name == "Abyss_21" &&
                f.FsmStates.SelectMany(s => s.Actions).OfType<SetPlayerDataBool>().Any(a => a.boolName.Value == "hasDoubleJump"));
            ResourceCheck(pd.GetBool("hasDoubleJump") && pickup.gameObject.activeInHierarchy,
                "shared wings ownership does not hide untouched native wings pickup");
            pd.SetInt("charmSlots", expectedSlots - 1);
            HeroController.instance.AffectedByGravity(true);
            HeroController.instance.transform.position = pickup.transform.position;
            HeroController.instance.GetComponent<Rigidbody2D>().velocity = Vector2.zero; Physics2D.SyncTransforms();
            yield return Until(() => !HeroController.instance.acceptingInput || HeroController.instance.controlReqlinquished,
                "actual native wings pickup takes control");
            Flag("host.audit.native-active");
            var deadline = Time.realtimeSinceStartup + 45;
            var previousState = "";
            var nextConfirm = Time.realtimeSinceStartup + 2;
            while (pickup != null && pickup.ActiveStateName != "Return Control" && Time.realtimeSinceStartup < deadline) {
                if (previousState != pickup.ActiveStateName) {
                    previousState = pickup.ActiveStateName;
                    Mark("AUDIT WINGS STATE " + previousState + " hero=" + HeroController.instance.transform.position +
                        " cutscene=" + pickup.FsmVariables.GetFsmGameObject("Cutscene").Value?.transform.position);
                }
                if (pickup.ActiveStateName == "Msg" && Time.realtimeSinceStartup >= nextConfirm) {
                    yield return ResourcePress(InputHandler.Instance.inputActions.jump);
                    nextConfirm = Time.realtimeSinceStartup + .5f;
                } else yield return null;
            }
            yield return Until(() => CoopMod.Statistics.NativePickups.TryGetValue("hasDoubleJump", out var n) && n == 1 &&
                HeroController.instance.acceptingInput && !HeroController.instance.controlReqlinquished && !pd.GetBool("disablePause"),
                "already-owned wings native cutscene completes and restores input", 2);
            yield return Until(() => Has("guest.audit.rejoined") && runtime.Peers.Count == 1 &&
                pd.GetInt("charmSlots") == expectedSlots && AuditIdle, "guest rejoin audits old member after native cutscene", 45);
            Freeze(); Flag("host.audit.native-complete");
        } else {
            yield return Until(() => Has("host.audit.native-active"), "host started real wings cutscene");
            engine.ClientManager.Disconnect();
            yield return Until(() => !runtime.Connected, "guest actual disconnect during native pickup");
            typeof(Hkmp.Game.Client.ClientManager).GetField("_autoConnect", ResourcePrivate).SetValue(engine.ClientManager, true);
            typeof(Hkmp.Game.Client.ClientManager).GetMethod("Connect", ResourcePrivate).Invoke(engine.ClientManager,
                new object[] { "127.0.0.1", 29650, "progress" + role });
            yield return Until(() => runtime.Connected && runtime.Peers.Count == 1 && engine.ClientManager.SceneSnapshotReady,
                "guest actual rejoin during native pickup", 20);
            Flag("guest.audit.rejoined");
            yield return Until(() => Has("host.audit.native-complete"), "host original cutscene and deferred audit complete", 95);
        }
        yield return ResourceBarrier("audit-native-wings");
        ResourceCheck(pd.GetBool("hasDash") && pd.GetBool("hasDoubleJump") && pd.GetInt("charmSlots") == expectedSlots,
            "repeat join keeps ability union and exact notch count");
        ResourceCheck(pd.GetInt("nailSmithUpgrades") == (ProgressAuditFull && !host ? 4 : 1) &&
            pd.GetInt("ore") == (ProgressAuditFull && host ? 6 : 0), "audit cannot copy a teammate's nail or refund spent ore");
    }
}
