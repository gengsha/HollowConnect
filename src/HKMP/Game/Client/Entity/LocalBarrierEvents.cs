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
    private static FsmState replayState;
    internal static bool PrivateTarget(GameObject receiver) => EntityRegistry.IsLocalOnlyBarrier(receiver) ||
        CoopContext.LocalWorldOnly && receiver != null && EntityReferences.Owner(receiver) == null;
    internal static bool Ignore(HutongGames.PlayMaker.Fsm receiver, string signal) => replayDepth != 0 && !AllowsPresentation(receiver, signal) && !AllowsBattleStart(receiver, signal) && !AllowsBattleCompletion(receiver, signal) && !BossGateCompletion.Allows(receiver, signal) &&
        (EntityRegistry.IsLocalOnlyBarrier(receiver.GameObject) || privateWorldDepth != 0 && PrivateTarget(receiver.GameObject));

    internal static bool StartsNativeBattle(HutongGames.PlayMaker.Fsm receiver, string signal) =>
        replayDepth != 0 && (AllowsBattleStart(receiver, signal) || AllowsBattleCompletion(receiver, signal));
    internal static void ContinueNativeBattle(System.Action continuation) {
        var previousDepth = replayDepth; var previousPrivate = privateWorldDepth; var previousSource = replaySource;
        replayDepth = privateWorldDepth = 0; replaySource = null;
        // This verified local controller now owns its original continuation,
        // including Music/FINISHED, BATTLE START and its own BG CLOSE. The
        // surrounding remote broadcast remains isolated for other recipients.
        try { continuation(); }
        finally { replayDepth = previousDepth; privateWorldDepth = previousPrivate; replaySource = previousSource; }
    }

    private static bool AllowsBattleStart(HutongGames.PlayMaker.Fsm receiver, string signal) =>
        LocalBattleSignals.Allows(receiver, signal, replaySource, replayState);
    private static bool AllowsBattleCompletion(HutongGames.PlayMaker.Fsm receiver, string signal) =>
        LocalBattleSignals.AllowsCompletion(receiver, signal, replaySource, replayState);

    private static bool AllowsPresentation(HutongGames.PlayMaker.Fsm receiver, string signal) {
        var go = receiver.GameObject;
        if (go == null || EntityReferences.Owner(go) != null) return false;
        if (Component.ChallengePromptComponent.AllowsLocalCage(receiver, signal, replaySource)) return true;
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
        var epoch = CoopContext.Epoch;
        var scene = owner.scene;
        if (delay < .001f) Send();
        else MonoBehaviourUtil.Instance.StartCoroutine(Delayed());

        void Send() {
            if (owner == null || CoopContext.LocalVisit != visit || CoopContext.Epoch != epoch || !scene.IsValid() || !scene.isLoaded) return;
            replayDepth++;
            var privateWorld = CoopContext.LocalWorldOnly && EntityReferences.Owner(owner) != null;
            if (privateWorld) privateWorldDepth++;
            var previousSource = replaySource;
            var previousState = replayState;
            replaySource = EntityReferences.Owner(owner);
            replayState = state;
            try {
                var entity = EntityReferences.Owner(owner);
                if (entity != null && BossGateCompletion.IsTerminal(entity.Type, state, name))
                    BossGateCompletion.Replay(scene.name, name, () => sender.Event(target, name));
                else sender.Event(target, name);
            }
            finally { replaySource = previousSource; replayState = previousState; if (privateWorld) privateWorldDepth--; replayDepth--; }
        }
        IEnumerator Delayed() {
            yield return new WaitForSeconds(delay);
            Send();
        }
    }
}
