using System;
using System.Collections.Generic;
using HollowConnect.Core;

internal static class GrimmFlameTests {
    internal static void Register(Action<string, Action> test) {
        void Check(bool value) { if (!value) throw new Exception("Grimm flame invariant"); }
        test("Grimm flames merge distinct locations for three peers without duplicate credit", () => {
            var room = new ProgressLedger(); var key = GrimmFlames.Key(1);
            Check(room.Merge(key, 1)); Check(!room.Merge(key, 1)); Check(room.Merge(key, 4));
            Check(!room.Merge(key, 4)); Check(room.Merge(key, 2)); Check(room.Get(key) == 7);
            Check(GrimmFlames.Count(room.Get(key)) == 3);
        });
        test("Grimm native receipts repair two collected flames with one counter", () => {
            var scenes = new List<string> { "Mines_10", "Fungus1_10", "Mines_10", "Town" };
            Check(GrimmFlames.Count(GrimmFlames.Evidence(1, scenes, false)) == 2);
            Check(GrimmFlames.Evidence(2, scenes, false) == 0);
        });
        test("Grimm round reset does not reuse earlier flames", () => {
            var room = new ProgressLedger(); room.Merge(GrimmFlames.Key(1), 7);
            Check(room.Get(GrimmFlames.Key(2)) == 0);
            Check(GrimmFlames.Key(4) == null && GrimmFlames.Evidence(4, new List<string> { "Mines_10" }, true) == 0);
        });
        test("Grimm receipts validate masks and respect charm sharing", () => {
            var room = new ProgressLedger(); Check(!room.Merge(GrimmFlames.Key(1), 8));
            Check(!room.Merge(GrimmFlames.Key(3), 16)); Check(!room.Merge(GrimmFlames.Key(1), -1));
            Check(!ProgressCatalog.CanShare(GrimmFlames.Key(1), true, false));
            Check(!ProgressCatalog.CanShare(GrimmFlames.Key(1), false, true));
            Check(GrimmFlames.Evidence(3, new List<string>(), true) == 8);
        });
    }
}
