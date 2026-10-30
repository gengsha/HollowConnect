using System;
using HollowConnect.Core;

internal static class ObservationTests {
    private static void Check(bool v) { if (!v) throw new Exception("observation invariant"); }
    private static Party Two() {
        var p = new Party(); p.Join(1, "a"); p.Join(2, "b");
        p.Presence(1, 0, 1, "Arena", 2, 3, true, true, 1, 2, 10);
        p.Presence(2, 0, 1, "Arena", 4, 3, true, true, 1, 2, 10);
        p.Members[1].RescuesUsed = 2; return p;
    }
    internal static void Register(Action<string, Action> test) {
        test("observation start validates epoch serial target and freshness", () => {
            var p = Two(); Check(!p.Observe(1, 1, 0, 1, "Arena", 10));
            Check(!p.Observe(1, 2, 1, 1, "Arena", 10)); Check(!p.Observe(1, 2, 0, 0, "Arena", 10));
            Check(!p.Observe(1, 2, 0, 1, "Other", 10)); Check(!p.Observe(1, 2, 0, 1, "Arena", 12));
            Check(p.Observe(1, 2, 0, 1, "Arena", 10)); var version = p.Members[1].LifeVersion;
            Check(p.Observe(1, 2, 0, 1, "Arena", 10)); Check(p.Members[1].LifeVersion == version);
            Check(!p.Observe(2, 1, 0, 1, "Arena", 10));
        });
        test("observation presence cannot resurrect move anchor or create death", () => {
            var p = Two(); Check(p.Observe(1, 2, 0, 1, "Arena", 10));
            Check(p.Presence(1, 0, 2, "Other", 20, 30, true, true, 5, 6, 11));
            var m = p.Members[1]; Check(m.Life == Life.Observing && m.Scene == "Arena" && m.X == 2 && !m.HasMap);
            Check(!p.Down(1, 0, 1, "Arena", 2, 3, 11)); Check(!p.Rest(1, 0, 1, "Arena"));
            Check(m.DownSerial == 0 && m.RescuesUsed == 2 && p.Deaths.Get("a") == 0);
        });
        test("observation return validates identity destination and stale packets", () => {
            var p = Two(); Check(p.Observe(1, 2, 0, 1, "Arena", 10));
            Check(!p.ReturnObservation(1, 0, 2)); Check(p.ReturnObservation(1, 0, 1));
            Check(p.ReturnObservation(1, 0, 1)); Check(!p.FinishObservation(1, 0, 1, 5, "Arena", 2, 3, 11));
            Check(!p.FinishObservation(1, 0, 2, 5, "Town", 2, 3, 11));
            Check(!p.FinishObservation(1, 0, 1, 5, "Town", float.NaN, 3, 11));
            Check(p.FinishObservation(1, 0, 1, 5, "Town", 2, 3, 11)); var m = p.Members[1];
            Check(m.Life == Life.Alive && m.CheckpointMarker == "RestBench" && m.RescuesUsed == 2 && m.Sequence == 5);
            Check(!p.FinishObservation(1, 0, 1, 5, "Town", 2, 3, 11)); Check(!p.ReturnObservation(1, 0, 1));
            Check(!p.Presence(1, 0, 4, "Arena", 2, 3, true, true, 1, 2, 11));
        });
        test("observation cannot keep last fighter alive or block wipe barrier", () => {
            foreach (var returning in new[] { false, true }) {
                var p = Two(); Check(p.Observe(1, 2, 0, 1, "Arena", 10));
                Check(!p.BeginWipe()); if (returning) Check(p.ReturnObservation(1, 0, 1));
                Check(p.Down(2, 0, 1, "Arena", 4, 3, 10, true));
                Check(!p.ObservationHasFighter(p.Members[1], 10)); Check(p.BeginWipe());
                Check(p.Members[1].ReturnScene == "Town" && p.Members[1].ReturnMarker == "RestBench");
                Check(!p.FinishObservation(1, 0, 1, 5, "Town", 2, 3, 11));
                Check(p.Ready(1, 1, "Town")); Check(!p.TryRelease()); Check(p.Ready(2, 1, "Town")); Check(p.TryRelease());
                Check(p.Members[1].RescuesUsed == 2 && p.Members[1].DownSerial == 0);
            }
        });
        test("observation target room departure and disconnect end watchability", () => {
            var p = Two(); Check(p.Observe(1, 2, 0, 1, "Arena", 10)); Check(p.ObservationHasFighter(p.Members[1], 10));
            Check(!p.ObservationHasFighter(p.Members[1], 12));
            p.Presence(2, 0, 2, "Other", 2, 3, true, true, 1, 2, 11); Check(!p.ObservationHasFighter(p.Members[1], 11));
            p.Leave(2); Check(!p.ObservationHasFighter(p.Members[1], 11));
        });
    }
}
