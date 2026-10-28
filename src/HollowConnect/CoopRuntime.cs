using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using Hkmp;
using Hkmp.Api.Client;
using Hkmp.Api.Client.Networking;
using Hkmp.Game.Client.Entity;
using Hkmp.Game.Client.Save;
using Hkmp.Util;
using HollowConnect.Core;
using Modding;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnitySceneManager = UnityEngine.SceneManagement.SceneManager;

namespace HollowConnect;

public sealed class Peer {
    public Message State;
    public double Seen;
    public uint DownSerial, LifeVersion, DownFeedbackSerial, RescueFeedbackSerial;
    public Life Life;
}

public sealed partial class CoopRuntime : MonoBehaviour {
    private IClientApi api;
    private IClientAddonNetworkSender<Channel> sender;
    internal readonly Dictionary<ushort, Peer> Peers = new Dictionary<ushort, Peer>();
    private ProgressLedger granted = new ProgressLedger();
    private ResourceShareService resources;
    private ProgressPickupService progressPickups;
    private MapPurchaseService mapPurchases;
    private TeammateDreamNail teammateDream;
    private DeathStatistics resourceSlot;
    private PlayerData resourcePlayerData;
    // The server can send its union while the native profile menu is still
    // open. Keep it for this connection independently of the selected slot,
    // just as granted keeps abilities until that slot is ready to apply them.
    private readonly HashSet<string> grantedResources = new HashSet<string>(StringComparer.Ordinal);
    private bool resourceImported;
    private bool progressAuditRequested;
    private uint progressAuditSerial;
    private readonly HashSet<string> offeredResources = new HashSet<string>(StringComparer.Ordinal);
    private readonly Dictionary<string, int> offered = new Dictionary<string, int>();
    private readonly ResourceNotifications resourceNotifications = new ResourceNotifications();
    private GUIStyle overlayStyle;
    private SaveSafetySnapshot sampledDebugState;
    private readonly Dictionary<string, FieldInfo> fields = new Dictionary<string, FieldInfo>();
    private readonly SaveChanges sceneSaveChanges = new SaveChanges();
    private MapService map;
    private ShadeService shades;
    private PersonalDeath personalDeath;
    private readonly EntryProtectionWindow entryProtection = new EntryProtectionWindow();
    private int protectionSceneHandle = int.MinValue;
    private string protectionSession = "";
    private bool protectEntry;
    private bool bossHealthScaling;
    private float bossExtraHealth, bossHealthDecay;
    internal double CurrentBossHealthMultiplier() {
        if (!Connected || !bossHealthScaling || Resetting || Returning) return 1;
        var count = 1;
        var now = Now;
        foreach (var peer in Peers.Values)
            if (peer.State != null && peer.State.Scene == CoopContext.LocalScene && now - peer.Seen < 2 &&
                peer.Life != Life.Returning) count++;
        return BossHealthScale.Multiplier(count, bossExtraHealth);
    }
    private bool takingDamage, nativeHitPlayed;
    private int damageHazard;
    internal Func<KeyCode, bool> KeyPressed = Input.GetKeyDown;
    internal string Session = "";
    internal ushort LocalId, Selected;
    internal uint ReleasedRound;
    internal bool Shared, ShareCharms, RescueEnabled, WarpEnabled, Down, Resetting, Returning;
    private uint sequence, downSerial, warpSerial, handledWarp;
    private string localName = "";
    private Vector2 deathScroll;
    private float nextPresence, nextProgress;
    private bool readySent, changingScene;
    private bool giveUpRequested;
    private Coroutine benchReturn;
    private Coroutine checkpointReload, teammateWarp;
    private string retryScene;
    private Vector2? warpEntry;
    private string warpScene;
    private int dreamRecoverySceneHandle = int.MinValue;
    private float dreamRecoveryAfter;
    private bool dreamRecoveryDone;
    private bool dreamRecoveryLogged;
    private HeroController lockedHero;
    private bool noDamage, heroBoxInactive, simulated, restoreControl, restoreAnimation;
    internal Vector3 DeathPosition { get; private set; }
    internal uint DeathSerial => downSerial;
    private string deathScene;
    private CameraController deathCamera;
    private CameraController.CameraMode cameraMode;
    private Vector3 cameraPosition;
    private string status = "";
    private float statusUntil;
    private Font font;
    private readonly DebugEventRing debugEvents = new DebugEventRing(256);
    private CoopDebugCommand debugCommand;
    private bool debugPanel;
    private bool debugRecoveryArmed;
    private Vector2 debugScroll;
    private string debugSignature = "";
    private string debugExportPath = "";
    internal static double Now => (DateTime.UtcNow - new DateTime(2020, 1, 1)).TotalSeconds;
    internal bool Connected => api != null && api.NetClient.IsConnected && Session.Length > 0;
    internal bool Playable => HeroController.instance != null && GameManager.instance != null &&
        !GameManager.instance.IsInSceneTransition && HeroController.instance.isHeroInPosition;
    private bool CanStartWarp => Playable && !SceneUtil.IsNonGameplayScene(UnitySceneManager.GetActiveScene().name) &&
        !Down && !Resetting && !Returning && !changingScene &&
        HeroController.instance.acceptingInput && !HeroController.instance.controlReqlinquished &&
        !HeroController.instance.cState.dead && !HeroController.instance.cState.hazardDeath &&
        !HeroController.instance.cState.hazardRespawning && !HeroController.instance.cState.transitioning &&
        HeroController.instance.transitionState == GlobalEnums.HeroTransitionState.WAITING_TO_TRANSITION &&
        !GameManager.instance.isPaused && Time.timeScale > 0;

    private void DebugEvent(string code, DebugEventSeverity severity = DebugEventSeverity.Info, string detail = "") {
        var scene = UnitySceneManager.GetActiveScene().name;
        debugEvents.Add(Now, severity, code, scene, detail);
    }

    internal void ToggleDebugPanel() {
        debugPanel = !debugPanel;
        debugRecoveryArmed = false;
        if (debugPanel) DebugEvent("debug-panel-opened");
    }

    internal SaveSafetySnapshot BuildDebugSnapshot() {
        var gm = GameManager.instance;
        var hero = HeroController.instance;
        var camera = GameCameras.instance?.cameraController;
        var pd = PlayerData.instance;
        var body = hero == null ? null : hero.GetComponent<Rigidbody2D>();
        var snapshot = new SaveSafetySnapshot {
            SceneName = UnitySceneManager.GetActiveScene().name,
            GameManagerScene = gm?.sceneName ?? "",
            EntryGateName = gm?.entryGateName ?? "",
            IsInSceneTransition = gm != null && gm.IsInSceneTransition,
            HasFinishedEnteringScene = gm != null && gm.HasFinishedEnteringScene,
            HeroInPosition = hero != null && hero.isHeroInPosition,
            HeroAcceptingInput = hero != null && hero.acceptingInput,
            HeroControlRelinquished = hero != null && hero.controlReqlinquished,
            HeroDead = hero != null && hero.cState.dead,
            HeroHazardDeath = hero != null && hero.cState.hazardDeath,
            HeroHazardRespawning = hero != null && hero.cState.hazardRespawning,
            HeroTransitioning = hero != null && hero.cState.transitioning,
            HeroBoxInactive = HeroBox.inactive,
            BodySimulated = body == null || body.simulated,
            CameraMode = camera?.mode.ToString() ?? "",
            CameraFrozen = camera != null && camera.mode == CameraController.CameraMode.FROZEN,
            TransitionState = hero?.transitionState.ToString() ?? "",
            Connected = Connected,
            SessionPresent = Session.Length != 0,
            PeerCount = Peers.Count,
            ChangingScene = changingScene,
            Down = Down,
            Resetting = Resetting,
            Returning = Returning,
            HasNativeDeath = personalDeath != null,
            HasBossContextGuard = BossTravel.HasTransientContext,
            HasCheckpointReload = checkpointReload != null,
            HasTeammateWarp = teammateWarp != null,
            WarpScene = warpScene ?? "",
            WarpEntry = warpEntry.HasValue ? warpEntry.Value.x.ToString("R") + "," + warpEntry.Value.y.ToString("R") : "",
            RetryScene = retryScene ?? "",
            InBossSequence = BossSequenceController.IsInSequence,
            BossSequenceName = BossTravel.CurrentSequenceName,
            ResourceReceiptCount = CoopMod.Statistics?.Resources?.Claimed?.Count ?? 0,
            SharedProgressCount = CoopMod.Statistics?.SharedProgress?.Count ?? 0
        };
        if (pd != null) {
            snapshot.RespawnScene = pd.GetString("respawnScene") ?? "";
            snapshot.RespawnMarkerName = pd.GetString("respawnMarkerName") ?? "";
            snapshot.RespawnType = pd.GetInt("respawnType");
            snapshot.BossReturnEntryGate = pd.GetString("bossReturnEntryGate") ?? "";
        }
        return snapshot;
    }

    private void SampleDebugState() {
        if (Time.unscaledTime < nextDebugSample) return;
        nextDebugSample = Time.unscaledTime + .5f;
        var snapshot = BuildDebugSnapshot();
        sampledDebugState = snapshot;
        var signature = string.Join("|", snapshot.SceneName, snapshot.IsInSceneTransition, snapshot.HasFinishedEnteringScene,
            snapshot.HeroAcceptingInput, snapshot.HeroControlRelinquished, snapshot.HeroBoxInactive, snapshot.BodySimulated,
            snapshot.HeroInPosition, snapshot.HeroTransitioning, snapshot.TransitionState,
            snapshot.ChangingScene, snapshot.Down, snapshot.Resetting, snapshot.Returning, snapshot.WarpScene,
            snapshot.HasCheckpointReload, snapshot.HasTeammateWarp);
        if (signature == debugSignature) return;
        debugSignature = signature;
        var risks = snapshot.RiskCodes();
        if (risks.Count > 0) DebugEvent("state-risk", DebugEventSeverity.Warning, string.Join(",", risks));
    }
    private float nextDebugSample;

    internal string ExportDebugReport() {
        try {
            var directory = Path.Combine(Application.persistentDataPath, "HollowConnect", "diagnostics");
            Directory.CreateDirectory(directory);
            var name = "diagnostic-" + DateTime.UtcNow.ToString("yyyyMMdd-HHmmss") + ".json";
            var path = Path.Combine(directory, name);
            File.WriteAllText(path, SafeDiagnosticsFormatter.Format(BuildDebugSnapshot(), debugEvents.Snapshot(), DateTime.UtcNow.ToString("o")));
            debugExportPath = path;
            DebugEvent("diagnostic-exported", DebugEventSeverity.Info);
            return path;
        } catch (Exception e) {
            DebugEvent("diagnostic-export-failed", DebugEventSeverity.Critical, e.GetType().Name);
            UnityEngine.Debug.LogError("HollowConnect diagnostic export failed: " + e);
            return "";
        }
    }

    // This routine only releases HollowConnect's transient locks. It never
    // calls SaveGame, ReadyForRespawn, SceneData.SaveMyState, or changes any
    // progress/receipt field. An accepted native scene load is allowed to
    // finish; the White Defender repair is scoped to its known stale entry.
    internal void RecoverTransientState() {
        DebugEvent("recovery-requested", DebugEventSeverity.Warning);
        if (checkpointReload != null) StopCoroutine(checkpointReload);
        if (teammateWarp != null) StopCoroutine(teammateWarp);
        checkpointReload = teammateWarp = null;
        changingScene = false;
        if (!Resetting) { readySent = false; retryScene = null; }
        if (GameManager.instance == null || !GameManager.instance.IsInSceneTransition) {
            warpEntry = null; warpScene = null;
        }
        if (!Down && !Resetting && !Returning) Unlock();
        RecoverWhiteDefenderDreamEntry();
        DebugEvent("recovery-complete", DebugEventSeverity.Info);
    }

    public void Initialize(IClientApi clientApi, CoopClientAddon addon) {
        Current = this;
        api = clientApi;
        sender = api.NetClient.GetNetworkSender<Channel>(addon);
        var receiver = api.NetClient.GetNetworkReceiver<Channel>(addon, Wire.CreateCollection);
        receiver.RegisterPacketHandler<Message>(Channel.Presence, Receive);
        receiver.RegisterPacketHandler<Message>(Channel.Event, Receive);
        api.ClientManager.ConnectEvent += OnConnect;
        api.ClientManager.DisconnectEvent += OnDisconnect;
        foreach (var key in ProgressCatalog.Limits.Keys) {
            var field = typeof(PlayerData).GetField(key, BindingFlags.Instance | BindingFlags.Public);
            if (field != null && (field.FieldType == typeof(bool) || field.FieldType == typeof(int))) fields[key] = field;
        }
        map = new MapService(this);
        shades = new ShadeService(this);
        resources = new ResourceShareService(() => Connected && Shared,
            () => Playable && !Down && !Resetting && !Returning && !changingScene,
            LocalResources, id => Send(new Message { Kind = Kind.ResourceOffer, Key = id }),
            s => CoopMod.Instance?.Log(s), ShowSharedResource);
        resources.Register();
        progressPickups = new ProgressPickupService(() => Playable);
        progressPickups.Register();
        mapPurchases = new MapPurchaseService(key => Connected && Shared && PlayerData.instance != null &&
            (PlayerData.instance.GetBoolInternal(key) || granted.Get(key) == 1));
        mapPurchases.Register();
        teammateDream = new TeammateDreamNail(this);
        BossTravel.Register();
        CoopContext.CaptureBossContext = BossTravel.CaptureContext;
        CoopContext.ApplyBossContext = BossTravel.ApplyContext;
        CoopContext.CommitBossContext = BossTravel.CommitTransientContext;
        CoopContext.RestoreBossContext = BossTravel.RestoreTransientContext;
        CoopContext.BossTransitionReady = () => Playable && !Down && !Resetting && !Returning && !changingScene && !BossSettlementPending;
        CoopContext.AwaitBossSettlement = AwaitBossSettlement;
        CoopContext.LocalRescueDown = () => Connected && Down;
        CoopContext.LivingBossTarget = LivingBossTarget;
        CoopContext.FlexibleBossTargetingOn = () => Connected && flexibleBossTargeting;
        CoopContext.BossTargetSession = () => Session;
        CoopContext.ChooseBossTarget = ChooseBossTarget;
        CoopContext.BossTargetObject = BossTargetObject;
        CoopContext.AttackSourcePlayer = AttackSourcePlayer;
        CoopMod.ActiveRoomSettingLabel = ActiveRoomSettingLabel;
        CoopContext.RemotePlayerOpacity = () => float.IsNaN(CoopMod.Settings.OtherPlayerOpacity) ? 1 : Mathf.Clamp01(CoopMod.Settings.OtherPlayerOpacity);
        CoopContext.PlayerNameColor = id => CoopMod.Settings.ColoredPlayerNames ? MapService.PeerColor(id) : Color.white;
        CoopContext.BossHealthMultiplier = CurrentBossHealthMultiplier;
        CoopContext.BossDefeated = ReportBossDefeated;
        CoopContext.BossEncounter = BeginDamageEncounter;
        CoopContext.BossDamage = ReportBossDamage;
        RegisterDamageRanking();
        CoopContext.FinishRadianceIntro = scene => Playable && !Down && !Resetting && !Returning &&
            HeroController.instance.transitionState == GlobalEnums.HeroTransitionState.WAITING_TO_TRANSITION && FinishRadianceWarpPresentation(scene);
        map.Register();
        On.HeroController.Die += OnDie;
        On.HeroController.TakeDamage += TakeDamage;
        On.HeroAudioController.PlaySound += TrackHitSound;
        ModHooks.AfterTakeDamageHook += AfterTakeDamage;
        On.CameraController.LateUpdate += CameraUpdate;
        On.GameManager.FindEntryPoint += FindWarpEntry;
        On.GameManager.BeginSceneTransitionRoutine += TrackNativeTransition;
        GameManager.SceneTransitionBegan += TrackNativeLoader;
        On.HeroController.EnterSceneDreamGate += CompleteWarpEntry;
        ModHooks.SetPlayerBoolHook += OnSetBool;
        ModHooks.SetPlayerIntHook += OnSetInt;
        font = Font.CreateDynamicFontFromOSFont(new[] { "Microsoft YaHei", "Arial" }, 18);
        debugCommand = new CoopDebugCommand(this);
        api.CommandManager.RegisterCommand(debugCommand);
        DebugEvent("runtime-initialized", DebugEventSeverity.Info);
    }

    private static ResourceClaims LocalResources() {
        if (CoopMod.Statistics.Resources == null) CoopMod.Statistics.Resources = new ResourceClaims();
        return CoopMod.Statistics.Resources;
    }

    private void OnConnect() {
        CoopMod.RoomRuleRevision++;
        ResetLinkedHealth();
        flexibleBossTargeting = false;
        DebugEvent("connect-start", DebugEventSeverity.Info);
        BossTravel.RestoreTransientContext();
        teammateDream.Reset();
        ResetRescueOptions();
        ResetStoryRescuePlacement();
        resources.ResetConnection(); offeredResources.Clear(); resourceNotifications.Clear();
        grantedResources.Clear();
        progressAuditRequested = false; progressAuditSerial = 0;
        localName = "";
        Session = ""; Peers.Clear(); offered.Clear(); granted = new ProgressLedger(); sequence = 0; handledWarp = 0; ReleasedRound = 0;
        dreamRecoverySceneHandle = int.MinValue; dreamRecoveryDone = false;
        dreamRecoveryLogged = false;
        entryProtection.Clear(); protectionSceneHandle = int.MinValue; protectionSession = "";
        Send(new Message { Kind = Kind.Hello });
    }
    private void OnDisconnect() {
        var linkedDeath = linkedHealthMode ? personalDeath ?? CaptureLinkedDisconnectDeath() : null;
        ResetLinkedHealth();
        ResetDamageRanking();
        CoopMod.RoomRuleRevision++;
        flexibleBossTargeting = false;
        DebugEvent("disconnect", DebugEventSeverity.Warning);
        BossTravel.RestoreTransientContext();
        teammateDream.Reset();
        resources.ResetConnection(); offeredResources.Clear(); resourceNotifications.Clear();
        grantedResources.Clear();
        progressAuditRequested = false; progressAuditSerial = 0;
        localName = "";
        CancelBenchReturn();
        CancelTransitions();
        ResetRescueOptions();
        ResetStoryRescuePlacement();
        shades.Clear(); Unlock(); Down = Resetting = changingScene = false; Session = ""; Peers.Clear(); offered.Clear();
        ResetBossVictoryRescue();
        dreamRecoverySceneHandle = int.MinValue; dreamRecoveryDone = false;
        dreamRecoveryLogged = false;
        CoopContext.Resetting = false; readySent = false; retryScene = null; map.Clear();
        // The original hero owns an accepted formal death; disconnect only
        // abandons our wait and must not cancel its animation/save/return.
        personalDeath = null; entryProtection.Clear(); protectionSession = "";
        if (linkedDeath != null && !linkedDeath.Started) linkedDeath.Start();
        debugSignature = "";
    }
    private void Send(Message message) {
        if (!api.NetClient.IsConnected) return;
        message.Session = Session; message.Round = CoopContext.Epoch;
        sender.SendCollectionData(message.IsReliable ? Channel.Event : Channel.Presence, message);
    }

    private void Receive(Message m) {
        if (m.Kind == Kind.Welcome) {
            if (Session.Length != 0 && Session != m.Session) return;
            Session = m.Session; LocalId = m.Id; CoopContext.Epoch = m.Round;
            localName = m.Name;
            CoopMod.Statistics.Merge(localName, 0);
            foreach (var entry in CoopMod.Statistics.Deaths)
                Send(new Message { Kind = Kind.DeathHistory, Name = entry.Key, Serial = entry.Value });
            Shared = (m.Value & 1) != 0; RescueEnabled = (m.Value & 2) != 0; WarpEnabled = (m.Value & 4) != 0;
            protectEntry = (m.Value & 8) != 0;
            // An older Welcome has no opt-out bit and keeps existing behavior.
            ShareCharms = (m.Value & 16) == 0;
            bossVictoryRescue = (m.Value & 32) == 0;
            bossHealthScaling = (m.Value & 64) != 0;
            SpectateAfterExhaustion = (m.Value & 128) != 0;
            flexibleBossTargeting = (m.Value & 256) != 0;
            damageRankingEnabled = (m.Value & 512) != 0;
            ApplyLinkedWelcome(m);
            RescueLimit = (int)Math.Min(999u, m.Aux);
            bossExtraHealth = m.X; bossHealthDecay = m.Y;
            CoopMod.RoomRuleRevision++;
            Notice("协作连接已建立" + (Shared ? " · 共享进度开启" : "") + " · 按住 " + CoopMod.Settings.DeathLeaderboardKey + " 查看死亡榜");
            nextPresence = nextProgress = 0;
            progressAuditRequested = Shared;
            return;
        }
        if (m.Session != Session || Session.Length == 0) return;
        if (m.Kind == Kind.DamageSnapshot) { ReceiveDamageSnapshot(m); return; }
        if (m.Kind == Kind.ProgressGrant && m.Key == Wire.ProgressAuditKey) {
            if (Shared && m.Serial > progressAuditSerial) {
                progressAuditSerial = m.Serial; progressAuditRequested = true;
            }
            return;
        }
        // Absolute saved totals merge by maximum; copied histories must not add twice.
        if (m.Kind == Kind.DeathCount) {
            CoopMod.Statistics.Merge(m.Name, m.Serial);
            return;
        }
        if (m.Kind == Kind.ResourceGrant) {
            if (Shared && ResourceCatalog.TryGet(m.Key, out _)) {
                grantedResources.Add(m.Key);
                resources.Receive(m.Key);
            }
            return;
        }
        if (m.Kind == Kind.LinkedHealthState) { ReceiveLinkedHealthState(m); return; }
        if (m.Kind == Kind.LinkedHealthEnd) { EndLinkedSteel(); return; }
        if (m.Kind == Kind.Wipe) {
            // Welcome may carry the current round to a late joiner who has
            // never released it. Completed rounds, however, cannot reopen.
            if (m.Round <= ReleasedRound || m.Round <= CoopContext.Epoch && Resetting || m.Round < CoopContext.Epoch) return;
            CoopContext.Epoch = m.Round; CoopContext.Resetting = true;
            if (LinkedHealthRuleEnabled) { linkedPending.Clear(); linkedDeathRequested = true; }
            CancelBenchReturn();
            CancelTransitions();
            StopSpectating();
            RestoreSpectatorPeers();
            shades.Clear();
            Resetting = true; Down = false; readySent = false; Peers.Clear();
            checkpointReload = StartCoroutine(ReloadCheckpoint(Session, m.Round)); return;
        }
        if (m.Round != CoopContext.Epoch) return;
        switch (m.Kind) {
            case Kind.RescueStatus:
                ReceiveRescueStatus(m); break;
            case Kind.BossVictory:
                ReceiveBossVictory(m); break;
            case Kind.Roster:
                CoopMod.Statistics.Merge(m.Name, 0);
                if (m.Id == LocalId) break;
                if (!Peers.TryGetValue(m.Id, out var p)) Peers[m.Id] = p = new Peer();
                if (p.State != null && m.Serial < p.State.Serial) break;
                if (m.Aux < p.LifeVersion) break;
                if (m.Aux >= p.LifeVersion) { p.LifeVersion = m.Aux; p.Life = (Life)m.Value; }
                m.Value = (int)p.Life; p.State = m; p.Seen = Now;
                if (m.Value == (int)Life.Down && uint.TryParse(m.Key, out var incomingDown) && incomingDown >= p.DownSerial) p.DownSerial = incomingDown;
                break;
            case Kind.RescueAnchor:
                ReceiveRescueAnchor(m); break;
            case Kind.Spectating:
                ReceiveSpectating(m); break;
            case Kind.BossSettlementReady:
                ReceiveBossSettlementReady(m); break;
            case Kind.Left:
                shades.Remove(m.Id); Peers.Remove(m.Id); map.Remove(m.Id); break;
            case Kind.Down:
                var downLife = m.Value == (int)Life.Spectating ? Life.Spectating : Life.Down;
                if (m.Id == LocalId) {
                    if (Down && m.Serial == downSerial && m.Aux >= localDownLifeVersion) {
                        localDownLifeVersion = m.Aux;
                        if (Spectating && downLife != Life.Spectating) StopSpectating();
                        Spectating = downLife == Life.Spectating;
                        if (Spectating) { shades.Remove(LocalId); HideSpectator(); Notice("救援不可用，正在观战 · 按 " + CoopMod.Settings.GiveUpKey + " 返回"); }
                    }
                    break;
                }
                if (!Peers.TryGetValue(m.Id, out var downPeer)) Peers[m.Id] = downPeer = new Peer();
                // An unreliable roster can arrive before the reliable event.
                // Keep state ordering separate from one-shot presentation.
                if (m.Aux < downPeer.LifeVersion || m.Serial < downPeer.DownSerial ||
                    m.Aux == downPeer.LifeVersion && downPeer.Life != downLife) break;
                downPeer.DownSerial = m.Serial; downPeer.LifeVersion = m.Aux; downPeer.Life = downLife;
                if (downPeer.State == null) downPeer.State = new Message { Id = m.Id, Name = m.Name };
                downPeer.State.Value = (int)downLife; downPeer.State.Scene = m.Scene;
                downPeer.State.X = m.X; downPeer.State.Y = m.Y; downPeer.Seen = Now;
                if (m.Serial > downPeer.DownFeedbackSerial) {
                    downPeer.DownFeedbackSerial = m.Serial;
                    PlayDownFeedback(m, true);
                }
                break;
            case Kind.Revived:
            case Kind.BossRevived:
                var bossRecovery = m.Kind == Kind.BossRevived;
                if (Resetting || m.Serial == 0 || !bossRecovery && m.Target == m.Id) break;
                if (m.Id == LocalId && Down && (!Spectating || bossRecovery) && m.Serial == downSerial &&
                    m.Scene == deathScene && m.Aux > localDownLifeVersion && !Returning && !changingScene) {
                    // Nail revival revalidates the shade's support. Settlement
                    // instead uses the server-validated surviving participant.
                    var rescued = HeroController.instance;
                    Vector3? landing = bossRecovery ? new Vector3(m.X, m.Y, DeathPosition.z) : !IsGodhomeRescueScene() ? FindStoryRescuePosition(rescued) :
                        TryWarpLanding(DeathPosition, deathScene, out var supported)
                        ? new Vector3(supported.x, supported.y, DeathPosition.z) : FindRescuePosition(rescued);
                    if (!landing.HasValue) {
                        if (!IsGodhomeRescueScene()) {
                            // Keep this accepted death's identity while the
                            // server transitions its Alive row to Returning.
                            giveUpRequested = true;
                            Send(new Message { Kind = Kind.RescueUnavailable, Serial = downSerial, Aux = m.Aux, Scene = deathScene });
                            Notice("地形已无法承载复活，正在返回自己的复活点…");
                            break;
                        }
                        // The server has accepted the old episode. Advertise a
                        // fresh Down before using its existing personal-return
                        // protocol; retain the original native death context.
                        downSerial++;
                        SendDown(); RequestPersonalReturn();
                        break;
                    }
                    rescued.transform.position = landing.Value;
                    Physics2D.SyncTransforms();
                    localDownLifeVersion = m.Aux;
                    giveUpRequested = false;
                    StopSpectating(); shades.Remove(m.Id); Down = false; Unlock();
                    if (!bossRecovery && !IsGodhomeRescueScene() && rescueAirAnchor) {
                        // Lock suspended gravity and death may have consumed
                        // the air moves. Return native movement before letting
                        // the rescued player recover on the validated wall.
                        rescued.ResetAirMoves(); rescued.AffectedByGravity(true);
                    }
                    if (bossRecovery) GameCameras.instance?.cameraController?.PositionToHero(true);
                    personalDeath = null;
                    PlayerData.instance.SetInt("healthBlue", 0); Heal(1);
                    Notice(bossRecovery ? "Boss已击败，已在存活队友身边复活 · 不消耗救援次数" : "队友击破了你的暗影，一血复活");
                    rescued.StartCoroutine("Invulnerable", rescued.INVUL_TIME);
                    PlayRescueFeedback(m);
                }
                // Roster is unreliable and can already report Alive before the
                // reliable acceptance arrives. Reward/presentation has its own
                // serial; an old acceptance must not affect a later death.
                if (Peers.TryGetValue(m.Id, out var revived) && m.Serial >= revived.DownSerial &&
                    m.Serial > revived.RescueFeedbackSerial && m.Aux >= revived.LifeVersion &&
                    (m.Aux > revived.LifeVersion || revived.Life == Life.Alive || revived.Life == Life.Loading)) {
                    revived.RescueFeedbackSerial = m.Serial;
                    shades.Remove(m.Id);
                    revived.LifeVersion = m.Aux; revived.Life = Life.Alive;
                    if (revived.State != null) revived.State.Value = (int)Life.Alive;
                    if (!bossRecovery && m.Target == LocalId && Connected && Playable && !Down && !Returning && !changingScene)
                        HeroController.instance.AddMPCharge(RescueFeedback.SoulReward);
                    PlayRescueFeedback(m);
                }
                break;
            case Kind.Returning:
                if (m.Id == LocalId && Down && m.Serial == downSerial && !Resetting && !Returning) {
                    StopSpectating();
                    encounterEnded = m.Name == "boss-failed" || m.Name == "boss-victory";
                    giveUpRequested = false; Returning = true; Down = false; shades.Remove(LocalId);
                    if (m.Name == "boss-failed") Notice("Boss房内无人能继续战斗，本场失败，返回自己的复活点");
                    benchReturn = StartCoroutine(ReturnToBench(m));
                }
                if (Peers.TryGetValue(m.Id, out var leaving) && m.Aux > leaving.LifeVersion) {
                    shades.Remove(m.Id); leaving.LifeVersion = m.Aux; leaving.Life = Life.Returning;
                    if (leaving.State != null) { leaving.State.Value = (int)Life.Returning; leaving.State.HasMap = leaving.State.IsReady = false; }
                }
                break;
            case Kind.Returned:
                if (m.Id == LocalId && Returning && m.Serial == downSerial && !Resetting) {
                    Returning = false; giveUpRequested = false; Unlock(); personalDeath = null;
                    Notice(encounterEnded ? "本场已结束，已回到自己的复活点" : "已回到自己的复活点，队友继续当前战斗");
                }
                if (Peers.TryGetValue(m.Id, out var returned) && m.Aux > returned.LifeVersion) {
                    returned.LifeVersion = m.Aux; returned.Life = Life.Alive;
                    if (returned.State != null) {
                        returned.State.Value = (int)Life.Alive; returned.State.Scene = m.Scene;
                        returned.State.X = m.X; returned.State.Y = m.Y; returned.State.HasMap = returned.State.IsReady = false;
                    }
                }
                break;
            case Kind.GiveUpDenied:
                if (m.Id == LocalId && m.Serial == downSerial) {
                    giveUpRequested = false;
                    if (Down) Notice("当前无法放弃或观战：没有可用队友／复活点，或本场重试已开始");
                }
                break;
            case Kind.Release:
                if (!Resetting) break;
                ReleasedRound = m.Round;
                Resetting = false; CoopContext.Resetting = false; readySent = false; Unlock(); personalDeath = null;
                if (LinkedHealthRuleEnabled) {
                    linkedHealthDeltaSerial = 0;
                    linkedPersonalHealth = LinkedPersonalCapacity(PlayerData.instance);
                    if (linkedHealthCurrent > 0) ApplyLinkedHealthState();
                }
                Notice("全队加载完成，重新出发"); break;
            case Kind.ProgressGrant:
                if (ProgressCatalog.CanShare(m.Key, Shared, ShareCharms) && granted.Merge(m.Key, m.Value)) {
                    ApplyProgress(m.Key, granted.Get(m.Key));
                    // The prompt is deliberately independent of scene/hero
                    // readiness. A player may be in another room or in a
                    // native transition when the host grants an ability.
                    var dreamVictory = m.Key == "falseKnightDreamDefeated" || m.Key == "infectedKnightDreamDefeated" || m.Key == "mageLordDreamDefeated";
                    Notice((dreamVictory ? "共享通关记录：" : "共享能力已获得：") + ProgressName(m.Key));
                }
                break;
            case Kind.Warp:
                if (m.Serial <= handledWarp || !CanStartWarp || string.IsNullOrEmpty(m.Scene) || SceneUtil.IsNonGameplayScene(m.Scene)) break;
                handledWarp = m.Serial;
                var travel = StartCoroutine(WarpTo(m));
                teammateWarp = changingScene ? travel : null;
                break;
        }
    }

    private void PlayRescueFeedback(Message m) {
        if (!Playable || m.Scene != CoopContext.LocalScene) return;
        RescueFeedback.Play(new Vector3(m.X, m.Y, 0));
        if (m.Id != LocalId) RescueFlash.Play(PlayerObject(m.Id));
    }

    private void Update() {
        if (EmergencyShortcutPressed()) RequestEmergencyDirtmouthBench();
        if (emergencyBenchActive) return;
        if (KeyPressed(KeyCode.F9)) ToggleDebugPanel();
        SampleDebugState();
        if (resourceSlot != CoopMod.Statistics || resourcePlayerData != PlayerData.instance) {
            if (resourceSlot != null || resourcePlayerData != null) ResetLinkedHealth(true);
            resourceSlot = CoopMod.Statistics; resourcePlayerData = PlayerData.instance;
            resourceImported = false; resourceNotifications.Clear();
            resources.ResetSlot(); offeredResources.Clear();
            progressAuditRequested = Connected && Shared;
        }
        progressPickups.Prepare();
        if (!resourceImported && Playable && !SceneUtil.IsNonGameplayScene(UnitySceneManager.GetActiveScene().name))
            resourceImported = resources.ImportNativeReceipts();
        if (progressAuditRequested && Connected && Shared && Playable && resources.Audit()) {
            progressAuditRequested = false; resourceImported = true;
            // ResetSlot only discards work tied to the previous slot. Replay
            // this session's accepted union after importing the loaded slot's
            // native history, so already-spent ore is never refunded.
            foreach (var source in grantedResources) resources.Receive(source);
            offered.Clear(); offeredResources.Clear(); nextProgress = 0;
            DebugEvent("progress-join-audit", DebugEventSeverity.Info);
        }
        resources.Tick();
        TickLinkedHealth();
        resourceNotifications.Tick(Connected && Shared && Playable && !Down && !Resetting && !Returning &&
            !GameManager.instance.isPaused && Time.timeScale > 0 &&
            HeroController.instance.acceptingInput && !HeroController.instance.controlReqlinquished);
        RecoverWhiteDefenderDreamEntry();
        RecoverPendingWarpEntry();
        if (!Connected) return;
        TickDamageRanking();
        TickRescueOptions();
        TickStoryRescuePlacement();
        RefreshEntryProtection();
        if (lockedHero != null && (Down || Resetting || Returning)) {
            lockedHero.RelinquishControl(); lockedHero.GetComponent<Rigidbody2D>().velocity = Vector2.zero;
            lockedHero.takeNoDamage = true; HeroBox.inactive = true;
            if (Down) lockedHero.transform.position = Spectating ? SpectatorPosition() : DeathPosition;
        }
        shades.Synchronize();
        if (Time.unscaledTime >= nextPresence) { nextPresence = Time.unscaledTime + .1f; SendPresence(); }
        if (Resetting && !readySent && !changingScene && Playable && UnityEngine.SceneManagement.SceneManager.GetActiveScene().name == retryScene) {
            Lock(true); readySent = true; Send(new Message { Kind = Kind.Ready, Scene = retryScene });
        }
        if (!Playable || Resetting || changingScene || Returning) return;
        RefreshPantheonRestProgress();
        if (Down && !LinkedHealthRuleEnabled && !giveUpRequested && KeyPressed(CoopMod.Settings.GiveUpKey)) {
            RequestGiveUp();
        }
        if (Shared && Time.unscaledTime >= nextProgress) { nextProgress = Time.unscaledTime + .5f; CheckProgress(); }
        if (KeyPressed(CoopMod.Settings.SelectTeammateKey)) {
            var ids = Peers.Where(p => p.Value.State != null).Select(p => p.Key).OrderBy(i => i).ToArray();
            if (ids.Length > 0) {
                var index = Array.IndexOf(ids, Selected);
                Selected = ids[(index + 1) % ids.Length];
                Notice("已选队友：" + Peers[Selected].State.Name);
            }
        }
        if (!Down && WarpEnabled && KeyPressed(CoopMod.Settings.TeleportKey)) {
            if (!CanStartWarp) { Notice("当前无法传送，请等待操作或复位完成"); return; }
            if (!Peers.ContainsKey(Selected)) Selected = Peers.Keys.OrderBy(i => i).FirstOrDefault();
            if (Peers.ContainsKey(Selected)) Send(new Message { Kind = Kind.WarpRequest, Target = Selected, Serial = ++warpSerial });
            else Notice("没有可传送的队友");
        }
    }

    // A dream-gate transition normally calls EnterSceneDreamGate, which
    // finishes the scene and releases the hero immediately.  The White
    // Defender story scene can still arrive with the native camera fade and
    // no-input state left behind when another client is present. Keep this
    // recovery narrowly scoped to that scene and only run it after the native
    // scene transition has finished; this restores the same post-entry state
    // as CompleteWarpEntry without touching ordinary dreams or cutscenes.
    private void RecoverWhiteDefenderDreamEntry() {
        var scene = UnitySceneManager.GetActiveScene();
        if (scene.name != "Dream_04_White_Defender") {
            dreamRecoverySceneHandle = int.MinValue; dreamRecoveryDone = false; dreamRecoveryLogged = false; return;
        }
        if (dreamRecoverySceneHandle != scene.handle) {
            dreamRecoverySceneHandle = scene.handle;
            dreamRecoveryAfter = Time.realtimeSinceStartup + 5.5f;
            dreamRecoveryDone = false;
            dreamRecoveryLogged = false;
            return;
        }
        if (dreamRecoveryDone || Time.realtimeSinceStartup < dreamRecoveryAfter) return;
        var gm = global::GameManager.instance;
        var hero = HeroController.instance;
        var camera = GameCameras.instance?.cameraController;
        if (gm == null || hero == null || gm.isPaused || gm.IsLoadingSceneTransition ||
            !scene.isLoaded || gm.sceneName != scene.name || Down || Resetting || Returning || personalDeath != null ||
            PlayerData.instance.GetBool("atBench") || PlayerData.instance.GetBool("disablePause") ||
            hero.cState.dead || hero.cState.hazardDeath || hero.cState.hazardRespawning) return;
        // A failed old co-op warp can leave the native transition flag set
        // forever. Give a legitimate dream entry a few extra seconds, then
        // treat the exact scene plus the stale control state as recoverable.
        if (gm.IsInSceneTransition && Time.realtimeSinceStartup < dreamRecoveryAfter + 4f) return;
        var staleEntry = !gm.HasFinishedEnteringScene || !hero.isHeroInPosition ||
            !hero.acceptingInput || hero.controlReqlinquished || camera != null && camera.mode == CameraController.CameraMode.FROZEN;
        if (!staleEntry) {
            dreamRecoveryDone = true; return;
        }
        if (!dreamRecoveryLogged) {
            dreamRecoveryLogged = true;
            DebugEvent("white-defender-entry-recovery", DebugEventSeverity.Warning);
            CoopMod.Instance?.Log("Recovering stale White Defender entry state after co-op/disconnect");
            if (Connected) Notice("已恢复白色防御者梦境入场状态");
        }
        if (teammateWarp != null) StopCoroutine(teammateWarp);
        teammateWarp = null; changingScene = false; warpEntry = null; warpScene = null;
        if (lockedHero == hero) Unlock();
        if (!hero.isHeroInPosition) hero.EnterSceneDreamGate();
        if (!gm.HasFinishedEnteringScene) gm.FinishedEnteringScene();
        hero.RegainControl(); hero.StartAnimationControl();
        var body = hero.GetComponent<Rigidbody2D>();
        if (body != null) { body.simulated = true; body.velocity = Vector2.zero; }
        HeroBox.inactive = false;
        if (camera != null && camera.mode == CameraController.CameraMode.FROZEN)
            camera.SetMode(CameraController.CameraMode.FOLLOWING);
        camera?.PositionToHero(false);
        gm.FadeSceneIn();
        dreamRecoveryDone = true;
    }


    // A native BeginSceneTransition cannot be cancelled by StopCoroutine. If
    // the native entry callback was skipped (for example after a disconnect),
    // clear our custom FindEntryPoint override as soon as the accepted load is
    // no longer running. This prevents a later unrelated dream gate from
    // reading a stale room/position and gives the native hero its control back.
    private void RecoverPendingWarpEntry() {
        if (!warpEntry.HasValue || string.IsNullOrEmpty(warpScene)) return;
        var gm = GameManager.instance;
        var hero = HeroController.instance;
        if (gm == null || gm.IsInSceneTransition) return;
        if (UnitySceneManager.GetActiveScene().name == warpScene && hero != null && gm.HasFinishedEnteringScene &&
            hero.isHeroInPosition && !hero.cState.dead && !hero.cState.hazardDeath) {
            hero.RegainControl(); hero.StartAnimationControl();
            GameCameras.instance?.cameraController?.PositionToHero(false);
            gm.FadeSceneIn();
            DebugEvent("warp-entry-recovered", DebugEventSeverity.Warning);
        } else {
            DebugEvent("warp-entry-cleared", DebugEventSeverity.Warning);
        }
        warpEntry = null; warpScene = null;
    }

    private void SendPresence() {
        var hero = HeroController.instance;
        if (hero == null) return;
        var pos = hero.transform.position;
        var location = Vector3.zero;
        var hasMap = Playable && !changingScene && !Returning && map.TryLocation(out location);
        var pd = PlayerData.instance;
        var message = new Message { Kind = Kind.Presence, Serial = ++sequence, Scene = UnityEngine.SceneManagement.SceneManager.GetActiveScene().name,
            Aux = (InBossEncounter() ? 1u : 0u) | (LinkedHealthRuleEnabled ? (uint)LocalLinkedRest() << 1 : 0u),
            LinkedRestEpoch = linkedRestEpoch,
            X = pos.x, Y = pos.y, HasMap = hasMap, MapX = hasMap ? location.x : 0, MapY = hasMap ? location.y : 0,
            IsReady = CanStartWarp, Key = pd.GetString("respawnScene") ?? "",
            Name = pd.GetString("respawnMarkerName") ?? "", Value = pd.GetInt("respawnType") };
        if (!BossTravel.Capture(message)) message.IsReady = false;
        if (LinkedHealthRuleEnabled && (!Playable || changingScene || SceneUtil.IsNonGameplayScene(message.Scene))) message.Aux |= 8;
        Send(message);
    }

    private IEnumerator OnDie(On.HeroController.orig_Die orig, HeroController self) {
        if (emergencyBenchActive) yield break;
        if (Connected && !Down && !Resetting && !Returning)
            Send(new Message { Kind = Kind.DeathCount, Serial = CoopMod.Statistics.Increment(localName) });
        if (LinkedHealthRuleEnabled) { BeginLinkedDeath(orig, self); yield break; }
        if (!Connected || !(RescueEnabled || SpectateAfterExhaustion && InBossEncounter()) || PlayerData.instance.permadeathMode != 0) { yield return orig(self); yield break; }
        if (Down || Resetting || Returning) yield break;
        personalDeath = new PersonalDeath(self, () => orig(self));
        deathScene = UnityEngine.SceneManagement.SceneManager.GetActiveScene().name;
        BeginStoryRescuePlacement(self);
        var landing = FindRescuePosition(self);
        DeathPosition = landing ?? self.transform.position;
        self.SetHeroParent(null);
        self.transform.position = DeathPosition;
        deathCamera = GameCameras.instance?.cameraController;
        if (deathCamera != null) {
            cameraMode = deathCamera.mode;
            // Keep the actual battle view when the shade is already visible.
            // PositionToStart would recenter locked Godhome arenas needlessly.
            var visible = deathCamera.cam.WorldToViewportPoint(DeathPosition);
            if (visible.z <= 0 || visible.x <= 0 || visible.x >= 1 || visible.y <= 0 || visible.y >= 1) {
                deathCamera.camTarget.PositionToStart();
                var view = deathCamera.KeepWithinSceneBounds(deathCamera.camTarget.transform.position);
                deathCamera.SnapTo(view.x, view.y);
                visible = deathCamera.cam.WorldToViewportPoint(DeathPosition);
                // A disabled hero may have crossed an obsolete lock area.
                // Keep that area's native ownership, but show the rescue point.
                if (visible.z <= 0 || visible.x <= 0 || visible.x >= 1 || visible.y <= 0 || visible.y >= 1) {
                    view = deathCamera.KeepWithinSceneBounds(DeathPosition);
                    deathCamera.SnapTo(view.x, view.y);
                }
            }
            cameraPosition = deathCamera.transform.position;
            deathCamera.FreezeInPlace();
        }
        Down = true; Spectating = !RescueAvailable && SpectateAfterExhaustion && InBossEncounter();
        downSerial++; localDownLifeVersion = 0; Lock(); Heal(1);
        if (Spectating) HideSpectator();
        shades.Synchronize();
        var message = SendDown();
        PlayDownFeedback(message, !takingDamage || !nativeHitPlayed);
        if (Spectating) Notice("正在观战 · 按 " + CoopMod.Settings.GiveUpKey + " 返回自己的复活点");
        else if (landing.HasValue && RescueAvailable) Notice("你已化为暗影；等待队友击破救援，或按 " + CoopMod.Settings.GiveUpKey +
            (SpectateAfterExhaustion ? " 放弃并转入观战" : " 放弃并回自己的复活点"));
        else RequestPersonalReturn();
    }

    private Message SendDown() {
        var message = new Message { Kind = Kind.Down, Serial = downSerial, Scene = deathScene, X = DeathPosition.x, Y = DeathPosition.y,
            Key = personalDeath.Destination, Name = personalDeath.Marker, Value = personalDeath.RespawnType, IsReady = InBossEncounter() };
        Send(message); return message;
    }
    private void RequestPersonalReturn() {
        giveUpRequested = true;
        Send(new Message { Kind = Kind.GiveUpRequest, Serial = downSerial,
            Scene = personalDeath?.Destination ?? "", Key = personalDeath?.Marker ?? "", Value = personalDeath?.RespawnType ?? 0 });
        Notice("正在按原版死亡规则返回自己的复活点…");
    }

    private Vector3? FindRescuePosition(HeroController hero) {
        if (!IsGodhomeRescueScene()) return FindStoryRescuePosition(hero);
        return FindLegacyRescuePosition(hero);
    }

    // Keep Godhome's established placement algorithm unchanged. Story rescue
    // uses a separate policy; formal death still belongs to PersonalDeath.
    private Vector3? FindLegacyRescuePosition(HeroController hero) {
        var origin = hero.transform.position;
        bool Ground(Vector2 point, out Vector3 position) {
            position = origin;
            // Guard the native extended ground query: without a hit it logs
            // an error and returns a point near (0,0), including in dreams.
            var hit = Physics2D.Raycast(point, Vector2.down, 50, 1 << 8);
            if (hit.collider == null || hit.distance == 0 || hit.collider.isTrigger || hit.collider.gameObject.scene.name != deathScene || hit.normal.y < .5f) return false;
            var grounded = hero.FindGroundPoint(point, true);
            if (!TryWarpLanding(grounded, deathScene, out var landing)) return false;
            position = new Vector3(landing.x, landing.y, origin.z); return true;
        }
        var zone = GameManager.instance.GetCurrentMapZone();
        var hazard = takingDamage && (damageHazard == 2 || damageHazard == 3 || damageHazard == 5);
        // White Defender's dream arena keeps a room-local blocker around the
        // fight. PlayerData.hazardRespawnLocation can still point to the
        // entry side of that blocker, so using it first can strand the shade
        // where the living teammate cannot reach it. Prefer the actual death
        // side when the native room has a supported floor below the victim.
        var localDream = deathScene == "Dream_04_White_Defender";
        if (localDream && Ground(origin, out var dreamLocal)) return dreamLocal;
        // Native story death chooses the closest Shade Marker. The shade's
        // Spawn Offset floats its art; the living knight needs ground below it.
        // Dream/Palace/Godhome and cross-room formal shade exceptions instead
        // use the current room's native hazard checkpoint for co-op rescue.
        if (!hazard && zone != "DREAM_WORLD" && zone != "WHITE_PALACE" && zone != "GODS_GLORY") {
            var marker = GameObject.FindGameObjectsWithTag("Shade Marker")
                .Where(g => g.scene.name == deathScene).OrderBy(g => (g.transform.position - origin).sqrMagnitude).FirstOrDefault();
            if (marker != null && Ground(marker.transform.position, out var marked)) return marked;
        }
        if (Ground(PlayerData.instance.GetVector3("hazardRespawnLocation"), out var safe)) return safe;
        return Ground(origin, out var nearby) ? nearby : (Vector3?)null;
    }

    private void PlayDownFeedback(Message m, bool sound) {
        DownFeedback.Play(new Vector3(m.X, m.Y, 0), m.Scene == CoopContext.LocalScene, sound);
    }

    internal bool EntryProtected => RefreshEntryProtection();
    private bool RefreshEntryProtection() {
        if (!Connected || !protectEntry) { entryProtection.Clear(); return false; }
        var scene = UnitySceneManager.GetActiveScene();
        if (protectionSceneHandle != scene.handle || protectionSession != Session) {
            protectionSceneHandle = scene.handle; protectionSession = Session; entryProtection.Begin();
        }
        var hero = HeroController.instance;
        var camera = GameCameras.instance?.cameraController;
        var ready = Playable && !changingScene && !Down && !Resetting && !Returning && hero.acceptingInput &&
            !hero.cState.dead && !hero.cState.transitioning && hero.transitionState == GlobalEnums.HeroTransitionState.WAITING_TO_TRANSITION &&
            Time.timeScale > 0 && camera != null && camera.mode != CameraController.CameraMode.FROZEN &&
            api.ClientManager is Hkmp.Game.Client.ClientManager manager && manager.SceneSnapshotReady;
        return entryProtection.Update(ready, Time.unscaledTime);
    }
    private void TakeDamage(On.HeroController.orig_TakeDamage orig, HeroController self, GameObject source,
        GlobalEnums.CollisionSide side, int damage, int hazard) {
        if (emergencyBenchActive) return;
        if (Connected && (Down || Resetting || Returning)) return;
        if (EntryProtected && hazard == 1) return;
        takingDamage = true; nativeHitPlayed = false; damageHazard = hazard;
        try { orig(self, source, side, damage, hazard); }
        finally { takingDamage = false; damageHazard = 0; }
    }
    private void TrackHitSound(On.HeroAudioController.orig_PlaySound orig, HeroAudioController self, GlobalEnums.HeroSounds sound) {
        if (takingDamage && (int)sound == 8) nativeHitPlayed = true;
        orig(self, sound);
    }
    private int AfterTakeDamage(int hazard, int damage) => EntryProtected ? 0 : damage;

    private void Lock(bool preserveNativeControl = false) {
        var hero = HeroController.instance;
        if (hero == null || lockedHero == hero) return;
        lockedHero = hero; var body = hero.GetComponent<Rigidbody2D>();
        simulated = body.simulated; noDamage = hero.takeNoDamage; heroBoxInactive = HeroBox.inactive;
        // A bench or dream wake can still own control after hero-in-position.
        // Releasing the party barrier must not interrupt its animation or input.
        restoreControl = !preserveNativeControl || !hero.controlReqlinquished;
        restoreAnimation = !preserveNativeControl || hero.GetComponent<HeroAnimationController>().controlEnabled;
        hero.RelinquishControl(); hero.takeNoDamage = true; body.velocity = Vector2.zero; hero.AffectedByGravity(false); HeroBox.inactive = true;
        body.simulated = false;
    }
    private void Unlock() {
        if (lockedHero != null) {
            lockedHero.takeNoDamage = noDamage;
            HeroBox.inactive = heroBoxInactive;
            if (restoreControl) lockedHero.RegainControl();
            if (restoreAnimation) lockedHero.StartAnimationControl();
            var body = lockedHero.GetComponent<Rigidbody2D>();
            body.simulated = simulated; body.velocity = Vector2.zero;
        }
        lockedHero = null;
        if (deathCamera != null && UnityEngine.SceneManagement.SceneManager.GetActiveScene().name == deathScene)
            deathCamera.SetMode(cameraMode);
        deathCamera = null;
    }
    private void Heal(int amount) {
        if (PlayerData.instance == null) return;
        PlayerData.instance.SetInt("health", Math.Min(PlayerData.instance.CurrentMaxHealth, amount));
        PlayMakerFSM.BroadcastEvent("HERO HEALED");
    }

    private void CameraUpdate(On.CameraController.orig_LateUpdate orig, CameraController self) {
        if (Down && self == deathCamera) self.SetMode(CameraController.CameraMode.FROZEN);
        orig(self);
        if (Down && self == deathCamera) {
            if (Spectating) {
                var position = self.KeepWithinSceneBounds(SpectatorPosition());
                position.z = cameraPosition.z;
                self.transform.position = position;
            } else self.transform.position = cameraPosition;
        }
    }
    private void LateUpdate() {
        if (Down && lockedHero != null) lockedHero.transform.position = Spectating ? SpectatorPosition() : DeathPosition;
    }
    internal GameObject PlayerObject(ushort id) => api.ClientManager.GetPlayer(id)?.PlayerObject;
    internal bool CanDreamNail(ushort id) {
        if (id == LocalId || !Connected || !Playable || Down || Resetting || Returning ||
            changingScene || GameManager.instance.isPaused || !PlayerData.instance.GetBool("hasDreamNail") ||
            HeroController.instance.cState.dead || HeroController.instance.cState.hazardDeath || HeroController.instance.cState.hazardRespawning) return false;
        var target = PlayerObject(id);
        return target != null && target.activeInHierarchy && Peers.TryGetValue(id, out var peer) &&
            peer.Life == Life.Alive && peer.State != null && peer.State.IsReady &&
            peer.State.Scene == CoopContext.LocalScene && Now - peer.Seen < 2;
    }
    internal bool RequestShadeHit(ushort target, uint serial) {
        if (!Connected || !Playable || Down || Resetting || Returning || changingScene || !RescueEnabled ||
            !Peers.TryGetValue(target, out var peer) || peer.Life != Life.Down || peer.DownSerial != serial ||
            peer.State == null || peer.State.Scene != CoopContext.LocalScene || Now - peer.Seen >= 2) return false;
        SendPresence();
        Send(new Message { Kind = Kind.ShadeHit, Target = target, Serial = serial, Scene = CoopContext.LocalScene });
        return true;
    }

    private bool OnSetBool(string key, bool value) {
        if (LinkedHealthRuleEnabled && LinkedBannedCharmKey(key)) {
            if (value) LinkedCharmDenied(); else ForgetLinkedCharm(key);
            return false;
        }
        return Connected && ProgressCatalog.CanShare(key, Shared, ShareCharms) && granted.Get(key) == 1 || value;
    }
    private int OnSetInt(string key, int value) {
        var linked = LinkedHealthWrite(key, value);
        if (linked.HasValue) value = linked.Value;
        if (Shared && Connected && Playable && key == "guardiansDefeated") {
            var pd = PlayerData.instance;
            // Native personal absorption increments after setting its defeat
            // flag. A previously shared Dreamer must not count a second time.
            return Math.Max(pd.GetInt("guardiansDefeated"), GuardianCount(pd));
        }
        return Shared && Connected && ProgressCatalog.Limits.TryGetValue(key, out var limit)
            ? Math.Min(limit, ProgressCatalog.Combine(key, value, granted.Get(key))) : value;
    }
    private static int GuardianCount(PlayerData pd) => (pd.GetBool("lurienDefeated") ? 1 : 0) +
        (pd.GetBool("hegemolDefeated") ? 1 : 0) + (pd.GetBool("monomonDefeated") ? 1 : 0);
    private static void RefreshGuardianCount() {
        var pd = PlayerData.instance;
        var count = GuardianCount(pd);
        if (pd.GetInt("guardiansDefeated") < count) pd.SetInt("guardiansDefeated", count);
    }
    private void CheckProgress() {
        if (resourceImported) foreach (var source in CoopMod.Statistics.Resources.Claimed)
            if (offeredResources.Add(source)) Send(new Message { Kind = Kind.ResourceOffer, Key = source });
        foreach (var pair in fields) {
            if (!ProgressCatalog.CanShare(pair.Key, Shared, ShareCharms)) continue;
            var obj = pair.Value.GetValue(PlayerData.instance);
            var value = obj is bool flag ? (flag ? 1 : 0) : (int)obj;
            // Reapply grants received before the local save/hero finished loading.
            var merged = ProgressCatalog.Combine(pair.Key, value, granted.Get(pair.Key));
            if (value != merged) { ApplyProgress(pair.Key, merged); value = merged; }
            if (value <= 0 || offered.TryGetValue(pair.Key, out var old) && value <= old) continue;
            offered[pair.Key] = value;
            Send(new Message { Kind = Kind.ProgressOffer, Key = pair.Key, Value = value });
        }
        RefreshRoyalCharmCost();
        RefreshStagNest();
        RefreshGuardianCount();
        RefreshGrimmFlames();
        // Old grants may already have set Completed without the matching
        // registration. Repair unchanged saves as well as new resource grants.
        ResourceShareService.RepairColosseumRegistrations(PlayerData.instance);
    }
    private void ApplyProgress(string key, int value) {
        if (GrimmFlames.IsKey(key)) { RefreshGrimmFlames(); return; }
        if (!Playable || !ProgressCatalog.CanShare(key, Shared, ShareCharms) || !fields.TryGetValue(key, out var field)) return;
        if (ProgressCatalog.IsPickup(key) && !resources.Idle) return;
        progressPickups.Prepare();
        var pd = PlayerData.instance;
        var current = field.GetValue(pd);
        if (field.FieldType == typeof(bool)) {
            if ((bool)current) return;
            pd.SetBool(key, value > 0);
            if (ProgressCatalog.IsMapPurchase(key) && key != "hasQuill") pd.SetBool("hasMap", true);
            if (key == "hasDash") pd.SetBool("canDash", true);
            if (key == "hasWalljump") pd.SetBool("canWallJump", true);
            if (key == "hasSuperDash") pd.SetBool("canSuperDash", true);
            if (key.StartsWith("gotCharm_", StringComparison.Ordinal)) {
                pd.SetBool("hasCharm", true);
                var count = ProgressCatalog.CharmCount(pd.GetBool, pd.GetInt("royalCharmState"));
                pd.SetInt("charmsOwned", Math.Max(pd.GetInt("charmsOwned"), count));
            }
        } else if ((int)current != ProgressCatalog.Combine(key, (int)current, value)) {
            pd.SetInt(key, ProgressCatalog.Combine(key, (int)current, value));
            if (key == "royalCharmState") pd.SetInt("charmsOwned", Math.Max(pd.GetInt("charmsOwned"),
                ProgressCatalog.CharmCount(pd.GetBool, pd.GetInt("royalCharmState"))));
        }
        if (key == "royalCharmState") RefreshRoyalCharmCost();
        if (key is "lurienDefeated" or "hegemolDefeated" or "monomonDefeated") RefreshGuardianCount();
        if (ProgressCatalog.IsMapPurchase(key)) {
            GameManager.instance.gameMap?.GetComponent<GameMap>()?.SetupMap();
        }
        sceneSaveChanges.ApplyPlayerDataSaveChange(key);
        DreamVictoryProgress.Refresh(key);
    }

    private void RefreshRoyalCharmCost() {
        var pd = PlayerData.instance;
        if (!ShareCharms || pd.GetInt("royalCharmState") != 4 || pd.GetInt("charmCost_36") == 0) return;
        // Native Void Heart acquisition also clears Kingsoul's notch cost.
        // A ledger grant skips that cutscene. Reconcile unchanged state too,
        // so saves affected by the old sync recover on the next progress tick.
        // Equipment remains personal, including native Pantheon bindings.
        pd.SetInt("charmCost_36", 0);
        pd.CalculateNotchesUsed();
        GameManager.instance.RefreshOvercharm();
        PlayMakerFSM.BroadcastEvent("CHARM EQUIP CHECK");
        PlayMakerFSM.BroadcastEvent("CHARM INDICATOR CHECK");
    }

    private static string ProgressName(string key) {
        if (GrimmFlames.IsKey(key)) return "格林火焰";
        switch (key) {
            case "hasDash": return "冲刺";
            case "hasWalljump": return "螳螂爪";
            case "hasSuperDash": return "水晶冲刺";
            case "hasShadowDash": return "暗影冲刺";
            case "hasAcidArmour": return "伊思玛的眼泪";
            case "hasDoubleJump": return "帝王之翼";
            case "hasDreamNail": return "梦之钉";
            case "hasDreamGate": return "梦之门";
            case "hasLantern": return "光蝇之灯";
            case "hasTramPass": return "电车通行证";
            case "hasKingsBrand": return "国王印记";
            case "falseKnightDreamDefeated": return "失败冠军";
            case "infectedKnightDreamDefeated": return "失落近亲";
            case "mageLordDreamDefeated": return "灵魂暴君";
            default: return key;
        }
    }

    private void ShowSharedResource(ResourceSource source) {
        resourceNotifications.Enqueue(source);
    }

    private IEnumerator ReloadCheckpoint(string session, uint round) {
        changingScene = true; Unlock();
        var death = personalDeath;
        if (death != null) {
            retryScene = death.Destination;
            death.Start();
        } else {
            // A late joiner did not die. Use their own checkpoint without
            // charging geo or synthesizing another player's death.
            while (Connected && Session == session && !Playable) yield return null;
            if (!Connected || Session != session) yield break;
            retryScene = PlayerData.instance.GetString("respawnScene");
            GameManager.instance.ReadyForRespawn(false);
        }
        var deadline = Time.realtimeSinceStartup + 30; var warned = false;
        yield return null;
        while (Connected && Session == session && Resetting && CoopContext.Epoch == round &&
            (death != null ? !death.Arrived : UnitySceneManager.GetActiveScene().name != retryScene || !Playable)) {
            if (!warned && Time.realtimeSinceStartup >= deadline) { warned = true; Notice("重试加载尚未完成，请检查游戏日志"); }
            yield return null;
        }
        checkpointReload = null;
        if (!Connected || Session != session || !Resetting || CoopContext.Epoch != round) yield break;
        changingScene = false;
        retryScene = UnitySceneManager.GetActiveScene().name;
        Lock(true);
    }

    private void CancelTransitions() {
        if (checkpointReload != null) StopCoroutine(checkpointReload);
        if (teammateWarp != null) StopCoroutine(teammateWarp);
        checkpointReload = teammateWarp = null; changingScene = false;
        // BeginSceneTransition is native work which cannot be cancelled by
        // stopping our coroutine. Its accepted arrival must still fade in
        // after disconnect; only post-arrival teammate relocation is cancelled.
        if (GameManager.instance == null || !GameManager.instance.IsInSceneTransition) { warpEntry = null; warpScene = null; }
        DebugEvent("transitions-cancelled", DebugEventSeverity.Warning);
    }

    private void CancelBenchReturn() {
        if (benchReturn != null) StopCoroutine(benchReturn);
        benchReturn = null; Returning = false; giveUpRequested = false;
    }
    private IEnumerator ReturnToBench(Message m) {
        var death = personalDeath;
        if (death == null || death.Destination != m.Scene) {
            // An invalid acknowledgement must not strand the client locked in
            // Returning or redirect them to another player's checkpoint.
            api.ClientManager.Disconnect(); death?.Start();
            Notice("个人死亡上下文不匹配，已断开联机并保留本机死亡流程");
            benchReturn = null; yield break;
        }
        changingScene = true; Unlock();
        // Formal death belongs only to this client. The surviving room's
        // authority/cache and party round remain intact.
        death.Start();
        var deadline = Time.realtimeSinceStartup + 30; var warned = false;
        yield return null;
        while (Connected && Returning && !death.Arrived) {
            if (!warned && Time.realtimeSinceStartup >= deadline) { warned = true; Notice("回椅加载尚未完成，请检查游戏日志"); }
            yield return null;
        }
        if (Connected && Returning && !Resetting) {
            changingScene = false; Lock(true);
            var pos = HeroController.instance.transform.position;
            Send(new Message { Kind = Kind.BenchReady, Serial = downSerial, Aux = sequence,
                Scene = UnitySceneManager.GetActiveScene().name, X = pos.x, Y = pos.y });
        }
        benchReturn = null;
    }

    private static bool TryWarpLanding(Vector2 target, string scene, out Vector2 landing) {
        var hero = HeroController.instance;
        if (hero == null) { landing = default; return false; }
        if (IsRadianceScene(scene)) {
            // Radiance has moving platforms and an abyss whose colliders are
            // not represented by the ordinary terrain raycast. Never place a
            // player on an arbitrary prop or inside the abyss during a warp.
            if (TryRadiancePlatformLanding(target, scene, hero, out landing)) return true;
            // Before the platform phase the arena uses ordinary solid terrain.
            // Keep the same supported, nearby terrain fallback used elsewhere.
        }
        Physics2D.SyncTransforms();
        if (!TryWarpBody(hero, out var size, out var centerOffset)) { landing = default; return false; }
        // Peers do not collide with each other. Prefer the supported position
        // they already occupy, especially on a platform narrower than 1.5 units.
        foreach (var dx in WarpSideOffsets(target, size, centerOffset, hero, scene)) {
            var origin = new Vector2(target.x + dx + centerOffset.x, target.y + centerOffset.y + size.y * .5f + .1f);
            foreach (var hit in Physics2D.RaycastAll(origin, Vector2.down, size.y + 4, 1 << 8)) {
                if (!WarpRoomScene(hit.collider.gameObject.scene.name, scene) || hit.collider.isTrigger || hit.distance == 0 || hit.normal.y < .5f) continue;
                var damage = hit.collider.GetComponent<DamageHero>();
                if (damage != null && damage.enabled && damage.damageDealt > 0) continue;
                var pos = new Vector2(target.x + dx, hit.point.y + size.y * .5f - centerOffset.y + .05f);
                if (Math.Abs(pos.y - target.y) > 3) continue;
                if (WarpPlacementBlocked(pos, size, centerOffset, hero, scene)) continue;
                landing = pos; return true;
            }
        }
        landing = default; return false;
    }

    private static bool IsRadianceScene(string scene) => !string.IsNullOrEmpty(scene) &&
        (scene == "Dream_Final_Boss" || scene.IndexOf("Radiance", StringComparison.OrdinalIgnoreCase) >= 0);

    // Native additive boss gates and platforms share their parent's warp space.
    private static bool WarpRoomScene(string candidate, string room) => candidate == room ||
        candidate.StartsWith(room + "_boss", StringComparison.Ordinal);

    private static bool IsRadiancePlatform(Collider2D collider, string scene) {
        if (collider == null || !collider.enabled || !collider.gameObject.activeInHierarchy ||
            collider.isTrigger || !WarpRoomScene(collider.gameObject.scene.name, scene)) return false;
        // The platform is named "Radiant Plat" in the normal and Godhome
        // scenes. Check parents as the solid collider is usually a child.
        for (var current = collider.transform; current != null; current = current.parent) {
            var name = current.gameObject.name;
            if (name.IndexOf("Radiant Plat", StringComparison.OrdinalIgnoreCase) >= 0 ||
                name.IndexOf("Radiance Platform", StringComparison.OrdinalIgnoreCase) >= 0) return true;
        }
        return false;
    }

    private static bool TryRadiancePlatformLanding(Vector2 target, string scene, HeroController hero, out Vector2 landing) {
        landing = default;
        Physics2D.SyncTransforms();
        if (!TryWarpBody(hero, out var size, out var centerOffset)) return false;
        Collider2D best = null;
        Vector2 bestPosition = default;
        var bestScore = float.PositiveInfinity;
        foreach (var collider in UnityEngine.Object.FindObjectsOfType<Collider2D>()) {
            if (!IsRadiancePlatform(collider, scene)) continue;
            var platform = collider.bounds;
            var halfWidth = Math.Max(.05f, size.x * .5f);
            var minX = platform.min.x + halfWidth + .03f;
            var maxX = platform.max.x - halfWidth - .03f;
            if (maxX < minX) { minX = maxX = platform.center.x; }
            var x = Mathf.Clamp(target.x + centerOffset.x, minX, maxX) - centerOffset.x;
            var pos = new Vector2(x, platform.max.y + size.y * .5f - centerOffset.y + .06f);
            if (WarpPlacementBlocked(pos, size, centerOffset, hero, scene, collider)) continue;
            var score = (pos - target).sqrMagnitude;
            if (score >= bestScore) continue;
            best = collider; bestPosition = pos; bestScore = score;
        }
        if (best == null) return false;
        landing = bestPosition;
        return true;
    }

    private static bool IsWarpBlocked(Collider2D collider, HeroController hero, string scene) {
        if (collider == null || !WarpRoomScene(collider.gameObject.scene.name, scene)) return false;
        if (hero != null && (collider.gameObject == hero.gameObject || collider.transform.IsChildOf(hero.transform))) return false;
        var damage = collider.GetComponent<DamageHero>();
        if (damage != null && damage.enabled && damage.damageDealt > 0) return true;
        // A fallback beside a native entrance must clear its exit trigger;
        // otherwise arrival immediately starts another room transition.
        var exit = collider.GetComponentInParent<TransitionPoint>();
        if (exit != null && exit.isActiveAndEnabled && !string.IsNullOrEmpty(exit.targetScene)) return true;
        if (collider.isTrigger) return false;
        // A target can be above a non-terrain collider (dream platform,
        // local gate, boss prop, or a detached scene piece). The old check
        // only rejected layer 8, allowing the hero to be placed inside such
        // geometry. Use the game's actual layer collision matrix so remote
        // player bodies and ignored decorative layers remain harmless.
        return !Physics2D.GetIgnoreLayerCollision(hero.gameObject.layer, collider.gameObject.layer);
    }

    private Vector2? FindWarpEntry(On.GameManager.orig_FindEntryPoint orig, GameManager self, string name, UnityEngine.SceneManagement.Scene scene) =>
        warpEntry.HasValue && name == "dreamGate" && scene.name == warpScene ? warpEntry : orig(self, name, scene);

    private void CompleteWarpEntry(On.HeroController.orig_EnterSceneDreamGate orig, HeroController self) {
        var ownArrival = warpEntry.HasValue && UnitySceneManager.GetActiveScene().name == warpScene && GameManager.instance.entryGateName == "dreamGate";
        if (ownArrival) {
            // Preload entry-point queries can run before the new scene's physics
            // is ready. Resolve here, before native entry saves its hazard point.
            var found = TryWarpLanding(warpEntry.Value, warpScene, out var landing);
            if (!found) {
                var scene = UnitySceneManager.GetSceneByName(warpScene);
                foreach (var entrance in scene.GetRootGameObjects().SelectMany(g => g.GetComponentsInChildren<TransitionPoint>(true))) {
                    if (!entrance.isActiveAndEnabled || !TryWarpLanding((Vector2)entrance.transform.position + entrance.entryOffset, warpScene, out landing)) continue;
                    found = true; break;
                }
            }
            if (found) {
                self.SetHeroParent(null);
                self.transform.position = new Vector3(landing.x, landing.y, self.transform.position.z);
                Physics2D.SyncTransforms();
            }
        }
        orig(self);
        // Native dream-gate travel relies on the Dream Nail FSM to fade in
        // and release control. F8 does not run that FSM. Complete our own
        // scoped arrival while retaining native entry/hero-in-position events.
        if (!ownArrival) return;
        self.RegainControl(); self.StartAnimationControl();
        if (IsRadianceScene(warpScene)) {
            HeroBox.inactive = false;
            var body = self.GetComponent<Rigidbody2D>();
            if (body != null) { body.simulated = true; body.velocity = Vector2.zero; }
        }
        GameCameras.instance?.cameraController?.PositionToHero(false);
        GameManager.instance.FadeSceneIn();
        warpEntry = null; warpScene = null;
    }

    private IEnumerator WarpTo(Message m) {
        if (!CanStartWarp || string.IsNullOrEmpty(m.Scene) || SceneUtil.IsNonGameplayScene(m.Scene) ||
            !Party.Finite(m.X) || !Party.Finite(m.Y)) yield break;
        var session = Session;
        changingScene = true;
        DebugEvent("warp-start", DebugEventSeverity.Info);
        Action rollback = null;
        var nativeTransitionAccepted = false;
        var hadBossContextGuard = BossTravel.HasTransientContext;
        var crossRoom = UnitySceneManager.GetActiveScene().name != m.Scene;
        Action commitWarpContext = () => { if (!hadBossContextGuard || crossRoom && nativeTransitionAccepted) BossTravel.CommitTransientContext(); };
        try {
            try { rollback = BossTravel.SaveState(); }
            catch (Exception e) {
                DebugEvent("warp-snapshot-failed", DebugEventSeverity.Critical, e.GetType().Name);
                Notice("无法保存传送前的临时上下文，已取消传送");
                yield break;
            }
            var prepared = false;
            try { prepared = BossTravel.Prepare(m); }
            catch (Exception e) { UnityEngine.Debug.LogError("HollowConnect boss travel prepare: " + e); }
            if (!prepared) {
                rollback(); rollback = null; commitWarpContext(); Notice("无法恢复队友的神居上下文，请检查日志");
                DebugEvent("warp-context-rejected", DebugEventSeverity.Warning);
                changingScene = false; teammateWarp = null; yield break;
            }
            if (UnityEngine.SceneManagement.SceneManager.GetActiveScene().name != m.Scene) {
                warpEntry = new Vector2(m.X, m.Y);
                warpScene = m.Scene;
                try { GameManager.instance.BeginSceneTransition(new GameManager.SceneLoadInfo { SceneName = m.Scene,
                    EntryGateName = "dreamGate", EntryDelay = 0, Visualization = GameManager.SceneLoadVisualizations.Dream,
                    WaitForSceneTransitionCameraFade = true }); }
                catch (Exception e) {
                    rollback(); rollback = null; commitWarpContext(); DebugEvent("warp-native-transition-rejected", DebugEventSeverity.Critical, e.GetType().Name);
                    warpEntry = null; warpScene = null; changingScene = false; teammateWarp = null;
                    Notice("传送加载被拒绝，已恢复原上下文"); UnityEngine.Debug.LogError(e); yield break;
                }
                nativeTransitionAccepted = true;
                var deadline = Time.realtimeSinceStartup + 30;
                var entered = false;
                yield return null;
                while (Connected && Session == session && m.Round == CoopContext.Epoch &&
                    (UnityEngine.SceneManagement.SceneManager.GetActiveScene().name != m.Scene || !Playable ||
                     api.ClientManager is Hkmp.Game.Client.ClientManager manager && !manager.SceneSnapshotReady)) {
                    if (Time.realtimeSinceStartup >= deadline) {
                        Notice("传送等待已结束；场景加载完成后可再次传送");
                        DebugEvent("warp-timeout", DebugEventSeverity.Warning);
                        yield break;
                    }
                    entered |= UnitySceneManager.GetActiveScene().name == m.Scene;
                    // Native travel may advance through another scene. Never wait
                    // forever for an intermediate cutscene that has already left.
                    if (entered && !GameManager.instance.IsInSceneTransition && Playable &&
                        UnitySceneManager.GetActiveScene().name != m.Scene) yield break;
                    yield return null;
                }
            }
            if (Connected && Session == session && m.Round == CoopContext.Epoch && Playable &&
                UnityEngine.SceneManagement.SceneManager.GetActiveScene().name == m.Scene && !Down && !Resetting && !Returning) {
                var target = Peers.TryGetValue(m.Target, out var peer) && peer.State != null && peer.State.Scene == m.Scene && Now - peer.Seen < 1.5
                    ? new Vector2(peer.State.X, peer.State.Y) : new Vector2(m.X, m.Y);
                var found = TryWarpLanding(target, m.Scene, out var pos);
                if (found) {
                    if (crossRoom && IsRadianceScene(m.Scene)) FinishRadianceWarpPresentation(m.Scene);
                    var hero = HeroController.instance;
                    hero.SetHeroParent(null);
                    hero.GetComponent<Rigidbody2D>().velocity = Vector2.zero;
                    hero.transform.position = new Vector3(pos.x, pos.y, hero.transform.position.z);
                    Physics2D.SyncTransforms();
                    hero.SetHazardRespawn(hero.transform.position, hero.cState.facingRight); hero.ResetAirMoves();
                    if (IsRadianceScene(m.Scene)) RestoreRadianceWarpCamera(m.Scene);
                    else GameCameras.instance?.cameraController?.PositionToHero(false);
                    nativeTransitionAccepted = true;
                }
                DebugEvent(found ? "warp-complete" : "warp-no-safe-landing", found ? DebugEventSeverity.Info : DebugEventSeverity.Warning);
                Notice(found ? "已到达队友身边" : "队友附近没有安全落点");
            }
        } finally {
            // Once Unity accepted the native load, its destination context is
            // needed by the scene even if the networking coroutine is stopped.
            // Before acceptance, always restore every temporary BossTravel
            // field so a failed request cannot poison a later save/scene load.
            if (!nativeTransitionAccepted && rollback != null) {
                try { rollback(); DebugEvent("warp-context-rolled-back", DebugEventSeverity.Info); }
                catch (Exception e) { DebugEvent("warp-rollback-failed", DebugEventSeverity.Critical, e.GetType().Name); UnityEngine.Debug.LogError(e); }
            }
            commitWarpContext();
            changingScene = false; teammateWarp = null;
            if (GameManager.instance == null || !GameManager.instance.IsInSceneTransition) {
                warpEntry = null; warpScene = null;
            }
        }
    }

    internal void Notice(string message) { status = message; statusUntil = Time.unscaledTime + 5; }
    private void OnGUI() {
        var emergencyNotice = emergencyStatusVisible && Time.unscaledTime < statusUntil;
        if (!Connected && !debugPanel && !emergencyNotice) return;
        if (!debugPanel && !(Connected && Input.GetKey(CoopMod.Settings.DeathLeaderboardKey)) && Event.current.type != EventType.Repaint) return;
        var style = overlayStyle ??= new GUIStyle(GUI.skin.label) { font = font, fontSize = 16, alignment = TextAnchor.MiddleCenter };
        style.normal.textColor = Color.white;
        if ((Connected || emergencyNotice) && Time.unscaledTime < statusUntil) GUI.Label(new Rect(0, 70, Screen.width, 36), status, style);
        if (Connected) {
            if (LinkedHealthActive && Playable) GUI.Label(new Rect(0, 12, Screen.width, 32),
                "链在一起 · 团队生命 " + PlayerData.instance.GetInt("health") + " / " + linkedHealthMax, style);
            if (Down || Resetting || Returning) GUI.Label(new Rect(0, Screen.height * .7f, Screen.width, 70),
                Resetting ? "全队失败 · 等待所有队友返回各自复活点" : Returning ? "正在返回自己的复活点" :
                LinkedHealthRuleEnabled ? "团队生命耗尽 · 等待全队返回各自复活点" :
                Spectating ? "正在观战 · 等待本场结束\n按 " + CoopMod.Settings.GiveUpKey + " 返回自己的复活点" :
                "暗影形态 · 等待队友击破暗影，一血复活\n按 " + CoopMod.Settings.GiveUpKey +
                (SpectateAfterExhaustion ? " 放弃救援并转入观战" : " 放弃救援并返回自己的复活点"), style);
            if (RescueLimit > 0 && Now < rescueCountUntil) GUI.Label(new Rect(0, Screen.height - 42, Screen.width, 32), RescueRemainingText, style);
            map.DrawOverlay(style);
        }
        if (Connected && Input.GetKey(CoopMod.Settings.DeathLeaderboardKey)) {
            var width = Mathf.Min(350, Screen.width - 20);
            var height = Mathf.Min(430, Screen.height - 110);
            GUILayout.BeginArea(new Rect(Screen.width - width - 10, 100, width, height), GUI.skin.box);
            GUILayout.Label("本存档累计死亡榜 · 倒地计一次", style);
            deathScroll = GUILayout.BeginScrollView(deathScroll);
            var rank = 0;
            foreach (var entry in CoopMod.Statistics.Deaths.OrderByDescending(s => s.Value).ThenBy(s => s.Key, StringComparer.Ordinal))
                GUILayout.Label(++rank + ". " + entry.Key + (entry.Key == localName ? "（你）" : "") + "   " + entry.Value + " 次", style);
            GUILayout.EndScrollView(); GUILayout.EndArea();
        }
        DrawDebugPanel(style);
    }

    private void DrawDebugPanel(GUIStyle style) {
        if (!debugPanel) return;
        var snapshot = sampledDebugState ?? BuildDebugSnapshot();
        var width = Mathf.Min(560, Screen.width - 20);
        var height = Mathf.Min(680, Screen.height - 20);
        GUILayout.BeginArea(new Rect(10, 10, width, height), GUI.skin.box);
        GUILayout.Label("HollowConnect 诊断（只读；F9 开关）", style);
        debugScroll = GUILayout.BeginScrollView(debugScroll, GUILayout.Height(height - 115));
        GUILayout.Label("场景 " + snapshot.SceneName + " / GM " + snapshot.GameManagerScene + " / 入口 " + snapshot.EntryGateName, style);
        GUILayout.Label("转换 " + snapshot.IsInSceneTransition + " · Finished " + snapshot.HasFinishedEnteringScene + " · Hero位置 " + snapshot.HeroInPosition, style);
        GUILayout.Label("输入 " + snapshot.HeroAcceptingInput + " · 控制锁 " + snapshot.HeroControlRelinquished + " · HeroBox禁用 " + snapshot.HeroBoxInactive + " · 刚体 " + snapshot.BodySimulated, style);
        GUILayout.Label("流程 changing=" + snapshot.ChangingScene + " down=" + snapshot.Down + " reset=" + snapshot.Resetting + " returning=" + snapshot.Returning, style);
        GUILayout.Label("warp=" + snapshot.WarpScene + " entry=" + snapshot.WarpEntry + " retry=" + snapshot.RetryScene, style);
        GUILayout.Label("复活点=" + snapshot.RespawnScene + " / " + snapshot.RespawnMarkerName + " type=" + snapshot.RespawnType, style);
        GUILayout.Label("Boss sequence=" + snapshot.BossSequenceName + " in=" + snapshot.InBossSequence + " guard=" + snapshot.HasBossContextGuard + " gate=" + snapshot.BossReturnEntryGate, style);
        GUILayout.Label("回执=" + snapshot.ResourceReceiptCount + " 共享进度=" + snapshot.SharedProgressCount + " peers=" + snapshot.PeerCount, style);
        var risks = snapshot.RiskCodes();
        GUILayout.Label(risks.Count == 0 ? "风险：未发现当前临时锁异常" : "风险：" + string.Join(", ", risks), style);
        GUILayout.Label("事件（最近 " + debugEvents.Count + "/" + debugEvents.Capacity + "）", style);
        foreach (var e in debugEvents.Snapshot().Reverse().Take(20))
            GUILayout.Label(e.Sequence + " " + e.Severity + " " + e.Code + (e.Detail.Length == 0 ? "" : " · " + e.Detail), style);
        GUILayout.EndScrollView();
        GUILayout.BeginHorizontal();
        if (GUILayout.Button("导出诊断")) ExportDebugReport();
        if (!debugRecoveryArmed) {
            if (GUILayout.Button("准备恢复临时状态")) debugRecoveryArmed = true;
        } else {
            if (GUILayout.Button("确认恢复（不改存档）")) { RecoverTransientState(); debugRecoveryArmed = false; }
        }
        if (GUILayout.Button("关闭")) { debugPanel = false; debugRecoveryArmed = false; }
        GUILayout.EndHorizontal();
        if (debugExportPath.Length != 0) GUILayout.Label("最近导出：" + debugExportPath, style);
        GUILayout.EndArea();
    }

    private void OnDestroy() {
        ResetLinkedHealth();
        On.GameManager.BeginSceneTransitionRoutine -= TrackNativeTransition;
        GameManager.SceneTransitionBegan -= TrackNativeLoader;
        teammateDream?.Dispose();
        BossTravel.Unregister();
        CoopContext.CaptureBossContext = null; CoopContext.ApplyBossContext = null; CoopContext.CommitBossContext = null; CoopContext.RestoreBossContext = null; CoopContext.BossTransitionReady = null; CoopContext.AwaitBossSettlement = null; CoopContext.ApplyBossEntityComplete = null;
        CoopContext.LocalRescueDown = null;
        CoopContext.LivingBossTarget = null;
        CoopContext.FlexibleBossTargetingOn = null;
        CoopContext.BossTargetSession = null;
        CoopContext.ChooseBossTarget = null;
        CoopContext.BossTargetObject = null;
        CoopContext.AttackSourcePlayer = null;
        CoopMod.ActiveRoomSettingLabel = null;
        CoopMod.RoomRuleRevision++;
        CoopContext.RemotePlayerOpacity = null; CoopContext.PlayerNameColor = null; CoopContext.BossHealthMultiplier = null;
        CoopContext.BossDefeated = null;
        CoopContext.BossEncounter = null; CoopContext.BossDamage = null;
        UnregisterDamageRanking();
        CoopContext.FinishRadianceIntro = null;
        StopSpectating();
        RestoreSpectatorPeers();
        CancelBenchReturn();
        CancelTransitions();
        shades?.Clear(); Unlock(); map?.Dispose();
        resources?.Dispose();
        resourceNotifications.Clear();
        progressPickups?.Dispose();
        mapPurchases?.Dispose();
        On.HeroController.Die -= OnDie; On.CameraController.LateUpdate -= CameraUpdate; On.GameManager.FindEntryPoint -= FindWarpEntry;
        On.HeroController.TakeDamage -= TakeDamage; ModHooks.AfterTakeDamageHook -= AfterTakeDamage;
        On.HeroAudioController.PlaySound -= TrackHitSound;
        On.HeroController.EnterSceneDreamGate -= CompleteWarpEntry;
        ModHooks.SetPlayerBoolHook -= OnSetBool; ModHooks.SetPlayerIntHook -= OnSetInt;
        if (api != null) { api.ClientManager.ConnectEvent -= OnConnect; api.ClientManager.DisconnectEvent -= OnDisconnect; }
        if (api != null && debugCommand != null) {
            try { api.CommandManager.DeregisterCommand(debugCommand); } catch { }
        }
    }
}
