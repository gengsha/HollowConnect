using System;
using HollowConnect;
using HollowConnect.Core;

internal static class RescueRulesTests {
    private static void Check(bool value) { if (!value) throw new Exception("rescue rules invariant failed"); }
    private static Party Party() {
        var party = new Party();
        for (ushort id = 1; id <= 3; id++) {
            party.Join(id, "player" + id);
            party.Presence(id, 0, 1, id == 3 ? "Road" : "Boss", id, 0, true, false, 0, 0, 100, id != 3);
            party.Members[id].CheckpointScene = "Bench" + id;
            party.Members[id].CheckpointMarker = "RestBench";
        }
        return party;
    }
    private static void Down(Party p, ushort id, uint serial = 1) {
        Check(p.Down(id, p.Round, serial, "Boss", id, 0, 100, true));
    }
    internal static void Register(Action<string, Action> test) {
        test("voluntary spectate falls back to personal return after teammates leave the room", () => {
            var p = Party(); p.SpectateAfterExhaustion = true;
            // White Palace guards are ordinary enemies, so no boss-room
            // failure watcher automatically returns this shade.
            p.Members[2].Scene = "White_Palace_20";
            Check(p.Down(2, 0, 1, "White_Palace_20", 50, 10, 100, false));
            p.Members[1].Scene = p.Members[3].Scene = "White_Palace_03_hub";
            Check(!p.StartSpectating(2, 0, 1, "White_Palace_20"));
            Check(p.GiveUpOrSpectate(2, 0, 1, "White_Palace_20"));
            Check(p.Members[2].Life == Life.Returning && p.Members[2].ReturnScene == "Bench2");
            Check(p.Members[1].Life == Life.Alive && p.Members[3].Life == Life.Alive);
            Check(p.Members[2].DownSerial == 1 && p.Members[2].RescuesUsed == 0 && !p.Resetting);
            Check(!p.GiveUpOrSpectate(2, 0, 1, "White_Palace_20"));
        });
        test("voluntary spectate fallback preserves same room spectating and rejects stale requests", () => {
            var p = Party(); p.SpectateAfterExhaustion = true; Down(p, 2);
            Check(!p.GiveUpOrSpectate(2, 1, 1, "Boss") && !p.GiveUpOrSpectate(2, 0, 2, "Boss") &&
                !p.GiveUpOrSpectate(2, 0, 1, "Road"));
            Check(p.GiveUpOrSpectate(2, 0, 1, "Boss") && p.Members[2].Life == Life.Spectating);
            p = Party(); p.SpectateAfterExhaustion = true; Down(p, 2); p.Members[1].Scene = "Road";
            p.Members[2].CheckpointScene = "";
            Check(!p.GiveUpOrSpectate(2, 0, 1, "Boss") && p.Members[2].Life == Life.Down);
            p.Members[2].CheckpointScene = "Bench2"; p.Leave(3); Down(p, 1); Check(p.BeginWipe());
            Check(!p.GiveUpOrSpectate(2, 0, 1, "Boss"));
        });
        test("boss room failure ignores a returning or travelling teammate", () => {
            foreach (var finishedReturn in new[] { false, true }) {
                var p = Party(); p.Leave(3); Down(p, 2); Check(p.GiveUp(2, 0, 1));
                if (finishedReturn) Check(p.FinishReturn(2, 0, 1, 2, "Bench2", 7, 8, 101));
                Down(p, 1); var failed = p.FailBossRooms();
                Check(failed.Length == 1 && failed[0].Id == 1 && failed[0].Life == Life.Returning);
                Check(p.Members[2].Life == (finishedReturn ? Life.Alive : Life.Returning));
                Check(p.Round == 0 && !p.Resetting && p.FailBossRooms().Length == 0);
                Check(!p.Revive(2, 1, 0, 1, 101));
            }
        });
        test("boss room failure leaves living players in other rooms untouched", () => {
            var p = Party(); Down(p, 1); Down(p, 2); var failed = p.FailBossRooms();
            Check(failed.Length == 2 && p.Members[3].Life == Life.Alive && p.Members[3].Scene == "Road");
            Check(p.Members[1].ReturnScene == "Bench1" && p.Members[2].ReturnScene == "Bench2");
        });
        test("living or entering boss room teammate prevents early failure", () => {
            var p = Party(); Down(p, 1); Check(p.FailBossRooms().Length == 0);
            p.Members[2].Life = Life.Loading; Check(p.FailBossRooms().Length == 0);
            p.Members[2].Scene = "PreviousRoom";
            Check(p.FailBossRooms((id, scene) => id == 2).Length == 0);
        });
        test("ordinary exploration retains whole party failure rules", () => {
            var p = Party(); p.Presence(1, 0, 2, "Road", 0, 0, true, false, 0, 0, 100);
            Check(p.Down(1, 0, 1, "Road", 0, 0, 100)); Check(p.FailBossRooms().Length == 0);
        });
        test("rescue limit counts successful revivals per player and restores only at bench", () => {
            var p = Party(); p.RescueLimit = 1; p.SpectateAfterExhaustion = true;
            Down(p, 2); Check(p.Revive(1, 2, 0, 1, 100)); Check(p.Members[2].RescuesUsed == 1);
            Check(p.Members[1].RescuesUsed == 0 && !p.Revive(1, 2, 0, 1, 100));
            Down(p, 2, 2); Check(p.Members[2].Life == Life.Spectating && !p.Revive(1, 2, 0, 2, 100));
            Check(!p.Rest(2, 0, 1, "Boss")); Check(p.GiveUp(2, 0, 2));
            Check(p.FinishReturn(2, 0, 2, 2, "Bench2", 2, 0, 101)); Check(p.Members[2].RescuesUsed == 1);
            Check(p.Rest(2, 0, 1, "Bench2")); Check(p.Members[2].RescuesUsed == 0);
            Check(!p.Rest(2, 0, 1, "Bench2") && !p.Rest(2, 1, 2, "Bench2"));
        });
        test("exhausted rescue without spectating follows personal death route", () => {
            var p = Party(); p.RescueLimit = 1; p.Members[2].RescuesUsed = 1;
            Down(p, 2); Check(!p.CanRescue(1, 2, 0, 1, 100));
            Check(p.ReturnWithoutRescue(2) && p.Members[2].Life == Life.Returning);
            Check(p.Members[1].Life == Life.Alive && p.Members[2].RescuesUsed == 1);
        });
        test("spectating works with rescue disabled and cannot hold failed room open", () => {
            var p = Party(); p.RescueEnabled = false; p.SpectateAfterExhaustion = true;
            Down(p, 2); Check(p.Members[2].Life == Life.Spectating);
            Check(!p.CanRescue(1, 2, 0, 1, 100) && p.FailBossRooms().Length == 0);
            Down(p, 1); Check(p.FailBossRooms().Length == 2 && p.Members[3].Life == Life.Alive);
        });
        test("spectators and shadows together trigger one full party wipe", () => {
            var p = Party(); p.Leave(3); p.RescueLimit = 1; p.SpectateAfterExhaustion = true;
            p.Members[2].RescuesUsed = 1; Down(p, 2); Down(p, 1);
            Check(p.BeginWipe() && !p.BeginWipe()); Check(p.Members[2].RescuesUsed == 1);
        });
        test("boss victory revives exhausted spectators at a survivor without spending quota", () => {
            var p = Party(); p.RescueEnabled = false; p.SpectateAfterExhaustion = true; Down(p, 2);
            var victory = new BossVictoryRecovery();
            var token = victory.Open(0, "Boss", new System.Collections.Generic.Dictionary<ushort, uint> { [1] = 3, [2] = 7 }, 100);
            Check(!victory.Revive(p, 2, 0, token, 1, "Boss", 8, 101, out _));
            Check(victory.Revive(p, 2, 0, token, 1, "Boss", 7, 101, out var survivor) && survivor == 1);
            Check(p.Members[2].Life == Life.Alive && p.Members[2].RescuesUsed == 0 && p.Members[2].X == p.Members[1].X);
            Check(!victory.Revive(p, 2, 0, token, 1, "Boss", 7, 101, out _));
        });
        test("boss settlement preserves nail rescue allowance even after exhaustion", () => {
            var p = Party(); p.RescueLimit = 1; Down(p, 2);
            Check(p.ReviveAfterBoss(2, 0, 1, "Boss", 1, 101) && p.Members[2].RescuesUsed == 0);
            p.Members[2].RescuesUsed = 1; Down(p, 2, 2);
            Check(p.ReviveAfterBoss(2, 0, 2, "Boss", 1, 101) && p.Members[2].RescuesUsed == 1);
        });
        test("voluntary spectating preserves death identity budget and failure eligibility", () => {
            var p = Party(); Down(p, 2);
            Check(!p.StartSpectating(2, 0, 1, "Boss"));
            p.SpectateAfterExhaustion = true;
            Check(!p.StartSpectating(2, 1, 1, "Boss") && !p.StartSpectating(2, 0, 2, "Boss") && !p.StartSpectating(2, 0, 1, "Road"));
            Check(p.StartSpectating(2, 0, 1, "Boss"));
            Check(p.Members[2].DownSerial == 1 && p.Members[2].LifeVersion == 2 && p.Members[2].RescuesUsed == 0);
            Check(!p.StartSpectating(2, 0, 1, "Boss") && !p.Revive(1, 2, 0, 1, 101));
            Down(p, 1); Check(p.FailBossRooms().Length == 2);
        });
        test("delayed presence cannot resurrect spectator or move its battle anchor", () => {
            var p = Party(); p.RescueEnabled = false; p.SpectateAfterExhaustion = true; Down(p, 2);
            Check(p.Presence(2, 0, 2, "Road", 9, 9, true, true, 1, 1, 101));
            Check(p.Members[2].Life == Life.Spectating && p.Members[2].Scene == "Boss");
        });
        test("rescue settings preserve existing default and allow independent spectating", () => {
            var s = new Settings(); Check(s.Rescue && s.RescueLimit == 0 && !s.SpectateAfterExhaustion);
            s.Rescue = false; s.SpectateAfterExhaustion = true; Check(s.SpectateAfterExhaustion);
        });
        test("reconnecting to the same party does not refill rescue quota", () => {
            var p = Party(); p.Members[2].RescuesUsed = 3; p.Leave(2); p.Join(9, "player2");
            Check(p.Members[9].RescuesUsed == 3);
        });
        test("personal return clears boss scope before the next presence", () => {
            var p = Party(); Down(p, 2); Check(p.GiveUp(2, 0, 1));
            Check(p.FinishReturn(2, 0, 1, 2, "Bench2", 2, 0, 101));
            Check(p.Down(2, 0, 2, "Bench2", 2, 0, 102));
            Check(!p.Members[2].InBossBattle && p.FailBossRooms().Length == 0);
        });
    }
}
