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

// Never included in the release payload. Exercise two actual Unity clients and
// the real UDP transport without opening or writing any player save file.
public sealed class ProbeMod : Mod {
    public override string GetVersion() => "test-only";
    public override void Initialize() {
        if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("HOLLOWCONNECT_SMOKE_ROLE"))) return;
        ProbeLogGuard.Install(Environment.GetEnvironmentVariable("HOLLOWCONNECT_SMOKE_ROOT"), Environment.GetEnvironmentVariable("HOLLOWCONNECT_SMOKE_ROLE"));
        ProbeNetworkFaults.Install();
        On.GameManager.SaveGame += (orig, gm) => { };
        On.GameManager.SaveGame_Action1 += (orig, gm, callback) => callback?.Invoke(true);
        On.GameManager.SaveGame_int_Action1 += (orig, gm, slot, callback) => callback?.Invoke(true);
        // Unity batch mode does not dispatch render-end waits. Preserve the
        // respawn routine while yielding the next simulation frame instead.
        On.HeroController.Respawn += (orig, hero) => SimulationFrames(orig(hero));
        On.HeroController.HazardRespawn += (orig, hero) => SimulationFrames(orig(hero));
        On.GameManager.LoadFirstScene += (orig, gm) => SimulationFrames(orig(gm));
        var go = new GameObject("HollowConnect smoke probe"); Object.DontDestroyOnLoad(go);
        go.AddComponent<SilentProbe>();
        if (Environment.GetEnvironmentVariable("HOLLOWCONNECT_SMOKE_MODE") == "rescue") { go.AddComponent<RescueProbe>(); return; }
        if (Environment.GetEnvironmentVariable("HOLLOWCONNECT_SMOKE_MODE") == "shade-inspect") { go.AddComponent<ShadeInspectProbe>(); return; }
        if (Environment.GetEnvironmentVariable("HOLLOWCONNECT_SMOKE_MODE") == "death") { go.AddComponent<DeathProbe>(); return; }
        if (Environment.GetEnvironmentVariable("HOLLOWCONNECT_SMOKE_MODE") == "grimm-motion") { go.AddComponent<GrimmMotionProbe>(); return; }
        if (Environment.GetEnvironmentVariable("HOLLOWCONNECT_SMOKE_MODE") == "pantheon") { go.AddComponent<PantheonProbe>(); return; }
        if (Environment.GetEnvironmentVariable("HOLLOWCONNECT_SMOKE_MODE") == "join") go.AddComponent<JoinProbe>();
        else if (Environment.GetEnvironmentVariable("HOLLOWCONNECT_SMOKE_MODE") == "boss") go.AddComponent<BossProbe>();
        else if (Environment.GetEnvironmentVariable("HOLLOWCONNECT_SMOKE_MODE") == "godhome") go.AddComponent<GodhomeProbe>();
        else if (Environment.GetEnvironmentVariable("HOLLOWCONNECT_SMOKE_MODE") == "coverage") go.AddComponent<CoverageProbe>();
        else go.AddComponent<Probe>();
    }
    private static IEnumerator SimulationFrames(IEnumerator routine) {
        while (routine.MoveNext()) yield return routine.Current is WaitForEndOfFrame ? null : routine.Current;
    }
}

// Mute only these disposable processes; never write the user's audio settings.
public sealed class SilentProbe : MonoBehaviour {
    private void Awake() => AudioListener.volume = 0;
    private void LateUpdate() => AudioListener.volume = 0;
}

public sealed class Probe : MonoBehaviour {
    private string role, root;
    private Hkmp.Game.GameManager engine;
    private CoopRuntime runtime;
    private bool host;
    private int pendingCameraPositions;
    private Vector2 testOrigin = new Vector2(20, 10);
    private static T Field<T>(object instance, string name) => (T)instance.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic).GetValue(instance);
    private void Mark(string message) { File.AppendAllText(Path.Combine(root, role + ".log"), DateTime.UtcNow.ToString("O") + " " + message + "\n"); }
    private void Flag(string name, string value = "ready") => File.WriteAllText(Path.Combine(root, name), value);
    private IEnumerator WatchCameraPosition(IEnumerator routine) {
        try { while (routine.MoveNext()) yield return routine.Current; }
        finally { pendingCameraPositions--; }
    }
    private bool Has(string name) => File.Exists(Path.Combine(root, name));
    private IEnumerator Until(Func<bool> ready, string name, float timeout = 45) {
        var end = Time.realtimeSinceStartup + timeout;
        while (!ready() && Time.realtimeSinceStartup < end) yield return null;
        if (!ready()) { Mark("FAIL timeout " + name); Flag(role + ".failed", name); }
        else Mark("PASS " + name);
    }
    private bool AliveScene(string scene) => HeroController.instance != null && HeroController.instance.isHeroInPosition &&
        GameManager.instance != null && !GameManager.instance.IsInSceneTransition && GameManager.instance.sceneName == scene;
    private void Freeze() {
        var hero = HeroController.instance;
        hero.transform.position = new Vector3(testOrigin.x, testOrigin.y, hero.transform.position.z);
        hero.GetComponent<Rigidbody2D>().velocity = Vector2.zero;
        hero.GetComponent<Rigidbody2D>().gravityScale = 0;
        hero.takeNoDamage = true; hero.RelinquishControl();
    }
    private void Die() {
        var hero = HeroController.instance; PlayerData.instance.SetInt("health", 0);
        hero.StartCoroutine((IEnumerator)typeof(HeroController).GetMethod("Die", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(hero, null));
    }
    private void Enter(string scene) => GameManager.instance.BeginSceneTransition(new GameManager.SceneLoadInfo {
        SceneName = scene, EntryGateName = "left1", Visualization = GameManager.SceneLoadVisualizations.Default,
        WaitForSceneTransitionCameraFade = true
    });

    private IEnumerator GiveUpFixture(EntityManager manager, ulong enemyId) {
        var checkpointScene = PlayerData.instance.GetString("respawnScene");
        var checkpointMarker = PlayerData.instance.GetString("respawnMarkerName");
        runtime.KeyPressed = key => key == KeyCode.F6;
        yield return new WaitForSecondsRealtime(.2f); runtime.KeyPressed = _ => false;
        if (!AliveScene("Crossroads_01") || runtime.Down || runtime.Returning) {
            Mark("FAIL F6 affected a living player"); Flag(role + ".failed"); yield break;
        }
        Mark("PASS F6 does nothing while alive");
        Die(); yield return Until(() => runtime.Down, role + " shade before voluntary return");
        var pressed = false;
        runtime.KeyPressed = key => key == KeyCode.F6 && !pressed && (pressed = true);
        yield return Until(() => pressed && AliveScene(checkpointScene) && !runtime.Down && !runtime.Returning && !runtime.Resetting,
            role + " F6 returns only self to personal bench");
        runtime.KeyPressed = _ => false;
        if (Has(role + ".failed")) yield break;
        // Native PositionToHero deliberately freezes the camera until its
        // positioning coroutine completes, after isHeroInPosition is true.
        yield return Until(() => pendingCameraPositions == 0 && GameCameras.instance.cameraController.mode != CameraController.CameraMode.FROZEN,
            role + " native bench camera positioning finishes", 5);
        if (Has(role + ".failed")) yield break;
        var bench = GameObject.FindGameObjectsWithTag("RespawnPoint").FirstOrDefault(g => g.name == checkpointMarker);
        Mark("BENCH ARRIVAL epoch=" + Hkmp.CoopContext.Epoch + " released=" + runtime.ReleasedRound + " target=" + checkpointScene + "/" + checkpointMarker +
            " bench=" + (bench == null ? "null" : bench.transform.position.ToString()) + " hero=" + HeroController.instance.transform.position +
            " hidden=" + HeroController.instance.GetComponent<Renderer>().forceRenderingOff + " camera=" + GameCameras.instance.cameraController.mode +
            " actual=" + PlayerData.instance.GetString("respawnScene") + "/" + PlayerData.instance.GetString("respawnMarkerName"));
        if (Hkmp.CoopContext.Epoch != 0 || runtime.ReleasedRound != 0 || bench == null ||
            Vector2.Distance(HeroController.instance.transform.position, bench.transform.position) > 6 ||
            HeroController.instance.GetComponent<Renderer>().forceRenderingOff ||
            GameCameras.instance.cameraController.mode == CameraController.CameraMode.FROZEN ||
            PlayerData.instance.GetString("respawnScene") != checkpointScene || PlayerData.instance.GetString("respawnMarkerName") != checkpointMarker) {
            Mark("FAIL personal bench arrival, restored camera/body or unchanged attempt"); Flag(role + ".failed"); yield break;
        }
        Mark("PASS own bench reached; camera and renderer restored; party attempt unchanged"); Flag(role + ".giveup-bench");
        yield return Until(() => Has(role + ".giveup-resume"), "survivor continued combat during " + role + " bench return");
        Enter("Crossroads_01"); yield return Until(() => AliveScene("Crossroads_01"), role + " rejoins ongoing combat after F6"); Freeze();
        var entities = Field<Dictionary<ulong, Entity>>(manager, "_entities");
        var expectedHp = int.Parse(File.ReadAllText(Path.Combine(root, "enemy.attack-hp")));
        yield return Until(() => !manager.IsSceneHost && entities.TryGetValue(enemyId, out var current) && current.Object.Client != null &&
            current.Object.Client.GetComponent<HealthManager>().hp == expectedHp, role + " reentry preserves ongoing enemy health");
        Flag(role + ".giveup-rejoined");
    }

    private IEnumerator Start() {
        role = Environment.GetEnvironmentVariable("HOLLOWCONNECT_SMOKE_ROLE");
        root = Environment.GetEnvironmentVariable("HOLLOWCONNECT_SMOKE_ROOT"); host = role == "host";
        On.CameraController.DoPositionToHero += (orig, camera, direct) => {
            pendingCameraPositions++;
            return WatchCameraPosition(orig(camera, direct));
        };
        Mark("probe started; save writes intercepted");
        yield return Until(() => Object.FindObjectOfType<CoopRuntime>() != null && GameManager.instance != null &&
            UnityEngine.SceneManagement.SceneManager.GetActiveScene().name == "Menu_Title", "mod and menu ready");
        if (Has(role + ".failed")) yield break;
        runtime = Object.FindObjectOfType<CoopRuntime>();
        var mod = ModHooks.GetMod("HKMP"); engine = Field<Hkmp.Game.GameManager>(mod, "_gameManager");
        var settings = Field<Hkmp.Game.Settings.ModSettings>(engine.ClientManager, "_modSettings");
        settings.AuthKey = Hkmp.Util.AuthUtil.GenerateAuthKey();
        CoopMod.Settings.ShareProgress = true; CoopMod.Settings.Rescue = true; CoopMod.Settings.EntryProtection = false;
        // Menu's fresh default PlayerData, no existing save is loaded.
        var pd = PlayerData.instance; pd.SetString("respawnScene", "Town"); pd.SetString("respawnMarkerName", "RestBench");
        pd.SetInt("respawnType", 1); pd.SetBool("hasWalljump", false); pd.SetBool("canWallJump", false);
        GameManager.instance.profileID = 99;
        GameManager.instance.StartCoroutine(GameManager.instance.RunContinueGame());
        yield return Until(() => AliveScene("Town"), "fresh unsaved game entered");
        if (Has(role + ".failed")) yield break;
        Freeze();
        foreach (var shade in Resources.FindObjectsOfTypeAll<GameObject>().Where(g => g.name == "Hollow Shade")) {
            var sprite = shade.GetComponent<tk2dSprite>(); var animator = shade.GetComponent<tk2dSpriteAnimator>();
            Mark("SHADE ASSET scene=" + shade.scene.name + " sprite=" + (sprite != null) + " scale=" + shade.transform.localScale +
                " clips=" + (animator?.Library == null ? "none" : string.Join(",", animator.Library.clips.Select(c => c.name))));
        }
        if (host) { engine.ServerManager.Start(29650, false); Flag("server.started"); }
        else yield return Until(() => Has("server.started"), "host server ready");
        yield return new WaitForSecondsRealtime(1);
        typeof(Hkmp.Game.Client.ClientManager).GetField("_autoConnect", BindingFlags.NonPublic | BindingFlags.Instance).SetValue(engine.ClientManager, true);
        typeof(Hkmp.Game.Client.ClientManager).GetMethod("Connect", BindingFlags.NonPublic | BindingFlags.Instance).Invoke(engine.ClientManager, new object[] { "127.0.0.1", 29650, "smoke" + role });
        yield return Until(() => runtime.Connected, "real UDP addon handshake");
        if (Has(role + ".failed")) yield break;
        Mark("session=" + runtime.Session + " id=" + runtime.LocalId);
        Flag(role + ".connected");
        yield return Until(() => Has(host ? "guest.connected" : "host.connected"), "both clients connected");
        if (!host) yield return new WaitForSecondsRealtime(2);
        Enter("Crossroads_01");
        yield return Until(() => AliveScene("Crossroads_01"), "actual enemy scene entered");
        Freeze(); Flag(role + ".room");
        yield return Until(() => Has(host ? "guest.room" : "host.room"), "both clients in enemy room");
        yield return new WaitForSecondsRealtime(2);
        var manager = Field<EntityManager>(engine.ClientManager, "_entityManager");
        var entities = Field<Dictionary<ulong, Entity>>(manager, "_entities");
        Mark("entities=" + entities.Count + " authority=" + manager.IsSceneHost);
        if (host) {
            var found = entities.Values.FirstOrDefault(e => e.Object.Host != null && e.Object.Host.activeInHierarchy && e.Object.Host.GetComponent<HealthManager>() != null && e.Object.Host.GetComponent<HealthManager>().hp > 0);
            if (found == null) { Mark("FAIL no enemy found"); Flag("host.failed"); yield break; }
            var hm = found.Object.Host.GetComponent<HealthManager>(); hm.hp = 40; hm.ApplyExtraDamage(3);
            Flag("enemy.expected", found.Id + "," + hm.hp); Mark("health authority id=" + found.Id + " hp=" + hm.hp);
            pd.SetBool("hasWalljump", true); pd.SetBool("canWallJump", true);
        } else {
            yield return Until(() => Has("enemy.expected"), "host enemy damage");
            var expected = File.ReadAllText(Path.Combine(root, "enemy.expected")).Split(',');
            var id = ulong.Parse(expected[0]); var hp = int.Parse(expected[1]);
            yield return Until(() => entities.TryGetValue(id, out var entity) && entity.Object.Client != null &&
                entity.Object.Client.GetComponent<HealthManager>().hp == hp, "remote absolute enemy health");
            yield return Until(() => pd.GetBool("hasWalljump") && pd.GetBool("canWallJump"), "networked Mantis Claw grant");
            if (Has(role + ".failed")) yield break;
            Flag("guest.health-progress");
        }
        if (host) yield return Until(() => Has("guest.health-progress"), "guest health and progress verified");
        if (Has(role + ".failed")) yield break;
        var enemyParts = File.ReadAllText(Path.Combine(root, "enemy.expected")).Split(',');
        var enemyId = ulong.Parse(enemyParts[0]);
        if (host) {
            var safe = false;
            for (var x = 15; x < 90 && !safe; x += 3)
                for (var y = 8; y < 45 && !safe; y += 3)
                    if (Physics2D.OverlapBox(new Vector2(x + 1, y), new Vector2(5, 3), 0, 1 << 8) == null) {
                        testOrigin = new Vector2(x, y); safe = true;
                    }
            Flag("combat.origin", testOrigin.x + "," + testOrigin.y); Freeze();
            var obj = entities[enemyId].Object.Host;
            foreach (var fsm in obj.GetComponents<PlayMakerFSM>()) fsm.enabled = false;
            var body = obj.GetComponent<Rigidbody2D>();
            if (body != null) { body.gravityScale = 0; body.velocity = Vector2.zero; }
            foreach (var behaviour in obj.GetComponents<MonoBehaviour>())
                if (behaviour is Walker || behaviour is Climber) behaviour.enabled = false;
            obj.transform.position = new Vector3(testOrigin.x + 2, testOrigin.y, obj.transform.position.z);
            Physics2D.SyncTransforms();
            var health = obj.GetComponent<HealthManager>(); health.IsInvincible = true;
            Mark("combat target " + obj.name + " active=" + obj.activeInHierarchy + " timeScale=" + Time.timeScale + " colliders=" + string.Join(",", obj.GetComponents<Collider2D>().Select(c => c.enabled + ":" + c.isTrigger + ":" + c.bounds)));
            Flag("enemy.moved");
            yield return Until(() => Has("guest.position-state"), "guest position and invincibility verified");
            health.IsInvincible = false; Flag("guest.attack-request");
            yield return Until(() => health.hp < int.Parse(enemyParts[1]), "remote player's real nail damages authority");
            if (Has(role + ".failed")) yield break;
            Flag("enemy.attack-hp", health.hp.ToString());
            yield return Until(() => Has("guest.attack-health"), "guest nail damage health converged");
        } else {
            yield return Until(() => Has("enemy.moved"), "authority moved enemy");
            var originParts = File.ReadAllText(Path.Combine(root, "combat.origin")).Split(',');
            testOrigin = new Vector2(float.Parse(originParts[0]), float.Parse(originParts[1])); Freeze();
            yield return Until(() => entities[enemyId].Object.Client.GetComponent<HealthManager>().IsInvincible &&
                Vector2.Distance(entities[enemyId].Object.Client.transform.position, testOrigin + new Vector2(2, 0)) < .1f,
                "remote enemy position and invincibility");
            Flag("guest.position-state");
            yield return Until(() => Has("guest.attack-request"), "nail attack requested");
            yield return new WaitForSecondsRealtime(.5f);
            HeroController.instance.RegainControl(); HeroController.instance.StartAnimationControl();
            HeroController.instance.FaceRight(); HeroController.instance.Attack(GlobalEnums.AttackDirection.normal);
            Physics2D.SyncTransforms();
            yield return new WaitForSecondsRealtime(.05f);
            Mark("attack timeScale=" + Time.timeScale + " hero=" + HeroController.instance.transform.position + " enemy=" + entities[enemyId].Object.Client.transform.position + " slashes=" + string.Join(";", HeroController.instance.GetComponentsInChildren<Collider2D>().Where(c => c.gameObject.name.Contains("Slash")).Select(c => c.name + ":" + c.enabled + ":" + c.bounds)));
            yield return new WaitForSecondsRealtime(.6f); Freeze();
            Mark("guest after nail hp=" + entities[enemyId].Object.Client.GetComponent<HealthManager>().hp);
            yield return Until(() => Has("enemy.attack-hp"), "authority received remote nail");
            if (Has(role + ".failed")) yield break;
            var hp = int.Parse(File.ReadAllText(Path.Combine(root, "enemy.attack-hp")));
            yield return Until(() => entities[enemyId].Object.Client.GetComponent<HealthManager>().hp == hp,
                "nail damage synchronized back to guest"); Flag("guest.attack-health");
        }
        yield return Until(() => runtime.Peers.Values.Any(p => p.State != null && p.State.HasMap), "live teammate map coordinate");
        var gameMap = GameManager.instance.gameMap.GetComponent<GameMap>();
        pd.SetBool("equippedCharm_2", true); gameMap.PositionCompass(false);
        yield return null; yield return null;
        var mapService = Field<MapService>(runtime, "map");
        if (!mapService.TryLocation(out var mapPoint) || Vector2.Distance(mapPoint, gameMap.compassIcon.transform.localPosition) > .002f) {
            Mark("FAIL map projection disagrees with game's own compass"); Flag(role + ".failed"); yield break;
        }
        pd.SetBool("equippedCharm_2", false);
        yield return Until(() => gameMap.transform.Find("HollowConnect teammate " + runtime.Peers.Keys.First()) != null,
            "teammate map icon exists without equipped compass");
        Mark("PASS map projection equals actual game compass"); gameMap.CloseQuickMap();
        if (Environment.GetEnvironmentVariable("HOLLOWCONNECT_SMOKE_MODE") == "authority") {
            yield return AuthorityFixture(manager, entities, enemyId);
            yield break;
        }
        if (host) {
            Flag("guest.giveup-request");
            yield return Until(() => Has("guest.giveup-bench"), "guest voluntarily returned to own bench");
            var health = entities[enemyId].Object.Host.GetComponent<HealthManager>();
            if (!AliveScene("Crossroads_01") || runtime.Down || runtime.Resetting || runtime.Returning || Hkmp.CoopContext.Epoch != 0 ||
                !manager.IsSceneHost || health.hp != int.Parse(File.ReadAllText(Path.Combine(root, "enemy.attack-hp")))) {
                Mark("FAIL guest give up interrupted survivor or reset combat"); Flag(role + ".failed"); yield break;
            }
            health.ApplyExtraDamage(1); Flag("enemy.attack-hp", health.hp.ToString());
            Mark("PASS host stays in combat and deals more damage while guest is at bench"); Flag("guest.giveup-resume");
            yield return Until(() => Has("guest.giveup-rejoined"), "guest rejoined unchanged combat snapshot");
            Flag("host.giveup-request"); yield return GiveUpFixture(manager, enemyId);
        } else {
            yield return Until(() => Has("guest.giveup-request"), "guest voluntary return requested");
            yield return GiveUpFixture(manager, enemyId);
            yield return Until(() => Has("host.giveup-bench") && manager.IsSceneHost, "room authority gave up; guest continues as authority");
            var health = entities[enemyId].Object.Host.GetComponent<HealthManager>();
            if (!AliveScene("Crossroads_01") || runtime.Down || runtime.Resetting || runtime.Returning || Hkmp.CoopContext.Epoch != 0 ||
                health.hp != int.Parse(File.ReadAllText(Path.Combine(root, "enemy.attack-hp")))) {
                Mark("FAIL authority give up reset ongoing combat"); Flag(role + ".failed"); yield break;
            }
            health.ApplyExtraDamage(1); Flag("enemy.attack-hp", health.hp.ToString());
            Mark("PASS surviving guest keeps enemy health and deals more damage after authority gives up"); Flag("host.giveup-resume");
            yield return Until(() => Has("host.giveup-rejoined"), "old authority rejoined current combat snapshot");
        }
        if (Has(role + ".failed")) yield break;
        // Keep a half-health enemy through authority transfer, then replay its
        // terminal death snapshot to a player who has genuinely left/re-entered.
        if (host) {
            Enter("Town"); yield return Until(() => AliveScene("Town"), "old authority leaves enemy room"); Freeze();
            Flag("host.authority-left");
            yield return Until(() => Has("guest.authority-health"), "guest kept health after authority transfer");
            Enter("Crossroads_01"); yield return Until(() => AliveScene("Crossroads_01"), "old authority rejoins as replica"); Freeze();
            yield return Until(() => !manager.IsSceneHost && entities.TryGetValue(enemyId, out var kept) && kept.Object.Client != null &&
                kept.Object.Client.GetComponent<HealthManager>().hp == int.Parse(File.ReadAllText(Path.Combine(root, "enemy.attack-hp"))),
                "rejoined replica receives current half-health snapshot"); Flag("host.half-health-rejoined");
            yield return Until(() => Has("guest.enemy-killed"), "new authority killed enemy");
            yield return Until(() => entities[enemyId].Object.Client == null || !entities[enemyId].Object.Client.activeInHierarchy,
                "terminal death appears on replica");
            Enter("Town"); yield return Until(() => AliveScene("Town"), "replica leaves after death"); Freeze();
            Enter("Crossroads_01"); yield return Until(() => AliveScene("Crossroads_01"), "replica late reentry after death"); Freeze();
            yield return Until(() => entities.TryGetValue(enemyId, out var dead) && !dead.Object.Host.activeInHierarchy &&
                (dead.Object.Client == null || !dead.Object.Client.activeInHierarchy), "late death snapshot does not resurrect enemy");
            Flag("host.dead-rejoined");
        } else {
            yield return Until(() => Has("host.authority-left") && manager.IsSceneHost, "guest becomes actual scene authority");
            var health = entities[enemyId].Object.Host.GetComponent<HealthManager>();
            if (health.hp != int.Parse(File.ReadAllText(Path.Combine(root, "enemy.attack-hp")))) {
                Mark("FAIL health reset during authority transfer"); Flag(role + ".failed"); yield break;
            }
            Mark("PASS authority transfer preserved enemy health"); Flag("guest.authority-health");
            yield return Until(() => Has("host.half-health-rejoined"), "host current health snapshot verified");
            health.Die(null, AttackTypes.Nail, true); Flag("guest.enemy-killed");
            yield return Until(() => Has("host.dead-rejoined"), "host late death snapshot verified");
        }
        if (Has(role + ".failed")) yield break;
        Flag(role + ".map");
        if (host) {
            Flag("guest.down-request");
            yield return Until(() => runtime.Peers.Values.Any(p => p.State != null && p.State.Value == (int)HollowConnect.Core.Life.Down), "guest down arrived over network");
            var target = runtime.Peers.First(p => p.Value.Life == HollowConnect.Core.Life.Down).Key;
            var shadeService = Field<ShadeService>(runtime, "shades");
            yield return Until(() => shadeService.Get(target) != null, "native teammate shade created");
            var native = Resources.FindObjectsOfTypeAll<tk2dSpriteAnimator>().First(a => a.name == "Hollow Shade" && a.Library != null);
            var rendered = shadeService.Get(target).GetComponentInChildren<tk2dSpriteAnimator>();
            if (rendered.Library != native.Library || rendered.CurrentClip.name != "Idle" || !runtime.PlayerObject(target).GetComponent<Renderer>().forceRenderingOff) {
                Mark("FAIL native shade visual or hidden remote hero"); Flag(role + ".failed"); yield break;
            }
            Mark("PASS native shade Idle animation replaces remote hero");
            yield return Until(() => Has("guest.anchor-verified"), "death anchor and camera verified");
            yield return new WaitForSecondsRealtime(1);
            if (runtime.Peers[target].Life != HollowConnect.Core.Life.Down) {
                Mark("FAIL mere proximity revived shade"); Flag(role + ".failed"); yield break;
            }
            Mark("PASS mere proximity never revives shade");
            On.HitTaker.Hit += (orig, targetObject, hit, depth) => {
                if (targetObject.name.StartsWith("HollowConnect shade"))
                    Mark("SHADE HIT source=" + hit.Source.name + " parent=" + hit.Source.transform.parent?.name +
                        " local=" + hit.Source.transform.IsChildOf(HeroController.instance.transform) + " type=" + hit.AttackType + " dmg=" + hit.DamageDealt);
                orig(targetObject, hit, depth);
            };
            var shadeAnchor = shadeService.Get(target).transform.position;
            var hero = HeroController.instance; hero.transform.position = new Vector3(shadeAnchor.x - 2, shadeAnchor.y, hero.transform.position.z);
            hero.RegainControl(); hero.StartAnimationControl(); hero.GetComponent<Rigidbody2D>().gravityScale = 0;
            hero.FaceLeft(); hero.Attack(GlobalEnums.AttackDirection.normal);
            yield return new WaitForSecondsRealtime(.7f);
            if (runtime.Peers[target].Life != HollowConnect.Core.Life.Down) {
                Mark("FAIL missed swing revived shade"); Flag(role + ".failed"); yield break;
            }
            Mark("PASS missed nail swing never revives shade");
            hero.FaceRight(); hero.Attack(GlobalEnums.AttackDirection.normal); Physics2D.SyncTransforms();
            yield return new WaitForSecondsRealtime(.05f);
            Mark("SHADE ATTACK hero=" + hero.transform.position + " target=" + shadeAnchor + " body=" + hero.GetComponent<Rigidbody2D>().simulated +
                " collisionIgnored=" + Physics2D.GetIgnoreLayerCollision(11, 16) + " slashes=" +
                string.Join(";", hero.GetComponentsInChildren<Collider2D>().Where(c => c.name.Contains("Slash")).Select(c => c.name + ":" + c.enabled + ":" + c.bounds)));
            yield return Until(() => Has("guest.revived"), "real local nail breaks shade over network");
            yield return Until(() => shadeService.Get(target) == null && !runtime.PlayerObject(target).GetComponent<Renderer>().forceRenderingOff,
                "remote shade cleared and knight visible after revive");
            Freeze();
            Flag("guest.wipe-request");
            yield return Until(() => runtime.Peers.Values.Any(p => p.State != null && p.State.Value == (int)HollowConnect.Core.Life.Down), "guest down for team wipe");
            Die();
        } else {
            yield return Until(() => Has("guest.down-request"), "first down requested"); Freeze();
            HeroController.instance.transform.position += new Vector3(2, 0, 0);
            var deathAnchor = HeroController.instance.transform.position;
            var camera = GameCameras.instance.cameraController;
            var deathCamera = camera.transform.position; var previousMode = camera.mode;
            var hero = HeroController.instance;
            hero.takeNoDamage = false; hero.cState.invulnerable = hero.cState.recoiling = false;
            pd.SetBool("isInvincible", false); hero.SetDamageMode(GlobalEnums.DamageMode.FULL_DAMAGE);
            hero.TakeDamage(null, GlobalEnums.CollisionSide.left, 99, 1);
            yield return Until(() => runtime.Down, "lethal TakeDamage enters guest shade state");
            HeroController.instance.transform.position += new Vector3(8, 5, 0); camera.transform.position += new Vector3(8, 5, 0);
            yield return new WaitForSecondsRealtime(.6f);
            if (!runtime.Down || !AliveScene("Crossroads_01") || Vector3.Distance(HeroController.instance.transform.position, deathAnchor) > .001f ||
                Vector3.Distance(camera.transform.position, deathCamera) > .001f || camera.mode != CameraController.CameraMode.FROZEN ||
                HeroController.instance.GetComponent<Rigidbody2D>().simulated || !HeroController.instance.GetComponent<Renderer>().forceRenderingOff) {
                Mark("FAIL death anchor, camera freeze or ghost body"); Flag(role + ".failed"); yield break;
            }
            Mark("PASS shade stays at death anchor; camera frozen; body hidden without bench respawn"); Flag("guest.anchor-verified");
            yield return Until(() => !runtime.Down && pd.GetInt("health") == 1 && pd.GetInt("healthBlue") == 0,
                "guest rescued with exactly one health");
            if (Vector3.Distance(HeroController.instance.transform.position, deathAnchor) > .1f || camera.mode != previousMode ||
                !HeroController.instance.GetComponent<Rigidbody2D>().simulated || HeroController.instance.GetComponent<Renderer>().forceRenderingOff) {
                Mark("FAIL in-place revive or restored camera/body"); Flag(role + ".failed"); yield break;
            }
            Mark("PASS one-health revive restores hero and camera in place"); Flag("guest.revived");
            Freeze(); yield return Until(() => Has("guest.wipe-request"), "second down requested"); Die();
        }
        yield return Until(() => Hkmp.CoopContext.Epoch == 1, "single new attempt number");
        yield return Until(() => AliveScene("Town") && !runtime.Resetting && runtime.ReleasedRound == 1, "team checkpoint barrier released", 60);
        if (Has(role + ".failed")) yield break;
        testOrigin = new Vector2(20, 10); Freeze();
        if (host) {
            var dreamScene = pd.GetString("dreamGateScene"); var dreamX = pd.GetFloat("dreamGateX"); var dreamY = pd.GetFloat("dreamGateY");
            Enter("Crossroads_01"); yield return Until(() => AliveScene("Crossroads_01"), "host departed checkpoint for warp test");
            Freeze();
            yield return Until(() => runtime.Peers.Values.Any(p => p.State != null && p.State.Scene == "Town" && p.State.IsReady), "live target in another room");
            yield return new WaitForSecondsRealtime(.3f); // let the first post-transition presence reach the server
            var selected = false;
            runtime.KeyPressed = key => key == KeyCode.F7 && !selected && (selected = true);
            yield return Until(() => selected && runtime.Peers.ContainsKey(runtime.Selected), "F7 selects teammate");
            var pressed = false;
            runtime.KeyPressed = key => key == KeyCode.F8 && !pressed && (pressed = true);
            yield return Until(() => pressed && AliveScene("Town") && Vector2.Distance(HeroController.instance.transform.position, new Vector2(20, 10)) < 4,
                "F8 authorized cross-room teleport arrives beside teammate");
            runtime.KeyPressed = _ => false;
            if (Has(role + ".failed")) yield break;
            if (dreamScene != pd.GetString("dreamGateScene") || dreamX != pd.GetFloat("dreamGateX") || dreamY != pd.GetFloat("dreamGateY")) {
                Mark("FAIL teleport altered original dream gate"); Flag(role + ".failed"); yield break;
            }
            Mark("PASS original dream gate preserved"); Flag("host.warp-verified");
        } else yield return Until(() => Has("host.warp-verified"), "host teleport verified");
        if (Has(role + ".failed")) yield break;
        Freeze(); Flag(role + ".second-wipe-ready");
        yield return Until(() => Has(host ? "guest.second-wipe-ready" : "host.second-wipe-ready"), "both ready for reverse death order");
        if (host) { Die(); Flag("host.second-wipe-down"); }
        else {
            yield return Until(() => Has("host.second-wipe-down") && runtime.Peers.Values.Any(p => p.Life == HollowConnect.Core.Life.Down), "host down first arrived");
            Die();
        }
        yield return Until(() => Hkmp.CoopContext.Epoch == 2, "reverse order increments attempt once");
        yield return Until(() => AliveScene("Town") && !runtime.Resetting && runtime.ReleasedRound == 2, "reverse order retry releases both clients");
        if (Has(role + ".failed")) yield break;
        typeof(CoopRuntime).GetMethod("Receive", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(runtime,
            new object[] { new Message { Kind = Kind.Wipe, Session = runtime.Session, Round = 2, Scene = "Town", Key = "RestBench" } });
        yield return new WaitForSecondsRealtime(.4f);
        if (runtime.Resetting || Hkmp.CoopContext.Resetting || !AliveScene("Town")) {
            Mark("FAIL replayed completed wipe reopened checkpoint barrier"); Flag(role + ".failed"); yield break;
        }
        Mark("PASS replayed completed wipe is ignored after release");
        Freeze(); Flag(role + ".suite-complete");
        yield return Until(() => Has(host ? "guest.suite-complete" : "host.suite-complete"), "both ready for shade disconnect cleanup");
        if (host) {
            var previousMode = GameCameras.instance.cameraController.mode;
            Die(); yield return Until(() => runtime.Down, "host shade before disconnect"); Flag("host.disconnect-down");
            yield return Until(() => Has("guest.disconnect-shade-seen"), "guest observed host shade before disconnect");
            engine.ClientManager.Disconnect();
            yield return Until(() => !runtime.Connected && !runtime.Down && !HeroController.instance.GetComponent<Renderer>().forceRenderingOff &&
                HeroController.instance.GetComponent<Rigidbody2D>().simulated && GameCameras.instance.cameraController.mode == previousMode,
                "disconnect restores local camera and hero from shade");
        } else {
            var target = runtime.Peers.Keys.Single();
            var shades = Field<ShadeService>(runtime, "shades");
            yield return Until(() => Has("host.disconnect-down") && shades.Get(target) != null, "host shade visible before disconnect");
            Flag("guest.disconnect-shade-seen");
            yield return Until(() => !runtime.Peers.ContainsKey(target) && shades.Get(target) == null, "disconnect removes remote shade");
        }
        if (Has(role + ".failed")) yield break;
        Flag(role + ".shade-cleanup-done");
        yield return Until(() => Has(host ? "guest.shade-cleanup-done" : "host.shade-cleanup-done"), "both finished shade cleanup");
        if (host) {
            typeof(Hkmp.Game.Client.ClientManager).GetMethod("Connect", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(engine.ClientManager,
                new object[] { "127.0.0.1", 29650, "host" });
            yield return Until(() => runtime.Connected && runtime.Peers.Count == 1, "reconnect before transition cancellation");
            Flag("host.transition-ready");
            yield return Until(() => Has("guest.transition-ready"), "guest ready for transition cancellation");
            typeof(CoopRuntime).GetMethod("Receive", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(runtime,
                new object[] { new Message { Kind = Kind.Warp, Session = runtime.Session, Round = Hkmp.CoopContext.Epoch,
                    Serial = 100000, Scene = "Crossroads_01", X = 20, Y = 10 } });
            if (Field<Coroutine>(runtime, "teammateWarp") == null || !Field<bool>(runtime, "changingScene")) {
                Mark("FAIL warp cancellation fixture did not start transition"); Flag(role + ".failed"); yield break;
            }
            engine.ClientManager.Disconnect();
            yield return Until(() => AliveScene("Crossroads_01"), "native scene load finishes after warp disconnect");
        } else {
            yield return Until(() => runtime.Peers.Count == 1 && Has("host.transition-ready"), "host reconnected before transition cancellation");
            Flag("guest.transition-ready");
            typeof(CoopRuntime).GetMethod("Receive", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(runtime,
                new object[] { new Message { Kind = Kind.Wipe, Session = runtime.Session, Round = Hkmp.CoopContext.Epoch + 1, Scene = "Town", Key = "RestBench" } });
            if (Field<Coroutine>(runtime, "checkpointReload") == null || !runtime.Resetting) {
                Mark("FAIL checkpoint cancellation fixture did not start transition"); Flag(role + ".failed"); yield break;
            }
            engine.ClientManager.Disconnect();
            yield return Until(() => AliveScene("Town"), "native checkpoint load finishes after disconnect");
        }
        yield return new WaitForSecondsRealtime(.5f);
        if (runtime.Connected || runtime.Resetting || Hkmp.CoopContext.Resetting || Field<bool>(runtime, "changingScene") ||
            Field<Coroutine>(runtime, "checkpointReload") != null || Field<Coroutine>(runtime, "teammateWarp") != null ||
            Field<Vector2?>(runtime, "warpEntry").HasValue || Field<HeroController>(runtime, "lockedHero") != null ||
            !HeroController.instance.GetComponent<Rigidbody2D>().simulated || HeroController.instance.GetComponent<Renderer>().forceRenderingOff) {
            Mark("FAIL disconnected transition retained a coroutine or hero lock"); Flag(role + ".failed"); yield break;
        }
        Mark("PASS disconnect cancels pending " + (host ? "warp" : "checkpoint") + " and restores hero");
        yield return ProbeShutdown.Run(engine,root,role);
        Mark("FINAL PASS real UDP: enemy health/position/invincibility, remote nail, authority transfer, late death, shared ability, map projection/icon, F6 personal bench return for guest and scene authority without combat reset, native shade nail rescue, both wipe orders and F7/F8 warp");
        Flag(role + ".done");
    }

    private IEnumerator AuthorityFixture(EntityManager manager, Dictionary<ulong, Entity> entities, ulong survivorId) {
        if (host) {
            foreach (var e in entities.Values) Mark("ENTITY " + e.Id + " " + e.Type + " " +
                (e.Object.Host == null ? "destroyed" : e.Object.Host.name + " active=" + e.Object.Host.activeInHierarchy));
            var candidates = entities.Values.Where(e => e.Id != survivorId && e.Object.Host != null &&
                e.Object.Host.activeInHierarchy && e.Object.Host.GetComponent<HealthManager>() != null &&
                e.Object.Host.GetComponent<HealthManager>().hp > 0).ToArray();
            var mover = candidates.First(e => e.Type == EntityType.WanderingHusk);
            var victim = candidates.First(e => e.Id != mover.Id);
            Flag("authority.ids", victim.Id + "," + mover.Id);
            victim.Object.Host.GetComponent<HealthManager>().Die(null, AttackTypes.Nail, true);
            yield return Until(() => Has("guest.dead-client-removed"), "replica of killed enemy removed before transfer");
            var flying = mover.Object.Host;
            foreach (var fsm in flying.GetComponents<PlayMakerFSM>()) {
                Mark("LIVE AI " + fsm.FsmName + " states=" + string.Join(",", fsm.FsmStates.Select(s => s.Name)));
                foreach(var s in fsm.FsmStates) Mark("TRANSFER TEMPLATE "+s.Name+" "+string.Join(";",s.Actions.Select(a=>a.GetType().Name+" enabled="+a.Enabled+" continuous="+Hkmp.Game.Client.Entity.Action.ActionRegistry.IsActionContinuous(a)+" fields="+string.Join(",",a.GetType().GetFields(BindingFlags.Instance|BindingFlags.Public).Select(f=>f.Name+"="+f.GetValue(a))))));
            }
            HeroController.instance.transform.position = flying.transform.position + new Vector3(2, 0, 0);
            var starting = flying.transform.position;
            yield return Until(() => Vector2.Distance(flying.transform.position, starting) > .3f, "living enemy AI moves before transfer", 8);
            if (Has(role + ".failed")) yield break;
            Enter("Town"); yield return Until(() => AliveScene("Town"), "authority leaves after aggro and enemy death"); Freeze();
            Flag("host.transfer-left");
            yield return Until(() => Has("guest.transfer-alive"), "survivor continues AI and combat after transfer");
            Enter("Crossroads_01"); yield return Until(() => AliveScene("Crossroads_01"), "original authority returns to ongoing room"); Freeze();
            var expected = int.Parse(File.ReadAllText(Path.Combine(root, "authority.survivor-hp")));
            yield return Until(() => entities[survivorId].Object.Client.GetComponent<HealthManager>().hp == expected,
                "returning authority receives continued damage");
        } else {
            yield return Until(() => Has("authority.ids"), "authority fixture ready");
            var ids = File.ReadAllText(Path.Combine(root, "authority.ids")).Split(',');
            var victimId = ulong.Parse(ids[0]); var moverId = ulong.Parse(ids[1]);
            yield return Until(() => entities[victimId].Object.Client == null || !entities[victimId].Object.Client.activeInHierarchy,
                "killed enemy death replicated");
            // Exercise normal Unity destruction after the terminal death, as
            // seen in the user's log; the server still retains its snapshot.
            if (entities[victimId].Object.Client != null) Object.Destroy(entities[victimId].Object.Client);
            yield return null; Flag("guest.dead-client-removed");
            yield return Until(() => Has("host.transfer-left"), "old authority left room");
            yield return Until(() => manager.IsSceneHost, "authority transfer completes despite destroyed enemy", 8);
            if (Has(role + ".failed")) yield break;
            var live = entities[moverId].Object.Host;
            var initial = live.transform.position;
            foreach(var f in live.GetComponents<PlayMakerFSM>())Mark("TRANSFER FSM "+f.FsmName+"="+f.ActiveStateName+" enabled="+f.enabled+" active="+f.Fsm.Active+" floats="+string.Join(",",f.FsmVariables.FloatVariables.Select(v=>v.Name+"="+v.Value))+" objects="+string.Join(",",f.FsmVariables.GameObjectVariables.Select(v=>v.Name+"="+(v.Value==null?"null":v.Value.name))));
            var rb=live.GetComponent<Rigidbody2D>();Mark("TRANSFER BODY "+rb.bodyType+" gravity="+rb.gravityScale+" velocity="+rb.velocity+" pos="+live.transform.position+" hero="+HeroController.instance.transform.position);
            // The earlier nail fixture leaves this hero far above the floor.
            // Stimulate the resumed native AI with a nearby live target rather
            // than requiring an idle, out-of-range enemy to move by chance.
            HeroController.instance.transform.position = live.transform.position + new Vector3(-2, 0, 0);
            Physics2D.SyncTransforms();
            var resumedFsm=live.GetComponents<PlayMakerFSM>().First();var resumedState=resumedFsm.ActiveStateName;var progressed=false;
            var diagnosticEnd=Time.realtimeSinceStartup+8;var diagnosticNext=0f;
            while(Time.realtimeSinceStartup<diagnosticEnd && !progressed) {
                progressed=resumedFsm.ActiveStateName!=resumedState;
                if(Time.realtimeSinceStartup>=diagnosticNext){diagnosticNext=Time.realtimeSinceStartup+1;
                    var anim=live.GetComponent<tk2dSpriteAnimator>();
                    Mark("TRANSFER TICK pos="+live.transform.position+" velocity="+rb.velocity+" simulated="+rb.simulated+" anim="+anim.enabled+"/"+anim.CurrentClip?.name+"/"+anim.CurrentClip?.wrapMode+"/playing="+anim.Playing+" paused="+anim.Paused+" states="+string.Join(";",live.GetComponents<PlayMakerFSM>().Select(f=>f.FsmName+"="+f.ActiveStateName+" actions="+string.Join(",",f.Fsm.ActiveState.ActiveActions.Select(a=>a.GetType().Name+"/"+a.Enabled)))));
                }yield return null;
            }
            if(!progressed){Mark("FAIL resumed FSM did not progress");Flag(role+".failed");yield break;}
            Mark("PASS resumed FSM progresses from "+resumedState+" to "+resumedFsm.ActiveStateName);
            yield return Until(() => live.activeInHierarchy && Vector2.Distance(live.transform.position, initial) > .3f,
                "living enemy AI continues moving after authority transfer", 1);
            if (Has(role + ".failed")) yield break;
            var survivor = entities[survivorId].Object.Host.GetComponent<HealthManager>();
            survivor.ApplyExtraDamage(1); Flag("authority.survivor-hp", survivor.hp.ToString());
            if (entities[victimId].Object.Host != null && entities[victimId].Object.Host.activeInHierarchy) {
                Mark("FAIL dead enemy resurrected on authority transfer"); Flag(role + ".failed"); yield break;
            }
            Mark("PASS dead enemy remains dead while survivors continue"); Flag("guest.transfer-alive");
        }
        if (Has(role + ".failed")) yield break;
        Mark("FINAL PASS authority transfer after aggro and destroyed enemy"); Flag(role + ".done");
    }
}
