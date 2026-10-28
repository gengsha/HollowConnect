using System;
using System.Collections.Generic;

namespace HollowConnect.Core;

// Rules shared by the room authority and the client presentation layer. The
// ordinary mask bonus is a transient room capacity; it must never be treated
// as a collected mask shard or persisted as a pickup receipt.
public static class LinkedHealthRules {
    public const int BaseHealth = 5;
    public const int MaxBonusMasks = 10;
    public const int MaxMaskPieces = 16;

    public static int NormalizeBonus(int value) => Math.Max(0, Math.Min(MaxBonusMasks, value));

    public static int MaskPieces(IEnumerable<string> receipts) {
        if (receipts == null) return 0;
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var id in receipts)
            if (ResourceCatalog.TryGet(id, out var source) && source.Kind == ResourceKind.MaskShard) seen.Add(id);
        return Math.Min(MaxMaskPieces, seen.Count);
    }

    public static int Capacity(int personalMax, int uniqueMaskPieces, int bonusMasks) {
        var personal = Math.Max(BaseHealth, personalMax);
        var collected = BaseHealth + MaskPiecesFromCount(uniqueMaskPieces) / 4;
        return Math.Max(personal, collected) + NormalizeBonus(bonusMasks);
    }

    public static int MaskPiecesFromCount(int count) => Math.Min(MaxMaskPieces, Math.Max(0, count));

    public static int ClampHealth(int health, int max) => Math.Max(0, Math.Min(Math.Max(0, max), health));

    public static int ApplyDelta(int current, int delta, int max) {
        if (delta > 100) delta = 100;
        if (delta < -100) delta = -100;
        return ClampHealth(current + delta, max);
    }
}

// One room bar. Report sequences are independent per connection; a gap is
// buffered so reliable reordering cannot discard a real heal or hit.
public sealed class LinkedHealthPool {
    private sealed class Reports {
        public uint Ack;
        public readonly SortedDictionary<uint, (int Delta, uint RestEpoch)> Pending = new();
    }
    private readonly Dictionary<ushort, Reports> reports = new Dictionary<ushort, Reports>();
    public bool Initialized { get; private set; }
    public int Health { get; private set; }
    public int MaxHealth { get; private set; }
    public uint Revision { get; private set; }
    private int initialPieces, initialReceipts, bonus;
    public void Join(ushort id) { if (!reports.ContainsKey(id)) reports[id] = new Reports(); }
    public void Leave(ushort id) => reports.Remove(id);
    public uint Ack(ushort id) => reports.TryGetValue(id, out var report) ? report.Ack : 0;
    public void Initialize(int health, int personalMax, int pieces, int extraMasks, int partialPieces = 0, int hostReceipts = -1) {
        if (Initialized) return;
        Initialized = true;
        initialReceipts = LinkedHealthRules.MaskPiecesFromCount(hostReceipts < 0 ? pieces : hostReceipts);
        initialPieces = Math.Max(initialReceipts, (Math.Max(5, Math.Min(9, personalMax)) - 5) * 4 + Math.Max(0, Math.Min(3, partialPieces)));
        bonus = LinkedHealthRules.NormalizeBonus(extraMasks);
        MaxHealth = Capacity(pieces);
        Health = LinkedHealthRules.ClampHealth(health + bonus, MaxHealth); Revision++;
    }
    private int Capacity(int receipts) => 5 + LinkedHealthRules.MaskPiecesFromCount(
        initialPieces + Math.Max(0, LinkedHealthRules.MaskPiecesFromCount(receipts) - initialReceipts)) / 4 + bonus;
    public bool UpdatePieces(int pieces) {
        if (!Initialized) return false;
        var next = Capacity(pieces);
        if (next <= MaxHealth) return false;
        if (Health > 0) Health += next - MaxHealth;
        MaxHealth = next; Revision++; return true;
    }
    public bool Report(ushort id, uint serial, int delta) => Report(id, serial, delta, 0, out _, out _);
    public bool Report(ushort id, uint serial, int delta, uint restEpoch, out bool damaged, out uint damageEpoch) {
        damaged = false; damageEpoch = 0;
        if (!Initialized || !reports.TryGetValue(id, out var track) || serial == 0 || serial <= track.Ack ||
            serial - track.Ack > 1024 || delta < -100 || delta > 100) return false;
        track.Pending[serial] = (delta, restEpoch);
        var changed = false;
        while (track.Pending.TryGetValue(track.Ack + 1, out var next)) {
            track.Pending.Remove(++track.Ack);
            // A zero bar is terminal until the party's checkpoint barrier
            // completes. A late heal can never revive only half the room.
            if (Health > 0) {
                if (next.Delta < 0) { damaged = true; damageEpoch = Math.Max(damageEpoch, next.RestEpoch); }
                Health = LinkedHealthRules.ApplyDelta(Health, next.Delta, MaxHealth);
            }
            changed = true;
        }
        if (changed) Revision++;
        return changed;
    }
    public void Refill() {
        if (!Initialized) return;
        Health = MaxHealth; Revision++;
        foreach (var report in reports.Values) { report.Ack = 0; report.Pending.Clear(); }
    }
    public bool Heal(int amount) {
        if (!Initialized || Health <= 0 || amount <= 0) return false;
        var next = LinkedHealthRules.ApplyDelta(Health, amount, MaxHealth);
        if (next == Health) return false;
        Health = next; Revision++; return true;
    }
    // Rest counts share the health snapshot. Version presentation changes as
    // well so a retransmitted older packet cannot restore stale progress.
    public void RestChanged() { if (Initialized) Revision++; }
}
