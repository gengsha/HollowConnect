using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Runtime.Serialization;
using Hkmp.Networking;
using Hkmp.Networking.Client;
using Hkmp.Networking.Packet;
using Hkmp.Networking.Packet.Data;
using Hkmp.Networking.Packet.Update;
using Hkmp.Networking.Server;
using MonoMod.RuntimeDetour;
using Org.BouncyCastle.Tls;

internal static class SelectiveAckProbe {
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
    private static readonly FieldInfo Elapsed = typeof(Stopwatch).GetField("elapsed", Private)
        ?? typeof(Stopwatch).GetField("_elapsed", Private);
    private static void Check(bool value, string message) { if (!value) throw new Exception(message); }
    private static void Age(Stopwatch stopwatch, long milliseconds) {
        stopwatch.Stop();
        Elapsed.SetValue(stopwatch, milliseconds * Stopwatch.Frequency / 1000);
    }

    // Drive the production send method, framing, decoding, ACK construction and
    // dispatch synchronously. Only the DTLS boundary and elapsed time are virtual;
    // timers cannot change the packet phase under test.
    private sealed class Endpoint<TPacket, TId> where TPacket : UpdatePacket<TId>, new() where TId : Enum {
        internal readonly UdpUpdateManager<TPacket, TId> Manager;
        internal readonly DtlsTransport Transport = (DtlsTransport)FormatterServices.GetUninitializedObject(typeof(DtlsTransport));
        internal readonly List<ushort> ReliableCarriers = new();
        private readonly Type type = typeof(UdpUpdateManager<TPacket, TId>);
        private readonly Dictionary<ushort, long> sentAt = new();
        private readonly List<byte[]> current = new();
        private readonly DatagramReassembler reassembler = new();
        private List<byte[]> held;
        internal Action<byte[]> Deliver;
        internal int Messages, Dropped, Reordered, Duplicated;
        private readonly bool faults;

        internal Endpoint(UdpUpdateManager<TPacket, TId> manager, ushort sequence, bool faults) {
            Manager = manager; this.faults = faults;
            manager.DtlsTransport = Transport;
            type.GetField("_localSequence", Private).SetValue(manager, sequence);
            type.GetField("_isUpdating", Private).SetValue(manager, true);
        }
        internal TPacket Pending => (TPacket)type.GetField("CurrentUpdatePacket", Private).GetValue(Manager);
        internal void Send(long now) {
            var congestion = (UdpCongestionManager<TPacket, TId>)type.GetField("_udpCongestionManager", Private).GetValue(Manager);
            var queue = (ConcurrentDictionary<ushort, SentPacket<TPacket, TId>>)congestion.GetType().GetField("_sentQueue", Private).GetValue(congestion);
            foreach (var pair in queue) Age(pair.Value.Stopwatch, now - sentAt[pair.Key]);
            var sequence = (ushort)type.GetField("_localSequence", Private).GetValue(Manager);
            sentAt[sequence] = now;
            type.GetMethod("CreateAndSendUpdatePacket", Private).Invoke(Manager, null);
        }
        internal void Frame(byte[] buffer, int offset, int length) {
            var frame = new byte[length]; Buffer.BlockCopy(buffer, offset, frame, 0, length);
            Check(DatagramFraming.ReadHeader(frame, length, out _, out _, out var index, out var count), "invalid outbound frame");
            if (index == 0) { Messages++; current.Clear(); }
            current.Add(frame);
            if (index != count - 1) return;
            var decoded = new TPacket();
            var bytes = current.SelectMany(f => f.Skip(DatagramFraming.HeaderSize)).ToArray();
            Check(decoded.ReadPacket(PacketManager.ReadDatagram(bytes)), "outbound datagram did not decode");
            if (decoded.GetPacketData().Count != 0 || decoded.GetAddonPacketData().Count != 0) ReliableCarriers.Add(decoded.Sequence);
            // Exactly the retained Smoke periodic schedule, including reverse ACK traffic.
            var remainder = Messages % 13;
            if (faults && (remainder == 0 || remainder == 1 && Messages > 1)) { Dropped++; return; }
            if (faults && Messages % 9 == 0 && held == null) { held = new List<byte[]>(current); return; }
            Emit(current);
            if (faults && Messages % 17 == 0) { Emit(current); Duplicated++; }
            if (held != null) { Emit(held); held = null; Reordered++; }
        }
        private void Emit(IEnumerable<byte[]> frames) {
            foreach (var frame in frames) if (reassembler.Accept(frame, frame.Length, out var message)) Deliver(message);
        }
        internal void Stop() { Manager.DtlsTransport = null; Manager.StopUpdates(); }
    }

    internal static void PeriodicDelivery(ushort sequence, int lostStep, int interval = 17) {
        var client = new Endpoint<ServerUpdatePacket, ServerUpdatePacketId>(new ClientUpdateManager(), sequence, true);
        var server = new Endpoint<ClientUpdatePacket, ClientUpdatePacketId>(new ServerUpdateManager(), sequence, true);
        var packets = new PacketManager(); var clientEnters = 0; var serverEnters = 0;
        const string scene = "GG_Mega_Moss_Charger";
        packets.RegisterServerUpdatePacketHandler<ServerPlayerEnterScene>(ServerUpdatePacketId.PlayerEnterScene, (_, data) => {
            Check(data.NewSceneName == scene && data.Visit == 2, "server enter payload changed"); serverEnters++;
        });
        packets.RegisterClientUpdatePacketHandler<ClientPlayerEnterScene>(ClientUpdatePacketId.PlayerEnterScene, data => {
            Check(data.SceneName == scene && data.Visit == 2, "client enter payload changed"); clientEnters++;
        });
        client.Deliver = bytes => {
            var packet = new ServerUpdatePacket(); Check(packet.ReadPacket(PacketManager.ReadDatagram(bytes)), "server decode failed");
            server.Manager.OnReceivePacket<ServerUpdatePacket, ServerUpdatePacketId>(packet);
            packets.HandleServerUpdatePacket(2, packet);
        };
        server.Deliver = bytes => {
            var packet = new ClientUpdatePacket(); Check(packet.ReadPacket(PacketManager.ReadDatagram(bytes)), "client decode failed");
            client.Manager.OnReceivePacket<ClientUpdatePacket, ClientUpdatePacketId>(packet);
            packets.HandleClientUpdatePacket(packet); ClientSessionProbe.DrainMainThread();
        };
        using (var hook = new Hook(typeof(DtlsTransport).GetMethod("Send", new[] { typeof(byte[]), typeof(int), typeof(int) }),
            (Action<Action<DtlsTransport, byte[], int, int>, DtlsTransport, byte[], int, int>)((orig, peer, buffer, offset, length) => {
                if (ReferenceEquals(peer, client.Transport)) client.Frame(buffer, offset, length);
                else if (ReferenceEquals(peer, server.Transport)) server.Frame(buffer, offset, length);
                else orig(peer, buffer, offset, length);
            }))) {
            try {
                for (var step = 0; step < lostStep + 130; step++) {
                    if (step == lostStep) {
                        client.Pending.SetSendingPacketData(ServerUpdatePacketId.PlayerEnterScene,
                            new ServerPlayerEnterScene { NewSceneName = scene, SceneName = scene, Visit = 2, Position = Hkmp.Math.Vector2.Zero });
                        var enters = new PacketDataCollection<ClientPlayerEnterScene>();
                        enters.DataInstances.Add(new ClientPlayerEnterScene { Id = 2, Username = "probe", SceneName = scene, Visit = 2, Position = Hkmp.Math.Vector2.Zero });
                        server.Pending.SetSendingPacketData(ClientUpdatePacketId.PlayerEnterScene, enters);
                    }
                    client.Send(step * interval); server.Send(step * interval);
                }
                Check(serverEnters == 1 && clientEnters == 1,
                    $"periodic loss starved or replayed enter: server={serverEnters} client={clientEnters}; " +
                    "client carriers=" + string.Join(",", client.ReliableCarriers) + "; server carriers=" + string.Join(",", server.ReliableCarriers));
                Check(client.Dropped > 0 && server.Dropped > 0 && client.Reordered > 0 && server.Reordered > 0 &&
                    client.Duplicated > 0 && server.Duplicated > 0, "fault schedule was not exercised in both directions");
                Check(client.ReliableCarriers.Count <= 3 && server.ReliableCarriers.Count <= 3, "delivered enter continued to retransmit");
            } finally { client.Stop(); server.Stop(); ClientSessionProbe.DrainMainThread(); }
        }
    }

    private sealed class Capture : UdpUpdateManager<ServerUpdatePacket, ServerUpdatePacketId> {
        internal int Resends;
        public override void ResendReliableData(ServerUpdatePacket lost) { Resends++; CurrentUpdatePacket.SetLostReliableData(lost); }
    }
    internal static void AckEvidence(ushort sequence) {
        var manager = new Capture(); var congestion = new UdpCongestionManager<ServerUpdatePacket, ServerUpdatePacketId>(manager);
        var original = new ServerUpdatePacket { Sequence = sequence };
        original.SetSendingPacketData(ServerUpdatePacketId.PlayerEnterScene, new ServerPlayerEnterScene { NewSceneName = "probe", Position = Hkmp.Math.Vector2.Zero });
        original.CreatePacket(new Packet()); congestion.OnSendPacket(sequence, original);
        var ack = new ClientUpdatePacket { Ack = unchecked((ushort)(sequence + 3)) };
        for (var i = 0; i < 5; i++) congestion.OnReceivePackets<ClientUpdatePacket, ClientUpdatePacketId>(ack);
        Check(manager.Resends == 0, "duplicate ACKs were counted as different delivered packets");
        ack.AckField[0] = true; congestion.OnReceivePackets<ClientUpdatePacket, ClientUpdatePacketId>(ack);
        Check(manager.Resends == 0, "two newer deliveries triggered recovery of a reordered packet");
        var stale = new ClientUpdatePacket { Ack = unchecked((ushort)(sequence - 1)) }; stale.AckField[0] = stale.AckField[1] = true;
        congestion.OnReceivePackets<ClientUpdatePacket, ClientUpdatePacketId>(stale);
        Check(manager.Resends == 0, "older ACKs triggered recovery across sequence wrap");
        ack.AckField[1] = true; congestion.OnReceivePackets<ClientUpdatePacket, ClientUpdatePacketId>(ack);
        Check(manager.Resends == 1, "three distinct newer deliveries did not recover the missing reliable packet");
        congestion.OnReceivePackets<ClientUpdatePacket, ClientUpdatePacketId>(ack);
        congestion.OnSendPacket(unchecked((ushort)(sequence + 4)), new ServerUpdatePacket());
        Check(manager.Resends == 1, "one lost packet was scheduled repeatedly by duplicate ACKs and send ticks");

        manager = new Capture(); congestion = new UdpCongestionManager<ServerUpdatePacket, ServerUpdatePacketId>(manager);
        congestion.OnSendPacket(sequence, original);
        ack.AckField[2] = true; congestion.OnReceivePackets<ClientUpdatePacket, ClientUpdatePacketId>(ack);
        Check(manager.Resends == 0, "an acknowledged packet was also queued for recovery");
    }
    internal static void TimeoutFallback() {
        var manager = new Capture(); var congestion = new UdpCongestionManager<ServerUpdatePacket, ServerUpdatePacketId>(manager);
        congestion.OnReceivePackets<ClientUpdatePacket, ClientUpdatePacketId>(new ClientUpdatePacket());
        var packet = new ServerUpdatePacket { Sequence = 9 };
        packet.SetSendingPacketData(ServerUpdatePacketId.PlayerEnterScene, new ServerPlayerEnterScene { NewSceneName = "probe", Position = Hkmp.Math.Vector2.Zero });
        packet.CreatePacket(new Packet()); congestion.OnSendPacket(9, packet);
        var queue = (ConcurrentDictionary<ushort, SentPacket<ServerUpdatePacket, ServerUpdatePacketId>>)congestion.GetType().GetField("_sentQueue", Private).GetValue(congestion);
        Age(queue[9].Stopwatch, 199); congestion.OnSendPacket(10, new ServerUpdatePacket());
        Check(manager.Resends == 0, "recovery shortened the no-evidence timeout");
        Age(queue[9].Stopwatch, 201); congestion.OnSendPacket(11, new ServerUpdatePacket());
        Check(manager.Resends == 1, "loss without newer ACK evidence lost the existing timeout fallback");
    }
}
