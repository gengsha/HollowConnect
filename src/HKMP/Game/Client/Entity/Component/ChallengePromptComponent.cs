using Hkmp.Networking.Client;
using System.Collections.Generic;
using System.Linq;
using Hkmp.Networking.Packet.Data;
using Hkmp.Util;
using HutongGames.PlayMaker.Actions;
using UnityEngine;
using Logger = Hkmp.Logging.Logger;

namespace Hkmp.Game.Client.Entity.Component;

/// <inheritdoc />
/// This component manages the challenge prompt that appears for Mantis Lords.
internal class ChallengePromptComponent : EntityComponent {
    /// <summary>
    /// The game object that handles the challenge prompt pop-up.
    /// </summary>
    private readonly GameObject _promptObj;

    /// <summary>
    /// The FSM corresponding to the challenge prompt object.
    /// </summary>
    private readonly PlayMakerFSM _promptFsm;
    private readonly PlayMakerFSM _cageFsm;
    private bool _requested, _localChallenge;
    private uint _visit, _epoch;
    private HeroController _hero;
    private static ChallengePromptComponent localChallenge;

    // The existing challenge cue is one int (zero). A peer requests it; the
    // authority's accepted cue also hides prompts for observers and late joins.
    internal static bool IsRequest(EntityNetworkData data) => data.Type == EntityComponentType.ChallengePrompt &&
        data.Packet.Length == 4 && System.BitConverter.ToInt32(data.Packet.ToArray(), 0) == 0;
    private bool Current => _localChallenge && _promptObj != null && _hero != null && _visit == CoopContext.LocalVisit &&
        _epoch == CoopContext.Epoch && !CoopContext.Resetting && CoopContext.LocalScene == "Fungus2_15";
    internal static bool AllowsLocalCage(HutongGames.PlayMaker.Fsm receiver, string signal, Entity source) =>
        localChallenge?.Current == true && signal == "MLORD CAGE DOWN" && source?.Type == EntityType.MantisThrone &&
        source.SourceScene == "Fungus2_15_boss" && localChallenge._cageFsm != null &&
        ReferenceEquals(receiver, localChallenge._cageFsm.Fsm);

    internal static void ObserveThrone(Entity source, HutongGames.PlayMaker.FsmState state) {
        var local = localChallenge;
        if (local?.Current != true || source.Type != EntityType.MantisThrone || source.SourceScene != "Fungus2_15_boss" ||
            state?.Fsm.Name != "Mantis Throne Main") return;
        var release = state.Fsm.States.FirstOrDefault(s => s.Name == "Regain Control" &&
            s.Actions.OfType<SetPlayerDataBool>().Any(a => a.Enabled && a.boolName.Value == "disablePause" && !a.value.Value) &&
            new[] { "RegainControl", "StartAnimationControl" }.All(method => s.Actions.OfType<CallMethodProper>().Any(a =>
                a.Enabled && a.behaviour.Value == "HeroController" && a.methodName.Value == method &&
                state.Fsm.GetOwnerDefaultTarget(a.gameObject) == local._hero.gameObject)));
        if (release == null) return;
        var pending = new Queue<HutongGames.PlayMaker.FsmState>(); pending.Enqueue(release);
        var seen = new HashSet<HutongGames.PlayMaker.FsmState>();
        while (pending.Count != 0) {
            var next = pending.Dequeue(); if (!seen.Add(next)) continue;
            if (next == state) {
                local._localChallenge = false; localChallenge = null;
                // This player's native prompt acquired control. Its matching
                // throne continuation releases only that acquisition, including
                // when the current snapshot has already advanced past it.
                if (!source.IsNativeAuthority) {
                    PlayerData.instance.SetBool("disablePause", false);
                    local._hero.RegainControl(); local._hero.StartAnimationControl();
                }
                return;
            }
            foreach (var transition in next.Transitions) {
                var target = state.Fsm.States.FirstOrDefault(s => s.Name == transition.ToState);
                if (target != null) pending.Enqueue(target);
            }
        }
    }

    public ChallengePromptComponent(
        NetClient netClient,
        ulong entityId,
        HostClientPair<GameObject> gameObject
    ) : base(netClient, entityId, gameObject) {
        var hostObj = gameObject.Host;
        var parent = hostObj.transform.parent;
        var parentTransform = parent.Find("Challenge Prompt");
        if (!parentTransform) {
            Logger.Debug("Could not find Challenge Prompt object");
            return;
        }

        _promptObj = parent.Find("Challenge Prompt").gameObject;
        _promptFsm = _promptObj.LocateMyFSM("Challenge Start");
        _cageFsm = parent.Find("mantis_cage_down")?.GetComponent<PlayMakerFSM>();

        _promptFsm.InsertMethod("Take Control", 6, () => {
            if (_requested) return;
            _requested = true;
            if (_promptObj.scene.name == "Fungus2_15_boss") {
                _localChallenge = true; _visit = CoopContext.LocalVisit; _epoch = CoopContext.Epoch;
                _hero = HeroController.instance; localChallenge = this;
            }
            var data = new EntityNetworkData {
                Type = EntityComponentType.ChallengePrompt
            };
            data.Packet.Write(0);

            SendData(data);
        });
    }

    /// <inheritdoc />
    public override void InitializeHost() {
    }

    /// <inheritdoc />
    public override void Update(EntityNetworkData data, bool alreadyInSceneUpdate) {
        if (!IsRequest(data) || _requested || _promptFsm == null || _promptObj == null || alreadyInSceneUpdate && !IsControlled) return;
        _requested = true;

        // If the player is a scene client we destroy the prompt, otherwise we start the fight by progressing the FSM
        if (IsControlled) {
            if (_promptObj != null) {
                Object.Destroy(_promptObj);
            }
        } else {
            // A peer request reaches only the authority. Publish acceptance
            // through the existing cue so observers cannot start it again.
            var accepted = new EntityNetworkData { Type = EntityComponentType.ChallengePrompt };
            accepted.Packet.Write(0); SendData(accepted);
            // Remove actions that rely on the local player
            _promptFsm.RemoveFirstAction<Tk2dPlayAnimation>("Challenge");
            _promptFsm.RemoveFirstAction<Tk2dWatchAnimationEvents>("Challenge");

            // Get some actions that we want to re-use in another state
            var activateObjAction = _promptFsm.GetFirstAction<ActivateGameObject>("Take Control");
            var sendEventAction = _promptFsm.GetFirstAction<SendEventByName>("Take Control");

            // Put these actions in the Challenge state for execution
            _promptFsm.InsertAction("Challenge", activateObjAction, 0);
            _promptFsm.InsertAction("Challenge", sendEventAction, 1);

            // Get the watch animation events action so we can get the FsmEvent is sends
            var watchAnimationEvent = _promptFsm.GetFirstAction<Tk2dWatchAnimationEvents>("Challenge Audio");

            // Insert a method that sends the event to go to the next stage instead of waiting for the animation to finish
            _promptFsm.InsertMethod("Challenge Audio", 1, () => {
                _promptFsm.Fsm.Event(watchAnimationEvent.animationCompleteEvent);
            });
            // Remove the original action
            _promptFsm.RemoveFirstAction<Tk2dWatchAnimationEvents>("Challenge Audio");

            // Start the FSM from the state 'Challenge'
            _promptFsm.SetState("Challenge");
        }
    }

    /// <inheritdoc />
    public override void Destroy() {
        if (ReferenceEquals(localChallenge, this)) localChallenge = null;
    }
}
