using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HollowConnect;
using HollowConnect.Core;
using HutongGames.PlayMaker.Actions;
using MonoMod.RuntimeDetour;
using UnityEngine;
using Object = UnityEngine.Object;

namespace HollowConnect.Smoke;

public sealed partial class ProgressProbe {
    private string SimpleKeyMode => Environment.GetEnvironmentVariable("HOLLOWCONNECT_SIMPLE_KEYS");
    private bool SimpleKeyCase => SimpleKeyMode != null;
    private static readonly string[] KeyDoors = { "jijiDoorUnlocked", "bathHouseOpened", "openedWaterwaysManhole", "godseekerUnlocked" };
    private void PrepareSimpleKeys() {
        CoopMod.Statistics = new DeathStatistics();
        CoopMod.Settings.ShareProgress = SimpleKeyMode != "off";
        var pd = PlayerData.instance;
        pd.SetBool("bossRushMode", false); pd.SetBool("slyRescued", true);
        pd.SetInt("simpleKeys", 0); pd.SetInt("geo", 3000);
        foreach (var door in KeyDoors) pd.SetBool(door, false);
        foreach (var source in ResourceCatalog.Sources.Where(s => s.Kind == ResourceKind.SimpleKey)) {
            if (source.PlayerFlag != null) pd.SetBool(source.PlayerFlag, false);
            if (source.PersistentId != null) SceneData.instance.SaveMyState(new PersistentBoolData {
                sceneName=source.Scene, id=source.PersistentId, activated=false
            });
        }
    }
    private IEnumerator KeyRoom(string scene) {
        Enter(scene);
        yield return Until(() => AliveScene(scene) && engine.ClientManager.SceneSnapshotReady, "key room " + scene, 35);
        Freeze(); yield return new WaitForSecondsRealtime(.3f);
    }
    private void KeyStandAt(Collider2D target, Vector3 fallback) {
        var hero=HeroController.instance;
        var body=hero.GetComponent<Collider2D>();
        var offset=body.bounds.center-hero.transform.position;
        var point=target!=null ? target.transform.TransformPoint(target.offset) : fallback;
        hero.transform.position=point-offset;
        hero.GetComponent<Rigidbody2D>().velocity=Vector2.zero;
        hero.AffectedByGravity(true); Physics2D.SyncTransforms();
    }
    private PlayMakerFSM KeyShiny(string scene) => Object.FindObjectsOfType<PlayMakerFSM>(true).FirstOrDefault(f =>
        f.gameObject.scene.name == scene && f.FsmName == "Shiny Control" && f.gameObject.activeInHierarchy &&
        (scene == "GG_Lurker" ? f.FsmVariables.FindFsmString("PD Bool Name")?.Value == "gotLurkerKey" :
            f.name == (scene == "Ruins1_17" ? "Shiny Item" : "Shiny Item Stand")));
    private IEnumerator KeyCollect(string scene, string id, bool fresh = true) {
        yield return KeyRoom(scene);
        var fsm = KeyShiny(scene);
        if (scene == "GG_Lurker" && fsm == null) {
            var lurker = Object.FindObjectsOfType<HealthManager>().Single(h => h.gameObject.scene.name == scene && h.name.Contains("Lurker"));
            Mark("KEY LURKER native damage hp=" + lurker.hp + " name=" + lurker.name);
            lurker.ApplyExtraDamage(lurker.hp);
        }
        yield return Until(() => (fsm = KeyShiny(scene)) != null && fsm.ActiveStateName == "Idle", "native simple key available " + id, 20);
        Mark("KEY PICKUP " + scene + "/" + fsm.name + " state=" + fsm.ActiveStateName +
            " vars=" + string.Join(",",fsm.FsmVariables.IntVariables.Select(v=>v.Name+"="+v.Value)));
        var inspect = fsm.GetComponentsInChildren<PlayMakerFSM>(true).FirstOrDefault(f=>f.FsmName=="inspect");
        var collider = inspect?.GetComponent<Collider2D>() ?? fsm.GetComponentsInChildren<Collider2D>(true).FirstOrDefault(c => c.enabled && c.isTrigger);
        var before = PlayerData.instance.GetInt("simpleKeys");
        Mark("KEY COLLIDER " + collider?.name + " enabled=" + collider?.enabled + " trigger=" + collider?.isTrigger + " bounds=" + collider?.bounds + " offset="+collider?.offset + " scale=" + collider?.transform.lossyScale);
        KeyStandAt(collider,fsm.transform.position);
        Mark("KEY HERO " + HeroController.instance.transform.position + " body=" + HeroController.instance.GetComponent<Collider2D>().bounds);
        yield return new WaitForFixedUpdate();
        yield return Until(()=>inspect!=null && inspect.Fsm.ActiveState.Actions.OfType<ListenForUp>().Any(),"native key inspect in range " + id,10);
        yield return Until(()=>HeroController.instance.CanInspect(),"native grounded hero can inspect " + id,10);
        Mark("KEY INSPECT " + inspect?.name + " state=" + inspect?.ActiveStateName);
        var up = inspect?.Fsm.ActiveState.Actions.OfType<ListenForUp>().FirstOrDefault();
        if (up != null) inspect.Fsm.Event(up.wasPressed);
        yield return Until(() => LocalResourceClaims.Contains(id), "native source receipt " + id, 15);
        yield return ResourceIdle(id);
        ResourceCheck(PlayerData.instance.GetInt("simpleKeys") == before + (fresh ? 1 : 0), "native key source awards at most once " + id);
        Mark("PASS KEY SOURCE " + id + " balance=" + PlayerData.instance.GetInt("simpleKeys"));
    }
    private IEnumerator KeyShop(bool fresh = true) {
        yield return KeyRoom("Room_shop");
        var before = PlayerData.instance.GetInt("simpleKeys"); var geo = PlayerData.instance.GetInt("geo");
        var region=ResourceFsm("Shop Region","Shop Region");
        Dump(region);
        KeyStandAt(region.GetComponent<Collider2D>(),region.transform.position);
        region.SetState("Take Control");
        var shop=ResourceFsm("Shop Menu","shop_control");
        var introDeadline=Time.realtimeSinceStartup+12;
        while(region.ActiveStateName!="Shop Up" && Time.realtimeSinceStartup<introDeadline) {
            ResourceAdvanceText(region);
            yield return new WaitForSecondsRealtime(.1f);
        }
        yield return Until(()=>shop.ActiveStateName=="Open" && region.ActiveStateName=="Shop Up" &&
            ResourceFsm("Item List","Item List Control").ActiveStateName=="Idle" &&
            ResourceFsm("Item List","ui_list_getinput").ActiveStateName=="Idle", "native Sly shop open",12);
        var list = ResourceFsm("Item List", "Item List Control"); var stock = list.GetComponent<ShopMenuStock>();
        var index = Array.FindIndex(stock.stockInv, g => g != null && g.GetComponent<ShopItemStats>().playerDataBoolName == "slySimpleKey");
        ResourceCheck(index >= 0 && stock.CanBuy(index), "native Sly key stock available");
        var cost=stock.GetCost(index);
        list.FsmVariables.GetFsmInt("Current Item").Value=index; list.SetState("Get Details");
        yield return Until(() => list.ActiveStateName == "Idle", "native Sly key selected"); list.SendEvent("UI CONFIRM");
        var confirm=ResourceFsm("UI List","Confirm Control");
        yield return Until(() => confirm.gameObject.activeInHierarchy && confirm.ActiveStateName=="Listen", "native Sly key confirmation");
        var selection=ResourceFsm("UI List","ui_list"); selection.FsmVariables.GetFsmInt("Current Item").Value=1;
        confirm.SendEvent("UI SELECTION MADE");
        yield return Until(() => LocalResourceClaims.Contains("key:slySimpleKey"), "native Sly key purchase", 15);
        yield return ResourceCloseSalubra(); yield return ResourceIdle("Sly key");
        ResourceCheck(PlayerData.instance.GetInt("simpleKeys")==before+(fresh?1:0) && PlayerData.instance.GetInt("geo")==geo-(fresh?cost:0),
            "Sly key awards and charges only an unreceived source");
    }
    private IEnumerator KeyOpenDoor(string scene, string field) {
        yield return KeyRoom(scene);
        // First-entry area titles start after the scene is playable. Their
        // BIG TITLE START would move npc_control to Inert during the prompt.
        // Allow that native title to start, then wait for its Idle restoration.
        yield return new WaitForSecondsRealtime(3);
        var pd=PlayerData.instance; var before=pd.GetInt("simpleKeys");
        var fsm=Object.FindObjectsOfType<PlayMakerFSM>(true).Single(f => f.gameObject.scene.name==scene &&
            f.FsmStates.SelectMany(s=>s.Actions).OfType<SetPlayerDataBool>().Any(a=>a.boolName.Value==field));
        Mark("KEY DOOR " + field + " object=" + fsm.name + "/" + fsm.FsmName + " state=" + fsm.ActiveStateName);
        var control=fsm.GetComponents<PlayMakerFSM>().FirstOrDefault(f=>f.FsmName=="npc_control") ?? fsm;
        var ranges=control.FsmVariables.FindFsmGameObject("Ranges Folder")?.Value ?? control.gameObject;
        var triggers=ranges.GetComponentsInChildren<Collider2D>(true).Where(c=>c.enabled && c.isTrigger).ToArray();
        foreach(var c in triggers) Mark("KEY DOOR COLLIDER " + c.name + " type=" + c.GetType().Name + " bounds=" + c.bounds);
        var collider=triggers.OrderByDescending(c=>c.bounds.size.y).FirstOrDefault();
        Mark("KEY DOOR RANGE " + ranges.name + "/" + collider?.name + " bounds=" + collider?.bounds);
        KeyStandAt(collider,fsm.transform.position);
        // Teleporting leaves the previous frame's grounded flag until physics runs.
        yield return new WaitForFixedUpdate();
        yield return new WaitForSecondsRealtime(.15f);
        yield return Until(() => (control.ActiveStateName=="Idle" || control.ActiveStateName=="In Range" || control.ActiveStateName=="In Range Turns") && HeroController.instance.CanTalk(),
            "initialized native door and grounded fixture " + field,10);
        // As with the existing shop fixture, enter at native interaction control.
        // Range geometry is outside this test; key checks, prompt, payment,
        // unlocking and control release all run through the original FSMs.
        control.SetState("Take Control");
        yield return Until(() => fsm.ActiveStateName=="Send Text" && Dialogue.ActiveStateName=="Ready for Input" &&
            Dialogue.FsmVariables.FindFsmGameObject("Requester")?.Value==fsm.gameObject, "native key door prompt " + field, 12);
        ChooseYes();
        yield return Until(() => pd.GetBool(field) && pd.GetInt("simpleKeys")==before-1 && HeroController.instance.acceptingInput,
            "native one-key personal unlock " + field, 20);
        Mark("PASS PERSONAL KEY DOOR " + field + " balance=" + pd.GetInt("simpleKeys"));
    }
    private void KeyReplayIsolation() {
        var pd=PlayerData.instance;
        var manager=Field<Hkmp.Game.Client.Save.SaveManager>(engine.ClientManager,"_saveManager");
        var map=Hkmp.Game.Client.Save.SaveDataMapping.Instance;
        var receive=manager.GetType().GetMethod("UpdateSaveWithData",ResourcePrivate,null,new[]{typeof(ushort),typeof(byte[])},null);
        var hosting=manager.IsHostingServer; manager.IsHostingServer=false;
        try {
            var keys=pd.GetInt("simpleKeys");
            receive.Invoke(manager,new object[]{map.PlayerDataIndices["simpleKeys"],Hkmp.Util.EncodeUtil.EncodeSaveDataValue(keys+10)});
            ResourceCheck(pd.GetInt("simpleKeys")==keys,"stale full-save balance cannot overwrite personal keys");
            foreach(var field in KeyDoors.Concat(new[]{"slySimpleKey","gotLurkerKey"})) {
                var before=pd.GetBool(field);
                receive.Invoke(manager,new object[]{map.PlayerDataIndices[field],Hkmp.Util.EncodeUtil.EncodeSaveDataValue(!before)});
                ResourceCheck(pd.GetBool(field)==before,"stale full-save flag remains personal " + field);
            }
            foreach(var source in ResourceCatalog.Sources.Where(s=>s.Kind==ResourceKind.SimpleKey && s.PersistentId!=null)) {
                var item=new Hkmp.Game.Client.Save.PersistentItemKey{SceneName=source.Scene,Id=source.PersistentId};
                var before=SceneData.instance.FindMyState(new PersistentBoolData{sceneName=source.Scene,id=source.PersistentId})?.activated==true;
                receive.Invoke(manager,new object[]{map.PersistentBoolIndices[item],new byte[]{(byte)(before?0:1)}});
                ResourceCheck((SceneData.instance.FindMyState(new PersistentBoolData{sceneName=source.Scene,id=source.PersistentId})?.activated==true)==before,
                    "stale full-save pickup cannot invent a key acquisition " + source.Id);
            }
        } finally {manager.IsHostingServer=hosting;}
        Mark("PASS key balance, four doors and four source markers preserve local state on save replay");
    }
    private IEnumerator SimpleKeyChecks() {
        if(SimpleKeyMode=="waterway") {
            if(host) {
                PlayerData.instance.SetInt("simpleKeys",1);
                On.HutongGames.PlayMaker.Fsm.hook_EnterState trace=(orig,fsm,state)=> {
                    if(fsm.GameObject?.name=="Waterways Machine") Mark("KEY TRACE "+fsm.Name+" -> "+state.Name+" keys="+PlayerData.instance.GetInt("simpleKeys"));
                    orig(fsm,state);
                };
                On.HutongGames.PlayMaker.Fsm.EnterState+=trace;
                try {
                    using(new Hook(typeof(HeroController).GetMethod("RelinquishControl"),(Action<Action<HeroController>,HeroController>)((orig,hero)=>{Mark("KEY RELINQUISH "+Environment.StackTrace);orig(hero);})))
                    using(new Hook(typeof(HeroController).GetMethod("RegainControl"),(Action<Action<HeroController>,HeroController>)((orig,hero)=>{Mark("KEY REGAIN "+Environment.StackTrace);orig(hero);})))
                        yield return KeyOpenDoor("Ruins1_05b","openedWaterwaysManhole");
                } finally {On.HutongGames.PlayMaker.Fsm.EnterState-=trace;}
            }
            yield return ResourceBarrier("key-waterway",100); yield break;
        }
        if(SimpleKeyMode=="doors" || SimpleKeyMode=="coffin-bathhouse") {
            if(host) {
                PlayerData.instance.SetInt("simpleKeys",SimpleKeyMode=="doors"?4:2); // Only fixture inventory; payments remain native.
                if(SimpleKeyMode=="doors") {
                    yield return KeyOpenDoor("Town","jijiDoorUnlocked");
                    yield return KeyOpenDoor("Ruins1_05b","openedWaterwaysManhole");
                }
                yield return KeyOpenDoor("GG_Waterways","godseekerUnlocked");
                yield return KeyOpenDoor("Ruins2_04","bathHouseOpened");
            }
            yield return ResourceBarrier("key-doors-preflight",150);
            ResourceCheck(KeyDoors.All(field=>PlayerData.instance.GetBool(field)==(host &&
                (SimpleKeyMode=="doors" || field=="godseekerUnlocked" || field=="bathHouseOpened"))),"native door openings never change the peer's four locks");
            yield break;
        }
        if(SimpleKeyMode=="abyss" || SimpleKeyMode=="lurker" || SimpleKeyMode=="shop") {
            if(host) {
                if(SimpleKeyMode=="shop") yield return KeyShop();
                else yield return KeyCollect(SimpleKeyMode=="abyss"?"Abyss_20":"GG_Lurker",SimpleKeyMode=="abyss"?"key:Abyss_20/Shiny Item Stand":"key:gotLurkerKey");
            }
            yield return ResourceBarrier("key-native-endpoint",100); yield break;
        }
        if (SimpleKeyMode=="inspect") {
            KeyReplayIsolation();
            foreach(var scene in new[]{"Ruins1_17","Abyss_20","GG_Lurker","Room_shop","Town","Ruins2_04","Ruins1_05b","GG_Waterways"}) {
                yield return KeyRoom(scene);
                foreach(var f in Object.FindObjectsOfType<PlayMakerFSM>(true).Where(f=>f.gameObject.scene.name==scene &&
                    (f.name.Contains("Shiny") || f.FsmName=="inspect" || f.FsmName=="npc_control" || f.FsmStates.SelectMany(s=>s.Actions).OfType<SetPlayerDataBool>().Any(a=>KeyDoors.Contains(a.boolName.Value))))) {
                    Mark("KEY PREFLIGHT " + scene + "/" + f.name + "/" + f.FsmName + " active=" + f.gameObject.activeInHierarchy + " state=" + f.ActiveStateName);
                    Dump(f);
                }
                yield return ResourceBarrier("key-inspect-"+scene);
            }
            yield break;
        }
        if(host) {
            yield return KeyCollect("Ruins1_17","key:Ruins1_17/Shiny Item");
            if(SimpleKeyMode=="flow") {
                yield return KeyCollect("Abyss_20","key:Abyss_20/Shiny Item Stand");
                yield return KeyCollect("GG_Lurker","key:gotLurkerKey");
                yield return KeyShop();
                yield return KeyOpenDoor("Town","jijiDoorUnlocked");
                yield return KeyOpenDoor("Ruins1_05b","openedWaterwaysManhole");
                yield return KeyOpenDoor("GG_Waterways","godseekerUnlocked");
                Flag("host.keys.spent");
            } else Flag("host.keys.collected");
        }
        yield return Until(()=>runtime.Peers.Count==1,"both key clients connected",60);
        var pd=PlayerData.instance;
        if(SimpleKeyMode=="off") {
            yield return Until(()=>Has("host.keys.collected"),"sharing-off host key pickup");
            yield return new WaitForSecondsRealtime(2);
            ResourceCheck(!runtime.Shared && pd.GetInt("simpleKeys")== (host?1:0),"sharing-off keeps source acquisition local");
            KeyReplayIsolation();
            if(host) {
                yield return Until(()=>((ResourceShareService)ResourceService).Idle,"legacy migration waits for native slot readiness",12);
                // Simulate a pre-ledger save after spending this actual native pickup.
                pd.SetInt("simpleKeys",0);
                LocalResourceClaims.Claimed.Remove("key:Ruins1_17/Shiny Item");
                var imported=((ResourceShareService)ResourceService).ImportNativeReceipts();
                Mark("KEY LEGACY import="+imported+" claim="+LocalResourceClaims.Contains("key:Ruins1_17/Shiny Item")+
                    " balance="+pd.GetInt("simpleKeys")+" marker="+SceneData.instance.FindMyState(new PersistentBoolData{sceneName="Ruins1_17",id="Shiny Item"})?.activated);
                ResourceCheck(imported &&
                    LocalResourceClaims.Contains("key:Ruins1_17/Shiny Item") && pd.GetInt("simpleKeys")==0,
                    "legacy native source imports without refunding spent key");
            }
            yield return ResourceBarrier("keys-off"); yield break;
        }
        yield return Until(()=>ResourceCatalog.Sources.Where(s=>s.Kind==ResourceKind.SimpleKey).All(s=>LocalResourceClaims.Contains(s.Id)) &&
            pd.GetInt("simpleKeys")== (host?1:4),"late entrant receives all four sources despite host expenditure",25);
        ResourceCheck(pd.GetBool("jijiDoorUnlocked")==host && !pd.GetBool("bathHouseOpened") && pd.GetBool("openedWaterwaysManhole")==host && pd.GetBool("godseekerUnlocked")==host,
            "four native door flags remain personal after late join");
        KeyReplayIsolation();
        if(!host) {
            yield return KeyCollect("Ruins1_17","key:Ruins1_17/Shiny Item",false);
            yield return KeyShop(false);
            yield return KeyOpenDoor("Ruins2_04","bathHouseOpened"); Flag("guest.key.spent");
        }
        yield return Until(()=>Has("guest.key.spent"),"guest spends its own shared key",90);
        yield return new WaitForSecondsRealtime(1);
        ResourceCheck(pd.GetInt("simpleKeys")== (host?1:3) && pd.GetBool("bathHouseOpened")==!host && pd.GetBool("jijiDoorUnlocked")==host,
            "each player's native opening consumes exactly one personal key");
        if(!host) {
            engine.ClientManager.Disconnect(); yield return Until(()=>!runtime.Connected,"key guest disconnects");
            CoopMod.Statistics=CoopMod.Statistics.Copy();
            typeof(Hkmp.Game.Client.ClientManager).GetField("_autoConnect",ResourcePrivate).SetValue(engine.ClientManager,true);
            typeof(Hkmp.Game.Client.ClientManager).GetMethod("Connect",ResourcePrivate).Invoke(engine.ClientManager,new object[]{"127.0.0.1",29650,"progress"+role});
            yield return Until(()=>runtime.Connected && runtime.Peers.Count==1,"key guest reconnects",30);
            Flag("guest.key.rejoined");
        }
        yield return Until(()=>Has("guest.key.rejoined") && runtime.Peers.Count==1 && ((ResourceShareService)ResourceService).Idle,"key reconnect settles",40);
        foreach(var source in ResourceCatalog.Sources.Where(s=>s.Kind==ResourceKind.SimpleKey)) {ResourceSend(source.Id);ResourceSend(source.Id);}
        yield return new WaitForSecondsRealtime(2);
        ResourceCheck(pd.GetInt("simpleKeys")== (host?1:3),"reconnect and repeated offers never refund spent keys");
        KeyReplayIsolation(); yield return ResourceBarrier("keys-complete");
    }
}
