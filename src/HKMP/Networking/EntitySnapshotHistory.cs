using System.Collections.Generic;
using System.Linq;
using Hkmp.Game.Client.Entity.Component;
using Hkmp.Networking.Packet;
using Hkmp.Networking.Packet.Data;

namespace Hkmp.Networking;

/// <summary>
/// Rejects older entity and player snapshot fields without discarding reliable actions or
/// sparse fields absent from later packets. One instance belongs to one peer
/// session; revisions are the unwrapped original UDP sequence numbers.
/// </summary>
internal sealed class EntitySnapshotHistory {
    private static readonly PlayerUpdateType[] PlayerMotionFields = {
        PlayerUpdateType.Position, PlayerUpdateType.Scale, PlayerUpdateType.MapPosition
    };
    private sealed class Entry {
        public long Latest;
        public readonly Dictionary<(EntityUpdateType Field, byte Axis), long> Fields = new();
        public readonly Dictionary<(byte Fsm, EntityHostFsmData.Type Field, byte Variable), long> FsmFields = new();
        public readonly Dictionary<EntityComponentType, long> Components = new();
    }

    private readonly Dictionary<(string Scene, uint Visit, uint Epoch, ulong Id), Entry> _entities = new();
    private sealed class PlayerEntry {
        public long Latest;
        public readonly Dictionary<PlayerUpdateType, long> Fields = new();
    }
    private readonly Dictionary<(string Scene, uint Visit, uint Epoch, uint PlayerVisit, ushort Id), PlayerEntry> _players = new();
    private long _lastPruned;

    public void Clear() {
        _entities.Clear();
        _players.Clear();
        _lastPruned = 0;
    }

    public void Advance(long revision) {
        if (revision - _lastPruned < 1024) return;
        var cutoff = revision - SequenceWindow.Size;
        foreach (var key in _entities.Where(pair => pair.Value.Latest < cutoff).Select(pair => pair.Key).ToArray())
            _entities.Remove(key);
        foreach (var key in _players.Where(pair => pair.Value.Latest < cutoff).Select(pair => pair.Key).ToArray())
            _players.Remove(key);
        _lastPruned = revision;
    }

    private static bool Accept<TKey>(Dictionary<TKey, long> revisions, TKey key, long revision) {
        if (revisions.TryGetValue(key, out var previous) && revision < previous) return false;
        revisions[key] = revision;
        return true;
    }

    public void Filter(IPacketData data, long revision) {
        if (data is RawPacketDataCollection collection) {
            foreach (var item in collection.DataInstances) Filter(item, revision);
            return;
        }
        // Initial room and takeover snapshots share the original datagram's
        // revision with live updates. Their nested fields must neither roll
        // back newer updates nor be overwritten by older delayed updates.
        if (data is HostTransfer transfer) {
            Filter(transfer.Snapshot, revision);
            return;
        }
        if (data is ClientPlayerAlreadyInScene scene) {
            foreach (var item in scene.EntityUpdateList) Filter(item, revision);
            foreach (var item in scene.ReliableEntityUpdateList) Filter(item, revision);
            return;
        }
        if (data is PlayerUpdate player) {
            // Id zero is valid on the server's per-peer receive path, before
            // the authenticated sender's real player id is stamped onto it.
            var playerKey = (player.SceneName, player.Visit, player.Epoch, player.PlayerVisit, player.Id);
            if (!_players.TryGetValue(playerKey, out var playerEntry)) {
                playerEntry = new PlayerEntry { Latest = revision };
                _players[playerKey] = playerEntry;
            }
            if (revision > playerEntry.Latest) playerEntry.Latest = revision;
            foreach (var field in PlayerMotionFields) {
                if (player.UpdateTypes.Contains(field) && !Accept(playerEntry.Fields, field, revision)) player.UpdateTypes.Remove(field);
            }
            return;
        }
        if (data is not BaseEntityUpdate update || update.Id == 0) return;
        if (update is ReliableEntityUpdate eventsOnly &&
            !eventsOnly.UpdateTypes.Contains(EntityUpdateType.Active) &&
            !eventsOnly.UpdateTypes.Contains(EntityUpdateType.HostFsm) &&
            !eventsOnly.GenericData.Any(item => item.Type == EntityComponentType.CombatState)) return;

        var key = (update.SceneName, update.Visit, update.Epoch, update.Id);
        if (!_entities.TryGetValue(key, out var entry)) {
            entry = new Entry { Latest = revision };
            _entities[key] = entry;
        }
        if (revision > entry.Latest) entry.Latest = revision;

        void Field(HashSet<EntityUpdateType> types, EntityUpdateType field) {
            if (types.Contains(field) && !Accept(entry.Fields, (field, (byte)0), revision)) types.Remove(field);
        }
        if (update is EntityUpdate motion) {
            Field(motion.UpdateTypes, EntityUpdateType.Position);
            Field(motion.UpdateTypes, EntityUpdateType.Animation);
            if (motion.UpdateTypes.Contains(EntityUpdateType.Scale)) {
                var scale = motion.Scale;
                if (scale.x && !Accept(entry.Fields, (EntityUpdateType.Scale, (byte)0), revision)) scale.x = false;
                if (scale.y && !Accept(entry.Fields, (EntityUpdateType.Scale, (byte)1), revision)) scale.y = false;
                if (scale.z && !Accept(entry.Fields, (EntityUpdateType.Scale, (byte)2), revision)) scale.z = false;
                if (scale.IsEmpty) motion.UpdateTypes.Remove(EntityUpdateType.Scale);
            }
        } else if (update is ReliableEntityUpdate reliable) {
            // CombatState is a complete snapshot of its managed subtree, not
            // an action. Delayed originals and resends must not undo newer
            // collider/child state even when this packet has no FSM fields.
            foreach (var item in reliable.GenericData.ToArray()) {
                if (item.Type == EntityComponentType.CombatState && !Accept(entry.Components, item.Type, revision))
                    reliable.GenericData.Remove(item);
            }
            if (reliable.GenericData.Count == 0) reliable.UpdateTypes.Remove(EntityUpdateType.Data);
            Field(reliable.UpdateTypes, EntityUpdateType.Active);
            foreach (var pair in reliable.HostFsmData.ToArray()) {
                var fsm = pair.Value; var index = pair.Key;
                if (fsm.Types.Contains(EntityHostFsmData.Type.State) &&
                    !Accept(entry.FsmFields, (index, EntityHostFsmData.Type.State, (byte)0), revision))
                    fsm.Types.Remove(EntityHostFsmData.Type.State);

                void Variables<T>(EntityHostFsmData.Type field, Dictionary<byte, T> values) {
                    if (!fsm.Types.Contains(field)) return;
                    foreach (var variable in values.Keys.ToArray()) {
                        if (!Accept(entry.FsmFields, (index, field, variable), revision)) values.Remove(variable);
                    }
                    if (values.Count == 0) fsm.Types.Remove(field);
                }
                Variables(EntityHostFsmData.Type.Floats, fsm.Floats);
                Variables(EntityHostFsmData.Type.Ints, fsm.Ints);
                Variables(EntityHostFsmData.Type.Bools, fsm.Bools);
                Variables(EntityHostFsmData.Type.Strings, fsm.Strings);
                Variables(EntityHostFsmData.Type.Vector2s, fsm.Vec2s);
                Variables(EntityHostFsmData.Type.Vector3s, fsm.Vec3s);
                Variables(EntityHostFsmData.Type.Objects, fsm.Objects);
                if (fsm.Types.Count == 0) reliable.HostFsmData.Remove(index);
            }
            if (reliable.HostFsmData.Count == 0) reliable.UpdateTypes.Remove(EntityUpdateType.HostFsm);
        }
    }
}
