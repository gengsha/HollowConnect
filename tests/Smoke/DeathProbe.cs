using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using Hkmp.Game.Client.Entity;
using HollowConnect;
using Modding;
using UnityEngine;
using Object = UnityEngine.Object;

namespace HollowConnect.Smoke;

public sealed class DeathProbe : MonoBehaviour {
    private string role, root, scene, dreamReturn;
    private bool host, started;
    private CoopRuntime runtime;
    private Hkmp.Game.GameManager engine;
    private int sounds;
    private string deathState;
    private readonly HashSet<string> clips = new HashSet<string>();
    private string benchScene => host ? "Town" : "Crossroads_47";
    private static T Field<T>(object obj, string key) => (T)obj.GetType().GetField(key, BindingFlags.Instance | BindingFlags.NonPublic).GetValue(obj);
    private readonly object logLock = new object();
    private void Log(string value) { lock (logLock) File.AppendAllText(Path.Combine(root, role + ".log"), DateTime.UtcNow.ToString("O") + " " + value + "\n"); }
    private sealed class EngineLog : Hkmp.Logging.ILogger {
        private readonly DeathProbe probe;
        internal EngineLog(DeathProbe probe) { this.probe = probe; }
        public void Info(string s) => probe.Log("ENGINE " + s);
        public void Debug(string s) { }
        public void Fine(string s) { }
        public void Warn(string s) => probe.Log("WARN " + s);
        public void Error(string s) => probe.Log("ERROR " + s);
    }
    private bool Has(string key) => File.Exists(Path.Combine(root, key));
    private void Flag(string key) => File.WriteAllText(Path.Combine(root, key), "ready");
    private void Require(bool ok, string name) { if (!ok) throw new Exception(name); Log("PASS " + name); }
    private IEnumerator Until(Func<bool> test, string name, float seconds = 50) {
        var deadline = Time.realtimeSinceStartup + seconds;
        while (!test() && Time.realtimeSinceStartup < deadline) yield return null;
        Require(test(), name);
    }
    private bool In(string name) => HeroController.instance != null && HeroController.instance.isHeroInPosition &&
        !GameManager.instance.IsInSceneTransition && UnityEngine.SceneManagement.SceneManager.GetActiveScene().name == name;
    private void Enter(string name) => GameManager.instance.BeginSceneTransition(new GameManager.SceneLoadInfo {
        SceneName = name, EntryGateName = name.StartsWith("Dream_") ? "door1" : name.StartsWith("GG_") ? "door_dreamEnter" : "left1",
        Visualization = name.StartsWith("Dream_") || name.StartsWith("GG_") ? GameManager.SceneLoadVisualizations.Dream : GameManager.SceneLoadVisualizations.Default,
        WaitForSceneTransitionCameraFade = true });
    private void PrepareHero(bool immune = true) {
        var h = HeroController.instance; h.RegainControl(); h.StartAnimationControl(); h.cState.dead = false;
        h.cState.invulnerable = false; h.cState.recoiling = false; h.cState.recoilingLeft = h.cState.recoilingRight = false;
        PlayerData.instance.SetBool("isInvincible", false); h.takeNoDamage = immune; HeroBox.inactive = false;
        h.SetDamageMode(GlobalEnums.DamageMode.FULL_DAMAGE);
        h.GetComponent<Rigidbody2D>().velocity = Vector2.zero; h.GetComponent<Rigidbody2D>().gravityScale = 0;
        h.transform.position = new Vector3(host ? 20 : 22, 12, 0);
    }
    private void Die() {
        PlayerData.instance.SetInt("health", 0);
        HeroController.instance.StartCoroutine((IEnumerator)typeof(HeroController).GetMethod("Die", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(HeroController.instance, null));
    }
    private IEnumerator Start() {
        role = Environment.GetEnvironmentVariable("HOLLOWCONNECT_SMOKE_ROLE");
        root = Environment.GetEnvironmentVariable("HOLLOWCONNECT_SMOKE_ROOT");
        var stack = new Stack<IEnumerator>(); stack.Push(Run());
        while (stack.Count != 0) {
            object current = null; var more = false;
            try { more = stack.Peek().MoveNext(); if (more) current = stack.Peek().Current; }
            catch (Exception e) { Log("FAIL " + e); File.WriteAllText(Path.Combine(root, role + ".failed"), e.ToString()); yield break; }
            if (!more) { stack.Pop(); continue; }
            if (current is IEnumerator nested) stack.Push(nested);
            else yield return current;
        }
    }
    private IEnumerator Run() {
        role = Environment.GetEnvironmentVariable("HOLLOWCONNECT_SMOKE_ROLE"); host = role == "host";
        root = Environment.GetEnvironmentVariable("HOLLOWCONNECT_SMOKE_ROOT");
        scene = Environment.GetEnvironmentVariable("HOLLOWCONNECT_DEATH_SCENE");
        if (string.IsNullOrEmpty(scene)) scene = "Crossroads_01";
        Log("native personal death probe; muted; all save overloads intercepted");
        Hkmp.Logging.Logger.AddLogger(new EngineLog(this));
        UnityEngine.SceneManagement.SceneManager.sceneLoaded += (loaded, mode) =>
            Log("SCENE " + loaded.name + " gates=" + string.Join(",", loaded.GetRootGameObjects().SelectMany(g => g.GetComponentsInChildren<TransitionPoint>(true)).Select(g => g.name + "/" + g.gameObject.activeInHierarchy)));
        On.GameManager.EnterHero += (orig, gm, additive) => {
            Log("ENTER zone=" + gm.GetCurrentMapZone() + " gate=" + gm.entryGateName + " bossGate=" + PlayerData.instance.GetString("bossReturnEntryGate"));
            orig(gm, additive);
            Log("ENTER RESULT gate=" + gm.entryGateName + " bossGate=" + gm.playerData.GetString("bossReturnEntryGate") +
                " samePD=" + ReferenceEquals(gm.playerData, PlayerData.instance));
        };
        Application.logMessageReceived += (message, trace, type) => {
            if ((type == LogType.Exception || type == LogType.Error) && !message.StartsWith("Couldn't find a Hero")) Log("UNITY " + message + " " + trace);
        };
        On.HeroAudioController.PlaySound += (orig, self, sound) => { if ((int)sound == 8) sounds++; orig(self, sound); };
        yield return Until(() => GameManager.instance != null && Object.FindObjectOfType<CoopRuntime>() != null &&
            UnityEngine.SceneManagement.SceneManager.GetActiveScene().name == "Menu_Title", "menu ready");
        runtime = Object.FindObjectOfType<CoopRuntime>(); engine = Field<Hkmp.Game.GameManager>(ModHooks.GetMod("HKMP"), "_gameManager");
        Field<Hkmp.Game.Settings.ModSettings>(engine.ClientManager, "_modSettings").AuthKey = Hkmp.Util.AuthUtil.GenerateAuthKey();
        CoopMod.Settings.ShareProgress = false; CoopMod.Settings.Rescue = true; CoopMod.Settings.EntryProtection = true;
        var pd = PlayerData.instance; pd.SetString("respawnScene", benchScene); pd.SetString("respawnMarkerName", "RestBench"); pd.SetInt("respawnType", 1);
        pd.SetInt("geo", host ? 123 : 456); pd.SetInt("geoPool", 0); pd.SetString("shadeScene", ""); pd.SetBool("soulLimited", false);
        dreamReturn = scene.StartsWith("GG_") ? "GG_Atrium" : "Crossroads_10";
        pd.SetString("dreamReturnScene", dreamReturn);
        if (scene.StartsWith("GG_")) {
            pd.SetVariable("bossDoorStateTier1", new BossSequenceDoor.Completion { unlocked = true });
            pd.SetVariable("bossDoorStateTier3", new BossSequenceDoor.Completion { unlocked = true });
        }
        GameManager.instance.profileID = 99; GameManager.instance.StartCoroutine(GameManager.instance.RunContinueGame());
        yield return Until(() => In(benchScene), "fresh unsaved personal checkpoint entered");
        var bench = GameObject.FindGameObjectsWithTag("RespawnPoint").FirstOrDefault(g => g.name == "RestBench");
        Log("CHECKPOINT " + benchScene + " markers=" + string.Join(",", GameObject.FindGameObjectsWithTag("RespawnPoint").Select(g => g.name)));
        Require(bench != null, "actual personal bench marker exists");
        if (host) { engine.ServerManager.Start(29650, false); Flag("server.started"); }
        else yield return Until(() => Has("server.started"), "server started");
        yield return new WaitForSecondsRealtime(1);
        typeof(Hkmp.Game.Client.ClientManager).GetField("_autoConnect", BindingFlags.Instance | BindingFlags.NonPublic).SetValue(engine.ClientManager, true);
        typeof(Hkmp.Game.Client.ClientManager).GetMethod("Connect", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(engine.ClientManager, new object[] { "127.0.0.1", 29650, "death" + role });
        yield return Until(() => runtime.Connected, "real UDP connected"); Flag(role + ".connected");
        yield return Until(() => Has((host ? "guest" : "host") + ".connected"), "both connected");
        if (!host) yield return new WaitForSecondsRealtime(3);
        if (scene.StartsWith("GG_")) {
            Enter("GG_Atrium"); yield return Until(() => In("GG_Atrium"), "native Godhome lobby visited before challenge");
            yield return null;
            var sequenceDeath = Environment.GetEnvironmentVariable("HOLLOWCONNECT_DEATH_SEQUENCE") == "1";
            var door = Object.FindObjectsOfType<BossSequenceDoor>().First(d => d.playerDataString == (sequenceDeath ? "bossDoorStateTier3" : "bossDoorStateTier1"));
            Require(door.dreamReturnGate != null, "actual Godhome personal return gate exists");
            pd.SetString("bossReturnEntryGate", door.dreamReturnGate.name);
            Log("ACTUAL GODHOME RETURN " + door.dreamReturnGate.name);
            BossSequenceController.Reset();
            if (sequenceDeath) {
                var nativeSequence = Resources.Load<BossSequence>("GG/Boss Sequence Tier 3");
                BossSequenceController.SetupNewSequence(nativeSequence, BossSequenceController.ChallengeBindings.None, "bossDoorStateTier3");
                typeof(BossSequenceController).GetField("bossIndex", BindingFlags.Static | BindingFlags.NonPublic).SetValue(null,
                    Enumerable.Range(0, nativeSequence.Count).First(i => nativeSequence.GetSceneAt(i) == scene));
            } else BossSceneController.SetupEvent = boss => { boss.BossLevel = 0; };
        }
        Enter(scene); yield return Until(() => In(scene), "combat room entered"); PrepareHero(false);
        pd.SetInt("health", 5); pd.SetInt("healthBlue", 0);
        Require(runtime.EntryProtected, "entry protection initially active");
        HeroController.instance.TakeDamage(null, GlobalEnums.CollisionSide.left, 1, 1);
        Require(pd.GetInt("health") == 5, "incoming hit cannot damage arriving player");
        if (scene == "Crossroads_01") {
            yield return Until(() => HeroController.instance.acceptingInput &&
                !HeroController.instance.cState.transitioning && HeroController.instance.transitionState == GlobalEnums.HeroTransitionState.WAITING_TO_TRANSITION &&
                GameCameras.instance.cameraController.mode != CameraController.CameraMode.FROZEN && engine.ClientManager.SceneSnapshotReady,
                "native input camera and snapshot ready within reaction protection", 10);
            PrepareHero(false);
            HeroController.instance.SetHazardRespawn(new Vector3(20, 12, 0), true);
            HeroController.instance.TakeDamage(null, GlobalEnums.CollisionSide.left, 1, 2);
            Log("PROTECTED HAZARD health=" + pd.GetInt("health") + " hazard=" + HeroController.instance.cState.hazardDeath +
                " transition=" + HeroController.instance.cState.transitioning + " input=" + HeroController.instance.acceptingInput + " protection=" + runtime.EntryProtected);
            Require(pd.GetInt("health") == 5 && HeroController.instance.cState.hazardDeath, "protected terrain hit preserves native hazard recovery without health loss");
            yield return Until(() => !HeroController.instance.cState.hazardDeath && !HeroController.instance.cState.hazardRespawning && HeroController.instance.acceptingInput,
                "native hazard recovery finishes during entry protection", 15);
            PrepareHero(false);
        }
        yield return Until(() => !runtime.EntryProtected, "protection expires after ready and reaction interval");
        PrepareHero(false); HeroController.instance.TakeDamage(null, GlobalEnums.CollisionSide.left, 1, 1);
        Require(pd.GetInt("health") == 4, "normal damage resumes after protection"); PrepareHero();
        started = true; Flag(role + ".arena");
        yield return Until(() => Has((host ? "guest" : "host") + ".arena"), "both ready in combat");
        var manager = Field<EntityManager>(engine.ClientManager, "_entityManager");
        yield return Until(() => manager.IsSceneHostDetermined && runtime.Peers.Count == 1, "scene authority and peer known");
        yield return FeedbackOrdering();
        if (Environment.GetEnvironmentVariable("HOLLOWCONNECT_DEATH_DISCONNECT") == "1") {
            yield return DisconnectDeath(manager);
        } else if (scene != "Crossroads_01") {
            yield return SpecialDeath();
        } else {
            yield return OrdinaryDeath(manager);
        }
        engine.ClientManager.Disconnect(); Flag(role + ".offline");
        yield return Until(() => Has((host ? "guest" : "host") + ".offline"), "both disconnected normally");
        if (host) engine.ServerManager.Stop();
        Log("FINAL PASS native personal death " + scene); Flag(role + ".done");
    }
    private IEnumerator FeedbackOrdering() {
        var receive = typeof(CoopRuntime).GetMethod("Receive", BindingFlags.Instance | BindingFlags.NonPublic);
        Action<Message> inject = m => { m.Session = runtime.Session; m.Round = Hkmp.CoopContext.Epoch; receive.Invoke(runtime, new object[] { m }); };
        var before = sounds;
        inject(new Message { Kind = Kind.Roster, Id = 65000, Aux = 3, Serial = 10, Key = "1", Value = (int)HollowConnect.Core.Life.Down, Scene = scene });
        var down = new Message { Kind = Kind.Down, Id = 65000, Aux = 3, Serial = 1, Scene = scene, X = 20, Y = 12 };
        inject(down); inject(down);
        Require(sounds == before + 1, "roster before reliable Down still gives exactly one cue");
        inject(new Message { Kind = Kind.Revived, Id = 65000, Target = 64999, Aux = 4, Serial = 1 }); inject(down);
        Require(sounds == before + 1 && runtime.Peers[65000].Life == HollowConnect.Core.Life.Alive, "stale Down after rescue cannot repeat cue or overwrite life");
        inject(new Message { Kind = Kind.Left, Id = 65000 });
        yield return new WaitForSecondsRealtime(1.3f);
        Require(!Object.FindObjectsOfType<ParticleSystem>().Any(p => p.name == "HollowConnect down particles"), "one-shot native particles clean up");
    }
    private IEnumerator DisconnectDeath(EntityManager manager) {
        if (!host) {
            Die(); yield return Until(() => runtime.Down, "down before disconnect death");
            var pressed = false; runtime.KeyPressed = key => key == KeyCode.F6 && !pressed && (pressed = true);
            yield return Until(() => runtime.Returning && HeroController.instance.heroDeathPrefab.activeSelf, "formal death accepted before disconnect");
            engine.ClientManager.Disconnect(); runtime.KeyPressed = _ => false; Flag("guest.native-disconnected");
            yield return Until(() => In(benchScene) && !runtime.Returning && !runtime.Resetting &&
                GameCameras.instance.cameraController.mode != CameraController.CameraMode.FROZEN, "accepted native death finishes after disconnect", 70);
            Require(clips.Contains("Death") && PlayerData.instance.GetInt("geoPool") == 456 &&
                PlayerData.instance.GetInt("geo") == 0 && PlayerData.instance.GetString("shadeScene") == scene,
                "disconnected formal death retains animation and exactly one native penalty");
            Flag("guest.native-arrived");
        } else {
            yield return Until(() => Has("guest.native-disconnected"), "guest disconnected during animation");
            yield return Until(() => Has("guest.native-arrived"), "guest reached private native checkpoint", 70);
            Require(In(scene) && manager.IsSceneHost && Hkmp.CoopContext.Epoch == 0 && !runtime.Resetting,
                "survivor keeps ongoing room after teammate disconnects during death");
        }
    }
    private IEnumerator OrdinaryDeath(EntityManager manager) {
        var pd = PlayerData.instance;
        if (!host) {
            yield return new WaitForSecondsRealtime(.4f);
            PrepareHero(false); pd.SetInt("health", 1);
            var before = sounds;
            HeroController.instance.TakeDamage(null, GlobalEnums.CollisionSide.left, 1, 1);
            yield return Until(() => runtime.Down, "guest rescue down from native lethal hit");
            Require(!HeroController.instance.heroDeathPrefab.activeSelf, "temporary down has no death animation");
            Require(pd.GetInt("geo") == 456 && !pd.GetBool("soulLimited") && pd.GetString("shadeScene") == "", "temporary down preserves geo and original shade state");
            Require(sounds == before + 1, "local temporary down plays one native hit sound");
            var stunInfo = new Hkmp.Animation.Effects.Stun().GetEffectInfo();
            Require(stunInfo.Length == 2 && stunInfo[1], "rescue down animation cannot duplicate remote hit sound");
            Flag("guest.down");
            yield return Until(() => !runtime.Down && pd.GetInt("health") == 1, "rescue revives at one health");
            Require(pd.GetInt("geo") == 456 && pd.GetString("shadeScene") == "", "rescue has no formal death penalty");
            PrepareHero(); Flag("guest.rescued"); yield return Until(() => Has("host.rescue-checked"), "host checked rescue");
            Die(); yield return Until(() => runtime.Down, "guest down before give up");
            var pressed = false; runtime.KeyPressed = key => key == KeyCode.F6 && !pressed && (pressed = true);
            yield return Until(() => !runtime.Down && !runtime.Returning && In(benchScene), "native give-up returns to guest's own bench", 70);
            runtime.KeyPressed = _ => false;
            Require(clips.Contains("Death"), "formal give-up plays native Death animation");
            Require(pd.GetInt("geo") == 0 && pd.GetInt("geoPool") == 456 && pd.GetString("shadeScene") == scene && pd.GetBool("soulLimited"), "formal story death creates native geo-bearing shade state");
            Require(pd.GetString("respawnScene") == benchScene && pd.GetInt("respawnType") == 1, "personal bench and respawn type remain unchanged");
            Require(Hkmp.CoopContext.Epoch == 0 && !runtime.Resetting, "individual death does not reset party");
            Flag("guest.giveup"); yield return Until(() => Has("host.survived"), "survivor remained in combat");
            Enter(scene); yield return Until(() => In(scene), "guest rejoins native shade room"); PrepareHero();
            yield return new WaitForSecondsRealtime(.3f);
            foreach (var g in Resources.FindObjectsOfTypeAll<GameObject>().Where(g => g.name.Contains("Hollow Shade")))
                Log("SHADE " + g.name + " scene=" + g.scene.name + " active=" + g.activeInHierarchy + " health=" + (g.GetComponent<HealthManager>() != null));
            yield return Until(() => Object.FindObjectsOfType<HealthManager>().Any(h => h.gameObject.name == "Hollow Shade(Clone)" && h.gameObject.scene.name == scene), "native personal shade spawned in death room");
            var shade = Object.FindObjectsOfType<HealthManager>().First(h => h.gameObject.name == "Hollow Shade(Clone)" && h.gameObject.scene.name == scene);
            Flag("guest.personal-shade-spawned");
            yield return Until(() => Has("host.personal-shade-checked"), "teammate checked while personal native shade is alive");
            shade.IsInvincible = false; shade.Die(null, AttackTypes.Nail, true);
            yield return Until(() => pd.GetInt("geo") == 456 && pd.GetInt("geoPool") == 0 && !pd.GetBool("soulLimited"), "native shade restores only owner's geo and soul");
            Flag("guest.recovered");
        } else {
            var before = sounds; yield return Until(() => Has("guest.down") && runtime.Peers.Values.Any(p => p.Life == HollowConnect.Core.Life.Down), "reliable teammate down received");
            Require(sounds == before + 1, "remote temporary down plays one native hit sound");
            var fx = Object.FindObjectsOfType<ParticleSystem>().FirstOrDefault(p => p.name == "HollowConnect down particles");
            Require(fx != null && fx.particleCount > 0 && fx.particleCount <= 16, "native down particle burst is present and bounded");
            var peer = runtime.Peers.First(); Require(runtime.RequestShadeHit(peer.Key, peer.Value.DownSerial), "rescue request accepted for current death");
            yield return Until(() => Has("guest.rescued"), "guest rescued");
            Require(pd.GetInt("geo") == 123 && pd.GetString("shadeScene") == "", "teammate rescue does not change host's geo"); Flag("host.rescue-checked");
            yield return Until(() => Has("guest.giveup"), "guest completed formal personal death", 70);
            Require(In(scene) && !runtime.Resetting && !runtime.Returning && manager.IsSceneHost && Hkmp.CoopContext.Epoch == 0, "survivor remains room authority during teammate's formal death");
            Require(pd.GetInt("geo") == 123 && pd.GetString("shadeScene") == "", "personal shade penalty remains private"); Flag("host.survived");
            yield return Until(() => Has("guest.personal-shade-spawned"), "guest personal shade is alive in shared room");
            yield return new WaitForSecondsRealtime(.4f);
            Require(!Object.FindObjectsOfType<HealthManager>().Any(h => h.gameObject.name == "Hollow Shade(Clone)"), "live ordinary teammate shade remains invisible locally");
            Require(!Field<Dictionary<ulong, Entity>>(manager, "_entities").Values.Any(e =>
                e.Object.Client != null && e.Object.Client.name.StartsWith("Hollow Shade")), "live personal shade is not a synchronized enemy");
            Flag("host.personal-shade-checked");
            yield return Until(() => Has("guest.recovered"), "guest recovered native personal shade");
        }
        PrepareHero(); pd.SetInt("geo", host ? 111 : 222); Flag(role + ".wipe-ready");
        yield return Until(() => Has((host ? "guest" : "host") + ".wipe-ready"), "both ready for full failure");
        var first = Environment.GetEnvironmentVariable("HOLLOWCONNECT_DEATH_FIRST");
        if (string.IsNullOrEmpty(first)) first = "guest";
        if (role == first) { Die(); Flag(first + ".wipe-down"); }
        else { yield return Until(() => Has(first + ".wipe-down") && runtime.Peers.Values.Any(p => p.Life == HollowConnect.Core.Life.Down), first + " down first"); Die(); }
        yield return Until(() => runtime.ReleasedRound == 1 && !runtime.Resetting && In(benchScene), "full team failure returns each to own bench", 75);
        Require(pd.GetString("respawnScene") == benchScene && pd.GetString("respawnMarkerName") == "RestBench" && pd.GetInt("respawnType") == 1, "wipe never writes another player's checkpoint");
        Require(pd.GetInt("geo") == 0 && pd.GetInt("geoPool") == (host ? 111 : 222) && pd.GetString("shadeScene") == scene, "wipe performs one native death penalty per player");
        Require(clips.Contains("Death") && !HeroController.instance.cState.dead && !HeroBox.inactive, "native death animation and playable respawn complete");
    }
    private IEnumerator SpecialDeath() {
        var pd = PlayerData.instance;
        Log("DEATH CONTEXT zone=" + GameManager.instance.GetCurrentMapZone() + " dream=" + pd.GetString("dreamReturnScene") + " gate=" + pd.GetString("bossReturnEntryGate"));
        Die(); yield return Until(() => runtime.Down || runtime.Resetting, "special-zone temporary down");
        var palace = GameManager.instance.GetCurrentMapZone() == "WHITE_PALACE";
        var destination = palace ? benchScene : dreamReturn;
        yield return Until(() => runtime.ReleasedRound == 1 && !runtime.Resetting && In(destination), "native special-zone return uses own destination", 75);
        Require(pd.GetInt("geo") == (host ? 123 : 456) && pd.GetInt("geoPool") == 0 && pd.GetString("shadeScene") == "" && !pd.GetBool("soulLimited"), "native special-zone death preserves money and does not create shade");
        // White Palace uses the prefab's dream animation but the native bench
        // route; inspect the actual map-zone branch rather than guessing.
        Require(clips.Contains("Death Dream"), "native special-zone death animation observed");
        Require(pd.GetString("respawnScene") == benchScene, "implicit dream return preserves personal bench");
    }
    private void Update() {
        if (!started || HeroController.instance == null) return;
        var death = HeroController.instance.heroDeathPrefab;
        if (death == null || !death.activeSelf) return;
        foreach (var animator in death.GetComponentsInChildren<tk2dSpriteAnimator>(true)) if (animator.CurrentClip != null) clips.Add(animator.CurrentClip.name);
        var fsm = death.GetComponent<PlayMakerFSM>();
        if (fsm != null && fsm.ActiveStateName != deathState) { deathState = fsm.ActiveStateName; Log("NATIVE DEATH " + deathState + " gate=" + PlayerData.instance.GetString("bossReturnEntryGate")); }
    }
}
