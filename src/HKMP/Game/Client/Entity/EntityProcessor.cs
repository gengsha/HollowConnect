using System;
using System.Collections.Generic;
using System.Linq;
using Hkmp.Game.Client.Entity.Component;
using Hkmp.Networking.Client;
using Hkmp.Util;
using UnityEngine;
using Logger = Hkmp.Logging.Logger;

namespace Hkmp.Game.Client.Entity; 

/// <summary>
/// Data processing class that receives a set of parameters and processing a given game object into an entity if
/// applicable. Will recursively process children of the game object as well and report success and the list of
/// entities created.
/// </summary>
internal class EntityProcessor {
    /// <summary>
    /// Reference to the dictionary of entities from the entity manager.
    /// </summary>
    private static Dictionary<ulong, Entity> _entities;
    /// <summary>
    /// The net client used to pass onto constructed entities.
    /// </summary>
    private static NetClient _netClient;

    /// <summary>
    /// The last used entity ID.
    /// </summary>

    
    /// <summary>
    /// The game object to process.
    /// </summary>
    public GameObject GameObject { get; init; }
    /// <summary>
    /// Whether the local client is the scene host.
    /// </summary>
    public bool IsSceneHost { get; init; }
    /// <summary>
    /// Whether the scene host is determined for this scene locally.
    /// </summary>
    public bool IsSceneHostDetermined { get; init; }
    /// <summary>
    /// Whether the processing of this entity should happen under the assumption that was a late load of the
    /// game object.
    /// </summary>
    public bool LateLoad { get; init; }
    /// <summary>
    /// Whether the game object was spawned and should have the designated ID.
    /// </summary>
    public ulong? SpawnedId { get; init; }
    public bool AllowHazard { get; init; }
    public bool BossCombatSpawn { get; init; }
    public bool BossTargetScope { get; init; }
    public Entity BossTargetSource { get; init; }

    /// <summary>
    /// The list of entities that were created during the processing.
    /// </summary>
    public List<Entity> Entities { get; } = new();
    /// <summary>
    /// Whether the processing of the entity was a success.
    /// </summary>
    public bool Success => Entities.Count > 0;

    /// <summary>
    /// Initialize the entity processor with a reference to the entity dict and the net client.
    /// </summary>
    /// <param name="entities">A reference to the dictionary of entities from the entity manager.</param>
    /// <param name="netClient">The net client instance to pass onto constructed entities.</param>
    public static void Initialize(Dictionary<ulong, Entity> entities, NetClient netClient) {
        _entities = entities;
        EntityReferences.Entities = entities;
        _netClient = netClient;


    }

    internal static bool ShouldNetwork(EntityRegistryEntry entry) => LocalWorldIsolation.Allows(entry);

    /// <summary>
    /// Process the game object set in this instance with the parameter set in this instance.
    /// </summary>
    /// <returns>The instance of this class for convenience.</returns>
    public EntityProcessor Process() {
        if (GameObject != null && LocalStoryScene.Contains(GameObject.scene.name)) return this;
        Process(GameObject);
        return this;
    }

    /// <summary>
    /// Process the given game object to (potentially) become an entity. Check for child objects as well and will
    /// recursively process those.
    /// </summary>
    /// <param name="gameObject">The game object to process.</param>
    /// <param name="entries">Entity registry entries to check against for the entity or null to use all
    /// top-level entries.</param>
    /// <param name="parentHostObject">The corresponding local authority parent.</param>
    /// <param name="parentClientObject">The client object of the parent entity for this entity or null if no such
    /// parent exists.</param>
    private void Process(
        GameObject gameObject, 
        IEnumerable<EntityRegistryEntry> entries = null,
        GameObject parentClientObject = null, GameObject parentHostObject = null
    ) {
        if (LocalStoryScene.IsTraversalPlatform(gameObject)) return;
        EntityRegistryEntry foundEntry;

        // If the given entries are null, query the entity registry for all top-level entries
        // Otherwise only use the given entries (used for child entities)
        if (entries == null) {
            if (!(SpawnedId.HasValue ? EntityRegistry.TryGetSpawnEntry(gameObject, out foundEntry) : EntityRegistry.TryGetEntry(gameObject, out foundEntry))) {
                if (!AllowHazard || !EntityRegistry.IsHazard(gameObject)) return;
                foundEntry = EntityRegistry.Transient(gameObject);
            }
        } else {
            if (!EntityRegistry.TryGetEntry(entries, gameObject, out foundEntry)) {
                return;
            }
        }

        if (!LocalWorldIsolation.Allows(foundEntry, BossCombatSpawn || parentHostObject != null)) {
            Logger.Debug($"Leaving local-trigger entity '{gameObject.name}' under native scene control");
            return;
        }

        if (_entities.Values.Any(e => e.Object.Host == gameObject || e.Object.Client == gameObject)) return;
        var id = SpawnedId.HasValue
            ? (parentHostObject == null ? SpawnedId.Value : EntityIdentity.Hash(SpawnedId.Value + "/" + EntityIdentity.Path(gameObject.transform, GameObject.transform) + ":" + foundEntry.Type) | 0x8000000000000000UL)
            : EntityIdentity.Static(gameObject, foundEntry.Type);
        if (_entities.ContainsKey(id)) {
            Logger.Error($"Entity identity collision or incompatible spawn: {id}, {foundEntry.Type}, {gameObject.name}");
            return;
        }

        // Get the array of component types for the entity or create an empty one if it is null
        var componentTypes = foundEntry.ComponentTypes ?? Array.Empty<EntityComponentType>();
        
        // Depending on whether a parent object was given we create the entity with this parent object
        Entity entity;
        var targetSource = EntityReferences.Owner(parentHostObject) ?? BossTargetSource;
        var targetScope = BossTargetScope || BossDefeatSignals.IsBoss(foundEntry.Type) || targetSource?.BossTargetScope == true;
        if (parentClientObject == null) {
            Logger.Debug($"Registering entity ({foundEntry.Type}) '{gameObject.name}' with ID '{id}'");

            entity = new Entity(
                _netClient,
                id,
                foundEntry.Type,
                gameObject,
                preserveHostActivation: SpawnedId.HasValue && LateLoad && IsSceneHostDetermined && IsSceneHost,
                bossTargetScope: targetScope, bossTargetSource: targetSource,
                types: componentTypes
            );
        } else {
            Logger.Debug($"Registering entity ({foundEntry.Type}) '{gameObject.name}' with ID '{id}' with parent: {parentClientObject.name}");
            
            // Find the correct child of the client object of the parent entity
            var clientObject = parentClientObject.GetChildren()
                .FirstOrDefault(c => {
                    if (Entities.Any(processedEntity => processedEntity.Object.Client == c)) {
                        return false;
                    }

                    return EntityIdentity.Path(c.transform, parentClientObject.transform) == EntityIdentity.Path(gameObject.transform, parentHostObject.transform);
                });
            if (clientObject == null) {
                Logger.Warn("Could not find child of client object of parent entity");
                return;
            }
            
            Logger.Debug($"Found child of client object of parent entity: {clientObject.name}, {clientObject.GetInstanceID()}");

            entity = new Entity(
                _netClient,
                id,
                foundEntry.Type,
                gameObject,
                clientObject,
                preserveHostActivation: SpawnedId.HasValue && LateLoad && IsSceneHostDetermined && IsSceneHost,
                bossTargetScope: targetScope, bossTargetSource: targetSource,
                types: componentTypes
            );
        }

        _entities[id] = entity;
        EntityReferences.Register(entity);

        Entities.Add(entity);

        // If this entry has child entries, we recursively check the children of the object as well
        if (foundEntry.Children != null) {
            foreach (var childObj in gameObject.GetChildren()) {
                Process(childObj, foundEntry.Children, entity.Object.Client, gameObject);
            }
        }
        
        if (LateLoad && IsSceneHostDetermined) {
            if (IsSceneHost) {
                // Since this is a late load it needs to be initialized as host if we are the scene host
                entity.InitializeHost();
            } else {
                // Its authoritative active state may already be queued; never
                // activate an additive boss merely because it loaded locally.
                entity.InitializeClient();
            }
        }
    }
}
