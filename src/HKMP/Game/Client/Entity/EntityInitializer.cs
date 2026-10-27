using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Hkmp.Game.Client.Entity.Action;
using HutongGames.PlayMaker;
using HutongGames.PlayMaker.Actions;
using UnityEngine;
using Logger = Hkmp.Logging.Logger;

namespace Hkmp.Game.Client.Entity; 

/// <summary>
/// Class that manages initializing client-side entities to ensure they have correct references within FSM actions
/// to game objects (such as child objects).
/// </summary>
internal static class EntityInitializer {
    private static readonly FieldInfo TemplateField = typeof(PlayMakerFSM).GetField(
        "fsmTemplate", BindingFlags.Instance | BindingFlags.NonPublic);
    /// <summary>
    /// Array of state names that indicates that it is a initializing state.
    /// </summary>
    private static readonly string[] InitStateNames = {
        "init",
        "initiate",
        "initialise",
        "initialize",
        "dormant",
        "pause",
        "init pause",
        "deparents",
        "opened" // For battle gates
    };

    /// <summary>
    /// Array of types that should be removed from client-side enemies so it doesn't interfere with remote behaviour.
    /// </summary>
    private static readonly Type[] ToRemoveTypes = {
        typeof(Walker),
        typeof(GrimmballControl),
        typeof(BigCentipede)
    };

    /// <summary>
    /// Array of types of actions that should be skipped during initialization. 
    /// </summary>
    private static readonly Type[] ToSkipTypes = {
        typeof(Tk2dPlayAnimation),
        typeof(ActivateAllChildren),
        typeof(CreateObject), typeof(SpawnObjectFromGlobalPool), typeof(FlingObjectsFromGlobalPool),
        typeof(FlingObjectsFromGlobalPoolTime), typeof(FlingObjectsFromGlobalPoolVel), typeof(PreSpawnGameObjects),
        typeof(SetCollider) // TODO: test whether this has effects on other entities during host transfer (this was added for battle gates)
    };

    /// <summary>
    /// Initialize the FSM of a client entity by finding initialize states and executing the actions in those states.
    /// </summary>
    /// <param name="fsm">The FSM to initialize.</param>
    public static void InitializeFsm(PlayMakerFSM fsm) {
        InitializeOwnerReferences(fsm);
        // Create a list of states to initialize later
        var statesToInit = new List<FsmState>();
        // Keep track of the indices where the individual initialization states begin in our final list
        var indices = new int[InitStateNames.Length];

        CheckPreProcessFsm(fsm);

        // Go over each state in the FSM
        foreach (var state in fsm.FsmStates) {
            var stateName = state.Name.ToLower();
            var index = Array.IndexOf(InitStateNames, stateName);
            // Check if it is a "init" state
            if (index == -1) {
                continue;
            }

            // Then insert it at the correct index according to our tracked indices
            statesToInit.Insert(indices[index], state);
            // Increase all indices that come after, since we inserted something before
            for (var i = index; i < indices.Length; i++) {
                indices[i]++;
            }
        }

        // Now we can loop over the states in the same order as our "InitStateNames" array
        foreach (var state in statesToInit) {
            Logger.Debug($"Found initialization state: {state.Name}, executing actions");

            // Go over each action and try to execute it by applying empty data to it
            foreach (var action in state.Actions) {
                if (!action.Enabled) {
                    continue;
                }
                if (BindCachedReference(action)) continue;

                // Initialization binds references only. Replaying native events,
                // parenting/destruction or attack setup from unrelated init
                // states can destroy a Mantis replica or start a second AI.
                if (action is GetComponent getComponent) {
                    var target=fsm.Fsm.GetOwnerDefaultTarget(getComponent.gameObject);
                    if(target!=null && getComponent.storeComponent!=null && !getComponent.storeComponent.IsNone)
                        getComponent.storeComponent.Value=target.GetComponent(getComponent.storeComponent.ObjectType);
                    continue;
                }
                if (!(action is GetOwner || action is FindChild || action is GetParent || action is FindGameObject || action is FindAlertRange)) {
                    continue;
                }

                if (!EntityFsmActions.SupportedActionTypes.Contains(action.GetType())) {
                    continue;
                }

                if (action.Fsm == null) {
                    Logger.Error($"FSM in action for state '{state.Name}', action '{action.GetType()}' is null");
                    continue;
                }

                Logger.Debug($"  Executing action {action.GetType()} for initialization");

                EntityFsmActions.ApplyNetworkDataFromAction(null, action);
            }
        }
        // Some init states find a child of an object bound by a later init
        // state. Retry only reference lookups, without replaying state actions.
        foreach (var state in statesToInit)
            foreach (var action in state.Actions.OfType<FindChild>())
                if (action.Enabled) BindCachedReference(action);
        foreach (var state in statesToInit)
            foreach (var action in state.Actions.OfType<FindAlertRange>())
                if (action.Enabled) BindCachedReference(action);
    }

    private static bool BindCachedReference(FsmStateAction action) {
        if (action is FindAlertRange range) {
            if (range.storeResult == null || range.storeResult.IsNone || range.Fsm == null) return true;
            var target = range.Fsm.GetOwnerDefaultTarget(range.target);
            var found = target == null ? null : AlertRange.Find(target, range.childName);
            if (found == null && target != null) {
                // Moss detaches its Attack Range during native Init. A later
                // authority resumes Hidden, so direct child lookup now fails.
                // Its paired FindChild still retains the local detached object.
                foreach (var rangeChild in range.Fsm.States.SelectMany(s => s.Actions).OfType<FindChild>()) {
                    if (!rangeChild.Enabled || rangeChild.childName?.Value != range.childName || rangeChild.Fsm == null ||
                        rangeChild.Fsm.GetOwnerDefaultTarget(rangeChild.gameObject) != target) continue;
                    var cached = rangeChild.storeResult?.Value;
                    if (cached != null) found = cached.GetComponent<AlertRange>();
                    if (found != null) break;
                }
            }
            // Rebinding must not erase a live component merely because its
            // original parent no longer contains it.
            if (found != null) range.storeResult.Value = found;
            return true;
        }
        if (action is FindChild child) {
            if (child.storeResult == null || child.storeResult.IsNone || child.Fsm == null) return true;
            var target = child.Fsm.GetOwnerDefaultTarget(child.gameObject);
            var found = target == null ? null : target.transform.Find(child.childName.Value);
            if (found != null) child.storeResult.Value = found.gameObject;
            return true;
        }
        if (action is GetParent parent) {
            if (parent.storeResult == null || parent.storeResult.IsNone || parent.Fsm == null) return true;
            var target = parent.Fsm.GetOwnerDefaultTarget(parent.gameObject);
            if (target != null && target.transform.parent != null) parent.storeResult.Value = target.transform.parent.gameObject;
            return true;
        }
        return false;
    }

    // An FSM may bind its own object in an Idle state, rather than an Init
    // state. A late replica/authority resumes a later state and never executes
    // that one-shot binding (e.g. Galien's scythe Attack/Self). Bind only these
    // local references; do not replay movement, damage or spawn actions.
    internal static void InitializeOwnerReferences(PlayMakerFSM fsm) {
        CheckPreProcessFsm(fsm);
        foreach (var state in fsm.FsmStates) {
            foreach (var action in state.Actions.OfType<GetOwner>()) {
                if (!action.Enabled || action.storeGameObject == null || action.storeGameObject.IsNone) continue;
                action.storeGameObject.Value = fsm.gameObject;
            }
        }
    }

    /// <summary>
    /// Remove all types that should be removed from a client-side entity object.
    /// </summary>
    /// <param name="gameObject">The game object on which to remove the types.</param>
    public static void RemoveClientTypes(GameObject gameObject) {
        foreach (var body in gameObject.GetComponents<Rigidbody2D>()) {
            body.velocity = Vector2.zero; body.angularVelocity = 0; body.gravityScale = 0;
            body.bodyType = RigidbodyType2D.Kinematic;
            body.useFullKinematicContacts = true;
        }
        foreach (var type in ToRemoveTypes) {
            var component = gameObject.GetComponent(type);
            if (component != null) {
                if (component is MonoBehaviour script) { script.StopAllCoroutines(); script.enabled = false; }
                UnityEngine.Object.Destroy(component);
            }
        }
    }

    private static void Reprocess(PlayMakerFSM fsm, bool expandTemplate = false) {
        // Preprocess(owner) resets event-handler flags, but an initialized
        // action's Awake does not run again to restore its physics/late loop.
        // Nosk's Climb then sets upward speed once and never maintains it.
        // Preserve both native update requirements when rebinding any FSM.
        var fixedUpdate = fsm.Fsm.HandleFixedUpdate;
        var lateUpdate = fsm.Fsm.HandleLateUpdate;
        if (expandTemplate) fsm.Preprocess();
        else fsm.Fsm.Preprocess(fsm);
        if (fixedUpdate && !fsm.Fsm.HandleFixedUpdate) fsm.Fsm.HandleFixedUpdate = true;
        if (lateUpdate && !fsm.Fsm.HandleLateUpdate) fsm.Fsm.HandleLateUpdate = true;
        fsm.Fsm.Preprocessed = true;
        fsm.AddEventHandlerComponents();
    }

    /// <summary>
    /// Rebind missing action owners without replacing live state or native update handlers.
    /// </summary>
    /// <param name="fsm">The PlayMaker FSM to check and potentially pre-process.</param>
    public static void CheckPreProcessFsm(PlayMakerFSM fsm) {
        // A dormant prefab's serialized default state can already have
        // owners AND Preprocessed=true, while Awake has not expanded its
        // template. That flag alone does not prove a usable network schema.
        var template = fsm.FsmTemplate;
        var states = fsm.FsmStates;
        if (template != null && !fsm.Fsm.Started && states.Length == 1 &&
            states[0].Name == "State 1" && states[0].Actions.Length == 0) {
            Reprocess(fsm, true);
            TemplateField.SetValue(fsm, null);
            return;
        }

        // Preserve an already expanded native instance, including changes
        // made by Awake/OnEnable. PlayMakerFSM.Preprocess would reconstruct
        // it from the template and erase those live values and actions.
        if (template != null && !fsm.Fsm.Started) TemplateField.SetValue(fsm, null);
        if (!fsm.Fsm.Preprocessed) {
            Reprocess(fsm);
            return;
        }

        foreach (var state in fsm.FsmStates) {
            if (state.Fsm == null) {
                Logger.Debug($"Reference to FSM in state '{state.Name}' was null, pre-processing FSM...");
                Reprocess(fsm);
                break;
            }

            var wasPreProcessed = false;
            foreach (var action in state.Actions) {
                if (action.Fsm == null) {
                    Logger.Debug($"Reference to FSM in action '{action.GetType()}' in state '{state.Name}' was null, pre-processing FSM...");
                    Reprocess(fsm);
                    wasPreProcessed = true;
                    break;
                }
            }

            if (wasPreProcessed) {
                break;
            }
        }
    }
}
