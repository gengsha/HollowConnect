using System;
using System.Collections.Generic;
using System.Linq;
using HutongGames.PlayMaker;
using HutongGames.PlayMaker.Actions;
using UnityEngine;

namespace Hkmp.Game.Client.Entity;

// Failed Champion releases its intro through EventRegister, an action that
// replicas do not execute. Retain that one-way boundary in ordinary versioned
// FSM bool data, so entering after Music also releases the local intro.
internal sealed class BossIntroCamera {
    private const string Variable = "HollowConnect Intro Released";
    private const string Signal = "INTRO OFF";
    private readonly Entity entity;
    private readonly uint visit;
    private readonly Action<int, FsmBool> publish;
    private bool applied;
    private float nextAttempt;
    private readonly List<(int Index, PlayMakerFSM Host, FsmBool Released)> bindings = new();

    internal BossIntroCamera(Entity entity, IList<PlayMakerFSM> host, IList<PlayMakerFSM> client, Action<int, FsmBool> publish) {
        this.entity = entity; this.publish = publish; visit = CoopContext.LocalVisit;
        for (var i = 0; i < host.Count && i < client.Count; i++) {
            if (!host[i].FsmStates.Any(ReleasesIntro)) continue;
            FsmBool Append(PlayMakerFSM fsm) {
                var value = fsm.FsmVariables.FindFsmBool(Variable);
                if (value != null) return value;
                value = new FsmBool { Name = Variable };
                fsm.FsmVariables.BoolVariables = fsm.FsmVariables.BoolVariables.Concat(new[] { value }).ToArray();
                return value;
            }
            bindings.Add((i, host[i], Append(host[i])));
            Append(client[i]);
        }
    }
    private static bool ReleasesIntro(FsmState state) => state.Actions.OfType<SendEventToRegister>()
        .Any(a => a.Enabled && a.eventName.Value == Signal);

    internal void StateEntered(FsmState state) {
        foreach (var binding in bindings) {
            if (binding.Released.Value || !ReferenceEquals(binding.Host.Fsm, state.Fsm) || !ReleasesIntro(state)) continue;
            binding.Released.Value = true;
            publish(binding.Index, binding.Released);
        }
    }
    internal void Tick() {
        if (applied || entity.IsNativeAuthority || CoopContext.LocalVisit != visit || !bindings.Any(b => b.Released.Value) ||
            Time.unscaledTime < nextAttempt) return;
        nextAttempt = Time.unscaledTime + .25f;
        // Keep every other EventRegister subscriber and arena/entrance lock
        // local. Deliver only to this scene's native self-deactivating intro.
        foreach (var area in UnityEngine.Object.FindObjectsOfType<CameraLockArea>()) {
            if (area.gameObject.scene.name != entity.SourceScene) continue;
            foreach (var receiver in area.GetComponents<PlayMakerFSM>()) {
                if (receiver.Fsm.ActiveState?.Transitions.Any(t => t.EventName == Signal &&
                    receiver.FsmStates.Any(s => s.Name == t.ToState && s.Actions.OfType<ActivateGameObject>().Any(a =>
                        a.Enabled && !a.activate.Value && receiver.Fsm.GetOwnerDefaultTarget(a.gameObject) == area.gameObject))) == true) {
                    receiver.SendEvent(Signal);
                    applied = true;
                }
            }
        }
    }
}
