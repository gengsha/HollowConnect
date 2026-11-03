// Adapted from Muurrcc/AHK-asyncronus-hollow-knight, commit 622735cb788dc718c8de0ec5c0ab4522366bcf2f.
// LGPL-2.1; see LICENSE and ../THIRD-PARTY.md. HollowConnect adaptations (2026).
using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using Hkmp.Logging;

namespace HollowConnect.Steam.Bridge;

/// <summary>
/// State of the client side of the bridge.
/// </summary>
internal enum SteamBridgeClientState {
    /// <summary>Waiting for the link (Steam connection) to be established.</summary>
    Connecting,
    /// <summary>Hello sent, waiting for the host response.</summary>
    Handshaking,
    /// <summary>Accepted: datagrams between the local HKMP client and the link are forwarded.</summary>
    Ready,
    /// <summary>The handshake or connection failed, see <see cref="SteamBridgeClientCore.FailureMessage"/>.</summary>
    Failed,
    /// <summary>The link was closed after it was ready.</summary>
    Closed
}

/// <summary>
/// Client side of the bridge: a local UDP socket on 127.0.0.1 that the HKMP client connects to as if it was a
/// server; datagrams are forwarded over the link to the host bridge.
/// Thread safety: <see cref="State"/>, <see cref="LocalPort"/> and <see cref="FailureMessage"/> may be read from
/// any thread, everything else is used from the worker thread.
/// </summary>
internal sealed class SteamBridgeClientCore : ISteamBridgePump {
    private readonly ISteamBridgeLink _link;
    private readonly string _password;
    private readonly SteamBridgeLimits _limits;
    private readonly Func<long> _clock;
    private readonly Socket _socket;
    private readonly byte[] _buffer = new byte[SteamBridgeProtocol.MaxDatagramSize + 1];

    private readonly long _createdAt;
    private long _handshakeStartedAt;
    private long _lastActivity;

    /// <summary>
    /// The end point of the local HKMP client (the last end point that sent a datagram to the local socket).
    /// </summary>
    private IPEndPoint _localClient;

    private int _state;
    private volatile string _failureMessage;
    private bool _disposed;

    /// <summary>
    /// Datagrams forwarded from the local client to the host.
    /// </summary>
    public long ForwardedToHost { get; private set; }

    /// <summary>
    /// Datagrams forwarded from the host to the local client.
    /// </summary>
    public long ForwardedToLocal { get; private set; }

    /// <summary>
    /// The current state.
    /// </summary>
    public SteamBridgeClientState State => (SteamBridgeClientState) Volatile.Read(ref _state);

    /// <summary>
    /// The handshake result reported by the host, if one was received.
    /// </summary>
    public SteamBridgeResult? HostResult { get; private set; }

    /// <summary>
    /// The reason of the failure or close, for the UI and logs.
    /// </summary>
    public string FailureMessage => _failureMessage;

    /// <summary>
    /// The local UDP port the HKMP client should connect to (on 127.0.0.1).
    /// </summary>
    public int LocalPort { get; }

    public SteamBridgeClientCore(ISteamBridgeLink link, string password, SteamBridgeLimits limits = null, Func<long> clock = null) {
        _link = link;
        _password = SteamBridgeProtocol.TrimPassword(password);
        _limits = limits ?? new SteamBridgeLimits();
        _clock = clock ?? SteamBridgeClock.NowMs;

        _socket = SteamBridgeUdp.CreateLoopbackSocket();
        LocalPort = ((IPEndPoint) _socket.LocalEndPoint).Port;

        _createdAt = _clock();
        _lastActivity = _createdAt;
    }

    /// <inheritdoc />
    public void Pump() {
        if (_disposed) {
            return;
        }

        var state = State;
        if (state == SteamBridgeClientState.Failed || state == SteamBridgeClientState.Closed) {
            return;
        }

        try {
            PumpInternal(state);
        } catch (Exception e) {
            Logger.Error($"Steam bridge client: exception:\n{e}");
            Fail(SteamBridgeClientState.Failed, $"Internal error / Error interno: {e.GetType().Name}");
        }
    }

    private void PumpInternal(SteamBridgeClientState state) {
        var now = _clock();
        var linkState = _link.State;

        if (linkState == SteamBridgeLinkState.Closed) {
            var why = _link.CloseDescription ?? "connection closed / conexión cerrada";
            if (state == SteamBridgeClientState.Ready) {
                Fail(SteamBridgeClientState.Closed, why);
            } else {
                // The host may have answered right before closing
                if (_link.TryReceive(out var lastData) && SteamBridgeProtocol.TryParseResponse(lastData, out var result)) {
                    HostResult = result;
                    why = SteamBridgeProtocol.Describe(result);
                }

                Fail(SteamBridgeClientState.Failed, why);
            }

            return;
        }

        switch (state) {
            case SteamBridgeClientState.Connecting:
                if (linkState == SteamBridgeLinkState.Connected) {
                    var hello = SteamBridgeProtocol.BuildHello(_password);
                    if (!_link.Send(hello, hello.Length, true)) {
                        Fail(SteamBridgeClientState.Failed, "Could not send handshake / No se pudo enviar el saludo");
                        return;
                    }

                    _handshakeStartedAt = now;
                    SetState(SteamBridgeClientState.Handshaking);
                    Logger.Info($"Steam bridge client: connected to {_link.Description}, handshake sent");
                } else if (now - _createdAt > _limits.ConnectTimeoutMs) {
                    Fail(SteamBridgeClientState.Failed, "Could not reach host / No se pudo contactar al anfitrión");
                }

                return;
            case SteamBridgeClientState.Handshaking:
                if (_link.TryReceive(out var data)) {
                    if (!SteamBridgeProtocol.TryParseResponse(data, out var result)) {
                        Fail(SteamBridgeClientState.Failed, SteamBridgeProtocol.Describe(SteamBridgeResult.Malformed));
                        return;
                    }

                    HostResult = result;
                    if (result != SteamBridgeResult.Accepted) {
                        Fail(SteamBridgeClientState.Failed, SteamBridgeProtocol.Describe(result));
                        return;
                    }

                    _lastActivity = now;
                    SetState(SteamBridgeClientState.Ready);
                    Logger.Info($"Steam bridge client: accepted by host, local port {LocalPort}");
                } else if (now - _handshakeStartedAt > _limits.ResponseTimeoutMs) {
                    Fail(SteamBridgeClientState.Failed, SteamBridgeProtocol.Describe(SteamBridgeResult.TimedOut));
                }

                return;
            case SteamBridgeClientState.Ready:
                Forward(now);
                return;
        }
    }

    private void Forward(long now) {
        // Local HKMP client -> host
        var received = 0;
        while (received < _limits.MaxMessagesPerPump &&
               SteamBridgeUdp.TryReceive(_socket, _buffer, out var length, out var sender)) {
            received++;
            if (length == 0 || length > SteamBridgeProtocol.MaxDatagramSize || !IPAddress.IsLoopback(sender.Address) ||
                _localClient != null && !_localClient.Equals(sender)) {
                continue;
            }

            if (_localClient == null || !_localClient.Equals(sender)) {
                Logger.Info($"Steam bridge client: local HKMP client at {sender}");
                _localClient = sender;
            }

            if (_link.Send(_buffer, length, false)) {
                ForwardedToHost++;
            }
        }

        // Host -> local HKMP client
        var moved = 0;
        while (moved < _limits.MaxMessagesPerPump && _link.TryReceive(out var data)) {
            moved++;
            if (_localClient == null || data.Length == 0 || data.Length > SteamBridgeProtocol.MaxDatagramSize) {
                continue;
            }

            if (SteamBridgeUdp.TrySend(_socket, data, data.Length, _localClient)) {
                ForwardedToLocal++;
            }
        }

        if (received > 0 || moved > 0) {
            _lastActivity = now;
        } else if (now - _lastActivity > _limits.IdleTimeoutMs) {
            Fail(SteamBridgeClientState.Closed, "Idle timeout / Inactividad");
        }
    }

    private void SetState(SteamBridgeClientState state) {
        Volatile.Write(ref _state, (int) state);
    }

    private void Fail(SteamBridgeClientState state, string message) {
        _failureMessage = message;
        SetState(state);
        Logger.Info(
            $"Steam bridge client: {state}: {message}; datagrams to host {ForwardedToHost}, to local {ForwardedToLocal}"
        );

        try {
            _link.Close(SteamBridgeProtocol.CloseReasonBase, message);
        } catch (Exception e) {
            Logger.Warn($"Steam bridge client: error closing link: {e.Message}");
        }
    }

    /// <inheritdoc />
    public void Wait(int timeoutMicroseconds) {
        // Steam messages cannot be waited on, so poll; slower while not forwarding yet
        SteamBridgeSleep.Sleep(State == SteamBridgeClientState.Ready ? timeoutMicroseconds : 5000);
    }

    /// <summary>
    /// Close the link and local socket. Must be called from the worker thread (or after it stopped).
    /// </summary>
    public void Dispose() {
        if (_disposed) {
            return;
        }

        _disposed = true;

        var state = State;
        if (state != SteamBridgeClientState.Failed && state != SteamBridgeClientState.Closed) {
            _failureMessage = "Stopped / Detenido";
            SetState(SteamBridgeClientState.Closed);
        }

        try {
            _link.Close(SteamBridgeProtocol.CloseReasonBase, "client stopped");
        } catch (Exception e) {
            Logger.Warn($"Steam bridge client: error closing link: {e.Message}");
        }

        // If the HKMP client was using the bridge it may still be sending for a few seconds until it notices the
        // disconnect: keep the port open (dropping data) so it does not get "port unreachable" errors meanwhile
        SteamBridgeUdp.CloseLater(_socket, _localClient != null ? _limits.SocketLingerMs : 0);
    }
}

