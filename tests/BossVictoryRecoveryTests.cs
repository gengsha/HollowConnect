using System;
using System.Collections.Generic;
using System.Linq;
using Hkmp.Game.Client.Entity;
using Hkmp.Networking.Packet;
using HollowConnect;
using HollowConnect.Core;
using HutongGames.PlayMaker;
using HutongGames.PlayMaker.Actions;

internal static class BossVictoryRecoveryTests {
    private static void Check(bool value) { if (!value) throw new Exception("Boss victory recovery invariant failed"); }
    private static Party Party() {
        var party = new Party();
        foreach (ushort id in new ushort[] { 1, 2, 3 }) {
            party.Join(id, "test" + id); party.Presence(id, 0, 1, id == 3 ? "Other" : "Boss", 100 * id, 0, true, false, 0, 0, 100);
            party.Members[id].CheckpointScene = "Town";
        }
        return party;
    }
    private static bool Down(Party party, ushort id, uint serial = 1) {
        var member = party.Members[id]; return party.Down(id, party.Round, serial, member.Scene, member.X, member.Y, 100);
    }
    private static uint Open(BossVictoryRecovery recovery, double now = 100) => recovery.Open(0, "Boss", new Dictionary<ushort, uint> { [1] = 7, [2] = 9 }, now);
    internal static void Register(Action<string, Action> test) {
        test("boss victory revives the matching shadow at any same-room distance exactly once", () => {
            var party = Party(); Down(party, 2); var recovery = new BossVictoryRecovery(); var token = Open(recovery);
            Check(recovery.Revive(party, 2, 0, token, 1, "Boss", 9, 101, out _));
            Check(party.Members[2].Life == Life.Alive && party.Members[2].LifeVersion == 2);
            Check(!recovery.Revive(party, 2, 0, token, 1, "Boss", 9, 101, out _));
            Check(!Down(party, 2, 1)); Check(Down(party, 2, 2));
            Check(!recovery.Revive(party, 2, 0, token, 2, "Boss", 9, 102, out _));
        });
        test("boss victory accepts a Down arriving after the authority victory report", () => {
            var party = Party(); var recovery = new BossVictoryRecovery(); var token = Open(recovery);
            Check(!recovery.Revive(party, 2, 0, token, 1, "Boss", 9, 100, out _));
            Check(Down(party, 2)); Check(recovery.Revive(party, 2, 0, token, 1, "Boss", 9, 101, out _));
        });
        test("boss victory rejects different rooms visits episodes rounds and expired tickets", () => {
            var party = Party(); Down(party, 2); Down(party, 3); var recovery = new BossVictoryRecovery(); var token = Open(recovery);
            Check(!recovery.Revive(party, 3, 0, token, 1, "Other", 9, 101, out _));
            Check(!recovery.Revive(party, 2, 0, token, 1, "Other", 9, 101, out _));
            Check(!recovery.Revive(party, 2, 0, token, 1, "Boss", 10, 101, out _));
            Check(!recovery.Revive(party, 2, 0, token, 2, "Boss", 9, 101, out _));
            Check(!recovery.Revive(party, 2, 1, token, 1, "Boss", 9, 101, out _));
            Check(!recovery.Revive(party, 2, 0, token, 1, "Boss", 9, 116, out _));
            Check(party.Members[2].Life == Life.Down && party.Members[2].LifeVersion == 1);
        });
        test("boss victory cannot interrupt accepted give-up or a whole-party retry", () => {
            var party = Party(); Down(party, 2); var recovery = new BossVictoryRecovery(); var token = Open(recovery);
            Check(party.GiveUp(2, 0, 1)); Check(!recovery.Revive(party, 2, 0, token, 1, "Boss", 9, 101, out _));
            party = Party(); Down(party, 1); Down(party, 2); Down(party, 3); Check(party.BeginWipe());
            Check(!recovery.Revive(party, 2, 0, token, 1, "Boss", 9, 101, out _));
            Check(!recovery.Revive(party, 2, party.Round, token, 1, "Boss", 9, 101, out _));
        });
        test("boss victory tickets copy room participants and remain bounded", () => {
            var party = Party(); Down(party, 2); var recovery = new BossVictoryRecovery();
            var visits = new Dictionary<ushort, uint> { [1] = 7, [2] = 9 }; var token = recovery.Open(0, "Boss", visits, 100); visits[2] = 10;
            Check(recovery.Revive(party, 2, 0, token, 1, "Boss", 9, 101, out _));
            for (var i = 1; i < 128; i++) Check(Open(recovery) != 0);
            Check(Open(recovery) == 0); Check(Open(recovery, 116) != 0);
        });
        test("settlement requires a fresh surviving original participant on its exact room visit", () => {
            var party = Party(); Down(party, 2); var recovery = new BossVictoryRecovery(); var token = Open(recovery);
            Check(!recovery.Revive(party, 2, 0, token, 1, "Boss", 9, 103, out _));
            party.Members[1].Seen = 103; party.Members[1].Life = Life.Loading;
            Check(!recovery.Revive(party, 2, 0, token, 1, "Boss", 9, 103, out _, (id, visit) => visit != 7));
            party.Members[1].Scene = "Other";
            party.Presence(3, 0, 2, "Boss", 9, 9, true, false, 0, 0, 103);
            Check(!recovery.Revive(party, 2, 0, token, 1, "Boss", 9, 103, out _));
            party.Members[1].Scene = "Boss";
            Check(recovery.Revive(party, 2, 0, token, 1, "Boss", 9, 103, out var survivor, (id, visit) => visit == 7));
            Check(survivor == 1 && party.Members[2].X == party.Members[1].X && party.Members[2].RescuesUsed == 0);
        });
        test("settlement receipts wait for all original visits without excluding a living acknowledged anchor", () => {
            var party = Party(); Down(party, 2); var recovery = new BossVictoryRecovery(); var token = Open(recovery);
            Check(!recovery.Acknowledge(0, token, 1, "Boss", 8, 101));
            Check(recovery.Acknowledge(0, token, 1, "Boss", 7, 101) && recovery.IsSettled(token, 1));
            Check(!recovery.IsSettled(token, 2));
            Check(recovery.Revive(party, 2, 0, token, 1, "Boss", 9, 101, out var survivor) && survivor == 1);
            Check(recovery.IsSettled(token, 2) && !recovery.Acknowledge(0, token, 2, "Boss", 9, 116));
            Check(Down(party, 1));
            Check(!recovery.Revive(party, 1, 0, token, 1, "Boss", 7, 101, out _));
        });
        test("boss recovery ignores minions armour and first-phase health deaths", () => {
            Check(!BossDefeatSignals.IsBoss(EntityType.Crawlid)); Check(!BossDefeatSignals.IsBoss(EntityType.VengeflySummon));
            Check(!BossDefeatSignals.IsFinalHealthDeath(EntityType.FalseKnightHead));
            Check(!BossDefeatSignals.IsFinalHealthDeath(EntityType.SoulMaster)); Check(!BossDefeatSignals.IsFinalHealthDeath(EntityType.SoulTyrant));
            Check(BossDefeatSignals.IsFinalHealthDeath(EntityType.SoulMasterPhase2)); Check(BossDefeatSignals.IsFinalHealthDeath(EntityType.WhiteDefender));
            Check(BossDefeatSignals.IsBoss(EntityType.AbsoluteRadiance));
            var actions = Array.Empty<FsmStateAction>();
            Check(!BossDefeatSignals.IsDefeatRecord(EntityType.FalseKnight, "FalseyControl", "Stunned", actions));
            Check(BossDefeatSignals.IsDefeatRecord(EntityType.HollowKnight, "Phase Control", "Die", actions));
            Check(!BossDefeatSignals.IsDefeatRecord(EntityType.HollowKnight, "Control", "Die", actions));
            actions = new FsmStateAction[] { new SetPlayerDataBool { boolName = "killedHornet", value = true } };
            Check(BossDefeatSignals.IsDefeatRecord(EntityType.Hornet, "Control", "Defeat", actions));
            Check(!BossDefeatSignals.IsDefeatRecord(EntityType.SoulMaster, "Mage Lord", "Fake Death", actions));
            actions = new FsmStateAction[] { new SetPlayerDataBool { boolName = "defeatedMantisLords", value = true } };
            Check(BossDefeatSignals.IsDefeatRecord(EntityType.MantisLordBattle, "Battle Control", "Defeat", actions));
            Check(!BossDefeatSignals.IsDefeatRecord(EntityType.Crawlid, "Control", "Defeat", actions));
            actions[0].Enabled = false; Check(!BossDefeatSignals.IsDefeatRecord(EntityType.Hornet, "Control", "Defeat", actions));
        });
        test("local story settlement copies only verified positive boss defeat metadata", () => {
            var actions = new FsmStateAction[] {
                new SetPlayerDataBool { boolName = "falseKnightDreamDefeated", value = true },
                new SetPlayerDataBool { boolName = "falseKnightOrbsCollected", value = true },
                new SetPlayerDataBool { boolName = "killedFalseKnight", value = false },
                new SetPlayerDataBool { boolName = "defeatedMantisLords", value = true, Enabled = false },
                new SetPlayerDataInt { intName = "dreamOrbs", value = 300 }
            };
            var flags = BossDefeatSignals.LocalDefeatFlags(EntityType.FalseKnight, "FalseyControl", "Set PlayerData", actions).ToArray();
            Check(flags.SequenceEqual(new[] { "falseKnightDreamDefeated" }));
            Check(!BossDefeatSignals.LocalDefeatFlags(EntityType.Crawlid, "Control", "Die", actions).Any());
        });
        test("Lost Kin corpse settlement accepts only its exact native defeat flag and terminal state", () => {
            var defeat = new SetPlayerDataBool { boolName = "infectedKnightDreamDefeated", value = true };
            var actions = new FsmStateAction[] { defeat,
                new SetPlayerDataBool { boolName = "infectedKnightOrbsCollected", value = true },
                new SetPlayerDataBool { boolName = "killedHornet", value = true },
                new SetPlayerDataInt { intName = "dreamOrbs", value = 400 }
            };
            Check(BossDefeatSignals.LocalDefeatFlags(EntityType.LostKinCorpse, "corpse", "Set Defeated", actions)
                .SequenceEqual(new[] { "infectedKnightDreamDefeated" }));
            Check(!BossDefeatSignals.IsDefeatRecord(EntityType.LostKinCorpse, "corpse", "Init", actions));
            Check(!BossDefeatSignals.IsDefeatRecord(EntityType.LostKinCorpse, "Control", "Set Defeated", actions));
            Check(!BossDefeatSignals.LocalDefeatFlags(EntityType.BrokenVesselCorpse, "corpse", "Set Defeated", actions).Any());
            Check(!BossDefeatSignals.IsBoss(EntityType.LostKinCorpse) && !BossDefeatSignals.IsFinalHealthDeath(EntityType.LostKinCorpse));
            defeat.value.Value = false;
            Check(!BossDefeatSignals.IsDefeatRecord(EntityType.LostKinCorpse, "corpse", "Set Defeated", actions));
            defeat.value.Value = true; defeat.Enabled = false;
            Check(!BossDefeatSignals.IsDefeatRecord(EntityType.LostKinCorpse, "corpse", "Set Defeated", actions));
        });
        test("only the exact Absolute Radiance terminal flag may continue a personal final cinematic", () => {
            bool Allows(EntityType type = EntityType.AbsoluteRadiance, string scene = "GG_Radiance", string fsm = "Control",
                string state = "Ending Scene", string variable = "ggCinematicEnding", bool value = true) =>
                BossDefeatSignals.IsAbsoluteEndingFlag(type, scene, fsm, state, variable, value);
            Check(Allows() && !Allows(value: false));
            Check(!Allows(type: EntityType.Radiance) && !Allows(type: EntityType.Crawlid));
            Check(!Allows(scene: "Dream_Final_Boss") && !Allows(fsm: "Phase Control") && !Allows(state: "Final Impact"));
            Check(!Allows(variable: "ggEndScene") && !Allows(variable: "bossDoorStateTier5") && !Allows(variable: ""));
        });
        test("boss victory messages preserve reliable visit and death identity", () => {
            foreach (var kind in new[] { Kind.BossDefeated, Kind.BossVictory, Kind.BossRecoveryRequest, Kind.BossRevived,
                Kind.SpectateRequest, Kind.Spectating, Kind.BossSettlementReady }) {
                var sent = new Message { Kind = kind, Session = "session", Round = 3, Target = 4, Serial = 8, Aux = 9, Scene = "Boss", Value = unchecked((int)uint.MaxValue) };
                var packet = new Packet(); sent.WriteData(packet); var received = new Message(); received.ReadData(new Packet(packet.ToArray()));
                Check(received.Kind == kind && received.IsReliable && !received.DropReliableDataIfNewerExists && received.Serial == 8 && received.Aux == 9 && received.Target == 4 && unchecked((uint)received.Value) == uint.MaxValue);
            }
            Check(new Settings().BossVictoryRescue);
        });
    }
}
