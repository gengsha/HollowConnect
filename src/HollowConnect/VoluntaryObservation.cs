using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using Hkmp;
using HollowConnect.Core;
using UnityEngine;
using UnitySceneManager = UnityEngine.SceneManagement.SceneManager;

namespace HollowConnect;

public sealed partial class CoopRuntime {
    internal bool Observing { get; private set; }
    internal bool ObservationReturning { get; private set; }
    internal ushort ObservationTarget { get; private set; }
    private HeroController observationHero;
    private PlayerData observationPlayerData;
    private int observationProfile;
    private CameraController observationCamera;
    private int observationSceneHandle;
    private string observationScene;
    private Vector3 observationOrigin, observationCameraOrigin;
    private uint observationSerial;
    private bool observationAccepted, observationExitRequested, observationOffline, observationBodyHeld, observationBenchArrived;
    private bool observationSimulated, observationDamage, observationHeroBox;
    private readonly Dictionary<Collider2D, bool> observationColliders = new();
    private readonly PlayerVisualHide observationRenderers = new();
    private Coroutine observationReturn;

    private bool TryObservationTarget(ushort id, out GameObject avatar) {
        avatar = null;
        if (!Peers.TryGetValue(id, out var peer) || peer.State == null ||
            peer.State.Scene != UnitySceneManager.GetActiveScene().name ||
            (peer.Life != Life.Alive && peer.Life != Life.Loading) || Now < peer.Seen || Now - peer.Seen >= 2) return false;
        avatar = PlayerObject(id);
        return avatar != null && avatar.activeInHierarchy && Party.Finite(avatar.transform.position.x) && Party.Finite(avatar.transform.position.y);
    }
    private bool CanStartObservation() {
        if (!CanStartWarp || personalDeath != null || BossSettlementPending || observationReturn != null) return false;
        var hero = HeroController.instance;
        var state = hero.cState;
        var body = hero.GetComponent<Rigidbody2D>();
        var camera = GameCameras.instance?.cameraController;
        return PlayerData.instance.health > 0 && !PlayerData.instance.GetBool("atBench") &&
            state.onGround && !state.attacking && !state.dashing && !state.recoiling && !state.focusing &&
            !state.bouncing && !state.shroomBouncing && !state.wallSliding &&
            body != null && body.simulated && !body.isKinematic && body.gravityScale > 0 && !HeroBox.inactive &&
            camera != null && (camera.mode == CameraController.CameraMode.FOLLOWING || camera.mode == CameraController.CameraMode.LOCKED) &&
            api.ClientManager is Hkmp.Game.Client.ClientManager manager && manager.SceneSnapshotReady;
    }
    private void StartObservation() {
        if (!CoopMod.Settings.VoluntaryObservation) { Notice("主动观战已在本机设置中关闭"); return; }
        if (!CanStartObservation()) { Notice("请先落地，等待操作和场景加载完成后再观战"); return; }
        var targets = Peers.Keys.Where(id => TryObservationTarget(id, out _)).OrderBy(id => id).ToArray();
        if (targets.Length == 0) { Notice("没有同房存活队友可观战"); return; }
        ObservationTarget = targets.Contains(Selected) ? Selected : targets[0]; Selected = ObservationTarget;
        observationHero = HeroController.instance; observationCamera = GameCameras.instance.cameraController;
        observationPlayerData = PlayerData.instance; observationProfile = GameManager.instance.profileID;
        observationSceneHandle = UnitySceneManager.GetActiveScene().handle; observationScene = CoopContext.LocalScene;
        observationOrigin = observationHero.transform.position; observationCameraOrigin = observationCamera.transform.position;
        observationSimulated = observationHero.GetComponent<Rigidbody2D>().simulated;
        observationDamage = observationHero.takeNoDamage; observationHeroBox = HeroBox.inactive;
        observationAccepted = observationExitRequested = observationOffline = observationBenchArrived = false;
        Observing = observationBodyHeld = true; observationSerial++;
        HoldObservationBody();
        Send(new Message { Kind = Kind.ObservationRequest, Serial = observationSerial, Target = ObservationTarget, Scene = observationScene });
        SendPresence(); DebugEvent("observation-requested"); Notice("正在观战：" + ObservationName + " · 退出将返回德特茅斯长椅");
    }
    private string ObservationName => Peers.TryGetValue(ObservationTarget, out var peer) ? peer.State?.Name ?? "队友" : "队友";
    private void HoldObservationBody() {
        if (!observationBodyHeld || observationHero == null) return;
        observationHero.RelinquishControl(); observationHero.takeNoDamage = true; HeroBox.inactive = true;
        var body = observationHero.GetComponent<Rigidbody2D>(); body.velocity = Vector2.zero; body.simulated = false;
        if (UnitySceneManager.GetActiveScene().handle == observationSceneHandle) observationHero.transform.position = observationOrigin;
        foreach (var collider in observationHero.GetComponentsInChildren<Collider2D>(true)) {
            if (!observationColliders.ContainsKey(collider)) observationColliders[collider] = collider.enabled;
            collider.enabled = false;
        }
    }
    private void ReleaseObservationBody() {
        if (!observationBodyHeld) return;
        observationBodyHeld = false;
        foreach (var pair in observationColliders) if (pair.Key != null) pair.Key.enabled = pair.Value;
        observationColliders.Clear();
        if (observationHero != null) {
            observationHero.GetComponent<Rigidbody2D>().simulated = observationSimulated;
            observationHero.takeNoDamage = observationDamage;
            if (observationHero == HeroController.instance) HeroBox.inactive = observationHeroBox;
        }
    }
    // Only denial, completed arrival, destruction and emergency recovery may
    // release the body. An ordinary exit first obtains server authorization.
    private void StopObservation(string notice = null, bool restoreInput = false) {
        if (!Observing) return;
        ReleaseObservationBody(); observationRenderers.Restore();
        Observing = ObservationReturning = false;
        if (restoreInput && observationHero != null && observationHero == HeroController.instance &&
            UnitySceneManager.GetActiveScene().handle == observationSceneHandle && !GameManager.instance.IsInSceneTransition) {
            observationHero.RegainControl(); observationCamera?.PositionToHero(true);
        }
        observationHero = null; observationCamera = null; observationPlayerData = null;
        observationAccepted = observationExitRequested = observationOffline = observationBenchArrived = false;
        DebugEvent("observation-stopped");
        if (!string.IsNullOrEmpty(notice)) Notice(notice);
    }
    private void RequestObservationReturn() {
        if (!Observing || ObservationReturning) return;
        observationExitRequested = true;
        // Wait for reliable start acceptance before canceling a pending start.
        if (observationOffline || !Connected) BeginObservationReturn();
        else if (observationAccepted) Send(new Message { Kind = Kind.ObservationReturnRequest, Serial = observationSerial });
    }
    private void BeginObservationReturn() {
        if (!Observing || observationReturn != null) return;
        if (ObservationAtBench) {
            if (Resetting) { retryScene = "Town"; changingScene = false; Lock(true); }
            else if (observationOffline || !Connected) CompleteObservationReturn();
            return;
        }
        ObservationReturning = true; Notice("正在返回德特茅斯长椅…");
        observationReturn = StartCoroutine(RunObservationReturn());
    }
    private void ReceiveObservation(Message m) {
        if (!Observing || m.Id != LocalId || m.Serial != observationSerial || Resetting) return;
        switch (m.Kind) {
            case Kind.ObservationStarted:
                observationAccepted = true; DebugEvent("observation-started");
                if (observationExitRequested) RequestObservationReturn();
                break;
            case Kind.ObservationDenied:
                if (!observationAccepted) StopObservation("当前无法开始观战，已恢复操作", true);
                break;
            case Kind.ObservationReturn:
                observationAccepted = true; BeginObservationReturn(); break;
            case Kind.ObservationReturned:
                // The player may already have stood up while this reliable
                // acknowledgement was in flight. Keep the proven arrival.
                if (ObservationReturning && observationBenchArrived && Playable && UnitySceneManager.GetActiveScene().name == "Town") CompleteObservationReturn();
                break;
        }
    }
    private bool ObservationAtBench => observationBenchArrived && Playable && UnitySceneManager.GetActiveScene().name == "Town" && PlayerData.instance.GetBool("atBench");
    private void CompleteObservationReturn() {
        StopObservation("已回到德特茅斯长椅");
        if (Connected) {
            ApplyLinkedHealthState();
            Send(new Message { Kind = Kind.BenchRest, Serial = ++restSerial, Scene = "Town" }); SendPresence();
        }
    }
    private IEnumerator RunObservationReturn() {
        var routine = ReturnObservationToBench();
        try {
            while (true) {
                object current;
                try { if (!routine.MoveNext()) break; current = routine.Current; }
                catch (Exception e) {
                    Debug.LogError("HollowConnect observation return: " + e);
                    Notice("观战回椅未完成，可用 Ctrl+Alt+F12 紧急脱离"); break;
                }
                yield return current;
            }
        } finally { (routine as IDisposable)?.Dispose(); observationReturn = null; }
    }
    private IEnumerator ReturnObservationToBench() {
        yield return null;
        var gm = GameManager.instance; var pd = observationPlayerData; var slot = observationProfile;
        RequireEmergencySlot(gm, pd, slot);
        // Let an accepted native load settle; never cancel engine coroutines,
        // clear loading flags or stack two native SceneLoads.
        var deadline = Time.realtimeSinceStartup + 60;
        while (NativeSceneLoad.GetValue(gm) != null || gm.IsInSceneTransition) {
            RequireEmergencySlot(gm, pd, slot);
            if (Time.realtimeSinceStartup > deadline) throw new TimeoutException("Accepted native load did not settle");
            yield return null;
        }
        RequireEmergencySlot(gm, pd, slot);
        if (gm.isPaused) {
            UIManager.instance.TogglePauseGame();
            while (gm.isPaused) { RequireEmergencySlot(gm, pd, slot); yield return null; }
        }
        // Native Respawn owns the bench FSM, fade, health and final controls.
        // This path never calls Die, charges geo or creates/removes a shade.
        ReleaseObservationBody(); BossTravel.ResetForEmergency();
        pd.SetString("respawnScene", "Town"); pd.SetString("respawnMarkerName", "RestBench");
        pd.SetInt("respawnType", 1); pd.SetBool("respawnFacingRight", false);
        pd.SetString("dreamReturnScene", ""); pd.SetString("bossReturnEntryGate", "");
        var origin = UnitySceneManager.GetActiveScene().handle;
        gm.ReadyForRespawn(false); DebugEvent("observation-bench-native-load-requested");
        deadline = Time.realtimeSinceStartup + 60;
        while (!AtEmergencyBench(gm, pd, origin)) {
            RequireEmergencySlot(gm, pd, slot);
            if (Time.realtimeSinceStartup > deadline) throw new TimeoutException("Native observation bench arrival was not confirmed");
            yield return null;
        }
        observationBenchArrived = true;
        DebugEvent("observation-bench-arrived");
        // Scene entry and addon reliable events use separate queues. Native
        // bench arrival alone does not prove the server accepted this visit.
        deadline = Time.realtimeSinceStartup + 45;
        while (Connected && !observationOffline && !Resetting &&
            api.ClientManager is Hkmp.Game.Client.ClientManager manager && !manager.SceneSnapshotReady) {
            RequireEmergencySlot(gm, pd, slot);
            if (Time.realtimeSinceStartup > deadline) throw new TimeoutException("Observation return room snapshot did not commit");
            yield return null;
        }
        if (Resetting && Connected) { retryScene = "Town"; changingScene = false; Lock(true); yield break; }
        if (observationOffline || !Connected) { CompleteObservationReturn(); yield break; }
        var pos = HeroController.instance.transform.position;
        Send(new Message { Kind = Kind.ObservationReady, Serial = observationSerial, Aux = sequence, Scene = "Town", X = pos.x, Y = pos.y });
    }
    private void TickObservation() {
        if (!Observing || GameManager.instance == null) return;
        if (!ReferenceEquals(PlayerData.instance, observationPlayerData) || GameManager.instance.profileID != observationProfile ||
            GameManager.instance.gameState == GlobalEnums.GameState.MAIN_MENU || GameManager.instance.gameState == GlobalEnums.GameState.INACTIVE) {
            if (observationReturn != null) StopCoroutine(observationReturn);
            observationReturn = null; StopObservation(); return;
        }
        if (HeroController.instance == null) return;
        observationRenderers.Hide(HeroController.instance.gameObject);
        if (ObservationReturning || Resetting) return;
        if (!Connected) { observationOffline = true; RequestObservationReturn(); return; }
        if (!CoopMod.Settings.VoluntaryObservation || UnitySceneManager.GetActiveScene().handle != observationSceneHandle ||
            GameManager.instance.IsInSceneTransition) { RequestObservationReturn(); return; }
        HoldObservationBody();
        if (TryObservationTarget(ObservationTarget, out _)) return;
        var targets = Peers.Keys.Where(id => TryObservationTarget(id, out _)).OrderBy(id => id).ToArray();
        if (targets.Length > 0) { ObservationTarget = Selected = targets[0]; return; }
        RequestObservationReturn();
    }
    private bool ObservationInput() {
        if (KeyPressed(CoopMod.Settings.ObserveKey)) {
            if (Observing) RequestObservationReturn(); else StartObservation(); return true;
        }
        if (!Observing) return false;
        if (!ObservationReturning && KeyPressed(CoopMod.Settings.SelectTeammateKey)) {
            var targets = Peers.Keys.Where(id => TryObservationTarget(id, out _)).OrderBy(id => id).ToArray();
            if (targets.Length == 0) RequestObservationReturn();
            else { ObservationTarget = Selected = targets[(Array.IndexOf(targets, ObservationTarget) + 1) % targets.Length]; Notice("正在观战：" + ObservationName); }
        }
        return true;
    }
    private bool ObservationCameraUpdate(On.CameraController.orig_LateUpdate orig, CameraController self) {
        if (!Observing || ObservationReturning || Resetting || self != observationCamera || !TryObservationTarget(ObservationTarget, out var avatar)) return false;
        var mode = self.mode; self.mode = CameraController.CameraMode.FROZEN;
        try { orig(self); } finally { self.mode = mode; }
        var position = self.KeepWithinSceneBounds(avatar.transform.position); position.z = observationCameraOrigin.z;
        self.transform.position = position; return true;
    }
    private bool NonCombatAttack(GameObject source) {
        if (source == null) return false;
        if (Observing && HeroController.instance != null && source.transform.IsChildOf(HeroController.instance.transform)) return true;
        var owner = AttackSourcePlayer(source);
        if (owner >= 0 && owner != LocalId) return Peers.TryGetValue((ushort)owner, out var peer) && Party.Observer(peer.Life);
        return Observing && (owner == LocalId || source.GetComponent<DamageEnemies>() != null || source.GetComponent<DamageEffectTicker>() != null);
    }
    private void ObservationRelinquishControl(On.HeroController.orig_RelinquishControl orig, HeroController self) => orig(self);
    private void ObservationRegainControl(On.HeroController.orig_RegainControl orig, HeroController self) {
        orig(self); if (Observing && observationBodyHeld && self == observationHero) self.RelinquishControl();
    }
    private void ObservationIgnoreInput(On.HeroController.orig_IgnoreInput orig, HeroController self) => orig(self);
    private void ObservationRelinquishNotVelocity(On.HeroController.orig_RelinquishControlNotVelocity orig, HeroController self) => orig(self);
    private void ObservationPause(On.HeroController.orig_Pause orig, HeroController self) => orig(self);
}
