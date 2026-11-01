using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Hkmp.Game.Server;
using Hkmp.Networking.Server;
using MonoMod.RuntimeDetour;
using UnityEngine;
using Object = UnityEngine.Object;

namespace HollowConnect.Smoke;

public sealed partial class EndingHudProbe {
    private bool EndingServerStarted => ((NetServer)typeof(ServerManager).GetField("_netServer",
        BindingFlags.Instance | BindingFlags.NonPublic).GetValue(engine.ServerManager)).IsStarted;

    // Native movie/credits/title boundary fixture. It does not stand in for
    // defeating every final boss, and never reads or writes a personal save.
    private IEnumerator EndingRoomChecks() {
        var ending = Environment.GetEnvironmentVariable("HOLLOWCONNECT_ENDING_ROOM_CASE") ?? "C";
        if (ending == "travel") { yield return EndingTravelControl(); yield break; }
        using var records = new CoverageFinalBossEndingChecks(Mark);
        using var journal = new Hook(typeof(JournalList).GetMethod("BuildEnemyList", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic),
            new Action<Action<JournalList>, JournalList>((orig, list) => { }));
        var pd = PlayerData.instance;
        var slot = GameManager.instance.profileID;
        var hudPosition = GameCameras.instance.hudCanvas.transform.localPosition;
        var bossRush = Environment.GetEnvironmentVariable("HOLLOWCONNECT_ENDING_BOSS_RUSH") == "1";
        pd.SetBool("bossRushMode", bossRush);
        if (bossRush) PrepareEndingGodhome();
        var first = Environment.GetEnvironmentVariable("HOLLOWCONNECT_ENDING_FIRST") ?? "guest";
        var scene = "Cinematic_Ending_" + ending;
        var nativeFinish = 0;
        On.CinematicPlayer.hook_FinishVideo finish = (orig, player) => {
            if (player.gameObject.scene.name == scene) {
                nativeFinish++;
                Mark("ENDING NATIVE FINISH role=" + role + " scene=" + scene + " connected=" + runtime.Connected + " server=" + EndingServerStarted);
            }
            orig(player);
        };
        On.CinematicPlayer.FinishVideo += finish;
        try {
            if (role != first) {
                yield return Until(() => Has(first + ".ending-video-started"), "first participant started ending", 60);
                Check(runtime.Connected && GameManager.instance.sceneName == "Town", "first ending leaves other participant in its own gameplay scene");
            }
            GameManager.instance.ChangeToScene(scene, "", 0);
            yield return Until(() => GameManager.instance.sceneName == scene &&
                UnityEngine.SceneManagement.SceneManager.GetActiveScene().name == scene, "native ending scene loaded", 50);
            var cinematic = Object.FindObjectsOfType<CinematicPlayer>().Single();
            yield return Until(() => Field<bool>(cinematic, "videoTriggered"), "actual native ending video started", 40);
            Mark("ENDING VIDEO START " + scene + " type=" + cinematic.videoType + " connected=" + runtime.Connected +
                " server=" + EndingServerStarted + " " + HudState());
            if (role == "host") Check(runtime.Connected && EndingServerStarted, "host keeps room open while ending video plays");
            else Check(!runtime.Connected && !engine.NetClient.IsConnected && !Field<bool>(engine.ClientManager, "_autoConnect"),
                "guest leaves room on ending entry without reconnecting");
            Flag(role + ".ending-video-started");
            yield return Until(() => Has((role == "host" ? "guest" : "host") + ".ending-video-started"), "both native ending videos started", 60);
            yield return new WaitForSecondsRealtime(1);
            Check(GameManager.instance.sceneName == scene && Field<bool>(cinematic, "videoTriggered") && nativeFinish == 0,
                "disconnect does not interrupt or finish native ending video");
            if (role == "host") Check(EndingServerStarted && runtime.Connected, "room remains open until host video finishes");
            Flag(role + ".ending-video-verified");
            if (role == "host") {
                yield return Until(() => Has("guest.ending-video-verified"), "guest verified independent video");
                cinematic.StartCoroutine(cinematic.SkipVideo());
                yield return Until(() => nativeFinish == 1 && !runtime.Connected && !EndingServerStarted,
                    "native host video finish disconnects host and closes server", 45);
                Flag("host.ending-room-closed");
            } else {
                yield return Until(() => Has("host.ending-room-closed"), "host closes room on video finish", 60);
                Check(GameManager.instance.sceneName == scene && Field<bool>(cinematic, "videoTriggered") && nativeFinish == 0,
                    "host room close preserves guest ending video");
                cinematic.StartCoroutine(cinematic.SkipVideo());
            }
            yield return Until(() => GameManager.instance.sceneName == (bossRush ? "GG_End_Sequence" : "End_Credits"),
                bossRush ? "native video advances to Godhome result" : "native video advances to credits", 45);
            Check(nativeFinish == 1 && !runtime.Connected, "ending video finishes once with client offline");
            if (bossRush) {
                yield return EndingGodhomeReturn(hudPosition);
                yield break;
            }
            yield return EndingNativeTitleReturn();
            // All saves are blocked. Reload the disposable in-memory character
            // through the ordinary title ContinueGame path to verify HUD/input.
            var resumed = PlayerData.instance;
            resumed.SetString("respawnScene", "Town"); resumed.SetString("respawnMarkerName", "RestBench"); resumed.SetInt("respawnType", 1);
            GameManager.instance.profileID = slot;
            GameManager.instance.ContinueGame();
            yield return Until(() => GameManager.instance.sceneName == "Town" && runtime.Playable &&
                GameManager.instance.HasFinishedEnteringScene && HeroController.instance.isHeroInPosition,
                "native continue from title reenters personal checkpoint", 60);
            yield return EndingBenchGetUp();
            yield return Until(() => GameCameras.instance.hudCamera.enabled && GameCameras.instance.hudCamera.gameObject.activeInHierarchy &&
                GameCameras.instance.hudCanvas.activeInHierarchy && GameCameras.instance.hudCanvas.GetComponents<PlayMakerFSM>()
                    .All(f => f.enabled && f.gameObject.activeInHierarchy) &&
                Vector3.Distance(GameCameras.instance.hudCanvas.transform.localPosition, hudPosition) < .05f,
                "native title continue restores health and soul HUD camera canvas and FSMs", 12);
            Mark("ENDING HUD RESUMED " + HudState());
            Check(!runtime.Connected && !EndingServerStarted, "ending return does not reconnect or reopen room");
            Flag(role + ".ending-resumed");
            yield return Until(() => Has((role == "host" ? "guest" : "host") + ".ending-resumed"), "both offline ending returns complete", 90);
        } finally { On.CinematicPlayer.FinishVideo -= finish; }
    }

    private IEnumerator EndingNativeTitleReturn() {
        var seen = new HashSet<int>();
        var deadline = Time.realtimeSinceStartup + 90;
        while ((GameManager.instance == null || GameManager.instance.sceneName != "Menu_Title") && Time.realtimeSinceStartup < deadline) {
            foreach (var helper in Object.FindObjectsOfType<CutsceneHelper>()) {
                if (!seen.Add(helper.GetInstanceID())) continue;
                Mark("ENDING NATIVE HELPER scene=" + helper.gameObject.scene.name + " next=" + helper.nextSceneType);
                helper.LoadNextScene();
            }
            yield return null;
        }
        Check(GameManager.instance != null && GameManager.instance.sceneName == "Menu_Title", "native credits completion unlock returns to title without checkpoint redirect");
        yield return Until(() => UIManager.instance != null && GameManager.instance.gameState == GlobalEnums.GameState.MAIN_MENU,
            "native title UI ready", 20);
    }

    private IEnumerator EndingTravelControl() {
        PlayerData.instance.SetString("nextScene", "Room_Town_Stag_Station");
        GameManager.instance.ChangeToScene("Cinematic_Stag_travel", "", 0);
        StagTravel travel = null;
        yield return Until(() => UnityEngine.SceneManagement.SceneManager.GetActiveScene().name == "Cinematic_Stag_travel" &&
            (travel = Object.FindObjectOfType<StagTravel>()) != null, "native stag scene and controller loaded", 50);
        var sequence = (CinematicSequence)typeof(StagTravel).GetField("cinematicSequence",
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic).GetValue(travel);
        yield return Until(() => sequence.VideoPlayer != null && sequence.VideoPlayer.IsPlaying, "native stag video started", 40);
        Check(runtime.Connected && (role != "host" || EndingServerStarted), "non-ending cinematic preserves room connection");
        Flag(role + ".travel-checked");
        yield return Until(() => Has((role == "host" ? "guest" : "host") + ".travel-checked"), "both non-ending cinematics checked");
        // StagTravel owns automatic completion and asynchronous destination
        // loading in this game version; do not substitute CinematicPlayer.
        yield return Until(() => GameManager.instance.sceneName == "Room_Town_Stag_Station" && GameManager.instance.HasFinishedEnteringScene,
            "native travel video returns to gameplay", 50);
        Check(runtime.Connected, "non-ending return remains connected");
    }

    private IEnumerator EndingBenchGetUp() {
        if (PlayerData.instance.GetBool("atBench")) {
            var bench = Object.FindObjectsOfType<PlayMakerFSM>().Single(f => f.name == "RestBench" && f.FsmName == "Bench Control");
            yield return Until(() => bench.ActiveStateName == "Resting", "native initial bench resting");
            bench.Fsm.Event(bench.Fsm.ActiveState.Actions.OfType<HutongGames.PlayMaker.Actions.ListenForJump>().Single().wasPressed);
        }
        yield return Until(() => HeroController.instance.acceptingInput && !HeroController.instance.controlReqlinquished &&
            HeroController.instance.GetComponent<Rigidbody2D>().simulated, "native bench get-up restores interaction");
    }
}
