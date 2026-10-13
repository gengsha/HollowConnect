using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Hkmp.Networking;
using Hkmp.Networking.Client;
using Hkmp.Networking.Packet;
using Hkmp.Networking.Packet.Data;
using Hkmp.Networking.Packet.Update;
using Hkmp.Networking.Server;

internal static class NetworkTrafficProbe {
    private static T Field<T>(object o, Type type, string name) => (T)type.GetField(name, BindingFlags.Instance | BindingFlags.NonPublic).GetValue(o);
    private static byte[] Update(ushort sequence, bool large) {
        var update = new ServerUpdatePacket { Sequence = sequence };
        if (large) {
            var entities = new PacketDataCollection<EntityUpdate>();
            for (ushort i = 0; i < 100; i++) {
                var entity = new EntityUpdate { Id = i, SceneName = "Crossroads_05", Visit = 1 };
                entity.UpdateTypes.Add(EntityUpdateType.Position);
                entity.Position = new Hkmp.Math.Vector2(i, sequence);
                entities.DataInstances.Add(entity);
            }
            update.SetSendingPacketData(ServerUpdatePacketId.EntityUpdate, entities);
        }
        var packet = new Packet(); update.CreatePacket(packet); return packet.ToArray();
    }
    public static void Run() {
        var server = new NetServer(new PacketManager());
        var clients = Enumerable.Range(0, 5).Select(_ => new DtlsClient()).ToArray();
        server.Start(0);
        var dtls = Field<DtlsServer>(server, typeof(NetServer), "_dtlsServer");
        var port = ((IPEndPoint)Field<Socket>(dtls, typeof(DtlsServer), "socket").LocalEndPoint).Port;
        try {
            var jobs = clients.Select((client, p) => Task.Factory.StartNew(() => {
                client.Connect("127.0.0.1", port);
                uint messageId = 0;
                void Send(ushort sequence, bool large, bool lose) {
                    var frames = DatagramFraming.Fragment(Update(sequence, large), messageId++).Reverse().ToArray();
                    if (large && frames.Length < 2) throw new Exception("Traffic probe did not fragment");
                    foreach (var frame in frames.Skip(lose ? 1 : 0)) client.DtlsTransport.Send(frame, 0, frame.Length);
                }
                for (ushort n = 0; n < 30; n++) {
                    Send((ushort)(1000 * (p + 1) + 2 * n), true, n % 3 == 0);
                    Send((ushort)(1000 * (p + 1) + 2 * n + 1), false, false);
                    Thread.Sleep(5);
                }
                // Repeat only the final heartbeat to allow normal UDP loss on a loaded test machine.
                for (var n = 0; n < 5; n++) { Send((ushort)(1000 * (p + 1) + 99), false, false); Thread.Sleep(10); }
            }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default)).ToArray();
            if (!Task.WaitAll(jobs, 15000)) throw new Exception("Concurrent fragmented DTLS traffic stalled");
            var registered = Field<ConcurrentDictionary<ushort, NetServerClient>>(server, typeof(NetServer), "_clientsById");
            if (!SpinWait.SpinUntil(() => registered.Count == 5 && registered.Values.All(c =>
                Field<ushort>(c.UpdateManager, typeof(UdpUpdateManager<ClientUpdatePacket, ClientUpdatePacketId>), "_remoteSequence") % 1000 == 99), 2000))
                throw new Exception("Interleaved or missing fragments blocked a peer's final heartbeat");
        } finally {
            foreach (var client in clients) client.Disconnect();
            server.Stop();
        }
    }
}
