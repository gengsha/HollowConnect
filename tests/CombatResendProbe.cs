using System;
using System.Linq;
using Hkmp.Game.Client.Entity.Component;
using Hkmp.Networking;
using Hkmp.Networking.Packet;
using Hkmp.Networking.Packet.Data;
using Hkmp.Networking.Packet.Update;

internal static class CombatResendProbe {
    private static void Check(bool ok, string message) { if (!ok) throw new Exception(message); }
    private static ServerUpdatePacket Batch(ushort sequence, byte serial, string scene = "Arena", uint visit = 1) {
        var packet = new ServerUpdatePacket { Sequence = sequence };
        var update = new ReliableEntityUpdate { Id = 7, SceneName = scene, Visit = visit, Epoch = 3 };
        update.UpdateTypes.Add(EntityUpdateType.Data);
        var state = new EntityNetworkData { Type = EntityComponentType.CombatState };
        state.Packet.Write((int)serial); state.Packet.Write(new byte[16000]); update.GenericData.Add(state);
        var action = new EntityNetworkData { Type = EntityComponentType.Fsm }; action.Packet.Write((int)serial); update.GenericData.Add(action);
        var collection = new PacketDataCollection<ReliableEntityUpdate>(); collection.DataInstances.Add(update);
        packet.SetSendingPacketData(ServerUpdatePacketId.ReliableEntityUpdate, collection);
        return packet;
    }
    internal static void LargeLossBacklog() {
        var originals = Enumerable.Range(0, 100).Select(i => Batch(unchecked((ushort)(65500 + i)), (byte)i)).ToArray();
        var outgoing = Batch(64, 100);
        foreach (var original in originals) outgoing.SetLostReliableData(original);
        var wire = new Packet(); outgoing.CreatePacket(wire);
        Check(wire.Length < 30000, "obsolete full states still amplify reliable resends: " + wire.Length);
        var incoming = new ServerUpdatePacket(); Check(incoming.ReadPacket(PacketManager.ReadDatagram(wire.ToArray())), "compacted packet must decode");
        var updates = ((RawPacketDataCollection)incoming.GetPacketData()[ServerUpdatePacketId.ReliableEntityUpdate]).DataInstances.Cast<ReliableEntityUpdate>().ToArray();
        var components = updates.SelectMany(u => u.GenericData).ToArray();
        Check(components.Where(c => c.Type == EntityComponentType.CombatState).Select(c => c.Packet.ReadInt()).SequenceEqual(new[] { 100 }), "newest complete state was not retained");
        Check(components.Where(c => c.Type == EntityComponentType.Fsm).Select(c => c.Packet.ReadInt()).SequenceEqual(Enumerable.Range(0, 101)), "reliable actions were lost/reordered during compaction");
        foreach (var original in originals) {
            var retained = (RawPacketDataCollection)original.GetPacketData()[ServerUpdatePacketId.ReliableEntityUpdate];
            Check(((ReliableEntityUpdate)retained.DataInstances[0]).GenericData.Count == 2, "compaction mutated an original carrier");
        }
        // A nested lost carrier must retain the compacted origins, including
        // every action, without relabeling them as the carrier's own events.
        var later = Batch(65, 101); later.SetLostReliableData(outgoing);
        wire = new Packet(); later.CreatePacket(wire);
        Check(wire.Length < 30000, "nested retry restored obsolete full states");
    }
    internal static void Identity() {
        var outgoing = Batch(4, 4);
        outgoing.SetLostReliableData(Batch(1, 1, "Other arena"));
        outgoing.SetLostReliableData(Batch(2, 2, visit: 2));
        outgoing.SetLostReliableData(Batch(3, 3));
        var wire = new Packet(); outgoing.CreatePacket(wire);
        var incoming = new ServerUpdatePacket(); Check(incoming.ReadPacket(PacketManager.ReadDatagram(wire.ToArray())), "identity packet decode");
        var updates = ((RawPacketDataCollection)incoming.GetPacketData()[ServerUpdatePacketId.ReliableEntityUpdate]).DataInstances.Cast<ReliableEntityUpdate>();
        var states = updates.SelectMany(u => u.GenericData).Where(c => c.Type == EntityComponentType.CombatState).Select(c => c.Packet.ReadInt()).OrderBy(i => i);
        Check(states.SequenceEqual(new[] { 1, 2, 4 }), "snapshot coalescing crossed a scene/visit boundary");
    }

    internal static void BoundedRetryCarriers() {
        var pending = new ServerUpdatePacket { Sequence = 1000 };
        for (ushort i = 0; i < 200; i++) {
            var original = new ServerUpdatePacket { Sequence = i };
            var update = new ReliableEntityUpdate { Id = 7, SceneName = "Arena", Visit = 1, Epoch = 3 };
            update.UpdateTypes.Add(EntityUpdateType.Data);
            var action = new EntityNetworkData { Type = EntityComponentType.Fsm };
            action.Packet.Write((int)i); action.Packet.Write(new byte[i == 0 ? 20000 : 1024]);
            update.GenericData.Add(action);
            var collection = new PacketDataCollection<ReliableEntityUpdate>(); collection.DataInstances.Add(update);
            original.SetSendingPacketData(ServerUpdatePacketId.ReliableEntityUpdate, collection);
            pending.SetLostReliableData(original);
        }
        var actions = new System.Collections.Generic.List<int>();
        var deferred = new ServerUpdatePacket { Sequence = 1001 };
        pending.DeferExcessResends(deferred, 0);
        Check(pending.ReliableOrigins().Length == 1 && deferred.ReliableOrigins().Length == 201,
            "a paced heartbeat must defer every retry origin without losing one");
        pending = deferred;
        var receipts = new SequenceWindow(); var snapshots = new EntitySnapshotHistory();
        var carriers = 0;
        while (pending.ReliableOrigins().Length > 1) {
            Check(++carriers < 40, "retry batching failed to drain");
            var next = new ServerUpdatePacket { Sequence = (ushort)(pending.Sequence + 1) };
            pending.DeferExcessResends(next, 8192);
            var wire = new Packet(); pending.CreatePacket(wire);
            Check(carriers == 1 ? wire.Length < 21000 : wire.Length < 9000, "retry carrier still grows with the loss backlog: " + wire.Length);
            var incoming = new ServerUpdatePacket(); Check(incoming.ReadPacket(PacketManager.ReadDatagram(wire.ToArray())), "bounded retry decode");
            incoming.FilterReceivedData(receipts, snapshots);
            var updates = (RawPacketDataCollection)incoming.GetPacketData()[ServerUpdatePacketId.ReliableEntityUpdate];
            actions.AddRange(updates.DataInstances.Cast<ReliableEntityUpdate>().SelectMany(u => u.GenericData).Select(c => c.Packet.ReadInt()));
            pending = next;
        }
        Check(actions.SequenceEqual(Enumerable.Range(0, 200)), "retry batching lost, replayed or reordered original FSM actions");
    }
}
