using System;
using System.Collections;
using System.IO;
using System.Linq;
using System.Reflection;
using MonoMod.RuntimeDetour;
using UnityEngine;
using Object = UnityEngine.Object;

namespace HollowConnect.Smoke;

public sealed partial class EndingHudProbe {
    private string HudState() {
        var cameras = GameCameras.instance;
        var canvas = cameras.hudCanvas;
        return "scene=" + GameManager.instance.sceneName + " canvas=" + canvas.activeSelf + "/" + canvas.activeInHierarchy +
            " pos=" + canvas.transform.localPosition + " camera=" + cameras.hudCamera.gameObject.activeInHierarchy + "/" + cameras.hudCamera.enabled +
            " fsms=" + string.Join(",", canvas.GetComponents<PlayMakerFSM>().Select(f => f.FsmName + ":" + f.enabled + ":" + f.ActiveStateName)) +
            " entering=" + GameManager.instance.HasFinishedEnteringScene + " changing=" + GameManager.instance.IsInSceneTransition;
    }

    private void EndingHudDump(PlayMakerFSM fsm) {
        var path = Path.Combine(root, role + ".ending-fsm.txt");
        void Write(string value) => File.AppendAllText(path, value + "\n");
        Write("FSM " + fsm.name + "/" + fsm.FsmName + "=" + fsm.ActiveStateName);
        foreach (var state in fsm.FsmStates) {
            Write("STATE " + state.Name + " transitions=" + string.Join(",", state.Transitions.Select(t => t.EventName + ":" + t.ToState)));
            foreach (var action in state.Actions) Write("ACTION " + action.GetType().Name + " " + string.Join(";",
                action.GetType().GetFields(BindingFlags.Instance | BindingFlags.Public).Select(f => f.Name + "=" + f.GetValue(action))));
        }
        Write("BOOLS " + string.Join(",", fsm.FsmVariables.BoolVariables.Select(v => v.Name + "=" + v.Value)));
    }

    private IEnumerator RadianceEndingHudChecks() {
        using var records = new CoverageFinalBossEndingChecks(Mark);
        var mode = Environment.GetEnvironmentVariable("HOLLOWCONNECT_RADIANCE_ENDING_HUD");
        Mark("ENDING HUD INITIAL " + HudState());
        On.CutsceneHelper.hook_DoSceneLoad trace = (orig, helper) => {
            Mark("ENDING HUD NATIVE EXIT helper=" + helper.name + " scene=" + helper.gameObject.scene.name + " next=" + helper.nextSceneType +
                " record=" + GameManager.instance.GetStatusRecordInt("RecPermadeathMode") + " " + HudState());
            orig(helper);
        };
        On.CutsceneHelper.DoSceneLoad += trace;
        try {
            GameManager.instance.ChangeToScene("Cinematic_Ending_C", "", 0);
            yield return Until(() => GameManager.instance.sceneName == "Cinematic_Ending_C",
                "native Radiance cinematic C loaded", 50);
            yield return new WaitForSecondsRealtime(3);
            Mark("ENDING HUD CINEMATIC " + HudState());
            foreach (var scene in Enumerable.Range(0, UnityEngine.SceneManagement.SceneManager.sceneCount)
                .Select(UnityEngine.SceneManagement.SceneManager.GetSceneAt))
                Mark("ENDING LOADED SCENE " + scene.name + " active=" + (scene.handle == UnityEngine.SceneManagement.SceneManager.GetActiveScene().handle));
            foreach (var component in Object.FindObjectsOfType<MonoBehaviour>(true).Where(c => c.gameObject.scene.name == "Cinematic_Ending_C"))
                Mark("ENDING COMPONENT " + component.name + "/" + component.GetType().FullName + " active=" + component.gameObject.activeInHierarchy);
            foreach (var helper in Object.FindObjectsOfType<CutsceneHelper>())
                Mark("ENDING HELPER " + helper.name + " next=" + helper.nextSceneType + " skip=" + helper.skipMode + " locked=" + helper.startSkipLocked);
            foreach (var fsm in Object.FindObjectsOfType<PlayMakerFSM>(true).Where(f => f.gameObject.scene.name == "Cinematic_Ending_C")) EndingHudDump(fsm);
            foreach (var fsm in GameCameras.instance.hudCanvas.GetComponents<PlayMakerFSM>()) EndingHudDump(fsm);
            Check(mode is "schema" or "roundtrip", "native cinematic schema observed");
            if (mode == "roundtrip") yield return RadianceEndingHudReturn();
        } finally { On.CutsceneHelper.DoSceneLoad -= trace; }
    }

    private IEnumerator RadianceEndingHudReturn() {
        // End_Game_Completion builds the native journal list while the test
        // copy has no populated enemy catalog. Keep that disposable UI helper
        // from throwing so the real credits/return boundary can finish.
        using var journalGuard = new Hook(typeof(JournalList).GetMethod("BuildEnemyList", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic),
            new Action<Action<JournalList>, JournalList>((orig, list) => { }));
        var cameras = GameCameras.instance;
        var canvas = cameras.hudCanvas;
        var startPosition = canvas.transform.localPosition;
        var playerData = PlayerData.instance;
        var slot = GameManager.instance.profileID;
        Flag(role + ".ending-c-ready");
        yield return Until(() => Has((role == "host" ? "guest" : "host") + ".ending-c-ready"),
            "both players reached native story ending C");
        if (role == "guest")
            yield return Until(() => Has("host.ending-hud-returned"), "host completed story ending return", 100);

        var cinematic = Object.FindObjectsOfType<CinematicPlayer>().Single();
        yield return Until(() => Field<bool>(cinematic, "videoTriggered"),
            "native story ending video started", 40);
        Mark("ENDING VIDEO type=" + cinematic.videoType + " skip=" + cinematic.skipMode +
            " locked=" + cinematic.startSkipLocked + " triggered=" + Field<bool>(cinematic, "videoTriggered"));
        // Invoke the game's own skip coroutine. It fades/stops the actual
        // video and leaves FinishVideo/End_Credits/next-scene logic native.
        cinematic.StartCoroutine(cinematic.SkipVideo());
        yield return Until(() => GameManager.instance.sceneName == "End_Credits",
            "native story ending video advances to credits", 45);
        CutsceneHelper helper = null;
        yield return Until(() => (helper = Object.FindObjectsOfType<CutsceneHelper>().FirstOrDefault()) != null,
            "native credits helper exists", 45);
        Mark("ENDING CREDITS helper=" + helper.name + " next=" + helper.nextSceneType +
            " skip=" + helper.skipMode + " locked=" + helper.startSkipLocked);
        helper.LoadNextScene();
        var returnDeadline = Time.realtimeSinceStartup + 100;
        while (!(GameManager.instance.sceneName == "Town" && runtime.Playable &&
                  GameManager.instance.HasFinishedEnteringScene && HeroController.instance.isHeroInPosition) &&
               Time.realtimeSinceStartup < returnDeadline) {
            // First-clear credits may pass through End_Game_Completion and a
            // second native helper before the main-menu hook redirects to the
            // multiplayer checkpoint. Continue only through helpers supplied
            // by the loaded native scene.
            var nextHelper = Object.FindObjectsOfType<CutsceneHelper>().FirstOrDefault();
            if (nextHelper != null && nextHelper != helper &&
                GameManager.instance.sceneName != "End_Credits") {
                helper = nextHelper;
                Mark("ENDING FOLLOWUP helper=" + helper.name + " scene=" + GameManager.instance.sceneName +
                    " next=" + helper.nextSceneType);
                helper.LoadNextScene();
            }
            yield return null;
        }
        Check(GameManager.instance.sceneName == "Town" && runtime.Playable &&
            GameManager.instance.HasFinishedEnteringScene && HeroController.instance.isHeroInPosition,
            "native story credits returns to personal checkpoint");
        yield return Until(() => cameras.hudCamera.gameObject.activeInHierarchy && cameras.hudCamera.enabled &&
            canvas.activeInHierarchy && canvas.GetComponents<PlayMakerFSM>().All(f => f.enabled && f.gameObject.activeInHierarchy) &&
            Vector3.Distance(canvas.transform.localPosition, startPosition) < .05f,
            "native story return restores health and soul HUD", 10);
        Check(runtime.Connected && PlayerData.instance == playerData && GameManager.instance.profileID == slot,
            "story ending return preserves multiplayer and personal profile");
        Mark("STORY ENDING HUD RESTORED camera=" + cameras.hudCamera.gameObject.activeInHierarchy +
            "/" + cameras.hudCamera.enabled + " canvas=" + canvas.activeSelf + "/" + canvas.activeInHierarchy +
            " fsms=" + string.Join(",", canvas.GetComponents<PlayMakerFSM>().Select(f =>
                f.FsmName + ":" + f.enabled + ":" + f.gameObject.activeInHierarchy + ":" + f.ActiveStateName)));
        Flag(role + ".ending-hud-returned");
    }
}
