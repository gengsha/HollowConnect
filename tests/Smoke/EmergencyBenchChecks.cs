using System;
using System.Collections;
using System.Linq;
using System.Reflection;
using HollowConnect;
using HutongGames.PlayMaker.Actions;
using UnityEngine;
using Scenes = UnityEngine.SceneManagement.SceneManager;
using Object = UnityEngine.Object;

namespace HollowConnect.Smoke;

public sealed partial class ProgressProbe {
    private sealed class EmergencyBlockedLoad : GameManager.SceneLoadInfo {
        public override bool IsReadyToActivate() => false;
    }
    private static readonly BindingFlags EmergencyPrivate = BindingFlags.Instance | BindingFlags.NonPublic;
    private int emergencySaveRequests, emergencySaveCallbacks;
    private bool emergencySaveWasSafe = true;
    private bool EmergencyActive => (bool)typeof(CoopRuntime).GetField("emergencyBenchActive", EmergencyPrivate).GetValue(runtime);
    private void EmergencyRequire(bool ok, string label) {
        if (!ok) throw new Exception(label);
        Mark("PASS " + label);
    }
    private IEnumerator EmergencyBarrier(string label) {
        Flag(role + ".emergency." + label);
        yield return Until(() => Has((host ? "guest" : "host") + ".emergency." + label), "emergency barrier " + label, 90);
        if (Has(role + ".failed")) throw new Exception("emergency barrier failed " + label);
    }
    private PlayMakerFSM EmergencyBenchFsm() => Object.FindObjectsOfType<PlayMakerFSM>()
        .FirstOrDefault(f => f.gameObject.scene.name == "Town" && f.name == "RestBench" && f.FsmName == "Bench Control");

    private IEnumerator EmergencyMenuShortcut() {
        var gm = GameManager.instance;
        var slot = gm.profileID;
        var checkpoint = PlayerData.instance.GetString("respawnScene");
        gm.profileID = 99; // A closed save can leave this value behind.
        runtime.KeyHeld = _ => true;
        runtime.KeyPressed = key => key == KeyCode.F12;
        yield return null;
        yield return null;
        runtime.KeyPressed = _ => false; runtime.KeyHeld = _ => false;
        gm.profileID = slot;
        EmergencyRequire(!EmergencyActive && Scenes.GetActiveScene().name == "Menu_Title" &&
            PlayerData.instance.GetString("respawnScene") == checkpoint,
            "menu ignores emergency shortcut even with a stale loaded-slot number");
    }

    private IEnumerator EmergencyIncompleteShortcut() {
        var origin = Scenes.GetActiveScene().handle;
        foreach (var held in new[] { new KeyCode[0], new[] { KeyCode.LeftControl }, new[] { KeyCode.LeftAlt } }) {
            runtime.KeyHeld = key => held.Contains(key);
            runtime.KeyPressed = key => key == KeyCode.F12;
            yield return null;
            yield return null;
            EmergencyRequire(!EmergencyActive && runtime.Connected && Scenes.GetActiveScene().handle == origin,
                "F12 with " + held.Length + " modifier(s) does not recover");
        }
        runtime.KeyPressed = _ => false; runtime.KeyHeld = _ => false;
    }

    private IEnumerator PressEmergencyBench(string label, bool leftModifiers = true) {
        var origin = Scenes.GetActiveScene().handle;
        var pd = PlayerData.instance;
        var geo = pd.GetInt("geo"); var ore = pd.GetInt("ore");
        var shade = pd.GetString("shadeScene"); var pool = pd.GetInt("geoPool");
        var receipts = Newtonsoft.Json.JsonConvert.SerializeObject(CoopMod.Statistics);
        var savesBefore = emergencySaveRequests;
        var callbacksBefore = emergencySaveCallbacks;
        var pressed = false;
        runtime.KeyHeld = key => leftModifiers ? key == KeyCode.LeftControl || key == KeyCode.LeftAlt :
            key == KeyCode.RightControl || key == KeyCode.RightAlt;
        runtime.KeyPressed = key => key == KeyCode.F12 && !pressed && (pressed = true);
        yield return Until(() => pressed && EmergencyActive, label + " shortcut starts with native controls blocked", 5);
        // Keep reporting new presses while recovery runs. Stop before its
        // completion to avoid deliberately requesting a second recovery.
        runtime.KeyPressed = key => key == KeyCode.F12 && emergencySaveRequests == savesBefore;
        yield return Until(() => pressed && !EmergencyActive, label + " recovery completes", 65);
        runtime.KeyPressed = _ => false; runtime.KeyHeld = _ => false;
        if (Has(role + ".failed")) throw new Exception(label + " timed out");
        var hero = HeroController.instance;
        var bench = EmergencyBenchFsm();
        var snapshot = runtime.BuildDebugSnapshot();
        Mark("EMERGENCY " + label + " state=" + Newtonsoft.Json.JsonConvert.SerializeObject(snapshot) +
            " bench=" + bench?.ActiveStateName + " pos=" + hero.transform.position);
        EmergencyRequire(Scenes.GetActiveScene().handle != origin && Scenes.GetActiveScene().name == "Town" &&
            bench != null && bench.ActiveStateName == "Resting" && pd.GetBool("atBench") &&
            Vector2.Distance(hero.transform.position, bench.transform.position) < 6,
            label + " reaches a newly loaded native Dirtmouth chair");
        EmergencyRequire(!runtime.Connected && !runtime.Down && !runtime.Resetting && !runtime.Returning &&
            !HeroBox.inactive && hero.GetComponent<Rigidbody2D>().simulated &&
            !GameManager.instance.IsInSceneTransition && !GameManager.instance.IsLoadingSceneTransition &&
            GameCameras.instance.cameraController.mode != CameraController.CameraMode.FROZEN,
            label + " clears co-op/native/body/camera locks");
        EmergencyRequire(pd.GetBool("hasDreamNail") && pd.GetBool("defeatedDungDefender") &&
            pd.GetInt("geo") == geo && pd.GetInt("ore") == ore &&
            pd.GetString("shadeScene") == shade && pd.GetInt("geoPool") == pool &&
            Newtonsoft.Json.JsonConvert.SerializeObject(CoopMod.Statistics) == receipts,
            label + " preserves abilities boss progress geo shade and resource ledgers");
        EmergencyRequire(pd.GetString("respawnScene") == "Town" && pd.GetString("respawnMarkerName") == "RestBench" &&
            pd.GetInt("respawnType") == 1 && !GameManager.instance.isPaused && Time.timeScale > 0,
            label + " records Dirtmouth checkpoint and resumes time");
        EmergencyRequire(emergencySaveWasSafe && emergencySaveRequests == savesBefore + 1 &&
            emergencySaveCallbacks == callbacksBefore + 1,
            label + " requests one native save at the arrived bench after repeated key presses");
        // Native input event: recovery is incomplete if the bench cannot get up.
        bench.Fsm.Event(bench.Fsm.ActiveState.Actions.OfType<ListenForJump>().Single().wasPressed);
        yield return Until(() => !pd.GetBool("atBench") && hero.acceptingInput && !hero.controlReqlinquished &&
            !hero.GetComponent<Rigidbody2D>().isKinematic, label + " native bench get-up restores play", 8);
        if (Has(role + ".failed")) throw new Exception(label + " cannot get up");
    }

    private IEnumerator EmergencyBench() {
        On.GameManager.SaveGame_Action1 += (orig, gm, callback) => {
            var recovering = EmergencyActive;
            if (recovering) {
                emergencySaveRequests++;
                emergencySaveWasSafe &= gm.profileID == 99 && Scenes.GetActiveScene().name == "Town" &&
                    PlayerData.instance.GetBool("atBench") && EmergencyBenchFsm()?.ActiveStateName == "Resting" &&
                    !gm.IsInSceneTransition && gm.HasFinishedEnteringScene;
                Mark("EMERGENCY SAVE requested slot=" + gm.profileID + " scene=" + gm.sceneName +
                    " bench=" + EmergencyBenchFsm()?.ActiveStateName + " safe=" + emergencySaveWasSafe);
            }
            // The existing ProbeMod hook acknowledges this without disk IO.
            orig(gm, ok => { if (recovering && ok) emergencySaveCallbacks++; callback?.Invoke(ok); });
        };
        yield return Room("Crossroads_01");
        yield return EmergencyIncompleteShortcut();
        var pd = PlayerData.instance;
        pd.SetBool("hasDreamNail", true); pd.SetBool("defeatedDungDefender", true);
        pd.SetInt("ore", 2); pd.SetString("shadeScene", "Crossroads_04"); pd.SetInt("geoPool", 37);
        yield return new WaitForSecondsRealtime(1);
        yield return EmergencyBarrier("fixtures");
        foreach (var actor in new[] { "host", "guest" }) {
            if (role == actor) {
                var gm = GameManager.instance;
                var hero = HeroController.instance;
                hero.RelinquishControl(); HeroBox.inactive = true; hero.GetComponent<Rigidbody2D>().simulated = false;
                runtime.Down = runtime.Resetting = runtime.Returning = true;
                gm.isPaused = true; hero.Pause(); Time.timeScale = 0;
                typeof(GameManager).GetField("<IsInSceneTransition>k__BackingField", EmergencyPrivate).SetValue(gm, true);
                typeof(GameManager).GetField("isLoading", EmergencyPrivate).SetValue(gm, true);
                yield return PressEmergencyBench(actor + " stale paused co-op save", actor == "host");
                Flag(actor + ".emergency-actor-done");
            } else {
                yield return Until(() => Has(actor + ".emergency-actor-done"), actor + " emergency completes independently", 75);
                if (Has(role + ".failed")) throw new Exception("peer recovery failed");
                if (actor == "host") EmergencyRequire(runtime.Connected && Scenes.GetActiveScene().name == "Crossroads_01",
                    "host shortcut leaves guest in their room and session");
            }
            yield return EmergencyBarrier(actor + "-done");
        }

        // Native loading has already started when the key is pressed. A second
        // load must wait for it rather than discarding Unity's AsyncOperation.
        var manager = GameManager.instance;
        manager.BeginSceneTransition(new EmergencyBlockedLoad {
            SceneName = "Dream_04_White_Defender", EntryGateName = "dreamGate",
            Visualization = GameManager.SceneLoadVisualizations.Dream, forceWaitFetch = true
        });
        EmergencyRequire(manager.IsInSceneTransition && manager.IsLoadingSceneTransition,
            "offline dream load is accepted before shortcut");
        yield return PressEmergencyBench(role + " in-flight White Defender dream", false);

        // An already running native death owns GameManager coroutines. Make
        // it pending without executing its permanent geo/fragile-charm costs.
        var heroNow = HeroController.instance;
        heroNow.cState.dead = true; pd.SetInt("health", 0);
        heroNow.RelinquishControl(); HeroBox.inactive = true;
        heroNow.GetComponent<Rigidbody2D>().simulated = false;
        yield return PressEmergencyBench(role + " offline dead hero");
        yield return EmergencyBarrier("all-done");
    }
}
