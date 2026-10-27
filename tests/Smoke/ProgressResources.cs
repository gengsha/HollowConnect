using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HollowConnect;
using HollowConnect.Core;
using HutongGames.PlayMaker;
using HutongGames.PlayMaker.Actions;
using MonoMod.RuntimeDetour;
using Newtonsoft.Json;
using UnityEngine;
using Object = UnityEngine.Object;

namespace HollowConnect.Smoke;

// One bounded native resource scenario in a save-write-blocked memory profile.
public sealed partial class ProgressProbe {
    private const BindingFlags ResourcePrivate = BindingFlags.Instance | BindingFlags.NonPublic;
    private bool ResourceContinuationCase => !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("HOLLOWCONNECT_RESOURCE_CONTINUATION"));
    private bool ResourceAfterShopCase => Environment.GetEnvironmentVariable("HOLLOWCONNECT_RESOURCE_CONTINUATION") == "after-shop";
    private string ResourceOther => host ? "guest" : "host";
    private object ResourceService => typeof(CoopRuntime).GetFields(ResourcePrivate)
        .Single(f => f.FieldType.Name == "ResourceShareService").GetValue(runtime);
    private ResourceClaims LocalResourceClaims => (ResourceClaims)((Delegate)ResourceService.GetType()
        .GetField("local", ResourcePrivate).GetValue(ResourceService)).DynamicInvoke();
    private void ResourceCheck(bool ok, string label) {
        if (!ok) throw new Exception("Resource: " + label);
        Mark("PASS resource " + label);
    }
    private IEnumerator ResourceBarrier(string stage, float seconds = 12) {
        Flag(role + ".resource." + stage);
        yield return Until(() => Has(ResourceOther + ".resource." + stage), "resource both " + stage, seconds);
        if (Has(role + ".failed")) throw new Exception("Resource barrier failed: " + stage);
    }
    private IEnumerator ResourceIdle(string label, float seconds = 12) {
        yield return Until(() => HeroController.instance.acceptingInput && !HeroController.instance.controlReqlinquished &&
            !PlayerData.instance.GetBool("disablePause") &&
            ((System.Collections.IDictionary)ResourceService.GetType().GetField("inFlight", ResourcePrivate).GetValue(ResourceService)).Count == 0,
            "native resource UI completed " + label, seconds);
        if (Has(role + ".failed")) throw new Exception("Resource UI did not restore control: " + label);
    }
    private void ResourceSend(string id) {
        // Kind name is intentionally resolved at integration so this draft
        // does not force a protocol name before the parent's active step.
        var kind = (Kind)Enum.Parse(typeof(Kind), "ResourceOffer");
        typeof(CoopRuntime).GetMethod("Send", ResourcePrivate).Invoke(runtime,
            new object[] { new Message { Kind = kind, Key = id } });
    }
    private IEnumerator ResourceExpect(string id, int masks, int pieces, int reserve, int fragments, int ore, int slots, string label) {
        var pd = PlayerData.instance;
        string previous = null;
        yield return Until(() => {
            var actual = "claimed=" + LocalResourceClaims.Contains(id) + " maxHealthBase=" + pd.GetInt("maxHealthBase") +
                " heartPieces=" + pd.GetInt("heartPieces") + " MPReserveMax=" + pd.GetInt("MPReserveMax") +
                " vesselFragments=" + pd.GetInt("vesselFragments") + " ore=" + pd.GetInt("ore") + " slots=" + pd.GetInt("charmSlots");
            if (actual != previous) { Mark("RESOURCE COUNTERS " + label + " " + actual); previous = actual; }
            return LocalResourceClaims.Contains(id) && pd.GetInt("maxHealthBase") == masks &&
            pd.GetInt("heartPieces") == pieces && pd.GetInt("MPReserveMax") == reserve &&
            pd.GetInt("vesselFragments") == fragments && pd.GetInt("ore") == ore && pd.GetInt("charmSlots") == slots;
        },
            "source and native counters " + label, 10);
        if (Has(role + ".failed")) throw new Exception("Resource counters mismatch: " + label);
    }

    private IEnumerator Resources() {
        if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable("HOLLOWCONNECT_SHOP_SHARD_CASE"))) {
            yield return ShopShardRaceChecks(); yield break;
        }
        var pd = PlayerData.instance;
        // Only fixture setup sets initial counters; all measured grants use
        // original pickup/upgrade FSM actions or the production UDP receiver.
        pd.SetInt("heartPieces", 0); pd.SetInt("vesselFragments", 0); pd.SetInt("ore", 0);
        pd.SetInt("geo", 30000); pd.SetInt("charmsOwned", 6);
        for (var i = 1; i <= 6; i++) pd.SetBool("gotCharm_" + i, true);
        pd.SetBool("metCharmSlug", true); pd.SetBool("metNailsmith", true);
        pd.SetInt("nailSmithUpgrades", 1); pd.SetInt("nailDamage", 9); pd.SetBool("honedNail", true);
        ResourceCheck(pd.GetInt("maxHealthBase") == 5 && pd.GetInt("MPReserveMax") == 0 && pd.GetInt("charmSlots") == 3 &&
            LocalResourceClaims.Claimed.Count == 0, "fresh unsaved slot has no imported resources");
        if (ResourceContinuationCase) {
            // Short continuation of the separately recorded world/source run.
            // This initial balance is fixture setup, not another tested grant.
            pd.SetInt("heartPieces", 1);
            Mark("RESOURCE CONTINUATION skips save/world checks; starts with one fixture shard");
        } else {
            yield return ResourceSaveIsolation();
            yield return ResourceWorldConcurrent();
        }
        if (ResourceAfterShopCase) {
            pd.SetInt("charmSlots", 4);
            Mark("RESOURCE CONTINUATION also excludes shop; initial slots=4 fixture only");
            // The native instant reward prefabs are loaded by this scene;
            // excluding its UI scenario must not exclude the asset lifetime.
            yield return Room("Room_Charm_Shop");
            ResourceCheck(new[] { "Heart Piece Instant", "Vessel Fragment Instant" }.All(name =>
                UnityEngine.Resources.FindObjectsOfTypeAll<GameObject>().Any(g => g.name == name && !g.scene.IsValid())),
                "original reward prefabs loaded before after-shop continuation");
        } else yield return ResourceShop();
        yield return ResourceConversionsAndOre();
        yield return ResourcePersonalNails();
        yield return ResourcePersistenceReconnect();
        Mark(ResourceAfterShopCase
            ? "FINAL PASS resource after-shop continuation: conversion, personal ore spending and reconnect; save/world/shop cases excluded"
            : ResourceContinuationCase
            ? "FINAL PASS resource continuation: native shop/stale confirmation, conversion, personal ore spending and reconnect; save/world cases excluded"
            : "FINAL PASS resources: native world/shop, source replay/concurrency, conversion, personal ore spending and reconnect");
    }
    private IEnumerator ResourceSaveIsolation() {
        var manager = Field<Hkmp.Game.Client.Save.SaveManager>(engine.ClientManager, "_saveManager");
        var mapping = Hkmp.Game.Client.Save.SaveDataMapping.Instance;
        var inbound = manager.GetType().GetMethod("UpdateSaveWithData", ResourcePrivate, null, new[] { typeof(ushort), typeof(byte[]) }, null);
        var hosting = manager.IsHostingServer; manager.IsHostingServer = false;
        try {
            foreach (var key in new[] { "ore", "charmSlots", "heartPieces", "vesselFragments", "MPReserveMax", "maxHealthBase", "nailSmithUpgrades", "nailDamage" }) {
                var before = PlayerData.instance.GetInt(key);
                ResourceCheck(mapping.PlayerDataIndices.TryGetValue(key, out var index), "native full-save mapping exists " + key);
                inbound.Invoke(manager, new object[] { index, Hkmp.Util.EncodeUtil.EncodeSaveDataValue(before + 10) });
                ResourceCheck(PlayerData.instance.GetInt(key) == before, "full-save replay preserves this slot's resource/nail value " + key);
            }
            foreach (var key in new[] { "hasDash", "hasDoubleJump", "gotCharm_6" }) {
                var before = PlayerData.instance.GetBool(key);
                inbound.Invoke(manager, new object[] { mapping.PlayerDataIndices[key], Hkmp.Util.EncodeUtil.EncodeSaveDataValue(!before) });
                ResourceCheck(PlayerData.instance.GetBool(key) == before, "full-save replay cannot bypass pickup ownership adapter " + key);
            }
            var spell = PlayerData.instance.GetInt("fireballLevel");
            inbound.Invoke(manager, new object[] { mapping.PlayerDataIndices["fireballLevel"], Hkmp.Util.EncodeUtil.EncodeSaveDataValue(spell + 1) });
            ResourceCheck(PlayerData.instance.GetInt("fireballLevel") == spell, "full-save replay cannot bypass native spell level adapter");
            var vessel = new Hkmp.Game.Client.Save.PersistentItemKey { SceneName = "Deepnest_38", Id = "Vessel Fragment" };
            ResourceCheck(mapping.PersistentBoolVarProperties[vessel].SyncType == Hkmp.Game.Client.Save.SaveDataMapping.SyncType.Player,
                "Deepnest vessel has personal native save classification");
            ResourceCheck(mapping.PersistentBoolIndices.TryGetValue(vessel, out var vesselIndex), "native full-save vessel mapping exists");
            inbound.Invoke(manager, new object[] { vesselIndex, new byte[] { 1 } });
            ResourceCheck(SceneData.instance.FindMyState(new PersistentBoolData { sceneName = "Deepnest_38", id = "Vessel Fragment" })?.activated != true,
                "full-save replay cannot prehide or seed an unreceived source");
        } finally { manager.IsHostingServer = hosting; }
        // Labeled lifecycle fault fixture: a pickup intent is canceled before
        // any numerical award. It must leave inFlight once control is idle,
        // without recording a receipt or blocking future remote resources.
        ResourceCatalog.TryGet("ore:dreamReward3", out var source);
        ResourceService.GetType().GetMethod("BeginNativeFlow", ResourcePrivate).Invoke(ResourceService, new object[] { source });
        yield return ResourceIdle("canceled unmutated intent", 3);
        ResourceCheck(!LocalResourceClaims.Contains(source.Id), "canceled native intent clears without inventing an acquisition");
        yield return ResourceBarrier("save-isolation");
    }

    private IEnumerator ResourceWorldConcurrent() {
        const string id = "mask:Crossroads_13/Heart Piece";
        yield return Room("Crossroads_13");
        var pd = PlayerData.instance;
        var pickup = Object.FindObjectsOfType<PlayMakerFSM>(true).Single(f => f.gameObject.scene.name == "Crossroads_13" &&
            f.name == "Heart Piece" && f.FsmName == "Heart Container Control");
        Dump(pickup);
        var persistent = pickup.GetComponent<PersistentBoolItem>();
        ResourceCheck(persistent != null && pickup.GetComponentsInChildren<Collider2D>(true).Any(c => c.isTrigger),
            "inspected actual world shard has its own persistent identity and native trigger");
        // Hold only resource claims. Presence, original control and native
        // pickup collision continue. Both native grants must occur before any
        // claim is released; this detects simultaneous-source double awards.
        var held = new List<Message>(); var release = false;
        using (new Hook(typeof(CoopRuntime).GetMethod("Send", ResourcePrivate),
            (Action<Action<CoopRuntime, Message>, CoopRuntime, Message>)((orig, self, message) => {
                if (!release && message.Kind.ToString() == "ResourceOffer") held.Add(message); else orig(self, message);
            }))) {
            yield return Until(() => pickup.gameObject.activeInHierarchy && pickup.ActiveStateName == "Idle", "world shard native pickup idle", 5);
            HeroController.instance.transform.position = pickup.transform.position;
            HeroController.instance.GetComponent<Rigidbody2D>().velocity = Vector2.zero;
            Physics2D.SyncTransforms();
            yield return Until(() => pd.GetInt("heartPieces") == 1 && LocalResourceClaims.Contains(id), "actual world collision awards one native shard", 8);
            yield return ResourceIdle("world shard");
            yield return ResourceBarrier("world-both-native");
            release = true;
            ResourceCheck(held.Any(m => m.Key == id), "native shard queued a production resource offer");
            foreach (var message in held) typeof(CoopRuntime).GetMethod("Send", ResourcePrivate).Invoke(runtime, new object[] { message });
            ResourceSend(id); ResourceSend(id);
            yield return new WaitForSecondsRealtime(.5f);
            ResourceCheck(pd.GetInt("heartPieces") == 1 && pd.GetInt("maxHealthBase") == 5,
                "simultaneous source union and repeated UDP offers do not duplicate native shard");
        }
        yield return ResourceBarrier("world-complete");
    }

    private PlayMakerFSM ResourceFsm(string owner, string fsm) => Object.FindObjectsOfType<PlayMakerFSM>(true)
        .Single(f => f.gameObject.scene.name == GameManager.instance.sceneName && f.name == owner && f.FsmName == fsm);
    private IEnumerator ResourceOpenSalubra() {
        var region = ResourceFsm("Shop Region", "Shop Region");
        HeroController.instance.transform.position = region.transform.position;
        region.SetState("Take Control"); // native interaction entrance, not payment or award state
        var list = ResourceFsm("Item List", "Item List Control");
        var input = ResourceFsm("Item List", "ui_list_getinput");
        var shop = ResourceFsm("Shop Menu", "shop_control");
        // Item List reaches Idle while the parent still runs its opening
        // animation. UI ACTIVE, and therefore its input listeners, come later.
        yield return Until(() => list.gameObject.activeInHierarchy && list.ActiveStateName == "Idle" &&
            input.ActiveStateName == "Idle" && shop.ActiveStateName == "Open" && region.ActiveStateName == "Shop Up",
            "Salubra original shop UI open and listening", 12);
        if (Has(role + ".failed")) throw new Exception("Salubra native shop failed to open");
    }
    private IEnumerator ResourceCloseSalubra() {
        var list = ResourceFsm("Item List", "Item List Control");
        var confirm = ResourceFsm("UI List", "Confirm Control");
        var input = ResourceFsm("Item List", "ui_list_getinput");
        var shop = ResourceFsm("Shop Menu", "shop_control");
        var region = ResourceFsm("Shop Region", "Shop Region");
        yield return Until(() => list.ActiveStateName == "Idle" && !confirm.gameObject.activeInHierarchy &&
            input.ActiveStateName == "Idle" && shop.ActiveStateName == "Open" && region.ActiveStateName == "Shop Up",
            "native shop confirmation returned to listening item list", 5);
        var cancel = InputHandler.Instance.inputActions.menuCancel;
        var cancelObserved = false;
        using (new Hook(typeof(ListenForMenuActions).GetMethod("OnUpdate"),
            (Action<Action<ListenForMenuActions>, ListenForMenuActions>)((orig, action) => {
                if (action.Fsm == shop.Fsm && action.State.Name == "Open" && cancel.WasPressed) {
                    cancelObserved = true;
                    Mark("RESOURCE SHOP native Open listener received cancel");
                }
                orig(action);
            })))
            yield return ResourcePress(cancel);
        ResourceCheck(cancelObserved, "Salubra cancel reached the original Open listener");
        // The listener must drive Open -> Down -> Send Event -> SHOP CLOSED;
        // Shop Region owns Regain Control. Special Close/Inactive bypass it.
        yield return Until(() => shop.ActiveStateName == "Idle" && !list.gameObject.activeInHierarchy &&
            region.ActiveStateName != "Shop Up", "Salubra native close animation completed", 5);
        yield return ResourceIdle("native shop cancel");
    }
    private IEnumerator ResourcePress(InControl.PlayerAction action) {
        var frame = Time.frameCount + 1;
        using (new Hook(typeof(InControl.OneAxisInputControl).GetProperty("WasPressed").GetGetMethod(),
            new Func<Func<InControl.OneAxisInputControl, bool>, InControl.OneAxisInputControl, bool>((orig, self) => ReferenceEquals(self, action) ? Time.frameCount == frame : orig(self))))
        using (new Hook(typeof(InControl.OneAxisInputControl).GetProperty("IsPressed").GetGetMethod(),
            new Func<Func<InControl.OneAxisInputControl, bool>, InControl.OneAxisInputControl, bool>((orig, self) => ReferenceEquals(self, action) ? Time.frameCount >= frame : orig(self))))
            yield return new WaitForSecondsRealtime(.15f);
    }
    private IEnumerator ResourceShop() {
        const string id = "notch:salubraNotch1";
        yield return Room("Room_Charm_Shop");
        var pd = PlayerData.instance; var beforeGeo = pd.GetInt("geo");
        // Both confirmations are opened through the real Item List. In
        // particular the recipient's Confirm Control must complete Awake/Init;
        // SetState on its never-activated prefab is not a native stale prompt.
        yield return ResourceOpenSalubra();
        var list = ResourceFsm("Item List", "Item List Control");
        var stock = list.GetComponent<ShopMenuStock>();
        var index = Array.FindIndex(stock.stockInv, g => g != null && g.GetComponent<ShopItemStats>().playerDataBoolName == "salubraNotch1");
        var cost = index >= 0 ? stock.GetCost(index) : 0;
        Mark("RESOURCE SHOP stock=" + string.Join(",", stock.stockInv.Where(g => g != null).Select(g => g.GetComponent<ShopItemStats>().playerDataBoolName)) +
            " selected=" + index + " charms=" + pd.GetInt("charmsOwned") + " geo=" + beforeGeo + " cost=" + cost);
        ResourceCheck(index >= 0 && stock.CanBuy(index), "Salubra notch is available for its actual native price");
        list.FsmVariables.GetFsmInt("Current Item").Value = index;
        list.SetState("Get Details");
        yield return Until(() => list.ActiveStateName == "Idle", "native shop selected notch");
        list.SendEvent("UI CONFIRM");
        var confirm = ResourceFsm("UI List", "Confirm Control");
        yield return Until(() => confirm.gameObject.activeInHierarchy && confirm.ActiveStateName == "Listen", "native notch confirmation prompt");
        ResourceCheck(confirm.FsmVariables.GetFsmString("PD Bool Name").Value == "salubraNotch1" &&
            confirm.FsmVariables.GetFsmInt("Cost").Value == cost, "native confirmation carries the selected source and cost");
        yield return ResourceBarrier("shop-prompts-open");
        if (!host) {
            yield return Until(() => ((HashSet<string>)ResourceService.GetType().GetField("queued", ResourcePrivate)
                .GetValue(ResourceService)).Contains(id), "shared source arrives while original confirmation is open", 12);
            ResourceCheck(!LocalResourceClaims.Contains(id) && pd.GetInt("charmSlots") == 3,
                "remote notch waits for the original UI instead of preempting its counters");
        }
        // Trace the actual native deduction entry rather than a synthetic
        // uninitialized state. The production guard may redirect it to Reset.
        On.HutongGames.PlayMaker.Fsm.hook_EnterState trace = (orig, fsm, state) => {
            if (ReferenceEquals(fsm, confirm.Fsm) && state.Name == "Deduct Geo and set PD") {
                Mark("RESOURCE SHOP deduction before flag=" + fsm.Variables.GetFsmString("PD Bool Name").Value +
                    " claimed=" + LocalResourceClaims.Contains(id) + " queued=" +
                    ((HashSet<string>)ResourceService.GetType().GetField("queued", ResourcePrivate).GetValue(ResourceService)).Contains(id) +
                    " geo=" + pd.GetInt("geo") + " slots=" + pd.GetInt("charmSlots"));
                orig(fsm, state);
                Mark("RESOURCE SHOP deduction after state=" + fsm.ActiveStateName + " geo=" + pd.GetInt("geo") +
                    " slots=" + pd.GetInt("charmSlots"));
            } else orig(fsm, state);
        };
        On.HutongGames.PlayMaker.Fsm.EnterState += trace;
        try {
            var selection = ResourceFsm("UI List", "ui_list");
            selection.FsmVariables.GetFsmInt("Current Item").Value = 1; // live IntSwitch maps 1 to YES
            confirm.SendEvent("UI SELECTION MADE");
            if (host)
                yield return Until(() => pd.GetInt("charmSlots") == 4 && LocalResourceClaims.Contains(id), "real Salubra purchase grants one notch", 8);
            yield return ResourceCloseSalubra();
        } finally { On.HutongGames.PlayMaker.Fsm.EnterState -= trace; }
        yield return Until(() => pd.GetInt("charmSlots") == 4 && LocalResourceClaims.Contains(id), "notch received exactly once after original UI releases control", 8);
        Mark("RESOURCE SHOP final geo=" + pd.GetInt("geo") + " slots=" + pd.GetInt("charmSlots") + " claimed=" + LocalResourceClaims.Contains(id));
        ResourceCheck(beforeGeo - pd.GetInt("geo") == (host ? cost : 0), host
            ? "only original buyer pays the original shop cost"
            : "stale native confirmation cannot charge or award a repeated source");
        yield return ResourceBarrier("shop-granted");
        yield return ResourceOpenSalubra();
        var itemList = ResourceFsm("Item List", "Item List Control");
        ResourceCheck(!itemList.GetComponent<ShopMenuStock>().stockInv.Any(g => g != null && g.GetComponent<ShopItemStats>().playerDataBoolName == "salubraNotch1"),
            "shared native source removed from current shop stock without a room reload");
        yield return ResourceCloseSalubra();
        ResourceSend(id); ResourceSend(id);
        yield return new WaitForSecondsRealtime(.4f);
        ResourceCheck(pd.GetInt("geo") == beforeGeo - (host ? cost : 0) && pd.GetInt("charmSlots") == 4, "notch replay leaves money and slots unchanged");
        yield return ResourceBarrier("shop-complete");
    }

    private IEnumerator ResourceTaggedInstant(string id, string prefabName) {
        // Explicit fixture identity: native instant prefab and original grant
        // actions remain intact. It is not evidence for a second world source.
        var prefab = UnityEngine.Resources.FindObjectsOfTypeAll<GameObject>().First(g => g.name == prefabName && !g.scene.IsValid());
        var staging = new GameObject("Resource probe inactive staging"); staging.SetActive(false);
        var clone = Object.Instantiate(prefab, staging.transform, false);
        var tagType = typeof(CoopRuntime).Assembly.GetType("HollowConnect.ResourceSourceTag", true);
        var tag = clone.AddComponent(tagType);
        tagType.GetField("SourceId", ResourcePrivate).SetValue(tag, id);
        clone.transform.position = HeroController.instance.transform.position;
        clone.SetActive(true); clone.transform.SetParent(null, true); Object.Destroy(staging);
        Mark("RESOURCE FIXTURE native " + prefabName + " tagged " + id);
        yield return null; yield return null;
        foreach (var fsm in clone == null ? Array.Empty<PlayMakerFSM>() : clone.GetComponents<PlayMakerFSM>()) Dump(fsm);
        yield return Until(() => LocalResourceClaims.Contains(id), "tagged original instant grant " + id, 6);
        yield return ResourceIdle("tagged instant " + id);
    }
    private IEnumerator ResourceConversionsAndOre() {
        // Two real native instant grant endpoints bring both local counters to
        // three pieces; a fourth missing source exercises remote conversion.
        yield return ResourceTaggedInstant("mask:slyShellFrag1", "Heart Piece Instant");
        yield return ResourceTaggedInstant("mask:slyShellFrag2", "Heart Piece Instant");
        ResourceCheck(PlayerData.instance.GetInt("heartPieces") == 3, "three original shards before remote rollover");
        yield return ResourceTaggedInstant("mask:slyShellFrag1", "Heart Piece Instant");
        ResourceCheck(PlayerData.instance.GetInt("heartPieces") == 3 && PlayerData.instance.GetInt("maxHealthBase") == 5,
            "repeated native mask animation cannot reset fragments or award another complete mask: pieces=" + PlayerData.instance.GetInt("heartPieces") + " max=" + PlayerData.instance.GetInt("maxHealthBase"));
        yield return ResourceBarrier("mask-at-three");
        if (!host) {
            Enter("Town"); yield return Until(() => AliveScene("Town"), "recipient enters another room before direct shared mask settlement"); Freeze();
            Flag("guest.resource.remote-mask-room");
        } else yield return Until(() => Has("guest.resource.remote-mask-room"), "recipient is in another room for shared mask");
        if (host) yield return ResourceTaggedInstant("mask:slyShellFrag3", "Heart Piece Instant");
        yield return ResourceExpect("mask:slyShellFrag3", 6, 0, 0, 0, 0, 4, "mask 3 to 4 conversion");
        yield return ResourceIdle("mask conversion");
        if (!host) ResourceCheck(!PlayerData.instance.GetBool("slyShellFrag3"), "direct remote settlement leaves the local shop item available");
        ResourceSend("mask:slyShellFrag3");
        // A remote numerical rollover completes before the buyer's original
        // UI. Keep the next source family from changing the other counters
        // while that buyer still verifies the current family.
        yield return ResourceBarrier("mask-complete");
        yield return Room("Room_Charm_Shop");
        yield return ResourceTaggedInstant("vessel:slyVesselFrag1", "Vessel Fragment Instant");
        yield return ResourceTaggedInstant("vessel:slyVesselFrag2", "Vessel Fragment Instant");
        ResourceCheck(PlayerData.instance.GetInt("vesselFragments") == 2, "two original vessel fragments before remote rollover");
        yield return ResourceTaggedInstant("vessel:slyVesselFrag1", "Vessel Fragment Instant");
        ResourceCheck(PlayerData.instance.GetInt("vesselFragments") == 2 && PlayerData.instance.GetInt("MPReserveMax") == 0,
            "repeated native vessel animation cannot reset fragments or award another soul vessel");
        yield return ResourceBarrier("vessel-at-two");
        if (host) yield return ResourceTaggedInstant("vessel:dreamReward5", "Vessel Fragment Instant");
        yield return ResourceExpect("vessel:dreamReward5", 6, 0, 33, 0, 0, 4, "vessel 2 to 3 conversion");
        yield return ResourceIdle("vessel conversion");
        ResourceSend("vessel:dreamReward5");
        yield return ResourceBarrier("vessel-complete");
        // Ore sources are synthetic network-source fixtures here. Actual
        // pickup identity was independently observed in the earlier world case.
        if (host) foreach (var id in ResourceOreSources) ResourceSend(id);
        yield return ResourceExpect(ResourceOreSources[2], 6, 0, 33, 0, 3, 4, "three distinct shared ore sources");
        yield return ResourceBarrier("conversions-and-ore");
    }
    private static readonly string[] ResourceOreSources = { "ore:dreamReward3", "ore:Mines_34/Shiny Item Stand", "ore:Deepnest_32/Shiny Item Stand" };

    private IEnumerator ResourceNailUpgrade(int expectedLevel, int expectedOre) {
        var pd = PlayerData.instance; var smith = ResourceFsm("Nailsmith", "Conversation Control");
        var control = ResourceFsm("Nailsmith", "npc_control");
        HeroController.instance.transform.position = smith.transform.position + Vector3.left * 2;
        control.SetState("Take Control");
        Dump(smith);
        var offerDeadline = Time.realtimeSinceStartup + 10;
        while (!(smith.ActiveStateName == "Send Text" && Dialogue.ActiveStateName == "Ready for Input") && Time.realtimeSinceStartup < offerDeadline) {
            ResourceAdvanceText(smith);
            yield return null;
        }
        ResourceCheck(smith.ActiveStateName == "Send Text" && Dialogue.ActiveStateName == "Ready for Input", "original Nailsmith offer");
        ChooseYes();
        var upgradeDeadline = Time.realtimeSinceStartup + 18;
        while (!(pd.GetInt("nailSmithUpgrades") == expectedLevel && pd.GetInt("ore") == expectedOre) && Time.realtimeSinceStartup < upgradeDeadline) {
            ResourceAdvanceText(smith);
            yield return null;
        }
        ResourceCheck(pd.GetInt("nailSmithUpgrades") == expectedLevel && pd.GetInt("ore") == expectedOre,
            "native personal nail upgrade and ore cost " + expectedLevel);
        // Only dismiss text/advance original dialogue. Never set Upgrade or
        // invoke its integer actions directly; cost and cinematic stay native.
        var deadline = Time.realtimeSinceStartup + 10;
        while (!HeroController.instance.acceptingInput && Time.realtimeSinceStartup < deadline) {
            ResourceAdvanceText(smith);
            yield return null;
        }
        yield return ResourceIdle("Nailsmith " + expectedLevel);
        ResourceCheck(pd.GetInt("nailDamage") == 5 + 4 * expectedLevel, "native nail damage matches only this player's upgrade");
    }
    private void ResourceAdvanceText(PlayMakerFSM conversation) {
        if (conversation.FsmStates.Single(s => s.Name == conversation.ActiveStateName).Transitions.Any(t => t.EventName == "CONVO_FINISH"))
            conversation.SendEvent("CONVO_FINISH");
    }
    private IEnumerator ResourcePersonalNails() {
        yield return Room("Room_nailsmith");
        foreach (var upgrader in new[] { "host", "guest" }) {
            var stage = "nail-level-2." + upgrader;
            if (role == upgrader) { yield return ResourceNailUpgrade(2, 2); Flag(upgrader + ".resource." + stage + ".finished"); }
            else yield return Until(() => Has(upgrader + ".resource." + stage + ".finished"), "peer finishes first personal upgrade", 30);
            ResourceCheck(PlayerData.instance.GetInt("nailSmithUpgrades") == (role == upgrader || upgrader == "guest" ? 2 : 1) &&
                PlayerData.instance.GetInt("ore") == (role == upgrader || upgrader == "guest" ? 2 : 3),
                "one player's first upgrade cannot change teammate level or ore " + upgrader);
            yield return ResourceBarrier(stage);
        }
        foreach (var upgrader in new[] { "host", "guest" }) {
            var stage = "nail-level-3." + upgrader;
            if (role == upgrader) { yield return ResourceNailUpgrade(3, 0); Flag(upgrader + ".resource." + stage + ".finished"); }
            else yield return Until(() => Has(upgrader + ".resource." + stage + ".finished"), "peer finishes second personal upgrade", 30);
            ResourceCheck(PlayerData.instance.GetInt("nailSmithUpgrades") == (role == upgrader || upgrader == "guest" ? 3 : 2) &&
                PlayerData.instance.GetInt("ore") == (role == upgrader || upgrader == "guest" ? 0 : 2),
                "three original ore buys two personal upgrades without sharing upgrades " + upgrader);
            yield return ResourceBarrier(stage);
        }
    }
    private IEnumerator ResourcePersistenceReconnect() {
        var claims = LocalResourceClaims;
        // Exercise the production local-settings serializer shape rather than
        // writing the disposable character or a user's profile to disk.
        var json = JsonConvert.SerializeObject(CoopMod.Statistics);
        var restored = JsonConvert.DeserializeObject<DeathStatistics>(json).Copy();
        var property = typeof(DeathStatistics).GetField(nameof(DeathStatistics.Resources));
        var roundtrip = (ResourceClaims)property.GetValue(restored);
        ResourceCheck(claims.Claimed.SetEquals(roundtrip.Claimed), "existing slot-local JSON roundtrip retains validated source receipts");
        var count = claims.Claimed.Count;
        yield return ResourceReconnectSameProfile();
        foreach (var source in ResourceOreSources) { ResourceSend(source); ResourceSend(source); }
        yield return new WaitForSecondsRealtime(.6f);
        ResourceCheck(PlayerData.instance.GetInt("ore") == 0 && PlayerData.instance.GetInt("nailSmithUpgrades") == 3 &&
            PlayerData.instance.GetInt("nailDamage") == 17 && LocalResourceClaims.Claimed.Count == count,
            "real reconnect and replay cannot refund consumed ore or alter personal nail");
        yield return ResourceBarrier("reconnect-complete");
    }
    private IEnumerator ResourceReconnectSameProfile() {
        foreach (var actor in new[] { "guest", "host" }) {
            var oldClaims = LocalResourceClaims;
            var oldHero = HeroController.instance;
            if (role == actor) {
                engine.ClientManager.Disconnect();
                yield return Until(() => !runtime.Connected, "resource real network disconnect " + actor, 6);
                Flag(actor + ".resource.offline");
                yield return Until(() => Has(actor + ".resource.disconnect-seen"), "resource survivor observed removal " + actor, 6);
                typeof(Hkmp.Game.Client.ClientManager).GetField("_autoConnect", ResourcePrivate).SetValue(engine.ClientManager, true);
                typeof(Hkmp.Game.Client.ClientManager).GetMethod("Connect", ResourcePrivate).Invoke(engine.ClientManager,
                    new object[] { "127.0.0.1", 29650, "progress" + role });
                yield return Until(() => runtime.Connected && runtime.Peers.Count == 1 && engine.ClientManager.SceneSnapshotReady,
                    "resource same-profile real reconnect " + actor, 10);
                ResourceCheck(oldHero == HeroController.instance && ReferenceEquals(oldClaims, LocalResourceClaims),
                    "reconnect retained the loaded profile and its existing source ledger " + actor);
                Flag(actor + ".resource.reconnected");
            } else {
                yield return Until(() => Has(actor + ".resource.offline") && runtime.Peers.Count == 0,
                    "resource actual roster removed disconnecting peer " + actor, 8);
                Flag(actor + ".resource.disconnect-seen");
                yield return Until(() => Has(actor + ".resource.reconnected") && runtime.Peers.Count == 1,
                    "resource survivor sees peer after real reconnect " + actor, 12);
            }
            yield return ResourceBarrier("reconnected." + actor);
        }
    }
}
