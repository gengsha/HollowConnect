using System;
using System.Collections.Generic;
using System.Linq;

namespace HollowConnect.Core;

// A victory is scoped to the room visits present when the authority reports
// it. The client captures its current DownSerial when this notice arrives,
// after its own reliable Down; no ordering across two players is assumed.
public sealed class BossVictoryRecovery {
    private sealed class Victory {
        internal uint Round;
        internal string Scene;
        internal double Expires;
        internal Dictionary<ushort, uint> Visits;
        internal readonly HashSet<ushort> Used = new HashSet<ushort>();
        internal readonly HashSet<ushort> Settled = new HashSet<ushort>();
    }
    private readonly Dictionary<uint, Victory> victories = new Dictionary<uint, Victory>();
    private uint next;
    public uint Open(uint round, string scene, IDictionary<ushort, uint> visits, double now) {
        foreach (var key in victories.Where(p => p.Value.Expires < now || p.Value.Round != round).Select(p => p.Key).ToArray()) victories.Remove(key);
        if (string.IsNullOrEmpty(scene) || visits == null || visits.Count == 0 || victories.Count >= 128) return 0;
        var token = ++next;
        if (token == 0) token = ++next;
        victories[token] = new Victory { Round = round, Scene = scene, Visits = new Dictionary<ushort, uint>(visits), Expires = now + 15 };
        return token;
    }
    public bool Revive(Party party, ushort id, uint round, uint token, uint serial, string scene, uint visit, double now,
        out ushort survivor, Func<ushort, uint, bool> occupiesVisit = null) {
        survivor = 0;
        if (!victories.TryGetValue(token, out var victory) || victory.Round != round || victory.Scene != scene ||
            now > victory.Expires || victory.Settled.Contains(id) || !victory.Visits.TryGetValue(id, out var expected) || expected != visit) return false;
        foreach (var participant in victory.Visits.OrderBy(p => p.Key)) {
            if (participant.Key == id || victory.Used.Contains(participant.Key) ||
                occupiesVisit != null && !occupiesVisit(participant.Key, participant.Value) ||
                !party.ReviveAfterBoss(id, round, serial, scene, participant.Key, now)) continue;
            survivor = participant.Key; victory.Used.Add(id); victory.Settled.Add(id); return true;
        }
        return false;
    }
    public bool Acknowledge(uint round, uint token, ushort id, string scene, uint visit, double now) {
        if (!victories.TryGetValue(token, out var victory) || victory.Round != round || victory.Scene != scene ||
            now > victory.Expires || !victory.Visits.TryGetValue(id, out var expected) || expected != visit) return false;
        victory.Settled.Add(id); return true;
    }
    public bool IsSettled(uint token, ushort id) => victories.TryGetValue(token, out var victory) && victory.Settled.Contains(id);
}
