using System;
using System.Collections;
using System.Collections.Generic;
using HutongGames.PlayMaker;
using HutongGames.PlayMaker.Actions;
using UnityEngine;
using Object = UnityEngine.Object;

namespace HollowConnect.Smoke;

// Lifecycle boundaries for the one-shot native prerequisite recheck. These
// fixtures never grant an ability or write a real defeat/save flag.
internal static class PickupConditionChecks {
    internal static IEnumerator Run(Action<bool, string> check) {
        var objects = new List<GameObject>();
        var defeated = false;
        Modding.Delegates.GetBoolProxy flag = (key, value) => key == "mageLordDefeated" ? defeated : value;
        PlayMakerFSM Make() {
            var owner = new GameObject("HollowConnect pickup condition fixture"); owner.SetActive(false); objects.Add(owner);
            var child = new GameObject("Inactive grant"); child.transform.SetParent(owner.transform); child.SetActive(false);
            var grant = child.AddComponent<PlayMakerFSM>(); grant.Reset(); grant.FsmName = "Grant";
            grant.Fsm.States = new[] { new FsmState(grant.Fsm) { Name = "Grant", Transitions = Array.Empty<FsmTransition>(),
                Actions = new FsmStateAction[] { new SetPlayerDataInt { intName = "quakeLevel", value = 1 } } } };
            grant.Fsm.StartState = "Grant"; grant.FsmStates[0].SaveActions();
            grant.Fsm.Reinitialize(); grant.Fsm.Init(grant);
            var fsm = owner.AddComponent<PlayMakerFSM>(); fsm.Reset(); fsm.FsmName = "Pickup";
            var ready = new FsmEvent("READY");
            fsm.Fsm.Events = new[] { ready };
            fsm.Fsm.States = new[] {
                new FsmState(fsm.Fsm) { Name = "Waiting", Actions = new FsmStateAction[] {
                    new PlayerDataBoolTest { gameObject = new FsmOwnerDefault { OwnerOption = OwnerDefaultOption.SpecifyGameObject,
                            GameObject = GameManager.instance.gameObject },
                        boolName = "mageLordDefeated", isTrue = ready }
                }, Transitions = new[] { new FsmTransition { FsmEvent = ready, ToState = "Appeared" } } },
                new FsmState(fsm.Fsm) { Name = "Appeared", Actions = Array.Empty<FsmStateAction>(), Transitions = Array.Empty<FsmTransition>() },
                new FsmState(fsm.Fsm) { Name = "Cancelled", Actions = Array.Empty<FsmStateAction>(), Transitions = Array.Empty<FsmTransition>() }
            };
            fsm.Fsm.StartState = "Waiting";
            foreach (var state in fsm.FsmStates) state.SaveActions();
            fsm.Fsm.Reinitialize(); fsm.Fsm.Init(fsm); owner.SetActive(true);
            return fsm;
        }
        Modding.ModHooks.GetPlayerBoolHook += flag;
        try {
            var waiting = Make(); yield return null;
            check(waiting.ActiveStateName == "Waiting", "pickup waits while native prerequisite is false");
            defeated = true; yield return new WaitForSeconds(1);
            check(waiting.ActiveStateName == "Waiting", "native corpse events have priority over missing-event recovery");
            yield return new WaitForSeconds(12);
            check(waiting.ActiveStateName == "Appeared", "late defeat flag rechecks original pickup action");
            defeated = false;
            var cancelled = Make(); yield return null;
            cancelled.SetState("Cancelled"); yield return null;
            defeated = true; yield return null; yield return null;
            check(cancelled.ActiveStateName == "Cancelled", "leaving pickup state cancels prerequisite continuation");
            defeated = false;
            var destroyed = Make(); yield return null;
            Object.Destroy(destroyed.gameObject); yield return null;
            defeated = true; yield return null; yield return null;
            check(destroyed == null && PlayerData.instance.GetInt("quakeLevel") == 0,
                "destroyed pickup cancels safely and fixtures grant no ability");
        } finally {
            Modding.ModHooks.GetPlayerBoolHook -= flag;
            foreach (var owner in objects) if (owner != null) Object.Destroy(owner);
        }
    }
}
