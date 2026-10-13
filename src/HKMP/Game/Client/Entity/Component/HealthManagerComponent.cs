using Hkmp.Networking.Client;
using Hkmp.Networking.Packet.Data;
using Hkmp.Util;
using UnityEngine;
using System.Linq;
using System.Reflection;

namespace Hkmp.Game.Client.Entity.Component;

// Absolute, versioned health is also applied to the dormant host copy. This is
// essential when the original scene authority leaves or is disconnected.
internal class HealthManagerComponent : EntityComponent {
    private static readonly FieldInfo Prevent = typeof(HealthManager).GetField("preventInvincibleEffect", BindingFlags.Instance | BindingFlags.NonPublic);
    private static readonly FieldInfo Stun = typeof(HealthManager).GetField("stunControlFSM", BindingFlags.Instance | BindingFlags.NonPublic);
    private static readonly FieldInfo Battle = typeof(HealthManager).GetField("battleScene", BindingFlags.Instance | BindingFlags.NonPublic);
    private static readonly FieldInfo Notified = typeof(HealthManager).GetField("notifiedBattleScene", BindingFlags.Instance | BindingFlags.NonPublic);
    private byte _flags;
    private string _stun = "", _battle = "";
    private static byte Flags(HealthManager h) => (byte)((h.hasSpecialDeath ? 1 : 0) | (h.deathReset ? 2 : 0) | (h.damageOverride ? 4 : 0) |
        ((bool)Prevent.GetValue(h) ? 8 : 0) | ((bool)Notified.GetValue(h) ? 16 : 0));
    private void ApplyPhase(HealthManager h, bool client) {
        h.hasSpecialDeath = (_flags & 1) != 0; h.deathReset = (_flags & 2) != 0; h.damageOverride = (_flags & 4) != 0;
        Prevent.SetValue(h, (_flags & 8) != 0); Notified.SetValue(h, (_flags & 16) != 0);
        Stun.SetValue(h, h.GetComponents<PlayMakerFSM>().FirstOrDefault(f => f.FsmName == _stun));
        Battle.SetValue(h, EntityReferences.Resolve(_battle, client));
    }
    private readonly HostClientPair<HealthManager> _healthManager;
    private bool _allowDeath, _known, _dead;
    private int _hp = -1, _direction = -1;
    private bool _invincible;
    private uint _revision, _receivedRevision;
    private uint _receivedPhaseRevision, _receivedExtraRevision;
    private float _nextSnapshot;

    public HealthManagerComponent(NetClient netClient, ulong entityId,
        HostClientPair<GameObject> gameObject, HostClientPair<HealthManager> healthManager)
        : base(netClient, entityId, gameObject) {
        _healthManager = healthManager;
        EntityHooks.WatchHealth(healthManager.Host, healthManager.Client, OnDie, OnExtraDamage);
        MonoBehaviourUtil.Instance.OnUpdateEvent += OnUpdate;
    }

    private void OnExtraDamage(On.HealthManager.orig_ApplyExtraDamage orig, HealthManager self, int amount) {
        if (self == _healthManager.Host && !IsControlled && !CoopContext.Resetting && !_dead) {
            var data = new EntityNetworkData { Type = EntityComponentType.ExtraDamage };
            data.Packet.Write(++_revision); SendData(data);
        }
        orig(self, amount);
    }

    private void OnDie(On.HealthManager.orig_Die orig, HealthManager self,
        float? attackDirection, AttackTypes attackType, bool ignoreEvasion) {
        if (self != _healthManager.Host && self != _healthManager.Client) {
            orig(self, attackDirection, attackType, ignoreEvasion); return;
        }
        if (self == _healthManager.Client) {
            if (_allowDeath) { _allowDeath = false; orig(self, attackDirection, attackType, ignoreEvasion); }
            return;
        }
        if (IsControlled || CoopContext.Resetting) return;
        orig(self, attackDirection, attackType, ignoreEvasion);
        // hasSpecialDeath is a phase transition (False Knight, etc.), not a tombstone.
        if (!self.isDead) {
            // A ZERO HP special-death event is a phase event, not a tombstone.
            var phase = new EntityNetworkData { Type = EntityComponentType.SpecialDeath };
            phase.Packet.Write(++_revision); phase.Packet.Write(attackDirection.HasValue);
            if (attackDirection.HasValue) phase.Packet.Write(attackDirection.Value);
            phase.Packet.Write((byte)attackType); phase.Packet.Write(ignoreEvasion); SendData(phase); return;
        }
        if (_dead) return;
        _dead = true; _hp = 0; _known = true;
        var data = new EntityNetworkData { Type = EntityComponentType.Death };
        data.Packet.Write(++_revision);
        data.Packet.Write(attackDirection.HasValue);
        if (attackDirection.HasValue) data.Packet.Write(attackDirection.Value);
        data.Packet.Write((byte)attackType); data.Packet.Write(ignoreEvasion);
        SendData(data);
    }

    private void OnUpdate() {
        if (IsControlled || CoopContext.Resetting || _healthManager.Host == null) return;
        var host = _healthManager.Host;
        var reset = _dead && !host.isDead && host.hp > 0;
        if (_dead && !reset) return;
        var flags = Flags(host);
        var stun = (Stun.GetValue(host) as PlayMakerFSM)?.FsmName ?? "";
        var battle = EntityReferences.Capture(Battle.GetValue(host) as GameObject);
        if (_hp == host.hp && _invincible == host.IsInvincible &&
            _direction == host.InvincibleFromDirection && flags == _flags && stun == _stun && battle == _battle && !reset && Time.unscaledTime < _nextSnapshot) return;
        if (reset) _dead = false;
        _flags = flags; _stun = stun; _battle = battle;
        _hp = host.hp; _invincible = host.IsInvincible; _direction = host.InvincibleFromDirection;
        _known = true; _nextSnapshot = Time.unscaledTime + 1f;
        var data = new EntityNetworkData { Type = reset ? EntityComponentType.HealthReset : EntityComponentType.Health };
        data.Packet.Write(++_revision); data.Packet.Write(_hp);
        data.Packet.Write(_invincible); data.Packet.Write(_direction);
        data.Packet.Write(_flags); data.Packet.Write(_stun); data.Packet.Write(_battle);
        SendData(data);
    }

    public override void InitializeHost() {
        if (_healthManager.Host == null) return;
        if (_dead) { _healthManager.Host.gameObject.SetActive(false); return; }
        if (_known) {
            _healthManager.Host.hp = _hp;
            _healthManager.Host.IsInvincible = _invincible;
            _healthManager.Host.InvincibleFromDirection = _direction;
            ApplyPhase(_healthManager.Host, false);
        }
        _revision = System.Math.Max(_revision, System.Math.Max(_receivedRevision, System.Math.Max(_receivedPhaseRevision, _receivedExtraRevision)));
        _nextSnapshot = 0;
    }

    public override void Update(EntityNetworkData data, bool alreadyInSceneUpdate) {
        if (!IsControlled) return;
        if (data.Type == EntityComponentType.ExtraDamage) {
            var revision = data.Packet.ReadUInt();
            if (_dead || alreadyInSceneUpdate || revision <= _receivedExtraRevision || _healthManager.Client == null) return;
            _receivedExtraRevision = revision;
            // HP comes from the absolute snapshot; only mirror the native event.
            FSMUtility.SendEventToGameObject(_healthManager.Client.gameObject, "EXTRA DAMAGED");
        } else if (data.Type == EntityComponentType.SpecialDeath) {
            var revision = data.Packet.ReadUInt();
            float? direction = data.Packet.ReadBool() ? data.Packet.ReadFloat() : (float?)null;
            var type = (AttackTypes)data.Packet.ReadByte(); var ignore = data.Packet.ReadBool();
            // Batching may put a newer absolute HP snapshot before this event.
            // Event deduplication must not share the HP revision watermark.
            if (_dead || revision <= _receivedPhaseRevision || alreadyInSceneUpdate || _healthManager.Client == null) return;
            _receivedPhaseRevision = revision;
            var hm = _healthManager.Client; var special = hm.hasSpecialDeath;
            hm.hasSpecialDeath = true; _allowDeath = true;
            try { hm.Die(direction, type, ignore); } finally { hm.hasSpecialDeath = special; _allowDeath = false; }
        } else if (data.Type == EntityComponentType.Health || data.Type == EntityComponentType.HealthReset) {
            var revision = data.Packet.ReadUInt();
            var hp = data.Packet.ReadInt();
            var invincible = data.Packet.ReadBool(); var direction = data.Packet.ReadInt();
            var flags = data.Packet.ReadByte(); var stun = data.Packet.ReadString(); var battle = data.Packet.ReadString();
            if (stun.Length > 256 || battle.Length > 2048) return;
            if ((_dead && data.Type != EntityComponentType.HealthReset) || revision <= _receivedRevision) return;
            if (data.Type == EntityComponentType.HealthReset && hp > 0) _dead = false;
            _flags = flags; _stun = stun; _battle = battle;
            _receivedRevision = revision; _hp = hp; _invincible = invincible; _direction = direction; _known = true;
            foreach (var hm in new[] { _healthManager.Host, _healthManager.Client }) {
                if (hm == null) continue;
                hm.hp = hp; hm.IsInvincible = invincible; hm.InvincibleFromDirection = direction;
                hm.isDead = _dead; ApplyPhase(hm, hm == _healthManager.Client);
            }
        } else if (data.Type == EntityComponentType.Death) {
            var revision = data.Packet.ReadUInt();
            float? direction = data.Packet.ReadBool() ? data.Packet.ReadFloat() : (float?)null;
            var type = (AttackTypes)data.Packet.ReadByte(); var ignore = data.Packet.ReadBool();
            if (_dead || revision <= _receivedRevision) return;
            _receivedRevision = revision; _dead = true; _hp = 0; _known = true;
            if (_healthManager.Host != null) {
                _healthManager.Host.hp = 0; _healthManager.Host.isDead = true;
                _healthManager.Host.SendDeathEvent();
                _healthManager.Host.gameObject.SetActive(false);
            }
            if (_healthManager.Client != null) {
                if (alreadyInSceneUpdate) {
                    _healthManager.Client.SetGeoSmall(0); _healthManager.Client.SetGeoMedium(0); _healthManager.Client.SetGeoLarge(0);
                }
                _allowDeath = true;
                _healthManager.Client.hasSpecialDeath = false;
                _healthManager.Client.Die(direction, type, ignore);
            }
        }
    }

    public override void Destroy() {
        EntityHooks.ForgetHealth(_healthManager.Host, _healthManager.Client);
        MonoBehaviourUtil.Instance.OnUpdateEvent -= OnUpdate;
    }
}
