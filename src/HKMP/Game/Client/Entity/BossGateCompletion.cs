using System;
using System.Collections.Generic;
using System.Linq;
using HutongGames.PlayMaker;
using HutongGames.PlayMaker.Actions;
using Hkmp.Networking.Packet.Data;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Hkmp.Game.Client.Entity;

// Local doors must ignore remote entrance events, but still receive the native
// final boss completion. Recognize the native defeat record followed by its
// FINISHED continuation, never health zero or an arbitrary event name alone.
internal static class BossGateCompletion {
    private static readonly Dictionary<string, HashSet<string>> Completed = new();
    // Keep only native schema, not completion receipts, across a disconnect in
    // the same loaded arena. The native boss may disappear while offline, so
    // its final snapshot cannot depend on registering that object again.
    private sealed class Layout {
        internal Scene Scene;
        internal EntityType Type;
        internal readonly Dictionary<(byte Fsm, byte State), string[]> Signals = new();
        internal readonly Dictionary<(byte Fsm, byte State), string[]> DefeatFlags = new();
    }
    private static readonly Dictionary<ulong, Layout> Layouts = new();
    private static readonly Dictionary<HutongGames.PlayMaker.Fsm, string> AftermathRelays = new();
    static BossGateCompletion() {
        // This pure schema cache also follows native unloads while offline.
        UnityEngine.SceneManagement.SceneManager.sceneUnloaded += scene => {
            foreach (var pair in Layouts.Where(p => p.Value.Scene == scene).ToArray()) Layouts.Remove(pair.Key);
            foreach (var pair in AftermathRelays.Where(p => p.Key.GameObject == null || p.Key.GameObject.scene == scene).ToArray()) AftermathRelays.Remove(pair.Key);
        };
    }
    private static string replayScene;
    private static uint visit, epoch;

    private static bool IsCorpse(EntityType type) => type is EntityType.BrokenVesselCorpse or EntityType.LostKinCorpse;
    // A registered, non-combat boss support can finish through a native scene
    // prop rather than opening a gate itself. Require its own positive defeat
    // record to match a native local arena guard; names alone prove nothing.
    private static IEnumerable<string> AftermathFlags(EntityType type, FsmState state, bool predecessors) {
        if (state?.Fsm.GameObject == null || !EntityRegistry.IsBossSupport(type) ||
            state.Fsm.GameObject.GetComponent<HealthManager>() != null || !InRoom(state.Fsm.GameObject.scene.name))
            return Enumerable.Empty<string>();
        var states = predecessors ? Graph(state, true) : new[] { state };
        var flags = states.SelectMany(s => s.Actions).OfType<SetPlayerDataBool>()
            .Where(a => a.Enabled && a.value.Value && BossDefeatSignals.DefeatKey(a.boolName.Value)).Select(a => a.boolName.Value).Distinct().ToArray();
        if (flags.Length == 0) return flags;
        var guards = UnityEngine.Object.FindObjectsOfType<PlayMakerFSM>(true).Where(f => InRoom(f.gameObject.scene.name) &&
            LocalBattleCompletion.IsLocal(f.gameObject)).SelectMany(f => f.FsmStates).SelectMany(s => s.Actions)
            .OfType<PlayerDataBoolTest>().Where(a => a.Enabled && a.isTrue != null).Select(a => a.boolName.Value).ToArray();
        return flags.Where(guards.Contains);
    }
    private static IEnumerable<FsmState> Graph(FsmState state, bool backwards) {
        var pending = new Queue<FsmState>(); var seen = new HashSet<FsmState>(); pending.Enqueue(state);
        while (pending.Count != 0) {
            var next = pending.Dequeue(); if (!seen.Add(next)) continue;
            yield return next;
            foreach (var adjacent in state.Fsm.States.Where(s => backwards
                ? s.Transitions.Any(t => t.ToState == next.Name)
                : next.Transitions.Any(t => t.ToState == s.Name))) pending.Enqueue(adjacent);
        }
    }
    private static bool AftermathReceiver(HutongGames.PlayMaker.Fsm fsm, string signal) {
        if (fsm?.GameObject == null || !InRoom(fsm.GameObject.scene.name) || !LocalBattleCompletion.IsLocal(fsm.GameObject)) return false;
        return fsm.States.SelectMany(s => s.Transitions).Concat(fsm.GlobalTransitions).Where(t => t.EventName == signal)
            .Select(t => fsm.States.FirstOrDefault(s => s.Name == t.ToState)).Where(s => s != null)
            .Any(s => Graph(s, false).Any(next => next.Actions.OfType<SendEventByName>().Any(a => a.Enabled && !a.everyFrame &&
                a.sendEvent.Value == "BG OPEN" && a.eventTarget.target == FsmEventTarget.EventTarget.BroadcastAll)));
    }
    private static bool AftermathSignal(EntityType type, FsmState state, string signal) =>
        state != null && state.Actions.Any(a => a.Enabled && (a is SendEventByName send && send.sendEvent.Value == signal ||
            a is SendEventByNameV2 send2 && send2.sendEvent.Value == signal)) && AftermathFlags(type, state, true).Any() &&
        UnityEngine.Object.FindObjectsOfType<PlayMakerFSM>(true).Any(f => AftermathReceiver(f.Fsm, signal));
    private static IEnumerable<string> CompletionFlags(EntityType type, FsmState state) =>
        Continuation(state).SelectMany(previous => BossDefeatSignals.LocalDefeatFlags(type, previous.Fsm.Name, previous.Name, previous.Actions))
            .Concat(AftermathFlags(type, state, true)).Distinct();
    private static bool DefeatGuardedOpening(HutongGames.PlayMaker.Fsm fsm, string signal) {
        if (fsm?.GameObject == null || !InRoom(fsm.GameObject.scene.name) || !LocalBattleCompletion.IsLocal(fsm.GameObject)) return false;
        // Some native exits dissolve their own collider instead of using BG
        // Control. Require their native defeat-flag guard as well as a local
        // collision-opening continuation; arbitrary remote world events stay
        // private, including ordinary switch and entrance broadcasts.
        var guarded = fsm.States.Any(s => s.Actions.OfType<PlayerDataBoolTest>().Any(a => a.Enabled &&
            BossDefeatSignals.DefeatKey(a.boolName.Value) && a.isTrue != null &&
            (signal is "BATTLE END" or "BOSS DEATH" || s.Transitions.Any(t => t.EventName == a.isTrue.Name && RemovesBarrier(fsm, t.ToState)))));
        return guarded && fsm.States.SelectMany(s => s.Transitions).Concat(fsm.GlobalTransitions)
            .Any(t => t.EventName == signal && RemovesBarrier(fsm, t.ToState));
    }
    private static bool RemovesBarrier(HutongGames.PlayMaker.Fsm fsm, string stateName) {
        var seen = new HashSet<FsmState>();
        var state = fsm.States.FirstOrDefault(s => s.Name == stateName);
        GameObject Target(FsmOwnerDefault owner) {
            var go = fsm.GetOwnerDefaultTarget(owner);
            if (go != null || owner?.GameObject == null) return go;
            var binding = fsm.States.SelectMany(s => s.Actions).OfType<FindChild>()
                .FirstOrDefault(a => a.Enabled && ReferenceEquals(a.storeResult, owner.GameObject));
            return binding == null ? null : fsm.GetOwnerDefaultTarget(binding.gameObject)?.transform.Find(binding.childName.Value)?.gameObject;
        }
        bool LocalCollider(GameObject go) => go != null && (go == fsm.GameObject || go.transform.IsChildOf(fsm.GameObject.transform)) &&
            go.GetComponentsInChildren<Collider2D>(true).Any(c => !c.isTrigger);
        while (state != null && seen.Add(state)) {
            if (state.Actions.Any(a => a.Enabled && (a is DestroySelf && LocalCollider(fsm.GameObject) ||
                a is ActivateGameObject active && !active.activate.Value && LocalCollider(Target(active.gameObject)) ||
                a is SetCollider collider && !collider.active.Value && LocalCollider(Target(collider.gameObject))))) return true;
            var finished = state.Transitions.Where(t => t.EventName == "FINISHED").ToArray();
            if (finished.Length != 1) return false;
            state = fsm.States.FirstOrDefault(s => s.Name == finished[0].ToState);
        }
        return false;
    }

    internal static bool IsTerminal(EntityType type, FsmState state, string signal) {
        if (state == null) return false;
        if (AftermathSignal(type, state, signal)) return true;
        if (IsCorpse(type)) return UnityEngine.Object.FindObjectsOfType<PlayMakerFSM>(true)
            .Any(f => DefeatGuardedOpening(f.Fsm, signal));
        if (!BossDefeatSignals.IsBoss(type)) return false;
        if (signal == "BOSS DEATH") return state.Fsm.GameObject != null &&
            state.Fsm.GameObject.GetComponents<PlayMakerFSM>().Any(f => f.FsmStates.Any(s =>
                BossDefeatSignals.IsDefeatRecord(type, f.FsmName, s.Name, s.Actions)));
        if (signal is not ("BATTLE END" or "BG OPEN")) return false;
        return Continuation(state).Any(next => BossDefeatSignals.IsDefeatRecord(type, next.Fsm.Name, next.Name, next.Actions));
    }
    private static IEnumerable<FsmState> Continuation(FsmState state) {
        var pending = new Queue<FsmState>(); var seen = new HashSet<FsmState>(); pending.Enqueue(state);
        while (pending.Count > 0) {
            var next = pending.Dequeue();
            if (!seen.Add(next)) continue;
            yield return next;
            foreach (var previous in state.Fsm.States.Where(s => s.Transitions.Any(t => t.EventName == "FINISHED" && t.ToState == next.Name)))
                pending.Enqueue(previous);
        }
    }
    private static bool InRoom(string scene) => scene == CoopContext.LocalScene || scene == CoopContext.LocalScene + "_boss" ||
        scene == CoopContext.LocalScene + "_boss_defeated";
    private static void Refresh() {
        if (visit == CoopContext.LocalVisit && epoch == CoopContext.Epoch) return;
        Completed.Clear(); AftermathRelays.Clear(); visit = CoopContext.LocalVisit; epoch = CoopContext.Epoch;
    }
    internal static void Clear() { Completed.Clear(); AftermathRelays.Clear(); replayScene = null; }

    private static void PruneLayouts() {
        foreach (var pair in Layouts.Where(p => !p.Value.Scene.IsValid() || !p.Value.Scene.isLoaded).ToArray())
            Layouts.Remove(pair.Key);
    }
    internal static void Track(ulong id, EntityType type, Scene scene, IList<PlayMakerFSM> fsms) {
        PruneLayouts();
        if (!BossDefeatSignals.IsBoss(type) && !IsCorpse(type) && !EntityRegistry.IsBossSupport(type)) return;
        var layout = new Layout { Scene = scene, Type = type };
        for (var f = 0; f < fsms.Count && f < byte.MaxValue; f++) {
            if (fsms[f] == null) continue;
            var states = fsms[f].FsmStates;
            for (var s = 0; s < states.Length && s < byte.MaxValue; s++) {
                var state = states[s];
                var signals = state.Actions.Where(a => a.Enabled).Select(a => a switch {
                    SendEventByName send => send.sendEvent.Value,
                    SendEventByNameV2 send => send.sendEvent.Value,
                    _ => null
                }).Where(signal => IsTerminal(type, state, signal)).Distinct().ToArray();
                if (signals.Length == 0) continue;
                var key = ((byte)f, (byte)s);
                layout.Signals[key] = signals;
                layout.DefeatFlags[key] = CompletionFlags(type, state).ToArray();
            }
        }
        if (layout.Signals.Count != 0) Layouts[id] = layout;
    }
    internal static void ObserveMissingSnapshot(ulong id, Dictionary<byte, EntityHostFsmData> fsms) {
        PruneLayouts();
        if (CoopContext.Resetting || !Layouts.TryGetValue(id, out var layout) || !InRoom(layout.Scene.name)) return;
        foreach (var pair in fsms) {
            if (!pair.Value.Types.Contains(EntityHostFsmData.Type.State) ||
                !layout.Signals.TryGetValue((pair.Key, pair.Value.CurrentState), out var signals)) continue;
            BossDefeatSignals.ApplyLocalDefeatRecord(layout.Type, layout.Scene.name,
                layout.DefeatFlags[(pair.Key, pair.Value.CurrentState)]);
            foreach (var signal in signals) Replay(layout.Scene.name, signal, () => {
                foreach (var receiver in UnityEngine.Object.FindObjectsOfType<PlayMakerFSM>(true))
                    if (receiver.enabled && receiver.gameObject.activeInHierarchy && Allows(receiver.Fsm, signal)) receiver.SendEvent(signal);
            });
        }
    }

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
    private static bool OpensGates(HutongGames.PlayMaker.Fsm fsm, string signal) => fsm.States.Any(s => s.Transitions.Any(t =>
        t.EventName == signal && LocalBattleSignals.OpensGates(fsm, t.ToState)));
    internal static bool Allows(HutongGames.PlayMaker.Fsm receiver, string signal) {
        if (replayScene == null || receiver.GameObject == null || !InRoom(receiver.GameObject.scene.name)) return false;
        if (signal == "BG OPEN") return EntityRegistry.IsLocalOnlyBarrier(receiver.GameObject);
        if (DefeatGuardedOpening(receiver, signal)) return true;
        if (AftermathReceiver(receiver, signal)) { AftermathRelays[receiver] = replayScene; return true; }
        return signal is "BATTLE END" or "BOSS DEATH" && EntityReferences.Owner(receiver.GameObject) == null && OpensGates(receiver, signal);
    }
    internal static void Observe(EntityType type, string scene, FsmState state) {
        if (CoopContext.Resetting || !InRoom(scene)) return;
        // A sparse/latest snapshot may already be past the one-shot record.
        // Its verified native predecessors carry the same positive metadata.
        BossDefeatSignals.ApplyLocalDefeatRecord(type, scene, AftermathFlags(type, state, true));
        foreach (var action in state.Actions) {
            if (!action.Enabled) continue;
            if (action is SendEventByName send && IsTerminal(type, state, send.sendEvent.Value))
                LocalBarrierEvents.Replay(state.Fsm, send.eventTarget, send.sendEvent.Value, send.delay.Value, state);
            else if (action is SendEventByNameV2 send2 && IsTerminal(type, state, send2.sendEvent.Value))
                LocalBarrierEvents.Replay(state.Fsm, send2.eventTarget, send2.sendEvent.Value, send2.delay.Value, state);
        }
    }
    internal static void NativeAftermathAction(FsmStateAction action) {
        if (CoopContext.Resetting || action.Fsm == null) return;
        Refresh();
        if (!AftermathRelays.TryGetValue(action.Fsm, out var scene)) return;
        string signal = null;
        if (action is SendEventByName send && action.Enabled && !send.everyFrame && send.delay.Value < .001f &&
            send.eventTarget.target == FsmEventTarget.EventTarget.BroadcastAll) signal = send.sendEvent.Value;
        else if (action is SendEventByNameV2 send2 && action.Enabled && !send2.everyFrame && send2.delay.Value < .001f &&
            send2.eventTarget.target == FsmEventTarget.EventTarget.BroadcastAll) signal = send2.sendEvent.Value;
        // Remember the consequence only after this admitted native relay has
        // really emitted it. A later local entry must not close doors forever,
        // and admitting the animation must never skip its native delay.
        if (signal is "BATTLE END" or "BG OPEN") Replay(scene, signal, () => { });
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

