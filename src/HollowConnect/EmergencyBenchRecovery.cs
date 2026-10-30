using System;
using System.Collections;
using System.Linq;
using System.Reflection;
using GlobalEnums;
using Hkmp;
using HollowConnect.Core;
using UnityEngine;
using UnitySceneManager = UnityEngine.SceneManagement.SceneManager;

namespace HollowConnect;

public sealed partial class CoopRuntime {
    internal Func<KeyCode, bool> KeyHeld = Input.GetKey;
    private bool emergencyBenchActive, emergencyAllowsNativeLoad, emergencyStatusVisible;
    private IEnumerator trackedTransition;
    private SceneLoad trackedLoader;
    private static readonly FieldInfo NativeSceneLoad = NativeField("sceneLoad");
    private static readonly FieldInfo NativeLoading = NativeField("isLoading");
    private static readonly FieldInfo NativeManualStart = NativeField("waitForManualLevelStart");
    private static readonly FieldInfo NativeTransition = NativeField("<IsInSceneTransition>k__BackingField");
    private static readonly FieldInfo NativeTimeSlowed = NativeField("timeSlowedCount");
    private static FieldInfo NativeField(string name) => typeof(GameManager).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic);

    // Checked before gameplay, pause, connection, rescue and warp guards. No
    // menu entry or confirmation is needed to use the emergency shortcut.
    private bool EmergencyShortcutPressed() => KeyPressed(KeyCode.F12) &&
        (KeyHeld(KeyCode.LeftControl) || KeyHeld(KeyCode.RightControl)) &&
        (KeyHeld(KeyCode.LeftAlt) || KeyHeld(KeyCode.RightAlt));

    private IEnumerator TrackNativeTransition(On.GameManager.orig_BeginSceneTransitionRoutine orig,
        GameManager gm, GameManager.SceneLoadInfo info) {
        if (emergencyBenchActive && !emergencyAllowsNativeLoad) return IgnoreEmergencyTransition();
        if (Observing && !ObservationReturning) RequestObservationReturn();
        var routine = orig(gm, info);
        if (NativeSceneLoad.GetValue(gm) == null) trackedTransition = routine;
        return routine;
    }
    private void TrackNativeLoader(SceneLoad loader) { if (Observing && !ObservationReturning) RequestObservationReturn(); trackedLoader = loader; }
    private IEnumerator IgnoreEmergencyTransition() {
        DebugEvent("emergency-bench-obsolete-transition-suppressed", DebugEventSeverity.Warning);
        yield break;
    }

    private void RequestEmergencyDirtmouthBench() {
        if (emergencyBenchActive) return;
        var gm = GameManager.UnsafeInstance;
        // A title-screen singleton still contains the last slot's PlayerData.
        // Never operate on it after that slot has been closed.
        if (gm == null || gm.profileID <= 0 || gm.gameState == GameState.MAIN_MENU ||
            gm.gameState == GameState.INACTIVE) return;
        if (observationReturn != null) StopCoroutine(observationReturn);
        observationReturn = null; StopObservation();
        emergencyBenchActive = true;
        emergencyStatusVisible = true;
        EmergencyNotice("紧急脱离：正在返回德特茅斯长椅…");
        DebugEvent("emergency-bench-requested", DebugEventSeverity.Warning);
        StartCoroutine(RunEmergencyRecovery(EmergencyReturnToDirtmouthBench(gm, PlayerData.instance, gm.profileID)));
    }

    // Catch coroutine failures so every exit releases the shortcut for retry.
    private IEnumerator RunEmergencyRecovery(IEnumerator routine) {
        try {
            while (true) {
                object current;
                try {
                    if (!routine.MoveNext()) break;
                    current = routine.Current;
                } catch (Exception e) {
                    DebugEvent("emergency-bench-failed", DebugEventSeverity.Critical, e.GetType().Name);
                    EmergencyNotice("紧急回椅未完成；可按 F9 导出诊断后重试");
                    UnityEngine.Debug.LogError("HollowConnect emergency bench recovery: " + e);
                    break;
                }
                yield return current;
            }
        } finally {
            (routine as IDisposable)?.Dispose();
            emergencyBenchActive = emergencyAllowsNativeLoad = false;
        }
    }

    private IEnumerator EmergencyReturnToDirtmouthBench(GameManager gm, PlayerData pd, int slot) {
        // Always yield once: repeated presses cannot start overlapping work,
        // even when the request is made while already sitting on the bench.
        yield return null;
        RequireEmergencySlot(gm, pd, slot);
        api?.ClientManager.Disconnect(); // also cancels a handshake without a co-op Session
        if (api != null && api.NetClient.IsConnected) throw new InvalidOperationException("Client did not disconnect");
        CancelBenchReturn(); CancelTransitions(); Unlock();
        Down = Resetting = Returning = changingScene = false;
        CoopContext.Resetting = false; readySent = giveUpRequested = false;
        personalDeath = null; retryScene = null; warpEntry = null; warpScene = null;
        shades?.Clear(); teammateDream?.Reset();
        ResumeEmergencyGame(gm);

        // A Unity AsyncOperation cannot be cancelled safely. Let the accepted
        // load settle, releasing only its outer readiness wait when we know the
        // exact native coroutine. Never discard a live SceneLoad or start a
        // second load over it; both can strand Unity's activation queue.
        var loading = NativeSceneLoad.GetValue(gm) as SceneLoad;
        if (loading != null) {
            DebugEvent("emergency-bench-wait-native-load", DebugEventSeverity.Warning);
            if (ReferenceEquals(loading, trackedLoader) && trackedTransition != null) {
                gm.StopCoroutine(trackedTransition);
                loading.IsFetchAllowed = true;
                loading.IsActivationAllowed = true;
            }
            var deadline = Time.realtimeSinceStartup + 45f;
            while (NativeSceneLoad.GetValue(gm) != null) {
                RequireEmergencySlot(gm, pd, slot);
                if (Time.realtimeSinceStartup >= deadline)
                    throw new TimeoutException("Native scene loading did not finish");
                yield return null;
            }
        }
        RequireEmergencySlot(gm, pd, slot);
        var heroDeadline = Time.realtimeSinceStartup + 15f;
        while (HeroController.instance == null && Time.realtimeSinceStartup < heroDeadline) yield return null;
        RequireEmergencySlot(gm, pd, slot);
        var hero = HeroController.instance;
        if (hero == null) throw new InvalidOperationException("No loaded hero");

        // Only now can all old death/hazard/pause coroutines be abandoned.
        // These fields describe loading, not save progress. A stale transition
        // flag without a SceneLoad is cleared before the new native load.
        gm.StopAllCoroutines(); hero.StopAllCoroutines();
        NativeLoading.SetValue(gm, false);
        NativeManualStart.SetValue(gm, false);
        NativeTransition.SetValue(gm, false);
        NativeTimeSlowed?.SetValue(gm, 0);
        gm.RespawningHero = false;
        BossTravel.ResetForEmergency();
        ResumeEmergencyGame(gm);
        hero.gameObject.SetActive(true); hero.enabled = true;
        hero.ResetState(); hero.SetHeroParent(null);
        hero.transitionState = HeroTransitionState.WAITING_TO_TRANSITION;
        hero.RegainControl(); hero.StartAnimationControl();
        hero.takeNoDamage = false; HeroBox.inactive = false;
        var body = hero.GetComponent<Rigidbody2D>();
        body.simulated = true; body.bodyType = RigidbodyType2D.Dynamic; body.velocity = Vector2.zero;
        var renderer = hero.GetComponent<Renderer>();
        if (renderer != null) { renderer.enabled = true; renderer.forceRenderingOff = false; }
        pd.SetBool("disablePause", false); pd.SetBool("atBench", false);

        // The destination belongs only to this loaded slot. Native Respawn
        // handles health, the RestBench FSM, collision, fade and hero position;
        // this does not invoke Die, charge geo or create/remove a shade.
        pd.SetString("respawnScene", "Town"); pd.SetString("respawnMarkerName", "RestBench");
        pd.SetInt("respawnType", 1); pd.SetBool("respawnFacingRight", false);
        pd.SetString("dreamReturnScene", ""); pd.SetString("bossReturnEntryGate", "");
        var origin = UnitySceneManager.GetActiveScene().handle;
        emergencyAllowsNativeLoad = true;
        try { gm.ReadyForRespawn(false); }
        finally { emergencyAllowsNativeLoad = false; }
        DebugEvent("emergency-bench-native-load-requested");

        var arrivalDeadline = Time.realtimeSinceStartup + 45f;
        while (!AtEmergencyBench(gm, pd, origin)) {
            RequireEmergencySlot(gm, pd, slot);
            if (Time.realtimeSinceStartup >= arrivalDeadline)
                throw new TimeoutException("Dirtmouth bench arrival was not confirmed");
            yield return null;
        }
        // Save through the game's own API only after native bench/camera/hero
        // arrival is verified. Never serialize or overwrite save files here.
        RequireEmergencySlot(gm, pd, slot);
        bool? saved = null;
        gm.SaveGame(ok => saved = ok);
        var saveDeadline = Time.realtimeSinceStartup + 10f;
        while (!saved.HasValue && Time.realtimeSinceStartup < saveDeadline) {
            RequireEmergencySlot(gm, pd, slot);
            yield return null;
        }
        DebugEvent(saved == true ? "emergency-bench-complete" : "emergency-bench-save-unconfirmed",
            saved == true ? DebugEventSeverity.Info : DebugEventSeverity.Warning);
        EmergencyNotice(saved == true ? "已回到德特茅斯长椅并保存；可重新联机" :
            "已回到德特茅斯长椅；保存尚未确认，请正常退出保存");
    }

    private static void RequireEmergencySlot(GameManager gm, PlayerData pd, int slot) {
        if (gm == null || !ReferenceEquals(PlayerData.instance, pd) || gm.profileID != slot ||
            gm.gameState == GameState.MAIN_MENU || gm.gameState == GameState.INACTIVE)
            throw new InvalidOperationException("Loaded save slot changed during recovery");
    }
    private static bool AtEmergencyBench(GameManager gm, PlayerData pd, int origin) {
        var scene = UnitySceneManager.GetActiveScene();
        var hero = HeroController.instance;
        if (scene.handle == origin || scene.name != "Town" || gm.sceneName != "Town" ||
            gm.IsInSceneTransition || !gm.HasFinishedEnteringScene || hero == null ||
            !hero.isHeroInPosition || !pd.GetBool("atBench") || HeroBox.inactive ||
            !hero.GetComponent<Rigidbody2D>().simulated ||
            GameCameras.instance?.cameraController == null ||
            GameCameras.instance.cameraController.mode == CameraController.CameraMode.FROZEN) return false;
        var bench = scene.GetRootGameObjects().SelectMany(g => g.GetComponentsInChildren<PlayMakerFSM>(true))
            .FirstOrDefault(f => f.name == "RestBench" && f.FsmName == "Bench Control");
        return bench != null && bench.ActiveStateName == "Resting" &&
            Vector2.Distance(hero.transform.position, bench.transform.position) < 6;
    }
    private static void ResumeEmergencyGame(GameManager gm) {
        Time.timeScale = 1f; gm.isPaused = false; gm.SetState(GameState.PLAYING);
        HeroController.instance?.UnPause();
        gm.inputHandler?.AllowPause(); gm.inputHandler?.StartAcceptingInput();
        if (gm.ui != null && gm.ui.uiState != UIState.PLAYING) {
            gm.ui.UIClosePauseMenu(); gm.ui.SetState(UIState.PLAYING); gm.ui.AudioGoToGameplay(.1f);
        }
    }
    private void EmergencyNotice(string message) {
        Notice(message); statusUntil = Time.unscaledTime + 15f;
        emergencyStatusVisible = true;
    }
}
