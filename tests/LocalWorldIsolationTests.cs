using System;
using System.Collections.Generic;
using System.Linq;
using Hkmp;
using Hkmp.Api.Addon;
using Hkmp.Game.Client.Entity;
using Hkmp.Game.Settings;
using Hkmp.Networking;
using Hkmp.Networking.Packet;
using Hkmp.Networking.Packet.Data;
using HollowConnect;

internal static class LocalWorldIsolationTests {
    private static void Check(bool ok, string reason) { if (!ok) throw new Exception(reason); }
    internal static void Register(Action<string, Action> test) {
        test("world-mode handshake preserves save, settings, roster and following packet fields", () => {
            foreach (var local in new[] { false, true }) foreach (var full in new[] { false, true }) {
                var source = new ServerInfo { ConnectionResult = ServerConnectionResult.Accepted,
                    LocalWorldOnly = local, FullSynchronisation = full, AddonOrder = new byte[] { 2, 0, 1 },
                    ServerSettingsUpdate = new ServerSettingsUpdate { ServerSettings = new ServerSettings() },
                    CurrentSave = new CurrentSave { NewForPlayer = true, SaveData = new Dictionary<ushort, byte[]> {
                        [17] = new byte[] { 0, 255, 3 }, [65000] = new byte[] { 8, 7 } } },
                    PlayerInfo = new List<(ushort, string)> { (1, "host"), (60000, "guest") } };
                var bytes = new Packet(); source.WriteData(bytes); bytes.Write(0x12345678);
                var input = new Packet(bytes.ToArray()); var result = new ServerInfo(); result.ReadData(input);
                Check(result.LocalWorldOnly == local && result.FullSynchronisation == full, "mode bits changed");
                Check(result.AddonOrder.SequenceEqual(source.AddonOrder) && result.CurrentSave.NewForPlayer &&
                    result.CurrentSave.SaveData[17].SequenceEqual(source.CurrentSave.SaveData[17]) &&
                    result.CurrentSave.SaveData[65000].SequenceEqual(source.CurrentSave.SaveData[65000]) &&
                    result.PlayerInfo.SequenceEqual(source.PlayerInfo), "mode bit shifted save or roster");
                Check(input.ReadInt() == 0x12345678, "handshake consumed subsequent field");
            }
        });
        test("incompatible addon rejection retains the old wire format", () => {
            var info = new ServerInfo { ConnectionResult = ServerConnectionResult.InvalidAddons,
                AddonData = new List<AddonData> { new AddonData("HollowConnect", CoopMod.NetworkVersion) } };
            var packet = new Packet(); info.WriteData(packet);
            var expected = new Packet(); expected.Write((byte)ServerConnectionResult.InvalidAddons);
            expected.Write((byte)1); expected.Write("HollowConnect"); expected.Write(CoopMod.NetworkVersion);
            Check(packet.ToArray().SequenceEqual(expected.ToArray()), "rejection cannot be read by older client");
            var result = new ServerInfo(); result.ReadData(new Packet(packet.ToArray()));
            Check(result.AddonData.Single().Version != CoopMod.Version, "old addon may accept the new layout");
        });
        test("local worlds retain boss roots while ordinary enemies and mechanisms stay native", () => InMode(true, () => {
            foreach (EntityType type in Enum.GetValues(typeof(EntityType))) {
                var entry = new EntityRegistryEntry { Type = type };
                if (BossDefeatSignals.IsBoss(type)) Check(LocalWorldIsolation.Allows(entry), "boss excluded: " + type);
            }
            foreach (var type in new[] { EntityType.Crawlid, EntityType.ShrumalOgre, EntityType.AspidMother,
                EntityType.CityElevator, EntityType.CrystalPeakPlatform, EntityType.DreamPlatform,
                EntityType.ColosseumManager, EntityType.GrimmkinSpawner, EntityType.EncounterSummoner })
                Check(!LocalWorldIsolation.Allows(new EntityRegistryEntry { Type = type }), "world object shared: " + type);
        }));
        test("trusted boss spawns can share ordinary summon types but never local barriers", () => InMode(true, () => {
            foreach (var type in new[] { EntityType.Crawlid, EntityType.InfectedBalloon, EntityType.DynamicHazard }) {
                var entry = new EntityRegistryEntry { Type = type };
                Check(!LocalWorldIsolation.Allows(entry) && LocalWorldIsolation.Allows(entry, true), "spawn inheritance failed: " + type);
                entry.Networked = false;
                Check(!LocalWorldIsolation.Allows(entry, true), "spawn ignored native-only entry");
            }
            foreach (EntityType type in Enum.GetValues(typeof(EntityType)))
                if (EntityRegistry.IsLocalOnlyBarrier(type))
                    Check(!LocalWorldIsolation.Allows(new EntityRegistryEntry { Type = type }, true), "spawn shared barrier: " + type);
        }));
        test("explicit scene boss support stays shared without sharing generic hazards", () => InMode(true, () => {
            var needle = new EntityRegistryEntry { Type = EntityType.DynamicHazard, BossSupport = true };
            Check(LocalWorldIsolation.Allows(needle), "scene boss attack excluded");
            Check(!LocalWorldIsolation.Allows(new EntityRegistryEntry { Type = EntityType.DynamicHazard }), "generic hazard shared");
            needle.Networked = false;
            Check(!LocalWorldIsolation.Allows(needle), "boss marker overrode native-only policy");
            foreach (EntityType type in Enum.GetValues(typeof(EntityType)))
                if (EntityRegistry.IsLocalOnlyBarrier(type))
                    Check(!LocalWorldIsolation.Allows(new EntityRegistryEntry { Type = type, BossSupport = true }), "boss marker shared barrier: " + type);
        }));
        test("colosseum moving walls follow shared battles and remain native in local worlds", () => {
            var wall = new EntityRegistryEntry { Type = EntityType.ColosseumWall };
            InMode(false, () => Check(LocalWorldIsolation.Allows(wall), "shared arena wall excluded"));
            InMode(true, () => Check(!LocalWorldIsolation.Allows(wall), "private arena wall shared"));
        });
        test("shared worlds preserve ordinary entity policy and local barrier exceptions", () => InMode(false, () => {
            foreach (EntityType type in Enum.GetValues(typeof(EntityType)))
                Check(LocalWorldIsolation.Allows(new EntityRegistryEntry { Type = type }) == !EntityRegistry.IsLocalOnlyBarrier(type), "shared-mode policy changed: " + type);
            Check(!LocalWorldIsolation.Allows(null), "null registry entry accepted");
        }));
    }
    private static void InMode(bool local, Action body) {
        var old = CoopContext.LocalWorldOnly;
        try { CoopContext.LocalWorldOnly = local; body(); }
        finally { CoopContext.LocalWorldOnly = old; }
    }
}
