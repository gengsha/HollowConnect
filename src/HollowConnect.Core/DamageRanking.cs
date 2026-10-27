using System;
using System.Collections.Generic;
using System.Linq;

namespace HollowConnect.Core;

// Ephemeral room statistics. Never persisted into a save or permanent progress.
public sealed class DamageStanding {
    public ushort Id;
    public string Name = "";
    public long Damage;
}

public sealed class DamageRankingSnapshot {
    public uint Board, Revision;
    public string Sequence = "";
    public bool Finished, Completed;
    public DamageStanding[] Players = Array.Empty<DamageStanding>();
    public long Total => Players.Sum(p => p.Damage);
}

public sealed class DamageRanking {
    private readonly Dictionary<ushort, DamageStanding> players = new();
    private readonly Dictionary<ushort, uint> reports = new();
    public readonly HashSet<ushort> Participants = new();
    public readonly Dictionary<ushort, uint> Visits = new();
    public uint Id { get; }
    public uint Revision { get; private set; }
    public string Sequence { get; }
    public bool Finished { get; private set; }
    public bool Completed { get; private set; }
    public int LastIndex { get; private set; }
    public DamageRanking(uint id, string sequence) { Id = id; Sequence = sequence ?? ""; }
    public void Join(ushort id, string name, uint visit) {
        Participants.Add(id); Visits[id] = visit;
        if (players.ContainsKey(id)) return;
        players[id] = new DamageStanding { Id = id, Name = name ?? "" }; Revision++;
    }
    public bool Add(ushort reporter, uint serial, ushort source, int amount, int index) {
        if (Finished || amount <= 0 || serial == 0 || !Participants.Contains(reporter) ||
            !players.TryGetValue(source, out var player) || reports.TryGetValue(reporter, out var previous) && serial <= previous) return false;
        reports[reporter] = serial; LastIndex = Math.Max(index, LastIndex);
        player.Damage += amount; Revision++; return true;
    }
    public void Finish(bool completed) {
        if (Finished) return;
        Finished = true; Completed = completed; Revision++;
    }
    public DamageRankingSnapshot Snapshot() => new() {
        Board = Id, Revision = Revision, Sequence = Sequence, Finished = Finished, Completed = Completed,
        Players = players.Values.OrderByDescending(p => p.Damage).ThenBy(p => p.Id)
            .Select(p => new DamageStanding { Id = p.Id, Name = p.Name, Damage = p.Damage }).ToArray()
    };
}
