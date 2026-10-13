using Hkmp.Networking.Packet;
using Hkmp.Networking.Packet.Data;

namespace Hkmp.Game.Server;

// Freeze cached state under worldLock; pending snapshots cannot share mutable
// dictionaries or packets with later authority updates on a network thread.
internal static class SnapshotPayload {
    internal static EntityHostFsmData Copy(EntityHostFsmData value) {
        var packet = new Packet(); value.WriteData(packet);
        var copy = new EntityHostFsmData(); copy.ReadData(new Packet(packet.ToArray())); return copy;
    }
    internal static EntityNetworkData Copy(EntityNetworkData value) =>
        new() { Type = value.Type, Packet = new Packet(value.Packet.ToArray()) };
    internal static EntityUpdate.ScaleData Copy(EntityUpdate.ScaleData value) {
        var packet = new Packet(); value.WriteData(packet);
        var copy = new EntityUpdate.ScaleData(); copy.ReadData(new Packet(packet.ToArray())); return copy;
    }
}
