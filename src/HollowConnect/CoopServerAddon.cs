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

public sealed partial class CoopServerAddon : ServerAddon {
    protected override string Name => "HollowConnect";
    protected override string Version => CoopMod.NetworkVersion;
    public override bool NeedsNetwork => true;
    private IServerApi api;
    private IServerAddonNetworkSender<Channel> sender;
    private readonly ConcurrentQueue<(int Generation, Action Action)> pending = new ConcurrentQueue<(int, Action)>();
    private int generation;
    private Party party = new Party();
    private BossVictoryRecovery bossVictories = new BossVictoryRecovery();
    private readonly Dictionary<uint, (string Scene, Dictionary<ushort, uint> Visits, double Expires)> bossSettlements = new();
    private readonly Dictionary<ushort, uint> lastBossReport = new Dictionary<ushort, uint>();
    private ProgressLedger progress = new ProgressLedger();
    private ResourceClaims resources = new ResourceClaims();
    private DeathStatistics hostStatistics;
    private readonly Dictionary<ushort, double> lastWarp = new Dictionary<ushort, double>();
    private readonly Dictionary<ushort, Message> destinations = new Dictionary<ushort, Message>();
    private bool wasStarted, shared, charms, rescue, warp, entryProtection, bossVictoryRescue;
    private bool linkedHealth;
    private int linkedHealthBonusMasks;
    private int linkedSaveMode;
    private bool linkedSteelEnded;
    private PlayerData linkedHostData;
    private int linkedHostProfile;
    private LinkedHealthPool linkedPool = new LinkedHealthPool();
    private readonly LinkedRestGroup linkedRest = new LinkedRestGroup();
    private LinkedRestCount linkedRestCount;
    private string linkedRestStatus = "";
    private readonly HashSet<string> linkedMaskReceipts = new HashSet<string>(StringComparer.Ordinal);
    private bool bossHealthScaling;
    private bool flexibleBossTargeting;
    private float bossExtraHealth, bossHealthDecay;
    private uint progressAuditSerial;
    private double nextRoster, nextLedgerSync;
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
        if (!started) { wasStarted = false; hostStatistics = null; while (pending.TryDequeue(out _)) { } return; }
        var currentGeneration = ((NetServer)api.NetServer).Generation;
        if (!wasStarted || generation != currentGeneration) {
            generation = currentGeneration;
            wasStarted = true; party = new Party(); progress = new ProgressLedger(); resources = new ResourceClaims();
            bossVictories = new BossVictoryRecovery(); bossSettlements.Clear(); lastBossReport.Clear(); progressAuditSerial = 0;
            linkedPool = new LinkedHealthPool();
            linkedRest.Clear(); linkedRestCount = default;
            linkedRestStatus = "";
            linkedSteelEnded = false;
            linkedHostData = null;
            linkedMaskReceipts.Clear();
            // The local server is hosted by the game that owns the save slot.
            // Seed both ledgers from that slot before accepting clients; a
            // server generation is a transport lifecycle, not a new save.
            progress.Import(CoopMod.Statistics?.SharedProgress);
            resources = HostResourceHistory();
            hostStatistics = CoopMod.Statistics;
            lastWarp.Clear(); destinations.Clear(); nextRoster = nextLedgerSync = 0;
            shared = !((ServerManager)api.ServerManager).LocalWorldOnly; charms = CoopMod.Settings.ShareCharms;
            linkedHealth = CoopMod.Settings.LinkedHealth && shared && charms;
            linkedHealthBonusMasks = LinkedHealthBonusRow.Normalize(CoopMod.Settings.LinkedHealthBonusMasks);
            rescue = linkedHealth ? false : CoopMod.Settings.Rescue; warp = CoopMod.Settings.AllowTeleport;
            bossVictoryRescue = CoopMod.Settings.BossVictoryRescue;
            party.RescueEnabled = rescue;
            party.RescueLimit = Math.Max(0, Math.Min(999, CoopMod.Settings.RescueLimit));
            party.SpectateAfterExhaustion = linkedHealth ? false : CoopMod.Settings.SpectateAfterExhaustion;
            entryProtection = CoopMod.Settings.EntryProtection;
            bossHealthScaling = CoopMod.Settings.BossHealthScaling;
            damageRankingEnabled = CoopMod.Settings.BossDamageRanking;
            ResetDamageRankings();
            flexibleBossTargeting = CoopMod.Settings.BossTargetingMode == 1;
            bossExtraHealth = BossHealthParameterRow.Normalize(CoopMod.Settings.BossExtraHealthPerPlayer);
            bossHealthDecay = bossExtraHealth;
            CoopContext.Epoch = 0; CoopContext.Resetting = false;
            var manager = (ServerManager)api.ServerManager;
            manager.InternalServerSettings.IsPvpEnabled = false;
            manager.InternalServerSettings.AlwaysShowMapIcons = true;
            manager.InternalServerSettings.OnlyBroadcastMapIconWithWaywardCompass = false;
            manager.OnUpdateServerSettings();
        }
        // Loading a different save slot replaces CoopMod.Statistics while a
        // local server can still be alive. Rebind the authority ledgers before
        // accepting offers so one slot's receipts cannot bleed into another.
        if (!ReferenceEquals(hostStatistics, CoopMod.Statistics) || linkedHealth && linkedPool.Initialized &&
            (!ReferenceEquals(linkedHostData, PlayerData.instance) || GameManager.instance.profileID != linkedHostProfile)) {
            if (linkedHealth && linkedPool.Initialized) {
                Logger.Info("HollowConnect: host changed save; closing linked-health room");
                ((ServerManager)api.ServerManager).Stop(); return;
            }
            ResetDamageRankings();
            hostStatistics = CoopMod.Statistics;
            progress = new ProgressLedger();
            progress.Import(CoopMod.Statistics?.SharedProgress);
            resources = HostResourceHistory();
            if (linkedHealth) linkedMaskReceipts.Clear();
            nextLedgerSync = 0;
        }
        // Native host pickups can be observed by the client a frame before
        // their ResourceOffer reaches this addon. Merge them into the host
        // ledger here so a reconnect/late join never misses a source.
        if (Now >= nextLedgerSync) { nextLedgerSync = Now + .25; SyncHostLedgers(); }
        InitializeLinkedHost();
        for (var i = 0; i < 2048 && pending.TryDequeue(out var action); i++) {
            if (action.Generation != generation) continue;
            try { action.Action(); } catch (Exception e) { Logger.Error("HollowConnect server: " + e); }
        }
        Guard("damage rankings", TickDamageRankings);
        Guard("linked rest", TickLinkedRest);
        Guard("boss settlement", FlushBossSettlements);
        Guard("wipe", () => {
            if (linkedSteelEnded || !(linkedHealth || rescue || party.SpectateAfterExhaustion) || !party.BeginWipe()) return;
            CoopContext.Epoch = party.Round; CoopContext.Resetting = true;
            ((ServerManager)api.ServerManager).ResetCoopEntities();
            Broadcast(new Message { Kind = Kind.Wipe });
        });
        Guard("boss rooms", () => {
            if (linkedHealth) return; // Shared zero belongs to the whole-party barrier.
            foreach (var failed in party.FailBossRooms((id, scene) => ((ServerManager)api.ServerManager).TryGetCoopSceneScope(id, scene, out _, out _)))
                SendReturning(failed, "boss-failed");
        });
        Guard("release", () => {
            if (!party.TryRelease()) return;
            CoopContext.Resetting = false;
            Broadcast(new Message { Kind = Kind.Release });
            if (linkedHealth) { linkedPool.Refill(); BroadcastLinkedHealth(); }
        });
        Guard("roster", () => {
            var now = Now;
            if (now < nextRoster) return;
            nextRoster = now + .1;
            foreach (var m in party.Members.Values.ToArray()) {
                var roster = new Message { Kind = Kind.Roster, Id = m.Id, Serial = m.Sequence, Aux = m.LifeVersion, Name = m.Name,
                    Key = m.DownSerial.ToString(), Scene = m.Scene, X = m.X, Y = m.Y, HasMap = m.HasMap && now - m.Seen < 2,
                    MapX = m.MapX, MapY = m.MapY, Value = (int)m.Life, Target = (ushort)Math.Min(65535, m.DownSerial),
                    IsReady = now - m.Seen < 2 && (m.Life == Life.Alive || m.Life == Life.Down) };
                // Rest stops have no boss death to carry the advancing peer's
                // context. Publish it even when that peer is not scene host.
                if (m.Scene == "GG_Spa" && m.Life == Life.Alive && roster.IsReady &&
                    destinations.TryGetValue(m.Id, out var destination) && destination.IsReady && destination.Scene == m.Scene &&
                    ((ServerManager)api.ServerManager).TryGetCoopSceneScope(m.Id, m.Scene, out _, out _) &&
                    party.Members.Values.Any(peer => peer.Id != m.Id && peer.Scene == m.Scene &&
                        (peer.Life == Life.Alive || peer.Life == Life.Loading) && now - peer.Seen < 2 &&
                        ((ServerManager)api.ServerManager).TryGetCoopSceneScope(peer.Id, peer.Scene, out _, out _) &&
                        destinations.TryGetValue(peer.Id, out var older) && older.Scene == m.Scene && older.BossSequence == destination.BossSequence &&
                        older.BossDoor == destination.BossDoor && older.BossBindings == destination.BossBindings && older.BossIndex < destination.BossIndex)) {
                    roster.BossSequence = destination.BossSequence; roster.BossIndex = destination.BossIndex;
                    roster.BossBindings = destination.BossBindings; roster.BossDoor = destination.BossDoor;
                    roster.BossPlan = destination.BossPlan;
                }
                Broadcast(roster);
            }
        });
    }

    private void Guard(string phase, Action action) {
        try { action(); }
        catch (Exception e) { Logger.Error("HollowConnect server " + phase + ": " + e); }
    }

    private void FlushBossSettlements() {
        foreach (var settlement in bossSettlements.ToArray()) {
            var scope = settlement.Value;
            if (Now < scope.Expires && scope.Visits.Any(p => !bossVictories.IsSettled(settlement.Key, p.Key) &&
                party.Members.TryGetValue(p.Key, out var occupant) && occupant.Life != Life.Returning && occupant.Life != Life.Resetting &&
                ((ServerManager)api.ServerManager).TryGetCoopSceneScope(p.Key, scope.Scene, out var visit, out _) && visit == p.Value)) continue;
            // Remove before sending: a retired recipient must never leave this
            // entry behind to be retried every frame.
            bossSettlements.Remove(settlement.Key);
            // Each recipient finishes independently: queued, retired, or a
            // logged terminal enqueue failure. No failure skips later peers.
            foreach (var participant in scope.Visits)
                Deliver(participant.Key, new Message { Kind = Kind.BossSettlementReady, Target = participant.Key, Serial = settlement.Key,
                    Aux = participant.Value, Scene = scope.Scene });
        }
    }

    private static Message DeathCount(string name, uint count) => new Message {
        Kind = Kind.DeathCount, Name = name, Serial = count
    };
    private void Receive(ushort id, Message m) {
        var player = api.ServerManager.GetPlayer(id);
        if (player == null) return;
        if (m.Kind == Kind.Hello) {
            var newlyJoined = !party.Members.ContainsKey(id);
            party.Join(id, player.Username);
            Send(id, new Message { Kind = Kind.Welcome, Id = id, Name = player.Username, X = bossExtraHealth, Y = bossHealthDecay,
                Aux = (uint)party.RescueLimit,
                Value = (shared ? 1 : 0) | (rescue ? 2 : 0) | (warp ? 4 : 0) | (entryProtection ? 8 : 0) | (charms ? 0 : 16) | (bossVictoryRescue ? 0 : 32) | (bossHealthScaling ? 64 : 0) | (party.SpectateAfterExhaustion ? 128 : 0) | (flexibleBossTargeting ? 256 : 0) | (damageRankingEnabled ? 512 : 0) | (linkedHealth ? 1024 : 0) | (linkedSteelEnded ? 2048 : 0),
                LinkedBonusMasks = linkedHealthBonusMasks, LinkedHealth = linkedPool.Health, LinkedMaxHealth = linkedPool.MaxHealth, LinkedRevision = linkedPool.Revision });
            SendRescueStatus(party.Members[id]);
            if (linkedHealth) { linkedPool.Join(id); linkedRest.Join(id); SendLinkedHealth(id); }
            foreach (var pair in progress.Values)
                if (ProgressCatalog.CanShare(pair.Key, shared, charms)) Send(id, new Message { Kind = Kind.ProgressGrant, Key = pair.Key, Value = pair.Value });
            if (shared) foreach (var source in resources.Claimed) Send(id, new Message { Kind = Kind.ResourceGrant, Key = source });
            if (shared && newlyJoined) Broadcast(new Message { Kind = Kind.ProgressGrant,
                Key = Wire.ProgressAuditKey, Serial = ++progressAuditSerial });
            foreach (var standing in party.Deaths.Deaths) Send(id, DeathCount(standing.Key, standing.Value));
            if (party.Resetting) {
                Send(id, new Message { Kind = Kind.Wipe });
            }
            return;
        }
        if (m.Session != party.Session || !party.Members.TryGetValue(id, out var member)) return;
        // Source acquisition survives a wipe: a reliable award from the just
        // completed round must not be lost when the party begins a new attempt.
        if (m.Kind == Kind.ResourceOffer) {
            if (shared && m.Round <= party.Round && ClaimHostResource(m.Key))
                Broadcast(new Message { Kind = Kind.ResourceGrant, Key = m.Key });
            return;
        }
        // A death counted just before the reliable wipe can arrive in the previous round.
        if (m.Kind == Kind.DeathCount || m.Kind == Kind.DeathHistory) {
            var name = m.Kind == Kind.DeathCount ? member.Name : m.Name;
            if (m.Round <= party.Round && party.Deaths.Merge(name, m.Serial)) Broadcast(DeathCount(name, m.Serial));
            return;
        }
        if (m.Round != party.Round) return;
        if (m.Kind is Kind.DamageBegin or Kind.DamageHit or Kind.DamageEnd) {
            ReceiveDamageRanking(id, m); return;
        }
        switch (m.Kind) {
            case Kind.LinkedHealthInit:
                ReceiveLinkedHealthInit(id, m); break;
            case Kind.LinkedHealthDelta:
                ReceiveLinkedHealthDelta(id, m); break;
            case Kind.LinkedHealthEnd:
                if (linkedHealth && linkedSaveMode == 1 && linkedPool.Initialized && linkedPool.Health == 0 && !linkedSteelEnded) {
                    linkedSteelEnded = true; Broadcast(new Message { Kind = Kind.LinkedHealthEnd });
                }
                break;
            case Kind.BossDefeated:
                if (!(rescue && bossVictoryRescue || party.SpectateAfterExhaustion) || party.Resetting || m.Serial == 0 ||
                    lastBossReport.TryGetValue(id, out var report) && m.Serial <= report ||
                    !((ServerManager)api.ServerManager).TryGetCoopSceneScope(id, m.Scene, out var reportVisit, out var authority) ||
                    !authority || reportVisit != m.Aux) break;
                lastBossReport[id] = m.Serial;
                var visits = new Dictionary<ushort, uint>();
                foreach (var participant in party.Members.Values) {
                    // Engine visit is current even if this player's first
                    // addon Presence/Down is still in flight from room entry.
                    if (participant.Life == Life.Returning || participant.Life == Life.Resetting) continue;
                    if (((ServerManager)api.ServerManager).TryGetCoopSceneScope(participant.Id, m.Scene, out var participantVisit, out _)) visits[participant.Id] = participantVisit;
                }
                var victory = bossVictories.Open(party.Round, m.Scene, visits, Now);
                if (victory != 0) bossSettlements[victory] = (m.Scene, visits, Now + 15);
                if (victory != 0) foreach (var participant in visits)
                    Send(participant.Key, new Message { Kind = Kind.BossVictory, Target = participant.Key, Serial = victory, Aux = participant.Value, Scene = m.Scene });
                break;
            case Kind.BossRecoveryRequest:
                if (!((ServerManager)api.ServerManager).TryGetCoopSceneScope(id, m.Scene, out var recoveryVisit, out _) || recoveryVisit != unchecked((uint)m.Value)) break;
                if (m.IsReady) {
                    // Living players and opted-out shadows acknowledge the
                    // notice too. Native Godhome completion waits for these
                    // receipts and all requested recoveries before advancing.
                    if (member.Life == Life.Alive || member.Life == Life.Loading ||
                        !(rescue && bossVictoryRescue || party.SpectateAfterExhaustion && member.Life == Life.Spectating))
                        bossVictories.Acknowledge(m.Round, m.Aux, id, m.Scene, recoveryVisit, Now);
                    break;
                }
                if ((rescue && bossVictoryRescue || party.SpectateAfterExhaustion && member.Life == Life.Spectating) &&
                    bossVictories.Revive(party, id, m.Round, m.Aux, m.Serial, m.Scene, recoveryVisit, Now, out var survivor,
                        (peer, visit) => ((ServerManager)api.ServerManager).TryGetCoopSceneScope(peer, m.Scene, out var currentVisit, out _) && currentVisit == visit)) {
                    Broadcast(new Message { Kind = Kind.BossRevived, Id = id, Serial = m.Serial, Aux = member.LifeVersion,
                        Target = survivor, Scene = member.Scene, X = member.X, Y = member.Y });
                    SendRescueStatus(member);
                }
                break;
            case Kind.SpectateRequest:
                if (((ServerManager)api.ServerManager).TryGetCoopSceneScope(id, m.Scene, out _, out _) &&
                    party.GiveUpOrSpectate(id, m.Round, m.Serial, m.Scene)) {
                    if (member.Life == Life.Spectating)
                        Broadcast(new Message { Kind = Kind.Spectating, Id = id, Serial = m.Serial, Aux = member.LifeVersion, Scene = member.Scene });
                    else SendReturning(member, "give-up-no-spectator-target");
                }
                else Send(id, new Message { Kind = Kind.GiveUpDenied, Id = id, Serial = m.Serial });
                break;
            case Kind.Presence:
                if (party.Presence(id, m.Round, m.Serial, m.Scene, m.X, m.Y, m.IsReady,
                    m.HasMap, m.MapX, m.MapY, Now, (m.Aux & 1) != 0)) {
                    if (member.Life != Life.Down && member.Life != Life.Spectating && member.Life != Life.Returning && !party.Resetting && Party.ValidCheckpoint(m.Key, m.Name, m.Value)) {
                        member.CheckpointScene = m.Key; member.CheckpointMarker = m.Name;
                        member.CheckpointRespawnType = m.Value;
                    }
                    destinations[id] = m;
                    if (linkedHealth) {
                        var rest = (LinkedRestKind)((m.Aux >> 1) & 3);
                        if (party.Resetting || member.Life != Life.Alive && member.Life != Life.Loading ||
                            !((ServerManager)api.ServerManager).TryGetCoopSceneScope(id, m.Scene, out _, out _)) rest = LinkedRestKind.None;
                        linkedRest.Report(id, rest, Now, m.LinkedRestEpoch);
                    }
                }
                break;
            case Kind.Down:
                if ((linkedHealth && linkedPool.Health == 0 || rescue || party.SpectateAfterExhaustion) && Party.ValidCheckpoint(m.Key, m.Name, m.Value) && party.Down(id, m.Round, m.Serial, m.Scene, m.X, m.Y, Now, m.IsReady)) {
                    member.CheckpointScene = m.Key; member.CheckpointMarker = m.Name; member.CheckpointRespawnType = m.Value;
                    Broadcast(new Message { Kind = Kind.Down, Id = id, Serial = m.Serial, Aux = member.LifeVersion,
                        Name = member.Name, Scene = member.Scene, X = member.X, Y = member.Y, Value = (int)member.Life });
                    SendRescueStatus(member);
                    if (!linkedHealth && party.ReturnWithoutRescue(id)) SendReturning(member, "exhausted");
                }
                break;
            case Kind.ShadeHit:
                if (rescue && m.Scene == member.Scene && party.Revive(id, m.Target, m.Round, m.Serial, Now)) {
                    var revived = party.Members[m.Target];
                    Broadcast(new Message { Kind = Kind.Revived, Id = m.Target, Target = id, Serial = m.Serial, Aux = revived.LifeVersion,
                        Scene = revived.Scene, X = revived.X, Y = revived.Y });
                    SendRescueStatus(revived);
                }
                break;
            case Kind.RescueAnchorRequest:
                if (rescue && ((ServerManager)api.ServerManager).TryGetCoopSceneScope(id, m.Scene, out _, out _) &&
                    party.MoveRescueAnchor(id, m.Round, m.Serial, m.Aux, m.Scene, m.X, m.Y, Now))
                    Broadcast(new Message { Kind = Kind.RescueAnchor, Id = id, Serial = member.DownSerial,
                        Aux = member.LifeVersion, Value = (int)member.RescueAnchorSerial,
                        Scene = member.Scene, X = member.X, Y = member.Y });
                else if (m.Aux > 0 && m.Aux <= int.MaxValue)
                    Send(id, new Message { Kind = Kind.RescueAnchor, Id = id, Serial = m.Serial,
                        Value = -(int)m.Aux, Scene = m.Scene });
                break;
            case Kind.RescueUnavailable:
                if (((ServerManager)api.ServerManager).TryGetCoopSceneScope(id, m.Scene, out _, out _) &&
                    party.ReturnUnsafeRescue(id, m.Round, m.Serial, m.Aux, m.Scene)) SendReturning(member, "unsafe-rescue");
                break;
            case Kind.GiveUpRequest:
                // Carry the current personal checkpoint reliably, even if a
                // heartbeat reporting a recently used bench has not arrived.
                var checkpointValid = Party.ValidCheckpoint(m.Scene, m.Key, m.Value);
                if (checkpointValid && (member.Life == Life.Down || member.Life == Life.Spectating) && member.DownSerial == m.Serial) {
                    member.CheckpointScene = m.Scene; member.CheckpointMarker = m.Key;
                    member.CheckpointRespawnType = m.Value;
                }
                if ((rescue || party.SpectateAfterExhaustion) && checkpointValid && party.GiveUp(id, m.Round, m.Serial))
                    SendReturning(member, "give-up");
                else Send(id, new Message { Kind = Kind.GiveUpDenied, Id = id, Serial = m.Serial });
                break;
            case Kind.BenchReady:
                if (party.FinishReturn(id, m.Round, m.Serial, m.Aux, m.Scene, m.X, m.Y, Now))
                    Broadcast(new Message { Kind = Kind.Returned, Id = id, Serial = m.Serial, Aux = member.LifeVersion,
                        Scene = member.Scene, X = member.X, Y = member.Y });
                break;
            case Kind.Ready:
                party.Ready(id, m.Round, m.Scene); break;
            case Kind.BenchRest:
                if (((ServerManager)api.ServerManager).TryGetCoopSceneScope(id, m.Scene, out _, out _) && party.Rest(id, m.Round, m.Serial, m.Scene))
                    SendRescueStatus(member);
                break;
            case Kind.ProgressOffer:
                if (ProgressCatalog.CanShare(m.Key, shared, charms) && !party.Resetting && progress.Merge(m.Key, m.Value)) {
                    if (CoopMod.Statistics?.SharedProgress != null) CoopMod.Statistics.SharedProgress[m.Key] = progress.Get(m.Key);
                    Broadcast(new Message { Kind = Kind.ProgressGrant, Key = m.Key, Value = progress.Get(m.Key) });
                }
                break;
            case Kind.WarpRequest:
                if (!warp || party.Resetting || member.Life != Life.Alive ||
                    !party.Members.TryGetValue(m.Target, out var target) || target.Life != Life.Alive || Now - target.Seen > 1.5 ||
                    string.IsNullOrEmpty(target.Scene) || SceneUtil.IsNonGameplayScene(target.Scene) ||
                    !destinations.TryGetValue(target.Id, out var destination) || !destination.IsReady ||
                    lastWarp.TryGetValue(id, out var last) && Now - last < 3) break;
                lastWarp[id] = Now;
                Send(id, new Message { Kind = Kind.Warp, Target = target.Id, Serial = m.Serial,
                    Scene = target.Scene, X = target.X, Y = target.Y, BossSequence = destination.BossSequence,
                    BossDoor = destination.BossDoor, BossIndex = destination.BossIndex, BossBindings = destination.BossBindings,
                    BossLevel = destination.BossLevel, BossPlan = destination.BossPlan, BossStatueKey = destination.BossStatueKey,
                    BossReturnGate = destination.BossReturnGate, BossReturnScene = destination.BossReturnScene });
                break;
        }
    }

    private void SendRescueStatus(Member member) => Send(member.Id, new Message {
        Kind = Kind.RescueStatus, Id = member.Id, Value = member.RescuesUsed, Serial = member.DownSerial, Aux = member.RestSerial
    });
    private int LinkedMaskPieces() {
        if (resources?.Claimed != null) foreach (var id in resources.Claimed)
            if (ResourceCatalog.TryGet(id, out var source) && source.Kind == ResourceKind.MaskShard) linkedMaskReceipts.Add(id);
        return LinkedHealthRules.MaskPieces(linkedMaskReceipts);
    }
    private void RefreshLinkedHealthCapacity() {
        if (linkedHealth && linkedPool.UpdatePieces(LinkedMaskPieces())) BroadcastLinkedHealth();
    }
    private void ReceiveLinkedHealthInit(ushort id, Message message) {
        if (!linkedHealth || party.Resetting) return;
        linkedPool.Join(id);
        InitializeLinkedHost(); SendLinkedHealth(id);
    }
    private void InitializeLinkedHost() {
        if (!linkedHealth || linkedPool.Initialized || party.Resetting) return;
        var host = CoopRuntime.Current;
        if (host == null || !host.Playable || !host.LinkedHostReady) return;
        var pd = PlayerData.instance;
        linkedHostData = pd; linkedHostProfile = GameManager.instance.profileID;
        linkedSaveMode = pd.GetIntInternal("permadeathMode");
        linkedPool.Initialize(host.LinkedHostHealth, pd.GetIntInternal("maxHealthBase"), LinkedMaskPieces(),
            linkedHealthBonusMasks, pd.GetIntInternal("heartPieces"), LinkedHealthRules.MaskPieces(CoopMod.Statistics.Resources.Claimed));
        BroadcastLinkedHealth();
    }
    private void ReceiveLinkedHealthDelta(ushort id, Message message) {
        if (!linkedHealth || party.Resetting) return;
        if (linkedPool.Report(id, message.Serial, message.Value, message.LinkedRestEpoch, out var damaged, out var damageEpoch) && damaged)
            linkedRest.Interrupt(id, damageEpoch, Now);
        BroadcastLinkedHealth();
    }
    private void SendLinkedHealth(ushort id) {
        if (!linkedHealth || !linkedPool.Initialized) return;
        Send(id, new Message { Kind = Kind.LinkedHealthState, Id = id, Serial = linkedPool.Ack(id),
            Value = linkedRestCount.Total, X = linkedRestCount.Benches, Y = linkedRestCount.Springs,
            LinkedHealth = linkedPool.Health, LinkedMaxHealth = linkedPool.MaxHealth,
            LinkedSaveMode = linkedSaveMode, LinkedRestStatus = linkedRestStatus,
            LinkedMaskPieces = LinkedMaskPieces(), LinkedBonusMasks = linkedHealthBonusMasks, LinkedRevision = linkedPool.Revision });
    }
    private void BroadcastLinkedHealth() {
        if (!linkedHealth || !linkedPool.Initialized) return;
        foreach (var id in party.Members.Keys.ToArray()) SendLinkedHealth(id);
    }
    private void SendReturning(Member member, string reason) => Broadcast(new Message {
        Kind = Kind.Returning, Id = member.Id, Serial = member.DownSerial, Aux = member.LifeVersion,
        Scene = member.ReturnScene, Key = member.ReturnMarker, Value = member.ReturnRespawnType, Name = reason
    });

    private void Leave(ushort id) {
        linkedPool.Leave(id);
        linkedRest.Leave(id);
        foreach (var board in damageBoards) board.Leave(id);
        foreach (var scope in damageVisits.Keys.Where(scope => scope.Player == id).ToArray()) damageVisits.Remove(scope);
        foreach (var attempt in damageAttempts.Keys.Where(attempt => attempt.Player == id).ToArray()) damageAttempts.Remove(attempt);
        party.Leave(id); lastWarp.Remove(id); destinations.Remove(id); lastBossReport.Remove(id);
        // A settlement must not keep a departed participant: otherwise the
        // final flush keeps targeting an id that no transport can deliver to.
        foreach (var settlement in bossSettlements.ToArray()) {
            settlement.Value.Visits.Remove(id);
            if (settlement.Value.Visits.Count == 0) bossSettlements.Remove(settlement.Key);
        }
        if (api.NetServer.IsStarted) Broadcast(new Message { Kind = Kind.Left, Id = id });
    }
    private bool ClaimHostResource(string sourceId) {
        if (!ResourceCatalog.TryGet(sourceId, out _)) return false;
        if (!resources.TryClaim(sourceId, out _)) return false;
        if (CoopMod.Statistics != null) {
            if (CoopMod.Statistics.SharedResources == null) CoopMod.Statistics.SharedResources = new ResourceClaims();
            CoopMod.Statistics.SharedResources.TryClaim(sourceId, out _);
        }
        RefreshLinkedHealthCapacity();
        return true;
    }
    private static ResourceClaims HostResourceHistory() {
        var stats = CoopMod.Statistics;
        if (stats == null) return new ResourceClaims();
        stats.SharedResources = stats.SharedResources?.Copy() ?? new ResourceClaims();
        if (stats.Resources?.Claimed != null)
            foreach (var id in stats.Resources.Claimed) stats.SharedResources.TryClaim(id, out _);
        return stats.SharedResources.Copy();
    }
    private void SyncHostLedgers() {
        if (!shared) return;
        if (CoopMod.Statistics?.SharedProgress != null)
            foreach (var pair in CoopMod.Statistics.SharedProgress)
                if (progress.Merge(pair.Key, pair.Value))
                    Broadcast(new Message { Kind = Kind.ProgressGrant, Key = pair.Key, Value = pair.Value });
        var hostClaims = CoopMod.Statistics?.Resources?.Claimed;
        if (hostClaims == null) return;
        foreach (var source in hostClaims)
            if (ClaimHostResource(source))
                Broadcast(new Message { Kind = Kind.ResourceGrant, Key = source });
    }
    private void Stamp(Message m) { m.Session = party.Session; m.Round = party.Round; }
    private enum DeliveryResult { Queued, Retired, Failed }
    private DeliveryResult Deliver(ushort id, Message m) {
        try { return Send(id, m) ? DeliveryResult.Queued : DeliveryResult.Retired; }
        catch (Exception e) {
            // The enqueue API has no transient-failure result. Invalid addon
            // configuration or collection usage is terminal for this message;
            // preserve its identity and let other recipients continue.
            Logger.Error("HollowConnect terminal delivery failure: kind=" + m.Kind + " recipient=" + id +
                " serial=" + m.Serial + " aux=" + m.Aux + " scene=" + m.Scene + ": " + e);
            return DeliveryResult.Failed;
        }
    }
    private bool Send(ushort id, Message m) {
        if (!Connected(id)) return false;
        Stamp(m);
        try {
            sender.SendCollectionData(m.IsReliable ? Channel.Event : Channel.Presence, m, id);
            return true;
        } catch (AddonConnectionUnavailableException) {
            // Only the transport's explicit retirement result is benign.
            // Configuration and collection errors remain visible to callers.
            return false;
        }
    }
    private bool Connected(ushort id) =>
        api?.NetServer?.IsStarted == true && ((ServerManager)api.ServerManager).IsPlayerConnected(id);
    private void Broadcast(Message m) {
        Stamp(m);
        sender.BroadcastCollectionData(m.IsReliable ? Channel.Event : Channel.Presence, m);
    }
}
