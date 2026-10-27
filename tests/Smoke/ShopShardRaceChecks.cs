using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using HollowConnect;
using HutongGames.PlayMaker;
using UnityEngine;
using Object = UnityEngine.Object;

namespace HollowConnect.Smoke;

// Both real shop FSMs and UDP resource delivery in unsaved isolated profiles.
public sealed partial class ProgressProbe {
    private IEnumerator ShopShardRaceChecks() {
        var scenario = Environment.GetEnvironmentVariable("HOLLOWCONNECT_SHOP_SHARD_CASE");
        var vessel = scenario.EndsWith("vessel", StringComparison.Ordinal);
        var settled = scenario.StartsWith("settled-", StringComparison.Ordinal);
        var simultaneous = scenario.StartsWith("simultaneous-", StringComparison.Ordinal);
        var pd = PlayerData.instance;
        var field = vessel ? "vesselFragments" : "heartPieces";
        var flag = vessel ? "slyVesselFrag1" : "slyShellFrag1";
        var id = (vessel ? "vessel:" : "mask:") + flag;
        var initial = Environment.GetEnvironmentVariable("HOLLOWCONNECT_SHOP_SHARD_INITIAL") == "zero" ? 0 : vessel ? 2 : 3;
        pd.SetInt("heartPieces", vessel ? 0 : initial); pd.SetInt("vesselFragments", vessel ? initial : 0);
        pd.SetInt("geo", 30000); pd.SetBool("slyRescued", true); pd.SetBool("metSlyShop", true);
        ResourceCheck(LocalResourceClaims.Claimed.Count == 0 && pd.GetInt("maxHealthBase") == 5 && pd.GetInt("MPReserveMax") == 0,
            "shop audit starts from unsaved empty resource history");
        Mark("SHOP SHARD CASE " + scenario + " initial=" + initial + " id=" + id);
        if (scenario.StartsWith("world-", StringComparison.Ordinal)) {
            yield return DuplicateWorldShardChecks(vessel, initial); yield break;
        }
        yield return Room("Room_shop");
        foreach (var f in Object.FindObjectsOfType<PlayMakerFSM>(true).Where(f => f.gameObject.scene.name == "Room_shop" &&
            new[] { "Shop Region", "Shop Menu", "Item List", "UI List" }.Contains(f.name))) Dump(f);
        var displays = 0; var cleanups = 0;
        On.HutongGames.PlayMaker.Fsm.hook_EnterState trace = (orig, fsm, state) => {
            if (fsm.Name == (vessel ? "Vessel Fragment UI" : "Heart Container UI")) {
                if (state.Name == "Fleur") displays++;
                if (state.Name == "Destroy Self") cleanups++;
            }
            var name = fsm.GameObject == null ? "" : fsm.GameObject.name;
            if (name.Contains("Heart") || name.Contains("Vessel") || fsm.Name == "Confirm Control" ||
                name == "Shop Region" || name == "Shop Menu") {
                File.AppendAllText(Path.Combine(root, role + ".shop-trace.txt"), DateTime.UtcNow.ToString("O") + " " +
                    name + "/" + fsm.Name + " -> " + state.Name + " pieces=" + pd.GetInt(field) +
                    " hp=" + pd.GetInt("maxHealthBase") + " reserve=" + pd.GetInt("MPReserveMax") +
                    " input=" + HeroController.instance.acceptingInput + " relinquished=" + HeroController.instance.controlReqlinquished + "\n");
            }
            orig(fsm, state);
            if (name == "Vessel Fragment UI(Clone)" && state.Name == "Fleur") {
                Dump(fsm.GameObject.GetComponents<PlayMakerFSM>().Single(f => ReferenceEquals(f.Fsm, fsm)));
            }
        };
        On.HutongGames.PlayMaker.Fsm.EnterState += trace;
        try {
            if (!settled || host) yield return ShopShardOpenPrompt(flag);
            yield return ResourceBarrier("shard-prompts");
            if (!host && settled) {
                yield return Until(() => LocalResourceClaims.Contains(id), "guest receives shared shard before shopping", 25);
                yield return ResourceIdle("shared shard before local purchase", 25);
                Mark("SHOP SHARD settled counters pieces=" + pd.GetInt(field) + " hp=" + pd.GetInt("maxHealthBase") + " reserve=" + pd.GetInt("MPReserveMax"));
                yield return ShopShardOpenPrompt(flag);
            } else if (!host && !simultaneous) {
                yield return Until(() => ((HashSet<string>)ResourceService.GetType().GetField("queued", ResourcePrivate).GetValue(ResourceService)).Contains(id),
                    "host shard queued while guest confirmation open", 15);
            }
            var geo = pd.GetInt("geo");
            var confirm = ResourceFsm("UI List", "Confirm Control");
            var cost = confirm.FsmVariables.GetFsmInt("Cost").Value;
            ResourceFsm("UI List", "ui_list").FsmVariables.GetFsmInt("Current Item").Value = 1;
            Mark("SHOP SHARD confirm YES claimed=" + LocalResourceClaims.Contains(id) + " queued=" +
                ((HashSet<string>)ResourceService.GetType().GetField("queued", ResourcePrivate).GetValue(ResourceService)).Contains(id));
            confirm.SendEvent("UI SELECTION MADE");
            yield return Until(() => LocalResourceClaims.Contains(id), "native purchase claims selected shard", 10);
            yield return new WaitForSecondsRealtime(1);
            var shop = ResourceFsm("Shop Menu", "shop_control");
            if (shop.ActiveStateName == "Open") yield return ResourcePress(InputHandler.Instance.inputActions.menuCancel);
            yield return ResourceIdle("Sly same-source " + scenario, 25);
            ResourceCheck(displays == (!host && settled ? 0 : 1) && cleanups == 1,
                "first awards display normally; duplicate awards run native cleanup without fragment display");
            CheckFragmentNativeCleanup();
            var expectedPieces = (initial + 1) % (vessel ? 3 : 4);
            ResourceCheck(pd.GetInt(field) == expectedPieces && pd.GetInt("maxHealthBase") == (!vessel && initial == 3 ? 6 : 5) &&
                pd.GetInt("MPReserveMax") == (vessel && initial == 2 ? 33 : 0),
                "same-source purchase grants only one fragment and one possible conversion");
            var charge = geo - pd.GetInt("geo");
            Mark("SHOP SHARD final charge=" + charge + " cost=" + cost + " pieces=" + pd.GetInt(field));
            ResourceCheck(simultaneous ? charge == 0 || charge == cost : charge == (host ? cost : 0), "native price or shared-source free confirmation");
            ResourceCheck(!ResourceFsm("Item List", "Item List Control").gameObject.activeInHierarchy,
                "shop list closed and player can move without rejoining");
            ResourceSend(id); ResourceSend(id); yield return new WaitForSecondsRealtime(1);
            ResourceCheck(pd.GetInt(field) == expectedPieces, "source replay leaves completed purchase stable");
            yield return ResourceBarrier("shard-finished", 50);
            Mark("FINAL PASS Sly same-source shard " + scenario + " initial=" + initial);
        } finally {
            On.HutongGames.PlayMaker.Fsm.EnterState -= trace;
            foreach (var f in Object.FindObjectsOfType<PlayMakerFSM>(true).Where(f =>
                f.name.Contains("Heart") || f.name.Contains("Vessel") || f.FsmName == "Confirm Control")) Dump(f);
        }
    }

    private void CheckFragmentNativeCleanup() => ResourceCheck(!PlayerData.instance.GetBool("isInvincible") &&
        Math.Abs(HeroController.instance.GetComponent<Rigidbody2D>().gravityScale - .79f) < .01f,
        "native cleanup restores invincibility flag and gravity as well as input and pause");

    private IEnumerator DuplicateWorldShardChecks(bool vessel, int initial) {
        var scene = vessel ? "Crossroads_37" : "Crossroads_13";
        var owner = vessel ? "Vessel Fragment" : "Heart Piece";
        var fsmName = vessel ? "Vessel Fragment Control" : "Heart Container Control";
        var uiName = vessel ? "Vessel Fragment UI" : "Heart Container UI";
        var id = (vessel ? "vessel:" : "mask:") + scene + "/" + owner;
        var field = vessel ? "vesselFragments" : "heartPieces";
        var pd = PlayerData.instance;
        yield return Room(scene);
        var pickup = ResourceFsm(owner, fsmName);
        Dump(pickup);
        ResourceCheck(pickup.gameObject.activeInHierarchy && pickup.GetComponent<PersistentBoolItem>() != null,
            "real world fragment visible with its native receipt");
        var displays = 0; var cleanups = 0;
        On.HutongGames.PlayMaker.Fsm.hook_EnterState trace = (orig, fsm, state) => {
            if (fsm.Name == uiName) {
                Mark("WORLD SHARD UI " + state.Name + " pieces=" + pd.GetInt(field));
                if (state.Name == "Fleur") displays++;
                if (state.Name == "Destroy Self") cleanups++;
            }
            orig(fsm, state);
        };
        On.HutongGames.PlayMaker.Fsm.EnterState += trace;
        try {
            if (host) yield return Touch();
            else {
                yield return Until(() => LocalResourceClaims.Contains(id), "world source shared before guest collision", 25);
                yield return ResourceIdle("remote world shard settled");
                ResourceCheck(displays == 0 && pickup.gameObject.activeInHierarchy,
                    "remote fragment settles without collection UI and leaves local world source available");
            }
            yield return ResourceBarrier("world-shard-shared", 35);
            if (!host) yield return Touch();
            yield return ResourceIdle("world same-source fragment", 25);
            ResourceCheck(displays == (host ? 1 : 0) && cleanups == 1,
                "world first award displays and duplicate only finishes native cleanup");
            CheckFragmentNativeCleanup();
            ResourceCheck(LocalResourceClaims.Contains(id) && LocalResourceClaims.Claimed.Count == 1 &&
                pd.GetInt(field) == (initial + 1) % (vessel ? 3 : 4) &&
                pd.GetInt("maxHealthBase") == (!vessel && initial == 3 ? 6 : 5) &&
                pd.GetInt("MPReserveMax") == (vessel && initial == 2 ? 33 : 0),
                "world duplicate does not consume remainder or award another full resource");
            yield return ResourceBarrier("world-shard-finished", 35);
            Mark("FINAL PASS real world duplicate " + id);
        } finally { On.HutongGames.PlayMaker.Fsm.EnterState -= trace; }

        IEnumerator Touch() {
            HeroController.instance.transform.position = pickup.transform.position;
            HeroController.instance.GetComponent<Rigidbody2D>().velocity = Vector2.zero; Physics2D.SyncTransforms();
            yield return Until(() => SceneData.instance.FindMyState(new PersistentBoolData {
                sceneName = scene, id = owner
            })?.activated == true, "real collision completes local fragment receipt", 20);
            yield return ResourceIdle("actual world fragment pickup", 25);
        }
    }

    private IEnumerator ShopShardOpenPrompt(string flag) {
        var region = ResourceFsm("Shop Region", "Shop Region");
        HeroController.instance.transform.position = region.transform.position;
        region.SetState("Take Control");
        var list = ResourceFsm("Item List", "Item List Control");
        var shop = ResourceFsm("Shop Menu", "shop_control");
        yield return Until(() => list.gameObject.activeInHierarchy && list.ActiveStateName == "Idle" &&
            ResourceFsm("Item List", "ui_list_getinput").ActiveStateName == "Idle" && shop.ActiveStateName == "Open" && region.ActiveStateName == "Shop Up",
            "Sly native shop open and listening", 12);
        var stock = list.GetComponent<ShopMenuStock>();
        var index = Array.FindIndex(stock.stockInv, g => g != null && g.GetComponent<ShopItemStats>().playerDataBoolName == flag);
        Mark("SHOP SHARD stock=" + string.Join(",", stock.stockInv.Where(g => g != null).Select(g => g.GetComponent<ShopItemStats>().playerDataBoolName)) + " index=" + index);
        ResourceCheck(index >= 0 && stock.CanBuy(index), "Sly selected shard purchasable");
        list.FsmVariables.GetFsmInt("Current Item").Value = index;
        list.SetState("Get Details"); yield return Until(() => list.ActiveStateName == "Idle", "Sly selected item details");
        list.SendEvent("UI CONFIRM");
        var confirm = ResourceFsm("UI List", "Confirm Control");
        yield return Until(() => confirm.gameObject.activeInHierarchy && confirm.ActiveStateName == "Listen", "Sly original confirmation open");
        ResourceCheck(confirm.FsmVariables.GetFsmString("PD Bool Name").Value == flag, "confirmation uses intended source");
    }
}
