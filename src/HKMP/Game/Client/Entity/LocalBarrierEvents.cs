using System;
using System.Collections;
using System.Linq;
using Hkmp.Util;
using HutongGames.PlayMaker;
using HutongGames.PlayMaker.Actions;
using UnityEngine;

namespace Hkmp.Game.Client.Entity;

// Excluding a gate entity is insufficient: another replicated boss can still
// broadcast BG CLOSE or address the gate directly. Suppress only the local
// barrier recipients while replaying that event; other listeners still run.
internal static class LocalBarrierEvents {
    private static int replayDepth;
    private static int privateWorldDepth;
    private static Entity replaySource;
    internal static bool PrivateTarget(GameObject receiver) => EntityRegistry.IsLocalOnlyBarrier(receiver) ||
        CoopContext.LocalWorldOnly && receiver != null && EntityReferences.Owner(receiver) == null;
    internal static bool Ignore(HutongGames.PlayMaker.Fsm receiver, string signal) => replayDepth != 0 && !AllowsPresentation(receiver, signal) && !AllowsMawlekBattleStart(receiver, signal) && !BossGateCompletion.Allows(receiver, signal) &&
        (EntityRegistry.IsLocalOnlyBarrier(receiver.GameObject) || privateWorldDepth != 0 && PrivateTarget(receiver.GameObject));

    private static bool AllowsMawlekBattleStart(HutongGames.PlayMaker.Fsm receiver, string signal) {
        // This native boss wakes the local reward controller, even in a
        // private world. Keep every other mechanism/entrance event isolated.
        return signal == "START" && replaySource?.Type == EntityType.BroodingMawlek &&
            replaySource.SourceScene == "Crossroads_09" && CoopContext.LocalScene == "Crossroads_09" &&
            receiver.GameObject != null && receiver.GameObject.scene.name == replaySource.SourceScene &&
            receiver.GameObject.name == "Battle Scene" && receiver.Name == "Battle Control" &&
            EntityReferences.Owner(receiver.GameObject) == null && receiver.ActiveState != null &&
            receiver.ActiveState.Transitions.Any(t => t.EventName == signal && receiver.States.Any(s => s.Name == t.ToState &&
                s.Actions.OfType<SetIntValue>().Any(a => a.Enabled && a.intVariable.Name == "Battle Enemies" && a.intValue.Value == 1) &&
                s.Actions.OfType<SendEventByName>().Any(a => a.Enabled && a.sendEvent.Value == "BATTLE START")));
    }

    private static bool AllowsPresentation(HutongGames.PlayMaker.Fsm receiver, string signal) {
        var go = receiver.GameObject;
        if (go == null || EntityReferences.Owner(go) != null) return false;
        if (ColosseumEntrance.AllowsOpen(receiver, signal)) return true;
        if (signal == "ZOTE ROAR START" && go.name == "Grey Prince Title") return true;
        // The replicated boss starts combat, so each player's native intro
        // lock must receive its release. Keep entrance/arena lock events local.
        return signal == "BATTLE STARTED" && go.GetComponent<CameraLockArea>() != null &&
            receiver.ActiveState != null && receiver.ActiveState.Transitions.Any(t => t.EventName == signal &&
                receiver.States.Any(s => s.Name == t.ToState && s.Actions.OfType<ActivateGameObject>().Any(a =>
                    a.Enabled && !a.activate.Value && receiver.GetOwnerDefaultTarget(a.gameObject) == go)));
    }

    internal static void Replay(HutongGames.PlayMaker.Fsm sender, FsmEventTarget target, string name, float delay, FsmState state = null) {
        if (EntityReferences.Owner(sender.GameObject)?.RoarLock?.Handles(sender, name) == true) return;
        var owner = sender.GameObject;
        if (owner == null) return;
        var visit = CoopContext.LocalVisit;
        var scene = owner.scene;
        if (delay < .001f) Send();
        else MonoBehaviourUtil.Instance.StartCoroutine(Delayed());

        void Send() {
            if (owner == null || CoopContext.LocalVisit != visit || !scene.IsValid() || !scene.isLoaded) return;
            replayDepth++;
            var privateWorld = CoopContext.LocalWorldOnly && EntityReferences.Owner(owner) != null;
            if (privateWorld) privateWorldDepth++;
            var previousSource = replaySource;
            replaySource = EntityReferences.Owner(owner);
            try {
                var entity = EntityReferences.Owner(owner);
                if (entity != null && BossGateCompletion.IsTerminal(entity.Type, state, name))
                    BossGateCompletion.Replay(scene.name, name, () => sender.Event(target, name));
                else sender.Event(target, name);
            }
            finally { replaySource = previousSource; if (privateWorld) privateWorldDepth--; replayDepth--; }
        }
        IEnumerator Delayed() {
            yield return new WaitForSeconds(delay);
            Send();
        }
    }
}
