using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Threading;
using Hkmp.Networking.Client;
using Hkmp.Networking;
using Hkmp.Networking.Packet;
using Hkmp.Networking.Packet.Connection;
using Hkmp.Networking.Packet.Data;
using Hkmp.Networking.Packet.Update;
using Hkmp.Networking.Server;
using Hkmp.Util;

internal static class ClientSessionProbe {
    private static T Field<T>(object o, string n) => (T)o.GetType().GetField(n, BindingFlags.Instance | BindingFlags.NonPublic).GetValue(o);
    internal static void DrainMainThread() {
        var queue = (List<Action>)typeof(ThreadUtil).GetField("ActionsToRun", BindingFlags.Static | BindingFlags.NonPublic).GetValue(null);
        var callbacks = queue.ToArray(); queue.Clear();
        foreach (var action in callbacks) action();
    }
    public static void CompleteDatagram() {
        DrainMainThread();
        var manager = new PacketManager(); var parent = false; var child = false; var addonApplied = false;
        manager.RegisterClientUpdatePacketHandler<ReliableEntityUpdate>(ClientUpdatePacketId.ReliableEntityUpdate,
            update => { if (update.Id == 17) parent = update.IsActive; else child = update.IsActive; });
        manager.RegisterClientAddonUpdatePacketHandler(1, 0, _ => addonApplied = true);
        var packet = new ClientUpdatePacket(); var batch = new PacketDataCollection<ReliableEntityUpdate>();
        foreach (var id in new ulong[] { 17, 18 }) {
            var update = new ReliableEntityUpdate { Id = id, IsActive = true };
            update.UpdateTypes.Add(EntityUpdateType.Active); batch.DataInstances.Add(update);
        }
        packet.SetSendingPacketData(ClientUpdatePacketId.ReliableEntityUpdate, batch);
        var addon = new AddonPacketData(1); addon.PacketData[0] = new EmptyData();
        packet.SetSendingAddonPacketData(1, addon);
        using var queued = new ManualResetEventSlim(); using var release = new ManualResetEventSlim();
        var mainThread = Thread.CurrentThread.ManagedThreadId; var scheduled = 0; Exception error = null;
        // Pump a Unity frame while the receive thread is paused immediately
        // after its first enqueue. The parent and child came in one datagram.
        using var hook = new MonoMod.RuntimeDetour.Hook(typeof(ThreadUtil).GetMethod("RunActionOnMainThread"),
            (Action<Action<Action>, Action>)((orig, callback) => {
                orig(callback);
                if (Thread.CurrentThread.ManagedThreadId != mainThread && Interlocked.Increment(ref scheduled) == 1) {
                    queued.Set(); if (!release.Wait(3000)) throw new Exception("Datagram enqueue barrier timed out");
                }
            }));
        var worker = new Thread(() => { try { manager.HandleClientUpdatePacket(packet); } catch (Exception e) { error = e; } });
        var partial = false; worker.Start();
        try {
            if (!queued.Wait(2000)) throw new Exception("Datagram was not queued");
            DrainMainThread(); partial = parent && (!child || !addonApplied);
        } finally {
            release.Set(); if (!worker.Join(3000)) throw new Exception("Datagram receive worker did not finish");
            DrainMainThread();
        }
        if (error != null) throw error;
        if (partial) throw new Exception("One received datagram exposed a parent update before its child/addon in a Unity frame");
        if (!parent || !child || !addonApplied) throw new Exception("Datagram lost a child or addon update");
        parent = child = addonApplied = false;
        manager.DeregisterClientUpdatePacketHandler(ClientUpdatePacketId.ReliableEntityUpdate);
        manager.RegisterClientUpdatePacketHandler<ReliableEntityUpdate>(ClientUpdatePacketId.ReliableEntityUpdate,
            update => {
                if (update.Id == 17) { parent = true; manager.InvalidateClientSession(); }
                else child = true;
            });
        manager.HandleClientUpdatePacket(packet); DrainMainThread();
        if (!parent || child || addonApplied) throw new Exception("Disconnect inside a datagram applied its remaining retired updates");
    }
    public static void OldCallbacks() {
        DrainMainThread();
        var manager = new PacketManager(); var client = new NetClient(manager); var calls = 0;
        manager.RegisterClientUpdatePacketHandler(ClientUpdatePacketId.ServerSettingsUpdated, () => calls++);
        // Replace the connection manager's real handler with a counting probe.
        // Registering over an existing ID only logs and keeps the old handler.
        manager.DeregisterClientConnectionPacketHandler(ClientConnectionPacketId.ServerInfo);
        manager.RegisterClientConnectionPacketHandler(ClientConnectionPacketId.ServerInfo, () => calls++);
        manager.RegisterClientAddonUpdatePacketHandler(1, 0, _ => calls++);
        manager.RegisterClientAddonConnectionPacketHandler(1, 0, _ => calls++);
        var old = new ClientUpdatePacket(); old.SetSendingPacketData(ClientUpdatePacketId.ServerSettingsUpdated, new EmptyData());
        var addon = new AddonPacketData(1); addon.PacketData[0] = new EmptyData();
        old.SetSendingAddonPacketData(1, addon);
        var oldConnection = new ClientConnectionPacket(); oldConnection.SetSendingPacketData(ClientConnectionPacketId.ServerInfo, new ServerInfo());
        oldConnection.SetSendingAddonPacketData(1, addon);
        manager.HandleClientUpdatePacket(old); // captured old handler already queued
        manager.HandleClientConnectionPacket(oldConnection);
        client.Disconnect();
        DrainMainThread();
        if (calls != 0) throw new Exception("Retired connection invoked a queued game callback");
        manager.RegisterClientAddonUpdatePacketHandler(1, 0, _ => calls++);
        manager.HandleClientUpdatePacket(old); manager.HandleClientConnectionPacket(oldConnection); DrainMainThread();
        if (calls != 4) throw new Exception("One of the four fresh callback routes was lost");
    }
    public static void EarlyUpdates() {
        DrainMainThread();
        var manager = new PacketManager(); var client = new NetClient(manager); var calls = 0;
        client.ConnectEvent += _ => manager.RegisterClientUpdatePacketHandler(ClientUpdatePacketId.PlayerConnect, () => calls++);
        typeof(NetClient).GetProperty("ConnectionStatus").SetValue(client, ClientConnectionStatus.Connecting, null);
        typeof(NetClient).GetMethod("OnServerInfoReceived", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(client,
            new object[] { new ServerInfo { ConnectionResult = ServerConnectionResult.Accepted } });
        // The wire has accepted the connection, but game setup is still queued.
        // Exercise an actual PlayerConnect packet before pumping the main thread.
        var first = new ClientUpdatePacket(); first.SetSendingPacketData(ClientUpdatePacketId.PlayerConnect, new PlayerConnect());
        manager.HandleClientUpdatePacket(first);
        DrainMainThread();
        if(calls != 1) throw new Exception("Initial player message was lost before connection setup");
        manager.HandleClientUpdatePacket(first); client.Disconnect(); DrainMainThread();
        if(calls != 1) throw new Exception("Old initial message survived a disconnect");
    }
    public static void RejectedConnection() {
        DrainMainThread();
        var server = new DtlsServer(); var client = new NetClient(new PacketManager());
        var dtls = Field<DtlsClient>(client, "_dtlsClient");
        var disconnected = 0; var failedThread = 0; var mainThread = Thread.CurrentThread.ManagedThreadId;
        client.DisconnectEvent += () => disconnected++;
        client.ConnectFailedEvent += _ => failedThread = Thread.CurrentThread.ManagedThreadId;
        server.Start(0);
        try {
            var socket = Field<Socket>(server, "socket");
            dtls.Connect("127.0.0.1", ((IPEndPoint)socket.LocalEndPoint).Port);
            client.UpdateManager.DtlsTransport = dtls.DtlsTransport;
            client.UpdateManager.StartUpdates();
            typeof(NetClient).GetProperty("ConnectionStatus").SetValue(client, ClientConnectionStatus.Connecting, null);
            Exception error = null;
            var rejection = new Thread(() => {
                try {
                    typeof(NetClient).GetMethod("OnServerInfoReceived", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(client,
                        new object[] { new ServerInfo { ConnectionResult = ServerConnectionResult.RejectedOther, ConnectionRejectedMessage = "test rejection" } });
                } catch (Exception e) { error = e; }
            });
            rejection.Start(); if (!rejection.Join(3000)) throw new Exception("Rejection cleanup hung");
            if (error != null) throw error;
            if (dtls.DtlsTransport != null || client.UpdateManager.DtlsTransport != null)
                throw new Exception("Rejected connection retained an active DTLS session");
            if (disconnected != 0 || failedThread != 0) throw new Exception("Handshake failure invoked game callbacks on a worker");
            DrainMainThread();
            if (failedThread != mainThread) throw new Exception("Failure callback did not run on main thread");
        } finally { client.Disconnect(); server.Stop(); DrainMainThread(); }
    }

    public static void CancelReconnect() {
        DrainMainThread();
        var server = new DtlsServer(); var client = new NetClient(new PacketManager());
        var failures = 0; client.ConnectFailedEvent += _ => failures++;
        using (var silent = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp)) {
            silent.Bind(new IPEndPoint(IPAddress.Loopback, 0));
            var silentPort = ((IPEndPoint)silent.LocalEndPoint).Port;
            server.Start(0);
            var port = ((IPEndPoint)Field<Socket>(server, "socket").LocalEndPoint).Port;
            try {
                // Include cancellation before the handshake thread starts and
                // while it is waiting on a real bound, silent UDP endpoint.
                for (var i = 0; i < 40; i++) {
                    client.Connect("127.0.0.1", silentPort, "cancel", "", new List<AddonData>());
                    var retired = Field<DtlsClient>(client, "_dtlsClient");
                    if ((i & 1) != 0) Thread.Sleep(4);
                    client.Disconnect();
                    if (retired.DtlsTransport != null) throw new Exception("Cancelled handshake published a transport");
                }
                client.Connect("127.0.0.1", port, "first", "", new List<AddonData>());
                if (!SpinWait.SpinUntil(() => client.UpdateManager.DtlsTransport != null && Field<Action>(client, "_timeoutHandler") != null, 5000))
                    throw new Exception("Handshake after cancellations did not finish");
                var oldTimeout = Field<Action>(client, "_timeoutHandler"); var oldSession = Field<int>(client, "_session");
                typeof(NetClient).GetMethod("OnServerInfoReceived", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(client,
                    new object[] { new ServerInfo { ConnectionResult = ServerConnectionResult.RejectedOther, ConnectionRejectedMessage = "reject then retry" } });
                client.Connect("127.0.0.1", port, "next", "", new List<AddonData>());
                if (!SpinWait.SpinUntil(() => client.UpdateManager.DtlsTransport != null, 5000)) throw new Exception("Reconnect after rejection did not finish");
                typeof(NetClient).GetMethod("OnServerInfoReceived", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(client,
                    new object[] { new ServerInfo { ConnectionResult = ServerConnectionResult.Accepted } });
                oldTimeout();
                typeof(NetClient).GetMethod("HandleConnectFailed", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(client,
                    new object[] { new ConnectionFailedResult { Reason = ConnectionFailedReason.TimedOut }, (int?)oldSession });
                Thread.Sleep(150); DrainMainThread();
                if (!client.IsConnected || client.UpdateManager.DtlsTransport == null || failures != 0)
                    throw new Exception("Old timeout/failure/rejection affected the replacement session");
            } finally { client.Disconnect(); server.Stop(); DrainMainThread(); }
        }
    }

    public static void ShutdownReceive() {
        DrainMainThread();
        var server = new DtlsServer(); var client = new NetClient(new PacketManager());
        var dtls = Field<DtlsClient>(client, "_dtlsClient");
        Thread shutdown = null; Exception error = null;
        server.Start(0);
        try {
            dtls.Connect("127.0.0.1", ((IPEndPoint)Field<Socket>(server, "socket").LocalEndPoint).Port);
            client.UpdateManager.DtlsTransport = dtls.DtlsTransport;
            client.UpdateManager.StartUpdates();
            var source = Field<CancellationTokenSource>(dtls, "_receiveTaskTokenSource");
            var token = source.Token;
            // Hold a real update-send barrier. Closing the room must retire its
            // receive callbacks even while the final outgoing update is blocked.
            var sendGate = typeof(ClientUpdateManager).BaseType.GetField("_sendGate", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(client.UpdateManager);
            lock (sendGate) {
                shutdown = new Thread(() => { try { client.Disconnect(); } catch (Exception e) { error = e; } });
                shutdown.Start();
                if (!SpinWait.SpinUntil(() => token.IsCancellationRequested, 1500))
                    throw new Exception("Retired room is still receiving while its final send waits");
            }
            if (!shutdown.Join(3000)) throw new Exception("Room shutdown did not finish");
            if (error != null) throw error;
        } finally {
            shutdown?.Join(3000); client.Disconnect(); server.Stop(); DrainMainThread();
        }
    }

    private sealed class SocketErrorLog : Hkmp.Logging.ILogger {
        internal int Errors; internal volatile bool Active = true;
        internal string LastError;
        public void Error(string message) {
            if (!Active || !message.Contains("UDP Socket exception")) return;
            LastError = message;
            if (message.Contains("Socket ErrorCode: TimedOut")) Interlocked.Increment(ref Errors);
        }
        public void Info(string s) { } public void Warn(string s) { }
        public void Debug(string s) { } public void Fine(string s) { }
    }

    public static void ReceiveFailureNeverBecomesData() {
        // Use a real socket receive timeout to exercise the active-error path.
        // Unlike an ICMP port-unreachable, this stimulus does not depend on
        // Windows choosing to deliver an ICMP to ReceiveFrom rather than Send.
        // Both reach the same SocketException branch in the production loop.
        var logger = new SocketErrorLog(); Hkmp.Logging.Logger.AddLogger(logger);
        using (var receiver = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp))
        using (var remote = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp))
        using (var cancellation = new CancellationTokenSource()) {
            remote.Bind(new IPEndPoint(IPAddress.Loopback, 0));
            var endpoint = (IPEndPoint)remote.LocalEndPoint;
            receiver.Connect(endpoint);
            receiver.ReceiveTimeout = 300;
            var local = (IPEndPoint)receiver.LocalEndPoint;
            var queue = new ClientDatagramTransport(receiver);
            var peer = new DtlsClient(); Exception workerError = null;
            var worker = new Thread(() => {
                try { typeof(DtlsClient).GetMethod("SocketReceiveLoop", BindingFlags.Instance | BindingFlags.NonPublic)
                    .Invoke(peer, new object[] { receiver, queue, cancellation.Token }); }
                catch (Exception e) { workerError = e; }
            });
            worker.Start();
            try {
                // First prove this worker can receive a real datagram, then
                // leave it idle until its actual ReceiveFrom throws TimedOut.
                remote.SendTo(new byte[] { 4, 5, 6 }, local);
                if (!queue.ReceivedDataCollection.TryTake(out var warmup, 3000) ||
                    warmup.Length != 3 || warmup.Buffer[0] != 4)
                    throw new Exception("Receive loop did not accept the warm-up datagram: " + workerError);
                if (!SpinWait.SpinUntil(() => Volatile.Read(ref logger.Errors) > 0 || workerError != null, 3000) ||
                    Volatile.Read(ref logger.Errors) == 0)
                    throw new Exception("Active receive timeout was not reported; worker: " + workerError + "; last error: " + logger.LastError);
                if (queue.ReceivedDataCollection.Count != 0) throw new Exception("Receive failure fabricated a datagram");
                remote.SendTo(new byte[] { 7, 8, 9 }, local);
                if (!queue.ReceivedDataCollection.TryTake(out var data, 3000) || data.Length != 3 ||
                    data.Buffer[0] != 7 || data.Buffer[1] != 8 || data.Buffer[2] != 9)
                    throw new Exception("Receive failure fabricated data or lost the next valid datagram");
                if (queue.ReceivedDataCollection.Count != 0) throw new Exception("Unexpected extra received entry");
            } finally {
                cancellation.Cancel(); receiver.Close();
                if (!worker.Join(3000)) throw new Exception("Receive loop did not cancel");
                queue.Dispose(); logger.Active = false;
            }
            if (workerError != null) throw workerError;
        }
    }
}
