// Adapted from Muurrcc/AHK-asyncronus-hollow-knight, commit 622735cb788dc718c8de0ec5c0ab4522366bcf2f.
// LGPL-2.1; see LICENSE and ../THIRD-PARTY.md. HollowConnect adaptations (2026).
using System;
using System.Net;
using System.Net.Sockets;
using System.Threading;

namespace HollowConnect.Steam.Bridge;

/// <summary>
/// State of a <see cref="ISteamBridgeLink"/>.
/// </summary>
internal enum SteamBridgeLinkState {
    /// <summary>The link is still being established.</summary>
    Connecting,
    /// <summary>The link is established and can carry messages.</summary>
    Connected,
    /// <summary>The link was closed (by either side or because of a problem).</summary>
    Closed
}

/// <summary>
/// Message based connection between the two bridges (a Steam networking connection in the game, an in-memory pair
/// in tests). All members are only used from the bridge worker thread.
/// </summary>
internal interface ISteamBridgeLink {
    /// <summary>
    /// Human readable description for logs.
    /// </summary>
    string Description { get; }

    /// <summary>
    /// The current state of the link.
    /// </summary>
    SteamBridgeLinkState State { get; }

    /// <summary>
    /// Description of why the link closed, if known.
    /// </summary>
    string CloseDescription { get; }

    /// <summary>
    /// Send a message.
    /// </summary>
    /// <param name="buffer">The buffer containing the message.</param>
    /// <param name="length">The number of bytes from the start of the buffer to send.</param>
    /// <param name="reliable">Whether the message must be delivered reliably (handshake) or not (datagrams).</param>
    /// <returns>True if the message was queued for sending.</returns>
    bool Send(byte[] buffer, int length, bool reliable);

    /// <summary>
    /// Try to receive one message.
    /// </summary>
    /// <param name="data">The message, or null if none is available.</param>
    /// <returns>True if a message was received.</returns>
    bool TryReceive(out byte[] data);

    /// <summary>
    /// Close the link and release its resources. Safe to call more than once.
    /// </summary>
    /// <param name="reason">Application close reason (see <see cref="SteamBridgeProtocol.CloseReasonBase"/>).</param>
    /// <param name="debug">Short description for the remote side and logs.</param>
    void Close(int reason, string debug);
}

/// <summary>
/// Helpers for the local UDP sockets of the bridge.
/// </summary>
internal static class SteamBridgeUdp {
    /// <summary>
    /// Windows IOCTL to stop ICMP "port unreachable" from surfacing as ConnectionReset on later receives.
    /// </summary>
    private const int SioUdpConnReset = -1744830452;

    /// <summary>
    /// Create a non-blocking UDP socket bound to an ephemeral port on the IPv4 loopback address.
    /// </summary>
    /// <returns>The socket.</returns>
    private static int _nextAlias = 1;
    public static Socket CreateLoopbackSocket(bool isolatedPeer = false) {
        var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        try {
            try {
                socket.IOControl(SioUdpConnReset, [0, 0, 0, 0], null);
            } catch (Exception) {
                // Not supported on this platform, receive errors are handled anyway
            }

            var address = IPAddress.Loopback;
            if (isolatedPeer) {
                // HKMP throttles failed authentication by IP. Give each Steam
                // peer a unique loopback IP, including across rapid rehosts.
                var alias = Interlocked.Increment(ref _nextAlias);
                if (alias > 0xFFFFFE) throw new InvalidOperationException("Loopback aliases exhausted");
                address = new IPAddress(new byte[] {127, (byte)(alias >> 16), (byte)(alias >> 8), (byte)alias});
            }
            socket.Bind(new IPEndPoint(address, 0));
            socket.Blocking = false;
            socket.ReceiveBufferSize = 256 * 1024;
            socket.SendBufferSize = 256 * 1024;
        } catch {
            socket.Close();
            throw;
        }

        return socket;
    }

    /// <summary>
    /// Try to receive one datagram from a non-blocking socket.
    /// </summary>
    /// <param name="socket">The socket.</param>
    /// <param name="buffer">The receive buffer.</param>
    /// <param name="length">The number of received bytes.</param>
    /// <param name="sender">The sender end point.</param>
    /// <returns>True if a datagram was received, false if none was available.</returns>
    public static bool TryReceive(Socket socket, byte[] buffer, out int length, out IPEndPoint sender) {
        length = 0;
        sender = null;

        while (true) {
            if (socket.Available <= 0) {
                return false;
            }

            EndPoint endPoint = new IPEndPoint(IPAddress.Any, 0);
            try {
                length = socket.ReceiveFrom(buffer, SocketFlags.None, ref endPoint);
                sender = (IPEndPoint) endPoint;
                return true;
            } catch (SocketException e) when (
                e.SocketErrorCode == SocketError.ConnectionReset ||
                e.SocketErrorCode == SocketError.MessageSize
            ) {
                // ICMP port unreachable or oversized datagram: drop it and continue
            } catch (SocketException e) when (e.SocketErrorCode == SocketError.WouldBlock) {
                return false;
            }
        }
    }

    /// <summary>
    /// Close a socket after a delay. While it stays open, datagrams sent to it by a local HKMP peer that has not
    /// noticed the disconnect yet are silently dropped instead of causing ICMP "port unreachable" errors there.
    /// </summary>
    /// <param name="socket">The socket.</param>
    /// <param name="delayMs">The delay; 0 or less closes immediately.</param>
    public static void CloseLater(Socket socket, int delayMs) {
        if (delayMs <= 0) {
            CloseQuietly(socket);
            return;
        }

        // Pending timers are rooted here, an unreferenced Timer could be garbage collected before it fires
        Timer timer = null;
        timer = new Timer(_ => {
            CloseQuietly(socket);
            lock (PendingCloseTimers) {
                PendingCloseTimers.Remove(timer);
            }

            timer?.Dispose();
        }, null, Timeout.Infinite, Timeout.Infinite);

        lock (PendingCloseTimers) {
            PendingCloseTimers.Add(timer);
        }

        timer.Change(delayMs, Timeout.Infinite);
    }

    /// <summary>
    /// Timers of <see cref="CloseLater"/> that did not fire yet.
    /// </summary>
    private static readonly System.Collections.Generic.HashSet<Timer> PendingCloseTimers = new();

    /// <summary>
    /// Close a socket, ignoring errors.
    /// </summary>
    public static void CloseQuietly(Socket socket) {
        try {
            socket?.Close();
        } catch (Exception) {
            // Ignore
        }
    }

    /// <summary>
    /// Send a datagram, ignoring transient errors (UDP semantics: the datagram is simply lost).
    /// </summary>
    /// <returns>True if the datagram was sent.</returns>
    public static bool TrySend(Socket socket, byte[] data, int length, EndPoint target) {
        try {
            socket.SendTo(data, 0, length, SocketFlags.None, target);
            return true;
        } catch (SocketException) {
            return false;
        }
    }
}

