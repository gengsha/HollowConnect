using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HutongGames.PlayMaker;
using HutongGames.PlayMaker.Actions;
using UnityEngine;

namespace Hkmp.Game.Client.Entity;

// A one-shot native start is durable state for a local battle controller.
// Discover its actual target and initialization schema, then carry the receipt
// in ordinary versioned FSM bools. No boss names, room list or enemy counts.
internal sealed class LocalBattleSignals {
    private const string Prefix = "HollowConnect Battle Start ";
    private static readonly FieldInfo KilledTarget = typeof(HealthManager).GetField("sendKilledTo", BindingFlags.Instance | BindingFlags.NonPublic);
    private static readonly FieldInfo KilledName = typeof(HealthManager).GetField("sendKilledToName", BindingFlags.Instance | BindingFlags.NonPublic);
    private static readonly FieldInfo KilledObject = typeof(HealthManager).GetField("sendKilledToObject", BindingFlags.Instance | BindingFlags.NonPublic);
    private readonly Entity entity;
    private readonly uint visit, epoch;
    private readonly Action<int, FsmBool> publish;
    private readonly List<Binding> bindings = new();
    private float next;
    private sealed class Binding {
        internal int FsmIndex, StateIndex, ActionIndex;
        internal PlayMakerFSM Host;
        internal FsmBool Receipt;
        internal bool Applied;
        internal FsmStateAction Cue => Host == null || StateIndex >= Host.FsmStates.Length ||
            ActionIndex >= Host.FsmStates[StateIndex].Actions.Length ? null : Host.FsmStates[StateIndex].Actions[ActionIndex];
    }

    internal LocalBattleSignals(Entity entity, IList<PlayMakerFSM> host, IList<PlayMakerFSM> client, Action<int, FsmBool> publish) {
        this.entity = entity; this.publish = publish; visit = CoopContext.LocalVisit; epoch = CoopContext.Epoch;
        if (!BossDefeatSignals.IsBoss(entity.Type)) return;
        for (var f = 0; f < host.Count && f < client.Count; f++) {
            if (host[f] == null || client[f] == null) continue;
            for (var s = 0; s < host[f].FsmStates.Length; s++) {
                var actions = host[f].FsmStates[s].Actions;
                for (var a = 0; a < actions.Length; a++) {
                    // Slots depend only on the native action schema. Runtime
                    // object references resolve at different times on peers.
                    if (!TryCue(actions[a], out _, out _) || host[f].FsmVariables.BoolVariables.Length >= byte.MaxValue ||
                        client[f].FsmVariables.BoolVariables.Length >= byte.MaxValue) continue;
                    var name = Prefix + s + ":" + a;
                    FsmBool Append(PlayMakerFSM fsm) {
                        var value = fsm.FsmVariables.FindFsmBool(name);
                        if (value != null) return value;
                        value = new FsmBool { Name = name };
                        fsm.FsmVariables.BoolVariables = fsm.FsmVariables.BoolVariables.Concat(new[] { value }).ToArray();
                        return value;
                    }
                    bindings.Add(new Binding { FsmIndex = f, StateIndex = s, ActionIndex = a, Host = host[f], Receipt = Append(host[f]) });
                    Append(client[f]);
                }
            }
        }
    }

    private static bool InRoom(string scene) => scene == CoopContext.LocalScene || scene == CoopContext.LocalScene + "_boss" ||
        scene == CoopContext.LocalScene + "_boss_defeated";
    private static bool TryCue(FsmStateAction action, out FsmEventTarget target, out string signal) {
        target = null; signal = null;
        if (action == null || !action.Enabled) return false;
        switch (action) {
            case SendEventByName send when !send.everyFrame && send.delay.Value < .001f:
                target = send.eventTarget; signal = send.sendEvent.Value; break;
            case SendEventByNameV2 send when !send.everyFrame && send.delay.Value < .001f:
                target = send.eventTarget; signal = send.sendEvent.Value; break;
        }
        return !string.IsNullOrEmpty(signal) && target != null && !target.sendToChildren.Value &&
            (target.target == FsmEventTarget.EventTarget.GameObjectFSM && target.fsmName.Value == "Battle Control" ||
             target.target == FsmEventTarget.EventTarget.GameObject ||
             target.target == FsmEventTarget.EventTarget.BroadcastAll && signal == "BATTLE START");
    }
    private static bool InitializesBattle(HutongGames.PlayMaker.Fsm fsm, string stateName) {
        var seen = new HashSet<FsmState>();
        var state = fsm.States.FirstOrDefault(s => s.Name == stateName);
        while (state != null && seen.Add(state)) {
            if (state.Actions.OfType<SetIntValue>().Any(a => a.Enabled && !a.everyFrame &&
                    a.intVariable.Name == "Battle Enemies" && a.intValue.Value > 0) &&
                state.Actions.OfType<SendEventByName>().Any(a => a.Enabled && !a.everyFrame && a.delay.Value < .001f &&
                    a.sendEvent.Value == "BATTLE START" && a.eventTarget.target == FsmEventTarget.EventTarget.BroadcastAll)) return true;
            // Empty native presentation states may immediately FINISHED into
            // initialization. Never skip actions, timers or another event.
            if (state.Actions.Any(a => a.Enabled) || state.Transitions.Length != 1 || state.Transitions[0].EventName != "FINISHED") return false;
            state = fsm.States.FirstOrDefault(s => s.Name == state.Transitions[0].ToState);
        }
        return false;
    }
    internal static bool OpensGates(HutongGames.PlayMaker.Fsm fsm, string stateName) {
        var seen = new HashSet<FsmState>();
        var state = fsm.States.FirstOrDefault(s => s.Name == stateName);
        while (state != null && seen.Add(state)) {
            if (state.Actions.OfType<SendEventByName>().Any(a => a.Enabled && !a.everyFrame &&
                a.sendEvent.Value == "BG OPEN" && a.eventTarget.target == FsmEventTarget.EventTarget.BroadcastAll)) return true;
            var finished = state.Transitions.Where(t => t.EventName == "FINISHED").ToArray();
            if (finished.Length != 1) return false;
            state = fsm.States.FirstOrDefault(s => s.Name == finished[0].ToState);
        }
        return false;
    }
    private static bool InitializesScriptedBattle(HutongGames.PlayMaker.Fsm fsm, string stateName) {
        var seen = new HashSet<FsmState>();
        var state = fsm.States.FirstOrDefault(s => s.Name == stateName);
        while (state != null && seen.Add(state)) {
            foreach (var killed in state.Transitions.Where(t => t.EventName == "KILLED")) {
                var ending = fsm.States.FirstOrDefault(s => s.Name == killed.ToState);
                if (ending != null && ending.Transitions.Any(t => t.EventName == "BOSS DEATH" && OpensGates(fsm, t.ToState))) return true;
            }
            var finished = state.Transitions.Where(t => t.EventName == "FINISHED").ToArray();
            if (finished.Length != 1) return false;
            state = fsm.States.FirstOrDefault(s => s.Name == finished[0].ToState);
        }
        return false;
    }
    private static GameObject CompletionTarget(Entity source) {
        var health = source?.Object.Host == null ? null : source.Object.Host.GetComponent<HealthManager>();
        if (health == null) return null;
        var target = KilledTarget.GetValue(health) as GameObject;
        if (target == null) {
            // A dormant native copy has not run HealthManager.Start. Resolve
            // the same serialized association, without starting its AI or
            // adding a network field. Limit name lookup to this local room.
            var name = KilledName.GetValue(health) as string;
            if (!string.IsNullOrEmpty(name)) {
                var candidates = UnityEngine.Object.FindObjectsOfType<PlayMakerFSM>(true).Select(f => f.gameObject)
                    .Where(go => go.name == name && InRoom(go.scene.name) && LocalBattleCompletion.IsLocal(go)).Distinct().Take(2).ToArray();
                if (candidates.Length == 1) target = candidates[0];
            } else target = KilledObject.GetValue(health) as GameObject;
        }
        if (target == null || !InRoom(target.scene.name) || !LocalBattleCompletion.IsLocal(target)) return null;
        foreach (var body in new[] { source.Object.Host, source.Object.Client }) {
            var local = body == null ? null : body.GetComponent<HealthManager>();
            if (local != null && KilledTarget.GetValue(local) as GameObject == null) KilledTarget.SetValue(local, target);
        }
        return target;
    }
    private static bool Starts(HutongGames.PlayMaker.Fsm receiver, string signal, bool waiting) =>
        receiver?.GameObject != null && InRoom(receiver.GameObject.scene.name) &&
        LocalBattleCompletion.IsLocal(receiver.GameObject) &&
        (waiting ? receiver.ActiveState?.Transitions : receiver.States.SelectMany(s => s.Transitions))?.Any(t =>
            t.EventName == signal && (receiver.Name == "Battle Control" && InitializesBattle(receiver, t.ToState) ||
                receiver.Name == "Battle Scene" && signal == "BATTLE START" && InitializesScriptedBattle(receiver, t.ToState))) == true;

    private static PlayMakerFSM Receiver(Entity source, FsmStateAction cue, FsmEventTarget target, string signal) {
        // Broadcast starts need an explicit native association as well: the
        // boss's HealthManager names the local controller that receives KILLED.
        // Its schema must then wait for the corpse's BOSS DEATH before BG OPEN.
        var go = target.target == FsmEventTarget.EventTarget.BroadcastAll ? CompletionTarget(source) :
            cue.Fsm.GetOwnerDefaultTarget(target.gameObject);
        if (go == null) return null;
        var matches = go.GetComponents<PlayMakerFSM>().Where(f => Starts(f.Fsm, signal, false)).Take(2).ToArray();
        return matches.Length == 1 ? matches[0] : null;
    }
    internal static bool Allows(HutongGames.PlayMaker.Fsm receiver, string signal, Entity source, FsmState state) =>
        source != null && BossDefeatSignals.IsBoss(source.Type) && InRoom(source.SourceScene) && state != null &&
        Starts(receiver, signal, true) && state.Actions.Any(a => TryCue(a, out var target, out var name) && name == signal &&
            ReferenceEquals(Receiver(source, a, target, name)?.Fsm, receiver));

    internal static bool AllowsCompletion(HutongGames.PlayMaker.Fsm receiver, string signal, Entity source, FsmState state) =>
        signal == "BOSS DEATH" && source != null && BossDefeatSignals.IsBoss(source.Type) && InRoom(source.SourceScene) &&
        receiver?.GameObject != null && receiver.GameObject == CompletionTarget(source) &&
        InRoom(receiver.GameObject.scene.name) && LocalBattleCompletion.IsLocal(receiver.GameObject) &&
        receiver.ActiveState?.Transitions.Any(t => t.EventName == signal && OpensGates(receiver, t.ToState)) == true &&
        state != null && state.Actions.OfType<SendEventByName>().Any(a => a.Enabled && !a.everyFrame && a.delay.Value < .001f &&
            a.sendEvent.Value == signal && a.eventTarget.target == FsmEventTarget.EventTarget.BroadcastAll);

    internal void ActionEntered(FsmStateAction action) {
        if (!entity.IsNativeAuthority || CoopContext.Resetting || visit != CoopContext.LocalVisit || epoch != CoopContext.Epoch || !InRoom(entity.SourceScene)) return;
        foreach (var binding in bindings) {
            if (binding.Receipt.Value || !ReferenceEquals(binding.Cue, action) || !TryCue(action, out var target, out var signal) ||
                Receiver(entity, action, target, signal) == null) continue;
            binding.Receipt.Value = true; publish(binding.FsmIndex, binding.Receipt);
        }
    }
    internal void Tick() {
        if (entity.IsNativeAuthority || CoopContext.Resetting || visit != CoopContext.LocalVisit || epoch != CoopContext.Epoch ||
            !InRoom(entity.SourceScene) || Time.unscaledTime < next) return;
        next = Time.unscaledTime + .1f;
        foreach (var binding in bindings) {
            if (binding.Applied || !binding.Receipt.Value || !TryCue(binding.Cue, out var target, out var signal)) continue;
            var receiver = Receiver(entity, binding.Cue, target, signal);
            if (receiver == null || !receiver.enabled || !receiver.gameObject.activeInHierarchy || !Starts(receiver.Fsm, signal, true)) continue;
            binding.Applied = true;
            LocalBarrierEvents.Replay(binding.Cue.Fsm, target, signal, 0, binding.Cue.State);
        }
    }
}
