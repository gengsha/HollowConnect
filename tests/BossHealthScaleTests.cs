using System;
using Hkmp.Game.Client.Entity;

internal static class BossHealthScaleTests {
    internal static void Register(Action<string, Action> test) {
        test("boss balance default curve and bounded invalid configuration", () => {
            void Equal(double a, double b) { if (Math.Abs(a - b) > 1e-6) throw new Exception(a + " != " + b); }
            Equal(BossHealthScale.Multiplier(1, .6, .6), 1);
            Equal(BossHealthScale.Multiplier(2, .6, .6), 1.6);
            Equal(BossHealthScale.Multiplier(3, .6, .6), 1.96);
            Equal(BossHealthScale.Multiplier(4, .6, .6), 2.176);
            Equal(BossHealthScale.Multiplier(256, .6, .6), 2.5);
            Equal(BossHealthScale.Multiplier(4, .5, 1), 2.5);
            Equal(BossHealthScale.Multiplier(3, .5, 0), 1.5);
            Equal(BossHealthScale.Multiplier(0, .6, .6), 1);
            Equal(BossHealthScale.Multiplier(3, double.NaN, .6), 1);
            Equal(BossHealthScale.Multiplier(3, .6, double.PositiveInfinity), 1);
            Equal(BossHealthScale.Multiplier(int.MaxValue, 100, 100), 10);
        });
        test("boss balance fractional poison and armor hits cannot round to permanent immunity", () => {
            foreach (var multiplier in new[] { 1.6, 1.96, 2.176, 10d }) {
                var budget = new BossDamageBudget(); var total = 0;
                for (var i = 0; i < 10000; i++) total += budget.Scale(1, multiplier);
                if (Math.Abs(total - 10000 / multiplier) >= 1) throw new Exception("fractional damage lost");
            }
        });
        test("boss balance count changes never create negative damage or accumulate a second multiplier", () => {
            var budget = new BossDamageBudget();
            if (budget.Scale(16, 1.6) != 10 || budget.Scale(16, 1.6) != 10 || budget.Scale(0, 1.6) != 0 ||
                budget.Scale(-1, 1.6) != -1 || budget.Scale(16, 1) != 16 || budget.Scale(16, 1.6) != 10 ||
                budget.Scale(16, double.NaN) != 16 || budget.Scale(int.MaxValue, 1) != int.MaxValue)
                throw new Exception("damage bound or disabled/solo passthrough failed");
        });
        test("boss balance host configuration uses existing reliable welcome layout", () => {
            var source = new HollowConnect.Message { Kind = HollowConnect.Kind.Welcome, Value = 64, X = .6f, Y = .6f };
            var packet = new Hkmp.Networking.Packet.Packet(); source.WriteData(packet);
            var target = new HollowConnect.Message(); target.ReadData(new Hkmp.Networking.Packet.Packet(packet.ToArray()));
            if (target.Kind != source.Kind || target.Value != 64 || target.X != source.X || target.Y != source.Y || !target.IsReliable)
                throw new Exception("host settings not preserved");
        });
    }
}
