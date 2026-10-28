using System;
using System.Collections.Generic;

namespace HollowConnect.Core;

public enum LinkedRestKind { None, Bench, Spring }

public readonly struct LinkedRestCount : IEquatable<LinkedRestCount> {
    public readonly int Total, Benches, Springs;
    public int Resting => Benches + Springs;
    public bool Ready => Total > 0 && Resting == Total;
    public LinkedRestCount(int total, int benches, int springs) { Total = total; Benches = benches; Springs = springs; }
    public bool Equals(LinkedRestCount other) => Total == other.Total && Benches == other.Benches && Springs == other.Springs;
    public string Label => Benches > 0 && Springs > 0 ? "休息" : Springs > 0 ? "温泉" : "椅子";
}

// Membership includes loading/idle players. An expired heartbeat stops rest;
// nobody silently disappears from the denominator while still in the room.
public sealed class LinkedRestGroup {
    private readonly Dictionary<ushort, (LinkedRestKind Kind, double Seen, uint Epoch)> members = new();
    private LinkedRestCount last;
    private double nextHeal = double.PositiveInfinity;
    public void Join(ushort id) { if (!members.ContainsKey(id)) { members[id] = (LinkedRestKind.None, 0, 0); CancelHeal(); } }
    public void Leave(ushort id) { members.Remove(id); CancelHeal(); }
    public void Clear() { members.Clear(); last = default; CancelHeal(); }
    public void CancelHeal() => nextHeal = double.PositiveInfinity;
    public void Report(ushort id, LinkedRestKind kind, double now, uint epoch = 0) {
        if (!members.TryGetValue(id, out var previous)) return;
        if (epoch < previous.Epoch) return;
        if (previous.Kind != kind) CancelHeal();
        members[id] = (kind >= LinkedRestKind.None && kind <= LinkedRestKind.Spring ? kind : LinkedRestKind.None, now, epoch);
    }
    public void Interrupt(ushort id, uint epoch, double now) {
        if (!members.TryGetValue(id, out var previous)) return;
        members[id] = (LinkedRestKind.None, now, Math.Max(epoch, previous.Epoch)); CancelHeal();
    }
    public LinkedRestKind Kind(ushort id, double now) => members.TryGetValue(id, out var member) &&
        now >= member.Seen && now - member.Seen < 1.5 ? member.Kind : LinkedRestKind.None;
    public LinkedRestCount Count(double now) {
        var benches = 0; var springs = 0;
        foreach (var member in members.Values) {
            if (now < member.Seen || now - member.Seen >= 1.5) continue;
            if (member.Kind == LinkedRestKind.Bench) benches++;
            if (member.Kind == LinkedRestKind.Spring) springs++;
        }
        return new LinkedRestCount(members.Count, benches, springs);
    }
    // A short settling window absorbs get-up/entry packets. Benches refill;
    // an all-spring party receives one mask per second, independent of size.
    public int Healing(double now) {
        var count = Count(now);
        if (!count.Ready) { CancelHeal(); last = count; return 0; }
        if (!count.Equals(last) || double.IsPositiveInfinity(nextHeal)) nextHeal = now + .35;
        last = count;
        if (now < nextHeal) return 0;
        nextHeal = now + 1;
        return count.Benches > 0 ? 100 : 1;
    }
}
