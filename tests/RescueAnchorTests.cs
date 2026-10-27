using System;
using HollowConnect;
using HollowConnect.Core;
using Hkmp.Networking.Packet;

internal static class RescueAnchorTests {
    private static void Check(bool ok) { if (!ok) throw new Exception("rescue anchor invariant failed"); }
    private static Party Fixture(string scene = "StoryArena") {
        var p = new Party();
        for (ushort id = 1; id <= 3; id++) {
            p.Join(id, "p" + id); p.Presence(id, 0, 1, scene, id, 5, true, false, 0, 0, 100);
            p.Members[id].CheckpointScene = "Town"; p.Members[id].CheckpointMarker = "RestBench";
        }
        Check(p.Down(2, 0, 1, scene, 2, 5, 100)); return p;
    }
    internal static void Register(Action<string, Action> test) {
        test("story rescue anchor changes coordinates within the same death without charging quota", () => {
            var p = Fixture(); var before = p.Members[2].LifeVersion;
            Check(p.MoveRescueAnchor(2, 0, 1, 1, "StoryArena", 40, 8, 100));
            var m = p.Members[2];
            Check(m.X == 40 && m.Y == 8 && m.DownSerial == 1 && m.Life == Life.Down && m.LifeVersion == before + 1);
            Check(m.RescuesUsed == 0 && m.RescueAnchorSerial == 1 && p.Round == 0 && !p.Resetting);
            Check(p.Members[1].X == 1 && p.Members[3].Life == Life.Alive);
        });
        test("story rescue distance uses the confirmed anchor and stale presence cannot undo it", () => {
            var p = Fixture(); Check(p.MoveRescueAnchor(2, 0, 1, 1, "StoryArena", 40, 8, 100));
            p.Presence(2, 0, 2, "StoryArena", 2, 5, false, false, 0, 0, 100);
            Check(p.Members[2].X == 40 && !p.CanRescue(1, 2, 0, 1, 100));
            p.Presence(1, 0, 2, "StoryArena", 39, 8, true, false, 0, 0, 100);
            Check(p.Revive(1, 2, 0, 1, 100) && p.Members[2].RescuesUsed == 1);
            Check(!p.MoveRescueAnchor(2, 0, 1, 2, "StoryArena", 80, 8, 100));
        });
        test("story rescue anchor rejects duplicate older wrong death round and scene requests", () => {
            var p = Fixture(); Check(p.MoveRescueAnchor(2, 0, 1, 2, "StoryArena", 8, 5, 100));
            var version = p.Members[2].LifeVersion;
            Check(!p.MoveRescueAnchor(2, 0, 1, 2, "StoryArena", 9, 5, 100));
            Check(!p.MoveRescueAnchor(2, 0, 1, 1, "StoryArena", 9, 5, 100));
            Check(!p.MoveRescueAnchor(2, 0, 2, 3, "StoryArena", 9, 5, 100));
            Check(!p.MoveRescueAnchor(2, 1, 1, 3, "StoryArena", 9, 5, 100));
            Check(!p.MoveRescueAnchor(2, 0, 1, 3, "AnotherRoom", 9, 5, 100));
            Check(p.Members[2].X == 8 && p.Members[2].LifeVersion == version);
        });
        test("story rescue anchor rejects nonfinite out of bounds stale and zero revision data", () => {
            var p = Fixture();
            foreach (var x in new[] { float.NaN, float.PositiveInfinity, -100001f })
                Check(!p.MoveRescueAnchor(2, 0, 1, 1, "StoryArena", x, 5, 100));
            Check(!p.MoveRescueAnchor(2, 0, 1, 1, "StoryArena", 5, float.NaN, 100));
            Check(!p.MoveRescueAnchor(2, 0, 1, 0, "StoryArena", 5, 5, 100));
            Check(!p.MoveRescueAnchor(2, 0, 1, uint.MaxValue, "StoryArena", 5, 5, 100));
            Check(!p.MoveRescueAnchor(2, 0, 1, 1, "StoryArena", 5, 5, 103));
        });
        test("Godhome rejects story rescue reanchoring and retains original coordinates", () => {
            var p = Fixture("GG_Vengefly");
            Check(!p.MoveRescueAnchor(2, 0, 1, 1, "GG_Vengefly", 40, 8, 100));
            Check(p.Members[2].X == 2 && p.Members[2].RescueAnchorSerial == 0 && p.CanRescue(1, 2, 0, 1, 100));
        });
        test("story rescue anchor cannot modify returning spectating exhausted or resetting players", () => {
            foreach (var life in new[] { Life.Alive, Life.Loading, Life.Returning, Life.Spectating, Life.Resetting }) {
                var p = Fixture(); p.Members[2].Life = life;
                Check(!p.MoveRescueAnchor(2, 0, 1, 1, "StoryArena", 8, 5, 100));
            }
            var exhausted = Fixture(); exhausted.RescueLimit = 1; exhausted.Members[2].RescuesUsed = 1;
            Check(!exhausted.MoveRescueAnchor(2, 0, 1, 1, "StoryArena", 8, 5, 100));
            var wipe = Fixture(); Check(wipe.Down(1, 0, 1, "StoryArena", 1, 5, 100)); Check(wipe.Down(3, 0, 1, "StoryArena", 3, 5, 100));
            Check(wipe.BeginWipe() && !wipe.MoveRescueAnchor(2, 0, 1, 1, "StoryArena", 8, 5, 100));
        });
        test("story rescue anchor revision restarts on a new death and old death requests stay rejected", () => {
            var p = Fixture(); Check(p.MoveRescueAnchor(2, 0, 1, 9, "StoryArena", 2, 5, 100)); Check(p.Revive(1, 2, 0, 1, 100));
            Check(p.Down(2, 0, 2, "StoryArena", 2, 5, 100));
            Check(p.Members[2].RescueAnchorSerial == 0 && !p.MoveRescueAnchor(2, 0, 1, 10, "StoryArena", 40, 5, 100));
            Check(p.MoveRescueAnchor(2, 0, 2, 1, "StoryArena", 3, 5, 100) && p.Members[2].RescuesUsed == 1);
        });
        test("story rescue anchor messages roundtrip in reliable existing envelope", () => {
            foreach (var kind in new[] { Kind.RescueAnchorRequest, Kind.RescueAnchor, Kind.RescueUnavailable }) {
                var m = new Message { Kind = kind, Id = 2, Serial = 7, Aux = 9, Value = 3, Scene = "StoryArena", X = 40, Y = 8 };
                var packet = new Packet(); m.WriteData(packet); var result = new Message(); result.ReadData(new Packet(packet.ToArray()));
                Check(result.Kind == kind && result.IsReliable && result.Serial == 7 && result.Aux == 9 && result.Value == 3 && result.X == 40 && result.Y == 8);
                m.Value = -3; packet = new Packet(); m.WriteData(packet); result.ReadData(new Packet(packet.ToArray()));
                Check(result.Value == -3 && result.IsReliable);
            }
        });
        test("unsafe story revive returns the same accepted death exactly once without another quota charge", () => {
            var p = Fixture(); Check(p.Revive(1, 2, 0, 1, 100)); var m = p.Members[2]; var version = m.LifeVersion;
            Check(p.ReturnUnsafeRescue(2, 0, 1, version, "StoryArena"));
            Check(m.Life == Life.Returning && m.DownSerial == 1 && m.RescuesUsed == 1 && m.LifeVersion == version + 1 && p.Round == 0);
            Check(!p.ReturnUnsafeRescue(2, 0, 1, version, "StoryArena"));
        });
        test("unsafe revive return rejects unaccepted stale wrong room and Godhome episodes", () => {
            var p = Fixture(); Check(!p.ReturnUnsafeRescue(2, 0, 1, p.Members[2].LifeVersion, "StoryArena"));
            Check(p.Revive(1, 2, 0, 1, 100)); var version = p.Members[2].LifeVersion;
            Check(!p.ReturnUnsafeRescue(2, 1, 1, version, "StoryArena") && !p.ReturnUnsafeRescue(2, 0, 2, version, "StoryArena"));
            Check(!p.ReturnUnsafeRescue(2, 0, 1, version - 1, "StoryArena") && !p.ReturnUnsafeRescue(2, 0, 1, version, "AnotherRoom"));
            Check(p.Down(2, 0, 2, "StoryArena", 2, 5, 100) && !p.ReturnUnsafeRescue(2, 0, 1, version, "StoryArena"));
            var gg = Fixture("GG_Vengefly"); Check(gg.Revive(1, 2, 0, 1, 100));
            Check(!gg.ReturnUnsafeRescue(2, 0, 1, gg.Members[2].LifeVersion, "GG_Vengefly"));
        });
    }
}
