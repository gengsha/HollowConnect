using System;
using System.Runtime.InteropServices;
using System.Threading;
using HollowConnect.Steam.Bridge;
using Steamworks;

namespace HollowConnect.Steam;

// Steamworks.NET 14 / SDK 1.48 as shipped with this game. The game alone
// owns SteamAPI.Init, RunCallbacks and Shutdown.
internal sealed class SteamNativeLink : ISteamBridgeLink {
    internal readonly HSteamNetConnection Handle;
    internal readonly ulong Remote;
    private readonly IntPtr[] messages = new IntPtr[1];
    private int closed;
    private string reason;
    public string Description => "Steam peer";
    public string CloseDescription => reason;
    public SteamBridgeLinkState State {
        get {
            if (Volatile.Read(ref closed) != 0 || !SteamNetworkingSockets.GetConnectionInfo(Handle, out var info))
                return SteamBridgeLinkState.Closed;
            switch (info.m_eState) {
                case ESteamNetworkingConnectionState.k_ESteamNetworkingConnectionState_Connected:
                    return SteamBridgeLinkState.Connected;
                case ESteamNetworkingConnectionState.k_ESteamNetworkingConnectionState_Connecting:
                case ESteamNetworkingConnectionState.k_ESteamNetworkingConnectionState_FindingRoute:
                    return SteamBridgeLinkState.Connecting;
                default: return SteamBridgeLinkState.Closed;
            }
        }
    }

    internal SteamNativeLink(HSteamNetConnection handle, ulong remote) { Handle = handle; Remote = remote; }
    public bool Send(byte[] buffer, int length, bool reliable) {
        if (Volatile.Read(ref closed) != 0 || length <= 0 || length > SteamBridgeProtocol.MaxDatagramSize || length > buffer.Length) return false;
        var pin = GCHandle.Alloc(buffer, GCHandleType.Pinned);
        try {
            // SDK 1.48 omits the convenience aliases. Reliable=8, NoNagle=1.
            return SteamNetworkingSockets.SendMessageToConnection(Handle, pin.AddrOfPinnedObject(), (uint)length,
                reliable ? 9 : 1, out _) == EResult.k_EResultOK;
        } finally { pin.Free(); }
    }

    public bool TryReceive(out byte[] data) {
        data = null;
        if (Volatile.Read(ref closed) != 0) return false;
        var count = SteamNetworkingSockets.ReceiveMessagesOnConnection(Handle, messages, 1);
        if (count <= 0) return false;
        var pointer = messages[0];
        try {
            var message = (SteamNetworkingMessage_t)Marshal.PtrToStructure(pointer, typeof(SteamNetworkingMessage_t));
            if (message.m_conn != Handle || message.m_pData == IntPtr.Zero ||
                message.m_cbSize <= 0 || message.m_cbSize > SteamBridgeProtocol.MaxDatagramSize) {
                data = Array.Empty<byte>(); return true;
            }
            data = new byte[message.m_cbSize];
            Marshal.Copy(message.m_pData, data, 0, data.Length);
            return true;
        } finally {
            // The old managed Release wrapper passes m_pfnRelease instead of
            // the original message pointer. Release that pointer explicitly.
            if (pointer != IntPtr.Zero) ReleaseMessage(pointer);
            messages[0] = IntPtr.Zero;
        }
    }

    [DllImport("steam_api64", EntryPoint = "SteamAPI_SteamNetworkingMessage_t_Release", CallingConvention = CallingConvention.Cdecl)]
    private static extern void ReleaseMessage(IntPtr message);

    public void Close(int closeReason, string debug) {
        if (Interlocked.Exchange(ref closed, 1) != 0) return;
        reason = debug;
        SteamNetworkingSockets.CloseConnection(Handle, closeReason, debug, true);
    }
    internal void Abandon() { reason = "Native Steam service stopped"; Interlocked.Exchange(ref closed, 1); }
}
