using System;
using System.Collections.Generic;
using System.Linq;
using HutongGames.PlayMaker;
using HutongGames.PlayMaker.Actions;

namespace Hkmp.Game.Client.Entity;

// Local doors must ignore remote entrance events, but still receive the native
// final boss completion. Recognize the native defeat record followed by its
// FINISHED continuation, never health zero or an arbitrary event name alone.
internal static class BossGateCompletion {
    private static readonly Dictionary<string, HashSet<string>> Completed = new();
    private static string replayScene;
    private static uint visit, epoch;

    internal static bool IsTerminal(EntityType type, FsmState state, string signal) {
        if (signal is not ("BATTLE END" or "BG OPEN") || state == null || !BossDefeatSignals.IsBoss(type)) return false;
        var pending = new Queue<FsmState>(); var seen = new HashSet<FsmState>(); pending.Enqueue(state);
        while (pending.Count > 0) {
            var next = pending.Dequeue();
            if (!seen.Add(next)) continue;
            if (BossDefeatSignals.IsDefeatRecord(type, next.Fsm.Name, next.Name, next.Actions)) return true;
            foreach (var previous in state.Fsm.States.Where(s => s.Transitions.Any(t => t.EventName == "FINISHED" && t.ToState == next.Name)))
                pending.Enqueue(previous);
        }
        return false;
    }
    private static bool InRoom(string scene) => scene == CoopContext.LocalScene || scene == CoopContext.LocalScene + "_boss" ||
        scene == CoopContext.LocalScene + "_boss_defeated";
    private static void Refresh() {
        if (visit == CoopContext.LocalVisit && epoch == CoopContext.Epoch) return;
        Completed.Clear(); visit = CoopContext.LocalVisit; epoch = CoopContext.Epoch;
    }
    internal static void Clear() { Completed.Clear(); replayScene = null; }

    internal static void Replay(string scene, string signal, System.Action send) {
        if (CoopContext.Resetting || !InRoom(scene)) return;
        Refresh();
        if (!Completed.TryGetValue(scene, out var events)) Completed[scene] = events = new HashSet<string>();
        events.Add(signal);
        // The native Battle Scene opens these doors in response to BATTLE END.
        // Remember that consequence for a door whose local close starts later.
        if (signal == "BATTLE END") events.Add("BG OPEN");
        var previous = replayScene; replayScene = scene;
        try { send(); } finally { replayScene = previous; }
    }
    private static bool OpensGates(HutongGames.PlayMaker.Fsm fsm, string signal) => fsm.States.Any(s => s.Transitions.Any(t => t.EventName == signal &&
        fsm.States.Any(target => target.Name == t.ToState && target.Actions.Any(a => a.Enabled &&
            (a is SendEventByName send && send.sendEvent.Value == "BG OPEN" ||
             a is SendEventByNameV2 send2 && send2.sendEvent.Value == "BG OPEN")))));
    internal static bool Allows(HutongGames.PlayMaker.Fsm receiver, string signal) {
        if (replayScene == null || receiver.GameObject == null || !InRoom(receiver.GameObject.scene.name)) return false;
        if (signal == "BG OPEN") return EntityRegistry.IsLocalOnlyBarrier(receiver.GameObject);
        return signal == "BATTLE END" && EntityReferences.Owner(receiver.GameObject) == null && OpensGates(receiver, signal);
    }
    internal static void Observe(EntityType type, string scene, FsmState state) {
        if (CoopContext.Resetting || !InRoom(scene)) return;
        foreach (var action in state.Actions) {
            if (!action.Enabled) continue;
            if (action is SendEventByName send && IsTerminal(type, state, send.sendEvent.Value))
                LocalBarrierEvents.Replay(state.Fsm, send.eventTarget, send.sendEvent.Value, send.delay.Value, state);
            else if (action is SendEventByNameV2 send2 && IsTerminal(type, state, send2.sendEvent.Value))
                LocalBarrierEvents.Replay(state.Fsm, send2.eventTarget, send2.sendEvent.Value, send2.delay.Value, state);
        }
    }
    internal static void LocalStateEntered(HutongGames.PlayMaker.Fsm fsm) {
        if (CoopContext.Resetting || fsm.GameObject == null || !InRoom(fsm.GameObject.scene.name)) return;
        Refresh();
        foreach (var pair in Completed.ToArray()) foreach (var signal in pair.Value.ToArray()) {
            if (fsm.ActiveState == null || !fsm.ActiveState.Transitions.Any(t => t.EventName == signal)) continue;
            Replay(pair.Key, signal, () => { if (Allows(fsm, signal)) fsm.Event(signal); });
        }
    }
}

