using System;
using Hkmp.Game.Client.Entity;
using HollowConnect;
using Hkmp.Networking.Packet;

internal static class BossTargetCommitmentTests {
    internal static void Register(Action<string, Action> test) {
        void Check(bool value) { if (!value) throw new Exception("target commitment invariant"); }
        test("boss target commitment survives takeover with player ID and hold", () => {
            var choice = new BossTargetCommitment(); choice.Commit(17, 100, "session", 2);
            var copy = BossTargetCommitment.Decode(choice.Encode(), "session", 2);
            Check(copy.Player == 17 && copy.Until == 104 && !copy.CanSwitch(103.99, true, false));
            Check(copy.CanSwitch(104, true, false));
        });
        test("boss target commitment rejects prior sessions rounds and malformed snapshots", () => {
            foreach (var value in new[] { "old|2|17|104", "session|1|17|104", "session|2|-1|104", "session|2|65536|104",
                "session|2|17|NaN", "session|2|17|Infinity", "session|2|17", "" })
                Check(BossTargetCommitment.Decode(value, "session", 2).Player == -1);
        });
        test("boss target commitment cannot switch during attack or in inherited attacks after hold", () => {
            var choice = new BossTargetCommitment(); choice.Commit(1, 100, "s", 0);
            Check(!choice.CanSwitch(200, false, false));
            Check(!choice.CanSwitch(200, true, true));
            Check(!choice.CanSwitch(200, false, true));
            Check(choice.CanSwitch(200, true, false));
        });
        test("boss target decision boundaries exclude attack preparation and auxiliary names", () => {
            foreach (var state in new[] { "Idle", "Idle 2", "Chase", "Chase Hero" }) Check(BossTargetCommitment.IsDecisionState(state));
            foreach (var state in new[] { "Throw", "Throw Antic", "Aim", "Attack", "Fire", "Dash", "Idle Antic", null })
                Check(!BossTargetCommitment.IsDecisionState(state));
        });
        test("boss targeting host rule survives existing reliable welcome envelope", () => {
            var source = new Message { Kind = Kind.Welcome, Value = 256 | 64 | 2, Session = "room" };
            var packet = new Packet(); source.WriteData(packet);
            var target = new Message(); target.ReadData(new Packet(packet.ToArray()));
            Check(target.Value == source.Value && target.IsReliable && target.Session == "room");
        });
    }
}
