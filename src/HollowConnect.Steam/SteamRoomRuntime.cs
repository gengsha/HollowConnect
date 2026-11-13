using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Reflection;
using Hkmp.Game;
using HollowConnect.Steam.Bridge;
using Steamworks;
using UnityEngine;

namespace HollowConnect.Steam;

internal sealed partial class SteamRoomRuntime : MonoBehaviour {
#if !HOLLOW_UNITY6
    private static readonly FieldInfo InitializedField = typeof(SteamManager).GetField("m_bInitialized", BindingFlags.Instance | BindingFlags.NonPublic);
#endif
    private static readonly FieldInfo OnlineSubsystemField = typeof(DesktopPlatform).GetField("onlineSubsystem", BindingFlags.Instance | BindingFlags.NonPublic);
    private static readonly FieldInfo OnlineInitializedField = typeof(SteamOnlineSubsystem).GetField("didInitialize", BindingFlags.Instance | BindingFlags.NonPublic);
    private enum Phase { Idle, SelectingSave, StartingHost, CreatingLobby, JoiningLobby, ConnectingBridge, ConnectingGame, Playing }
    private readonly List<IDisposable> callbacks = new(), calls = new(), completedCalls = new();
    private readonly Dictionary<uint, SteamNativeLink> peers = new();
    private readonly SteamOperationEpoch epoch = new();
    private ExternalConnectionGateway gateway;
    private object lease;
    private Phase phase;
    private CSteamID local, lobby, originalHost;
    private HSteamListenSocket listener;
    private SteamBridgeHostCore hostBridge;
    private SteamBridgeClientCore clientBridge;
    private SteamBridgeWorker worker;
    private Guid session;
    private bool ready, hosting, menuAdded, quitting, cleaning;
    private string presence, username = "", status = "等待游戏的 Steam 服务", lastError = "";
    private double nextCheck, deadline;
    private ulong joiningLobby, pendingLobby;
    private bool pendingConfirmation;
    private static double Now => Time.realtimeSinceStartup;
    private bool Busy => phase != Phase.Idle || lease != null;
    private static bool NativeInitialized {
        get {
            // Never use SteamManager.Initialized: its getter instantiates a
            // manager and can restart the game. Reuse only the game's manager.
            var platform = Platform.Current as DesktopPlatform;
            var subsystem = platform != null ? OnlineSubsystemField?.GetValue(platform) as SteamOnlineSubsystem : null;
            if (subsystem != null && OnlineInitializedField != null && (bool)OnlineInitializedField.GetValue(subsystem)) return true;
#if HOLLOW_UNITY6
            return false;
#else
            var manager = UnityEngine.Object.FindObjectOfType<SteamManager>();
            return manager != null && InitializedField != null && (bool)InitializedField.GetValue(manager);
#endif
        }
    }

    private void Update() {
        if (Input.GetKeyDown(KeyCode.F10)) ToggleRoomMenu();
        try {
            foreach (var call in completedCalls) call.Dispose();
            completedCalls.Clear();
            gateway = ExternalConnectionGateway.Current;
            if (gateway != null && !menuAdded) {
                gateway.AddMenuEntry(OpenRoomMenu); menuAdded = true;
                if (!string.IsNullOrEmpty(gateway.Username)) username = gateway.Username;
            }
            if (Now >= nextCheck) {
                nextCheck = Now + 1;
                if (!NativeInitialized) {
                    if (ready) BackendLost("游戏的 Steam 服务已关闭");
                    else status = "请从 Steam 启动空洞骑士并保持在线";
                    return;
                }
                if (!SteamUser.BLoggedOn() || SteamUtils.GetAppID().m_AppId != SteamRoomRules.AppId) {
                    if (ready) BackendLost("Steam 已离线或游戏 App ID 不匹配");
                    else status = "Steam 尚未在线登录，或不是 Steam 版空洞骑士";
                    return;
                }
                if (!ready) {
                    local = SteamUser.GetSteamID(); BindCallbacks(); ready = true;
                    status = "Steam 已就绪"; ReadLaunchIntent();
                }
                if (lobby.m_SteamID != 0) ValidateMembership();
            }
            if (!ready) return;
            if (phase == Phase.Idle && worker != null && !worker.IsRunning) { worker = null; hostBridge = null; clientBridge = null; }
            if (worker != null && !worker.IsRunning) { Fail("Steam 数据桥接已停止"); return; }
            if (deadline != 0 && Now >= deadline) { Fail("创建或连接房间超时，请重试"); return; }
            if (phase == Phase.StartingHost && gateway.IsConnected) CreateLobby();
            if (phase == Phase.ConnectingBridge) {
                if (clientBridge.State == SteamBridgeClientState.Ready) {
                    gateway.Connect(lease, clientBridge.LocalPort, username, false);
                    phase = Phase.ConnectingGame; deadline = Now + 45;
                    status = "Steam 已连接，正在加入游戏…";
                } else if (clientBridge.State == SteamBridgeClientState.Failed || clientBridge.State == SteamBridgeClientState.Closed) {
                    Fail("Steam 房间连接失败：" + clientBridge.FailureMessage); return;
                }
            }
            if (phase == Phase.ConnectingGame && gateway.IsConnected) {
                phase = Phase.Playing; deadline = 0; status = "已连接 Steam 房间；请按游戏提示选择并确认存档";
            }
            if ((phase == Phase.StartingHost || phase == Phase.CreatingLobby || phase == Phase.Playing || phase == Phase.ConnectingGame) &&
                !gateway.ClientActive) { Fail("游戏连接已结束"); return; }
            if (!hosting && phase == Phase.Playing && clientBridge != null && clientBridge.State != SteamBridgeClientState.Ready) {
                Fail("Steam 房主连接已结束"); return;
            }
            if (pendingLobby != 0 && !pendingConfirmation && !Busy && gateway != null && gateway.IsIdle &&
                ExternalConnectionGateway.AtTitle && UIManager.instance != null && InputHandler.Instance.acceptingInput)
                JoinPending();
            foreach (var key in peers.Where(p => p.Value.State == SteamBridgeLinkState.Closed).Select(p => p.Key).ToArray())
                peers.Remove(key);
        } catch (Exception e) { Fail("Steam 联机错误：" + e.GetType().Name + " " + e.Message); }
        finally { UpdateRoomMenu(); }
    }

    private void BindCallbacks() {
        if (callbacks.Count != 0) return;
        callbacks.Add(Callback<GameLobbyJoinRequested_t>.Create(m => Guard(() => QueueJoin(m.m_steamIDLobby.m_SteamID))));
        callbacks.Add(Callback<GameRichPresenceJoinRequested_t>.Create(m => Guard(() => {
            if (SteamRoomRules.TryCommand(m.m_rgchConnect, out var id)) QueueJoin(id);
        })));
        callbacks.Add(Callback<SteamNetConnectionStatusChangedCallback_t>.Create(m => Guard(() => ConnectionChanged(m))));
        callbacks.Add(Callback<LobbyChatUpdate_t>.Create(m => Guard(() => {
            if (m.m_ulSteamIDLobby == lobby.m_SteamID) ValidateMembership();
        })));
        callbacks.Add(Callback<LobbyDataUpdate_t>.Create(m => Guard(() => {
            if (m.m_ulSteamIDLobby == lobby.m_SteamID) {
                if (phase == Phase.JoiningLobby) ConnectHost();
                else ValidateMembership();
            }
        })));
    }

    private void ReadLaunchIntent() {
        if (SteamRoomRules.TryArguments(Environment.GetCommandLineArgs(), out var id)) { QueueJoin(id); return; }
        SteamApps.GetLaunchCommandLine(out var command, 1024);
        if (SteamRoomRules.TryCommand(command, out id)) QueueJoin(id);
    }

    private void QueueJoin(ulong id) {
        var target = new CSteamID(id);
        if (!target.IsValid() || !target.IsLobby() || id == lobby.m_SteamID || id == joiningLobby) return;
        pendingLobby = id; Visible = true;
        pendingConfirmation = Busy || gateway == null || !gateway.IsIdle || !ExternalConnectionGateway.AtTitle;
        status = pendingConfirmation ? "收到邀请；请结束当前游戏并返回主菜单，再点击加入邀请" : "收到邀请，等待主菜单就绪…";
    }

    private bool Begin() {
        // Let a cancelled native operation finish and clean up before starting
        // another. Steam lobby membership is per user: overlapping joins to
        // the same lobby could otherwise let an old result leave the new one.
        if (!ready || Busy || gateway == null || calls.Count != 0 || worker != null) return false;
        username = (username ?? "").Trim();
        if (username.Length == 0 || username.Length > 20 || username.Any(c => !char.IsLetterOrDigit(c))) {
            lastError = "请输入 1–20 位字母、汉字或数字的玩家名"; return false;
        }
        var candidate = new object();
        if (!gateway.Reserve(candidate)) { lastError = "请先结束当前连接并返回主菜单"; return false; }
        lease = candidate; lastError = ""; return true;
    }

    private void Host() {
        EnsureRoomMenu();
        if (!Begin()) return;
        hosting = true; originalHost = local; session = Guid.NewGuid();
        phase = Phase.SelectingSave; status = "请选择主持游戏使用的存档"; Visible = false;
        var attempt = epoch.Begin();
        gateway.SelectHostSave(lease, selected => Guard(() => {
            if (!epoch.IsCurrent(attempt)) return;
            if (!selected) { Leave("已取消创建房间"); return; }
            var port = gateway.StartHost(lease);
            if (port <= 0) { Fail("本机游戏服务器未启动"); return; }
            hostBridge = new SteamBridgeHostCore(new IPEndPoint(IPAddress.Loopback, port), session.ToString("N"));
            worker = new SteamBridgeWorker("HollowConnect Steam host", hostBridge); worker.Start();
            gateway.Connect(lease, port, username, true);
            phase = Phase.StartingHost; deadline = Now + 45; status = "正在启动主持游戏…";
        }));
    }

    private void Complete(IDisposable call) { calls.Remove(call); completedCalls.Add(call); }
    private void CreateLobby() {
        phase = Phase.CreatingLobby; deadline = Now + 30; status = "正在创建 Steam 好友房间…";
        listener = SteamNetworkingSockets.CreateListenSocketP2P(SteamRoomRules.VirtualPort, 0, null);
        if (listener.m_HSteamListenSocket == 0) { Fail("Steam P2P 监听失败"); return; }
        SteamNetworkingSockets.InitAuthentication();
        var attempt = epoch.Begin();
        CallResult<LobbyCreated_t> result = null;
        result = CallResult<LobbyCreated_t>.Create((created, io) => {
            Complete(result);
            Guard(() => {
                var createdLobby = new CSteamID(created.m_ulSteamIDLobby);
                if (!epoch.IsCurrent(attempt)) {
                    if (!io && created.m_eResult == EResult.k_EResultOK) SteamMatchmaking.LeaveLobby(createdLobby);
                    return;
                }
                if (io || created.m_eResult != EResult.k_EResultOK) { Fail("Steam 创建房间失败：" + created.m_eResult); return; }
                lobby = createdLobby;
                if (!SteamMatchmaking.SetLobbyJoinable(lobby, false) ||
                    !SetData(SteamRoomRules.KeyMarker, SteamRoomRules.Marker) ||
                    !SetData(SteamRoomRules.KeyProtocol, SteamRoomRules.Protocol) ||
                    !SetData(SteamRoomRules.KeyVersion, Hkmp.Version.String) ||
                    !SetData(SteamRoomRules.KeyHost, local.m_SteamID.ToString(System.Globalization.CultureInfo.InvariantCulture)) ||
                    !SetData(SteamRoomRules.KeySession, session.ToString("N")) ||
                    !SetData(SteamRoomRules.KeyReady, "1") || !SteamMatchmaking.SetLobbyJoinable(lobby, true)) {
                    Fail("发布 Steam 房间信息失败"); return;
                }
                PublishPresence(); phase = Phase.Playing; deadline = 0; status = "好友房间已创建，可以邀请好友";
            });
        });
        calls.Add(result); result.Set(SteamMatchmaking.CreateLobby(ELobbyType.k_ELobbyTypeFriendsOnly, SteamRoomRules.MaxMembers));
    }

    private bool SetData(string key, string value) => SteamMatchmaking.SetLobbyData(lobby, key, value);
    private string Data(string key) => SteamMatchmaking.GetLobbyData(lobby, key);
    private void JoinPending() {
        if (!Begin()) return;
        var id = pendingLobby; pendingLobby = 0; pendingConfirmation = false;
        hosting = false; joiningLobby = id; phase = Phase.JoiningLobby; deadline = Now + 30;
        status = "正在加入 Steam 好友房间…";
        var attempt = epoch.Begin();
        CallResult<LobbyEnter_t> result = null;
        result = CallResult<LobbyEnter_t>.Create((entered, io) => {
            Complete(result);
            Guard(() => {
                var enteredLobby = new CSteamID(entered.m_ulSteamIDLobby);
                var success = !io && entered.m_EChatRoomEnterResponse == (uint)EChatRoomEnterResponse.k_EChatRoomEnterResponseSuccess;
                if (!epoch.IsCurrent(attempt)) {
                    if (success && enteredLobby != lobby) SteamMatchmaking.LeaveLobby(enteredLobby);
                    return;
                }
                joiningLobby = 0;
                if (!success) { Fail("Steam 加入失败：" + (EChatRoomEnterResponse)entered.m_EChatRoomEnterResponse); return; }
                lobby = enteredLobby; ConnectHost();
            });
        });
        calls.Add(result); result.Set(SteamMatchmaking.JoinLobby(new CSteamID(id)));
    }

    private bool ReadMetadata(out Guid nonce, out CSteamID owner) {
        owner = SteamMatchmaking.GetLobbyOwner(lobby);
        return SteamRoomRules.ValidMetadata(Data(SteamRoomRules.KeyMarker), Data(SteamRoomRules.KeyProtocol),
            Data(SteamRoomRules.KeyVersion), Data(SteamRoomRules.KeyHost), Data(SteamRoomRules.KeySession),
            Data(SteamRoomRules.KeyReady), Hkmp.Version.String, owner.m_SteamID, out nonce);
    }

    private void ConnectHost() {
        if (hosting || phase != Phase.JoiningLobby || lobby.m_SteamID == 0) return;
        if (string.IsNullOrEmpty(Data(SteamRoomRules.KeyMarker))) { SteamMatchmaking.RequestLobbyData(lobby); return; }
        if (!ReadMetadata(out session, out originalHost) || originalHost == local || !IsMember(originalHost.m_SteamID)) {
            Fail("房间版本不兼容、尚未就绪或原房主已离开"); return;
        }
        var identity = new SteamNetworkingIdentity(); identity.SetSteamID64(originalHost.m_SteamID);
        SteamNetworkingSockets.InitAuthentication();
        var connection = SteamNetworkingSockets.ConnectP2P(ref identity, SteamRoomRules.VirtualPort, 0, null);
        if (connection.m_HSteamNetConnection == 0) { Fail("Steam P2P 连接启动失败"); return; }
        var link = new SteamNativeLink(connection, originalHost.m_SteamID);
        peers.Add(connection.m_HSteamNetConnection, link);
        clientBridge = new SteamBridgeClientCore(link, session.ToString("N"));
        worker = new SteamBridgeWorker("HollowConnect Steam guest", clientBridge); worker.Start();
        phase = Phase.ConnectingBridge; deadline = Now + 45; status = "正在连接 Steam 房主…";
    }

    private bool IsMember(ulong id) {
        if (lobby.m_SteamID == 0) return false;
        var count = Math.Min(SteamRoomRules.MaxMembers, SteamMatchmaking.GetNumLobbyMembers(lobby));
        for (var i = 0; i < count; i++) if (SteamMatchmaking.GetLobbyMemberByIndex(lobby, i).m_SteamID == id) return true;
        return false;
    }

    private void ValidateMembership() {
        if (lobby.m_SteamID == 0 || phase == Phase.JoiningLobby) return;
        if (!IsMember(local.m_SteamID) || !IsMember(originalHost.m_SteamID) ||
            !ReadMetadata(out var currentSession, out var owner) || owner != originalHost || currentSession != session) {
            Fail("原房间已结束，请重新获取房主邀请"); return;
        }
        foreach (var peer in peers.Values.ToArray())
            if (!IsMember(peer.Remote)) peer.Close(SteamBridgeProtocol.CloseReasonBase, "Lobby membership ended");
    }

    private void ConnectionChanged(SteamNetConnectionStatusChangedCallback_t change) {
        var connection = change.m_hConn; var info = change.m_info;
        if (info.m_eState == ESteamNetworkingConnectionState.k_ESteamNetworkingConnectionState_Connecting &&
            listener.m_HSteamListenSocket != 0 && info.m_hListenSocket == listener) {
            var id = info.m_identityRemote.GetSteamID64();
            if (!hosting || phase != Phase.Playing || !IsMember(id) || id == local.m_SteamID ||
                hostBridge == null || peers.Values.Any(p => p.Remote == id && p.State != SteamBridgeLinkState.Closed) ||
                peers.Count >= SteamRoomRules.MaxMembers - 1) {
                SteamNetworkingSockets.CloseConnection(connection, 1000, "Room unavailable", false); return;
            }
            if (SteamNetworkingSockets.AcceptConnection(connection) != EResult.k_EResultOK) {
                SteamNetworkingSockets.CloseConnection(connection, 1000, "Accept failed", false); return;
            }
            var link = new SteamNativeLink(connection, id);
            if (!hostBridge.TryAdd(link)) { link.Close(1000, "Room full"); return; }
            peers[connection.m_HSteamNetConnection] = link;
        }
        if (!peers.TryGetValue(connection.m_HSteamNetConnection, out var known)) return;
        if (info.m_eState == ESteamNetworkingConnectionState.k_ESteamNetworkingConnectionState_Connected &&
            (!IsMember(known.Remote) || info.m_identityRemote.GetSteamID64() != known.Remote))
            known.Close(1000, "Room identity changed");
        else if (info.m_eState == ESteamNetworkingConnectionState.k_ESteamNetworkingConnectionState_Connected &&
            TryGetConnectionPing(connection, out var ping))
            Hkmp.Logging.Logger.Info("Steam connection established: pingMs=" + ping +
                "; relayPOP=" + info.m_idPOPRelay.m_SteamNetworkingPOPID +
                "; remotePOP=" + info.m_idPOPRemote.m_SteamNetworkingPOPID);
        if (info.m_eState == ESteamNetworkingConnectionState.k_ESteamNetworkingConnectionState_ClosedByPeer ||
            info.m_eState == ESteamNetworkingConnectionState.k_ESteamNetworkingConnectionState_ProblemDetectedLocally) {
            known.Close(1000, "Steam peer disconnected");
            if (!hosting) Fail("Steam 房主连接已断开");
        }
    }

    private static bool TryGetConnectionPing(HSteamNetConnection connection, out int ping) {
#if HOLLOW_UNITY6
        var quick = new SteamNetConnectionRealTimeStatus_t();
        var lane = new SteamNetConnectionRealTimeLaneStatus_t();
        var ok = SteamNetworkingSockets.GetConnectionRealTimeStatus(connection, ref quick, 0, ref lane) == EResult.k_EResultOK;
#else
        var ok = SteamNetworkingSockets.GetQuickConnectionStatus(connection, out var quick);
#endif
        ping = quick.m_nPing;
        return ok;
    }
    private void PublishPresence() {
        presence = SteamRoomRules.ConnectString(lobby.m_SteamID);
        if (!SteamFriends.SetRichPresence("connect", presence)) status = "房间已创建；好友状态发布失败，请使用邀请按钮";
    }
    private void ClearPresence() {
        if (presence != null && SteamFriends.GetFriendRichPresence(local, "connect") == presence)
            SteamFriends.SetRichPresence("connect", "");
        presence = null;
    }

    private void Leave(string message) {
        if (cleaning) return;
        cleaning = true;
        try {
            epoch.Cancel(); deadline = 0; joiningLobby = 0;
            // Stop public entry points first, then retire only the game session
            // that this module owns. Never clear another mod's rich presence.
            if (ready) {
                if (hosting && lobby.m_SteamID != 0) {
                    Cleanup(() => SteamMatchmaking.SetLobbyJoinable(lobby, false));
                    Cleanup(() => SetData(SteamRoomRules.KeyReady, "0"));
                }
                Cleanup(ClearPresence);
                if (listener.m_HSteamListenSocket != 0) Cleanup(() => SteamNetworkingSockets.CloseListenSocket(listener));
            }
            listener = default;
            if (lease != null) Cleanup(() => gateway?.Release(lease));
            lease = null;
            if (worker != null) {
                if (worker.Stop()) worker = null;
                else lastError = "Steam 桥接线程仍在结束，请稍候";
            }
            if (worker == null) { hostBridge = null; clientBridge = null; }
            if (ready) {
                foreach (var link in peers.Values) Cleanup(() => link.Close(1000, "Room ended"));
                if (lobby.m_SteamID != 0) Cleanup(() => SteamMatchmaking.LeaveLobby(lobby));
            }
            peers.Clear(); lobby = default; originalHost = default; session = Guid.Empty;
            hosting = false; phase = Phase.Idle; status = message;
        } finally { cleaning = false; }
    }

    private void BackendLost(string message) {
        // Being offline does not shut down SteamAPI. Its listen sockets still
        // occupy their virtual ports and must be closed before reconnecting.
        // Only abandon native handles after the game itself lost its API owner.
        if (NativeInitialized) {
            try { Leave(message); } finally { ready = false; }
            return;
        }
        ready = false;
        foreach (var link in peers.Values) link.Abandon();
        presence = null;
        Leave(message);
    }
    private static void Cleanup(Action action) {
        try { action(); } catch (Exception e) { Hkmp.Logging.Logger.Warn("Steam cleanup could not complete: " + e.Message); }
    }
    private void Fail(string message) {
        lastError = message; Visible = true;
        Hkmp.Logging.Logger.Warn(message);
        try { Leave(message); } catch (Exception e) { Hkmp.Logging.Logger.Warn("Steam cleanup: " + e.Message); }
    }
    private void Guard(Action action) { if (quitting) return; try { action(); } catch (Exception e) { Fail(e.GetType().Name + ": " + e.Message); } }
    private void OnApplicationQuit() => Shutdown();
    private void OnDestroy() => Shutdown();
    private void Shutdown() {
        if (quitting) return; quitting = true;
        try { Leave("已关闭"); } catch (Exception e) { Hkmp.Logging.Logger.Warn("Steam shutdown: " + e.Message); }
        foreach (var callback in callbacks) callback.Dispose(); callbacks.Clear();
        foreach (var call in calls.Concat(completedCalls)) call.Dispose(); calls.Clear(); completedCalls.Clear();
    }
}
