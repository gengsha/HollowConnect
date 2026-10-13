using System;
using System.Collections;
using Modding;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnitySceneManager = UnityEngine.SceneManagement.SceneManager;

namespace HollowConnect;

// Preserve each player's own native death route. The original Die coroutine
// owns animations, fragile charms, geo/shade placement, dream return and saves.
internal sealed class PersonalDeath {
    private readonly HeroController hero;
    private readonly Func<IEnumerator> originalDie;
    private readonly string benchScene, benchMarker, dreamScene, bossGate;
    private readonly int benchType;
    private readonly bool benchFacing;
    internal readonly string Destination, Marker;
    internal readonly int RespawnType;
    internal readonly int OriginHandle;
    internal readonly Vector3 Position;
    internal bool Started { get; private set; }

    internal PersonalDeath(HeroController hero, Func<IEnumerator> originalDie) {
        this.hero = hero; this.originalDie = originalDie;
        var pd = PlayerData.instance;
        benchScene = pd.GetString("respawnScene") ?? "";
        benchMarker = pd.GetString("respawnMarkerName") ?? "";
        benchType = pd.GetInt("respawnType"); benchFacing = pd.GetBool("respawnFacingRight");
        dreamScene = pd.GetString("dreamReturnScene") ?? "";
        bossGate = pd.GetString("bossReturnEntryGate") ?? "";
        var zone = GameManager.instance.GetCurrentMapZone();
        var dream = zone == "DREAM_WORLD" || zone == "GODS_GLORY";
        Destination = dream ? dreamScene : benchScene;
        Marker = dream ? "door_dreamReturn" : benchMarker;
        RespawnType = dream ? 2 : benchType;
        Position = hero.transform.position; OriginHandle = UnitySceneManager.GetActiveScene().handle;
    }

    internal void Start() {
        if (Started || hero == null) return;
        Started = true;
        var pd = PlayerData.instance;
        // These fields belong to this player's death, never a teammate's bench.
        pd.SetString("respawnScene", benchScene); pd.SetString("respawnMarkerName", benchMarker);
        pd.SetInt("respawnType", benchType); pd.SetBool("respawnFacingRight", benchFacing);
        pd.SetString("dreamReturnScene", dreamScene); pd.SetString("bossReturnEntryGate", bossGate);
        hero.transform.position = Position;
        hero.cState.dead = false;
        pd.SetInt("health", 0); pd.SetInt("healthBlue", 0);
        // Run on the native hero, so disconnecting/stopping our wait coroutine
        // cannot interrupt an accepted death or its subsequent native loading.
        hero.StartCoroutine(originalDie());
    }

    internal bool Arrived => Started && UnitySceneManager.GetActiveScene().handle != OriginHandle &&
        (Destination.Length == 0 || UnitySceneManager.GetActiveScene().name == Destination) &&
        HeroController.instance != null && HeroController.instance.isHeroInPosition &&
        !HeroController.instance.cState.dead && !GameManager.instance.IsInSceneTransition &&
        GameCameras.instance?.cameraController != null &&
        GameCameras.instance.cameraController.mode != CameraController.CameraMode.FROZEN;
}
