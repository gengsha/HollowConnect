using System;
using System.Collections.Generic;
using System.Linq;
using Hkmp.Game.Server;
using HollowConnect.Core;
using Newtonsoft.Json;

namespace HollowConnect;

public sealed partial class CoopServerAddon {
    private bool damageRankingEnabled;
    private uint damageBoardSerial;
    private double nextDamageSnapshot;
    private readonly List<DamageRanking> damageBoards = new();
    private readonly Dictionary<(ushort Player, string Scene, uint Visit), DamageRanking> damageVisits = new();
    private readonly Dictionary<(ushort Player, string Attempt), DamageRanking> damageAttempts = new();
    private readonly Dictionary<uint, uint> sentDamageRevisions = new();
    private readonly Dictionary<uint, uint> failedDamageRevisions = new();
    private readonly Dictionary<uint, double> damageAbsentSince = new();
    private void ResetDamageRankings() {
        damageBoards.Clear(); damageVisits.Clear(); damageAttempts.Clear(); sentDamageRevisions.Clear(); failedDamageRevisions.Clear(); damageAbsentSince.Clear();
        damageBoardSerial = 0; nextDamageSnapshot = 0;
    }
    private void ReceiveDamageRanking(ushort id, Message m) {
        if (!damageRankingEnabled || party.Resetting || m.Aux == 0 ||
            !((ServerManager)api.ServerManager).TryGetCoopSceneScope(id, m.Scene, out var visit, out var authority) ||
            visit != m.Aux || !authority) return;
        var scope = (id, m.Scene, visit);
        if (m.Kind == Kind.DamageBegin) {
            if (damageVisits.TryGetValue(scope, out var existing)) { SendDamageSnapshot(existing); return; }
            DamageRanking board = null;
            var sequence = m.BossSequence;
            var occupants = party.Members.Keys.Where(p =>
                ((ServerManager)api.ServerManager).TryGetCoopSceneScope(p, m.Scene, out _, out _)).ToArray();
            if (sequence.Length > 0 && m.Key.Length > 0) {
                damageAttempts.TryGetValue((id, m.Key), out board);
                // An authority handoff or a late entrant keeps the current run.
                board ??= damageBoards.LastOrDefault(b => !b.Finished && b.Sequence == sequence &&
                    b.Participants.Overlaps(occupants) && m.BossIndex >= b.LastIndex &&
                    !damageAttempts.Any(p => p.Key.Player == id && p.Value == b && p.Key.Attempt != m.Key));
            }
            // A regular boss arena may also change authority before victory.
            else board = damageVisits.Where(p => p.Key.Scene == m.Scene && !p.Value.Finished &&
                ((ServerManager)api.ServerManager).TryGetCoopSceneScope(p.Key.Player, m.Scene, out var oldVisit, out _) && oldVisit == p.Key.Visit &&
                p.Value.Participants.Overlaps(occupants)).Select(p => p.Value).LastOrDefault();
            if (board == null || board.Finished) {
                board = new DamageRanking(++damageBoardSerial, sequence); damageBoards.Add(board);
            }
            foreach (var peer in occupants) {
                ((ServerManager)api.ServerManager).TryGetCoopSceneScope(peer, m.Scene, out var peerVisit, out _);
                board.Join(peer, party.Members[peer].Name, peerVisit);
            }
            board.Join(id, party.Members[id].Name, visit);
            damageVisits[scope] = board;
            if (sequence.Length > 0) damageAttempts[(id, m.Key)] = board;
            SendDamageSnapshot(board);
            return;
        }
        if (!damageVisits.TryGetValue(scope, out var current) || current.Finished) return;
        if (m.Kind == Kind.DamageHit) {
            if (!party.Members.ContainsKey(m.Target) || !current.Participants.Contains(m.Target)) return;
            current.Add(id, m.Serial, m.Target, m.Value, m.BossIndex);
        } else if (m.Kind == Kind.DamageEnd && (current.Sequence.Length == 0 || m.IsReady)) {
            current.Finish(true); SendDamageSnapshot(current);
        }
    }
    private void TickDamageRankings() {
        if (!damageRankingEnabled || Now < nextDamageSnapshot) return;
        nextDamageSnapshot = Now + .15;
        foreach (var board in damageBoards.ToArray()) {
            if (!board.Finished) {
                foreach (var active in damageVisits.Where(p => p.Value == board &&
                    ((ServerManager)api.ServerManager).TryGetCoopSceneScope(p.Key.Player, p.Key.Scene, out var activeVisit, out _) && activeVisit == p.Key.Visit).ToArray())
                    foreach (var peer in party.Members.Values)
                        if (((ServerManager)api.ServerManager).TryGetCoopSceneScope(peer.Id, active.Key.Scene, out var peerVisit, out _))
                            board.Join(peer.Id, peer.Name, peerVisit);
                var present = !party.Resetting && board.Participants.Any(id => party.Members.TryGetValue(id, out var member) &&
                    member.Life is not (Life.Returning or Life.Resetting) &&
                    (board.Sequence.Length == 0
                        ? damageVisits.Any(p => p.Value == board && p.Key.Player == id &&
                            ((ServerManager)api.ServerManager).TryGetCoopSceneScope(id, p.Key.Scene, out var visit, out _) && visit == p.Key.Visit)
                        : destinations.TryGetValue(id, out var destination) && destination.BossSequence == board.Sequence));
                if (present) damageAbsentSince.Remove(board.Id);
                else if (!damageAbsentSince.TryGetValue(board.Id, out var absent)) damageAbsentSince[board.Id] = Now;
                else if (Now - absent > 3) board.Finish(false);
            }
            if (!sentDamageRevisions.TryGetValue(board.Id, out var revision) || revision != board.Revision) SendDamageSnapshot(board);
        }
        // Keep only active runs and a small set of latest results. No save history.
        foreach (var old in damageBoards.Where(b => b.Finished).Reverse().Skip(12).ToArray()) {
            damageBoards.Remove(old); sentDamageRevisions.Remove(old.Id); failedDamageRevisions.Remove(old.Id); damageAbsentSince.Remove(old.Id);
            foreach (var key in damageVisits.Where(p => p.Value == old).Select(p => p.Key).ToArray()) damageVisits.Remove(key);
            foreach (var key in damageAttempts.Where(p => p.Value == old).Select(p => p.Key).ToArray()) damageAttempts.Remove(key);
        }
    }
    private void SendDamageSnapshot(DamageRanking board) {
        // A rejected enqueue is terminal for this revision. Retrying the same
        // invalid call every 150ms only floods logs and resends other peers.
        if (failedDamageRevisions.TryGetValue(board.Id, out var failedRevision) && failedRevision == board.Revision) return;
        failedDamageRevisions.Remove(board.Id);
        var json = JsonConvert.SerializeObject(board.Snapshot());
        if (json.Length > 24576) return;
        var delivered = true;
        var failed = false;
        foreach (var id in board.Participants) {
            if (!party.Members.ContainsKey(id)) continue;
            var result = Deliver(id, new Message { Kind = Kind.DamageSnapshot, Serial = board.Id, Aux = board.Revision, BossPlan = json, Scene = party.Members[id].Scene });
            if (result != DeliveryResult.Queued) delivered = false;
            if (result == DeliveryResult.Failed) failed = true;
        }
        // Queued means the reliable transport owns delivery. Retirement races
        // can retry after Leave; permanent errors have a separate failed state.
        if (delivered) sentDamageRevisions[board.Id] = board.Revision;
        if (failed) failedDamageRevisions[board.Id] = board.Revision;
    }
}
