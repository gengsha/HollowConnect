using System.Collections.Generic;
using System.Linq;
using Hkmp.Game.Client.Entity.Component;
using Hkmp.Util;
using Newtonsoft.Json;
using Newtonsoft.Json.Converters;
using UnityEngine;
using Logger = Hkmp.Logging.Logger;

namespace Hkmp.Game.Client.Entity; 

/// <summary>
/// Static class that manages loading and storing of entity data. Such as names of game objects, names of FSMs and
/// corresponding types.
/// </summary>
internal static class EntityRegistry {
    private static readonly HashSet<string> Reported = new();
    internal static bool IsHazard(GameObject go) => go != null && go.GetComponent<DamageHero>() != null;
    internal static EntityRegistryEntry Transient(GameObject go) => new EntityRegistryEntry {
        BaseObjectName = go.name, Type = EntityType.DynamicHazard, ComponentTypes = new[] { EntityComponentType.Rotation, EntityComponentType.ZPosition }
    };
    // Entrance gates keep per-player triggers. Colosseum's moving walls are
    // shared arena geometry and follow the battle authority instead.
    internal static bool IsLocalOnlyBarrier(EntityType type) => type is
        EntityType.BattleGate or EntityType.MantisLordCage or EntityType.CameraLockArea;

    internal static bool IsLocalOnlyBarrier(GameObject go) {
        if (go == null) return false;
        for (var transform = go.transform; transform != null; transform = transform.parent) {
            var name = transform.name;
            foreach (var entry in LocalBarrierEntries) {
                if (!name.Contains(entry.BaseObjectName)) continue;
                if (entry.FsmName == null || transform.GetComponents<PlayMakerFSM>().Any(f => f.FsmName == entry.FsmName)) return true;
            }
        }
        return false;
    }
    /// <summary>
    /// The file path of the embedded resource file for the entity registry.
    /// </summary>
    private const string EntityRegistryFilePath = "Hkmp.Resource.entity-registry.json";
    
    /// <summary>
    /// List of all entity registry entries that are loaded from the embedded file.
    /// </summary>
    private static List<EntityRegistryEntry> Entries { get; }
    private static EntityRegistryEntry[] SpawnEntries { get; }
    private static EntityRegistryEntry[] LocalBarrierEntries { get; }

    static EntityRegistry() {
        Entries = FileUtil.LoadObjectFromEmbeddedJson<List<EntityRegistryEntry>>(EntityRegistryFilePath);
        if (Entries == null) {
            Logger.Warn("Could not load entity registry");
        }
        SpawnEntries = Entries?.SelectMany(Flatten).ToArray() ?? System.Array.Empty<EntityRegistryEntry>();
        // Root entries identify their whole local subtree.
        LocalBarrierEntries = Entries?.Where(e => IsLocalOnlyBarrier(e.Type)).ToArray() ?? System.Array.Empty<EntityRegistryEntry>();
    }

    /// <summary>
    /// Try to get the corresponding entry from the given enumerable of entries and the given game object.
    /// </summary>
    /// <param name="gameObject">The game object to find the entry for.</param>
    /// <param name="foundEntry">The entry if it was found; otherwise null.</param>
    /// <returns>true if the entry was found; otherwise false.</returns>
    public static bool TryGetEntry(
        GameObject gameObject,
        out EntityRegistryEntry foundEntry
    ) {
        return TryGetEntry(Entries, gameObject, out foundEntry);
    }

    // A registered child may also be spawned as a detached pooled root.
    internal static bool TryGetSpawnEntry(GameObject gameObject, out EntityRegistryEntry entry) =>
        TryGetEntry(SpawnEntries, gameObject, out entry);
    private static IEnumerable<EntityRegistryEntry> Flatten(EntityRegistryEntry entry) {
        yield return entry;
        if (entry.Children != null)
            foreach (var child in entry.Children.SelectMany(Flatten)) yield return child;
    }

    /// <summary>
    /// Try to get the corresponding entry from the given enumerable of entries and the given game object.
    /// </summary>
    /// <param name="entries">The enumerable of entries to check for.</param>
    /// <param name="gameObject">The game object to find the entry for.</param>
    /// <param name="foundEntry">The entry if it was found; otherwise null.</param>
    /// <returns>true if the entry was found; otherwise false.</returns>
    public static bool TryGetEntry(
        IEnumerable<EntityRegistryEntry> entries, 
        GameObject gameObject, 
        out EntityRegistryEntry foundEntry
    ) {
        var longestBaseName = 0;
        var exactFsm = false;
        var ambiguous = false;
        foundEntry = null;
        if (gameObject == null) return false;
        // Reading Unity's name crosses the native boundary and creates a new
        // managed string. Do it once, rather than once per registry entry for
        // every projectile and every candidate prefab.
        var objectName = gameObject.name;

        // The name card also has a Control FSM, but its native timed text
        // animation must never be cloned/frozen as the Grey Prince boss.
        if (objectName.StartsWith("Grey Prince Title", System.StringComparison.Ordinal)) return false;

        foreach (var entry in entries) {
            if (!objectName.Contains(entry.BaseObjectName)) {
                continue;
            }

            // If the entry has an FSM name defined and the child object does not have any FSM components
            // that match this name, we continue
            var matchesFsm = entry.FsmName == null || gameObject.GetComponents<PlayMakerFSM>().Any(
                    childFsm => childFsm.Fsm.Name.Equals(entry.FsmName)
            );
            if (!matchesFsm) {
                if (Reported.Add(gameObject.scene.name + "/" + gameObject.name + "/" + entry.FsmName))
                    Logger.Warn($"Entity FSM mismatch: {gameObject.scene.name}/{gameObject.name}, expected '{entry.FsmName}', actual [{string.Join(",", gameObject.GetComponents<PlayMakerFSM>().Select(f => f.FsmName))}]");
                // Entity snapshots enumerate the actual FSMs, so a unique
                // health-bearing name match can support a renamed variant.
                // Corpses/props without health keep the strict registration.
                if (gameObject.GetComponent<HealthManager>() == null) continue;
            }

            // If the entry has a parent name defined, we need to check if the parent of the game object matches it
            if (entry.ParentName != null) {
                var parent = gameObject.transform.parent;
                // No parent at all, so it trivially doesn't match the name
                if (!parent) {
                    continue;
                }

                if (!parent.gameObject.name.Contains(entry.ParentName)) {
                    continue;
                }
            }

            // Specifically check for entries that don't have a defined FSM whether they contain the
            // correct component(s)
            if (entry.Type == EntityType.DreamPlatform) {
                if (!gameObject.GetComponent<DreamPlatform>()) {
                    continue;
                }
            } else if (entry.Type == EntityType.DynamicHazard && !IsHazard(gameObject)) {
                continue;
            } else if (entry.Type == EntityType.Tiktik) {
                if (!gameObject.GetComponent<Climber>()) {
                    continue;
                }
            } else if (entry.Type == EntityType.VengeflySummon) {
                if (!gameObject.GetComponent<EnemySpawner>()) {
                    continue;
                }
            } else if (entry.Type == EntityType.CollectorJar) {
                if (!gameObject.GetComponent<SpawnJarControl>()) {
                    continue;
                }
            } else if (entry.Type == EntityType.Garpede) {
                if (!gameObject.GetComponent<BigCentipede>()) {
                    continue;
                }
            } else if (entry.Type == EntityType.ShadowCreeper) {
                if (!gameObject.GetComponent<Crawler>()) {
                    continue;
                }
            } else if (entry.Type == EntityType.GrimmFireball) {
                if (!gameObject.GetComponent<GrimmballControl>()) {
                    continue;
                }
            }

            var baseNameLength = entry.BaseObjectName.Length;
            if (baseNameLength > longestBaseName || baseNameLength == longestBaseName && matchesFsm && !exactFsm) {
                longestBaseName = baseNameLength;
                foundEntry = entry;
                exactFsm = matchesFsm; ambiguous = false;
            } else if (baseNameLength == longestBaseName && !matchesFsm && !exactFsm && foundEntry.Type != entry.Type) {
                ambiguous = true;
            }
        }

        if (longestBaseName == 0 || ambiguous) {
            foundEntry = null;
            return false;
        }

        return true;
    }
}

/// <summary>
/// Class representing a single entry in the entity registry that contains the relevant data for an entity type.
/// </summary>
internal class EntityRegistryEntry {
    /// <summary>
    /// The base of the game object name of the entity.
    /// For example: "Zombie Leaper", which in-game can be represented as "Zombie Leaper (Clone) (1)"
    /// </summary>
    [JsonProperty("base_object_name")]
    public string BaseObjectName { get; set; }
    
    /// <summary>
    /// The type of the entity.
    /// </summary>
    [JsonConverter(typeof(StringEnumConverter))]
    [JsonProperty("type")]
    public EntityType Type { get; set; }

    [JsonProperty("networked")]
    public bool Networked { get; set; } = true;

    /// <summary>A scene-owned boss attack/support object outside its hierarchy.</summary>
    [JsonProperty("boss_support")]
    public bool BossSupport { get; set; }
    
    /// <summary>
    /// The name of the FSM that this entity has. Can be empty if the entity does not have a FSM.
    /// </summary>
    [JsonProperty("fsm_name")]
    public string FsmName { get; set; }
    
    /// <summary>
    /// The name of the parent of this object. Can be empty if there is no parent or it is not relevant.
    /// </summary>
    [JsonProperty("parent_name")]
    public string ParentName { get; set; }

    /// <summary>
    /// Array of additional entity component that should be initialized for the entity.
    /// </summary>
    [JsonProperty("components")]
    public EntityComponentType[] ComponentTypes { get; set; }

    /// <summary>
    /// List of entries that are children of this entry.
    /// </summary>
    [JsonProperty("children")]
    public List<EntityRegistryEntry> Children { get; set; }
}
