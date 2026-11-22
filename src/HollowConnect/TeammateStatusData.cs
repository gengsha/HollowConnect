using System;
using System.Globalization;

namespace HollowConnect;

// Presence.Target and Roster.BossReturnScene were unused by those respective
// message kinds. Using them keeps the RC3 packet layout and mixed-version rooms.
internal static class TeammateStatusData {
    internal const int MaxMasks = 64;

    internal static ushort Pack(int white, int blue) => (ushort)(1 + (Cap(white) << 7) + Cap(blue));
    private static int Cap(int masks) => Math.Max(0, Math.Min(MaxMasks, masks));
    internal static bool Unpack(ushort packed, out int white, out int blue) {
        int value = packed - 1;
        white = value >> 7;
        blue = value & 127;
        if (packed > 0 && white <= MaxMasks && blue <= MaxMasks) return true;
        white = blue = 0;
        return false;
    }

    internal static string Relay(ushort packed, int? rtt, bool fresh) => string.Format(CultureInfo.InvariantCulture,
        "hud:{0}:{1}:{2}", Unpack(packed, out _, out _) ? packed : 0, rtt ?? -1, fresh ? 1 : 0);

    internal static bool Read(string wire, out int white, out int blue, out int? rtt, out bool fresh) {
        white = blue = 0;
        rtt = null;
        fresh = false;
        if (string.IsNullOrEmpty(wire) || !wire.StartsWith("hud:", StringComparison.Ordinal)) return false;
        var parts = wire.Split(':');
        if (parts.Length != 4 || !ushort.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var packed) ||
            !int.TryParse(parts[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out var latency)) return false;
        fresh = parts[3] == "1";
        if (fresh && latency >= 0) rtt = latency;
        return Unpack(packed, out white, out blue);
    }
}
