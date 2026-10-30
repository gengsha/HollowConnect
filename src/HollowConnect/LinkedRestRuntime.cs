using System;
using System.Linq;
using HollowConnect.Core;
using HutongGames.PlayMaker;
using UnityEngine;

namespace HollowConnect;

public sealed partial class CoopRuntime {
    private LinkedRestCount linkedRestCount;
    private string linkedRestStatus = "";
    private uint linkedRestEpoch;
    private LinkedRestView linkedRestView;
    private string linkedRestScene;
    private int linkedRestSceneHandle;
    private PlayMakerFSM[] linkedBenches = Array.Empty<PlayMakerFSM>(), linkedSprings = Array.Empty<PlayMakerFSM>();
    private LinkedRestKind linkedLastRest;
    private bool BlockLinkedRestHeal => linkedHealBlockScope > 0 || PlayerData.instance?.GetBool("atBench") == true;
    private bool LinkedSpringSoul(On.HeroController.orig_TryAddMPChargeSpa orig, HeroController self, int amount) {
        if (LinkedHealthRuleEnabled && (!linkedRestCount.Ready || LocalLinkedRest() != LinkedRestKind.Spring)) return false;
        return orig(self, amount);
    }
    private bool BeginLinkedRestFsm(Fsm fsm) {
        if (!LinkedHealthRuleEnabled || fsm == null) return false;
        if (fsm.Name != "Bench Control" && fsm.Name != "Hive Health Regen" && !(fsm.GameObject != null && fsm.GameObject.name.StartsWith("Spa Region", StringComparison.Ordinal) &&
            (fsm.Name == "Heal HP" || fsm.Name == "Spa Region"))) return false;
        linkedHealBlockScope++; return true;
    }
    private static bool HealingBench(PlayMakerFSM fsm) => fsm.FsmName == "Bench Control" && fsm.FsmStates.Any(s => s.Actions.Any(a =>
        (a.GetType().GetField("methodName")?.GetValue(a) as FsmString)?.Value == "MaxHealth"));
    private LinkedRestKind LocalLinkedRest() {
        var hero = HeroController.instance;
        if (!LinkedHealthRuleEnabled || !Playable || Down || Observing || Resetting || Returning || changingScene || hero == null ||
            hero.cState.dead || hero.cState.hazardDeath || hero.cState.hazardRespawning || hero.cState.transitioning ||
            GameManager.instance.isPaused || Time.timeScale <= 0) return LinkedRestKind.None;
        var scene = UnityEngine.SceneManagement.SceneManager.GetActiveScene().name;
        var handle = UnityEngine.SceneManagement.SceneManager.GetActiveScene().handle;
        if (linkedRestScene != scene || linkedRestSceneHandle != handle) {
            linkedRestScene = scene;
            linkedRestSceneHandle = handle;
            var fsms = UnityEngine.Object.FindObjectsOfType<PlayMakerFSM>(true).Where(f => f.gameObject.scene.name == scene).ToArray();
            linkedBenches = fsms.Where(HealingBench).ToArray();
            linkedSprings = fsms.Where(f => f.FsmName == "Spa Region").ToArray();
        }
        if (PlayerData.instance.GetBool("atBench") && linkedBenches.Any(f => f != null && f.gameObject.activeInHierarchy &&
            Vector2.Distance(f.transform.position, hero.transform.position) < 4)) return LinkedRestKind.Bench;
        if (linkedSprings.Any(f => f != null && f.gameObject.activeInHierarchy &&
            (f.ActiveStateName == "Stay" || f.ActiveStateName == "Heal Pause" || f.ActiveStateName == "Healing" || f.ActiveStateName == "Heal")))
            return LinkedRestKind.Spring;
        return LinkedRestKind.None;
    }
    private void TickLinkedRest() {
        var rest = LocalLinkedRest();
        if (rest != linkedLastRest) { linkedLastRest = rest; SendPresence(); }
        if (Playable && !Down && !Resetting && linkedRestCount.Resting > 0) {
            if (linkedRestView == null) linkedRestView = new LinkedRestView(transform);
            linkedRestView.Show(linkedRestCount, linkedRestStatus);
        } else linkedRestView?.Hide();
    }
    private void ResetLinkedRest() {
        linkedRestView?.Dispose(); linkedRestView = null;
        linkedRestCount = default; linkedLastRest = LinkedRestKind.None; linkedRestScene = null;
        linkedRestStatus = ""; linkedRestEpoch = 0;
        linkedBenches = linkedSprings = Array.Empty<PlayMakerFSM>();
    }
}
