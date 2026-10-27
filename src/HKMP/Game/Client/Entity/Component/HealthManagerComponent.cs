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
        Prevent.SetValue(h, (_flags & 8) != 0);
        Stun.SetValue(h, h.GetComponents<PlayMakerFSM>().FirstOrDefault(f => f.FsmName == _stun));
        var battle = EntityReferences.Resolve(_battle, client);
        // An early authority snapshot may precede its SetBattleScene call.
        // It cannot unbind a native local encounter or acknowledge its deaths.
        var localBattle = Battle.GetValue(h) as GameObject;
        if (battle == null && LocalBattleCompletion.IsLocal(localBattle)) battle = localBattle;
        Battle.SetValue(h, battle);
        // This bit is a receipt for this player's local battle counter.
        // An authority receipt cannot acknowledge the replica's local arena.
        if (battle != null && !LocalBattleCompletion.IsLocal(battle)) Notified.SetValue(h, (_flags & 16) != 0);
        LocalBattleCompletion.Bound(h, battle);
    }
    private readonly HostClientPair<HealthManager> _healthManager;
    private readonly EntityType _entityType;
    private readonly ulong _bossId;
    private bool _allowDeath, _known, _dead;
    private bool _bossVictorySignaled;
    private int _hp = -1, _direction = -1;
    private bool _invincible;
    private uint _revision, _receivedRevision;
    private uint _receivedPhaseRevision, _receivedExtraRevision;
    private float _nextSnapshot;
    private float _nextDamageEncounter;
    private readonly BossDamageBudget _damageBudget = new();
    private int ScaleBossDamage(int damage) {
        if (IsControlled || CoopContext.Resetting || _dead || _healthManager.Host == null || _healthManager.Host.hp <= 0 ||
            _healthManager.Host.isDead) return damage;
        return _damageBudget.Scale(damage, CoopContext.BossHealthMultiplier?.Invoke() ?? 1);
    }

    public HealthManagerComponent(NetClient netClient, ulong entityId,
        HostClientPair<GameObject> gameObject, HostClientPair<HealthManager> healthManager, EntityType entityType)
        : base(netClient, entityId, gameObject) {
        _healthManager = healthManager;
        _entityType = entityType;
        _bossId = entityId;
        EntityHooks.WatchHealth(healthManager.Host, healthManager.Client, OnDie, OnExtraDamage);
        if (BossDefeatSignals.IsBoss(entityType) || entityType is EntityType.FalseKnightHead or EntityType.BroodingMawlekHead)
            BossDamageScaling.Watch(healthManager.Host, ScaleBossDamage, ObserveDamage);
        MonoBehaviourUtil.Instance.OnUpdateEvent += OnUpdate;
    }

    private void ObserveDamage(int damage, GameObject source) {
        if (!IsControlled && !CoopContext.Resetting && !_dead && _healthManager.Host != null && !_healthManager.Host.isDead)
            CoopContext.BossDamage?.Invoke(damage, source);
    }

    private void OnExtraDamage(On.HealthManager.orig_ApplyExtraDamage orig, HealthManager self, int amount) {
        if (self == _healthManager.Host && !IsControlled && !CoopContext.Resetting && !_dead) {
            var data = new EntityNetworkData { Type = EntityComponentType.ExtraDamage };
            data.Packet.Write(++_revision); SendData(data);
        }
        orig(self, amount);
        // A number of native boss death actions apply terminal damage without
        // re-entering HealthManager.Die.  Report immediately after the native
        // call, before its death effect can disable or recycle the object.
        if (self == _healthManager.Host && !IsControlled && !CoopContext.Resetting &&
            !_bossVictorySignaled && self.isDead && self.hp <= 0 && !self.hasSpecialDeath && !self.deathReset) {
            _bossVictorySignaled = true;
            BossDefeatSignals.HealthDeath(_entityType, _bossId);
        }
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
        var earlyBattle = LocalBattleCompletion.BeforeNativeDeath(self);
        orig(self, attackDirection, attackType, ignoreEvasion);
        // hasSpecialDeath/deathReset are phase transitions (False Knight,
        // Soul Master, etc.), not tombstones.
        if (!self.isDead || self.hasSpecialDeath || self.deathReset) {
            // A ZERO HP special-death event is a phase event, not a tombstone.
            var phase = new EntityNetworkData { Type = EntityComponentType.SpecialDeath };
            phase.Packet.Write(++_revision); phase.Packet.Write(attackDirection.HasValue);
            if (attackDirection.HasValue) phase.Packet.Write(attackDirection.Value);
            phase.Packet.Write((byte)attackType); phase.Packet.Write(ignoreEvasion); SendData(phase); return;
        }
        if (_dead) return;
        LocalBattleCompletion.NativeDeathApplied(_bossId, self, earlyBattle);
        _dead = true; _hp = 0; _known = true;
        if (!_bossVictorySignaled) { _bossVictorySignaled = true; BossDefeatSignals.HealthDeath(_entityType, _bossId); }
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
        if (BossDefeatSignals.IsBoss(_entityType) && !host.isDead && host.hp > 0 && host.gameObject.activeInHierarchy &&
            Time.unscaledTime >= _nextDamageEncounter) {
            _nextDamageEncounter = Time.unscaledTime + .25f;
            CoopContext.BossEncounter?.Invoke();
        }
        // Some native HealthManager paths mark isDead through a special death
        // action without calling Die again. Observe the terminal flag here so
        // ordinary story bosses still report victory exactly once.
        if (host.isDead && host.hp <= 0 && !host.hasSpecialDeath && !host.deathReset && !_bossVictorySignaled) {
            _bossVictorySignaled = true;
            BossDefeatSignals.HealthDeath(_entityType, _bossId);
        }
        var reset = _dead && !host.isDead && host.hp > 0;
        if (_dead && !reset) return;
        var flags = Flags(host);
        var stun = (Stun.GetValue(host) as PlayMakerFSM)?.FsmName ?? "";
        var battle = EntityReferences.Capture(Battle.GetValue(host) as GameObject);
        if (_hp == host.hp && _invincible == host.IsInvincible &&
            _direction == host.InvincibleFromDirection && flags == _flags && stun == _stun && battle == _battle && !reset && Time.unscaledTime < _nextSnapshot) return;
        if (reset) { _dead = false; _bossVictorySignaled = false; LocalBattleCompletion.ResetDeath(_bossId); }
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
            if (data.Type == EntityComponentType.HealthReset && hp > 0) {
                _dead = false;
                LocalBattleCompletion.ResetDeath(_bossId);
                foreach (var hm in new[] { _healthManager.Host, _healthManager.Client })
                    if (hm != null) Notified.SetValue(hm, false);
            }
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
            LocalBattleCompletion.BeforeRemoteDeath(_bossId, _healthManager.Host, _healthManager.Client);
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
            if (alreadyInSceneUpdate) BossDefeatSignals.ApplyLateDeathRecord(_entityType);
            LocalBattleCompletion.RemoteDeathApplied(_healthManager.Host, _healthManager.Client);
        }
    }

    public override void Destroy() {
        BossDamageScaling.Forget(_healthManager.Host);
        EntityHooks.ForgetHealth(_healthManager.Host, _healthManager.Client);
        MonoBehaviourUtil.Instance.OnUpdateEvent -= OnUpdate;
    }
}
