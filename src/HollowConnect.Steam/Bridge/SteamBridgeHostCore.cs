// Adapted from Muurrcc/AHK-asyncronus-hollow-knight, commit 622735cb788dc718c8de0ec5c0ab4522366bcf2f.
// LGPL-2.1; see LICENSE and ../THIRD-PARTY.md. HollowConnect adaptations (2026).
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using Hkmp.Logging;

namespace HollowConnect.Steam.Bridge;

/// <summary>
/// Limits and timeouts of the bridge.
/// </summary>
internal sealed class SteamBridgeLimits {
    /// <summary>Maximum number of accepted (forwarding) connections on the host.</summary>
    public int MaxConnections { get; set; } = 16;

    /// <summary>Maximum number of connections that have not completed the handshake yet.</summary>
    public int MaxPending { get; set; } = 8;

    /// <summary>Time a host waits for the hello of a new connection.</summary>
    public int HelloTimeoutMs { get; set; } = 15000;

    /// <summary>Time a client waits for the Steam connection to be established.</summary>
    public int ConnectTimeoutMs { get; set; } = 30000;

    /// <summary>Time a client waits for the handshake response after sending its hello.</summary>
    public int ResponseTimeoutMs { get; set; } = 10000;

    /// <summary>Time without any traffic in either direction after which a forwarding connection is closed.</summary>
    public int IdleTimeoutMs { get; set; } = 60000;

    /// <summary>
    /// Time a closed connection's local UDP socket stays open (discarding data). Without this the HKMP server
    /// would get ICMP "port unreachable" (ConnectionReset errors in its log) until it times the client out.
    /// </summary>
    public int SocketLingerMs { get; set; } = 15000;

    /// <summary>Maximum number of messages moved per direction and connection in one pump.</summary>
    public int MaxMessagesPerPump { get; set; } = 256;
}

/// <summary>
/// Monotonic millisecond clock (replaceable in tests).
/// </summary>
internal static class SteamBridgeClock {
    private static readonly Stopwatch Stopwatch = Stopwatch.StartNew();

    /// <summary>
    /// Default clock.
    /// </summary>
    public static long NowMs() => Stopwatch.ElapsedMilliseconds;
}

/// <summary>
/// Something that the <see cref="SteamBridgeWorker"/> pumps on its thread.
/// </summary>
internal interface ISteamBridgePump : IDisposable {
    /// <summary>
    /// Move pending data in both directions and process state changes.
    /// </summary>
    void Pump();

    /// <summary>
    /// Block until there might be work or the timeout elapsed.
    /// </summary>
    /// <param name="timeoutMicroseconds">The maximum time to wait.</param>
    void Wait(int timeoutMicroseconds);
}

/// <summary>
/// Host side of the bridge: every incoming link (Steam connection) gets its own local UDP socket that talks to the
/// HKMP server at <see cref="Target"/>, so the server sees one distinct end point per remote player.
/// Thread safety: <see cref="TryAdd"/> and <see cref="ActiveCount"/> may be used from any thread, everything else
/// only from the worker thread.
/// </summary>
internal sealed class SteamBridgeHostCore : ISteamBridgePump {
    private enum SessionState {
        AwaitingHello,
        Forwarding
    }

    private sealed class Session {
        public ISteamBridgeLink Link;
        public SessionState State;
        public Socket Socket;
        public long CreatedAt;
        public long LastActivity;
        public long ForwardedToServer;
        public long ForwardedToClient;
    }

    /// <summary>
    /// The end point of the local HKMP server.
    /// </summary>
    public IPEndPoint Target { get; }

    private readonly string _password;
    private readonly SteamBridgeLimits _limits;
    private readonly Func<long> _clock;

    private readonly ConcurrentQueue<ISteamBridgeLink> _incoming = new();
    private readonly List<Session> _sessions = new();
    private readonly List<KeyValuePair<Socket, long>> _lingering = new();
    private readonly byte[] _buffer = new byte[SteamBridgeProtocol.MaxDatagramSize + 1];

    /// <summary>
    /// Time to wait between pumps while there are no connections.
    /// </summary>
    private const int IdleWaitMicroseconds = 10000;

    private int _totalCount;
    private int _activeCount;
    private volatile bool _disposed;
    private readonly object _incomingGate = new();

    /// <summary>
    /// Number of links that have completed the handshake and are being forwarded.
    /// </summary>
    public int ActiveCount => Volatile.Read(ref _activeCount);

    /// <summary>
    /// Number of links (pending + forwarding + queued).
    /// </summary>
    public int TotalCount => Volatile.Read(ref _totalCount);

    public SteamBridgeHostCore(IPEndPoint target, string password, SteamBridgeLimits limits = null, Func<long> clock = null) {
        Target = target;
        _password = SteamBridgeProtocol.TrimPassword(password);
        _limits = limits ?? new SteamBridgeLimits();
        _clock = clock ?? SteamBridgeClock.NowMs;
    }

    /// <summary>
    /// Queue a new link. Returns false (and does not take ownership) if too many links are open already, in which
    /// case the caller should close the link.
    /// </summary>
    /// <param name="link">The new link.</param>
    /// <returns>True if the link was queued.</returns>
    public bool TryAdd(ISteamBridgeLink link) {
        lock (_incomingGate) {
        if (_disposed) {
            return false;
        }

        var max = _limits.MaxConnections + _limits.MaxPending;
        while (true) {
            var current = Volatile.Read(ref _totalCount);
            if (current >= max) {
                return false;
            }

            if (Interlocked.CompareExchange(ref _totalCount, current + 1, current) == current) {
                break;
            }
        }

        _incoming.Enqueue(link);
        return true;
        }
    }

    /// <inheritdoc />
    public void Pump() {
        if (_disposed) {
            return;
        }

        var now = _clock();

        while (_incoming.TryDequeue(out var link)) {
            _sessions.Add(new Session {
                Link = link,
                State = SessionState.AwaitingHello,
                CreatedAt = now,
                LastActivity = now
            });
            Logger.Info($"Steam bridge: new connection {link.Description}");
        }

        PumpLingering(now);

        for (var i = _sessions.Count - 1; i >= 0; i--) {
            var session = _sessions[i];
            string closeReason;
            var closeResult = SteamBridgeResult.NotHosting;
            try {
                closeReason = PumpSession(session, now, ref closeResult);
            } catch (Exception e) {
                closeReason = $"internal error: {e.GetType().Name}: {e.Message}";
                Logger.Error($"Steam bridge: exception for {session.Link.Description}:\n{e}");
            }

            if (closeReason != null) {
                RemoveSession(i, closeReason, closeResult);
            }
        }
    }

    /// <summary>
    /// Pump a single session.
    /// </summary>
    /// <param name="session">The session.</param>
    /// <param name="now">The current time.</param>
    /// <param name="closeResult">Set to the result to report when closing because of the handshake.</param>
    /// <returns>Null to keep the session, or the reason to close it.</returns>
    private string PumpSession(Session session, long now, ref SteamBridgeResult closeResult) {
        var link = session.Link;
        var linkState = link.State;
        if (linkState == SteamBridgeLinkState.Closed) {
            return $"closed by remote ({link.CloseDescription ?? "no reason"})";
        }

        if (session.State == SessionState.AwaitingHello) {
            if (now - session.CreatedAt > _limits.HelloTimeoutMs) {
                closeResult = SteamBridgeResult.TimedOut;
                return "handshake timed out";
            }

            if (linkState != SteamBridgeLinkState.Connected || !link.TryReceive(out var hello)) {
                return null;
            }

            var result = Evaluate(hello);
            var response = SteamBridgeProtocol.BuildResponse(result);
            link.Send(response, response.Length, true);

            if (result != SteamBridgeResult.Accepted) {
                Logger.Info($"Steam bridge: rejected {link.Description}: {result}");
                closeResult = result;
                return SteamBridgeProtocol.Describe(result);
            }

            session.Socket = SteamBridgeUdp.CreateLoopbackSocket(true);
            session.State = SessionState.Forwarding;
            session.LastActivity = now;
            Interlocked.Increment(ref _activeCount);
            Logger.Info(
                $"Steam bridge: accepted {link.Description}, forwarding via local port " +
                $"{((IPEndPoint) session.Socket.LocalEndPoint).Port} to {Target}"
            );
        }

        // Remote -> HKMP server
        var moved = 0;
        while (moved < _limits.MaxMessagesPerPump && link.TryReceive(out var data)) {
            moved++;
            if (data.Length == 0 || data.Length > SteamBridgeProtocol.MaxDatagramSize) {
                continue;
            }

            if (SteamBridgeUdp.TrySend(session.Socket, data, data.Length, Target)) {
                session.ForwardedToServer++;
            }
        }

        // HKMP server -> remote
        var received = 0;
        while (received < _limits.MaxMessagesPerPump &&
               SteamBridgeUdp.TryReceive(session.Socket, _buffer, out var length, out var sender)) {
            received++;
            if (!sender.Equals(Target) || length == 0 || length > SteamBridgeProtocol.MaxDatagramSize) {
                continue;
            }

            if (link.Send(_buffer, length, false)) {
                session.ForwardedToClient++;
            }
        }

        if (moved > 0 || received > 0) {
            session.LastActivity = now;
        } else if (now - session.LastActivity > _limits.IdleTimeoutMs) {
            return "idle timeout";
        }

        return null;
    }

    /// <summary>
    /// Discard data arriving on sockets of closed sessions and close them once their linger time expired.
    /// </summary>
    private void PumpLingering(long now) {
        for (var i = _lingering.Count - 1; i >= 0; i--) {
            var socket = _lingering[i].Key;
            var expired = now >= _lingering[i].Value;
            try {
                var drained = 0;
                while (!expired && drained++ < _limits.MaxMessagesPerPump &&
                       SteamBridgeUdp.TryReceive(socket, _buffer, out _, out _)) {
                }
            } catch (Exception) {
                expired = true;
            }

            if (expired) {
                CloseSocket(socket);
                _lingering.RemoveAt(i);
            }
        }
    }

    private static void CloseSocket(Socket socket) {
        try {
            socket.Close();
        } catch (Exception e) {
            Logger.Warn($"Steam bridge: error closing local socket: {e.Message}");
        }
    }

    /// <summary>
    /// Decide whether the hello is acceptable.
    /// </summary>
    private SteamBridgeResult Evaluate(byte[] hello) {
        if (!SteamBridgeProtocol.TryParseHello(hello, out var version, out var password)) {
            return SteamBridgeResult.Malformed;
        }

        if (version != SteamBridgeProtocol.Version) {
            return SteamBridgeResult.VersionMismatch;
        }

        if (!SteamBridgeProtocol.PasswordMatches(_password, password)) {
            return SteamBridgeResult.WrongPassword;
        }

        if (ActiveCount >= _limits.MaxConnections) {
            return SteamBridgeResult.ServerFull;
        }

        return SteamBridgeResult.Accepted;
    }

    private void RemoveSession(int index, string reason, SteamBridgeResult closeResult) {
        var session = _sessions[index];
        _sessions.RemoveAt(index);
        Interlocked.Decrement(ref _totalCount);

        if (session.State == SessionState.Forwarding) {
            Interlocked.Decrement(ref _activeCount);
        }

        if (session.Socket != null) {
            if (_disposed || _limits.SocketLingerMs <= 0) {
                CloseSocket(session.Socket);
            } else {
                _lingering.Add(new KeyValuePair<Socket, long>(session.Socket, _clock() + _limits.SocketLingerMs));
            }
        }

        try {
            session.Link.Close(SteamBridgeProtocol.CloseReasonBase + (int) closeResult, reason);
        } catch (Exception e) {
            Logger.Warn($"Steam bridge: error closing link: {e.Message}");
        }

        Logger.Info(
            $"Steam bridge: closed {session.Link.Description} ({reason}); datagrams to server " +
            $"{session.ForwardedToServer}, to client {session.ForwardedToClient}"
        );
    }

    /// <inheritdoc />
    public void Wait(int timeoutMicroseconds) {
        // Steam messages cannot be waited on, so poll; slower while nobody is connected
        var idle = _sessions.Count == 0 && _lingering.Count == 0 && _incoming.IsEmpty;
        SteamBridgeSleep.Sleep(idle ? IdleWaitMicroseconds : timeoutMicroseconds);
    }

    /// <summary>
    /// Close all links and sockets. Must be called from the worker thread (or after it stopped).
    /// </summary>
    public void Dispose() {
        if (_disposed) {
            return;
        }

        lock (_incomingGate) _disposed = true;

        while (_incoming.TryDequeue(out var link)) {
            _sessions.Add(new Session { Link = link });
        }

        for (var i = _sessions.Count - 1; i >= 0; i--) {
            RemoveSession(i, "host stopped", SteamBridgeResult.NotHosting);
        }

        foreach (var pair in _lingering) {
            CloseSocket(pair.Key);
        }

        _lingering.Clear();
    }

    /// <summary>
    /// Number of local sockets of closed connections that are still open (for tests).
    /// </summary>
    internal int LingeringSocketCount => _lingering.Count;
}

