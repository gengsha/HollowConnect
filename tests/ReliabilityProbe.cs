using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Hkmp.Networking;
using Hkmp.Networking.Client;
using Hkmp.Networking.Packet;
using Hkmp.Networking.Packet.Data;
using Hkmp.Networking.Packet.Update;
using Hkmp.Networking.Server;
using HollowConnect;
using HollowConnect.Core;

internal static class ReliabilityProbe {
    private static void Check(bool condition, string message) {
        if (!condition) throw new Exception(message);
    }

    private static ServerUpdatePacket Events(ushort sequence, byte serial) {
        var packet = new ServerUpdatePacket { Sequence = sequence };
        var update = new ReliableEntityUpdate { Id = 17, IsActive = serial == 3 };
        update.UpdateTypes.UnionWith(new[] { EntityUpdateType.Active, EntityUpdateType.HostFsm, EntityUpdateType.Data });
        var state = new EntityHostFsmData { CurrentState = serial };
        state.Types.Add(EntityHostFsmData.Type.State);
        update.HostFsmData[0] = state;
        var action = new EntityNetworkData(); action.Packet.Write(serial);
        update.GenericData.Add(action);
        var collection = new PacketDataCollection<ReliableEntityUpdate>();
        collection.DataInstances.Add(update);
        packet.SetSendingPacketData(ServerUpdatePacketId.ReliableEntityUpdate, collection);
        return packet;
    }

    private static ServerUpdatePacket Wire(ServerUpdatePacket outgoing) {
        var bytes = new Packet(); outgoing.CreatePacket(bytes);
        var incoming = new ServerUpdatePacket();
        Check(incoming.ReadPacket(PacketManager.ReadDatagram(bytes.ToArray())), "reliability datagram did not decode");
        return incoming;
    }

    private static byte[] Serials(ServerUpdatePacket packet) {
        var serials = new List<byte>(); var manager = new PacketManager();
        manager.RegisterServerUpdatePacketHandler<ReliableEntityUpdate>(ServerUpdatePacketId.ReliableEntityUpdate,
            (_, update) => serials.Add(update.GenericData[0].Packet.ReadByte()));
        manager.HandleServerUpdatePacket(2, packet);
        return serials.ToArray();
    }

    internal static void NestedResends(ushort sequence) {
        var first = Events(unchecked((ushort)(sequence - 2)), 1);
        var second = Events(unchecked((ushort)(sequence - 1)), 2); second.SetLostReliableData(first);
        // Both the original and its carrier can be marked lost in the same send.
        // Also exercise caching before the carrier itself is retransmitted.
        second.GetPacketData();
        var third = Events(sequence, 3); third.SetLostReliableData(second); third.SetLostReliableData(first);
        var received = Wire(third);
        Check(Serials(received).SequenceEqual(new byte[] { 1, 2, 3 }),
            "nested retransmission duplicated or relabeled the original action");
        received = Wire(third);
        received.DropDuplicateResendData(new Queue<ushort>(new[] { first.Sequence }));
        Check(Serials(received).SequenceEqual(new byte[] { 2, 3 }),
            "already consumed origin survived inside its retransmission carrier");
    }

    internal static void NestedAddonResends() {
        const byte addonId = 242;
        BasePacket<ServerUpdatePacketId>.AddonPacketInfoDict[addonId] = new AddonPacketInfo(
            _ => new PacketDataCollection<Message>(), 1);
        try {
            ServerUpdatePacket Addon(ushort sequence, uint serial) {
                var packet = new ServerUpdatePacket { Sequence = sequence };
                var addon = new AddonPacketData(1); var collection = new PacketDataCollection<Message>();
                collection.DataInstances.Add(new Message { Kind = Kind.Down, Serial = serial });
                addon.PacketData[0] = collection; packet.SetSendingAddonPacketData(addonId, addon);
                return packet;
            }
            var first = Addon(ushort.MaxValue, 1);
            var second = Addon(0, 2); second.SetLostReliableData(first); second.GetAddonPacketData();
            var third = Addon(1, 3); third.SetLostReliableData(second); third.SetLostReliableData(first);
            var received = Wire(third);
            received.DropDuplicateResendData(new Queue<ushort>(new[] { first.Sequence }));
            var serials = new List<uint>(); var manager = new PacketManager();
            manager.RegisterServerAddonUpdatePacketHandler(addonId, 0, (_, data) => serials.Add(((Message)data).Serial));
            manager.HandleServerUpdatePacket(2, received);
            Check(serials.SequenceEqual(new uint[] { 2, 3 }),
                "nested addon retransmission lost its original identity or duplicated an event");
        } finally { BasePacket<ServerUpdatePacketId>.AddonPacketInfoDict.Remove(addonId); }
    }

    internal static void RepeatedOrigins(ushort sequence, int heartbeats) {
        var receiver = new ServerUpdateManager(); var manager = new PacketManager();
        var serials = new List<byte>();
        manager.RegisterServerUpdatePacketHandler<ReliableEntityUpdate>(ServerUpdatePacketId.ReliableEntityUpdate,
            (_, update) => serials.Add(update.GenericData[0].Packet.ReadByte()));
        void Deliver(ServerUpdatePacket outgoing) {
            var packet = Wire(outgoing);
            receiver.OnReceivePacket<ServerUpdatePacket, ServerUpdatePacketId>(packet);
            manager.HandleServerUpdatePacket(2, packet);
        }
        receiver.StartUpdates();
        try {
            var first = Events(sequence, 1);
            var second = Events(unchecked((ushort)(sequence + 1)), 2); second.SetLostReliableData(first);
            Deliver(second); // The original datagram was lost; its actions arrive via this carrier.
            for (var i = 0; i < heartbeats; i++)
                Deliver(new ServerUpdatePacket { Sequence = unchecked((ushort)(sequence + 2 + i)) });
            var third = Events(unchecked((ushort)(sequence + 2 + heartbeats)), 3); third.SetLostReliableData(first);
            Deliver(third);
            Check(serials.SequenceEqual(new byte[] { 1, 2, 3 }),
                "an origin already delivered by a resend executed again after " + heartbeats + " heartbeats");
            Deliver(first); Deliver(second); Deliver(third);
            Check(serials.SequenceEqual(new byte[] { 1, 2, 3 }),
                "a delayed original or duplicate carrier replayed consumed actions");
            receiver.StopUpdates(); receiver.StartUpdates(); Deliver(first);
            Check(serials.SequenceEqual(new byte[] { 1, 2, 3, 1 }),
                "a fresh session inherited the previous session's consumed origins");
        } finally { receiver.StopUpdates(); }
    }

    internal static void RepeatedAddonOrigins() {
        const byte addonId = 242;
        BasePacket<ServerUpdatePacketId>.AddonPacketInfoDict[addonId] = new AddonPacketInfo(
            _ => new PacketDataCollection<Message>(), 1);
        var receiver = new ServerUpdateManager(); var manager = new PacketManager(); var serials = new List<uint>();
        var builtIn = new List<byte>();
        manager.RegisterServerUpdatePacketHandler<ReliableEntityUpdate>(ServerUpdatePacketId.ReliableEntityUpdate,
            (_, update) => builtIn.Add(update.GenericData[0].Packet.ReadByte()));
        manager.RegisterServerAddonUpdatePacketHandler(addonId, 0, (_, data) => serials.Add(((Message)data).Serial));
        ServerUpdatePacket Addon(ushort sequence, uint serial) {
            var packet = Events(sequence, (byte)serial); var addon = new AddonPacketData(1);
            var collection = new PacketDataCollection<Message>();
            collection.DataInstances.Add(new Message { Kind = Kind.Down, Serial = serial });
            addon.PacketData[0] = collection; packet.SetSendingAddonPacketData(addonId, addon); return packet;
        }
        void Deliver(ServerUpdatePacket outgoing) {
            var packet = Wire(outgoing); receiver.OnReceivePacket<ServerUpdatePacket, ServerUpdatePacketId>(packet);
            manager.HandleServerUpdatePacket(2, packet);
        }
        receiver.StartUpdates();
        try {
            var first = Addon(ushort.MaxValue, 1);
            var second = Addon(0, 2); second.SetLostReliableData(first); Deliver(second);
            var third = Addon(1, 3); third.SetLostReliableData(first); Deliver(third);
            Deliver(first); Deliver(third);
            Check(serials.SequenceEqual(new uint[] { 1, 2, 3 }),
                "addon origin delivered by a resend or delayed original executed more than once");
            Check(builtIn.SequenceEqual(new byte[] { 1, 2, 3 }),
                "consuming an origin discarded either its built-in or addon payload");
        } finally { receiver.StopUpdates(); BasePacket<ServerUpdatePacketId>.AddonPacketInfoDict.Remove(addonId); }
    }

    internal static void ReorderedSnapshots(ushort sequence, bool viaResend) {
        var receiver = new ServerUpdateManager(); var manager = new PacketManager();
        var events = new List<byte>(); var active = false; var merged = new EntityHostFsmData();
        var secondFsm = new EntityHostFsmData(); var position = 0f; var scale = 0f;
        manager.RegisterServerUpdatePacketHandler<ReliableEntityUpdate>(ServerUpdatePacketId.ReliableEntityUpdate,
            (_, update) => {
                if (update.UpdateTypes.Contains(EntityUpdateType.Active)) active = update.IsActive;
                foreach (var fsm in update.HostFsmData) (fsm.Key == 0 ? merged : secondFsm).MergeData(fsm.Value);
                foreach (var action in update.GenericData) events.Add(action.Packet.ReadByte());
            });
        manager.RegisterServerUpdatePacketHandler<EntityUpdate>(ServerUpdatePacketId.EntityUpdate, (_, update) => {
            if (update.UpdateTypes.Contains(EntityUpdateType.Position)) position = update.Position.X;
            if (update.UpdateTypes.Contains(EntityUpdateType.Scale)) scale = update.Scale.xScale;
        });
        ServerUpdatePacket Snapshot(ushort seq, byte serial) {
            var packet = Events(seq, serial);
            packet.TryGetSendingPacketData(ServerUpdatePacketId.ReliableEntityUpdate, out var raw);
            var update = (ReliableEntityUpdate)((RawPacketDataCollection)raw).DataInstances[0];
            var fsm = update.HostFsmData[0]; fsm.Types.Add(EntityHostFsmData.Type.Floats); fsm.Floats[0] = serial;
            if (serial == 1) {
                // A newer state does not supersede fields it never included.
                fsm.Floats[1] = 11;
                var other = new EntityHostFsmData { CurrentState = 7 }; other.Types.Add(EntityHostFsmData.Type.State);
                update.HostFsmData[1] = other;
            }
            var motion = new EntityUpdate { Id = update.Id, Position = new Hkmp.Math.Vector2(serial, 0) };
            motion.UpdateTypes.Add(EntityUpdateType.Position);
            if (serial == 1) {
                motion.UpdateTypes.Add(EntityUpdateType.Scale); motion.Scale.xScale = 2; motion.Scale.x = true;
            }
            var collection = new PacketDataCollection<EntityUpdate>(); collection.DataInstances.Add(motion);
            packet.SetSendingPacketData(ServerUpdatePacketId.EntityUpdate, collection);
            return packet;
        }
        void Deliver(ServerUpdatePacket outgoing) {
            var packet = Wire(outgoing); receiver.OnReceivePacket<ServerUpdatePacket, ServerUpdatePacketId>(packet);
            manager.HandleServerUpdatePacket(2, packet);
        }
        receiver.StartUpdates();
        try {
            var first = Snapshot(unchecked((ushort)(sequence - 2)), 1);
            var second = Snapshot(unchecked((ushort)(sequence - 1)), 2);
            Deliver(Snapshot(sequence, 3));
            if (viaResend) {
                var carrier = new ServerUpdatePacket { Sequence = unchecked((ushort)(sequence + 1)) };
                carrier.SetLostReliableData(second); carrier.SetLostReliableData(first); Deliver(carrier);
            } else { Deliver(first); Deliver(second); }
            Check(active && merged.CurrentState == 3 && merged.Floats[0] == 3,
                "an independently delayed datagram or resend rolled back newer active/FSM fields");
            Check(merged.Floats[1] == 11 && secondFsm.CurrentState == 7,
                "rejecting stale snapshots dropped untouched FSM variables or another FSM");
            Check(events.OrderBy(x => x).SequenceEqual(new byte[] { 1, 2, 3 }),
                "rejecting stale snapshots lost or duplicated reliable action events");
            if (!viaResend) Check(position == 3 && scale == 2,
                "motion fields rolled back, or an untouched scale field was discarded");
        } finally { receiver.StopUpdates(); }
    }

    internal static void CompleteSequenceWrap() {
        var receiver = new ServerUpdateManager(); var manager = new PacketManager(); var serials = new List<byte>();
        manager.RegisterServerUpdatePacketHandler<ReliableEntityUpdate>(ServerUpdatePacketId.ReliableEntityUpdate,
            (_, update) => serials.Add(update.GenericData[0].Packet.ReadByte()));
        void Deliver(ServerUpdatePacket outgoing) {
            var packet = Wire(outgoing); receiver.OnReceivePacket<ServerUpdatePacket, ServerUpdatePacketId>(packet);
            manager.HandleServerUpdatePacket(2, packet);
        }
        receiver.StartUpdates();
        try {
            Deliver(Events(22, 1));
            for (var i = 1; i < 65536; i++) Deliver(new ServerUpdatePacket { Sequence = unchecked((ushort)(22 + i)) });
            Deliver(Events(22, 2)); Deliver(Events(23, 3));
            Check(serials.SequenceEqual(new byte[] { 1, 2, 3 }),
                "sequence reuse after a complete wrap was mistaken for an old origin");
        } finally { receiver.StopUpdates(); }
    }

    internal static void ClientReceivePath() {
        ClientSessionProbe.DrainMainThread();
        var manager = new PacketManager(); var client = new NetClient(manager); var serials = new List<byte>();
        var active = false; var snapshot = new EntityHostFsmData();
        manager.RegisterClientUpdatePacketHandler<ReliableEntityUpdate>(ClientUpdatePacketId.ReliableEntityUpdate, update => {
            if (update.UpdateTypes.Contains(EntityUpdateType.Active)) active = update.IsActive;
            if (update.HostFsmData.TryGetValue(0, out var fsm)) snapshot.MergeData(fsm);
            foreach (var action in update.GenericData) serials.Add(action.Packet.ReadByte());
        });
        ClientUpdatePacket Outgoing(ushort sequence, byte serial, bool large = false) {
            var packet = new ClientUpdatePacket { Sequence = sequence };
            Events(sequence, serial).TryGetSendingPacketData(ServerUpdatePacketId.ReliableEntityUpdate, out var data);
            if (large) ((ReliableEntityUpdate)((RawPacketDataCollection)data).DataInstances[0]).GenericData[0].Packet.Write(new byte[4096]);
            packet.SetSendingPacketData(ClientUpdatePacketId.ReliableEntityUpdate, data); return packet;
        }
        uint messageId = 0;
        var receive = typeof(NetClient).GetMethod("OnReceiveData", BindingFlags.Instance | BindingFlags.NonPublic);
        void Deliver(ClientUpdatePacket outgoing) {
            var packet = new Packet(); outgoing.CreatePacket(packet);
            var frames = DatagramFraming.Fragment(packet.ToArray(), messageId++).Reverse().ToArray();
            foreach (var frame in frames) receive.Invoke(client, new object[] { frame, frame.Length });
            ClientSessionProbe.DrainMainThread();
        }
        typeof(NetClient).GetProperty("ConnectionStatus").SetValue(client, ClientConnectionStatus.Connected, null);
        client.UpdateManager.StartUpdates();
        try {
            var first = Outgoing(65534, 1); var carrier = Outgoing(65535, 2); carrier.SetLostReliableData(first);
            var latest = Outgoing(1, 3, true); Deliver(latest); Deliver(carrier); Deliver(first); Deliver(carrier); Deliver(latest);
            Check(serials.SequenceEqual(new byte[] { 3, 1, 2 }) && active && snapshot.CurrentState == 3,
                "client framing/receive/main-thread dispatch replayed actions or rolled back the latest state");
        } finally { client.Disconnect(); ClientSessionProbe.DrainMainThread(); }
    }
}
