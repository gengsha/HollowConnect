using System;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using Hkmp.Logging;
using Org.BouncyCastle.Tls;
using Org.BouncyCastle.Tls.Crypto.Impl.BC;

namespace Hkmp.Networking.Client;

/// <summary>
/// DTLS implementation for a client-side peer for networking.
/// </summary>
internal class DtlsClient {
    // A NetClient creates one peer per attempt. A cancelled peer must never
    // start a handshake later, even if its background thread has not run yet.
    private readonly object _gate = new object();
    private volatile bool _closed;
    /// <summary>
    /// The maximum packet size for sending and receiving DTLS packets.
    /// </summary>
    public const int MaxPacketSize = 1400;

    /// <summary>
    /// The maximum time the DTLS handshake can take in milliseconds before timing out.
    /// </summary>
    public const int DtlsHandshakeTimeoutMillis = 5000;

    /// <summary>
    /// The socket instance for the underlying networking.
    /// </summary>
    private Socket _socket;
    /// <summary>
    /// The TLS client for communicating supported cipher suites and handling certificates.
    /// </summary>
    private ClientTlsClient _tlsClient;
    /// <summary>
    /// The client datagram transport that provides networking to the DTLS client.
    /// </summary>
    private ClientDatagramTransport _clientDatagramTransport;

    /// <summary>
    /// Token source for cancellation tokens for the receive task.
    /// </summary>
    private CancellationTokenSource _receiveTaskTokenSource;

    /// <summary>
    /// DTLS transport instance from establishing a connection to a server.
    /// </summary>
    public DtlsTransport DtlsTransport { get; private set; }
    
    /// <summary>
    /// Event that is called when data is received from the server. 
    /// </summary>
    public event Action<byte[], int> DataReceivedEvent;

    /// <summary>
    /// Try to establish a connection to a server with the given address and port.
    /// </summary>
    /// <param name="address">The address of the server.</param>
    /// <param name="port">The port of the server.</param>
    /// <exception cref="SocketException">Thrown when the underlying socket fails to connect to the server.</exception>
    /// <exception cref="IOException">Thrown when the DTLS protocol fails to connect to the server.</exception>
    public void Connect(string address, int port) {
        Socket socket;
        ClientTlsClient tls;
        ClientDatagramTransport datagram;
        CancellationToken token;
        lock (_gate) {
            if (_closed) throw new ObjectDisposedException(nameof(DtlsClient));
            if (_socket != null) throw new InvalidOperationException("DTLS peer already started");
            socket = _socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            tls = _tlsClient = new ClientTlsClient(new BcTlsCrypto());
            datagram = _clientDatagramTransport = new ClientDatagramTransport(socket);
            _receiveTaskTokenSource = new CancellationTokenSource();
            token = _receiveTaskTokenSource.Token;
        }
        try {
            socket.Connect(address, port);
            new Thread(() => SocketReceiveLoop(socket, datagram, token)) { IsBackground = true }.Start();
            // Only locals are used while Disconnect can clear the published fields.
            var transport = new DtlsClientProtocol().Connect(tls, datagram);
            lock (_gate) {
                if (_closed) {
                    try { transport.Close(); } catch (IOException) { } catch (ObjectDisposedException) { } catch (SocketException) { }
                    throw new ObjectDisposedException(nameof(DtlsClient));
                }
                DtlsTransport = transport;
                new Thread(() => DtlsReceiveLoop(transport, token)) { IsBackground = true }.Start();
            }
            Logger.Debug($"Successfully connected DTLS client to endpoint: {address}:{port}");
        } catch (Exception e) when (e is IOException || e is SocketException || e is ObjectDisposedException || e is OperationCanceledException) {
            var cancelled = _closed;
            Disconnect();
            if (cancelled) throw new ObjectDisposedException(nameof(DtlsClient), "DTLS attempt was cancelled");
            throw;
        }
    }

    /// <summary>
    /// Disconnect the DTLS client from the server. This will cancel, dispose, or close all internal objects to
    /// clean up potential previous connection attempts.
    /// </summary>
    public void Disconnect() {
        CancellationTokenSource source;
        ClientDatagramTransport datagram;
        ClientTlsClient tls;
        Socket socket;
        DtlsTransport transport;
        lock (_gate) {
            if (_closed) return;
            _closed = true;
            source = _receiveTaskTokenSource; _receiveTaskTokenSource = null;
            datagram = _clientDatagramTransport; _clientDatagramTransport = null;
            tls = _tlsClient; _tlsClient = null;
            socket = _socket; _socket = null;
            transport = DtlsTransport; DtlsTransport = null;
        }
        source?.Cancel();
        tls?.Cancel();
        datagram?.Close();
        try { transport?.Close(); }
        catch (IOException) { } catch (SocketException) { } catch (ObjectDisposedException) { }
        socket?.Close();
        datagram?.Dispose();
        source?.Dispose();
    }

    /// <summary>
    /// Continuously tries to receive data from the socket until cancellation is requested.
    /// </summary>
    /// <param name="socket">The socket for this connection attempt.</param>
    /// <param name="datagram">The datagram queue for this connection attempt.</param>
    /// <param name="cancellationToken">The cancellation token to cancel the loop.</param>
    private void SocketReceiveLoop(Socket socket, ClientDatagramTransport datagram, CancellationToken cancellationToken) {
        while (!cancellationToken.IsCancellationRequested) {
            EndPoint endPoint = new IPEndPoint(IPAddress.Any, 0);
            
            var numReceived = 0;
            var buffer = new byte[MaxPacketSize];

            try {
                numReceived = socket.ReceiveFrom(
                    buffer,
                    SocketFlags.None,
                    ref endPoint
                );
            } catch (ObjectDisposedException) { break;
            } catch (SocketException e) when (e.SocketErrorCode == SocketError.Interrupted || cancellationToken.IsCancellationRequested) {
                // SocketError Interrupted happens when the socket is closed during the receive call
                // We close the socket when the client disconnects, thus this exception is expected, so we simply break
                Logger.Debug("SocketException with error code interrupted");
                break;
            } catch (SocketException e) {
                Logger.Error($"UDP Socket exception, ErrorCode: {e.ErrorCode}, Socket ErrorCode: {e.SocketErrorCode}, Exception:\n{e}");
            }

            if (cancellationToken.IsCancellationRequested) {
                Logger.Debug("Cancellation requested");
                break;
            }

            try {
                datagram.ReceivedDataCollection.Add(new UdpDatagramTransport.ReceivedData {
                    Buffer = buffer,
                    Length = numReceived
                }, cancellationToken);
            } catch (OperationCanceledException) {
                Logger.Debug("OperationCanceledException");
                break;
            } catch (ObjectDisposedException) { break; }
        }
        
        Logger.Debug("Receive loop cancelled");
    }

    /// <summary>
    /// Continuously tries to receive data from the DTLS transport until cancellation is requested.
    /// </summary>
    /// <param name="transport">The DTLS session for this connection attempt.</param>
    /// <param name="cancellationToken">The cancellation token to cancel the loop.</param>
    private void DtlsReceiveLoop(DtlsTransport transport, CancellationToken cancellationToken) {
        try {
            while (!cancellationToken.IsCancellationRequested) {
                var buffer = new byte[MaxPacketSize];
                var length = transport.Receive(buffer, 0, buffer.Length, 50);
                if (length > 0 && !cancellationToken.IsCancellationRequested) {
                    DataReceivedEvent?.Invoke(buffer, length);
                }
            }
        } catch (Exception e) when (e is IOException || e is SocketException || e is ObjectDisposedException || e is OperationCanceledException) {
            if (!cancellationToken.IsCancellationRequested) Logger.Warn("DTLS receive ended: " + e.Message);
        }
    }
}
