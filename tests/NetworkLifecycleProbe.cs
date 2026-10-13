using System;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Threading;
using Hkmp.Networking;
using Hkmp.Networking.Client;
using Hkmp.Networking.Packet;
using Hkmp.Networking.Server;

internal static class NetworkLifecycleProbe {
    private static T Field<T>(object o, string name) => (T)o.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic).GetValue(o);
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
