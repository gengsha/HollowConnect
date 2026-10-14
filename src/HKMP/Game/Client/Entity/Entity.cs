using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using Hkmp.Collection;
using Hkmp.Fsm;
using Hkmp.Game.Client.Entity.Action;
using Hkmp.Game.Client.Entity.Component;
using Hkmp.Networking.Client;
using Hkmp.Networking.Packet.Data;
using Hkmp.Util;
using HutongGames.PlayMaker;
using Modding;
using On.HutongGames.PlayMaker.Actions;
using UnityEngine;
using Logger = Hkmp.Logging.Logger;
using Vector2 = Hkmp.Math.Vector2;

namespace Hkmp.Game.Client.Entity;

/// <summary>
/// A networked entity that is either sending behaviour updates to the server or is entirely controlled by
/// updates from the server.
/// </summary>
internal class Entity {
    private bool destroyed;
    internal static readonly HashSet<Entity> WaitingForEntrance=new();
    internal bool OwnsHostObject;
    private float nextFsmSample;
    internal static event System.Action<Entity> LifetimeEnded;
    private readonly Queue<(HutongGames.PlayMaker.Fsm Fsm, HutongGames.PlayMaker.FsmEvent Event, float Deadline)> entranceEvents = new();
    internal bool QueueEntranceEvent(HutongGames.PlayMaker.Fsm fsm, HutongGames.PlayMaker.FsmEvent ev) {
        if (_isSceneHostDetermined || fsm.Active || ev == null || ev.Name == "FINISHED" || Object.Host == null ||
            !fsm.GameObject.transform.IsChildOf(Object.Host.transform)) return false;
        if (!fsm.States.Any(s=>s.Transitions.Any(t=>t.EventName==ev.Name)) && !fsm.GlobalTransitions.Any(t=>t.EventName==ev.Name)) return false;
        if (entranceEvents.Count < 64 && !entranceEvents.Any(e=>e.Fsm==fsm&&e.Event==ev)) entranceEvents.Enqueue((fsm,ev,Time.unscaledTime+8));
        return true;
    }
    internal void QueueEntranceBroadcast(HutongGames.PlayMaker.Fsm sender, HutongGames.PlayMaker.FsmEvent ev, bool excludeSelf) {
        if (_isSceneHostDetermined || Object.Host == null) return;
        foreach(var f in Object.Host.GetComponentsInChildren<PlayMakerFSM>(true))
            if (!excludeSelf || f.Fsm!=sender) QueueEntranceEvent(f.Fsm,ev);
    }
    private void ReplayEntranceEvents() {
        var count=entranceEvents.Count;
        for(var i=0;i<count;i++) {
            var item=entranceEvents.Dequeue();
            if(item.Fsm.GameObject==null || Time.unscaledTime>item.Deadline)continue;
            if(item.Fsm.Active && (item.Fsm.ActiveState?.Transitions.Any(t=>t.EventName==item.Event.Name)==true || item.Fsm.GlobalTransitions.Any(t=>t.EventName==item.Event.Name)))
                item.Fsm.ProcessEvent(item.Event);
            else entranceEvents.Enqueue(item);
        }
    }
    /// <summary>
    /// The net client for networking.
    /// </summary>
    private readonly NetClient _netClient;

    /// <summary>
    /// Whether the entity has a parent entity.
    /// </summary>
    private readonly bool _hasParent;

    /// <summary>
    /// The ID of the entity.
    /// </summary>
    public ulong Id { get; }
    public string SourceScene { get; }

    /// <summary>
    /// The type of the entity.
    /// </summary>
    public EntityType Type { get; }

    /// <summary>
    /// Host-client pair for the game objects.
    /// </summary>
    public HostClientPair<GameObject> Object { get; }

    /// <summary>
    /// Host-client pair for the sprite animators.
    /// </summary>
    private readonly HostClientPair<tk2dSpriteAnimator> _animator;

    /// <summary>
    /// Bi-directional lookup for animation clip names to IDs.
    /// </summary>
    private readonly BiLookup<string, byte> _animationClipNameIds;

    /// <summary>
    /// Host-client pair for the lists of FSMs on the entity.
    /// </summary>
    private readonly HostClientPair<List<PlayMakerFSM>> _fsms;

    /// <summary>
    /// Dictionary mapping data types to entity components.
    /// </summary>
    private readonly Dictionary<EntityComponentType, EntityComponent> _components;

    /// <summary>
    /// Dictionary mapping FSM actions to their entity action data instances.
    /// </summary>
    private readonly Dictionary<FsmStateAction, HookedEntityAction> _hookedActions;
    /// <summary>
    /// Set of FSM action types that have been hooked to prevent duplicate hooks.
    /// </summary>
    private readonly HashSet<Type> _hookedTypes;

    /// <summary>
    /// Whether the unity game object for the host entity was originally active.
    /// </summary>
    private bool _originalIsActive;
    private readonly Dictionary<PlayMakerFSM, bool> _suspendedRestartFlags = new();
    private readonly List<(Rigidbody2D Body, UnityEngine.Vector2 Velocity, float Angular, float Gravity,
        (PlayMakerFSM Fsm, string State)[] States)> _pausedNativeMotion = new();

    /// <summary>
    /// Whether the entity is controlled, i.e. in control by updates from the server.
    /// </summary>
    private bool _isControlled;

    /// <summary>
    /// Whether the scene host is determined, or alternatively whether the entity has been determined to be a host or client entity.
    /// </summary>
    private bool _isSceneHostDetermined;

    /// <summary>
    /// The last position of the entity.
    /// </summary>
    private Vector3 _lastPosition;
    /// <summary>
    /// The last scale of the entity.
    /// </summary>
    private Vector3 _lastScale;
    /// <summary>
    /// Whether the game object for the entity was last active.
    /// </summary>
    private bool _lastIsActive;

    /// <summary>
    /// Whether to allow the client entity to animate itself.
    /// </summary>
    private bool _allowClientAnimation;

    /// <summary>
    /// List of snapshots for each FSM of a host entity that contain latest values for state and FSM variables.
    /// Used to check whether state/variables change and to update the server accordingly.
    /// </summary>
    private readonly List<FsmSnapshot> _fsmSnapshots;
    private readonly Dictionary<(byte Fsm, byte Variable), float> _pendingReferences = new();

    private bool ApplyObjectReference(byte fsmIndex, byte variable, string key) {
        if (key == "@local") return true;
        bool Apply(PlayMakerFSM fsm, bool client) {
            if (fsm == null || variable >= fsm.FsmVariables.GameObjectVariables.Length) return true;
            var obj = EntityReferences.Resolve(key, client);
            if (obj == null && key.Length != 0) return false;
            fsm.FsmVariables.GameObjectVariables[variable].Value = obj; return true;
        }
        var host = Apply(_fsms.Host[fsmIndex], false);
        var client = fsmIndex >= _fsms.Client.Count || Apply(_fsms.Client[fsmIndex], true);
        return host && client;
    }
    internal void RetryReferences() {
        foreach (var pair in _pendingReferences.ToArray()) {
            var (fsm, variable) = pair.Key;
            if (ApplyObjectReference(fsm, variable, _fsmSnapshots[fsm].Objects[variable]) || Time.unscaledTime >= pair.Value)
                _pendingReferences.Remove(pair.Key);
        }
    }

    public Entity(
        NetClient netClient,
        ulong id,
        EntityType type,
        GameObject hostObject,
        GameObject clientObject = null,
        bool preserveHostActivation = false,
        params EntityComponentType[] types
    ) {
        _netClient = netClient;
        Id = id;
        SourceScene = hostObject.scene.name;

        Type = type;

        _isControlled = true;
        WaitingForEntrance.Add(this);

        if (clientObject == null) {
            // Instantiate beneath an inactive parent so native OnEnable/FSM
            // attack scripts cannot run once before the replica is disabled.
            var staging = new GameObject("HollowConnect replica staging");
            staging.SetActive(false);
            var clone = UnityEngine.Object.Instantiate(hostObject, staging.transform, true);
            clone.SetActive(false);
            // iTween is a runtime controller, not prefab state. Its private
            // arguments live in a global table keyed to the ORIGINAL target.
            // A copied component's first Awake cannot retrieve arguments for
            // the clone. Remove it while the staging hierarchy is still
            // inactive, before Awake; deferred Destroy is too late for a
            // snapshot delivered in the same frame. Do not call iTween.Stop:
            // copied public IDs can still refer to the native tween's table.
            foreach (var tween in clone.GetComponentsInChildren<iTween>(true))
                UnityEngine.Object.DestroyImmediate(tween);
            clone.transform.SetParent(null, true);
            clone.transform.position = hostObject.transform.position;
            clone.transform.rotation = hostObject.transform.rotation;
            UnityEngine.SceneManagement.SceneManager.MoveGameObjectToScene(clone, UnityEngine.SceneManagement.SceneManager.GetActiveScene());
            UnityEngine.Object.Destroy(staging);
            Object = new HostClientPair<GameObject> {
                Host = hostObject,
                Client = clone
            };

            EntityRegistry.TryGetEntry(Object.Host, out var parentEntry);
            DestroyManagedChildren(Object.Client, parentEntry?.Children);

            _hasParent = false;
        } else {
            Object = new HostClientPair<GameObject> {
                Host = hostObject,
                Client = clientObject
            };

            _hasParent = true;
        }

        Object.Client.transform.localScale = _lastScale = _hasParent 
            ? Object.Host.transform.localScale 
            : Object.Host.transform.lossyScale;

        // Store whether the host object was active and set it not active until we know if we are scene host
        _originalIsActive = Object.Host.activeSelf;

        _lastIsActive = _hasParent ? Object.Host.activeSelf : Object.Host.activeInHierarchy;

        Logger.Debug(
            $"Entity '{Object.Host.name}' was original active: {_originalIsActive}, last active: {_lastIsActive}");

        // Add a position interpolation component to the enemy so we can smooth out position updates
        Object.Client.AddComponent<PositionInterpolation>();

        // Register an update event to send position updates and check for certain value changes
        MonoBehaviourUtil.Instance.OnUpdateEvent += OnUpdate;

        _animator = new HostClientPair<tk2dSpriteAnimator> {
            Host = Object.Host.GetComponent<tk2dSpriteAnimator>(),
            Client = Object.Client.GetComponent<tk2dSpriteAnimator>()
        };
        if (_animator.Host != null) {
            _animationClipNameIds = new BiLookup<string, byte>();

            var index = 0;
            foreach (var animationClip in _animator.Host.Library.clips) {
                if (_animationClipNameIds.ContainsFirst(animationClip.name)) {
                    continue;
                }

                _animationClipNameIds.Add(animationClip.name, (byte)index++);

                if (index > byte.MaxValue) {
                    Logger.Error($"Too many animation clips to fit in a byte for entity: {Object.Client.name}");
                    break;
                }
            }

            EntityHooks.WatchAnimator(_animator.Host, _animator.Client, OnAnimationPlayed);
        }
        
        // Always disallow the client object from being recycled, because it will simply be destroyed
        EntityHooks.WatchRecycle(Object.Client, ObjectPoolOnRecycleGameObject);
        EntityHooks.WatchRecycle(Object.Host, ObjectPoolOnRecycleGameObject);
        
        // Register a hook for the ActivateGameObject action to update the active state of the host game object
        // before scene host is determined
        EntityHooks.WatchActivation(Object.Host, OnDoActivateGameObject);

        _fsms = new HostClientPair<List<PlayMakerFSM>> {
            Host = Object.Host.GetComponents<PlayMakerFSM>().OrderBy(f => f.FsmName, StringComparer.Ordinal).ToList(),
            Client = Object.Client.GetComponents<PlayMakerFSM>().OrderBy(f => f.FsmName, StringComparer.Ordinal).ToList()
        };

        _hookedActions = new Dictionary<FsmStateAction, HookedEntityAction>();
        _hookedTypes = new HashSet<Type>();
        _fsmSnapshots = new List<FsmSnapshot>();
        foreach (var fsm in _fsms.Host) {
            ProcessHostFsm(fsm);
        }

        // Remove all components that (re-)activate FSMs
        foreach (var fsmActivator in Object.Client.GetComponents<FSMActivator>()) {
            fsmActivator.StopAllCoroutines();
            UnityEngine.Object.Destroy(fsmActivator);
        }

        foreach (var fsm in _fsms.Client) {
            ProcessClientFsm(fsm);
        }
        foreach (var fsm in Object.Client.GetComponentsInChildren<PlayMakerFSM>(true)) {
            if (_fsms.Client.Contains(fsm)) continue;
            fsm.enabled = false;
        }
        foreach (var body in Object.Client.GetComponentsInChildren<Rigidbody2D>(true)) {
            body.velocity = UnityEngine.Vector2.zero; body.angularVelocity = 0; body.gravityScale = 0;
            body.bodyType = RigidbodyType2D.Kinematic; body.useFullKinematicContacts = true;
        }
        if (Type == EntityType.DynamicHazard) {
            // Unknown native projectile controllers must not integrate motion
            // on top of the authority's transforms. DamageHero stays local.
            foreach (var script in Object.Client.GetComponents<MonoBehaviour>()) {
                if (script == null || script is HealthManager || script is DamageHero || script is EnemyDeathEffects || script is PlayMakerFSM) continue;
                if (script.GetType().Assembly == typeof(HealthManager).Assembly) { script.StopAllCoroutines(); script.enabled = false; }
            }
        }

        _components = new Dictionary<EntityComponentType, EntityComponent>();
        HandleComponents(types);
        
        HandleEnemyDeathEffects();

        // A native pooled spawn has already run OnEnable and its launching
        // action before CombatSpawns.Flush observes it. Toggling it off here
        // stops native coroutines/tweens; enabling it again resets launch
        // parameters (GrimmballControl.Force, for example). Freeze only objects
        // whose authority has not been established, or dormant remote sources.
        if (!preserveHostActivation) {
            // An already-running native scene is registered again on reconnect.
            // Disabling a RestartOnEnable FSM resets its variables and state;
            // reenabling then runs the entrance instead of the current fight.
            foreach (var fsm in _fsms.Host.Where(f => f != null && f.Fsm.Started && !string.IsNullOrEmpty(f.ActiveStateName))) {
                _suspendedRestartFlags[fsm] = fsm.Fsm.RestartOnEnable;
                fsm.Fsm.RestartOnEnable = false;
            }
            if (_suspendedRestartFlags.Count != 0) {
                // Unity clears rigidbody velocity when a running hierarchy is
                // disabled. Capture children before pausing the parent: their
                // own later registration would otherwise only see zeroes.
                foreach (var body in Object.Host.GetComponentsInChildren<Rigidbody2D>(true)) {
                    if (!body.gameObject.activeInHierarchy) continue;
                    var states = body.GetComponents<PlayMakerFSM>().Where(f=>f.Fsm.Started&&!string.IsNullOrEmpty(f.ActiveStateName))
                        .Select(f=>(Fsm:f,State:f.ActiveStateName)).ToArray();
                    if (states.Length == 0) states = _suspendedRestartFlags.Keys.Select(f=>(Fsm:f,State:f.ActiveStateName)).ToArray();
                    _pausedNativeMotion.Add((body, body.velocity, body.angularVelocity, body.gravityScale, states));
                }
            }
            Object.Host.SetActive(false);
        }
        Object.Client.SetActive(false);
        
        // Room completion is owned by EntityManager, including empty boss arrays.
        
        // // Debug code that logs each action's OnEnter method call
        // foreach (var fsm in _fsms.Host) {
        //     foreach (var state in fsm.FsmStates) {
        //         foreach (var action in state.Actions) {
        //             FsmActionHooks.RegisterFsmStateActionType(action.GetType(), stateAction => {
        //                 if (stateAction != action) {
        //                     return;
        //                 }
        //                 
        //                 Logger.Debug($"Entity ({Id}, {Type}) has host FSM enter action: {state.Name}, {action.GetType()}, {state.Actions.ToList().IndexOf(action)}");
        //             });
        //         }
        //     }
        // }
    }

    /// <summary>
    /// Destroy the children of the given game object that are registered entities in the system themselves.
    /// Recursively go through the non-registered children as well.
    /// </summary>
    /// <param name="root">The root game object to start searching for children.</param>
    /// <param name="expected">Explicit child entities paired with this clone.</param>
    private void DestroyManagedChildren(GameObject root, IEnumerable<EntityRegistryEntry> expected = null) {
        foreach (var child in root.GetChildren()) {
            // Explicit child entities are paired with THIS clone later by the
            // processor. A loose top-level name fallback must not delete them.
            if (expected != null && EntityRegistry.TryGetEntry(expected, child, out var paired)) {
                DestroyManagedChildren(child, paired.Children);
            } else if (EntityRegistry.TryGetEntry(child, out var entry)) {
                Logger.Debug($"Found managed child: {child.name}, {entry.Type}, destroying it");
                UnityEngine.Object.Destroy(child);
            } else {
                DestroyManagedChildren(child, expected);
            }
        }
    }

    /// <summary>
    /// Processes the given FSM for the host entity by hooking supported FSM actions.
    /// </summary>
    /// <param name="fsm">The Playmaker FSM to process.</param>
    private void ProcessHostFsm(PlayMakerFSM fsm) {
        Logger.Debug($"Processing host FSM: {fsm.Fsm.Name}");

        EntityInitializer.CheckPreProcessFsm(fsm);
        
        for (var i = 0; i < fsm.FsmStates.Length; i++) {
            var state = fsm.FsmStates[i];
            var stateName = state.Name;

            for (var j = 0; j < state.Actions.Length; j++) {
                var action = state.Actions[j];
                // Boss setup can enable a previously disabled attack action
                // after registration (Godhome Grimm replaces bats with pillars).
                // Observe every supported action; only executed OnEnter fires.
                if (!EntityFsmActions.SupportedActionTypes.Contains(action.GetType())) {
                    continue;
                }

                if (action.Fsm == null) {
                    Logger.Error($"FSM in action for state ({i}, {state.Name}), action ({j}, {action.GetType()}) is null");
                    continue;
                }

                _hookedActions[action] = new HookedEntityAction {
                    Action = action,
                    FsmIndex = _fsms.Host.IndexOf(fsm),
                    StateIndex = i,
                    ActionIndex = j
                };
                Logger.Debug(
                    $"Created hooked action: {action.GetType()}, {_fsms.Host.IndexOf(fsm)}, {stateName}, {j}");

                if (_hookedTypes.Add(action.GetType())) {
                    FsmActionHooks.RegisterFsmStateActionType(action.GetType(), Object.Host, OnActionEntered);
                }
            }
        }

        var snapshot = new FsmSnapshot {
            CurrentState = fsm.ActiveStateName
        };

        snapshot.Objects = new string[fsm.FsmVariables.GameObjectVariables.Length];
        snapshot.Floats = fsm.FsmVariables.FloatVariables.Select(f => f.Value).ToArray();
        snapshot.Ints = fsm.FsmVariables.IntVariables.Select(i => i.Value).ToArray();
        snapshot.Bools = fsm.FsmVariables.BoolVariables.Select(b => b.Value).ToArray();
        snapshot.Strings = fsm.FsmVariables.StringVariables.Select(s => s.Value).ToArray();
        snapshot.Vector2s = fsm.FsmVariables.Vector2Variables.Select(v => v.Value).ToArray();
        snapshot.Vector3s = fsm.FsmVariables.Vector3Variables.Select(v => v.Value).ToArray();

        _fsmSnapshots.Add(snapshot);
        fsm.Fsm.StateChanged += OnHostStateChanged;
    }

    internal void EnterNativeState(On.HutongGames.PlayMaker.Fsm.orig_EnterState orig, HutongGames.PlayMaker.Fsm fsm, FsmState state) {
        if (destroyed || _isControlled || !_isSceneHostDetermined || fsm.GameObject != Object.Host) {
            orig(fsm, state);
            return;
        }
        // StateChanged runs before OnEnter. Hold the normal send locks across
        // both so a parent continuation cannot precede its child activation.
        _netClient.UpdateManager.RunAtomicUpdate(() => orig(fsm, state));
    }

    private void OnHostStateChanged(FsmState state) {
        if (destroyed || _isControlled || !_isSceneHostDetermined) return;
        var index = _fsms.Host.FindIndex(f => f != null && ReferenceEquals(f.Fsm, state.Fsm));
        if (index < 0 || index >= byte.MaxValue) return;
        // State entry precedes native OnEnter side effects (including activating
        // another entity). Publish the new continuation state immediately, so
        // those effects cannot be paired with a previous attack's sampled state.
        _fsmSnapshots[index].CurrentState = state.Name;
        var data = new EntityHostFsmData { CurrentState = (byte)Array.IndexOf(_fsms.Host[index].FsmStates, state) };
        data.Types.Add(EntityHostFsmData.Type.State);
        _lastIsActive = _hasParent ? Object.Host.activeSelf : Object.Host.activeInHierarchy;
        _netClient.UpdateManager.AddEntityHostFsmData(Id, (byte)index, data, _lastIsActive);
        nextFsmSample = 0;
    }

    /// <summary>
    /// Processes the given FSM for the client entity by disabling it.
    /// </summary>
    /// <param name="fsm">The Playmaker FSM to process.</param>
    private void ProcessClientFsm(PlayMakerFSM fsm) {
        Logger.Debug($"Processing client FSM: {fsm.Fsm.Name}");
        EntityInitializer.InitializeFsm(fsm);
        fsm.enabled = false;
    }

    /// <summary>
    /// Check the host and client objects for components that are supported for networking.
    /// </summary>
    private void HandleComponents(EntityComponentType[] types) {
        var addedComponentsString = $"Adding components to entity ({Object.Host.name}, {Id}):";
        
        var hostHealthManager = Object.Host.GetComponent<HealthManager>();
        var clientHealthManager = Object.Client.GetComponent<HealthManager>();
        if (hostHealthManager != null && clientHealthManager != null) {
            var healthManager = new HostClientPair<HealthManager> {
                Host = hostHealthManager,
                Client = clientHealthManager
            };

            var hmComponent = new HealthManagerComponent(
                _netClient,
                Id,
                Object,
                healthManager
            );
            _components[EntityComponentType.Death] = hmComponent;
            _components[EntityComponentType.Invincibility] = hmComponent;
            _components[EntityComponentType.Health] = hmComponent;
            _components[EntityComponentType.HealthReset] = hmComponent;
            _components[EntityComponentType.SpecialDeath] = hmComponent;
            _components[EntityComponentType.ExtraDamage] = hmComponent;

            // Check if the object from the health manager is in any of the colosseum trial scenes and remove the
            // geo drops from them if so
            var goScene = hostHealthManager.gameObject.scene.name;
            if (goScene is "Room_Colosseum_Bronze" or "Room_Colosseum_Silver" or "Room_Colosseum_Gold") {
                clientHealthManager.SetGeoSmall(0);
                clientHealthManager.SetGeoMedium(0);
                clientHealthManager.SetGeoLarge(0);
            }

            addedComponentsString += " Death Invincibility";
        }

        var climber = Object.Client.GetComponent<Climber>();
        if (climber != null) {
            _components[EntityComponentType.Climber] = new ClimberComponent(
                _netClient,
                Id,
                Object,
                climber
            );
            _components[EntityComponentType.Rotation] = new RotationComponent(
                _netClient,
                Id,
                Object
            );
            
            addedComponentsString += " Climber Rotation";
        }

        var hostCollider = Object.Host.GetComponent<Collider2D>();
        var clientCollider = Object.Client.GetComponent<Collider2D>();
        if (hostCollider != null && clientCollider != null) {
            Logger.Debug($"Adding collider component to entity: {Object.Host.name}");

            var collider = new HostClientPair<Collider2D> {
                Host = hostCollider,
                Client = clientCollider
            };

            _components[EntityComponentType.Collider] = new ColliderComponent(
                _netClient,
                Id,
                Object,
                collider
            );
            
            addedComponentsString += " Collider";
        }

        var hostDamageHero = Object.Host.GetComponent<DamageHero>();
        var clientDamageHero = Object.Client.GetComponent<DamageHero>();
        if (hostDamageHero != null && clientDamageHero != null) {
            Logger.Debug($"Adding DamageHero component to entity: {Object.Host.name}");

            var damageHero = new HostClientPair<DamageHero> {
                Host = hostDamageHero,
                Client = clientDamageHero
            };

            _components[EntityComponentType.DamageHero] = new DamageHeroComponent(
                _netClient,
                Id,
                Object,
                damageHero
            );
            
            addedComponentsString += " DamageHero";
        }
        
        var hostMeshRenderer = Object.Host.GetComponent<MeshRenderer>();
        var clientMeshRenderer = Object.Client.GetComponent<MeshRenderer>();
        if (hostMeshRenderer != null && clientMeshRenderer != null) {
            Logger.Debug($"Adding MeshRenderer component to entity: {Object.Host.name}");

            var meshRenderer = new HostClientPair<MeshRenderer> {
                Host = hostMeshRenderer,
                Client = clientMeshRenderer
            };

            _components[EntityComponentType.MeshRenderer] = new MeshRendererComponent(
                _netClient,
                Id,
                Object,
                meshRenderer
            );
            
            addedComponentsString += " MeshRenderer";
        }

        EntityInitializer.RemoveClientTypes(Object.Client);
        _components[EntityComponentType.CombatState] = new CombatStateComponent(_netClient, Id, Object);

        // Instantiate all types defined in the entity registry, which are passed to the constructor
        foreach (var type in types) {
            var component = ComponentFactory.InstantiateByType(type, _netClient, Id, Object);
            if (component == null) {
                Logger.Debug($"Could not instantiate component for type: {type}");
            } else {
                _components[type] = component;
            }
            
            addedComponentsString += $" {type}";
        }
        // Every native rigidbody needs these values to continue its current
        // attack on takeover, even when the historical registry omitted them.
        var nativeBody = Object.Host.GetComponent<Rigidbody2D>();
        if (nativeBody != null) {
            if (!_components.ContainsKey(EntityComponentType.Velocity))
                _components[EntityComponentType.Velocity] = new VelocityComponent(_netClient, Id, Object, nativeBody);
            if (!_components.ContainsKey(EntityComponentType.GravityScale))
                _components[EntityComponentType.GravityScale] = new GravityScaleComponent(_netClient, Id, Object, nativeBody);
        }

        Logger.Debug(addedComponentsString);
    }

    /// <summary>
    /// Handle specifics for a set of enemies that rely on EnemyDeathEffects for additional enemies.
    /// </summary>
    private void HandleEnemyDeathEffects() {
        string corpseName;
        switch (Type) {
            case EntityType.Ooma:
                corpseName = "Corpse Jellyfish(Clone)";
                break;
            case EntityType.Flukemon:
                corpseName = "Corpse Flukeman(Clone)";
                break;
            case EntityType.HuskHornhead:
                corpseName = "Zombie Spider 2(Clone)";
                break;
            case EntityType.WanderingHusk:
                corpseName = "Zombie Spider 1(Clone)";
                break;
            case EntityType.DungDefender:
                corpseName = "Corpse Dung Defender(Clone)";
                break;
            case EntityType.BrokenVessel:
                corpseName = "Corpse Infected Knight(Clone)";
                break;
            case EntityType.LostKin:
                corpseName = "Corpse Infected Knight Dream(Clone)";
                break;
            default:
                return;
        }
        
        Logger.Debug($"Entity ({Id}, {Type}) has corpse that is also enemy, deleting death effects and corpse from client entity");
        
        var enemyDeathEffects = Object.Client.GetComponent<EnemyDeathEffects>();
        if (enemyDeathEffects == null) {
            Logger.Debug("  EnemyDeathEffects is null, cannot remove");
        }
        UnityEngine.Object.Destroy(enemyDeathEffects);

        var corpse = Object.Client.FindGameObjectInChildren(corpseName);
        if (corpse != null) {
            Logger.Debug($"  Destroying corpse of client object: {corpse.name}");
            UnityEngine.Object.Destroy(corpse);
        } else {
            Logger.Debug("  Could not find corpse of client object");
        }
    }

    /// <summary>
    /// Attach authoritative native scene-completion synchronization to Godhome bosses.
    /// </summary>
    private void CheckGodhome() {
        var bossSceneControllers = UnityEngine.Object.FindObjectsOfType<BossSceneController>();
        var bossSceneController = bossSceneControllers.FirstOrDefault(
            con => con.gameObject.scene.Equals(UnityEngine.SceneManagement.SceneManager.GetActiveScene())
        );
        if (bossSceneController == null) {
            return;
        }
        
        var hostHealthManager = Object.Host.GetComponent<HealthManager>();
        if (hostHealthManager == null) {
            return;
        }
        
        var clientHealthManager = Object.Client.GetComponent<HealthManager>();
        if (clientHealthManager == null) {
            Logger.Debug($"Entity ({Id}, {Type}) has HealthManager on host but not on client");
            return;
        }
        
        if (!bossSceneController.bosses.Contains(hostHealthManager)) {
            return;
        }
        
        _components[EntityComponentType.BossSceneComplete] = new BossSceneComponent(_netClient, Id, Object, bossSceneController);
    }

    /// <summary>
    /// Callback method for entering a hooked FSM action.
    /// </summary>
    /// <param name="self">The FSM action instance that was entered.</param>
    private void OnActionEntered(FsmStateAction self) {
        if (_isControlled) {
            return;
        }

        if (!_hookedActions.TryGetValue(self, out var hookedEntityAction)) {
            // PlayMaker/Godhome can rebuild action instances after our scene
            // registration. Bind the executing instance to its current slot;
            // unrelated native FSMs remain outside this entity's observer.
            var fsmIndex = _fsms.Host.FindIndex(f => f != null && ReferenceEquals(f.Fsm, self.Fsm));
            if (fsmIndex < 0 || fsmIndex > byte.MaxValue) return;
            var states = _fsms.Host[fsmIndex].FsmStates;
            var stateIndex = Array.FindIndex(states, s => ReferenceEquals(s, self.State));
            if (stateIndex < 0 || stateIndex > byte.MaxValue) return;
            var actionIndex = Array.IndexOf(states[stateIndex].Actions, self);
            if (actionIndex < 0 || actionIndex > byte.MaxValue) return;
            _hookedActions[self] = hookedEntityAction = new HookedEntityAction {
                Action = self, FsmIndex = fsmIndex, StateIndex = stateIndex, ActionIndex = actionIndex
            };
        }

        Logger.Debug(
            $"Entity ({Id}, {Type}) hooked action: {self.Fsm.Name}, {self.State.Name}, {self.GetType()} ({hookedEntityAction.FsmIndex}, {hookedEntityAction.StateIndex}, {hookedEntityAction.ActionIndex})");

        var networkData = new EntityNetworkData {
            Type = EntityComponentType.Fsm
        };

        if (_fsms.Host.Count > 1) {
            networkData.Packet.Write((byte)hookedEntityAction.FsmIndex);
        }

        networkData.Packet.Write((byte)hookedEntityAction.StateIndex);
        networkData.Packet.Write((byte)hookedEntityAction.ActionIndex);

        // Only if the GetNetworkDataFromAction method returns true do we add the entity data
        // for sending
        if (EntityFsmActions.GetNetworkDataFromAction(networkData, self)) {
            _netClient.UpdateManager.AddEntityData(Id, networkData);
        }
    }

    /// <summary>
    /// Callback method for handling updates.
    /// </summary>
    [SuppressMessage("ReSharper", "CompareOfFloatsByEqualityOperator")]
    private void OnUpdate() {
        if (!_isControlled && _isSceneHostDetermined && entranceEvents.Count!=0) ReplayEntranceEvents();
        if (!_isControlled && Object.Host == null && (Id & 0x8000000000000000UL) != 0) {
            LifetimeEnded?.Invoke(this); return;
        }
        if (Object.Host == null) {
            if (_lastIsActive) {
                // If the host object was active, but now it null (or destroyed in Unity), we can send
                // to the server that the entity can be regarded as inactive
                if (Object.Client == null) {
                    Logger.Debug($"Entity ({Id}, {Type}) host and client object is null (or destroyed) and was active");
                } else {
                    Logger.Debug($"Entity '{Object.Client.name}' host object is null (or destroyed) and was active");
                }

                _lastIsActive = false;

                _netClient.UpdateManager.UpdateEntityIsActive(
                    Id,
                    false
                );
            }

            return;
        }

        var hostObjectActive = Object.Host.activeSelf;

        if (_isControlled) {
            if (hostObjectActive) {
                if (!_isSceneHostDetermined) {
                    Logger.Debug($"Entity '{Object.Host.name}' host object became active, but scene host is not determined yet, re-disabling for now");
                    _originalIsActive = true;
                } else {
                    Logger.Debug($"Entity '{Object.Host.name}' host object became active, re-disabling");
                }

                Object.Host.SetActive(false);
            }

            return;
        }

        var transform = Object.Host.transform;

        // Registration ownership survives native reparenting. Position packets
        // use world space so that ownership cannot select an obsolete basis.
        var newPosition = transform.position;
        if (newPosition != _lastPosition) {
            _lastPosition = newPosition;

            _netClient.UpdateManager.UpdateEntityPosition(
                Id,
                new Vector2(newPosition.x, newPosition.y)
            );
        }

        const float epsilon = 0.0001f;

        var newScale = _hasParent ? transform.localScale : transform.lossyScale;
        if (newScale != _lastScale) {
            var scaleData = new EntityUpdate.ScaleData {
                origin = true
            };

            if (newScale.x != _lastScale.x) {
                scaleData.x = true;
                scaleData.xScale = newScale.x;

                if (System.Math.Abs(newScale.x - _lastScale.x * -1) < epsilon) {
                    scaleData.xFlipped = true;
                }
            }
            
            if (newScale.y != _lastScale.y) {
                scaleData.y = true;
                scaleData.yScale = newScale.y;

                if (System.Math.Abs(newScale.y - _lastScale.y * -1) < epsilon) {
                    scaleData.yFlipped = true;
                }
            }
            
            if (newScale.z != _lastScale.z) {
                scaleData.z = true;
                scaleData.zScale = newScale.z;

                if (System.Math.Abs(newScale.z - _lastScale.z * -1) < epsilon) {
                    scaleData.zFlipped = true;
                }
            }

            _netClient.UpdateManager.UpdateEntityScale(Id, scaleData);
            
            _lastScale = newScale;
        }

        var newActive = _hasParent ? Object.Host.activeSelf : Object.Host.activeInHierarchy;
        if (newActive != _lastIsActive) {
            _lastIsActive = newActive;

            Logger.Debug($"Entity '{Object.Host.name}' changed active: {newActive}");

            _netClient.UpdateManager.UpdateEntityIsActive(
                Id,
                newActive
            );
        }

        if(Time.unscaledTime<nextFsmSample)return;
        nextFsmSample=Time.unscaledTime+.05f;
        for (byte fsmIndex = 0; fsmIndex < _fsms.Host.Count; fsmIndex++) {
            var fsm = _fsms.Host[fsmIndex];
            var snapshot = _fsmSnapshots[fsmIndex];

            var data = new EntityHostFsmData();

            var lastStateName = snapshot.CurrentState;
            if (fsm.ActiveStateName != lastStateName) {
                snapshot.CurrentState = fsm.ActiveStateName;

                data.Types.Add(EntityHostFsmData.Type.State);
                data.CurrentState = (byte) Array.IndexOf(fsm.FsmStates, fsm.Fsm.ActiveState);
                
                Logger.Debug($"Entity ({Id}, {Type}) host changed states: {lastStateName}, {fsm.ActiveStateName}");
            }

            // Define a method that allows generalization of checking for changes in all FSM variables
            void CondAddData<TVar, TBase, TData>(
                TVar[] fsmVars,
                TBase[] snapshotArray,
                Func<TVar, TBase> fsmVarValue,
                EntityHostFsmData.Type type,
                Dictionary<byte, TData> dataDict
            ) {
                for (byte i = 0; i < fsmVars.Length; i++) {
                    var fsmVar = fsmVars[i];
                    var snapshotVar = snapshotArray[i];

                    if (snapshotVar == null) {
                        Logger.Warn("No last value found for FSM var");
                        continue;
                    }

                    var value = fsmVarValue.Invoke(fsmVar);
                    if (!value.Equals(snapshotVar)) {
                        // Update the value in the snapshot since it changed
                        snapshotArray[i] = value;

                        data.Types.Add(type);
                        // Some funky casting here to make sure we can use this method with Vector2 and Vector3
                        // Since there is a mismatch between our Hkmp.Math.Vector2 and Unity's Vector2
                        // But our types have explicit converters, so casting is possible
                        if (value is UnityEngine.Vector2 vec2) {
                            dataDict[i] = (TData) (object) (Vector2) vec2;
                        } else if (value is Vector3 vec3) {
                            dataDict[i] = (TData) (object) (Hkmp.Math.Vector3) vec3;
                        } else {
                            dataDict[i] = (TData) (object) value;
                        }
                    }
                }
            }

            CondAddData(
                fsm.FsmVariables.FloatVariables, 
                snapshot.Floats,
                fsmFloat => fsmFloat.Value,
                EntityHostFsmData.Type.Floats,
                data.Floats
            );
            CondAddData(
                fsm.FsmVariables.IntVariables, 
                snapshot.Ints,
                fsmInt => fsmInt.Value,
                EntityHostFsmData.Type.Ints,
                data.Ints
            );
            CondAddData(
                fsm.FsmVariables.BoolVariables, 
                snapshot.Bools,
                fsmBool => fsmBool.Value,
                EntityHostFsmData.Type.Bools,
                data.Bools
            );
            CondAddData(
                fsm.FsmVariables.StringVariables, 
                snapshot.Strings,
                fsmString => fsmString.Value,
                EntityHostFsmData.Type.Strings,
                data.Strings
            );
            CondAddData(
                fsm.FsmVariables.Vector2Variables, 
                snapshot.Vector2s,
                fsmVec2 => fsmVec2.Value,
                EntityHostFsmData.Type.Vector2s,
                data.Vec2s
            );
            CondAddData(
                fsm.FsmVariables.Vector3Variables, 
                snapshot.Vector3s,
                fsmVec3 => fsmVec3.Value,
                EntityHostFsmData.Type.Vector3s,
                data.Vec3s
            );

            for (var i = 0; i < fsm.FsmVariables.GameObjectVariables.Length && i < 255; i++) {
                var key = EntityReferences.Capture(fsm.FsmVariables.GameObjectVariables[i].Value);
                if (snapshot.Objects[i] == key) continue;
                snapshot.Objects[i] = key; data.Types.Add(EntityHostFsmData.Type.Objects); data.Objects[(byte)i] = key;
            }

            if (data.Types.Count > 0) {
                _netClient.UpdateManager.AddEntityHostFsmData(Id, fsmIndex, data);
            }
        }
    }

    /// <summary>
    /// Callback method for when the sprite animator plays an animation.
    /// </summary>
    /// <param name="orig">The original method.</param>
    /// <param name="self">The sprite animator instance.</param>
    /// <param name="clip">The animation clip that was played.</param>
    /// <param name="clipStartTime">The start time of the animation clip.</param>
    /// <param name="overrideFps">The FPS override for the clip.</param>
    private void OnAnimationPlayed(
        On.tk2dSpriteAnimator.orig_Play_tk2dSpriteAnimationClip_float_float orig,
        tk2dSpriteAnimator self,
        tk2dSpriteAnimationClip clip,
        float clipStartTime,
        float overrideFps
    ) {
        if (self == _animator.Client) {
            if (!_allowClientAnimation) {
                Logger.Debug($"Entity '{Object.Client.name}' client animator tried playing animation");
            } else {
                // Logger.Debug($"Entity '{_object.Client.name}' client animator was allowed to play animation");

                orig(self, clip, clipStartTime, overrideFps);

                _allowClientAnimation = false;
            }

            return;
        }

        orig(self, clip, clipStartTime, overrideFps);

        if (self != _animator.Host) {
            return;
        }

        if (_isControlled) {
            return;
        }

        if (!_animationClipNameIds.TryGetValue(clip.name, out var animationId)) {
            Logger.Warn($"Entity '{Object.Client.name}' played unknown animation: {clip.name}");
            return;
        }

        Logger.Debug($"Entity '{Object.Host.name}' sends animation: {clip.name}, {animationId}, {clip.wrapMode}");
        _netClient.UpdateManager.UpdateEntityAnimation(
            Id,
            animationId,
            (byte)clip.wrapMode
        );
    }
    
    /// <summary>
    /// Callback method for when a game object is recycled. Used to prevent client objects from being recycled, which
    /// shouldn't happen because they are instantiated manually instead of from a pool.
    /// </summary>
    private void ObjectPoolOnRecycleGameObject(On.ObjectPool.orig_Recycle_GameObject orig, GameObject obj) {
        if (obj == Object.Client) {
            Logger.Debug($"Client object of entity: {Id}, {Type} tried to be recycled");
            return;
        }

        orig(obj);
        if (obj == Object.Host && !_isControlled && (Id & 0x8000000000000000UL) != 0) LifetimeEnded?.Invoke(this);
    }
    
    /// <summary>
    /// Callback method for when the 'active' of the host game object is changed. Used to update whether the host
    /// game object should return to what active state after the scene host is determined.
    /// </summary>
    private void OnDoActivateGameObject(ActivateGameObject.orig_DoActivateGameObject orig, HutongGames.PlayMaker.Actions.ActivateGameObject self) {
        // If the game object in the action is not our host game object, we skip it
        if (self.Fsm.GetOwnerDefaultTarget(self.gameObject) != Object.Host || Object.Host == null) {
            orig(self);
            return;
        }
        
        // If the host client is determined already we skip (although this hook should have been deregistered
        if (_isSceneHostDetermined) {
            orig(self);
            return;
        }
        
        Logger.Debug($"Entity '{Object.Host.name}' tried changing active of host object, while host is not determined yet, updating original active to: {self.activate.Value}");

        // Update the original active value to whatever this action will set
        // Also, we do not let this action execute any further since we do not want it to modify our host object
        // before the scene host is determined
        _originalIsActive = self.activate.Value;
    }

    /// <summary>
    /// Initializes the entity when the client user is the scene host.
    /// </summary>
    public void InitializeHost(bool deferMotionRestore = false) {
        if (destroyed || Object.Host == null) return;
        WaitingForEntrance.Remove(this);
        foreach (var fsm in _fsms.Host) if (fsm != null) EntityInitializer.InitializeFsm(fsm);
        _isControlled = false;
        _isSceneHostDetermined = true;
        Object.Host.SetActive(_originalIsActive);
        RestoreRestartFlags();

        // Also update the last active variable to account for this potential change
        // Otherwise we might trigger the update sending of activity twice
        _lastIsActive = _hasParent ? Object.Host.activeSelf : Object.Host.activeInHierarchy;

        Logger.Debug(
            $"Initializing entity '{Object.Host.name}' with active: {_originalIsActive}, sending active: {_lastIsActive}");

        _netClient.UpdateManager.UpdateEntityIsActive(Id, _lastIsActive);

        _isControlled = false;
        _isSceneHostDetermined = true;

        foreach (var component in _components.Values.Distinct()) {
            component.IsControlled = false;
            component.InitializeHost();
        }
        ReplayEntranceEvents();
        if (!deferMotionRestore) CompleteHostInitialization();
        
        // Deregister the hook for updating the active value of the host object
        EntityHooks.ForgetActivation(Object.Host);
    }

    internal void CompleteHostInitialization() {
        foreach (var motion in _pausedNativeMotion) {
            var body = motion.Body;
            if (body == null || !body.gameObject.activeInHierarchy) continue;
            // A replayed native entrance event may have advanced the FSM and
            // deliberately stopped/launched it. That new state's physics wins.
            if (motion.States.Any(s=>s.Fsm==null||s.Fsm.ActiveStateName!=s.State)) continue;
            body.velocity = motion.Velocity;
            body.angularVelocity = motion.Angular;
            body.gravityScale = motion.Gravity;
        }
        _pausedNativeMotion.Clear();
    }

    /// <summary>
    /// Initializes the entity when the client user is a scene client. Only sets a variable to indicate the scene
    /// host has been determined.
    /// </summary>
    public void InitializeClient() {
        _pausedNativeMotion.Clear();
        WaitingForEntrance.Remove(this);
        _isSceneHostDetermined = true;
        entranceEvents.Clear();
        EntityReferences.DisableDetachedParts(this, false);
        
        // Deregister the hook for updating the active value of the host object
        EntityHooks.ForgetActivation(Object.Host);
    }

    /// <summary>
    /// Makes the entity a host entity if the client user became the scene host.
    /// </summary>
    public void MakeHost() {
        // A takeover restores the current authority's component snapshots,
        // never the native values from this peer's earlier registration.
        _pausedNativeMotion.Clear();
        Logger.Debug($"Making entity ({Id}, {Type}) a host entity");

        // Unity can destroy either side after a terminal death while its
        // network tombstone remains registered for the room's lifetime.
        if (Object.Host == null) return;

        // If the client object is null, we don't have to care about doing anything for the host object anymore
        if (Object.Client == null) {
            if (Object.Host != null) {
                Object.Host.SetActive(false);
            }
            
            _isControlled = false;

            foreach (var component in _components.Values.Distinct()) {
                component.IsControlled = false;
                component.InitializeHost();
            }

            Logger.Debug("  Removed client object, retaining inactive host object");
            return;
        }

        if (_hasParent) {
            Logger.Debug("  Entity has parent, only setting local transform");

            Object.Host.transform.localPosition = _lastPosition = Object.Client.transform.localPosition;
            Object.Host.transform.localScale = _lastScale = Object.Client.transform.localScale;
        } else {
            Logger.Debug("  Entity has no parent, calculating transform");

            var worldPosition=Object.Client.transform.position;
            Object.Host.transform.position=worldPosition;
            _lastPosition=worldPosition;
            
            // Since the scale of the client object is the entire scale we have and the host object scale can be in a
            // hierarchy, we need to calculate what the new local scale of the host will be to match the client scale
            var clientScale = Object.Client.transform.localScale;
            var hostLocalScale = Object.Host.transform.localScale;
            var hostLossyScale = Object.Host.transform.lossyScale;
            
            var newScaleX = hostLocalScale.x == 0 || hostLossyScale.x == 0
                ? 0f
                : clientScale.x / (hostLossyScale.x / hostLocalScale.x);
            var newScaleY = hostLocalScale.y == 0 || hostLossyScale.y == 0
                ? 0f
                : clientScale.y / (hostLossyScale.y / hostLocalScale.y);
            var newScaleZ = hostLocalScale.z == 0 || hostLossyScale.z == 0
                ? 0f
                : clientScale.z / (hostLossyScale.z / hostLocalScale.z);
        
            Object.Host.transform.localScale = _lastScale = new Vector3(newScaleX, newScaleY, newScaleZ);
        }

        // Make sure that the sprite animator doesn't play the default clip after enabling the object
        if (_animator.Host != null) {
            _animator.Host.playAutomatically = false;
        }

        var clientActive = Object.Client.activeSelf;
        Object.Client.SetActive(false);
        EntityFsmActions.ForgetActions(Object.Client);
        foreach (var combat in _components.Values.OfType<CombatStateComponent>()) combat.PrepareTakeover();
        EntityReferences.DisableDetachedParts(this, true);
        foreach (var fsm in _fsms.Host) {
            if (fsm != null) EntityInitializer.InitializeOwnerReferences(fsm);
        }
        Object.Host.SetActive(clientActive);

        Logger.Debug($"  Set Active of host object to: {clientActive}, disabling client object");

        // We need to set the isKinematic property of rigid bodies to ensure physics work again after enabling
        // the host object. In Hornet 1 this is necessary because another state sets this property normally in the
        // fight. See the "Wake" or "Refight Ready" state of the "Control" FSM on Hornet 1.
        // For the Mantis Lord and City Elevator entity, this should never be disabled, since they are always kinematic.
        var rigidBody = Object.Host.GetComponent<Rigidbody2D>();
        if (rigidBody != null && Type != EntityType.MantisLord && Type != EntityType.CityElevator) {
            Logger.Debug("  Resetting isKinematic of Rigidbody to ensure physics work for host object");
            rigidBody.isKinematic = false;
        }

        _lastIsActive = _hasParent ? Object.Host.activeSelf : Object.Host.activeInHierarchy;
        
        _isControlled = false;

        foreach (var component in _components.Values.Distinct()) {
            component.IsControlled = false;
                component.InitializeHost();
        }

        if (_animator.Client != null) {
            var currentClip = _animator.Client.CurrentClip;
            if (currentClip != null) {
                var clientAnimation = currentClip.name;
                var wrapMode = currentClip.wrapMode;
            
                Logger.Debug($"  Animator and current clip present, updating animation: {clientAnimation}, {wrapMode}");
            
                LateUpdateAnimation(_animator.Host, clientAnimation, wrapMode);   
            }
        }

        Logger.Debug("  Restoring FSMs from snapshots");

        for (var fsmIndex = 0; fsmIndex < _fsms.Host.Count; fsmIndex++) {
            var fsm = _fsms.Host[fsmIndex];
            if (fsm == null) continue;

            Logger.Debug($"    Restoring FSM: {fsm.Fsm.Name}");

            // Force initialize the host FSM, since it might have been disabled before initializing
            EntityInitializer.InitializeFsm(fsm);
            
            var snapshot = _fsmSnapshots[fsmIndex];

            for (var i = 0; i < snapshot.Floats.Length; i++) {
                fsm.FsmVariables.FloatVariables[i].Value = snapshot.Floats[i];
            }
            for (var i = 0; i < snapshot.Ints.Length; i++) {
                fsm.FsmVariables.IntVariables[i].Value = snapshot.Ints[i];
            }
            for (var i = 0; i < snapshot.Bools.Length; i++) {
                fsm.FsmVariables.BoolVariables[i].Value = snapshot.Bools[i];
            }
            for (var i = 0; i < snapshot.Strings.Length; i++) {
                fsm.FsmVariables.StringVariables[i].Value = snapshot.Strings[i];
            }
            for (var i = 0; i < snapshot.Vector2s.Length; i++) {
                fsm.FsmVariables.Vector2Variables[i].Value = snapshot.Vector2s[i];
            }
            for (var i = 0; i < snapshot.Vector3s.Length; i++) {
                fsm.FsmVariables.Vector3Variables[i].Value = snapshot.Vector3s[i];
            }

            for (var i = 0; i < snapshot.Objects.Length; i++) {
                if (snapshot.Objects[i] != null && snapshot.Objects[i] != "@local") {
                    var resolved = EntityReferences.Resolve(snapshot.Objects[i], false);
                    if (resolved != null || snapshot.Objects[i].Length == 0) fsm.FsmVariables.GameObjectVariables[i].Value = resolved;
                }
            }
            // Restore local owner/child bindings after the snapshot overlay.
            // A detached child can still be alive despite no longer being
            // found beneath its former parent during initialization.
            EntityInitializer.InitializeFsm(fsm);

            // A disabled snapshot can retain the last state of a pooled attack.
            // SetState executes OnEnter even on an inactive GameObject: a tween
            // created there has not run Awake/RetrieveArgs, and its next native
            // OnExit cannot stop it. Honour the original disable semantics and
            // leave the next activation to the native FSM instead of starting
            // asynchronous work on a dormant source.
            if (!Object.Host.activeInHierarchy) {
                if (_suspendedRestartFlags.TryGetValue(fsm, out var restart)) fsm.Fsm.RestartOnEnable = restart;
                fsm.Fsm.Stop();
                continue;
            }

            // Before setting the state, we replace the actions of the to-be state to only include the ones that
            // should be executed again (including actions with "everyFrame" on true or that continuously check
            // collisions for example).
            var state = fsm.GetState(snapshot.CurrentState);
            if (state == null) {
                Logger.Debug("  Not setting FSM state, because current state is empty");
                continue;
            }
            
            Logger.Debug($"  Setting FSM state: {snapshot.CurrentState}");
            
            var oldActions = state.Actions;
            var newActions = oldActions.Where(ActionRegistry.IsActionContinuous).ToArray();

            Logger.Debug($"  Only using actions: {string.Join(", ", newActions.Select(a => a.GetType().ToString()))}");

            // Replace the actions, set the state and reset the actions again
            state.Actions = newActions;
            try { fsm.SetState(snapshot.CurrentState); }
            finally { state.Actions = oldActions; }
        }
        RestoreRestartFlags();
    }

    /// <summary>
    /// Updates the position of the client entity.
    /// </summary>
    /// <param name="position">The new position.</param>
    public void UpdatePosition(Vector2 position) {
        if (Object.Client == null || Object.Host == null) {
            Logger.Warn($"Cannot update position for entity ({Id}, {Type}), client or host object is null");
            return;
        }
        
        var unityPos = new Vector3(
            position.X, 
            position.Y,
            Object.Host.transform.position.z
        );

        var positionInterpolation = Object.Client.GetComponent<PositionInterpolation>();
        if (positionInterpolation == null) {
            return;
        }

        if (Object.Client.transform.parent != null) unityPos = Object.Client.transform.parent.InverseTransformPoint(unityPos);
        positionInterpolation.SetNewPosition(unityPos);
    }

    /// <summary>
    /// Updates the scale of the client entity.
    /// </summary>
    /// <param name="scale">The new scale data.</param>
    public void UpdateScale(EntityUpdate.ScaleData scale) {
        if (Object.Client == null) {
            Logger.Warn($"Cannot update scale for entity ({Id}, {Type}), client object is null");
            return;
        }
        
        var transform = Object.Client.transform;
        var localScale = transform.localScale;
        
        if (scale.x) {
            if (scale.xFlipped) {
                var currentScaleX = localScale.x;

                if (currentScaleX > 0 != scale.xPos) {
                    currentScaleX *= -1;

                    localScale.x = currentScaleX;
                }
            } else {
                localScale.x = scale.xScale;
            }
        }
        
        if (scale.y) {
            if (scale.yFlipped) {
                var currentScaleY = localScale.y;

                if (currentScaleY > 0 != scale.yPos) {
                    currentScaleY *= -1;

                    localScale.y = currentScaleY;
                }
            } else {
                localScale.y = scale.yScale;
            }
        }

        if (scale.z) {
            if (scale.zFlipped) {
                var currentScaleZ = localScale.z;

                if (currentScaleZ > 0 != scale.zPos) {
                    currentScaleZ *= -1;

                    localScale.z = currentScaleZ;
                }
            } else {
                localScale.z = scale.zScale;
            }
        }

        transform.localScale = localScale;
    }

    /// <summary>
    /// Updates the animation of the client entity.
    /// </summary>
    /// <param name="animationId">The ID of the animation.</param>
    /// <param name="wrapMode">The wrap mode of the animation clip.</param>
    /// <param name="alreadyInSceneUpdate">Whether this update is when entering a new scene.</param>
    public void UpdateAnimation(
        byte animationId, 
        tk2dSpriteAnimationClip.WrapMode wrapMode, 
        bool alreadyInSceneUpdate
    ) {
        if (Object.Client == null || _animator.Client == null) return;

        if (!_animationClipNameIds.TryGetValue(animationId, out var clipName)) {
            Logger.Warn($"Entity '{Object.Client.name}' received unknown animation ID: {animationId}");
            return;
        }

        Logger.Debug($"Entity '{Object.Client.name}' received animation: {animationId}, {clipName}, {wrapMode}");

        // All paths lead to calling the Play method of the sprite animator that is hooked, so we allow the call
        // through the hook
        _allowClientAnimation = true;

        if (alreadyInSceneUpdate) {
            // Since this is an animation update from an entity that was already present in a scene,
            // we need to determine where to start playing this specific animation
            LateUpdateAnimation(_animator.Client, clipName, wrapMode);
        }

        // Otherwise, default to just playing the clip
        _animator.Client.Play(clipName);
    }

    /// <summary>
    /// Update the animation for the given animator with the given clip name and wrap mode. This assumes that we need
    /// to replicate animation behaviour for a late update.
    /// </summary>
    /// <param name="animator">The sprite animator to update.</param>
    /// <param name="clipName">The name of the animation clip.</param>
    /// <param name="wrapMode">The wrap mode for the animation.</param>
    private void LateUpdateAnimation(
        tk2dSpriteAnimator animator, 
        string clipName, 
        tk2dSpriteAnimationClip.WrapMode wrapMode
    ) {
        if (animator == null) return;
        var clip = animator.GetClipByName(clipName);
        if (clip == null) {
            Logger.Warn($"Entity ({Id}, {Type}) has no animation clip '{clipName}'");
            return;
        }
        if (wrapMode == tk2dSpriteAnimationClip.WrapMode.Loop) {
            animator.Play(clipName);
            return;
        }

        Logger.Debug($"Entity ({Id}, {Type}) LateUpdateAnimation: {clip.name}, {wrapMode}");

        if (wrapMode == tk2dSpriteAnimationClip.WrapMode.LoopSection) {
            // The clip loops in a specific section in the frames, so we start playing
            // it from the start of that section
            animator.PlayFromFrame(clipName, clip.loopStart);
            return;
        }

        if (wrapMode == tk2dSpriteAnimationClip.WrapMode.Once ||
            wrapMode == tk2dSpriteAnimationClip.WrapMode.Single) {
            // Since the clip was played once, it stops on the last frame,
            // so we emulate that by only "playing" the last frame of the clip
            var clipLength = clip.frames.Length;
            animator.PlayFromFrame(clipName, clipLength - 1);

            // Logger.Debug(
            // $"  Played animation: {clipName}, {clipLength - 1} on {_animator.Client.name}, {_animator.Client.GetHashCode()}");
        }
    }

    /// <summary>
    /// Updates whether the game object for the client entity is active.
    /// </summary>
    /// <param name="active">The new value for active.</param>
    public void UpdateIsActive(bool active) {
        if (Object.Client != null) {
            Logger.Debug($"Entity '{Object.Client.name}' received active: {active}");
            Object.Client.SetActive(active);
        } else {
            Logger.Warn($"Entity ({Id}, {Type}) could not update active, because client object is null");
        }
    }

    /// <summary>
    /// Updates generic data for the client entity.
    /// </summary>
    /// <param name="entityNetworkData">A list of data to update the client entity with.</param>
    /// <param name="alreadyInSceneUpdate">Whether this data is from an already in scene update.</param>
    public void UpdateData(List<EntityNetworkData> entityNetworkData, bool alreadyInSceneUpdate) {
        foreach (var data in entityNetworkData) {
            if (data.Type == EntityComponentType.Fsm) {
                PlayMakerFSM fsm;
                byte stateIndex;
                byte actionIndex;

                if (_fsms.Client.Count > 1) {
                    // Do a check on the length of the data
                    if (data.Packet.Length < 3) {
                        continue;
                    }

                    var fsmIndex = data.Packet.ReadByte();
                    if (fsmIndex >= _fsms.Client.Count) continue;
                    fsm = _fsms.Client[fsmIndex];

                    stateIndex = data.Packet.ReadByte();
                    actionIndex = data.Packet.ReadByte();
                } else {
                    // Do a check on the length of the data
                    if (data.Packet.Length < 2) {
                        continue;
                    }

                    fsm = _fsms.Client[0];

                    stateIndex = data.Packet.ReadByte();
                    actionIndex = data.Packet.ReadByte();
                }

                if (fsm == null || stateIndex >= fsm.FsmStates.Length) continue;
                var state = fsm.FsmStates[stateIndex];
                if (actionIndex >= state.Actions.Length) continue;
                var action = state.Actions[actionIndex];
                
                Logger.Debug($"Received entity network data for FSM: {fsm.Fsm.Name}, {state.Name}, {actionIndex} ({action.GetType()})");

                EntityFsmActions.ApplyNetworkDataFromAction(data, action);

                continue;
            }

            if (_components.TryGetValue(data.Type, out var component)) {
                component.Update(data, alreadyInSceneUpdate);
            }
        }
    }

    /// <summary>
    /// Update the FSMs of the host entity to prepare for host transfer or disconnects.
    /// </summary>
    /// <param name="hostFsmData">Dictionary mapping FSM index to data.</param>
    public void UpdateHostFsmData(Dictionary<byte, EntityHostFsmData> hostFsmData) {
        foreach (var fsmPair in hostFsmData) {
            var fsmIndex = fsmPair.Key;
            var data = fsmPair.Value;

            if (_fsms.Host.Count <= fsmIndex) {
                Logger.Warn($"Tried to update host FSM data for unknown FSM index: {fsmIndex}");
                continue;
            }

            var hostFsm = _fsms.Host[fsmIndex];
            if (hostFsm == null) continue;
            var snapshot = _fsmSnapshots[fsmIndex];

            if (data.Types.Contains(EntityHostFsmData.Type.State)) {
                var states = hostFsm.FsmStates;
                if (data.CurrentState == byte.MaxValue) {
                    // Inactive FSMs have no current state.
                    snapshot.CurrentState = string.Empty;
                    if (_fsms.Client[fsmIndex] != null)
                        EntityFsmActions.RegisterStateChange(_fsms.Client[fsmIndex].Fsm, string.Empty);
                } else if (states.Length <= data.CurrentState) {
                    Logger.Warn($"Tried to update host FSM state for unknown state index: {data.CurrentState}");
                } else {
                    var stateName = states[data.CurrentState].Name;
                    
                    snapshot.CurrentState = stateName;
                    
                    // Also propagate this state change to the EntityFsmActions class with the client FSM for the
                    // same index
                    if (_fsms.Client[fsmIndex] != null)
                        EntityFsmActions.RegisterStateChange(_fsms.Client[fsmIndex].Fsm, stateName);
                }
            }

            var fsms = new[] { hostFsm, _fsms.Client[fsmIndex] };

            void CondUpdateVars<TFsm, TBase>(
                EntityHostFsmData.Type type,
                Dictionary<byte, TBase> dataDict,
                TFsm[] fsmVarArray,
                Action<byte, TFsm, TBase> setValueAction
            ) {
                if (data.Types.Contains(type)) {
                    foreach (var pair in dataDict) {
                        if (fsmVarArray.Length <= pair.Key) {
                            Logger.Warn($"Tried to update host FSM var ({typeof(TBase)}) for unknown index: {pair.Key}");
                        } else {
                            setValueAction.Invoke(pair.Key, fsmVarArray[pair.Key], pair.Value);
                        }
                    }
                }
            }

            if (data.Types.Contains(EntityHostFsmData.Type.Objects)) {
                foreach (var pair in data.Objects) {
                    if (pair.Key >= snapshot.Objects.Length || pair.Value.Length > 2048 || pair.Value == "@local") continue;
                    snapshot.Objects[pair.Key] = pair.Value;
                    if (ApplyObjectReference(fsmIndex, pair.Key, pair.Value)) _pendingReferences.Remove((fsmIndex, pair.Key));
                    else if (_pendingReferences.Count < 4096) _pendingReferences[(fsmIndex, pair.Key)] = Time.unscaledTime + 15;
                }
            }
            foreach (var fsm in fsms) {
                if (fsm == null) continue;
                CondUpdateVars(
                    EntityHostFsmData.Type.Floats,
                    data.Floats,
                    fsm.FsmVariables.FloatVariables,
                    (index, fsmVar, value) => {
                        fsmVar.Value = value;
                        snapshot.Floats[index] = value;
                    }
                );
                CondUpdateVars(
                    EntityHostFsmData.Type.Ints,
                    data.Ints,
                    fsm.FsmVariables.IntVariables,
                    (index, fsmVar, value) => {
                        fsmVar.Value = value;
                        snapshot.Ints[index] = value;
                    }
                );
                CondUpdateVars(
                    EntityHostFsmData.Type.Bools,
                    data.Bools,
                    fsm.FsmVariables.BoolVariables,
                    (index, fsmVar, value) => {
                        fsmVar.Value = value;
                        snapshot.Bools[index] = value;
                    }
                );
                CondUpdateVars(
                    EntityHostFsmData.Type.Strings,
                    data.Strings,
                    fsm.FsmVariables.StringVariables,
                    (index, fsmVar, value) => {
                        fsmVar.Value = value;
                        snapshot.Strings[index] = value;
                    }
                );
                CondUpdateVars(
                    EntityHostFsmData.Type.Vector2s,
                    data.Vec2s,
                    fsm.FsmVariables.Vector2Variables,
                    (index, fsmVar, value) => {
                        fsmVar.Value = (UnityEngine.Vector2) value;
                        snapshot.Vector2s[index] = (UnityEngine.Vector2) value;
                    }
                );
                CondUpdateVars(
                    EntityHostFsmData.Type.Vector3s,
                    data.Vec3s,
                    fsm.FsmVariables.Vector3Variables,
                    (index, fsmVar, value) => {
                        fsmVar.Value = (Vector3) value;
                        snapshot.Vector3s[index] = (Vector3) value;
                    }
                );
            }
        }
    }

    /// <summary>
    /// Destroys the entity.
    /// </summary>
    internal void RestoreLocalSimulation() {
        if (destroyed || OwnsHostObject || Object.Host == null) return;
        if (!_isSceneHostDetermined) InitializeHost();
        else if (_isControlled) MakeHost();
    }
    public void Destroy() {
        if (destroyed) return;
        destroyed = true;
        WaitingForEntrance.Remove(this);
        entranceEvents.Clear();
        _pendingReferences.Clear();
        _pausedNativeMotion.Clear();
        RestoreRestartFlags();
        MonoBehaviourUtil.Instance.OnUpdateEvent -= OnUpdate;
        foreach (var fsm in _fsms.Host) if (fsm != null) fsm.Fsm.StateChanged -= OnHostStateChanged;
        Cleanup(() => EntityFsmActions.ForgetActions(Object.Client));
        Cleanup(() => EntityFsmActions.ForgetActions(Object.Host));
        Cleanup(() => EntityHooks.ForgetActivation(Object.Host));
        Cleanup(() => EntityReferences.DisableDetachedParts(this, true, true));
        Cleanup(() => EntityReferences.Forget(this));
        foreach (var type in _hookedTypes) Cleanup(() => FsmActionHooks.RemoveObserver(type, Object.Host, OnActionEntered));
        Cleanup(() => EntityHooks.ForgetAnimator(_animator.Host, _animator.Client));
        Cleanup(() => EntityHooks.ForgetRecycle(Object.Client));
        Cleanup(() => EntityHooks.ForgetRecycle(Object.Host));
        foreach (var component in _components.Values.Distinct()) {
            Cleanup(component.Destroy);
        }
        if (!_hasParent && Object.Client != null) UnityEngine.Object.Destroy(Object.Client);
        if (OwnsHostObject && Object.Host != null) UnityEngine.Object.Destroy(Object.Host);
    }
    private void Cleanup(System.Action operation) {
        try { operation(); }
        catch (Exception e) { Logger.Warn($"Entity cleanup ({Id}, {Type}): {e}"); }
    }
    private void RestoreRestartFlags() {
        foreach (var pair in _suspendedRestartFlags) if (pair.Key != null) pair.Key.Fsm.RestartOnEnable = pair.Value;
        _suspendedRestartFlags.Clear();
    }

    /// <summary>
    /// Get the list of client FSMs.
    /// </summary>
    /// <returns>A list containing the client FSM instances.</returns>
    public List<PlayMakerFSM> GetClientFsms() {
        return _fsms.Client;
    }
}
