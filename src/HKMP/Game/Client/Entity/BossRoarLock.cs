using System;
using System.Collections.Generic;
using System.Linq;
using HutongGames.PlayMaker;
using HutongGames.PlayMaker.Actions;
using Modding;
using UnityEngine;

namespace Hkmp.Game.Client.Entity;

// ROAR ENTER/EXIT are ordered state, even though ordinary reliable FSM
// actions may arrive out of order. Carry the current value in the existing
// revision-filtered bool snapshots instead of replaying those two impulses.
internal sealed class BossRoarLock {
    private const string Variable = "HollowConnect Roar Active";
    private const string FinishedVariable = "HollowConnect Roar Finished";
    private readonly Entity entity;
    private readonly uint visit;
    private readonly List<Binding> bindings = new();
    private readonly Action<int, FsmBool> publish;
    private PlayMakerFSM lockedHero;
    private bool applied;
    private sealed class Binding {
        internal int Index;
        internal PlayMakerFSM Host, Client;
        internal FsmBool Native, Replica;
        internal FsmBool FinishedNative, FinishedReplica;
        internal FsmGameObject Emitter;
        internal GameObject EndedEmitter;
        internal GameObject Bow;
        internal readonly Dictionary<FsmStateAction, bool> Cues = new();
    }

    internal BossRoarLock(Entity entity, IList<PlayMakerFSM> host, IList<PlayMakerFSM> client, Action<int, FsmBool> publish) {
        this.entity = entity; this.publish = publish; visit = CoopContext.LocalVisit;
        // These two native introductions have a direct, zero-delay pair and
        // a timed roar. Other bosses keep their existing local continuations.
        if (entity.Type != EntityType.Grimm && entity.Type != EntityType.TheCollector) return;
        for (var i = 0; i < host.Count && i < client.Count; i++) {
            var cues = host[i].FsmStates.SelectMany(s => s.Actions).OfType<SendEventByName>()
                .Where(a => a.Enabled && a.delay.Value < .001f && a.eventTarget.target == FsmEventTarget.EventTarget.GameObject &&
                    (a.sendEvent.Value == "ROAR ENTER" || a.sendEvent.Value == "ROAR EXIT")).ToArray();
            if (!cues.Any(a => a.sendEvent.Value == "ROAR ENTER") || !cues.Any(a => a.sendEvent.Value == "ROAR EXIT")) continue;
            FsmBool Append(PlayMakerFSM fsm, string name = Variable) {
                var value = fsm.FsmVariables.FindFsmBool(name);
                if (value != null) return value;
                value = new FsmBool { Name = name };
                fsm.FsmVariables.BoolVariables = fsm.FsmVariables.BoolVariables.Concat(new[] { value }).ToArray();
                return value;
            }
            var binding = new Binding { Index = i, Host = host[i], Client = client[i], Native = Append(host[i]), Replica = Append(client[i]) };
            // Grimm's wave emitter is a separate scene object, outside both
            // entity subtrees. Its END impulse can precede CreateObject (or
            // reach a state without an END transition), leaving the waves at
            // the bow position. Retain a completed receipt in the existing
            // ordered bool snapshots so late creation can still be cleaned up.
            if (entity.Type == EntityType.Grimm) {
                binding.Emitter = client[i].FsmStates.Where(s => s.Name == "Roar").SelectMany(s => s.Actions).OfType<CreateObject>()
                    .FirstOrDefault(a => a.gameObject.Value != null && a.gameObject.Value.name == "Roar Wave Emitter")?.storeObject;
                if (binding.Emitter != null) {
                    binding.FinishedNative = Append(host[i], FinishedVariable);
                    binding.FinishedReplica = Append(client[i], FinishedVariable);
                }
                var hideBow = client[i].FsmStates.Where(s => s.Name == "GG Cancel Bow").SelectMany(s => s.Actions).OfType<ActivateGameObject>()
                    .FirstOrDefault(a => !a.activate.Value && client[i].Fsm.GetOwnerDefaultTarget(a.gameObject)?.name == "Grimm Bow");
                if (hideBow != null) binding.Bow = client[i].Fsm.GetOwnerDefaultTarget(hideBow.gameObject);
            }
            foreach (var cue in cues) binding.Cues[cue] = cue.sendEvent.Value == "ROAR ENTER";
            bindings.Add(binding);
        }
    }

    internal bool Handles(HutongGames.PlayMaker.Fsm sender, string signal, FsmState state = null) =>
        bindings.Any(b => ReferenceEquals(b.Client.Fsm, sender) && (signal == "ROAR ENTER" || signal == "ROAR EXIT" ||
            signal == "END" && state?.Name == "Roar End" && b.Emitter != null));

    internal void ActionEntered(FsmStateAction action) {
        foreach (var binding in bindings) {
            if (!binding.Cues.TryGetValue(action, out var active)) continue;
            Set(binding, active);
            // Native actions already affected the authority's own hero. Keep
            // ownership only when this roar actually acquired its native FSM.
            if (active) { applied = true; RememberLock(); }
            else Release();
        }
    }
    internal void StateEntered(FsmState state) {
        foreach (var binding in bindings) {
            if (!binding.Native.Value || !ReferenceEquals(state.Fsm, binding.Host.Fsm)) continue;
            if (state.Name == "Roar" || state.Name == "Roar End" || entity.Type == EntityType.TheCollector && state.Name == "Music") continue;
            // Death or another native interruption can skip Roar End.
            Set(binding, false); Release();
        }
    }
    private void Set(Binding binding, bool active) {
        if (binding.Native.Value == active) return;
        if (binding.FinishedNative != null) {
            binding.FinishedNative.Value = !active;
            publish(binding.Index, binding.FinishedNative);
        }
        binding.Native.Value = active; publish(binding.Index, binding.Native);
    }
    private static void FinishEmitter(Binding binding) {
        var emitter = binding.Emitter?.Value;
        if (emitter == null || emitter == binding.EndedEmitter || !emitter.activeInHierarchy) return;
        var fsm = emitter.LocateMyFSM("emitter");
        if (fsm == null || !fsm.Fsm.Started) return;
        if (fsm.ActiveStateName == "End" || fsm.ActiveStateName == "Destroy") { binding.EndedEmitter = emitter; return; }
        // Finish Lines has no END transition. Wait for its native completion
        // rather than losing the event or forcing a gameplay/FSM state.
        if (fsm.Fsm.ActiveState == null || !fsm.Fsm.ActiveState.Transitions.Any(t => t.EventName == "END")) return;
        fsm.SendEvent("END");
        if (fsm.ActiveStateName == "End" || fsm.ActiveStateName == "Destroy") binding.EndedEmitter = emitter;
    }
    internal void Tick() {
        if (bindings.Count == 0 || CoopContext.LocalVisit != visit) return;
        foreach (var binding in bindings)
            if (!binding.Replica.Value && binding.FinishedReplica?.Value == true) FinishEmitter(binding);
        if (entity.IsNativeAuthority) {
            if (entity.Object.Host != null && entity.Object.Host.activeInHierarchy) return;
            foreach (var binding in bindings) Set(binding, false);
            Release(); return;
        }
        // Godhome uses a separate Mecanim bow model, also outside the entity
        // subtree. Early damage can skip its transient hide action on the
        // replica; a running or completed roar proves the bow is already over.
        foreach (var binding in bindings)
            if ((binding.Replica.Value || binding.FinishedReplica?.Value == true) && binding.Bow != null && binding.Bow.activeSelf)
                binding.Bow.SetActive(false);
        var hero = HeroController.instance;
        if (hero == null || !hero.isHeroInPosition) return;
        var active = entity.Object.Client != null && entity.Object.Client.activeInHierarchy && bindings.Any(b => b.Replica.Value);
        if (active == applied) return;
        if (!active) { Release(); return; }
        applied = true;
        var roar = hero.gameObject.LocateMyFSM("Roar Lock");
        if (roar == null) return;
        roar.SendEvent("ROAR ENTER"); RememberLock();
    }
    private void RememberLock() {
        var roar = HeroController.instance?.gameObject.LocateMyFSM("Roar Lock");
        if (roar != null && (roar.ActiveStateName == "Lock Grounded" || roar.ActiveStateName == "Lock Air")) lockedHero = roar;
    }
    internal void Release() {
        applied = false;
        var roar = lockedHero; lockedHero = null;
        if (CoopContext.LocalVisit == visit && roar != null && HeroController.instance != null && roar.gameObject == HeroController.instance.gameObject &&
            (roar.ActiveStateName == "Lock Grounded" || roar.ActiveStateName == "Lock Air"))
            roar.SendEvent("ROAR EXIT");
    }
}
