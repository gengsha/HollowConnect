using Hkmp.Networking.Packet;
using Hkmp.Networking.Packet.Data;

namespace Hkmp.Game.Server;

// Freeze cached state under worldLock; pending snapshots cannot share mutable
// dictionaries or packets with later authority updates on a network thread.
internal static class SnapshotPayload {
    internal static EntityHostFsmData Copy(EntityHostFsmData value) {
        var copy = new EntityHostFsmData();
        copy.MergeData(value);
        if (value.Types.Contains(EntityHostFsmData.Type.Vector2s))
            foreach (var pair in value.Vec2s)
                copy.Vec2s[pair.Key] = new Hkmp.Math.Vector2(pair.Value.X, pair.Value.Y);
        if (value.Types.Contains(EntityHostFsmData.Type.Vector3s))
            foreach (var pair in value.Vec3s)
                copy.Vec3s[pair.Key] = new Hkmp.Math.Vector3(pair.Value.X, pair.Value.Y, pair.Value.Z);
        return copy;
    }
    internal static EntityNetworkData Copy(EntityNetworkData value) =>
        new() { Type = value.Type, Packet = new Packet(value.Packet.ToArray()) };
    internal static EntityUpdate.ScaleData Copy(EntityUpdate.ScaleData value) {
        var packet = new Packet(); value.WriteData(packet);
        var copy = new EntityUpdate.ScaleData(); copy.ReadData(new Packet(packet.ToArray())); return copy;
    }
}
