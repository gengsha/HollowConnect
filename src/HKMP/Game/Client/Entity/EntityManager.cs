using System;
using System.Collections.Generic;
using System.Linq;
using Hkmp.Game.Client.Entity.Action;
using Hkmp.Game.Client.Entity.Component;
using Hkmp.Networking.Client;
using Hkmp.Networking.Packet.Data;
using Hkmp.Util;
using HutongGames.PlayMaker.Actions;
using Modding;
using UnityEngine;
using UnityEngine.SceneManagement;
using FindGameObject = On.HutongGames.PlayMaker.Actions.FindGameObject;
using Logger = Hkmp.Logging.Logger;
using Object = UnityEngine.Object;

namespace Hkmp.Game.Client.Entity;

/// <summary>
/// Manager class that handles entity creation, updating, networking and destruction.
/// </summary>
internal class EntityManager {
    /// <summary>
    /// The net client for networking.
    /// </summary>
    private readonly NetClient _netClient;
    private readonly BossRoomSynchronizer _bossRoom;

    /// <summary>
    /// Dictionary mapping entity IDs to their respective entity instances.
    /// </summary>
    private readonly Dictionary<ulong, Entity> _entities;

    /// <summary>
    /// Whether the scene host is determined for this scene locally.
    /// </summary>
    public bool IsSceneHostDetermined { get; private set; }

    /// <summary>
    /// Whether the client user is the scene host.
    /// </summary>
    public bool IsSceneHost { get; private set; }

    /// <summary>
    /// Queue of entity updates that have not been applied yet because of a missing entity.
    /// Usually this occurs because the entities are loaded later than the updates are received when the local player
    /// enters a new scene.
    /// </summary>
    private sealed class PendingUpdate {
        public BaseEntityUpdate Update;
        public bool Snapshot;
        public float Deadline;
    }
    private readonly Queue<PendingUpdate> _receivedUpdates;
    private readonly HashSet<ulong> _retiredIds = new();
    private readonly HashSet<ulong> _localWorldIds = new();
    private readonly HashSet<string> _privateBossScenes = new();
    private HashSet<ulong> _bossScope;
    internal bool IsPrivateBossScene(string scene) => CoopContext.LocalWorldOnly && _privateBossScenes.Contains(scene) &&
        !_entities.Values.Any(e => e.SourceScene == scene && BossDefeatSignals.IsBoss(e.Type));
    internal bool HasSharedBoss => _entities.Values.Any(e => BossDefeatSignals.IsBoss(e.Type));
    internal string CaptureBossScope() => Newtonsoft.Json.JsonConvert.SerializeObject(
        _entities.Keys.Concat(_retiredIds).Where(id => (id >> 63) == 0).OrderBy(id => id).ToArray());
    internal void AcceptBossScope(string json) {
        if (!CoopContext.LocalWorldOnly || IsSceneHost || json.Length > 32768) return;
        var ids = Newtonsoft.Json.JsonConvert.DeserializeObject<ulong[]>(json);
        if (ids == null || ids.Length > 2048 || ids.Any(id => id == 0 || (id >> 63) != 0)) return;
        var additions = _bossScope != null && ids.Any(id => !_bossScope.Contains(id));
        _bossScope = new HashSet<ulong>(ids);
        _localWorldIds.ExceptWith(_bossScope);
        // Later phases may add native scene objects after the initial scope.
        // Revisit only when authority adds identities, otherwise a previously
        // private phase would stay outside the shared encounter forever.
        if (additions) for (var i = 0; i < UnityEngine.SceneManagement.SceneManager.sceneCount; i++) {
            var scene = UnityEngine.SceneManagement.SceneManager.GetSceneAt(i);
            if (scene.isLoaded && (scene.name == CoopContext.LocalScene || scene.name.StartsWith(CoopContext.LocalScene + "_", StringComparison.Ordinal)))
                FindEntitiesInScene(scene, true);
        }
        // Different local progression can legitimately select different
        // boss/defeated scenes. Release unmatched native bosses instead of
        // forcing the authority's world layout or leaving them dormant.
        var local = _entities.Values.Where(e => (e.Id >> 63) == 0 && !_bossScope.Contains(e.Id)).ToArray();
        foreach (var entity in local) {
            _localWorldIds.Add(entity.Id);
            if (BossDefeatSignals.IsBoss(entity.Type)) _privateBossScenes.Add(entity.SourceScene);
            entity.RestoreLocalSimulation(unmatched: true);
        }
        foreach (var entity in local) {
            _entities.Remove(entity.Id);
            entity.Destroy();
        }
    }
    private bool hooksRegistered, clearing;
    private int? preparedSceneHandle;

    public EntityManager(NetClient netClient) {
        _netClient = netClient;
        _bossRoom = new BossRoomSynchronizer(netClient, this);
        _entities = new Dictionary<ulong, Entity>();
        _receivedUpdates = new Queue<PendingUpdate>();
    }

    /// <summary>
    /// Initialize the entity manager by initializing the processor and action hooks.
    /// </summary>
    public void Initialize() {
        EntityProcessor.Initialize(_entities, _netClient);
    }

    /// <summary>
    /// Register the hooks for entity-related operations.
    /// </summary>
    public void RegisterHooks() {
        if (hooksRegistered) return;
        hooksRegistered = true;
        LocalStoryScene.Register();
        EntityHooks.Register();
        CombatSpawns.Register();
        BossTargeting.Register();
        SpawnJarComponent.RegisterHooks();
        Entity.LifetimeEnded += RetireLifetime;
        _bossRoom.Register();
        MonoBehaviourUtil.Instance.OnUpdateEvent += RetryPending;
        FsmActionHooks.RegisterHooks();
        MusicComponent.RegisterHooks();
        
        EntityFsmActions.EntitySpawnEvent += OnGameObjectSpawned;
        UnityEngine.SceneManagement.SceneManager.sceneLoaded += OnSceneLoaded;
        UnityEngine.SceneManagement.SceneManager.sceneUnloaded += OnSceneUnloaded;
        
        FindGameObject.Find += OnFindGameObject;
    }

    /// <summary>
    /// Deregister the hooks for entity-related operations.
    /// </summary>
    public void DeregisterHooks() {
        if (!hooksRegistered) return;
        hooksRegistered = false;
        LocalStoryScene.Unregister();
        _bossRoom.Destroy();
        Entity.LifetimeEnded -= RetireLifetime;
        MonoBehaviourUtil.Instance.OnUpdateEvent -= RetryPending;
        FsmActionHooks.DeregisterHooks();
        MusicComponent.DeregisterHooks();
        
        EntityFsmActions.EntitySpawnEvent -= OnGameObjectSpawned;
        UnityEngine.SceneManagement.SceneManager.sceneLoaded -= OnSceneLoaded;
        UnityEngine.SceneManagement.SceneManager.sceneUnloaded -= OnSceneUnloaded;
        
        FindGameObject.Find -= OnFindGameObject;

        ClearEntities();
        preparedSceneHandle = null;
        EntityHooks.Unregister();
        CombatSpawns.Unregister();
        BossTargeting.Unregister();
        SpawnJarComponent.DeregisterHooks();
    }

    /// <summary>
    /// Initializes the entity manager if we are the scene host.
    /// </summary>
    public void InitializeSceneHost(bool applyPending = true) {
        applyingSnapshot = !applyPending;
        Logger.Debug("We are scene host, releasing control of all registered entities");

        IsSceneHost = true;
        IsSceneHostDetermined = true;

        foreach (var entity in _entities.Values.ToArray()) {
            try { entity.InitializeHost(true); } catch (Exception e) { Logger.Error($"Initialize host {entity.Id}: {e}"); }
        }
        // Restore a paused hierarchy only after all independently registered
        // children have reached their final activation state.
        foreach (var entity in _entities.Values.ToArray()) {
            try { entity.CompleteHostInitialization(); } catch (Exception e) { Logger.Error($"Restore paused motion {entity.Id}: {e}"); }
        }
        
        if (applyPending) CheckReceivedUpdates();
    }

    /// <summary>
    /// Initializes the entity manager if we are a scene client.
    /// </summary>
    public void InitializeSceneClient(bool applyPending = true) {
        applyingSnapshot = !applyPending;
        Logger.Debug("We are scene client, taking control of all registered entities");

        IsSceneHost = false;
        IsSceneHostDetermined = true;

        foreach (var entity in _entities.Values.ToArray()) {
            try { entity.InitializeClient(); } catch (Exception e) { Logger.Error($"Initialize replica {entity.Id}: {e}"); }
        }
        
        if (applyPending) CheckReceivedUpdates();
    }

    // Live deltas received during entry are newer than the initial snapshot.
    // Commit the base snapshot first, then replay those deltas in receive order.
    internal void CompleteSceneSnapshot() {
        try { CheckReceivedUpdates(); }
        finally { applyingSnapshot = false; }
    }

    /// <summary>
    /// Updates the entity manager if we become the scene host.
    /// </summary>
    public void BecomeSceneHost() {
        Logger.Debug("Becoming scene host");

        IsSceneHost = true;
        _bossRoom.AuthorityTransferred();

        foreach (var entity in _entities.Values.ToArray()) {
            try { entity.MakeHost(); }
            catch (Exception e) {
                Logger.Error($"Could not take over entity ({entity.Id}, {entity.Type}): {e}");
            }
        }
    }

    private readonly Dictionary<ulong, EntitySpawn> _pendingSpawns = new();
    private readonly Dictionary<ulong, float> _spawnDeadlines = new();
    private static readonly long SpawnFrameBudget = System.Diagnostics.Stopwatch.Frequency / 500;
    private int spawnFrame = -1;
    private long spawnTicks;
    private bool budgetDeferred, applyingSnapshot;
    /// <summary>Reconstruct one authoritative dynamic lifetime.</summary>
    /// <param name="spawn">The authoritative spawn descriptor.</param>
    public void SpawnEntity(EntitySpawn spawn) {
        if (spawn.SceneName != CoopContext.LocalScene || spawn.Visit != CoopContext.LocalVisit || spawn.Epoch != CoopContext.Epoch) return;
        if (_retiredIds.Contains(spawn.Id)) return;
        if (CoopContext.LocalWorldOnly && _localWorldIds.Contains(spawn.SourceId)) return;
        if (!IsSceneHostDetermined) { DeferSpawn(spawn); return; }
        if (_entities.TryGetValue(spawn.Id, out var existing)) {
            if (existing.Type != spawn.SpawnedType) Logger.Error($"Spawn identity/type mismatch: {spawn.Id}");
            _pendingSpawns.Remove(spawn.Id); _spawnDeadlines.Remove(spawn.Id);
            return;
        }
        if (!_entities.TryGetValue(spawn.SourceId, out var source)) {
            DeferSpawn(spawn);
            return;
        }
        if (spawnFrame != Time.frameCount) { spawnFrame = Time.frameCount; spawnTicks = 0; }
        if (!applyingSnapshot && spawnTicks >= SpawnFrameBudget) {
            budgetDeferred = true; DeferSpawn(spawn, false); return;
        }
        var started = System.Diagnostics.Stopwatch.GetTimestamp();
        try {
            var obj = SpawnDescriptor.Create(spawn, source);
            if (obj == null) obj = EntitySpawner.SpawnEntityGameObject(spawn.SpawningType, spawn.SpawnedType, source.Object.Client, source.GetClientFsms());
            if (obj == null) {
                DeferSpawn(spawn);
                return;
            }
            obj.transform.position = new Vector3(spawn.X, spawn.Y, spawn.Z);
            var processor = new EntityProcessor { GameObject = obj, IsSceneHost = IsSceneHost,
                IsSceneHostDetermined = IsSceneHostDetermined, LateLoad = false, SpawnedId = spawn.Id, AllowHazard = true,
                BossCombatSpawn = true, BossTargetScope = source.BossTargetScope, BossTargetSource = source }.Process();
            if (!processor.Success) { Logger.Warn($"Spawn registration failed: {spawn.Id}, {spawn.SpawnedType}"); UnityEngine.Object.Destroy(obj); }
            else {processor.Entities[0].OwnsHostObject=true;foreach(var e in processor.Entities){e.InitializeClient();e.UpdateIsActive(spawn.Active);}SpawnDescriptor.Bind(spawn, source, processor.Entities[0]);}
            _pendingSpawns.Remove(spawn.Id); _spawnDeadlines.Remove(spawn.Id);
        } finally { spawnTicks += System.Diagnostics.Stopwatch.GetTimestamp() - started; }
    }
    private void DeferSpawn(EntitySpawn spawn, bool warn = true) {
        if (_pendingSpawns.Count >= 1024) return;
        if (!_spawnDeadlines.ContainsKey(spawn.Id)) {
            _spawnDeadlines[spawn.Id] = Time.unscaledTime + 15;
            if (warn) Logger.Warn($"Spawn deferred: {spawn.Id}, source {spawn.SourceId}, {spawn.Fsm}/{spawn.State}/{spawn.ActionIndex}, {spawn.SpawnedType}");
        }
        _pendingSpawns[spawn.Id] = spawn;
    }

    /// <summary>
    /// Method for handling received entity updates.
    /// </summary>
    /// <param name="entityUpdate">The entity update to handle.</param>
    /// <param name="alreadyInSceneUpdate">Whether this is the update from the already in scene packet.</param>
    public bool HandleEntityUpdate(EntityUpdate entityUpdate, bool alreadyInSceneUpdate = false) {
        if (entityUpdate.SceneName != CoopContext.LocalScene || entityUpdate.Visit != CoopContext.LocalVisit || entityUpdate.Epoch != CoopContext.Epoch || CoopContext.Resetting) return true;
        // ID 0 carries reliable room data only. Snapshots may also include an
        // empty position record for it; there is no Unity entity to wait for.
        if (entityUpdate.Id == 0) return true;
        if (_localWorldIds.Contains(entityUpdate.Id)) return true;
        if (_retiredIds.Contains(entityUpdate.Id)) return true;
        if (IsSceneHost) {
            return true;
        }

        if (!_entities.TryGetValue(entityUpdate.Id, out var entity) || !IsSceneHostDetermined) {
            if (IsSceneHostDetermined) {
                Logger.Debug($"Could not find entity ({entityUpdate.Id}) to apply update for; storing update for now");
            } else {
                Logger.Debug("Scene host is not determined yet to apply update; storing update for now");
            }

            QueueUpdate(entityUpdate, alreadyInSceneUpdate);
            
            return false;
        }

        if (entityUpdate.UpdateTypes.Contains(EntityUpdateType.Position)) {
            entity.UpdatePosition(entityUpdate.Position, alreadyInSceneUpdate);
        }

        if (entityUpdate.UpdateTypes.Contains(EntityUpdateType.Scale)) {
            entity.UpdateScale(entityUpdate.Scale);
        }
            
        if (entityUpdate.UpdateTypes.Contains(EntityUpdateType.Animation)) {
            entity.UpdateAnimation(
                entityUpdate.AnimationId, 
                (tk2dSpriteAnimationClip.WrapMode) entityUpdate.AnimationWrapMode,
                alreadyInSceneUpdate
            );
        }

        return true;
    }

    /// <summary>
    /// Method for handling received reliable entity updates.
    /// </summary>
    /// <param name="entityUpdate">The reliable entity update to handle.</param>
    /// <param name="alreadyInSceneUpdate">Whether this is the update from the already in scene packet.</param>
    public bool HandleReliableEntityUpdate(ReliableEntityUpdate entityUpdate, bool alreadyInSceneUpdate = false) {
        if (entityUpdate.SceneName != CoopContext.LocalScene || entityUpdate.Visit != CoopContext.LocalVisit || entityUpdate.Epoch != CoopContext.Epoch || CoopContext.Resetting) return true;
        if (!IsSceneHostDetermined) { QueueUpdate(entityUpdate, alreadyInSceneUpdate); return false; }
        if (entityUpdate.Id == 0) {
            foreach (var data in entityUpdate.GenericData) {
                if (data.Type == EntityComponentType.Music && !CoopContext.LocalWorldOnly) MusicComponent.ReceiveRoom(data, alreadyInSceneUpdate);
                if (data.Type == EntityComponentType.BossSceneComplete || data.Type == EntityComponentType.BossSceneContext || data.Type == EntityComponentType.SceneLayout || data.Type == EntityComponentType.EnemyRoster || data.Type == EntityComponentType.DreamWarrior || data.Type == EntityComponentType.BossCombatScope)
                    _bossRoom.Receive(data);
            }
            return true;
        }
        if (_localWorldIds.Contains(entityUpdate.Id)) return true;
        if (entityUpdate.GenericData.Any(d => d.Type == EntityComponentType.Retired)) {
            _retiredIds.Add(entityUpdate.Id);
            if (_entities.TryGetValue(entityUpdate.Id, out var retired)) {
                retired.Destroy(); _entities.Remove(retired.Id);
                if (retired.Object.Client != null) UnityEngine.Object.Destroy(retired.Object.Client);
                if (!IsSceneHost && retired.Object.Host != null) UnityEngine.Object.Destroy(retired.Object.Host);
            }
            _pendingSpawns.Remove(entityUpdate.Id); _spawnDeadlines.Remove(entityUpdate.Id);
            return true;
        }
        if (_retiredIds.Contains(entityUpdate.Id)) return true;
        if (!_entities.TryGetValue(entityUpdate.Id, out var entity) || !IsSceneHostDetermined) {
            if (alreadyInSceneUpdate && !IsSceneHost && entityUpdate.UpdateTypes.Contains(EntityUpdateType.HostFsm))
                BossGateCompletion.ObserveMissingSnapshot(entityUpdate.Id, entityUpdate.HostFsmData);
            if (IsSceneHostDetermined) {
                Logger.Debug($"Could not find entity ({entityUpdate.Id}) to apply update for; storing update for now");
            } else {
                Logger.Debug("Scene host is not determined yet to apply update; storing update for now");
            }
            
            QueueUpdate(entityUpdate, alreadyInSceneUpdate);

            return false;
        }

        // Check if we are not the scene host for processing this data, active state and host FSM data should only
        // be applied if we not the scene host, while data type below should always be applied
        if (!IsSceneHost) {
            if (entityUpdate.UpdateTypes.Contains(EntityUpdateType.Active)) {
                entity.UpdateIsActive(entityUpdate.IsActive);
            }
            
            if (entityUpdate.UpdateTypes.Contains(EntityUpdateType.HostFsm)) {
                entity.UpdateHostFsmData(entityUpdate.HostFsmData);
            }
        }

        if (entityUpdate.UpdateTypes.Contains(EntityUpdateType.Data)) {
            entity.UpdateData(entityUpdate.GenericData, alreadyInSceneUpdate);
        }

        return true;
    }

    /// <summary>
    /// Callback method for when a game object is spawned from an existing entity.
    /// </summary>
    /// <param name="details">The entity spawn details containing how the entity was spawned.</param>
    /// <returns>Whether an entity was registered from this spawn.</returns>
    private bool OnGameObjectSpawned(EntitySpawnDetails details) {
        // Unity compares destroyed objects equal to null. Do not mistake a
        // missing spawn result for an already registered, destroyed entity.
        if (clearing || details.GameObject == null) return false;
        var sourceObject = details.Source ?? details.Action?.Fsm?.GameObject;
        var source = EntityReferences.Owner(sourceObject);
        if (!IsSceneHost) {
            // Native activation can spawn and detach a hazard before scene
            // authority is known (Markoth's first shield). Pausing its boss
            // later cannot stop that detached copy. Preserve managed replicas.
            if (source != null && !source.IsNativeAuthority && EntityReferences.Owner(details.GameObject) == null)
                details.GameObject.SetActive(false);
            return false;
        }
        if (source==null && details.Prefab!=null && !CoopContext.LocalWorldOnly)
            source=_entities.Values.FirstOrDefault(e=>e.Object.Host!=null&&e.Object.Host.scene.name==UnityEngine.SceneManagement.SceneManager.GetActiveScene().name&&e.Object.Host.GetComponent<HealthManager>()!=null);
        if (source == null) return false;
        var old = EntityReferences.Owner(details.GameObject);
        // Several native/FSM hooks may report a single spawn. Recycling removes
        // the old lifetime before pool reuse, so a live root is a duplicate.
        if (old!=null && old.Object.Host==details.GameObject) return true;
        var id = EntityIdentity.Dynamic();
        var position = details.GameObject.transform.position;
        var processor = new EntityProcessor { GameObject = details.GameObject, IsSceneHost = true,
            IsSceneHostDetermined = IsSceneHostDetermined, LateLoad = true, SpawnedId = id, AllowHazard = true,
            BossCombatSpawn = true, BossTargetScope = source.BossTargetScope, BossTargetSource = source }.Process();
        if (!processor.Success) return false;
        var top = processor.Entities[0];
        if (details.AttackTargetReceipt != null && top.BossTargetReceipt != null) {
            top.BossTargetReceipt.Value = details.AttackTargetReceipt;
            top.InheritedBossTarget = true;
        }
        var action = details.Action;
        var spawn = new EntitySpawn { Id = top.Id, SourceId = source.Id, SpawningType = source.Type, SpawnedType = top.Type,
            Fsm = action?.Fsm?.Name ?? "", State = action?.State?.Name ?? "",
            FsmPath=action?.Fsm?.GameObject!=null&&action.Fsm.GameObject.transform.IsChildOf(source.Object.Host.transform)?EntityIdentity.Path(action.Fsm.GameObject.transform,source.Object.Host.transform):"",
            ActionIndex = action == null ? -1 : Array.IndexOf(action.State.Actions, action), Prefab = details.Prefab != null ? details.Prefab.name : SpawnDescriptor.PrefabName(action, top.Type),
            X = position.x, Y = position.y, Z = position.z, Active=details.GameObject.activeInHierarchy };
        _netClient.UpdateManager.SetEntitySpawn(spawn);
        return true;
    }

    /// <summary>
    /// Check to see if there are received un-applied entity updates.
    /// </summary>
    private void QueueUpdate(BaseEntityUpdate update, bool snapshot) {
        if (_receivedUpdates.Count >= 4096) _receivedUpdates.Dequeue();
        _receivedUpdates.Enqueue(new PendingUpdate { Update = update, Snapshot = snapshot, Deadline = Time.unscaledTime + 15 });
    }
    private void RetireLifetime(Entity root) {
        if (!IsSceneHost || !_entities.ContainsKey(root.Id)) return;
        var host = root.Object.Host;
        var members = _entities.Values.Where(e => e == root || (host != null && e.Object.Host != null && e.Object.Host.transform.IsChildOf(host.transform))).ToArray();
        foreach (var e in members) {
            _netClient.UpdateManager.AddEntityData(e.Id, new EntityNetworkData { Type = EntityComponentType.Retired });
            _retiredIds.Add(e.Id); e.Destroy(); _entities.Remove(e.Id);
        }
        if (root.Object.Client != null) UnityEngine.Object.Destroy(root.Object.Client);
    }
    private float nextRetry;
    private void RetryPending() {
        if (!IsSceneHostDetermined) return;
        // Native spawns must be observed each frame; batching them every
        // 100 ms delayed attacks and rebuilt a whole burst in a single frame.
        CombatSpawns.Flush();
        if (Time.unscaledTime < nextRetry && !budgetDeferred) return;
        var retryReferences = Time.unscaledTime >= nextRetry;
        if (retryReferences) nextRetry = Time.unscaledTime + .1f;
        budgetDeferred = false;
        if (_pendingSpawns.Count != 0 || _receivedUpdates.Count != 0) CheckReceivedUpdates();
        if (retryReferences) foreach (var entity in _entities.Values.ToArray()) entity.RetryReferences();
    }

    private void CheckReceivedUpdates() {
        if (!IsSceneHostDetermined) return;
        foreach (var spawn in _pendingSpawns.Values.ToArray()) {
            if (Time.unscaledTime > _spawnDeadlines[spawn.Id]) {
                _pendingSpawns.Remove(spawn.Id); _spawnDeadlines.Remove(spawn.Id);
                if (!CoopContext.LocalWorldOnly) Logger.Error($"Spawn expired without a matching source/prefab: {spawn.Id}, {spawn.SpawnedType}");
                continue;
            }
            if (!_entities.ContainsKey(spawn.SourceId)) continue;
            _pendingSpawns.Remove(spawn.Id);
            try { SpawnEntity(spawn); } catch (Exception e) { Logger.Error($"Deferred spawn {spawn.Id}: {e}"); }
        }
        // One bounded pass: a missing dynamic enemy must not freeze Unity's main thread.
        // A late-loaded entity can have both its base snapshot and newer live
        // deltas waiting here. Preserve the same base-before-live ordering.
        var pendingUpdates = _receivedUpdates.OrderByDescending(p => p.Snapshot).ToArray();
        _receivedUpdates.Clear();
        foreach (var pending in pendingUpdates) {
            var update = pending.Update;
            if (_retiredIds.Contains(update.Id) || _localWorldIds.Contains(update.Id) || Time.unscaledTime > pending.Deadline) continue;
            if (update.Id != 0 && !_entities.ContainsKey(update.Id)) {
                if (_receivedUpdates.Count < 4096) _receivedUpdates.Enqueue(pending);
                continue;
            }
            if (update is EntityUpdate position) HandleEntityUpdate(position, pending.Snapshot);
            else if (update is ReliableEntityUpdate state) HandleReliableEntityUpdate(state, pending.Snapshot);
        }
    }

    /// <summary>
    /// Callback method for when the scene changes. Will clear existing entities and start checking for
    /// new entities.
    /// </summary>
    /// <param name="oldScene">The old scene.</param>
    /// <param name="newScene">The new scene.</param>
    private void OnSceneChanged(Scene oldScene, Scene newScene) {
        if (preparedSceneHandle == newScene.handle) return;
        preparedSceneHandle = newScene.handle;
        CoopContext.EnterScene(newScene.name);
        Logger.Debug("Scene changed, clearing registered entities");
            
        ClearEntities(true);

        if (!_netClient.IsConnected) {
            return;
        }

        IsSceneHostDetermined = false;

        FindEntitiesInScene(newScene, false);
        Logger.Info($"Room prepared: {newScene.name}, visit={CoopContext.LocalVisit}, epoch={CoopContext.Epoch}, entities={_entities.Count}");
        
        // Since we have tried finding entities in the scene, we also check whether there are un-applied updates for
        // those entities
        CheckReceivedUpdates();
    }

    // A connection made after native scene loading has finished still needs
    // registration and a fresh visit before publishing its entry.
    internal void PrepareCurrentScene(Scene scene) => OnSceneChanged(default, scene);

    /// <summary>
    /// Clears all the registered entities, and resets static components.
    /// </summary>
    private void ClearEntities(bool sceneChanged=false) {
        clearing = true;
        var previous = _entities.Values.ToArray();
        // Disconnect can leave the native scene loaded. Return its latest
        // state to local play before removing replicas, so a reconnect cannot
        // register dormant native bosses as permanently inactive.
        if (!sceneChanged) foreach (var entity in previous) {
            try { entity.RestoreLocalSimulation(); }
            catch (Exception e) { Logger.Warn($"Local simulation restore ({entity.Id}): {e}"); }
        }
        IsSceneHostDetermined = IsSceneHost = false;
        // Retire the whole room before disposing any Unity object. Cleanup
        // callbacks cannot re-register old lifetimes into the new visit.
        _entities.Clear();
        _receivedUpdates.Clear();
        _pendingSpawns.Clear();
        _spawnDeadlines.Clear();
        budgetDeferred = applyingSnapshot = false; spawnFrame = -1; spawnTicks = 0;
        _retiredIds.Clear();
        _localWorldIds.Clear();
        _privateBossScenes.Clear();
        _bossScope = null;
        _bossRoom.Clear(sceneChanged);
        foreach (var entity in previous) {
            try { entity.Destroy(); }
            catch (Exception e) { Logger.Warn($"Room cleanup ({entity.Id}, {entity.Type}): {e}"); }
            // Global-pool projectiles live in DontDestroyOnLoad. Return them
            // before the next room can mistake them for fresh local attacks.
            if(sceneChanged && (entity.Id>>63)!=0 && !entity.OwnsHostObject && entity.Object.Host!=null && entity.Object.Host.scene.name=="DontDestroyOnLoad") {
                try {global::ObjectPool.Recycle(entity.Object.Host);}catch(Exception e){Logger.Warn("Old-room projectile cleanup: "+e);}
            }
        }

        // A native boss may destroy its health object before entity teardown.
        // Damage registrations belong to this room visit, including retired bodies.
        BossDamageScaling.Clear();
        // Clear the list of entities and the queue of received updates that have not been applied yet
        EntityFsmActions.ClearActions();
        EntityReferences.Clear();
        SpawnDescriptor.Clear();
        
        MusicComponent.ClearInstance();
        clearing = false;
    }

    /// <summary>
    /// Callback method for when a scene is loaded.
    /// </summary>
    /// <param name="scene">The scene that is loaded.</param>
    /// <param name="mode">The load scene mode.</param>
    private void OnSceneLoaded(Scene scene, LoadSceneMode mode) {
        SpawnDescriptor.Clear();
        if (!_netClient.IsConnected || clearing) return;
        var currentSceneName = UnityEngine.SceneManagement.SceneManager.GetActiveScene().name;
        // If this scene is a boss or boss-defeated scene it starts with the same name, so we skip all other
        // loaded scenes
        if (!scene.name.StartsWith(currentSceneName) || scene.name.Equals(currentSceneName)) {
            return;
        }

        Logger.Debug($"Additional scene loaded ({scene.name}), looking for entities");

        FindEntitiesInScene(scene, true);

        // Since we have tried finding entities in the scene, we also check whether there are un-applied updates for
        // those entities
        CheckReceivedUpdates();
    }

    /// <summary>
    /// Find entities to register in the given scene.
    /// </summary>
    private void OnSceneUnloaded(Scene scene) {
        foreach (var entity in _entities.Values.Where(e => e.SourceScene == scene.name).ToArray()) {
            _entities.Remove(entity.Id);
            try { entity.Destroy(); }
            catch (Exception e) { Logger.Warn($"Unloaded room cleanup ({entity.Id}): {e}"); }
            if (entity.Object.Client != null) UnityEngine.Object.Destroy(entity.Object.Client);
        }
    }
    private static string StablePath(Transform t) {
        var path = t.name + "#" + t.GetSiblingIndex().ToString("D5");
        while (t.parent != null) { t = t.parent; path = t.name + "#" + t.GetSiblingIndex().ToString("D5") + "/" + path; }
        return path;
    }

    private void FindEntitiesInScene(Scene scene, bool lateLoad) {
        if (LocalStoryScene.Contains(scene.name)) return;
        // Find all EnemyDeathEffects components
        var objectsToCheck = Object.FindObjectsOfType<EnemyDeathEffects>()
            // Filter out EnemyDeathEffects components not in the current scene
            .Where(e => e.gameObject.scene == scene)
            // Project each death effect to their GameObject and the corpse of the pre-instantiated EnemyDeathEffects
            // component
            .SelectMany(enemyDeathEffects => {
                if (CoopContext.LocalWorldOnly && (!EntityRegistry.TryGetEntry(enemyDeathEffects.gameObject, out var entry) ||
                    !LocalWorldIsolation.Allows(entry))) return new[] { enemyDeathEffects.gameObject };
                try {
                    enemyDeathEffects.PreInstantiate();
                } catch (Exception) {
                    // If we get an exception it might not be possible to pre-instantiate the enemy death effects
                    // This can happen when the object uses a PersonalObjectPool, which can't be pre-instantiated
                    // this early, so we return only the original gameobject
                    return new[] { enemyDeathEffects.gameObject };
                }

                var corpse = ReflectionHelper.GetField<EnemyDeathEffects, GameObject>(
                    enemyDeathEffects, 
                    "corpse"
                );

                return new[] { enemyDeathEffects.gameObject, corpse };
            })
            // Concatenate all GameObjects for PlayMakerFSM components in the current scene, and check whether it is the
            // FSM for a Colosseum Cage, in which case we pre-instantiate the enemy inside and concatenate it as well
            .Concat(Object.FindObjectsOfType<PlayMakerFSM>(true)
                .Where(fsm => fsm.gameObject.scene == scene)
                .SelectMany(fsm => {
                    if (CoopContext.LocalWorldOnly) return new[] { fsm.gameObject };
                    if (!fsm.name.StartsWith("Colosseum Cage Small") &&
                        !fsm.name.StartsWith("Colosseum Cage Large") &&
                        !fsm.name.StartsWith("Colosseum Cage Zote")) {
                        return new[] { fsm.gameObject };
                    }
                
                    if (!fsm.Fsm.Name.Equals("Spawn") &&
                        !fsm.Fsm.Name.Equals("Control")
                    ) {
                        return new[] { fsm.gameObject };
                    }
                    
                    var createAction = fsm.GetFirstAction<CreateObject>("Init");
                    if (createAction == null) return new[] { fsm.gameObject };
                    EntityFsmActions.ApplyNetworkDataFromAction(null, createAction);

                    createAction.Enabled = false;

                    var createdObject = createAction.storeObject.Value;
                    if (createdObject == null) {
                        return new[] { fsm.gameObject };
                    }

                    var fsmTransform = fsm.gameObject.transform;
                    
                    createdObject.transform.position = fsmTransform.position;
                    createdObject.transform.rotation = Quaternion.Euler(fsmTransform.eulerAngles);
                    createdObject.SetActive(false);

                    return new[] { fsm.gameObject, createdObject };
                })
            )
            // Project each GameObject into its children including itself
            .SelectMany(obj => obj == null ? Array.Empty<GameObject>() : obj.GetChildren().Prepend(obj))
            // Concatenate all GameObjects for Climber components (Tiktiks)
            .Concat(Object.FindObjectsOfType<Climber>(true).Select(climber => climber.gameObject))
            .Concat(Object.FindObjectsOfType<DamageHero>(true).Select(d => d.gameObject))
            // Concatenate all GameObjects for Walker components (Amblooms)
            .Concat(Object.FindObjectsOfType<Walker>(true).Select(walker => walker.gameObject))
            // Concatenate all GameObjects for BigCentipede components (Garpedes)
            .Concat(Object.FindObjectsOfType<BigCentipede>(true).Select(centipede => centipede.gameObject))
            // Concatenate all GameObjects for CameraLockArea components
            .Concat(Object.FindObjectsOfType<CameraLockArea>(true).Select(cameraLockArea => cameraLockArea.gameObject))
            // Concatenate all GameObjects for DreamPlatform components
            .Concat(Object.FindObjectsOfType<DreamPlatform>(true).Select(dreamPlatform => dreamPlatform.gameObject))
            // Filter out GameObjects not in the current scene
            .Where(obj => obj.scene == scene)
            .Distinct()
            .OrderBy(obj => StablePath(obj.transform), StringComparer.Ordinal).ToArray();

        foreach (var obj in objectsToCheck) {
            if (CoopContext.LocalWorldOnly && !IsSceneHost && _bossScope != null && EntityRegistry.TryGetEntry(obj, out var scopedEntry) &&
                !_bossScope.Contains(EntityIdentity.Static(obj, scopedEntry.Type))) {
                if (BossDefeatSignals.IsBoss(scopedEntry.Type)) _privateBossScenes.Add(obj.scene.name);
                continue;
            }
            try {
                new EntityProcessor { GameObject = obj, IsSceneHost = IsSceneHost,
                    IsSceneHostDetermined = IsSceneHostDetermined, LateLoad = lateLoad }.Process();
            } catch (Exception e) { Logger.Error($"Scene entity registration failed: {scene.name}/{obj.name}: {e}"); }
        }
        ColosseumEntrance.Prepare(scene);
    }

    /// <summary>
    /// Callback method for when the find method of FindGameObject is called. This is to look for objects that are
    /// normally not found by the action due to our entity system making certain objects inactive. If we notice that
    /// the find failed, but the name to look for was one of our host objects in the entity system, we set that object
    /// instead.
    /// </summary>
    private void OnFindGameObject(FindGameObject.orig_Find orig, HutongGames.PlayMaker.Actions.FindGameObject self) {
        orig(self);

        // If the object was found after the method executed, we skip
        if (self.store.Value != null) {
            return;
        }
        
        // Check for specific instances where we don't want to manually find the entity, because it messes
        // with the logic of the FSM
        if (self.State.Name.Equals("Can Roller?") && self.Fsm.Name.Equals("Blocker Control")) {
            return;
        }
        
        Logger.Debug($"OnFindGameObject, find failed: looking for '{self.objectName.Value}'");
        
        // If the object to find is tagged we skip, since this doesn't happen in our case
        if (self.withTag.Value != "Untagged") {
            return;
        }

        // Check if the name we are looking for is one of our registered entity's host objects
        foreach (var entity in _entities.Values) {
            var obj = entity.Object.Host;
            if (obj != null && obj.name == self.objectName.Value) {
                // The host object of the entity matches the name the action was looking for, so we set the variable
                self.store.Value = obj;
                
                Logger.Debug($"  Name matches host object of entity: ({entity.Id}, {entity.Type})");
                return;
            }
        }
        
        Logger.Debug("  Name did not match any entity");
    }
}
