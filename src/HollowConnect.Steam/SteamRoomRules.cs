using System;
using System.Globalization;

namespace HollowConnect.Steam;

internal static class SteamRoomRules {
    internal const string Marker = "HollowConnect", Protocol = "hc-steam-1";
    internal const string KeyMarker = "hc_game", KeyProtocol = "hc_transport", KeyHost = "hc_host",
        KeySession = "hc_session", KeyReady = "hc_ready", KeyVersion = "hc_version";
    internal const int AppId = 367520, VirtualPort = 27361, MaxMembers = 16;

    internal static bool TryId(string text, out ulong id) {
        id = 0;
        return text != null && text.Length > 0 && text.Length <= 20 &&
            ulong.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out id) && id != 0;
    }

    internal static string ConnectString(ulong lobby) => "+connect_lobby " + lobby.ToString(CultureInfo.InvariantCulture);
    internal static bool TryCommand(string command, out ulong lobby) => TryArguments(
        command != null && command.Length <= 1024 ? command.Split(new[] {' ', '\t', '\r', '\n'},
            StringSplitOptions.RemoveEmptyEntries) : null, out lobby);

    internal static bool TryArguments(string[] args, out ulong lobby) {
        lobby = 0; var found = false;
        if (args == null || args.Length > 128) return false;
        for (var i = 0; i < args.Length; i++) {
            if (args[i] != "+connect_lobby") continue;
            if (found || ++i >= args.Length || !TryId(args[i], out lobby)) { lobby = 0; return false; }
            found = true;
        }
        return found;
    }

    internal static bool ValidMetadata(string marker, string protocol, string version, string host,
        string session, string ready, string expectedVersion, ulong actualOwner, out Guid nonce) {
        nonce = Guid.Empty;
        return marker == Marker && protocol == Protocol && version == expectedVersion && ready == "1" &&
            TryId(host, out var id) && id == actualOwner &&
            Guid.TryParseExact(session, "N", out nonce) && nonce != Guid.Empty;
    }
}

// A pending native CallResult is kept alive until it completes, even after a
// cancellation. Old results clean up their own lobby instead of reviving it.
internal sealed class SteamOperationEpoch {
    private int generation;
    internal int Begin() => ++generation;
    internal void Cancel() => ++generation;
    internal bool IsCurrent(int attempt) => attempt == generation;
}
