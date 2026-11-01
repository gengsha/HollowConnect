using System;
using System.Collections;
using System.Linq;
using MonoMod.RuntimeDetour;
using UnityEngine;
using Object = UnityEngine.Object;

namespace HollowConnect.Smoke;

public sealed partial class EndingHudProbe {
    private void PrepareEndingGodhome() {
        // Boundary data for the native Godseeker movie/result route. This
        // does not simulate or claim completion of the Pantheon battles.
        var pd = PlayerData.instance;
        pd.SetVariable("bossDoorStateTier5", new BossSequenceDoor.Completion { unlocked = true, canUnlock = true });
        pd.SetString("dreamReturnScene", "GG_Atrium_Roof");
        pd.SetString("bossReturnEntryGate", "door_dreamReturnGG_GG_Final_Challenge_Door");
        var sequence = Resources.LoadAll<BossSequence>("GG").Single(s => s.name == "Boss Sequence Tier 5");
        BossSequenceController.Reset();
        BossSequenceController.SetupNewSequence(sequence, BossSequenceController.ChallengeBindings.None, "bossDoorStateTier5");
        Check(BossSequenceController.IsInSequence, "Godhome boundary has native sequence context");
    }

    private IEnumerator EndingGodhomeReturn(Vector3 hudPosition) {
        yield return Until(() => Object.FindObjectOfType<BossDoorChallengeCompleteUI>() != null, "native Godhome result UI loaded");
        var ui = Object.FindObjectOfType<BossDoorChallengeCompleteUI>();
        yield return Until(() => Field<bool>(ui, "waitingForInput"), "native Godhome result UI awaits input", 60);
        var consumed = false;
        using (new Hook(typeof(InControl.InputDevice).GetProperty("AnyButtonWasPressed").GetGetMethod(),
            new Func<Func<InControl.InputDevice, bool>, InControl.InputDevice, bool>((orig, device) => {
                if (ReferenceEquals(device, InputHandler.Instance.gameController) && !consumed) {
                    consumed = true; return true;
                }
                return orig(device);
            }))) {
            yield return Until(() => consumed && GameManager.instance.sceneName == "GG_Atrium_Roof" &&
                GameManager.instance.HasFinishedEnteringScene, "native Godhome result input returns to atrium", 50);
        }
        yield return Until(() => HeroController.instance.acceptingInput && !HeroController.instance.controlReqlinquished &&
            !HeroController.instance.cState.transitioning, "native Godhome return restores interaction", 20);
        Check(HeroController.instance.GetEntryGateName() == "door_dreamReturnGG_GG_Final_Challenge_Door",
            "Godhome result retains its native return gate");
        yield return Until(() => GameCameras.instance.hudCamera.enabled && GameCameras.instance.hudCamera.gameObject.activeInHierarchy &&
            GameCameras.instance.hudCanvas.activeInHierarchy && GameCameras.instance.hudCanvas.GetComponents<PlayMakerFSM>()
                .All(f => f.enabled && f.gameObject.activeInHierarchy) &&
            Vector3.Distance(GameCameras.instance.hudCanvas.transform.localPosition, hudPosition) < .05f,
            "native Godhome return restores health and soul HUD", 15);
        Mark("ENDING GODHOME HUD RESUMED " + HudState());
        Check(!runtime.Connected && !EndingServerStarted, "Godhome result return keeps room closed");
        Flag(role + ".ending-resumed");
        yield return Until(() => Has((role == "host" ? "guest" : "host") + ".ending-resumed"), "both offline Godhome returns complete", 90);
    }
}
