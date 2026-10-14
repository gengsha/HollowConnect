using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using Hkmp.Logging;
using Org.BouncyCastle.Tls;
using Org.BouncyCastle.Tls.Crypto.Impl.BC;

namespace Hkmp.Networking.Server;

/// <summary>One UDP socket with isolated DTLS sessions and handshake queues per endpoint.</summary>
internal class DtlsServer {
    public const int MaxPacketSize = 1400;
    private readonly object gate = new object();
    private readonly Dictionary<IPEndPoint, DtlsServerClient> clients = new Dictionary<IPEndPoint, DtlsServerClient>();
    private readonly Dictionary<IPEndPoint, ServerDatagramTransport> handshakes = new Dictionary<IPEndPoint, ServerDatagramTransport>();
    private Socket socket;
    private CancellationTokenSource stop;
    public event Action<DtlsServerClient, byte[], int> DataReceivedEvent;
    public event Action<DtlsServerClient> ClientClosedEvent;

    public void Start(int port) {
        if (socket != null) throw new InvalidOperationException("DTLS server is already started");
        var identity = new ServerTlsServer(new BcTlsCrypto());
        var listener = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        try { listener.Bind(new IPEndPoint(IPAddress.Any, port)); }
        catch { listener.Dispose(); throw; }
        socket = listener; stop = new CancellationTokenSource();
        var token = stop.Token;
        new Thread(() => SocketReceiveLoop(listener, identity, token)) { IsBackground = true }.Start();
    }

    public void Stop() {
        stop?.Cancel();
        DtlsServerClient[] connected;
        ServerDatagramTransport[] pending;
        lock (gate) {
            connected = clients.Values.ToArray(); pending = handshakes.Values.ToArray();
            clients.Clear(); handshakes.Clear();
        }
        foreach (var transport in pending) { try { transport.Close(); } catch (ObjectDisposedException) { } }
        foreach (var client in connected) InternalDisconnectClient(client);
        socket?.Close(); socket = null;
        stop?.Dispose(); stop = null;
    }

    public void DisconnectClient(IPEndPoint endPoint) {
        DtlsServerClient client;
        lock (gate) {
            if (!clients.TryGetValue(endPoint, out client)) return;
            clients.Remove(endPoint);
        }
        InternalDisconnectClient(client);
    }

    private static void InternalDisconnectClient(DtlsServerClient client) {
        // The receive worker owns disposal after its last blocking receive exits.
        try { client.ReceiveLoopTokenSource.Cancel(); client.DatagramTransport.Close(); }
        catch (ObjectDisposedException) { }
    }

    private void SocketReceiveLoop(Socket listener, ServerTlsServer identity, CancellationToken token) {
        while (!token.IsCancellationRequested) {
            EndPoint endpoint = new IPEndPoint(IPAddress.Any, 0);
            var buffer = new byte[MaxPacketSize];
            int count;
            try { count = listener.ReceiveFrom(buffer, SocketFlags.None, ref endpoint); }
            catch (ObjectDisposedException) { break; }
            catch (SocketException e) {
                if (token.IsCancellationRequested) break;
                Logger.Warn("UDP receive failed: " + e.SocketErrorCode); continue;
            }
            if (count <= 0 || token.IsCancellationRequested) continue;
            var address = (IPEndPoint)endpoint;
            ServerDatagramTransport transport;
            var begin = false;
            lock (gate) {
                if (token.IsCancellationRequested) break;
                if (clients.TryGetValue(address, out var client)) transport = client.DatagramTransport;
                else if (!handshakes.TryGetValue(address, out transport)) {
                    if (handshakes.Count >= 32) continue;
                    transport = new ServerDatagramTransport(listener) { IPEndPoint = address };
                    handshakes.Add(address, transport); begin = true;
                }
            }
            try {
                transport.ReceivedDataCollection.Add(new UdpDatagramTransport.ReceivedData { Buffer = buffer, Length = count }, token);
            } catch (OperationCanceledException) { break; }
            catch (ObjectDisposedException) { continue; }
            if (begin) {
                var pending = transport;
                new Thread(() => AcceptClient(address, pending, identity, token)) { IsBackground = true }.Start();
            }
        }
    }

    private void AcceptClient(IPEndPoint address, ServerDatagramTransport transport, ServerTlsServer identity, CancellationToken token) {
        var handedOff = false;
        try {
            var tls = new ServerTlsServer(new BcTlsCrypto(), identity);
            var dtls = new DtlsServerProtocol().Accept(tls, transport);
            var client = new DtlsServerClient { DtlsTransport = dtls, DatagramTransport = transport,
                EndPoint = address, ReceiveLoopTokenSource = new CancellationTokenSource() };
            lock (gate) {
                if (token.IsCancellationRequested) { client.ReceiveLoopTokenSource.Dispose(); return; }
                // Register before removing the pending queue so no datagram crosses sessions.
                clients[address] = client; handshakes.Remove(address); handedOff = true;
            }
            new Thread(() => ClientReceiveLoop(client)) { IsBackground = true }.Start();
        } catch (Exception e) when (e is IOException || e is SocketException || e is OperationCanceledException || e is ObjectDisposedException) {
            if (!token.IsCancellationRequested) Logger.Warn("DTLS handshake failed for " + address + ": " + e.Message);
        } finally {
            if (!handedOff) {
                lock (gate) {
                    if (handshakes.TryGetValue(address, out var pending) && pending == transport) handshakes.Remove(address);
                }
                transport.Close(); transport.Dispose();
            }
        }
    }

    private void ClientReceiveLoop(DtlsServerClient client) {
        var token = client.ReceiveLoopTokenSource.Token;
        var dtls = client.DtlsTransport;
        try {
            while (!token.IsCancellationRequested && !client.DatagramTransport.IsClosed) {
                var buffer = new byte[dtls.GetReceiveLimit()];
                var count = dtls.Receive(buffer, 0, buffer.Length, 50);
                if (count <= 0) continue;
                try { DataReceivedEvent?.Invoke(client, buffer, count); }
                catch (Exception e) { Logger.Error("DTLS packet handler failed: " + e); }
            }
        } catch (Exception e) when (e is IOException || e is SocketException || e is OperationCanceledException || e is ObjectDisposedException) {
            if (!token.IsCancellationRequested) Logger.Warn("DTLS client ended: " + e.Message);
        } finally {
            lock (gate) {
                if (clients.TryGetValue(client.EndPoint, out var registered) && registered == client) clients.Remove(client.EndPoint);
            }
            client.DatagramTransport.Close();
            ClientClosedEvent?.Invoke(client);
            try { dtls.Close(); } catch (IOException) { } catch (SocketException) { } catch (ObjectDisposedException) { }
            client.DatagramTransport.Dispose(); client.ReceiveLoopTokenSource.Dispose();
        }
    }
}
