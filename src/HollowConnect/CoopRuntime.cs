using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Hkmp;
using Hkmp.Api.Client;
using Hkmp.Api.Client.Networking;
using Hkmp.Game.Client.Save;
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

public sealed class CoopRuntime : MonoBehaviour {
    private IClientApi api;
    private IClientAddonNetworkSender<Channel> sender;
    internal readonly Dictionary<ushort, Peer> Peers = new Dictionary<ushort, Peer>();
    private ProgressLedger granted = new ProgressLedger();
    private readonly Dictionary<string, int> offered = new Dictionary<string, int>();
    private readonly Dictionary<string, FieldInfo> fields = new Dictionary<string, FieldInfo>();
    private readonly SaveChanges sceneSaveChanges = new SaveChanges();
    private MapService map;
    private ShadeService shades;
    private PersonalDeath personalDeath;
    private readonly EntryProtectionWindow entryProtection = new EntryProtectionWindow();
    private int protectionSceneHandle = int.MinValue;
    private string protectionSession = "";
    private bool protectEntry;
    private bool takingDamage, nativeHitPlayed;
    private int damageHazard;
    internal Func<KeyCode, bool> KeyPressed = Input.GetKeyDown;
    internal string Session = "";
    internal ushort LocalId, Selected;
    internal uint ReleasedRound;
    internal bool Shared, RescueEnabled, WarpEnabled, Down, Resetting, Returning;
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
    internal static double Now => (DateTime.UtcNow - new DateTime(2020, 1, 1)).TotalSeconds;
    internal bool Connected => api != null && api.NetClient.IsConnected && Session.Length > 0;
    internal bool Playable => HeroController.instance != null && GameManager.instance != null &&
        !GameManager.instance.IsInSceneTransition && HeroController.instance.isHeroInPosition;
    private bool CanStartWarp => Playable && !Down && !Resetting && !Returning && !changingScene &&
        HeroController.instance.acceptingInput && !HeroController.instance.controlReqlinquished &&
        !HeroController.instance.cState.dead && !HeroController.instance.cState.hazardDeath &&
        !HeroController.instance.cState.hazardRespawning && !HeroController.instance.cState.transitioning &&
        HeroController.instance.transitionState == GlobalEnums.HeroTransitionState.WAITING_TO_TRANSITION &&
        !GameManager.instance.isPaused && Time.timeScale > 0;

    public void Initialize(IClientApi clientApi, CoopClientAddon addon) {
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
        BossTravel.Register();
        CoopContext.CaptureBossContext = BossTravel.CaptureContext;
        CoopContext.ApplyBossContext = BossTravel.ApplyContext;
        CoopContext.BossTransitionReady = () => Playable && !Down && !Resetting && !Returning && !changingScene;
        CoopContext.LocalRescueDown = () => Connected && RescueEnabled && Down;
        map.Register();
        On.HeroController.Die += OnDie;
        On.HeroController.TakeDamage += TakeDamage;
        On.HeroAudioController.PlaySound += TrackHitSound;
        ModHooks.AfterTakeDamageHook += AfterTakeDamage;
        On.CameraController.LateUpdate += CameraUpdate;
        On.GameManager.FindEntryPoint += FindWarpEntry;
        On.HeroController.EnterSceneDreamGate += CompleteWarpEntry;
        ModHooks.SetPlayerBoolHook += OnSetBool;
        ModHooks.SetPlayerIntHook += OnSetInt;
        font = Font.CreateDynamicFontFromOSFont(new[] { "Microsoft YaHei", "Arial" }, 18);
    }

    private void OnConnect() {
        localName = "";
        Session = ""; Peers.Clear(); offered.Clear(); granted = new ProgressLedger(); sequence = 0; handledWarp = 0; ReleasedRound = 0;
        entryProtection.Clear(); protectionSceneHandle = int.MinValue; protectionSession = "";
        Send(new Message { Kind = Kind.Hello });
    }
    private void OnDisconnect() {
        localName = "";
        CancelBenchReturn();
        CancelTransitions();
        shades.Clear(); Unlock(); Down = Resetting = changingScene = false; Session = ""; Peers.Clear(); offered.Clear();
        CoopContext.Resetting = false; readySent = false; retryScene = null; map.Clear();
        // The original hero owns an accepted formal death; disconnect only
        // abandons our wait and must not cancel its animation/save/return.
        personalDeath = null; entryProtection.Clear(); protectionSession = "";
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
            Notice("协作连接已建立" + (Shared ? " · 共享进度开启" : "") + " · 按住 " + CoopMod.Settings.DeathLeaderboardKey + " 查看死亡榜");
            nextPresence = nextProgress = 0;
            return;
        }
        if (m.Session != Session || Session.Length == 0) return;
        // Absolute saved totals merge by maximum; copied histories must not add twice.
        if (m.Kind == Kind.DeathCount) {
            CoopMod.Statistics.Merge(m.Name, m.Serial);
            return;
        }
        if (m.Kind == Kind.Wipe) {
            // Welcome may carry the current round to a late joiner who has
            // never released it. Completed rounds, however, cannot reopen.
            if (m.Round <= ReleasedRound || m.Round <= CoopContext.Epoch && Resetting || m.Round < CoopContext.Epoch) return;
            CoopContext.Epoch = m.Round; CoopContext.Resetting = true;
            CancelBenchReturn();
            CancelTransitions();
            shades.Clear();
            Resetting = true; Down = false; readySent = false; Peers.Clear();
            checkpointReload = StartCoroutine(ReloadCheckpoint(Session, m.Round)); return;
        }
        if (m.Round != CoopContext.Epoch) return;
        switch (m.Kind) {
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
            case Kind.Left:
                shades.Remove(m.Id); Peers.Remove(m.Id); map.Remove(m.Id); break;
            case Kind.Down:
                if (m.Id == LocalId) break;
                if (!Peers.TryGetValue(m.Id, out var downPeer)) Peers[m.Id] = downPeer = new Peer();
                // An unreliable roster can arrive before the reliable event.
                // Keep state ordering separate from one-shot presentation.
                if (m.Aux < downPeer.LifeVersion || m.Serial < downPeer.DownSerial ||
                    m.Aux == downPeer.LifeVersion && downPeer.Life != Life.Down) break;
                downPeer.DownSerial = m.Serial; downPeer.LifeVersion = m.Aux; downPeer.Life = Life.Down;
                if (downPeer.State == null) downPeer.State = new Message { Id = m.Id, Name = m.Name };
                downPeer.State.Value = (int)Life.Down; downPeer.State.Scene = m.Scene;
                downPeer.State.X = m.X; downPeer.State.Y = m.Y; downPeer.Seen = Now;
                if (m.Serial > downPeer.DownFeedbackSerial) {
                    downPeer.DownFeedbackSerial = m.Serial;
                    PlayDownFeedback(m, true);
                }
                break;
            case Kind.Revived:
                if (Resetting || m.Serial == 0 || m.Target == m.Id) break;
                if (m.Id == LocalId && Down && m.Serial == downSerial && !Resetting) {
                    // A moving/disappearing platform may have left since Down.
                    // Never release the knight into an unsupported rescue.
                    var rescued = HeroController.instance;
                    var landing = TryWarpLanding(DeathPosition, deathScene, out var supported)
                        ? new Vector3(supported.x, supported.y, DeathPosition.z) : FindRescuePosition(rescued);
                    if (!landing.HasValue) {
                        // The server has accepted the old episode. Advertise a
                        // fresh Down before using its existing personal-return
                        // protocol; retain the original native death context.
                        downSerial++;
                        SendDown(); RequestPersonalReturn();
                        break;
                    }
                    rescued.transform.position = landing.Value;
                    Physics2D.SyncTransforms();
                    giveUpRequested = false;
                    shades.Remove(m.Id); Down = false; Unlock();
                    personalDeath = null;
                    PlayerData.instance.SetInt("healthBlue", 0); Heal(1); Notice("队友击破了你的暗影，一血复活");
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
                    if (m.Target == LocalId && Connected && Playable && !Down && !Returning && !changingScene)
                        HeroController.instance.AddMPCharge(RescueFeedback.SoulReward);
                    PlayRescueFeedback(m);
                }
                break;
            case Kind.Returning:
                if (m.Id == LocalId && Down && m.Serial == downSerial && !Resetting && !Returning) {
                    giveUpRequested = false; Returning = true; Down = false; shades.Remove(LocalId);
                    benchReturn = StartCoroutine(ReturnToBench(m));
                }
                if (Peers.TryGetValue(m.Id, out var leaving) && m.Aux > leaving.LifeVersion) {
                    shades.Remove(m.Id); leaving.LifeVersion = m.Aux; leaving.Life = Life.Returning;
                    if (leaving.State != null) { leaving.State.Value = (int)Life.Returning; leaving.State.HasMap = leaving.State.IsReady = false; }
                }
                break;
            case Kind.Returned:
                if (m.Id == LocalId && Returning && m.Serial == downSerial && !Resetting) {
                    Returning = false; giveUpRequested = false; Unlock(); personalDeath = null; Notice("已回到自己的复活点，队友继续当前战斗");
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
                    if (Down) Notice("当前无法放弃：尚无可用长椅，或全队重试已开始");
                }
                break;
            case Kind.Release:
                if (!Resetting) break;
                ReleasedRound = m.Round;
                Resetting = false; CoopContext.Resetting = false; readySent = false; Unlock(); personalDeath = null;
                Notice("全队加载完成，重新出发"); break;
            case Kind.ProgressGrant:
                if (Shared && granted.Merge(m.Key, m.Value)) ApplyProgress(m.Key, m.Value);
                break;
            case Kind.Warp:
                if (m.Serial <= handledWarp || !CanStartWarp) break;
                handledWarp = m.Serial; teammateWarp = StartCoroutine(WarpTo(m)); break;
        }
    }

    private void PlayRescueFeedback(Message m) {
        if (!Playable || m.Scene != CoopContext.LocalScene) return;
        RescueFeedback.Play(new Vector3(m.X, m.Y, 0));
        if (m.Id != LocalId) RescueFlash.Play(PlayerObject(m.Id));
    }

    private void Update() {
        if (!Connected) return;
        RefreshEntryProtection();
        if (lockedHero != null && (Down || Resetting || Returning)) {
            lockedHero.RelinquishControl(); lockedHero.GetComponent<Rigidbody2D>().velocity = Vector2.zero;
            lockedHero.takeNoDamage = true; HeroBox.inactive = true;
            if (Down) lockedHero.transform.position = DeathPosition;
        }
        shades.Synchronize();
        if (Time.unscaledTime >= nextPresence) { nextPresence = Time.unscaledTime + .1f; SendPresence(); }
        if (Resetting && !readySent && !changingScene && Playable && UnityEngine.SceneManagement.SceneManager.GetActiveScene().name == retryScene) {
            Lock(true); readySent = true; Send(new Message { Kind = Kind.Ready, Scene = retryScene });
        }
        if (!Playable || Resetting || changingScene || Returning) return;
        if (Down && !giveUpRequested && KeyPressed(CoopMod.Settings.GiveUpKey)) {
            RequestPersonalReturn();
            Notice("正在放弃救援，按原版死亡规则返回自己的复活点…");
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

    private void SendPresence() {
        var hero = HeroController.instance;
        if (hero == null) return;
        var pos = hero.transform.position;
        var location = Vector3.zero;
        var hasMap = Playable && !changingScene && !Returning && map.TryLocation(out location);
        var pd = PlayerData.instance;
        var message = new Message { Kind = Kind.Presence, Serial = ++sequence, Scene = UnityEngine.SceneManagement.SceneManager.GetActiveScene().name,
            X = pos.x, Y = pos.y, HasMap = hasMap, MapX = hasMap ? location.x : 0, MapY = hasMap ? location.y : 0,
            IsReady = Playable && !changingScene && !Returning, Key = pd.GetString("respawnScene") ?? "",
            Name = pd.GetString("respawnMarkerName") ?? "", Value = pd.GetInt("respawnType") };
        BossTravel.Capture(message); Send(message);
    }

    private IEnumerator OnDie(On.HeroController.orig_Die orig, HeroController self) {
        if (Connected && !Down && !Resetting && !Returning)
            Send(new Message { Kind = Kind.DeathCount, Serial = CoopMod.Statistics.Increment(localName) });
        if (!Connected || !RescueEnabled || PlayerData.instance.permadeathMode != 0) { yield return orig(self); yield break; }
        if (Down || Resetting || Returning) yield break;
        personalDeath = new PersonalDeath(self, () => orig(self));
        deathScene = UnityEngine.SceneManagement.SceneManager.GetActiveScene().name;
        var landing = FindRescuePosition(self);
        DeathPosition = landing ?? self.transform.position;
        self.SetHeroParent(null);
        self.transform.position = DeathPosition;
        deathCamera = GameCameras.instance?.cameraController;
        if (deathCamera != null) {
            cameraMode = deathCamera.mode; cameraPosition = deathCamera.transform.position;
            deathCamera.FreezeInPlace();
        }
        Down = true; downSerial++; Lock(); Heal(1);
        shades.Synchronize();
        var message = SendDown();
        PlayDownFeedback(message, !takingDamage || !nativeHitPlayed);
        if (landing.HasValue) Notice("你已化为暗影；等待队友击破救援，或按 " + CoopMod.Settings.GiveUpKey + " 放弃并回自己的复活点");
        else RequestPersonalReturn();
    }

    private Message SendDown() {
        var message = new Message { Kind = Kind.Down, Serial = downSerial, Scene = deathScene, X = DeathPosition.x, Y = DeathPosition.y,
            Key = personalDeath.Destination, Name = personalDeath.Marker, Value = personalDeath.RespawnType };
        Send(message); return message;
    }
    private void RequestPersonalReturn() {
        giveUpRequested = true;
        Send(new Message { Kind = Kind.GiveUpRequest, Serial = downSerial,
            Scene = personalDeath?.Destination ?? "", Key = personalDeath?.Marker ?? "", Value = personalDeath?.RespawnType ?? 0 });
        Notice("正在按原版死亡规则返回自己的复活点…");
    }

    private Vector3? FindRescuePosition(HeroController hero) {
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
        if (Down && self == deathCamera) self.transform.position = cameraPosition;
    }
    private void LateUpdate() {
        if (Down && lockedHero != null) lockedHero.transform.position = DeathPosition;
    }
    internal GameObject PlayerObject(ushort id) => api.ClientManager.GetPlayer(id)?.PlayerObject;
    internal bool RequestShadeHit(ushort target, uint serial) {
        if (!Connected || !Playable || Down || Resetting || Returning || changingScene || !RescueEnabled ||
            !Peers.TryGetValue(target, out var peer) || peer.Life != Life.Down || peer.DownSerial != serial ||
            peer.State == null || peer.State.Scene != CoopContext.LocalScene || Now - peer.Seen >= 2) return false;
        SendPresence();
        Send(new Message { Kind = Kind.ShadeHit, Target = target, Serial = serial, Scene = CoopContext.LocalScene });
        return true;
    }

    private bool OnSetBool(string key, bool value) => Shared && Connected && granted.Get(key) == 1 || value;
    private int OnSetInt(string key, int value) => Shared && Connected && ProgressCatalog.Limits.TryGetValue(key, out var limit)
        ? Math.Min(limit, Math.Max(value, granted.Get(key))) : value;
    private void CheckProgress() {
        foreach (var pair in fields) {
            var obj = pair.Value.GetValue(PlayerData.instance);
            var value = obj is bool flag ? (flag ? 1 : 0) : (int)obj;
            // Reapply grants received before the local save/hero finished loading.
            if (value < granted.Get(pair.Key)) { ApplyProgress(pair.Key, granted.Get(pair.Key)); value = granted.Get(pair.Key); }
            if (value <= 0 || offered.TryGetValue(pair.Key, out var old) && value <= old) continue;
            offered[pair.Key] = value;
            Send(new Message { Kind = Kind.ProgressOffer, Key = pair.Key, Value = value });
        }
    }
    private void ApplyProgress(string key, int value) {
        if (!Playable || !fields.TryGetValue(key, out var field)) return;
        var pd = PlayerData.instance;
        var current = field.GetValue(pd);
        if (field.FieldType == typeof(bool)) {
            if ((bool)current) return;
            pd.SetBool(key, value > 0);
            if (key == "hasDash") pd.SetBool("canDash", true);
            if (key == "hasWalljump") pd.SetBool("canWallJump", true);
            if (key == "hasSuperDash") pd.SetBool("canSuperDash", true);
            if (key.StartsWith("gotCharm_", StringComparison.Ordinal)) {
                var count = Enumerable.Range(1, 40).Count(i => pd.GetBool("gotCharm_" + i));
                pd.SetInt("charmsOwned", Math.Max(pd.GetInt("charmsOwned"), count));
            }
        } else if ((int)current < value) {
            pd.SetInt(key, value);
            if (key == "nailSmithUpgrades") pd.SetInt("nailDamage", 5 + 4 * value);
        }
        sceneSaveChanges.ApplyPlayerDataSaveChange(key);
        Notice("共享进度：" + key);
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
        Physics2D.SyncTransforms();
        var bounds = hero.GetComponent<Collider2D>().bounds;
        var centerOffset = (Vector2)(bounds.center - hero.transform.position);
        var size = (Vector2)bounds.size;
        // Peers do not collide with each other. Prefer the supported position
        // they already occupy, especially on a platform narrower than 1.5 units.
        foreach (var dx in new[] { 0f, 1.5f, -1.5f, 3f, -3f }) {
            var origin = new Vector2(target.x + dx, target.y + bounds.extents.y);
            foreach (var hit in Physics2D.RaycastAll(origin, Vector2.down, size.y + 4, 1 << 8)) {
                if (hit.collider.gameObject.scene.name != scene || hit.collider.isTrigger || hit.distance == 0 || hit.normal.y < .5f) continue;
                var pos = new Vector2(origin.x, hit.point.y + bounds.extents.y - centerOffset.y + .05f);
                if (Math.Abs(pos.y - target.y) > 3) continue;
                var blocked = Physics2D.OverlapBoxAll(pos + centerOffset, size * .98f, 0).Any(c => {
                    if (c.gameObject.scene.name != scene) return false;
                    if (!c.isTrigger && c.gameObject.layer == 8) return true;
                    var damage = c.GetComponent<DamageHero>();
                    return damage != null && damage.enabled && damage.damageDealt > 0;
                });
                if (blocked) continue;
                landing = pos; return true;
            }
        }
        landing = default; return false;
    }

    private Vector2? FindWarpEntry(On.GameManager.orig_FindEntryPoint orig, GameManager self, string name, UnityEngine.SceneManagement.Scene scene) =>
        warpEntry.HasValue && name == "dreamGate" && scene.name == warpScene ? warpEntry : orig(self, name, scene);

    private void CompleteWarpEntry(On.HeroController.orig_EnterSceneDreamGate orig, HeroController self) {
        var ownArrival = warpEntry.HasValue && GameManager.instance.sceneName == warpScene && GameManager.instance.entryGateName == "dreamGate";
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
        GameCameras.instance.cameraController.PositionToHero(false);
        GameManager.instance.FadeSceneIn();
        warpEntry = null; warpScene = null;
    }

    private IEnumerator WarpTo(Message m) {
        if (!CanStartWarp || !Party.Finite(m.X) || !Party.Finite(m.Y)) yield break;
        var session = Session;
        changingScene = true;
        var rollback = BossTravel.SaveState();
        var prepared = false;
        try { prepared = BossTravel.Prepare(m); }
        catch (Exception e) { UnityEngine.Debug.LogError("HollowConnect boss travel prepare: " + e); }
        if (!prepared) {
            rollback(); Notice("无法恢复队友的神居上下文，请检查日志");
            changingScene = false; teammateWarp = null; yield break;
        }
        if (UnityEngine.SceneManagement.SceneManager.GetActiveScene().name != m.Scene) {
            warpEntry = new Vector2(m.X, m.Y);
            warpScene = m.Scene;
            try { GameManager.instance.BeginSceneTransition(new GameManager.SceneLoadInfo { SceneName = m.Scene,
                EntryGateName = "dreamGate", EntryDelay = 0, Visualization = GameManager.SceneLoadVisualizations.Dream,
                WaitForSceneTransitionCameraFade = true }); }
            catch (Exception e) {
                rollback(); warpEntry = null; warpScene = null; changingScene = false; teammateWarp = null;
                Notice("传送加载被拒绝，已恢复原上下文"); UnityEngine.Debug.LogError(e); yield break;
            }
            var deadline = Time.realtimeSinceStartup + 30;
            var warned = false;
            yield return null;
            while (Connected && Session == session && m.Round == CoopContext.Epoch &&
                (UnityEngine.SceneManagement.SceneManager.GetActiveScene().name != m.Scene || !Playable ||
                 api.ClientManager is Hkmp.Game.Client.ClientManager manager && !manager.SceneSnapshotReady)) {
                if (!warned && Time.realtimeSinceStartup >= deadline) { warned = true; Notice("传送加载尚未完成，请检查游戏日志"); }
                yield return null;
            }
        }
        if (Connected && Session == session && m.Round == CoopContext.Epoch && Playable &&
            UnityEngine.SceneManagement.SceneManager.GetActiveScene().name == m.Scene && !Down && !Resetting && !Returning) {
            var target = Peers.TryGetValue(m.Target, out var peer) && peer.State != null && peer.State.Scene == m.Scene && Now - peer.Seen < 1.5
                ? new Vector2(peer.State.X, peer.State.Y) : new Vector2(m.X, m.Y);
            var found = TryWarpLanding(target, m.Scene, out var pos);
            if (found) {
                var hero = HeroController.instance;
                hero.SetHeroParent(null);
                hero.GetComponent<Rigidbody2D>().velocity = Vector2.zero;
                hero.transform.position = new Vector3(pos.x, pos.y, hero.transform.position.z);
                Physics2D.SyncTransforms();
                hero.SetHazardRespawn(hero.transform.position, hero.cState.facingRight); hero.ResetAirMoves();
                GameCameras.instance.cameraController.PositionToHero(false);
            }
            Notice(found ? "已到达队友身边" : "队友附近没有安全落点");
        }
        changingScene = false; teammateWarp = null;
    }

    internal void Notice(string message) { status = message; statusUntil = Time.unscaledTime + 5; }
    private void OnGUI() {
        if (!Connected) return;
        var style = new GUIStyle(GUI.skin.label) { font = font, fontSize = 16, alignment = TextAnchor.MiddleCenter };
        style.normal.textColor = Color.white;
        if (Time.unscaledTime < statusUntil) GUI.Label(new Rect(0, 70, Screen.width, 36), status, style);
        if (Down || Resetting || Returning) GUI.Label(new Rect(0, Screen.height * .7f, Screen.width, 70),
            Resetting ? "全队失败 · 等待所有队友返回各自复活点" : Returning ? "正在返回自己的复活点 · 队友继续战斗" :
            "暗影形态 · 等待队友击破暗影，一血复活\n按 " + CoopMod.Settings.GiveUpKey + " 放弃救援，按原版死亡规则返回", style);
        map.DrawOverlay(style);
        if (Input.GetKey(CoopMod.Settings.DeathLeaderboardKey)) {
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
    }

    private void OnDestroy() {
        BossTravel.Unregister();
        CoopContext.CaptureBossContext = null; CoopContext.ApplyBossContext = null; CoopContext.BossTransitionReady = null;
        CoopContext.LocalRescueDown = null;
        CancelBenchReturn();
        CancelTransitions();
        shades?.Clear(); Unlock(); map?.Dispose();
        On.HeroController.Die -= OnDie; On.CameraController.LateUpdate -= CameraUpdate; On.GameManager.FindEntryPoint -= FindWarpEntry;
        On.HeroController.TakeDamage -= TakeDamage; ModHooks.AfterTakeDamageHook -= AfterTakeDamage;
        On.HeroAudioController.PlaySound -= TrackHitSound;
        On.HeroController.EnterSceneDreamGate -= CompleteWarpEntry;
        ModHooks.SetPlayerBoolHook -= OnSetBool; ModHooks.SetPlayerIntHook -= OnSetInt;
        if (api != null) { api.ClientManager.ConnectEvent -= OnConnect; api.ClientManager.DisconnectEvent -= OnDisconnect; }
    }
}
