using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using HollowConnect.Steam;
using HollowConnect.Steam.Bridge;
using Hkmp.Networking.Client;
using Hkmp.Networking.Server;

internal static class SteamBridgeTests {
    private static void Check(bool ok, string reason) { if (!ok) throw new Exception(reason); }
    private sealed class Link : ISteamBridgeLink {
        private readonly ConcurrentQueue<byte[]> incoming = new();
        internal Link Other;
        internal bool Closed, DropNextUnreliable;
        public string Description => "in-memory test link";
        public string CloseDescription => "test closed";
        public SteamBridgeLinkState State => Closed ? SteamBridgeLinkState.Closed : SteamBridgeLinkState.Connected;
        public bool Send(byte[] b, int length, bool reliable) {
            if (Closed || Other.Closed || Other.incoming.Count >= 1024) return false;
            if (!reliable && DropNextUnreliable) { DropNextUnreliable = false; return true; }
            Other.incoming.Enqueue(b.Take(length).ToArray()); return true;
        }
        public bool TryReceive(out byte[] data) => incoming.TryDequeue(out data);
        public void Close(int reason, string debug) { Closed = true; Other.Closed = true; }
        internal static Link Pair(out Link peer) { var local = new Link(); peer = new Link(); local.Other = peer; peer.Other = local; return local; }
    }

    internal static void Register(Action<string, Action> test) {
        test("Steam bridge rejects malformed flags UTF8 response versions and result codes", Protocol);
        test("Steam launch intents reject duplicate malformed and injected lobby arguments", Arguments);
        test("Steam lobby metadata binds original owner session and compatible version", Metadata);
        test("Steam cancelled operation generations cannot accept stale lobby results", Epochs);
        test("Steam bridge rejects old room tokens and bounds pending handshakes", Rejection);
        test("Steam bridge handshake timeout retires pending links", Timeout);
        test("Steam bridge preserves UDP boundaries loss semantics and local endpoint ownership", Relay);
        test("Steam bridge five real DTLS clients remain isolated and rehost with fresh aliases", Dtls);
        test("Steam listener binds loopback while existing direct listener still binds any address", BindAddresses);
    }

    private static void Protocol() {
        var hello = SteamBridgeProtocol.BuildHello("中文-room");
        Check(SteamBridgeProtocol.TryParseHello(hello, out var version, out var token) && version == 1 && token == "中文-room", "Unicode hello");
        hello[5] = 1; Check(!SteamBridgeProtocol.TryParseHello(hello, out _, out _), "Unsupported flags");
        hello = SteamBridgeProtocol.BuildHello("a"); hello[8] = 0xFF;
        Check(!SteamBridgeProtocol.TryParseHello(hello, out _, out _), "Malformed UTF8");
        var response = SteamBridgeProtocol.BuildResponse(SteamBridgeResult.Accepted);
        response[4]++; Check(!SteamBridgeProtocol.TryParseResponse(response, out _), "Response version ignored");
        response[4]--; response[5] = 255; Check(!SteamBridgeProtocol.TryParseResponse(response, out _), "Unknown result");
        var random = new Random(718);
        for (var i = 0; i < 2000; i++) { var bytes = new byte[random.Next(0, 1500)]; random.NextBytes(bytes);
            SteamBridgeProtocol.TryParseHello(bytes, out _, out _); SteamBridgeProtocol.TryParseResponse(bytes, out _); }
    }
    private static void Arguments() {
        Check(SteamRoomRules.TryArguments(new[] {"game.exe", "+connect_lobby", "109775241234567890"}, out var lobby) && lobby == 109775241234567890, "Cold start");
        foreach (var input in new[] {"+connect_lobby 0", "+connect_lobby -1", "+connect_lobby 1;cmd", "+connect_lobby 1 +connect_lobby 2", "+connect_lobby", "+connect_lobby 18446744073709551616"})
            Check(!SteamRoomRules.TryCommand(input, out _), "Accepted bad intent: " + input);
        Check(!SteamRoomRules.TryCommand(new string('x', 1025), out _), "Overlong intent");
    }
    private static void Metadata() {
        var nonce = Guid.NewGuid().ToString("N");
        bool Valid(string host, string session, string ready, string version, ulong owner) => SteamRoomRules.ValidMetadata(
            SteamRoomRules.Marker, SteamRoomRules.Protocol, version, host, session, ready, "build", owner, out _);
        Check(Valid("17", nonce, "1", "build", 17), "Good metadata");
        Check(!Valid("17", nonce, "1", "build", 18), "Lobby auto migration accepted");
        Check(!Valid("17", nonce, "0", "build", 17), "Closed room accepted");
        Check(!Valid("17", Guid.Empty.ToString("N"), "1", "build", 17), "Empty nonce");
        Check(!Valid("17", nonce, "1", "old", 17), "Version mismatch");
    }
    private static void Epochs() {
        var epoch = new SteamOperationEpoch(); var old = epoch.Begin(); epoch.Cancel(); var current = epoch.Begin();
        Check(!epoch.IsCurrent(old) && epoch.IsCurrent(current), "Cancel/rehost stale completion");
        epoch.Cancel(); Check(!epoch.IsCurrent(current), "Timeout stale completion");
    }
    private static void Rejection() {
        using (var socket = SteamBridgeUdp.CreateLoopbackSocket())
        using (var host = new SteamBridgeHostCore((IPEndPoint)socket.LocalEndPoint, "new-room", new SteamBridgeLimits {MaxConnections = 1, MaxPending = 1, SocketLingerMs = 0})) {
            var local = Link.Pair(out var remote); var other = Link.Pair(out var otherRemote); var excess = Link.Pair(out _);
            Check(host.TryAdd(remote) && host.TryAdd(otherRemote) && !host.TryAdd(excess), "Pending queue not bounded");
            using (var client = new SteamBridgeClientCore(local, "old-room", new SteamBridgeLimits {SocketLingerMs = 0})) {
                client.Pump(); host.Pump(); client.Pump();
                Check(client.State == SteamBridgeClientState.Failed && client.HostResult == SteamBridgeResult.WrongPassword, "Stale room reached UDP server");
                Check(socket.Available == 0 && host.ActiveCount == 0, "Rejected data forwarded");
            }
        }
    }
    private static void Timeout() {
        long now = 0;
        using (var socket = SteamBridgeUdp.CreateLoopbackSocket())
        using (var host = new SteamBridgeHostCore((IPEndPoint)socket.LocalEndPoint, "room", new SteamBridgeLimits {HelloTimeoutMs = 10, SocketLingerMs = 0}, () => now)) {
            Link.Pair(out var remote); host.TryAdd(remote); host.Pump(); now = 11; host.Pump();
            Check(host.TotalCount == 0 && remote.Closed, "Pending handshake did not time out");
        }
    }
    private static void Relay() {
        using (var server = SteamBridgeUdp.CreateLoopbackSocket())
        using (var local = SteamBridgeUdp.CreateLoopbackSocket())
        using (var rogue = SteamBridgeUdp.CreateLoopbackSocket())
        using (var host = new SteamBridgeHostCore((IPEndPoint)server.LocalEndPoint, "room", new SteamBridgeLimits {SocketLingerMs = 0})) {
            var link = Link.Pair(out var remote);
            using (var client = new SteamBridgeClientCore(link, "room", new SteamBridgeLimits {SocketLingerMs = 0})) {
                host.TryAdd(remote); client.Pump(); host.Pump(); client.Pump();
                Check(client.State == SteamBridgeClientState.Ready, "Handshake");
                var target = new IPEndPoint(IPAddress.Loopback, client.LocalPort);
                local.SendTo(new byte[] {1, 2, 3}, target); client.Pump(); host.Pump();
                var buffer = new byte[1500];
                Check(SteamBridgeUdp.TryReceive(server, buffer, out var count, out var peer) && count == 3 && buffer[2] == 3, "Boundary changed");
                Check(peer.Address.ToString() != "127.0.0.1" && IPAddress.IsLoopback(peer.Address), "Peer alias missing");
                server.SendTo(new byte[] {7, 8}, peer); host.Pump(); client.Pump();
                Check(SteamBridgeUdp.TryReceive(local, buffer, out count, out _) && count == 2 && buffer[0] == 7, "Reply lost");
                rogue.SendTo(new byte[] {44}, target); client.Pump(); host.Pump();
                Check(server.Available == 0, "Second local endpoint hijacked bridge");
                link.DropNextUnreliable = true;
                local.SendTo(new byte[] {9}, target); client.Pump(); host.Pump();
                Check(server.Available == 0, "Unreliable loss became reliable");
                var oversized = new byte[1401]; remote.Send(oversized, oversized.Length, false); client.Pump();
                Check(local.Available == 0, "Oversized remote datagram forwarded");
                local.SendTo(oversized, target); client.Pump(); host.Pump();
                Check(server.Available == 0, "Oversized local datagram forwarded");
            }
        }
    }
    private static void Dtls() {
        var aliases = new HashSet<string>();
        for (var round = 0; round < 2; round++) {
            var server = new DtlsServer();
            var seen = new ConcurrentDictionary<string, byte>();
            server.DataReceivedEvent += (peer, bytes, count) => {
                seen.TryAdd(peer.EndPoint.Address.ToString(), 0); peer.DtlsTransport.Send(bytes, 0, count);
            };
            server.Start(0, IPAddress.Loopback);
            var limits = new SteamBridgeLimits {SocketLingerMs = 0};
            var host = new SteamBridgeHostCore(new IPEndPoint(IPAddress.Loopback, server.BoundPort), "round" + round, limits);
            var hostWorker = new SteamBridgeWorker("test host", host);
            var clients = new List<DtlsClient>(); var workers = new List<SteamBridgeWorker>();
            hostWorker.Start();
            try {
                var ports = new List<int>();
                for (var i = 0; i < 5; i++) {
                    var link = Link.Pair(out var remote); Check(host.TryAdd(remote), "Host queue");
                    var bridge = new SteamBridgeClientCore(link, "round" + round, limits);
                    var worker = new SteamBridgeWorker("test guest " + i, bridge); workers.Add(worker); worker.Start();
                    Check(SpinWait.SpinUntil(() => bridge.State == SteamBridgeClientState.Ready, 3000), "Bridge not ready");
                    ports.Add(bridge.LocalPort); clients.Add(new DtlsClient());
                }
                var jobs = clients.Select((client, index) => Task.Factory.StartNew(() => {
                    using (var reply = new ManualResetEventSlim()) {
                        client.DataReceivedEvent += (bytes, count) => { if (count == 1300 && bytes[0] == index && bytes[1299] == 117) reply.Set(); };
                        client.Connect("127.0.0.1", ports[index]);
                        var payload = new byte[1300]; payload[0] = (byte)index; payload[1299] = 117;
                        client.DtlsTransport.Send(payload, 0, payload.Length);
                        Check(reply.Wait(5000), "DTLS reply wrong/missing for peer " + index);
                    }
                }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default)).ToArray();
                Check(Task.WaitAll(jobs, 20000), "DTLS bridge timeout");
                Check(seen.Count == 5 && seen.Keys.All(a => !aliases.Contains(a)), "Alias reused between peers/rehosts");
                foreach (var alias in seen.Keys) aliases.Add(alias);
            } finally {
                foreach (var client in clients) client.Disconnect();
                foreach (var worker in workers) Check(worker.Stop(), "Client worker leak");
                Check(hostWorker.Stop(), "Host worker leak"); server.Stop();
            }
        }
    }
    private static void BindAddresses() {
        var server = new DtlsServer();
        var field = typeof(DtlsServer).GetField("socket", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
        try {
            server.Start(0, IPAddress.Loopback);
            Check(((IPEndPoint)((Socket)field.GetValue(server)).LocalEndPoint).Address.Equals(IPAddress.Loopback), "Steam listener exposed to LAN");
            Check(server.BoundPort > 0, "Missing ephemeral port"); server.Stop();
            server.Start(0);
            Check(((IPEndPoint)((Socket)field.GetValue(server)).LocalEndPoint).Address.Equals(IPAddress.Any), "Direct listener binding changed");
        } finally { server.Stop(); }
    }
}
