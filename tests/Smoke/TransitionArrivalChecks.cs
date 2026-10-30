using System;
using System.Collections;
using System.Linq;
using Hkmp.Game.Client.Entity;
using UnityEngine;
using Object = UnityEngine.Object;

namespace HollowConnect.Smoke;

public sealed partial class RescueProbe {
    private IEnumerator TransitionArrivalChecks() {
        const string room = "Fungus1_04";
        var injected = false;
        var duplicateLoads = 0;
        using var contact = IgnoreExternalCombatDamage();
        On.GameManager.hook_BeginSceneTransitionRoutine load = (orig, gm, info) => {
            if (injected && info.SceneName != room) {
                duplicateLoads++;
                Log("ARRIVAL EXIT ACCEPTED " + gm.sceneName + " -> " + info.SceneName + "/" + info.EntryGateName +
                    " busy=" + gm.IsInSceneTransition + " positioned=" + HeroController.instance.isHeroInPosition);
                // Capture this attempted second load before it can replace the
                // already accepted native load. This is a boundary fault probe.
                return EmptyArrivalLoad();
            }
            return orig(gm, info);
        };
        On.TransitionPoint.hook_OnTriggerEnter2D entry = (orig, door, collider) => {
            var hero = HeroController.instance;
            if (!injected && door.gameObject.scene.name == room && door.name == "right1" && collider != null &&
                collider.transform.IsChildOf(hero.transform) &&
                (GameManager.instance.IsInSceneTransition || !hero.isHeroInPosition || hero.cState.transitioning)) {
                injected = true;
                Log("ARRIVAL FAULT actual native contact=" + door.GetComponent<Collider2D>().bounds + " hero=" + hero.transform.position +
                    " nativeBusy=" + GameManager.instance.IsInSceneTransition + " positioned=" + hero.isHeroInPosition +
                    " transitioning=" + hero.cState.transitioning);
                // Inject early presentation recovery at an actual incoming
                // collider callback, without moving the hero or native gate.
                var state = GameManager.instance.gameState;
                GameManager.instance.SetState(GlobalEnums.GameState.PLAYING);
                try { orig(door, collider); }
                finally { GameManager.instance.SetState(state); }
            } else orig(door, collider);
        };
        On.GameManager.BeginSceneTransitionRoutine += load;
        On.TransitionPoint.OnTriggerEnter2D += entry;
        try {
            yield return RuleEnter(room, "right1");
            Check(injected, "fault preflight reached native in-progress entry with overlapping exit collider");
            Check(duplicateLoads == 0, "early PLAYING presentation cannot start a second exit during native entry");
            Flag(role + ".arrival-checked");
            yield return Until(() => Has("host.arrival-checked") && Has("guest.arrival-checked"), "both actual arrival boundaries checked");
        } finally {
            On.TransitionPoint.OnTriggerEnter2D -= entry;
            On.GameManager.BeginSceneTransitionRoutine -= load;
        }
    }
    private static IEnumerator EmptyArrivalLoad() { yield break; }
}
