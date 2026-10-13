using System.Collections;
using Hkmp.Game.Client.Entity.Action;
using Hkmp.Networking.Client;
using Hkmp.Networking.Packet.Data;
using UnityEngine;

namespace Hkmp.Game.Client.Entity.Component;

/// <inheritdoc />
/// This component manages the EnemySpawner behaviour of the entity.
internal class EnemySpawnerComponent : EntityComponent {
    /// <summary>
    /// The <see cref="EnemySpawner"/> unity component of the entity.
    /// </summary>
    private readonly HostClientPair<EnemySpawner> _spawner;

    public EnemySpawnerComponent(
        NetClient netClient,
        ulong entityId,
        HostClientPair<GameObject> gameObject,
        HostClientPair<EnemySpawner> spawner
    ) : base(netClient, entityId, gameObject) {
        _spawner = spawner;
        spawner.Client.enabled = false;
        
        On.EnemySpawner.Start += EnemySpawnerOnStart;
        On.EnemySpawner.Awake += EnemySpawnerOnAwake;
        spawner.Host.OnEnemySpawned += OnEnemySpawned;
    }

    private void EnemySpawnerOnAwake(On.EnemySpawner.orig_Awake orig, EnemySpawner self) {
        // The replica receives the real summoned enemy. Its native Awake must
        // not create a second, unregistered enemy beside that network lifetime.
        if (self != _spawner.Client) orig(self);
    }

    /// <summary>
    /// Hook for when the EnemySpawner starts to check whether the interpolation move happens.
    /// </summary>
    private void EnemySpawnerOnStart(On.EnemySpawner.orig_Start orig, EnemySpawner self) {
        orig(self);

        if (self != _spawner.Host) {
            return;
        }

        // If the game object is still active after this method, we know that the interpolation was
        // initiated
        if (self.gameObject.activeSelf) {
            var data = new EntityNetworkData {
                Type = EntityComponentType.EnemySpawner
            };
            SendData(data);
        }
    }
    
    /// <summary>
    /// Hook for when the enemy object is spawned from the spawner so we can call the spawn event.
    /// </summary>
    /// <param name="obj">The spawned game object.</param>
    private void OnEnemySpawned(GameObject obj) {
        EntityFsmActions.CallEntitySpawnEvent(new EntitySpawnDetails {
            Type = EntitySpawnType.EnemySpawnerComponent,
            GameObject = obj, Source = _spawner.Host.gameObject, Prefab = _spawner.Host.enemyPrefab
        });
    }

    /// <inheritdoc />
    public override void InitializeHost() {
    }

    /// <inheritdoc />
    public override void Update(EntityNetworkData data, bool alreadyInSceneUpdate) {
        // Position and final tint come from the authority. Replaying this
        // relative tween on a late snapshot moves the summon twice.
    }

    /// <inheritdoc />
    public override void Destroy() {
        On.EnemySpawner.Start -= EnemySpawnerOnStart;
        On.EnemySpawner.Awake -= EnemySpawnerOnAwake;

        if (_spawner.Host != null) {
            _spawner.Host.OnEnemySpawned -= OnEnemySpawned;
        }
    }
}
