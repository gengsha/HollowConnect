using System;
using System.Globalization;

namespace Hkmp.Game.Client.Entity;

// A player ID, never an avatar reference. Stored in the ordinary FSM snapshot
// so late entrants and a new native authority inherit the same attack target.
internal sealed class BossTargetCommitment {
    internal int Player = -1;
    internal double Until;
    internal string Session = "";
    internal uint Epoch;
    internal bool CanSwitch(double now, bool decisionBoundary, bool inherited) =>
        decisionBoundary && !inherited && now >= Until;
    internal void Commit(int player, double now, string session, uint epoch) {
        Player = player; Until = now + 4; Session = session; Epoch = epoch;
    }
    internal string Encode() => Player < 0 ? "" : string.Join("|", Session,
        Epoch.ToString(CultureInfo.InvariantCulture), Player.ToString(CultureInfo.InvariantCulture), Until.ToString("R", CultureInfo.InvariantCulture));
    internal static BossTargetCommitment Decode(string value, string session, uint epoch) {
        var result = new BossTargetCommitment();
        var fields = (value ?? "").Split('|');
        if (fields.Length == 4 && fields[0] == session && session.Length > 0 &&
            uint.TryParse(fields[1], NumberStyles.None, CultureInfo.InvariantCulture, out var round) && round == epoch &&
            int.TryParse(fields[2], NumberStyles.None, CultureInfo.InvariantCulture, out var player) && player >= 0 && player <= ushort.MaxValue &&
            double.TryParse(fields[3], NumberStyles.Float, CultureInfo.InvariantCulture, out var until) && !double.IsNaN(until) && !double.IsInfinity(until)) {
            result.Player = player; result.Until = until; result.Session = session; result.Epoch = epoch;
        }
        return result;
    }
    internal static bool IsDecisionState(string name) => name == "Idle" || name == "Idle 2" || name == "Idle 3" ||
        name == "Chase" || name == "Chase Hero" || name == "Chase Air" || name == "Chase Ground";
}
