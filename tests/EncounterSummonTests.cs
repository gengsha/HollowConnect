using System;
using Hkmp.Game.Client.Entity;
using Hkmp.Game.Client.Entity.Component;
using Hkmp.Networking.Packet;
using Hkmp.Networking.Packet.Data;

internal static class EncounterSummonTests {
    private static void Check(bool ok) { if (!ok) throw new Exception("encounter summon invariant failed"); }
    internal static void Register(Action<string, Action> test) {
        test("encounter summon requires authority registration before peer requests", () => {
            var state = new EncounterSummonState();
            Check(!state.Apply(EncounterSummonState.Register, false));
            Check(!state.Apply(EncounterSummonState.Request, false) && !state.Registered && !state.Requested);
            Check(state.Apply(EncounterSummonState.Register, true));
            Check(state.Apply(EncounterSummonState.Request, false) && state.State == EncounterSummonState.Accepted);
        });
        test("encounter summon receipt survives duplicate requests and authority registration on takeover", () => {
            var state = new EncounterSummonState(); state.Apply(EncounterSummonState.Register, true);
            Check(state.Apply(EncounterSummonState.Request, false));
            for (var i = 0; i < 20; i++) {
                Check(!state.Apply(EncounterSummonState.Request, i % 2 == 0));
                Check(!state.Apply(EncounterSummonState.Register, true));
                Check(state.Requested && state.State == EncounterSummonState.Accepted);
            }
        });
        test("encounter summon rejects client acknowledgements and unknown actions", () => {
            var state = new EncounterSummonState(); state.Apply(EncounterSummonState.Register, true);
            foreach (var action in new byte[] { EncounterSummonState.Accepted, 3, 127, 255 })
                Check(!state.Apply(action, false) && !state.Apply(action, true) && !state.Requested);
            Check(state.Apply(EncounterSummonState.Request, true));
        });
        test("encounter summon reliable snapshots preserve identity visit epoch and following packet fields", () => {
            var source = new ReliableEntityUpdate { Id = 0x123456789UL, SceneName = "Crossroads_08", Visit = 31, Epoch = 42 };
            source.UpdateTypes.Add(EntityUpdateType.Data);
            var data = new EntityNetworkData { Type = EntityComponentType.EncounterSummon };
            data.Packet.Write(EncounterSummonState.Accepted); source.GenericData.Add(data);
            var packet = new Packet(); source.WriteData(packet); packet.Write(0x12345678);
            var reader = new Packet(packet.ToArray()); var result = new ReliableEntityUpdate(); result.ReadData(reader);
            Check(result.IsReliable && result.Id == source.Id && result.SceneName == source.SceneName && result.Visit == 31 && result.Epoch == 42);
            Check(result.GenericData.Count == 1 && result.GenericData[0].Type == EntityComponentType.EncounterSummon &&
                result.GenericData[0].Packet.ReadByte() == EncounterSummonState.Accepted && reader.ReadInt() == 0x12345678);
        });
    }
}
