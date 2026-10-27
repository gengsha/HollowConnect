using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.Serialization;
using Hkmp.Api.Server;
using Hkmp.Api.Server.Networking;
using Hkmp.Game.Server;
using Hkmp.Logging;
using Hkmp.Networking.Packet;
using Hkmp.Networking.Server;
using HollowConnect;
using HollowConnect.Core;

internal static class SettlementDeliveryTests {
    private const BindingFlags Hidden = BindingFlags.Instance | BindingFlags.NonPublic;
    private static void Set(object owner, string name, object value) {
        for (var t = owner.GetType(); t != null; t = t.BaseType) {
            var f = t.GetField(name, Hidden | BindingFlags.Public | BindingFlags.DeclaredOnly);
            if (f != null) { f.SetValue(owner, value); return; }
        }
        throw new MissingFieldException(name);
    }
    private static T Get<T>(object owner, string name) => (T)owner.GetType().GetField(name, Hidden).GetValue(owner);
    private static object Call(object owner, string name, params object[] args) {
        try { return owner.GetType().GetMethod(name, Hidden).Invoke(owner, args); }
        catch (TargetInvocationException e) { throw e.InnerException; }
    }
    private static void Check(bool ok, string label) { if (!ok) throw new Exception(label); }
    private sealed class Errors : ILogger, IDisposable {
        internal readonly List<string> Messages = new();
        internal Errors() => Logger.AddLogger(this);
        public void Error(string message) => Messages.Add(message);
        public void Info(string message) { }
        public void Fine(string message) { }
        public void Debug(string message) { }
        public void Warn(string message) { }
        public void Dispose() => ((List<ILogger>)typeof(Logger).GetField("Loggers", BindingFlags.Static | BindingFlags.NonPublic).GetValue(null)).Remove(this);
    }
    private sealed class Sender : IServerAddonNetworkSender<Channel> {
        internal readonly List<(ushort Id, Message Message)> Sent = new();
        internal Action<ushort, Message> Before;
        public void SendCollectionData<T>(Channel channel, T data, ushort id) where T : IPacketData, new() {
            var m = (Message)(object)data; Before?.Invoke(id, m); Sent.Add((id, m));
        }
        public void SendCollectionData<T>(Channel channel, T data, params ushort[] ids) where T : IPacketData, new() {
            foreach (var id in ids) SendCollectionData(channel, data, id);
        }
        public void BroadcastCollectionData<T>(Channel channel, T data) where T : IPacketData, new() => Sent.Add((ushort.MaxValue, (Message)(object)data));
        public void SendSingleData(Channel channel, IPacketData data, ushort id) => throw new NotSupportedException();
        public void SendSingleData(Channel channel, IPacketData data, params ushort[] ids) => throw new NotSupportedException();
        public void BroadcastSingleData(Channel channel, IPacketData data) => throw new NotSupportedException();
    }
    private sealed class Fixture {
        internal readonly CoopServerAddon Addon = new();
        internal readonly Party Party;
        internal readonly Sender Sender = new();
        internal readonly ConcurrentDictionary<ushort, NetServerClient> Clients = new();
        internal readonly NetServer Net;
        internal readonly Dictionary<uint, (string Scene, Dictionary<ushort, uint> Visits, double Expires)> Settlements;
        internal Fixture() {
            // Isolate the real addon methods from native Unity startup. No
            // socket or game loop is started; only transport membership is seeded.
            var net = Net = (NetServer)FormatterServices.GetUninitializedObject(typeof(NetServer));
            Set(net, "_clientsById", Clients); Set(net, "<IsStarted>k__BackingField", true);
            var manager = (ModServerManager)FormatterServices.GetUninitializedObject(typeof(ModServerManager));
            Set(manager, "_netServer", net); Set(manager, "worldLock", new object());
            foreach (var n in new[] { "_playerData", "_entityData" }) {
                var field = typeof(ServerManager).GetField(n, Hidden);
                field.SetValue(manager, Activator.CreateInstance(field.FieldType));
            }
            Set(Addon, "api", new ServerApi(manager, null, net, null)); Set(Addon, "sender", Sender);
            Set(Addon, "wasStarted", true); Set(Addon, "hostStatistics", CoopMod.Statistics);
            Set(Addon, "nextLedgerSync", double.MaxValue);
            Party = Get<Party>(Addon, "party");
            Settlements = Get<Dictionary<uint, (string, Dictionary<ushort, uint>, double)>>(Addon, "bossSettlements");
            foreach (ushort id in new ushort[] { 1, 2, 3 }) {
                Party.Join(id, "test" + id); Party.Members[id].Scene = "Town";
                Party.Members[id].Life = Life.Alive;
                var client = (NetServerClient)FormatterServices.GetUninitializedObject(typeof(NetServerClient));
                Set(client, "<UpdateManager>k__BackingField", new ServerUpdateManager()); Clients[id] = client;
            }
        }
        internal void Open() => Settlements[42] = ("Town", new Dictionary<ushort, uint> { [2] = 1, [1] = 1, [3] = 1 }, 0);
    }
    internal static void Register(Action<string, Action> test) {
        test("settlement skips retired transport before pending Leave and reaches both survivors", () => {
            var f = new Fixture(); f.Open(); f.Clients.TryRemove(2, out _);
            Call(f.Addon, "FlushBossSettlements");
            Check(f.Party.Members.ContainsKey(2), "test must retain stale roster");
            Check(f.Settlements.Count == 0 && f.Sender.Sent.Select(p => p.Id).SequenceEqual(new ushort[] { 1, 3 }), "survivor delivery or poison retirement failed");
        });
        test("settlement tolerates retirement after Connected and still processes later recipients", () => {
            var f = new Fixture(); f.Open();
            f.Sender.Before = (id, m) => { if (id == 2) { f.Clients.TryRemove(id, out _); throw new AddonConnectionUnavailableException(id, "retired at enqueue"); } };
            Call(f.Addon, "FlushBossSettlements"); Call(f.Addon, "FlushBossSettlements");
            Check(f.Settlements.Count == 0 && f.Sender.Sent.Select(p => p.Id).SequenceEqual(new ushort[] { 1, 3 }), "race must not starve or resend survivors");
        });
        test("settlement Leave removes only departed visits and retires empty entries", () => {
            var f = new Fixture(); f.Open(); Call(f.Addon, "Leave", (ushort)2);
            Check(!f.Settlements[42].Visits.ContainsKey(2) && f.Settlements[42].Visits.Count == 2, "departed visit retained");
            Call(f.Addon, "Leave", (ushort)1); Call(f.Addon, "Leave", (ushort)3);
            Check(f.Settlements.Count == 0, "empty settlement leaked");
        });
        test("settlement Tick continues roster wipe and release after an offline recipient", () => {
            var f = new Fixture(); f.Open(); f.Clients.TryRemove(2, out _); f.Party.Leave(2);
            Set(f.Addon, "rescue", true); Call(f.Addon, "Tick");
            Check(f.Sender.Sent.Any(p => p.Message.Kind == Kind.Roster), "roster starved");
            foreach (var m in f.Party.Members.Values) { m.Life = Life.Down; m.CheckpointScene = "Town"; }
            Call(f.Addon, "Tick"); Check(f.Party.Resetting && f.Sender.Sent.Any(p => p.Message.Kind == Kind.Wipe), "wipe starved");
            foreach (var id in f.Party.Members.Keys) f.Party.Ready(id, f.Party.Round, "Town");
            Call(f.Addon, "Tick"); Check(!f.Party.Resetting && f.Sender.Sent.Any(p => p.Message.Kind == Kind.Release), "release starved");
        });
        test("damage final snapshot retries failed enqueue without prematurely advancing revision", () => {
            var f = new Fixture(); var board = new DamageRanking(9, "");
            foreach (var id in f.Party.Members.Keys) board.Join(id, "test" + id, 1);
            board.Add(1, 1, 1, 27, 0); board.Finish(true); f.Clients.TryRemove(2, out _);
            var sent = Get<Dictionary<uint, uint>>(f.Addon, "sentDamageRevisions");
            Call(f.Addon, "SendDamageSnapshot", board); Check(!sent.ContainsKey(9), "partial enqueue advanced revision");
            f.Party.Leave(2); Call(f.Addon, "SendDamageSnapshot", board);
            Check(sent[9] == board.Revision && f.Sender.Sent.Count(p => p.Id == 3) == 2, "final revision failed to converge after Leave");
        });
        test("settlement phase isolates non-retirement errors from roster wipe and release", () => {
            var f = new Fixture(); f.Open(); Set(f.Addon, "rescue", true);
            f.Sender.Before = (id, m) => { if (m.Kind == Kind.BossSettlementReady) throw new FormatException("injected non-retirement enqueue error"); };
            foreach (var m in f.Party.Members.Values) { m.Life = Life.Down; m.CheckpointScene = "Town"; }
            Call(f.Addon, "Tick");
            Check(f.Settlements.Count == 0, "failed settlement left poison entry");
            Check(f.Sender.Sent.Any(p => p.Message.Kind == Kind.Roster) && f.Sender.Sent.Any(p => p.Message.Kind == Kind.Wipe), "exception escaped phase boundary");
            foreach (var id in f.Party.Members.Keys) f.Party.Ready(id, f.Party.Round, "Town");
            Call(f.Addon, "Tick");
            Check(!f.Party.Resetting && f.Sender.Sent.Any(p => p.Message.Kind == Kind.Release), "release remained starved after failed phase");
        });
        test("online non-disconnect send errors stay visible instead of masquerading as retirement", () => {
            var f = new Fixture();
            f.Sender.Before = (id, m) => throw new InvalidOperationException("injected non-transport collection mismatch");
            var visible = false;
            try { Call(f.Addon, "Send", (ushort)1, new Message { Kind = Kind.BossSettlementReady }); }
            catch (InvalidOperationException) { visible = true; }
            Check(visible, "online non-transport error was silently converted to false; A1-01 remains open");
        });
        test("non-disconnect send errors remain visible even when the recipient concurrently retires", () => {
            var f = new Fixture();
            f.Sender.Before = (id, m) => { f.Clients.TryRemove(id, out _); throw new InvalidOperationException("configuration failed during retirement"); };
            var visible = false;
            try { Call(f.Addon, "Send", (ushort)1, new Message { Kind = Kind.BossSettlementReady }); }
            catch (InvalidOperationException e) { visible = e is not AddonConnectionUnavailableException; }
            Check(visible, "a second connection check hid the actual non-transport error");
        });
        test("real addon sender distinguishes retirement from invalid channels addon IDs and collection usage", () => {
            var f = new Fixture(); f.Addon.Id = 3;
            var sender = new ServerAddonNetworkSender<Channel>(f.Net, f.Addon);
            Exception Failure(Action action) { try { action(); } catch (Exception e) { return e; } throw new Exception("expected enqueue rejection"); }
            f.Clients.TryRemove(2, out _);
            Check(Failure(() => sender.SendCollectionData(Channel.Event, new Message(), (ushort)2)) is AddonConnectionUnavailableException { PlayerId: 2 }, "retirement not distinguished");
            Check(Failure(() => sender.SendCollectionData((Channel)99, new Message(), (ushort)1)) is InvalidOperationException and not AddonConnectionUnavailableException, "invalid channel treated as retirement");
            f.Addon.Id = null;
            Check(Failure(() => sender.SendCollectionData(Channel.Event, new Message(), (ushort)1)) is InvalidOperationException and not AddonConnectionUnavailableException, "missing addon ID treated as retirement");
            f.Addon.Id = 3;
            sender.SendSingleData(Channel.Event, new Message(), (ushort)1);
            Check(Failure(() => sender.SendCollectionData(Channel.Event, new Message(), (ushort)1)) is InvalidOperationException and not AddonConnectionUnavailableException, "collection conflict treated as retirement");
            Set(f.Net, "<IsStarted>k__BackingField", false);
            Check(Failure(() => sender.SendCollectionData(Channel.Event, new Message(), (ushort)1)) is AddonConnectionUnavailableException, "stopped server not distinguished");
        });
        test("settlement enqueue rejection logs a terminal recipient outcome and still delivers later peers", () => {
            var f = new Fixture(); f.Open(); using var errors = new Errors();
            f.Sender.Before = (id, m) => { if (id == 2) throw new InvalidOperationException("injected collection conflict"); };
            Call(f.Addon, "FlushBossSettlements"); Call(f.Addon, "FlushBossSettlements");
            Check(f.Sender.Sent.Select(p => p.Id).SequenceEqual(new ushort[] { 1, 3 }), "one rejected recipient skipped later peers");
            Check(f.Settlements.Count == 0 && errors.Messages.Count == 1, "failed message leaked or repeated logging");
            var log = errors.Messages.Single();
            Check(log.Contains("terminal delivery failure") && log.Contains("kind=BossSettlementReady") && log.Contains("recipient=2") &&
                log.Contains("serial=42") && log.Contains("aux=1") && log.Contains("injected collection conflict"), "failure lost recipient or settlement identity");
        });
        test("damage enqueue rejection is terminal for one revision and a new revision can recover", () => {
            var f = new Fixture(); using var errors = new Errors(); var board = new DamageRanking(9, "");
            foreach (var id in f.Party.Members.Keys) board.Join(id, "test" + id, 1);
            var attempts = 0;
            f.Sender.Before = (id, m) => { if (id == 2) { attempts++; throw new InvalidOperationException("injected board configuration error"); } };
            Call(f.Addon, "SendDamageSnapshot", board); Call(f.Addon, "SendDamageSnapshot", board);
            var sent = Get<Dictionary<uint, uint>>(f.Addon, "sentDamageRevisions");
            var failed = Get<Dictionary<uint, uint>>(f.Addon, "failedDamageRevisions");
            Check(!sent.ContainsKey(9) && failed[9] == board.Revision, "rejected revision incorrectly marked successful");
            Check(attempts == 1 && errors.Messages.Count == 1 && f.Sender.Sent.Select(p => p.Id).SequenceEqual(new ushort[] { 1, 3 }), "invalid revision retried or starved other recipients");
            f.Sender.Before = null; board.Finish(true); Call(f.Addon, "SendDamageSnapshot", board);
            Check(sent[9] == board.Revision && !failed.ContainsKey(9), "new revision did not recover after configuration corrected");
            failed[10] = 1; Call(f.Addon, "ResetDamageRankings");
            Check(failed.Count == 0, "server generation reset retained failed revisions");
        });
    }
}
