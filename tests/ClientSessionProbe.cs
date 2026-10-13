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
        public void Error(string message) { if (Active && message.Contains("UDP Socket exception")) Interlocked.Increment(ref Errors); }
        public void Info(string s) { } public void Warn(string s) { }
        public void Debug(string s) { } public void Fine(string s) { }
    }

    public static void ReceiveFailureNeverBecomesData() {
        // Windows reports ICMP port-unreachable as a reset on a connected UDP
        // socket. Exercise the actual receive loop, then reopen that endpoint
        // and require the first real datagram without a fabricated empty entry.
        if (Environment.OSVersion.Platform != PlatformID.Win32NT) return;
        var logger = new SocketErrorLog(); Hkmp.Logging.Logger.AddLogger(logger);
        using (var receiver = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp))
        using (var remote = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp))
        using (var cancellation = new CancellationTokenSource()) {
            remote.Bind(new IPEndPoint(IPAddress.Loopback, 0));
            var endpoint = (IPEndPoint)remote.LocalEndPoint;
            receiver.Connect(endpoint);
            // .NET Framework and Unity Mono use different defaults for this
            // Windows option. Explicitly enable the ICMP reset under test.
            receiver.IOControl(unchecked((IOControlCode)0x9800000C), BitConverter.GetBytes(1), null);
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
                remote.Close(); receiver.Send(new byte[] { 1 });
                if (!SpinWait.SpinUntil(() => Volatile.Read(ref logger.Errors) > 0, 3000))
                    throw new Exception("Active socket error was not reported");
                using (var reopened = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp)) {
                    reopened.Bind(endpoint); reopened.SendTo(new byte[] { 7, 8, 9 }, local);
                    if (!queue.ReceivedDataCollection.TryTake(out var data, 3000) || data.Length != 3 || data.Buffer[0] != 7)
                        throw new Exception("Receive failure fabricated data or lost the next valid datagram");
                    if (queue.ReceivedDataCollection.Count != 0) throw new Exception("Unexpected extra received entry");
                }
            } finally {
                cancellation.Cancel(); receiver.Close();
                if (!worker.Join(3000)) throw new Exception("Receive loop did not cancel");
                queue.Dispose(); logger.Active = false;
            }
            if (workerError != null) throw workerError;
        }
    }
}
