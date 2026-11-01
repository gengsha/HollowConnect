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
        if (mode == "roundtrip") { yield return EndingRoomChecks(); yield break; }
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

        } finally { On.CutsceneHelper.DoSceneLoad -= trace; }
    }

}
