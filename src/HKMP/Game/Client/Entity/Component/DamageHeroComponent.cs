using Hkmp.Networking.Client;
using Hkmp.Networking.Packet.Data;
using Hkmp.Util;
using UnityEngine;

namespace Hkmp.Game.Client.Entity.Component;

/// <inheritdoc />
/// This component manages the damage that an entity deals to the player.
internal class DamageHeroComponent : EntityComponent {
    /// <summary>
    /// The host-client pair of <see cref="DamageHero"/> unity components of the entity.
    /// </summary>
    private readonly HostClientPair<DamageHero> _damageHero;

    /// <summary>
    /// The last value of damage dealt for the damage hero.
    /// </summary>
    private int _lastDamageDealt = -1;

    public DamageHeroComponent(
        NetClient netClient,
        ulong entityId,
        HostClientPair<GameObject> gameObject,
        HostClientPair<DamageHero> damageHero
    ) : base(netClient, entityId, gameObject) {
        _damageHero = damageHero;

        MonoBehaviourUtil.Instance.OnUpdateEvent += OnUpdate;
    }

    /// <summary>
    /// Callback method to check for damage hero updates.
    /// </summary>
    private void OnUpdate() {
        if (IsControlled) {
            return;
        }

        if (GameObject.Host == null) {
            return;
        }

        var newDamageDealt = _damageHero.Host.damageDealt;
        if (newDamageDealt != _lastDamageDealt) {
            _lastDamageDealt = newDamageDealt;
            
            var data = new EntityNetworkData {
                Type = EntityComponentType.DamageHero
            };
            data.Packet.Write((byte) newDamageDealt);

            SendData(data);
        }
    }

    /// <inheritdoc />
    public override void InitializeHost() {
        _lastDamageDealt = -1;
    }

    /// <inheritdoc />
    public override void Update(EntityNetworkData data, bool alreadyInSceneUpdate) {
        var damageDealt = data.Packet.ReadByte();
        if (_damageHero.Host != null) _damageHero.Host.damageDealt = damageDealt;
        if (_damageHero.Client != null) _damageHero.Client.damageDealt = damageDealt;
    }

    /// <inheritdoc />
    public override void Destroy() {
        MonoBehaviourUtil.Instance.OnUpdateEvent -= OnUpdate;
    }
}
