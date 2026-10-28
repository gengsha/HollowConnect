using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using HollowConnect;
using HollowConnect.Core;
using HutongGames.PlayMaker.Actions;
using UnityEngine;
using Object = UnityEngine.Object;

namespace HollowConnect.Smoke;

public sealed partial class ProgressProbe {
    private readonly List<string> sourceNotices = new List<string>();
    private string KeyEssenceMode => Environment.GetEnvironmentVariable("HOLLOWCONNECT_KEY_ESSENCE");
    private bool KeyEssenceCase => !string.IsNullOrEmpty(KeyEssenceMode);
    private void PrepareKeyEssence() {
        CoopMod.Statistics = new DeathStatistics();
        CoopMod.Settings.ShareProgress = KeyEssenceMode != "off";
        var pd = PlayerData.instance;
        pd.SetBool("bossRushMode", false); pd.SetBool("hasDreamNail", true); pd.SetBool("hasLantern", true);
        pd.SetBool("slyRescued", true); pd.SetBool("gaveSlykey", true);
        pd.SetInt("geo", 30000); pd.SetInt("dreamOrbs", 0);
        foreach (var source in ResourceCatalog.Sources) {
            if (source.Kind == ResourceKind.UniqueKey) {
                pd.SetBool(source.InventoryFlag, false); pd.SetBool(source.SpentFlag, false);
                if (source.PersistentId != null) SceneData.instance.SaveMyState(new PersistentBoolData {
                    sceneName = source.Scene, id = source.PersistentId, activated = false
                });
            }
            if (source.Kind == ResourceKind.DreamEssence) {
                if (source.PlayerFlag != null) pd.SetBool(source.PlayerFlag, false);
                if (source.ClaimInt != null) pd.SetInt(source.ClaimInt, 1);
            }
        }
        // Set up a completed fight before room entry. Measured reward claiming
        // still uses the untouched native spirit/return FSM and UDP transport.
        foreach (var key in new[] { "falseKnightDreamDefeated", "mageLordDreamDefeated", "infectedKnightDreamDefeated",
            "whiteDefenderDefeated", "greyPrinceDefeated", "killedFalseKnight", "falseKnightDefeated", "killedMageLord",
            "mageLordDefeated", "killedInfectedKnight", "infectedKnightDefeated", "killedDungDefender", "defeatedDungDefender",
            "dungDefenderEncounterReady", "brettaRescued", "zoteDefeated" }) pd.SetBool(key, true);
        pd.SetBool("dungDefenderSleeping", true); pd.SetInt("whiteDefenderDefeats", 1); pd.SetInt("greyPrinceDefeats", 1);
    }
    private IEnumerator KeyEssenceChecks() {
        yield return Until(() => runtime.Peers.Count == 1 && engine.ClientManager.SceneSnapshotReady, "key/essence roster ready");
        if (KeyEssenceMode == "inspect") {
            var scenes = host ? new[] { "Crossroads_10", "Room_shop", "Fungus3_49", "Mines_11", "Ruins1_31", "Fungus2_21", "Ruins2_11" } :
                ResourceCatalog.Sources.Where(s => s.Kind == ResourceKind.DreamEssence).Select(s => s.Scene).Distinct().ToArray();
            foreach (var scene in scenes) {
                yield return KeyRoom(scene);
                foreach (var f in Object.FindObjectsOfType<PlayMakerFSM>(true).Where(f => f.gameObject.scene.name.StartsWith(scene, StringComparison.Ordinal) &&
                    (f.FsmName == "Shiny Control" || f.FsmName == "Conversation Control" || f.FsmName == "Control" && f.name.Contains("Dream")))) {
                    Mark("SOURCE PREFLIGHT " + scene + "/" + f.name + "/" + f.FsmName + " active=" + f.gameObject.activeInHierarchy + " state=" + f.ActiveStateName +
                        " vars=" + string.Join(",", f.FsmVariables.StringVariables.Select(v => v.Name + "=" + v.Value)));
                    Dump(f);
                }
            }
            yield return ResourceBarrier("source-inspection", 300); yield break;
        }
        On.HutongGames.PlayMaker.Fsm.EnterState += SourceNotice;
        try {
            if (KeyEssenceMode == "tree") {
                if (host) {
                    yield return KeyRoom("Crossroads_07");
                    var plant = Object.FindObjectsOfType<DreamPlant>().Single();
                    var hero = HeroController.instance;
                    hero.transform.position = plant.transform.position + Vector3.left * 1.5f + Vector3.up * 2;
                    hero.GetComponent<Rigidbody2D>().velocity = Vector2.zero;
                    hero.FaceRight(); hero.AffectedByGravity(true); Physics2D.SyncTransforms();
                    Mark("TREE NATIVE " + plant.name + " position=" + plant.transform.position + " orbs=" + Object.FindObjectsOfType<DreamPlantOrb>().Length);
                    // Teleporting retains the previous room's onGround for a
                    // frame. Let native physics settle before sampling input.
                    yield return new WaitForSecondsRealtime(.5f);
                    yield return Until(() => hero.cState.onGround && hero.CanDreamNail(), "native grounded dream-nail tree swing", 10);
                    Mark("TREE READY hero=" + hero.transform.position + " velocity=" + hero.GetComponent<Rigidbody2D>().velocity +
                        " dreamFSM=" + hero.GetComponents<PlayMakerFSM>().Single(f => f.FsmName == "Dream Nail").ActiveStateName);
                    yield return DreamSwing();
                    var orbs = Object.FindObjectsOfType<DreamPlantOrb>();
                    ResourceCheck(orbs.Length > 0, "native dream tree orb set exists");
                    yield return Until(() => orbs.Any(o => Field<bool>(o, "isActive") && Field<bool>(o, "canPickup")), "tree strike activates native orb collision", 10);
                    var orb = orbs.First(o => Field<bool>(o, "isActive") && Field<bool>(o, "canPickup"));
                    hero.transform.position = orb.transform.position;
                    Freeze(); Physics2D.SyncTransforms();
                    yield return Until(() => PlayerData.instance.GetInt("dreamOrbs") == 1, "actual native tree orb collision grants personal essence", 6);
                }
                yield return ResourceBarrier("tree-collected", 45);
                yield return new WaitForSecondsRealtime(2);
                ResourceCheck(PlayerData.instance.GetInt("dreamOrbs") == (host ? 1 : 0) &&
                    !LocalResourceClaims.Claimed.Any(id => id.StartsWith("essence:")) && sourceNotices.Count == 0,
                    "dream-tree essence stays personal with progress sharing enabled and creates no source notice");
                SourceSaveReplay(); yield break;
            }
            if (KeyEssenceMode == "keys") {
                foreach (var flag in new[] { "hasCityKey", "hasLoveKey", "hasSlykey", "hasWhiteKey" }) {
                    ResourceCatalog.TryGet("key:" + flag, out var source);
                    var notices = sourceNotices.Count;
                    if (host) {
                        if (flag == "hasWhiteKey") yield return KeyShop(true, flag);
                        else yield return ClaimUniqueShiny(source);
                    }
                    yield return Until(() => LocalResourceClaims.Contains(source.Id) && PlayerData.instance.GetBool(flag), "cross-room unique key " + flag, 60);
                    if (!host) yield return Until(() => sourceNotices.Count > notices && sourceNotices.Last().Contains(
                        flag == "hasCityKey" ? "城市纹章" : flag == "hasLoveKey" ? "爱之钥" : flag == "hasSlykey" ? "店主的钥匙" : "典雅的钥匙"),
                        "unique key named lower-left notice " + flag, 12);
                    yield return ResourceBarrier("key-shared-" + flag, 60);
                }
                if (host) {
                    yield return OpenUniqueDoor("Fungus2_21", "openedCityGate", "hasCityKey");
                    yield return OpenUniqueDoor("Ruins2_11_b", "openedLoveDoor", "hasLoveKey");
                    yield return OpenUniqueDoor("Ruins1_31", "usedWhiteKey", "hasWhiteKey");
                }
                yield return ResourceBarrier("unique-spent", 90);
                var pd = PlayerData.instance;
                ResourceCheck(pd.GetBool("openedCityGate") == host && pd.GetBool("openedLoveDoor") == host && pd.GetBool("usedWhiteKey") == host &&
                    pd.GetBool("gaveSlykey") == host, "four unique-key consumers stay personal");
                var inventory = new[] { "hasCityKey", "hasLoveKey", "hasSlykey", "hasWhiteKey" }.Select(pd.GetBool).ToArray();
                var noticeTotal = sourceNotices.Count;
                yield return ResourceReconnectSameProfile();
                foreach (var source in ResourceCatalog.Sources.Where(s => s.Kind == ResourceKind.UniqueKey)) ResourceSend(source.Id);
                yield return new WaitForSecondsRealtime(2);
                ResourceCheck(inventory.SequenceEqual(new[] { "hasCityKey", "hasLoveKey", "hasSlykey", "hasWhiteKey" }.Select(pd.GetBool)) &&
                    sourceNotices.Count == noticeTotal, "reconnect and replay never refund spent unique keys or repeat notices");
                SourceSaveReplay(); yield break;
            }
            if (KeyEssenceMode == "history") {
                var pd = PlayerData.instance;
                foreach (var source in ResourceCatalog.Sources.Where(s => s.Kind == ResourceKind.UniqueKey || s.Kind == ResourceKind.DreamEssence)) {
                    if (source.SpentFlag != null) { pd.SetBool(source.InventoryFlag, false); pd.SetBool(source.SpentFlag, true); }
                    if (source.ClaimInt != null) pd.SetInt(source.ClaimInt, 2);
                    if (source.PlayerFlag != null) pd.SetBool(source.PlayerFlag, true);
                }
                ResourceCheck(((ResourceShareService)ResourceService).ImportNativeReceipts(), "legacy import at native idle boundary");
                ResourceCheck(ResourceCatalog.Sources.Where(s => s.Kind == ResourceKind.UniqueKey || s.Kind == ResourceKind.DreamEssence)
                    .All(s => LocalResourceClaims.Contains(s.Id)), "legacy four spent keys and twelve claimed boss rewards import receipts");
                ResourceCheck(pd.GetInt("dreamOrbs") == 0 && ResourceCatalog.Sources.Where(s => s.Kind == ResourceKind.UniqueKey).All(s => !pd.GetBool(s.InventoryFlag)),
                    "legacy receipts cannot refill spent keys or already awarded essence");
                SourceSaveReplay(); yield return ResourceBarrier("source-history"); yield break;
            }
            if (KeyEssenceMode == "essence" || KeyEssenceMode == "off") {
                if (KeyEssenceMode == "off") {
                    ResourceCatalog.TryGet("key:hasCityKey", out var cityKey);
                    if (host) yield return ClaimUniqueShiny(cityKey);
                    yield return ResourceBarrier("unique-key-off", 45);
                    yield return new WaitForSecondsRealtime(1);
                    ResourceCheck(PlayerData.instance.GetBool("hasCityKey") == host && LocalResourceClaims.Contains(cityKey.Id) == host &&
                        sourceNotices.Count == 0, "sharing off keeps unique key and its notice local");
                }
                foreach (var source in ResourceCatalog.Sources.Where(s => s.Kind == ResourceKind.DreamEssence &&
                    s.PlayerFlag != "whiteDefenderOrbsCollected" && s.PlayerFlag != "greyPrinceOrbsCollected")) {
                    var before = PlayerData.instance.GetInt("dreamOrbs");
                    var noticeCount = sourceNotices.Count;
                    if (host) yield return ClaimSpirit(source);
                    yield return ResourceBarrier("spirit-host-" + source.Id.Replace(':', '-'), 60);
                    if (KeyEssenceMode == "off") {
                        yield return new WaitForSecondsRealtime(1);
                        ResourceCheck(PlayerData.instance.GetInt("dreamOrbs") == before + (host ? source.Amount : 0) &&
                            LocalResourceClaims.Contains(source.Id) == host && sourceNotices.Count == noticeCount, "sharing off preserves personal essence " + source.Id);
                        yield break;
                    }
                    yield return Until(() => LocalResourceClaims.Contains(source.Id) && PlayerData.instance.GetInt("dreamOrbs") == before + source.Amount,
                        "cross-room essence source " + source.Id, 15);
                    if (!host) {
                        yield return Until(() => sourceNotices.Count > noticeCount, "essence lower-left notice " + source.Id, 12);
                        ResourceCheck(sourceNotices.Last() == "共享梦境精华 ×" + source.Amount, "essence notice has native reward quantity " + source.Id);
                        yield return ClaimSpirit(source);
                        ResourceCheck(PlayerData.instance.GetInt("dreamOrbs") == before + source.Amount,
                            "native second claimant cannot duplicate shared essence " + source.Id);
                    }
                    yield return ResourceBarrier("spirit-guest-" + source.Id.Replace(':', '-'), 60);
                    yield return KeyRoom("Town");
                    yield return ResourceBarrier("spirit-town-" + source.Id.Replace(':', '-'));
                }
                var total = PlayerData.instance.GetInt("dreamOrbs"); var notices = sourceNotices.Count;
                foreach (var source in ResourceCatalog.Sources.Where(s => LocalResourceClaims.Contains(s.Id) && s.Kind == ResourceKind.DreamEssence))
                    ResourceSend(source.Id);
                yield return ResourceReconnectSameProfile();
                yield return new WaitForSecondsRealtime(2);
                ResourceCheck(PlayerData.instance.GetInt("dreamOrbs") == total && sourceNotices.Count == notices,
                    "essence reconnect and repeated receipts do not repeat grants or notices");
                yield break;
            }
            throw new Exception("Unimplemented source fixture " + KeyEssenceMode);
        } finally { On.HutongGames.PlayMaker.Fsm.EnterState -= SourceNotice; }
    }
    private void SourceSaveReplay() {
        var manager = Field<Hkmp.Game.Client.Save.SaveManager>(engine.ClientManager, "_saveManager");
        var mapping = Hkmp.Game.Client.Save.SaveDataMapping.Instance;
        var receive = manager.GetType().GetMethod("UpdateSaveWithData", ResourcePrivate, null, new[] { typeof(ushort), typeof(byte[]) }, null);
        var hosting = manager.IsHostingServer; manager.IsHostingServer = false;
        try {
            var fields = ResourceCatalog.Sources.Where(s => s.Kind == ResourceKind.UniqueKey).SelectMany(s => new[] { s.InventoryFlag, s.SpentFlag })
                .Concat(ResourceCatalog.Sources.Where(s => s.Kind == ResourceKind.DreamEssence).Select(s => s.ClaimInt ?? s.PlayerFlag))
                .Concat(new[] { "openedMageDoor_v2", "dreamOrbs" });
            foreach (var field in fields) {
                var pd = PlayerData.instance;
                var isInt = field == "dreamOrbs" || ResourceCatalog.Sources.Any(s => s.ClaimInt == field);
                object before = isInt ? (object)pd.GetInt(field) : pd.GetBool(field);
                object replay = before is int number ? (object)(number + 100) : !(bool)before;
                receive.Invoke(manager, new object[] { mapping.PlayerDataIndices[field], Hkmp.Util.EncodeUtil.EncodeSaveDataValue(replay) });
                ResourceCheck(before.Equals(isInt ? (object)pd.GetInt(field) : pd.GetBool(field)), "full-save protects source-owned field " + field);
            }
            foreach (var source in ResourceCatalog.Sources.Where(s => s.Kind == ResourceKind.UniqueKey && s.PersistentId != null)) {
                var item = new Hkmp.Game.Client.Save.PersistentItemKey { SceneName = source.Scene, Id = source.PersistentId };
                var query = new PersistentBoolData { sceneName = source.Scene, id = source.PersistentId };
                var before = SceneData.instance.FindMyState(query)?.activated == true;
                receive.Invoke(manager, new object[] { mapping.PersistentBoolIndices[item], new byte[] { (byte)(before ? 0 : 1) } });
                ResourceCheck((SceneData.instance.FindMyState(query)?.activated == true) == before,
                    "full-save cannot hide or replay a unique key pickup " + source.Id);
            }
        } finally { manager.IsHostingServer = hosting; }
    }
    private IEnumerator ClaimUniqueShiny(ResourceSource source) {
        yield return KeyRoom(source.Scene);
        var number = source.InventoryFlag == "hasCityKey" ? 9 : source.InventoryFlag == "hasLoveKey" ? 10 : 8;
        var shiny = Object.FindObjectsOfType<PlayMakerFSM>(true).Single(f => f.gameObject.scene.name == source.Scene &&
            f.FsmName == "Shiny Control" && f.FsmVariables.FindFsmInt("Trinket Num")?.Value == number);
        ResourceCheck(shiny.gameObject.activeInHierarchy && shiny.ActiveStateName == "Idle", "native configured key shiny " + source.Id);
        Dump(shiny);
        var inspect = shiny.GetComponentsInChildren<PlayMakerFSM>(true).Single(f => f.FsmName == "inspect");
        KeyStandAt(inspect.GetComponent<Collider2D>(), shiny.transform.position);
        yield return new WaitForFixedUpdate();
        yield return Until(() => inspect.Fsm.ActiveState.Actions.OfType<ListenForUp>().Any() && HeroController.instance.CanInspect(),
            "native unique key inspection range " + source.Id, 10);
        inspect.Fsm.Event(inspect.Fsm.ActiveState.Actions.OfType<ListenForUp>().Single().wasPressed);
        yield return Until(() => LocalResourceClaims.Contains(source.Id), "native unique key receipt " + source.Id, 15);
        yield return ResourceIdle(source.Id);
    }
    private IEnumerator OpenUniqueDoor(string scene, string spent, string inventory) {
        yield return KeyRoom(scene);
        yield return new WaitForSecondsRealtime(3);
        var fsm = Object.FindObjectsOfType<PlayMakerFSM>(true).Single(f => f.gameObject.scene.name == scene && f.FsmName == "Conversation Control" &&
            f.FsmStates.SelectMany(s => s.Actions).OfType<SetPlayerDataBool>().Any(a => a.boolName.Value == spent));
        var control = fsm.GetComponents<PlayMakerFSM>().Single(f => f.FsmName == "npc_control");
        var ranges = control.FsmVariables.FindFsmGameObject("Ranges Folder")?.Value ?? control.gameObject;
        KeyStandAt(ranges.GetComponentsInChildren<Collider2D>(true).FirstOrDefault(c => c.enabled && c.isTrigger), fsm.transform.position);
        yield return new WaitForFixedUpdate();
        yield return Until(() => HeroController.instance.CanTalk() && control.ActiveStateName != "Inert", "native unique door can interact " + spent, 10);
        control.SetState("Take Control");
        yield return Until(() => fsm.ActiveStateName == "Send Text" && Dialogue.ActiveStateName == "Ready for Input", "native unique key yes/no prompt " + spent, 12);
        ChooseYes();
        yield return Until(() => PlayerData.instance.GetBool(spent) && HeroController.instance.acceptingInput,
            "native unique key consumer completed " + spent, 20);
        Mark("PASS native unique key spent " + inventory + " remains=" + PlayerData.instance.GetBool(inventory));
    }
    private void SourceNotice(On.HutongGames.PlayMaker.Fsm.orig_EnterState orig, HutongGames.PlayMaker.Fsm fsm, HutongGames.PlayMaker.FsmState state) {
        if (fsm.GameObject?.name == ResourceNotifications.PopupName && state.Name == "Up") {
            var text = fsm.GameObject.transform.Find("Text").GetComponent<TMPro.TextMeshPro>().text;
            var point = GameCameras.instance.hudCamera.WorldToViewportPoint(fsm.GameObject.transform.position);
            ResourceCheck(point.x >= 0 && point.x <= .3f && point.y >= 0 && point.y <= .3f && point.z > 0,
                "shared source notice appears in lower-left HUD: " + text);
            sourceNotices.Add(text);
        }
        orig(fsm, state);
    }
    private IEnumerator ClaimSpirit(ResourceSource source) {
        yield return KeyRoom(source.Scene);
        var npc = Object.FindObjectsOfType<PlayMakerFSM>(true).Single(f => f.gameObject.scene.name.StartsWith(source.Scene, StringComparison.Ordinal) &&
            f.FsmName == "Conversation Control" && (source.ClaimInt != null ?
                f.FsmVariables.FindFsmString("PD Int Name")?.Value == source.ClaimInt :
                f.FsmStates.SelectMany(s => s.Actions).OfType<SetPlayerDataBool>().Any(a => a.boolName.Value == source.PlayerFlag)));
        ResourceCheck(npc.gameObject.activeInHierarchy && npc.enabled, "native reward spirit exists " + source.Id);
        var action = npc.FsmStates.SelectMany(s => s.Actions).OfType<PlayerDataIntAdd>().Single(a => a.intName.Value == "dreamOrbs");
        ResourceCheck(action.amount.Value == source.Amount, "catalog amount matches native reward " + source.Id);
        var control = npc.GetComponents<PlayMakerFSM>().Single(f => f.FsmName == "npc_control");
        HeroController.instance.transform.position = npc.transform.position + Vector3.right;
        Freeze(); Physics2D.SyncTransforms();
        yield return Until(() => npc.ActiveStateName == "Idle" && control.enabled, "native spirit idle " + source.Id, 12);
        control.SetState("Take Control");
        var deadline = Time.realtimeSinceStartup + 30;
        while (Time.realtimeSinceStartup < deadline && !(LocalResourceClaims.Contains(source.Id) &&
            (source.ClaimInt != null ? PlayerData.instance.GetInt(source.ClaimInt) == 2 : PlayerData.instance.GetBool(source.PlayerFlag)))) {
            if (npc.Fsm.ActiveState?.Transitions.Any(t => t.EventName == "CONVO_FINISH") == true) npc.SendEvent("CONVO_FINISH");
            yield return null;
        }
        ResourceCheck(LocalResourceClaims.Contains(source.Id) && (source.ClaimInt != null ? PlayerData.instance.GetInt(source.ClaimInt) == 2 :
            PlayerData.instance.GetBool(source.PlayerFlag)), "native spirit completes receipt " + source.Id + " state=" + npc.ActiveStateName);
        yield return ResourceIdle(source.Id);
    }
}
