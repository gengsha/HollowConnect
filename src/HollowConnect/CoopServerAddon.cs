using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using Hkmp;
using Hkmp.Api.Server;
using Hkmp.Api.Server.Networking;
using Hkmp.Game.Server;
using Hkmp.Networking.Server;
using Hkmp.Util;
using HollowConnect.Core;

namespace HollowConnect;

public sealed class CoopServerAddon : ServerAddon {
    protected override string Name => "HollowConnect";
    protected override string Version => CoopMod.Version;
    public override bool NeedsNetwork => true;
    private IServerApi api;
    private IServerAddonNetworkSender<Channel> sender;
    private readonly ConcurrentQueue<(int Generation, Action Action)> pending = new ConcurrentQueue<(int, Action)>();
    private int generation;
    private Party party = new Party();
    private ProgressLedger progress = new ProgressLedger();
    private readonly Dictionary<ushort, double> lastWarp = new Dictionary<ushort, double>();
    private readonly Dictionary<ushort, Message> destinations = new Dictionary<ushort, Message>();
    private bool wasStarted, shared, rescue, warp, entryProtection;
    private double nextRoster;
    private static double Now => (DateTime.UtcNow - new DateTime(2020, 1, 1)).TotalSeconds;

    public override void Initialize(IServerApi serverApi) {
        api = serverApi;
        sender = api.NetServer.GetNetworkSender<Channel>(this);
        var receiver = api.NetServer.GetNetworkReceiver<Channel>(this, Wire.CreateCollection);
        receiver.RegisterPacketHandler<Message>(Channel.Presence, (id, m) => Enqueue(() => Receive(id, m)));
        receiver.RegisterPacketHandler<Message>(Channel.Event, (id, m) => Enqueue(() => Receive(id, m)));
        api.ServerManager.PlayerDisconnectEvent += p => { if (p != null) Enqueue(() => Leave(p.Id)); };
        MonoBehaviourUtil.Instance.OnUpdateEvent += Tick;
        Logger.Info("HollowConnect server addon ready");
    }

    private void Enqueue(Action action) => pending.Enqueue((((NetServer)api.NetServer).Generation, action));

    private void Tick() {
        var started = api.NetServer.IsStarted;
        if (!started) { wasStarted = false; while (pending.TryDequeue(out _)) { } return; }
        var currentGeneration = ((NetServer)api.NetServer).Generation;
        if (!wasStarted || generation != currentGeneration) {
            generation = currentGeneration;
            wasStarted = true; party = new Party(); progress = new ProgressLedger();
            lastWarp.Clear(); destinations.Clear(); nextRoster = 0;
            shared = CoopMod.Settings.ShareProgress; rescue = CoopMod.Settings.Rescue; warp = CoopMod.Settings.AllowTeleport;
            entryProtection = CoopMod.Settings.EntryProtection;
            CoopContext.Epoch = 0; CoopContext.Resetting = false;
            var manager = (ServerManager)api.ServerManager;
            manager.InternalServerSettings.IsPvpEnabled = false;
            manager.InternalServerSettings.AlwaysShowMapIcons = true;
            manager.InternalServerSettings.OnlyBroadcastMapIconWithWaywardCompass = false;
            manager.OnUpdateServerSettings();
        }
        for (var i = 0; i < 2048 && pending.TryDequeue(out var action); i++) {
            if (action.Generation != generation) continue;
            try { action.Action(); } catch (Exception e) { Logger.Error("HollowConnect server: " + e); }
        }
        if (rescue && party.BeginWipe()) {
            CoopContext.Epoch = party.Round; CoopContext.Resetting = true;
            ((ServerManager)api.ServerManager).ResetCoopEntities();
            Broadcast(new Message { Kind = Kind.Wipe });
        }
        if (party.TryRelease()) {
            CoopContext.Resetting = false;
            Broadcast(new Message { Kind = Kind.Release });
        }
        var now = Now;
        if (now < nextRoster) return;
        nextRoster = now + .1;
        foreach (var m in party.Members.Values.ToArray()) {
            Broadcast(new Message { Kind = Kind.Roster, Id = m.Id, Serial = m.Sequence, Aux = m.LifeVersion, Name = m.Name,
                Key = m.DownSerial.ToString(), Scene = m.Scene, X = m.X, Y = m.Y, HasMap = m.HasMap && now - m.Seen < 2,
                MapX = m.MapX, MapY = m.MapY, Value = (int)m.Life, Target = (ushort)Math.Min(65535, m.DownSerial),
                IsReady = now - m.Seen < 2 && (m.Life == Life.Alive || m.Life == Life.Down) });
        }
    }

    private void Receive(ushort id, Message m) {
        var player = api.ServerManager.GetPlayer(id);
        if (player == null) return;
        if (m.Kind == Kind.Hello) {
            party.Join(id, player.Username);
            Send(id, new Message { Kind = Kind.Welcome, Id = id, Value = (shared ? 1 : 0) | (rescue ? 2 : 0) | (warp ? 4 : 0) | (entryProtection ? 8 : 0) });
            foreach (var pair in progress.Values) Send(id, new Message { Kind = Kind.ProgressGrant, Key = pair.Key, Value = pair.Value });
            if (party.Resetting) {
                Send(id, new Message { Kind = Kind.Wipe });
            }
            return;
        }
        if (m.Session != party.Session || m.Round != party.Round || !party.Members.TryGetValue(id, out var member)) return;
        switch (m.Kind) {
            case Kind.Presence:
                if (party.Presence(id, m.Round, m.Serial, m.Scene, m.X, m.Y, m.IsReady,
                    m.HasMap, m.MapX, m.MapY, Now)) {
                    if (member.Life != Life.Down && member.Life != Life.Returning && !party.Resetting && Party.ValidCheckpoint(m.Key, m.Name, m.Value)) {
                        member.CheckpointScene = m.Key; member.CheckpointMarker = m.Name;
                        member.CheckpointRespawnType = m.Value;
                    }
                    destinations[id] = m;
                }
                break;
            case Kind.Down:
                if (rescue && Party.ValidCheckpoint(m.Key, m.Name, m.Value) && party.Down(id, m.Round, m.Serial, m.Scene, m.X, m.Y, Now)) {
                    member.CheckpointScene = m.Key; member.CheckpointMarker = m.Name; member.CheckpointRespawnType = m.Value;
                    Broadcast(new Message { Kind = Kind.Down, Id = id, Serial = m.Serial, Aux = member.LifeVersion,
                        Name = member.Name, Scene = member.Scene, X = member.X, Y = member.Y });
                }
                break;
            case Kind.ShadeHit:
                if (rescue && m.Scene == member.Scene && party.Revive(id, m.Target, m.Round, m.Serial, Now)) {
                    var revived = party.Members[m.Target];
                    Broadcast(new Message { Kind = Kind.Revived, Id = m.Target, Target = id, Serial = m.Serial, Aux = revived.LifeVersion,
                        Scene = revived.Scene, X = revived.X, Y = revived.Y });
                }
                break;
            case Kind.GiveUpRequest:
                // Carry the current personal checkpoint reliably, even if a
                // heartbeat reporting a recently used bench has not arrived.
                var checkpointValid = Party.ValidCheckpoint(m.Scene, m.Key, m.Value);
                if (checkpointValid && member.Life == Life.Down && member.DownSerial == m.Serial) {
                    member.CheckpointScene = m.Scene; member.CheckpointMarker = m.Key;
                    member.CheckpointRespawnType = m.Value;
                }
                if (rescue && checkpointValid && party.GiveUp(id, m.Round, m.Serial))
                    Broadcast(new Message { Kind = Kind.Returning, Id = id, Serial = m.Serial, Aux = member.LifeVersion,
                        Scene = member.ReturnScene, Key = member.ReturnMarker, Value = member.ReturnRespawnType });
                else Send(id, new Message { Kind = Kind.GiveUpDenied, Id = id, Serial = m.Serial });
                break;
            case Kind.BenchReady:
                if (party.FinishReturn(id, m.Round, m.Serial, m.Aux, m.Scene, m.X, m.Y, Now))
                    Broadcast(new Message { Kind = Kind.Returned, Id = id, Serial = m.Serial, Aux = member.LifeVersion,
                        Scene = member.Scene, X = member.X, Y = member.Y });
                break;
            case Kind.Ready:
                party.Ready(id, m.Round, m.Scene); break;
            case Kind.ProgressOffer:
                if (shared && !party.Resetting && progress.Merge(m.Key, m.Value))
                    Broadcast(new Message { Kind = Kind.ProgressGrant, Key = m.Key, Value = m.Value });
                break;
            case Kind.WarpRequest:
                if (!warp || party.Resetting || member.Life != Life.Alive ||
                    !party.Members.TryGetValue(m.Target, out var target) || target.Life != Life.Alive || Now - target.Seen > 1.5 ||
                    string.IsNullOrEmpty(target.Scene) || !destinations.TryGetValue(target.Id, out var destination) || !destination.IsReady ||
                    lastWarp.TryGetValue(id, out var last) && Now - last < 3) break;
                lastWarp[id] = Now;
                Send(id, new Message { Kind = Kind.Warp, Target = target.Id, Serial = m.Serial,
                    Scene = target.Scene, X = target.X, Y = target.Y, BossSequence = destination.BossSequence,
                    BossDoor = destination.BossDoor, BossIndex = destination.BossIndex, BossBindings = destination.BossBindings,
                    BossLevel = destination.BossLevel, BossPlan = destination.BossPlan, BossStatueKey = destination.BossStatueKey, BossReturnGate = destination.BossReturnGate });
                break;
        }
    }

    private void Leave(ushort id) {
        party.Leave(id); lastWarp.Remove(id); destinations.Remove(id);
        if (api.NetServer.IsStarted) Broadcast(new Message { Kind = Kind.Left, Id = id });
    }
    private void Stamp(Message m) { m.Session = party.Session; m.Round = party.Round; }
    private void Send(ushort id, Message m) {
        Stamp(m);
        sender.SendCollectionData(m.IsReliable ? Channel.Event : Channel.Presence, m, id);
    }
    private void Broadcast(Message m) {
        Stamp(m);
        sender.BroadcastCollectionData(m.IsReliable ? Channel.Event : Channel.Presence, m);
    }
}
