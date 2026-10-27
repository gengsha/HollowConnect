using System.Collections.Generic;
using System.Linq;
using Hkmp.Util;
using HutongGames.PlayMaker.Actions;
using Modding;
using UnityEngine;

namespace HollowConnect;

public sealed partial class CoopRuntime {
    private static bool FinishRadianceWarpPresentation(string sceneName) {
        var intro = Object.FindObjectsOfType<PlayMakerFSM>().FirstOrDefault(f =>
            f.gameObject.scene.name == sceneName && f.name == "Boss Control" && f.FsmName == "Control");
        if (intro == null) return false;
        if (intro.ActiveStateName == "Battle Start") {
            intro.transform.Find("Challenge Prompt Radiant")?.gameObject.SetActive(false);
            return true;
        }
        var terminal = intro.GetState("Battle Start");
        // This is the local presentation controller, separate from the boss's
        // Control/Phase Control FSMs. Validate its native terminal actions:
        // hero control, intro geometry and pause only; never activate the boss
        // or replay a remote AI state when finishing a teammate arrival.
        if (terminal == null || terminal.Transitions.Length != 0 || terminal.Actions.Length == 0 ||
            terminal.Actions.Any(a => !(a is SendMessage || a is ActivateGameObject || a is SetPlayerDataBool))) return false;
        foreach (var action in terminal.Actions) {
            if (action is SendMessage send &&
                (intro.Fsm.GetOwnerDefaultTarget(send.gameObject) != HeroController.instance.gameObject ||
                 !(send.functionCall.FunctionName == "FaceLeft" || send.functionCall.FunctionName == "RegainControl" ||
                   send.functionCall.FunctionName == "StartAnimationControl"))) return false;
            if (action is ActivateGameObject active && (active.activate.Value ||
                !(intro.Fsm.GetOwnerDefaultTarget(active.gameObject)?.name is "Intro Wall" or "CamLock Intro"))) return false;
            if (action is SetPlayerDataBool pause && (pause.boolName.Value != "disablePause" || pause.value.Value)) return false;
        }
        // A late player never used this local interaction. Leaving its Up
        // listener alive can take hero control after the fight has started,
        // when Boss Control no longer accepts the CHALLENGE event.
        intro.transform.Find("Challenge Prompt Radiant")?.gameObject.SetActive(false);
        // A slow snapshot can arrive after the local roar/title has begun.
        // Complete their native exit work before restoring hero control.
        HeroController.instance.gameObject.LocateMyFSM("Roar Lock")?.SendEvent("ROAR EXIT");
        intro.GetComponent<AudioSource>()?.Stop();
        intro.SetState("Battle Start");
        var title = intro.GetState("Title Up");
        if (title != null)
            foreach (var action in title.Actions) {
                if (action is ActivateGameObject active && !active.activate.Value) {
                    var target = intro.Fsm.GetOwnerDefaultTarget(active.gameObject);
                    if (target != null && target.name is "Radiance Roar" or "CamLock Challenge") target.SetActive(false);
                }
                if (action is SetFsmBool shake && !shake.setValue.Value && shake.fsmName.Value == "CameraShake") {
                    var target = intro.Fsm.GetOwnerDefaultTarget(shake.gameObject)?.LocateMyFSM("CameraShake");
                    var flag = target?.FsmVariables.GetFsmBool(shake.variableName.Value);
                    if (flag != null) flag.Value = false;
                }
            }
        var flashDown = intro.GetState("Flash Down");
        if (flashDown != null)
            foreach (var fade in flashDown.Actions.OfType<FadeGroupDown>())
                intro.Fsm.GetOwnerDefaultTarget(fade.target)?.GetComponent<FadeGroup>()?.FadeDownFast();
        // Leaving a local intro half way can cancel its fade tween with a
        // white overlay still opaque. Complete only the native zero-alpha
        // presentation fades, after the old state's OnExit has stopped them.
        foreach (var stateName in new[] { "Appear Boom", "Flash Down" }) {
            var state = intro.GetState(stateName);
            if (state == null) continue;
            foreach (var fade in state.Actions.OfType<iTweenFadeTo>().Where(a => a.alpha.Value == 0)) {
                var target = intro.Fsm.GetOwnerDefaultTarget(fade.gameObject);
                if (target == null || !(target.name is "White Fader" or "Light Solid")) continue;
                iTween.Stop(target);
                foreach (var renderer in target.GetComponentsInChildren<Renderer>(true)) {
                    var material = renderer.material;
                    foreach (var property in new[] { "_Color", "_TintColor" }) {
                        if (!material.HasProperty(property)) continue;
                        var color = material.GetColor(property); color.a = 0; material.SetColor(property, color);
                    }
                }
            }
        }
        // Keep the native refight audio, without its boss activation action.
        var refight = intro.GetState("Refight");
        if (refight != null) {
            foreach (var audio in refight.Actions.OfType<TransitionToAudioSnapshot>())
                (audio.snapshot.Value as UnityEngine.Audio.AudioMixerSnapshot)?.TransitionTo(audio.transitionTime.Value);
            foreach (var music in refight.Actions.OfType<ApplyMusicCue>())
                if (music.musicCue.Value is MusicCue cue)
                    GameManager.instance.AudioManager.ApplyMusicCue(cue, music.delayTime.Value, music.transitionTime.Value, false);
        }
        // Setup may already have hidden the HUD before the snapshot arrived.
        var ready = intro.GetState("Knight Ready");
        if (ready != null)
            foreach (var send in ready.Actions.OfType<SendEventByName>().Where(a => a.sendEvent.Value == "IN")) {
                var target = intro.Fsm.GetOwnerDefaultTarget(send.eventTarget.gameObject);
                if (target != null && target.name == "Hud Canvas")
                    foreach (var fsm in target.GetComponents<PlayMakerFSM>()) fsm.SendEvent("IN");
            }
        return true;
    }
    private static void RestoreRadianceWarpCamera(string sceneName) {
        var hero = HeroController.instance;
        var camera = GameCameras.instance?.cameraController;
        if (hero == null || camera == null) return;
        // Changing CameraMode alone leaves CameraTarget's lock bounds intact.
        // A teleport also skips the trigger exit/enter pair. Reconcile those
        // native locks once at the actual landing position.
        var oldLocks = ReflectionHelper.GetField<CameraController, List<CameraLockArea>>(camera, "lockZoneList");
        if (oldLocks != null)
            foreach (var area in oldLocks.ToArray())
                if (area != null) camera.ReleaseLock(area);
        var areas = Object.FindObjectsOfType<CameraLockArea>()
            .Where(a => a.isActiveAndEnabled && a.gameObject.scene.name == sceneName &&
                a.GetComponents<Collider2D>().Any(c => c.enabled && c.OverlapPoint(hero.transform.position)))
            .OrderBy(a => a.maxPriority).ToArray();
        foreach (var area in areas) camera.LockToArea(area);
        camera.SetMode(areas.Length > 0 ? CameraController.CameraMode.LOCKED : CameraController.CameraMode.FOLLOWING);
        // PositionToHero starts a coroutine that briefly freezes the camera.
        // Call it once; starting one every LateUpdate races its restore step.
        camera.PositionToHero(true);
    }
}
