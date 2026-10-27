using System;
using System.Linq;
using HollowConnect;
using HollowConnect.Core;
using Hkmp.Networking.Packet;

internal static class DamageRankingTests {
    private static void Check(bool value) { if (!value) throw new Exception("damage ranking invariant failed"); }
    internal static void Register(Action<string, Action> test) {
        test("damage ranking attributes accepted reports once across authority handoff", () => {
            var board = new DamageRanking(1, ""); board.Join(1, "host", 10); board.Join(2, "guest", 20);
            Check(board.Add(1, 1, 2, 25, 0)); Check(!board.Add(1, 1, 2, 25, 0));
            Check(!board.Add(1, 0, 2, 25, 0)); Check(!board.Add(3, 1, 2, 25, 0));
            Check(!board.Add(1, 2, 3, 25, 0)); Check(!board.Add(1, 2, 1, -25, 0));
            Check(board.Add(2, 1, 1, 75, 0));
            var snapshot = board.Snapshot(); Check(snapshot.Total == 100);
            Check(snapshot.Players[0].Id == 1 && snapshot.Players[0].Damage == 75 && snapshot.Players[1].Damage == 25);
        });
        test("damage ranking pantheon survives rooms and settles only at run end", () => {
            var board = new DamageRanking(3, "BossSequenceTier5"); board.Join(1, "host", 1); board.Join(2, "guest", 1);
            Check(board.Add(1, 1, 1, 100, 0));
            board.Join(1, "host", 2); board.Join(2, "guest", 2);
            Check(board.Add(1, 2, 2, 200, 5)); Check(board.LastIndex == 5);
            Check(board.Snapshot().Total == 300 && !board.Finished);
            board.Finish(true); var revision = board.Revision; board.Finish(false);
            Check(!board.Add(1, 3, 1, 20, 5)); Check(board.Revision == revision && board.Completed);
            var retry = new DamageRanking(4, board.Sequence); retry.Join(1, "host", 3);
            Check(retry.Snapshot().Total == 0 && !retry.Finished);
        });
        test("damage ranking stable ties wide totals and immutable snapshots", () => {
            var board = new DamageRanking(1, ""); board.Join(2, "guest", 1); board.Join(1, "host", 1);
            Check(board.Add(1, 1, 1, int.MaxValue, 0)); Check(board.Add(1, 2, 2, int.MaxValue, 0));
            var snapshot = board.Snapshot(); Check(snapshot.Total == 4294967294L && snapshot.Players[0].Id == 1);
            snapshot.Players[0].Damage = 0; Check(board.Snapshot().Total == 4294967294L);
            board.Finish(false); Check(board.Finished && !board.Completed);
        });
        test("damage ranking wire host opt in and isolated event kinds roundtrip", () => {
            Check(!new Settings().BossDamageRanking);
            foreach (var kind in new[] { Kind.Welcome, Kind.Warp, Kind.DamageBegin, Kind.DamageHit, Kind.DamageEnd, Kind.DamageSnapshot }) {
                var source = new Message { Kind = kind, Value = 512, Session = "room", Scene = "GG_Hornet_1", Serial = 4,
                    Aux = 8, Target = 2, BossSequence = "BossSequenceTier5", BossIndex = 5, BossPlan = "{}",
                    BossReturnScene = "GG_Atrium_Roof", BossReturnGate = "door_dreamReturnGG_Boss_Door" };
                var packet = new Packet(); source.WriteData(packet);
                var copy = new Message(); copy.ReadData(new Packet(packet.ToArray()));
                Check(copy.Kind == kind && copy.Value == 512 && copy.Serial == 4 && copy.Aux == 8 && copy.Target == 2 &&
                    copy.BossSequence == source.BossSequence && copy.BossIndex == 5 && copy.BossPlan == "{}" && copy.IsReliable &&
                    copy.BossReturnScene == source.BossReturnScene && copy.BossReturnGate == source.BossReturnGate);
            }
        });
    }
}
