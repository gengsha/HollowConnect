using System;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Threading;
using Hkmp.Networking;
using Hkmp.Networking.Client;
using Hkmp.Networking.Packet;
using Hkmp.Networking.Packet.Data;
using Hkmp.Networking.Packet.Update;
using Hkmp.Networking.Server;

internal static class NetworkLifecycleProbe {
    private static T Field<T>(object o, string name) => (T)o.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic).GetValue(o);
    public static void ClosedTransport() {
        var manager = new PacketManager(); var server = new NetServer(manager); var client = new DtlsClient();
        var peers = Field<ConcurrentDictionary<ushort, NetServerClient>>(server, "_clientsById");
        var snapshot = false; var closed = 0;
        // Reflection keeps this regression runnable against the old server,
        // where the close event does not exist yet.
        typeof(NetServer).GetEvent("ClientTransportClosedEvent")?.AddEventHandler(server, (Action<ushort>)(_ => {
            if (!snapshot) throw new Exception("Transport retirement overtook the final queued snapshot");
            Interlocked.Increment(ref closed);
        }));
        manager.RegisterServerUpdatePacketHandler<ReliableEntityUpdate>(ServerUpdatePacketId.ReliableEntityUpdate,
            (id, update) => snapshot = peers.ContainsKey(id) && update.Id == 17 && update.IsActive);
        server.Start(0);
        uint messageId = 0;
        void Send(ServerUpdatePacket packet) {
            var bytes = new Packet(); packet.CreatePacket(bytes);
            foreach (var frame in DatagramFraming.Fragment(bytes.ToArray(), messageId++))
                client.DtlsTransport.Send(frame, 0, frame.Length);
        }
        try {
            var dtls = Field<DtlsServer>(server, "_dtlsServer");
            var port = ((IPEndPoint)Field<Socket>(dtls, "socket").LocalEndPoint).Port;
            client.Connect("127.0.0.1", port); Send(new ServerUpdatePacket());
            if (!SpinWait.SpinUntil(() => peers.Count == 1, 2000)) throw new Exception("Close probe did not register its peer");
            foreach (var peer in peers.Values) peer.IsRegistered = true;
            var final = new ServerUpdatePacket { Sequence = 1 };
            var updates = new PacketDataCollection<ReliableEntityUpdate>();
            var update = new ReliableEntityUpdate { Id = 17, IsActive = true }; update.UpdateTypes.Add(EntityUpdateType.Active);
            updates.DataInstances.Add(update); final.SetSendingPacketData(ServerUpdatePacketId.ReliableEntityUpdate, updates);
            Send(final); client.Disconnect(); // Deliberately omit the application's disconnect packet.
            if (!SpinWait.SpinUntil(() => peers.IsEmpty, 1000))
                throw new Exception("Closed DTLS peer remains registered until the 5-second heartbeat timeout");
            if (!snapshot) throw new Exception("Final snapshot was lost before transport retirement");
            if (!Field<ConcurrentDictionary<IPEndPoint, NetServerClient>>(server, "_clientsByEndPoint").IsEmpty)
                throw new Exception("Closed transport retained an endpoint mapping");
            if (typeof(NetServer).GetEvent("ClientTransportClosedEvent") != null &&
                !SpinWait.SpinUntil(() => Volatile.Read(ref closed) == 1, 1000))
                throw new Exception("Transport close did not notify the room exactly once");
        } finally { client.Disconnect(); server.Stop(); }
    }
    public static void Run() {
        var server = new NetServer(new PacketManager());
        var dtls = Field<DtlsServer>(server, "_dtlsServer");
        server.Start(0);
        var generation = server.Generation;
        var port = ((IPEndPoint)Field<Socket>(dtls, "socket").LocalEndPoint).Port;
        var client = new DtlsClient();
        try {
            client.Connect("127.0.0.1", port);
            foreach (var frame in DatagramFraming.Fragment(new byte[] { 0, 0, 0, 0 }, 0))
                client.DtlsTransport.Send(frame, 0, frame.Length);
            if (!SpinWait.SpinUntil(() => Field<ConcurrentDictionary<ushort, NetServerClient>>(server, "_clientsById").Count == 1, 3000))
                throw new Exception("NetServer client was not created");
            client.Disconnect();
            Thread.Sleep(200);
            server.Stop(); // A retired DTLS session must not abort cleanup.
            server.Start(port); // Reuse the exact same port in the same process.
            if (server.Generation == generation) throw new Exception("Reopened room retained its generation");
            var next = new DtlsClient();
            try { next.Connect("127.0.0.1", port); }
            finally { next.Disconnect(); }
            server.Stop();
            server.Stop(); // Shutdown is idempotent, including before restart.
        } finally {
            client.Disconnect();
            try { if (server.IsStarted) server.Stop(); } finally { dtls.Stop(); }
        }
    }

    public static void FailedBind() {
        var server = new NetServer(new PacketManager());
        using (var occupied = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp)) {
            occupied.Bind(new IPEndPoint(IPAddress.Any, 0));
            var port = ((IPEndPoint)occupied.LocalEndPoint).Port;
            try { server.Start(port); throw new Exception("Expected bind failure"); }
            catch (SocketException) { }
            if (server.IsStarted) throw new Exception("Bind failure left server marked as started");
            server.Stop(); occupied.Close();
            try { server.Start(port); }
            finally { server.Stop(); }
        }
    }
}
