using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using Hkmp.Game.Client.Entity;
using Hkmp.Game.Client.Entity.Action;
using Hkmp.Networking.Packet.Data;
using HutongGames.PlayMaker;
using HutongGames.PlayMaker.Actions;
using UnityEngine;
using Object = UnityEngine.Object;

namespace HollowConnect.Smoke;

// Receiver/action boundary fixtures. Real door collision and original pickup
// continuations are checked separately in the story encounter.
internal static class LocalBarrierEventChecks {
    internal static IEnumerator Run(Action<bool, string> check) {
        var objects = new List<GameObject>();
        const string close = "HC BARRIER FIXTURE CLOSE", open = "HC BARRIER FIXTURE OPEN";
        PlayMakerFSM Make(string name, string fsmName, Transform parent = null) {
            var go = new GameObject(name); go.SetActive(false); objects.Add(go);
            if (parent != null) go.transform.SetParent(parent);
            var fsm = go.AddComponent<PlayMakerFSM>(); fsm.Reset(); fsm.FsmName = fsmName;
            var closeEvent = new FsmEvent(close) { IsGlobal = true };
            var openEvent = new FsmEvent(open) { IsGlobal = true };
            fsm.Fsm.Events = new[] { closeEvent, openEvent };
            fsm.Fsm.States = new[] { "Open", "Closed" }.Select(name => new FsmState(fsm.Fsm) {
                Name = name, Actions = Array.Empty<FsmStateAction>(), Transitions = new[] {
                    new FsmTransition { FsmEvent = closeEvent, ToState = "Closed" },
                    new FsmTransition { FsmEvent = openEvent, ToState = "Open" }
                }
            }).ToArray();
            fsm.Fsm.StartState = "Open";
            fsm.Fsm.Variables.BoolVariables = new[] { new FsmBool { Name = "Fixture", Value = false } };
            foreach (var state in fsm.FsmStates) state.SaveActions();
            fsm.Fsm.Reinitialize(); fsm.Fsm.Init(fsm); go.SetActive(true);
            return fsm;
        }
        try {
            var sender = Make("HollowConnect event sender", "Sender");
            var observer = Make("HollowConnect event observer", "Observer");
            var ordinaryWall = Make("Wall", "Control");
            var walls = Make("Colosseum Wall fixture", "Control");
            var wallChild = Make("Wall child", "Control", walls.transform);
            var barriers = new[] { Make("Battle Gate fixture", "BG Control"), Make("Dream Gate fixture", "Control"),
                Make("mantis_cage_down fixture", "Cage Control"),
                Make("CameraLockArea fixture", "Control") };
            yield return null;
            check(barriers.All(f => f.ActiveStateName == "Open"), "barrier fixture listeners start in original open state");
            foreach (var v2 in new[] { false, true }) {
                FsmStateAction Replay(string name, float delay) {
                    var target = new FsmEventTarget { target = FsmEventTarget.EventTarget.BroadcastAll };
                    FsmStateAction action = v2 ? new SendEventByNameV2 { eventTarget = target, sendEvent = name, delay = delay } :
                        new SendEventByName { eventTarget = target, sendEvent = name, delay = delay };
                    action.Init(sender.FsmStates[0]);
                    EntityFsmActions.ApplyNetworkDataFromAction(null, action); return action;
                }
                Replay(close, 0); yield return null;
                check(observer.ActiveStateName == "Closed" && ordinaryWall.ActiveStateName == "Closed" && barriers.All(f => f.ActiveStateName == "Open"),
                    "network broadcast V2=" + v2 + " keeps local barrier recipients open and reaches ordinary observer");
                check(walls.ActiveStateName == "Closed" && wallChild.ActiveStateName == "Closed",
                    "shared arena walls receive replayed events V2=" + v2);
                foreach (var barrier in barriers) barrier.SendEvent(close);
                yield return null;
                check(barriers.All(f => f.ActiveStateName == "Closed"), "native per-player close still works V2=" + v2);
                Replay(open, 0); yield return null;
                check(observer.ActiveStateName == "Open" && barriers.All(f => f.ActiveStateName == "Closed"),
                    "remote open cannot override a local closed encounter V2=" + v2);
                foreach (var barrier in barriers) barrier.SendEvent(open);
                Replay(close, .1f); yield return new WaitForSecondsRealtime(.25f);
                check(observer.ActiveStateName == "Closed" && barriers.All(f => f.ActiveStateName == "Open"),
                    "delayed network event preserves local barrier boundary V2=" + v2);
                observer.SendEvent(open);
                Replay(close, .2f);
                Object.Destroy(sender.gameObject); yield return new WaitForSecondsRealtime(.35f);
                check(observer.ActiveStateName == "Open", "destroyed old sender cannot dispatch a delayed event V2=" + v2);
                sender = Make("HollowConnect event sender", "Sender"); yield return null;
                var target = new FsmEventTarget { target = FsmEventTarget.EventTarget.GameObject,
                    gameObject = new FsmOwnerDefault { OwnerOption = OwnerDefaultOption.SpecifyGameObject, GameObject = barriers[0].gameObject },
                    fsmName = barriers[0].FsmName };
                FsmStateAction direct = v2 ? new SendEventByNameV2 { eventTarget = target, sendEvent = close, delay = 0 } :
                    new SendEventByName { eventTarget = target, sendEvent = close, delay = 0 };
                direct.Init(sender.FsmStates[0]); EntityFsmActions.ApplyNetworkDataFromAction(null, direct); yield return null;
                check(barriers[0].ActiveStateName == "Open", "direct remote event cannot close local gate V2=" + v2);
                observer.SendEvent(open); ordinaryWall.SendEvent(open);
                Replay(close, .1f);
                var visit = Hkmp.CoopContext.LocalVisit;
                try {
                    Hkmp.CoopContext.LocalVisit++;
                    yield return new WaitForSecondsRealtime(.25f);
                    check(observer.ActiveStateName == "Open", "old scene visit cancels delayed broadcast V2=" + v2);
                } finally { Hkmp.CoopContext.LocalVisit = visit; }
                Replay(close, .1f);
                var epoch = Hkmp.CoopContext.Epoch;
                try {
                    Hkmp.CoopContext.Epoch++;
                    yield return new WaitForSecondsRealtime(.25f);
                    check(observer.ActiveStateName == "Open", "old attempt cancels delayed broadcast V2=" + v2);
                } finally { Hkmp.CoopContext.Epoch = epoch; }
            }
            foreach (var barrier in barriers) {
                var owner = new FsmOwnerDefault { OwnerOption = OwnerDefaultOption.SpecifyGameObject, GameObject = barrier.gameObject };
                var set = new SetFsmBool { gameObject = owner, fsmName = barrier.FsmName, variableName = "Fixture", setValue = true };
                set.Init(sender.FsmStates[0]); EntityFsmActions.ApplyNetworkDataFromAction(new EntityNetworkData(), set);
                var deactivate = new ActivateGameObject { gameObject = owner, activate = false, recursive = false };
                deactivate.Init(sender.FsmStates[0]); EntityFsmActions.ApplyNetworkDataFromAction(null, deactivate);
                var position = barrier.transform.position;
                var move = new SetPosition { gameObject = owner, vector = new Vector3(200, 200, 0), space = Space.World };
                move.Init(sender.FsmStates[0]); EntityFsmActions.ApplyNetworkDataFromAction(null, move);
                var scale = new SetScale { gameObject = owner, vector = Vector3.zero };
                scale.Init(sender.FsmStates[0]); EntityFsmActions.ApplyNetworkDataFromAction(null, scale);
                check(!barrier.FsmVariables.FindFsmBool("Fixture").Value && barrier.gameObject.activeSelf,
                    "external network variable and activation writes cannot change " + barrier.name);
                check(barrier.transform.position == position && barrier.transform.localScale == Vector3.one,
                    "external network position and scale cannot move local " + barrier.name);
            }
        } finally { foreach (var go in objects) if (go != null) Object.Destroy(go); }
    }
}
