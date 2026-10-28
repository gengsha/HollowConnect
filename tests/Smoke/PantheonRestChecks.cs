using System;
using System.Collections;
using System.Linq;
using System.Reflection;
using HollowConnect;
using UnityEngine;

namespace HollowConnect.Smoke;

public sealed partial class PantheonProbe {
    private IEnumerator PantheonRestChecks() {
        var waiter = Environment.GetEnvironmentVariable("HOLLOWCONNECT_REST_PROGRESS");
        var waiting = role == waiter;
        var sequence = Resources.LoadAll<BossSequence>("GG").Single(s => s.name == "Boss Sequence Tier 5");
        var rests = Enumerable.Range(0, sequence.Count).Where(i => sequence.GetSceneAt(i) == "GG_Spa").ToArray();
        Mark("P5 SEQUENCE " + string.Join(",", Enumerable.Range(0, sequence.Count).Select(i => i + "/" + sequence.GetSceneAt(i))));
        if (rests.Length < 3) throw new Exception("P5 requires three native rest stops");
        var oldRest = rests[1]; var nextRest = rests[2]; var finalBoss = nextRest - 1;
        while (!sequence.CanLoad(finalBoss)) finalBoss--;
        BossSequenceController.Reset();
        PlayerData.instance.SetString("dreamReturnScene", "GG_Atrium_Roof");
        PlayerData.instance.SetString("bossReturnEntryGate", "door_dreamReturnGG_Boss_Door");
        BossSequenceController.SetupNewSequence(sequence, BossSequenceController.ChallengeBindings.None, "bossDoorStateTier5");
        typeof(BossSequenceController).GetField("bossIndex", BindingFlags.Static | BindingFlags.NonPublic).SetValue(null, waiting ? oldRest : finalBoss);
        if (!waiting) yield return Until(() => Has("rest.waiter.ready"), "old rest authority waiting");
        Enter(waiting ? "GG_Spa" : sequence.GetSceneAt(finalBoss));
        yield return Until(() => In(waiting ? "GG_Spa" : sequence.GetSceneAt(finalBoss)), "saved native sequence entry"); SafeHero();
        yield return Until(() => manager.IsSceneHostDetermined && manager.IsSceneHost, "separate room authority");
        string oldContext = null;
        if (waiting) {
            // Playable becomes true before the native dream-entry FSM's
            // delayed RegainControl action. Let that entry finish before
            // simulating a player who remains seated or in conversation.
            yield return Until(() => GameManager.instance.HasFinishedEnteringScene && HeroController.instance.acceptingInput &&
                !HeroController.instance.controlReqlinquished, "native rest entrance releases control before waiting lock");
            if (BossSequenceController.BossIndex != oldRest + 1 || BossSceneController.Instance.customExitPoint == null)
                throw new Exception("Old native rest setup unavailable index=" + BossSequenceController.BossIndex);
            oldContext = BossTravel.CaptureContext();
            HeroController.instance.RelinquishControl();
            Flag("rest.waiter.ready");
        } else {
            yield return Defeat(sequence.GetSceneAt(finalBoss), "GG_Spa"); SafeHero();
            Flag("rest.fighter.arrived");
        }
        yield return Until(() => Has("rest.fighter.arrived") && BossSequenceController.BossIndex == nextRest + 1,
            "rest progress converges to completed section", 115);
        var exit = BossSceneController.Instance.customExitPoint;
        if (exit == null || exit.targetScene != sequence.GetSceneAt(nextRest + 1))
            throw new Exception("Rest exit repeats old section: " + exit?.targetScene + " index=" + BossSequenceController.BossIndex);
        if (waiting) {
            if (!HeroController.instance.controlReqlinquished) throw new Exception("Waiting input-lock fixture was released");
            var applied = BossTravel.ApplyContext(oldContext);
            if (!applied || BossSequenceController.BossIndex != nextRest + 1)
                throw new Exception("Stale rest context result=" + applied + " index=" + BossSequenceController.BossIndex + " expected=" + (nextRest + 1));
            HeroController.instance.RegainControl(); HeroController.instance.StartAnimationControl();
        }
        yield return Both("rest-new-exit");
        GameManager.instance.BeginSceneTransition(new GameManager.SceneLoadInfo { SceneName = exit.targetScene,
            EntryGateName = exit.entryPoint, Visualization = GameManager.SceneLoadVisualizations.Default, WaitForSceneTransitionCameraFade = true });
        yield return Until(() => In(sequence.GetSceneAt(nextRest + 1)), "native rest exit enters next section"); SafeHero();
        yield return Both("rest-next-section");
        if (BossSequenceController.BossIndex != nextRest + 1) throw new Exception("Next section index changed");
        Mark("FINAL PASS P5 split rest convergence, stale heartbeat and native next section");
        yield return Finish();
    }
}
