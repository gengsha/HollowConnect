using System;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Hkmp.Networking.Client;
using Hkmp.Networking.Server;

internal static class TransportProbe {
    public static void Run() {
        var server = new DtlsServer();
        server.DataReceivedEvent += (client, bytes, count) => client.DtlsTransport.Send(bytes, 0, count);
        server.Start(0);
        var socket = (Socket)typeof(DtlsServer).GetField("socket", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(server);
        var port = ((IPEndPoint)socket.LocalEndPoint).Port;
        using (var begin = new ManualResetEventSlim()) {
            var clients = Enumerable.Range(1, 5).Select(_ => new DtlsClient()).ToArray();
            try {
                var jobs = clients.Select((client, index) => Task.Factory.StartNew(() => {
                    begin.Wait();
                    using (var reply = new ManualResetEventSlim()) {
                        client.DataReceivedEvent += (bytes, count) => { if (count == 2 && bytes[0] == index && bytes[1] == 117) reply.Set(); };
                        client.Connect("127.0.0.1", port);
                        var payload = new[] { (byte)index, (byte)117 };
                        client.DtlsTransport.Send(payload, 0, payload.Length);
                        if (!reply.Wait(5000)) throw new Exception("DTLS response lost or delivered to wrong peer " + index);
                    }
                }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default)).ToArray();
                begin.Set();
                if (!Task.WaitAll(jobs, 20000)) throw new Exception("Concurrent DTLS handshakes timed out");
            } finally {
                foreach (var client in clients) client.Disconnect();
                server.Stop();
            }
        }
        // A new listener after cleanup must accept another connection.
        server.Start(0);
        try {
            socket = (Socket)typeof(DtlsServer).GetField("socket", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(server);
            var reconnect = new DtlsClient();
            try { reconnect.Connect("127.0.0.1", ((IPEndPoint)socket.LocalEndPoint).Port); }
            finally { reconnect.Disconnect(); }
        } finally { server.Stop(); }
    }
}
