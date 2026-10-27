using System;
using System.Collections;
using System.Linq;
using System.Reflection;
using Hkmp.Game.Client;
using Hkmp.Networking.Packet.Data;
using HollowConnect;
using HutongGames.PlayMaker.Actions;
using UnityEngine;
using Scenes = UnityEngine.SceneManagement.SceneManager;

namespace HollowConnect.Smoke;

public sealed partial class ProgressProbe {
    private Hkmp.Ui.UiManager ExitUi => Field<Hkmp.Ui.UiManager>(engine.ClientManager, "_uiManager");
    private void VerifyMenuDoesNotSave() {
        var calls = 0;
        On.GameManager.hook_SaveGame_int_Action1 guard = (orig, gm, slot, callback) => { calls++; callback?.Invoke(true); };
        On.GameManager.SaveGame_int_Action1 += guard;
        try { ExitUi.ReturnToMainMenuFromGame(); ResourceCheck(calls == 0, "disconnect in title/slot menu does not save"); }
        finally { On.GameManager.SaveGame_int_Action1 -= guard; }
    }
    private IEnumerator MapExitChecks() {
        yield return Room("Crossroads_33");
        var pd = PlayerData.instance;
        var geo = pd.GetInt("geo");
        var visited = pd.scenesVisited.ToArray();
        var cornifer = GameObject.Find("Cornifer");
        var conversation = cornifer.GetComponents<PlayMakerFSM>().Single(f => f.FsmName == "Conversation Control");
        var control = cornifer.GetComponents<PlayMakerFSM>().Single(f => f.FsmName == "npc_control");
        HeroController.instance.transform.position = cornifer.transform.position + new Vector3(-1, 2, 0);
        control.SetState("Take Control");
        yield return Until(() => conversation.ActiveStateName == "Meet", "Cornifer first native conversation");
        conversation.SendEvent("CONVO_FINISH");
        yield return Until(() => conversation.ActiveStateName == "Send Text" && Dialogue.ActiveStateName == "Ready for Input", "Cornifer native map confirmation open");
        yield return ResourceBarrier("map-prompts");
        if (!host) {
            yield return Until(() => pd.GetBool("mapCrossroads") && pd.GetBool("hasMap"), "map ownership arrives during guest confirmation");
            ResourceCheck(pd.GetInt("geo") == geo && pd.scenesVisited.SequenceEqual(visited), "map grant preserves money and explored rooms");
        }
        ChooseYes();
        if (host) {
            // The buyer also receives the party echo. Depending on UDP timing,
            // native First Map? may show its first-map UI or normal map UI.
            yield return Until(() => pd.GetBool("mapCrossroads") && conversation.ActiveStateName != "Send Text" &&
                (conversation.ActiveStateName == "Map Input" || HeroController.instance.acceptingInput && !pd.GetBool("disablePause")),
                "native map acquisition finishes or waits on first-map input");
            if (conversation.ActiveStateName == "Map Input") {
                conversation.SendEvent("CONVO END");
                yield return Until(() => conversation.ActiveStateName == "Iselda Mention", "first map UI closes");
                conversation.SendEvent("CONVO_FINISH");
            }
        }
        else yield return Until(() => conversation.ActiveStateName != "Send Text", "guest finishes native duplicate purchase dialogue");
        yield return Until(() => HeroController.instance.acceptingInput && !pd.GetBool("disablePause"), "map purchase restores native input");
        Mark("MAP FINAL owned=" + pd.GetBool("mapCrossroads") + " hasMap=" + pd.GetBool("hasMap") + " charge=" + (geo - pd.GetInt("geo")));
        ResourceCheck(pd.GetBool("mapCrossroads") && pd.GetBool("hasMap") && pd.GetInt("geo") == geo - (host ? 30 : 0),
            "first map costs 30; shared duplicate confirmation costs zero");
        yield return ResourceBarrier("map-complete");

        // Optional specialty: exercise the Iselda shop payment state with real native
        // actions; Cornifer uses SendMessage and this shop uses CallMethodProper.
        if (Environment.GetEnvironmentVariable("HOLLOWCONNECT_MAP_EXIT_SHOP_CHECK") == "1") {
        pd.SetBool("corn_greenpathLeft", true);
        pd.SetBool("corniferAtHome", true);
        pd.SetBool("openedMapperShop", true); pd.SetBool("metIselda", true);
        pd.SetBool("iseldaConvo1", true); pd.SetBool("iseldaCorniferHomeConvo", true);
        yield return Room("Room_mapper");
        yield return ShopShardOpenPrompt("mapGreenpath");
        yield return ResourceBarrier("iselda-prompts");
        var shopGeo = pd.GetInt("geo");
        var confirm = ResourceFsm("UI List", "Confirm Control");
        var cost = confirm.FsmVariables.GetFsmInt("Cost").Value;
        if (!host) yield return Until(() => pd.GetBool("mapGreenpath"), "Iselda map shared while confirmation is open");
        ResourceFsm("UI List", "ui_list").FsmVariables.GetFsmInt("Current Item").Value = 1;
        confirm.SendEvent("UI SELECTION MADE");
        yield return Until(() => pd.GetBool("mapGreenpath") && confirm.ActiveStateName != "Listen", "Iselda map purchase completes");
        yield return new WaitForSecondsRealtime(.5f);
        var shop = ResourceFsm("Shop Menu", "shop_control");
        if (shop.ActiveStateName == "Open") yield return ResourcePress(InputHandler.Instance.inputActions.menuCancel);
        yield return ResourceIdle("Iselda duplicate map");
        ResourceCheck(pd.GetInt("geo") == shopGeo - (host ? cost : 0), "Iselda charges only the first map purchase");
        yield return ResourceBarrier("iselda-complete");
        }

        // All save overloads remain blocked by ProbeMod. Hold the native
        // callback to prove that scene unload cannot race the save operation.
        GameManager.instance.profileID = 1;
        var origin = Scenes.GetActiveScene().handle;
        var expectedGeo = pd.GetInt("geo");
        var calls = 0;
        Action<bool> complete = null;
        On.GameManager.hook_SaveGame_int_Action1 save = (orig, gm, slot, callback) => {
            calls++;
            ResourceCheck(slot == 1 && ReferenceEquals(pd, PlayerData.instance) && !runtime.Connected &&
                !runtime.Down && !runtime.Returning && !runtime.Resetting && !BossTravel.HasTransientContext,
                "save samples loaded local slot after disconnect cleanup");
            ResourceCheck(pd.GetBool("mapCrossroads") && pd.GetInt("geo") == expectedGeo,
                "save receives latest map and money values");
            complete = callback;
        };
        On.GameManager.SaveGame_int_Action1 += save;
        try {
            if (host) yield return Until(() => Has("guest.exit-complete"), "guest timeout save check finishes", 30);
            var method = typeof(ClientManager).GetMethod(host ? "OnDisconnect" : "OnTimeout", BindingFlags.Instance | BindingFlags.NonPublic);
            method.Invoke(engine.ClientManager, host ? new object[] { new ServerClientDisconnect { Reason = DisconnectReason.Shutdown } } : null);
            yield return Until(() => complete != null, "forced disconnect requests local save");
            yield return new WaitForSecondsRealtime(.3f);
            ResourceCheck(Scenes.GetActiveScene().handle == origin && calls == 1, "scene remains loaded while asynchronous save is pending");
            ExitUi.ReturnToMainMenuFromGame();
            ResourceCheck(calls == 1, "repeated disconnect cannot start a second save or unload");
            if (!host) {
                complete(false); complete = null;
                yield return null; yield return null;
                ResourceCheck(Scenes.GetActiveScene().handle == origin && pd.GetInt("geo") == expectedGeo,
                    "failed save cancels exit and retains current progress");
                ExitUi.ReturnToMainMenuFromGame();
                yield return Until(() => complete != null && calls == 2, "player can retry save after failed exit");
            }
            complete(true);
            yield return Until(() => Scenes.GetActiveScene().name == "Menu_Title", "successful save allows native menu unload", 15);
            Flag(role + ".exit-complete");
            if (host) engine.ServerManager.Stop();
        } finally { On.GameManager.SaveGame_int_Action1 -= save; }
        Mark("PASS " + (host ? "server shutdown" : "timeout and failed-save retry") + " saves before native menu unload; probe blocked all disk writes");
    }
}
