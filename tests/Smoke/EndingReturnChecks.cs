using System;
using System.Collections;
using System.IO;
using System.Linq;
using System.Reflection;
using HollowConnect;
using UnityEngine;
using Object = UnityEngine.Object;

namespace HollowConnect.Smoke;

public sealed partial class PantheonProbe {
    // Focused native result-screen fixture, not a complete Pantheon clear.
    // The source door and F8 join are real, as are the result UI and return.
    private IEnumerator EndingReturnChecks() {
        var tier = Environment.GetEnvironmentVariable("HOLLOWCONNECT_ENDING_RETURN");
        var atrium = tier == "5" ? "GG_Atrium_Roof" : "GG_Atrium";
        var sequence = Resources.LoadAll<BossSequence>("GG").Single(s => s.name == "Boss Sequence Tier " + tier);
        var pd = PlayerData.instance;
        // Disposable profile has this door unlocked, matching the reported
        // characters. Its native Start must keep the return gate active.
        pd.SetVariable("bossDoorStateTier" + tier, new BossSequenceDoor.Completion { unlocked = true, canUnlock = true });
        pd.SetString("dreamReturnScene", "GG_Waterways");
        pd.SetString("bossReturnEntryGate", "");
        if (host) {
            yield return NativeEntrance(sequence);
            SafeHero();
            Flag("ending.host.entered");
        } else {
            // Match the report's guest start in Godhome, rather than a fresh
            // character still owned by Town's initial bench/wake sequence.
            GameManager.instance.BeginSceneTransition(new GameManager.SceneLoadInfo {
                SceneName = atrium, EntryGateName = tier == "5" ? "door_dreamReturnGG_GG_Final_Challenge_Door" : "door_dreamEnter",
                Visualization = GameManager.SceneLoadVisualizations.Default
            });
            yield return Until(() => In(atrium), "guest native Godhome staging entry");
            yield return Until(() => Has("ending.host.entered") && runtime.Peers.Values.Any(p => p.State?.IsReady == true), "host native door context ready", 120);
            Mark("F8 PRECHECK " + Newtonsoft.Json.JsonConvert.SerializeObject(runtime.BuildDebugSnapshot()) + " paused=" + GameManager.instance.isPaused + " scale=" + Time.timeScale);
            runtime.Selected = runtime.Peers.Single(p => p.Value.State?.IsReady == true).Key;
            var original = runtime.KeyPressed;
            yield return Until(() => (bool)typeof(CoopRuntime).GetProperty("CanStartWarp", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(runtime), "guest eligible for F8");
            var pressed = false;
            try {
                runtime.KeyPressed = key => key == CoopMod.Settings.TeleportKey && !pressed && (pressed = true);
                yield return Until(() => pressed, "one F8 input consumed", 5);
                runtime.KeyPressed = _ => false;
                yield return Until(() => In(sequence.GetSceneAt(0)), "guest F8 joins native sequence");
            } finally { runtime.KeyPressed = original; }
            SafeHero();
        }
        yield return Both("ending.joined");
        yield return Until(() => engine.ClientManager.SceneSnapshotReady, "joined room committed");
        var route = BossTravel.CaptureContext();
        var expectedGate = pd.GetString("bossReturnEntryGate");
        if (pd.GetString("dreamReturnScene") != atrium || string.IsNullOrEmpty(expectedGate))
            throw new Exception("Sequence join lost native return destination: " + pd.GetString("dreamReturnScene") + "/" + expectedGate);
        Mark("PASS full native return destination captured after UDP/F8 join " + atrium + "/" + expectedGate);

        // Rejected updates and a cancelled overlay must preserve both fields.
        var rollback = BossTravel.SaveState();
        try {
            pd.SetString("dreamReturnScene", "Town"); pd.SetString("bossReturnEntryGate", "sentinel");
            rollback();
            if (pd.GetString("dreamReturnScene") != atrium || pd.GetString("bossReturnEntryGate") != expectedGate)
                throw new Exception("Cancelled context failed to restore the complete return destination");
            var invalid = Newtonsoft.Json.JsonConvert.DeserializeObject<Message>(route);
            invalid.BossReturnScene = "Menu_Title";
            if (BossTravel.ApplyContext(Newtonsoft.Json.JsonConvert.SerializeObject(invalid)) ||
                pd.GetString("dreamReturnScene") != atrium || pd.GetString("bossReturnEntryGate") != expectedGate)
                throw new Exception("Invalid return route replaced an accepted destination");
        } finally { rollback(); }
        Mark("PASS cancelled and rejected context preserve return route");
        yield return Both("ending.route");

        // Start at the result screen to isolate the reported return path.
        // No forced hero/input/camera/FSM state is used after this boundary.
        GameManager.instance.BeginSceneTransition(new GameManager.SceneLoadInfo {
            SceneName = "GG_End_Sequence", EntryGateName = "", Visualization = GameManager.SceneLoadVisualizations.Dream
        });
        yield return Until(() => Object.FindObjectOfType<BossDoorChallengeCompleteUI>() != null, "native result screen loaded");
        var ui = Object.FindObjectOfType<BossDoorChallengeCompleteUI>();
        yield return Until(() => Field<bool>(ui, "waitingForInput"), "native result screen awaits input", 60);
        var inputFrame = Time.frameCount + 1;
        using (new MonoMod.RuntimeDetour.Hook(typeof(InControl.InputDevice).GetProperty("AnyButtonWasPressed").GetGetMethod(),
            new Func<Func<InControl.InputDevice, bool>, InControl.InputDevice, bool>((orig, device) =>
                ReferenceEquals(device, InputHandler.Instance.gameController) && Time.frameCount == inputFrame || orig(device))))
            for (var i = 0; i < 4; i++) yield return null;
        yield return Until(() => In(atrium), "native result return enters the correct atrium", 45);
        yield return Until(() => HeroController.instance.acceptingInput && !HeroController.instance.controlReqlinquished &&
            !HeroController.instance.cState.transitioning, "native result return restores interaction", 20);
        if (HeroController.instance.GetEntryGateName() != expectedGate)
            throw new Exception("Returned through a different gate");
        if (runtime.BuildDebugSnapshot().RiskCodes().Contains("hero-exit-without-native-load"))
            throw new Exception("Return retained an unfinished hero exit");
        Mark("FINAL PASS native Pantheon " + tier + " source door, guest F8 join, result UI and interactive return");
        yield return Finish();
    }
}
