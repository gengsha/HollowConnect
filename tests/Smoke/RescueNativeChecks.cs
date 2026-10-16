using System;
using System.Collections;
using System.Linq;
using HollowConnect;
using HollowConnect.Core;
using UnityEngine;
using Object = UnityEngine.Object;

namespace HollowConnect.Smoke;

public sealed partial class RescueProbe {
    private IEnumerator NativeRescueChecks() {
        var hero = HeroController.instance;
        var pd = PlayerData.instance;
        var body = hero.GetComponent<Rigidbody2D>();
        var other = host ? "guest" : "host";
        var protectionBaseline = Environment.GetEnvironmentVariable("HOLLOWCONNECT_RESCUE_NATIVE_CHECK") == "protection";
        UnityEngine.SceneManagement.SceneManager.sceneLoaded += (loaded, mode) => Log("NATIVE SCENE " + loaded.name + " " + mode);
        On.GameManager.BeginSceneTransition += (orig, gm, info) => {
            Log("NATIVE TRANSITION " + info.SceneName + "/" + info.EntryGateName + " from=" + gm.sceneName + " hero=" + hero.transform.position + " " + Environment.StackTrace);
            orig(gm, info);
        };
        IEnumerator Together(string step) {
            Flag(role + ".native." + step);
            yield return Until(() => Has(other + ".native." + step), "both " + step);
        }
        if (pd.GetBool("atBench")) {
            var bench = Object.FindObjectsOfType<PlayMakerFSM>().Single(f => f.name == "RestBench" && f.FsmName == "Bench Control");
            yield return Until(() => bench.ActiveStateName == "Resting", "native starting bench ready");
            bench.Fsm.Event(bench.Fsm.ActiveState.Actions.OfType<HutongGames.PlayMaker.Actions.ListenForJump>().Single().wasPressed);
            yield return Until(() => !pd.GetBool("atBench") && hero.acceptingInput && !body.isKinematic, "native starting bench get-up");
        }
        foreach (var scene in new[] { "Crossroads_01", "GG_Vengefly", "Dream_02_Mage_Lord" }) {
            GameManager.instance.BeginSceneTransition(new GameManager.SceneLoadInfo {
                SceneName = scene, EntryGateName = scene.StartsWith("GG_") ? "door_dreamEnter" : scene.StartsWith("Dream_") ? "door1" : "left1",
                Visualization = GameManager.SceneLoadVisualizations.Default, WaitForSceneTransitionCameraFade = true
            });
            yield return Until(() => runtime.Playable && GameManager.instance.sceneName == scene && hero.acceptingInput &&
                !hero.cState.transitioning && engine.ClientManager.SceneSnapshotReady, "native rescue room ready " + scene);
            hero.takeNoDamage = true;
            var entry = hero.FindGroundPoint(pd.GetVector3("hazardRespawnLocation"), true);
            hero.transform.position = entry + Vector3.up * .1f;
            hero.AffectedByGravity(true); Physics2D.SyncTransforms();
            yield return Until(() => hero.cState.onGround, "native entry support " + scene);
            entry = hero.transform.position;
            foreach (var victimRole in new[] { "guest", "host" }) {
                var victim = role == victimRole;
                var step = scene + "." + victimRole;
                var missingCheckpoint = scene == "Dream_02_Mage_Lord" && victimRole == "host";
                hero.takeNoDamage = true; hero.cState.invulnerable = false; hero.cState.recoiling = false;
                pd.SetInt("health", 5); pd.SetInt("healthBlue", 0); pd.SetBool("isInvincible", false);
                pd.SetInt("geo", 321); pd.SetInt("geoPool", 67); pd.SetString("shadeScene", "Crossroads_10");
                pd.SetFloat("shadePositionX", 9); pd.SetFloat("shadePositionY", 13);
                hero.transform.position = entry; body.velocity = Vector2.zero;
                hero.SetHazardRespawn(missingCheckpoint ? Vector3.zero : entry, true);
                hero.AffectedByGravity(true); Physics2D.SyncTransforms();
                yield return Together(step + ".ready");
                if (victim) {
                    // Ordinary lethal hit in the air, independent of hazard type.
                    hero.transform.position = entry + new Vector3(4, 3, 0); Physics2D.SyncTransforms();
                    var origin = hero.transform.position;
                    var zone = GameManager.instance.GetCurrentMapZone();
                    var marker = GameObject.FindGameObjectsWithTag("Shade Marker").Where(g => g.scene.name == scene)
                        .OrderBy(g => (g.transform.position - origin).sqrMagnitude).FirstOrDefault();
                    Log("NATIVE RESCUE " + step + " zone=" + zone + " origin=" + origin + " entry=" + entry + " marker=" + (marker == null ? "none" : marker.transform.position.ToString()));
                    if (marker != null) {
                        var hit = Physics2D.Raycast(marker.transform.position, Vector2.down, 50, 1 << 8);
                        Log("MARKER GROUND " + step + " collider=" + hit.collider + " point=" + hit.point + " normal=" + hit.normal + " distance=" + hit.distance + " trigger=" + (hit.collider != null && hit.collider.isTrigger));
                        if (hit.collider != null && hit.distance > 0) {
                            var ground = hero.FindGroundPoint(marker.transform.position, true);
                            var args = new object[] { (Vector2)ground, scene, null };
                            var supported = typeof(CoopRuntime).GetMethod("TryWarpLanding", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static).Invoke(null, args);
                            Log("MARKER SUPPORT " + step + " native=" + ground + " valid=" + supported + " landing=" + args[2]);
                        }
                    }
                    hero.takeNoDamage = false; pd.SetInt("health", 1);
                    hero.TakeDamage(null, GlobalEnums.CollisionSide.left, 1, 1);
                    yield return Until(() => runtime.Down, "ordinary airborne lethal hit " + step);
                    Log("NATIVE ANCHOR " + step + " position=" + runtime.DeathPosition);
                    if (!protectionBaseline) {
                        Check(Vector2.Distance(runtime.DeathPosition, origin) > 1, "ordinary rescue leaves airborne death point " + step);
                        if (scene == "Crossroads_01") {
                            Check(marker != null, "story room has real native shade marker " + step);
                            // A shade can float over a ledge; the knight's full
                            // body may need the existing landing search's 3-unit
                            // clearance. Require this marker's area plus actual
                            // supported standing below, rather than exact art X.
                            Check(Math.Abs(runtime.DeathPosition.x - marker.transform.position.x) <= 3.05f,
                                "story rescue uses nearest native marker supported area " + step);
                        } else {
                            Check(zone == "GODS_GLORY" || zone == "DREAM_WORLD", "real special map-zone branch " + step);
                            if (missingCheckpoint)
                                Check(Math.Abs(runtime.DeathPosition.x - origin.x) < .2f && runtime.DeathPosition.y < origin.y - 1,
                                    "missing dream checkpoint falls back to supported death location " + step);
                            else Check(Vector2.Distance(runtime.DeathPosition, entry) < .2f, "dream rescue uses native local checkpoint " + step);
                        }
                    }
                    yield return Until(() => !runtime.Down && pd.health == 1, "one-health native rescue " + step);
                    Check(hero.cState.invulnerable, "rescue immediately grants native invulnerability " + step);
                    var started = Time.time;
                    hero.takeNoDamage = false;
                    hero.TakeDamage(null, GlobalEnums.CollisionSide.left, 1, 1);
                    Check(!runtime.Down && pd.health == 1, "normal attack cannot kill freshly rescued knight " + step);
                    hero.takeNoDamage = true;
                    var sprite = hero.GetComponent<tk2dSprite>(); var normal = sprite.color;
                    yield return new WaitForSeconds(hero.DAMAGE_FREEZE_DOWN + .15f);
                    Check(Field<bool>(hero.GetComponent<InvulnerablePulse>(), "pulsing") && sprite.color != normal, "native rescue pulse changes sprite colour " + step);
                    // Duplicate acceptance must not extend immunity.
                    Inject(new Message { Kind = Kind.Revived, Id = runtime.LocalId, Target = runtime.Peers.Single().Key, Serial = runtime.DeathSerial, Scene = scene });
                    yield return Until(() => !hero.cState.invulnerable, "rescue immunity expires " + step, hero.INVUL_TIME + 1);
                    Check(Time.time - started < hero.INVUL_TIME + hero.DAMAGE_FREEZE_DOWN + .35f, "duplicate rescue cannot renew native protection " + step);
                    Check(!Field<bool>(hero.GetComponent<InvulnerablePulse>(), "pulsing"), "native pulse stops with immunity " + step);
                    yield return Until(() => hero.cState.onGround && hero.acceptingInput, "rescued knight stands on native support " + step);
                    Check(!hero.GetComponent<Renderer>().forceRenderingOff, "rescued knight is visible " + step);
                    pd.SetInt("health", 2); hero.takeNoDamage = false;
                    // Recoil into the platform, so the expiry test does not
                    // deliberately throw the actor out of the dream arena.
                    hero.TakeDamage(null, GlobalEnums.CollisionSide.right, 1, 1);
                    Check(pd.health == 1 && !runtime.Down, "normal damage works after protection expires " + step);
                    hero.takeNoDamage = true;
                    yield return Until(() => !hero.cState.invulnerable && !hero.cState.recoiling, "native test hit finishes " + step);
                    yield return Until(() => hero.cState.onGround && !GameManager.instance.IsInSceneTransition,
                        "native expiry hit settles on support " + step);
                    Check(pd.GetInt("geo") == 321 && pd.GetInt("geoPool") == 67 && pd.GetString("shadeScene") == "Crossroads_10" &&
                        pd.GetFloat("shadePositionX") == 9 && pd.GetFloat("shadePositionY") == 13, "rescue preserves earlier personal shade and geo " + step);
                } else {
                    yield return Until(() => runtime.Peers.Values.Any(p => p.Life == Life.Down), "ordinary teammate down received " + step);
                    var peer = runtime.Peers.Single(); var service = Field<ShadeService>(runtime, "shades");
                    yield return Until(() => service.Get(peer.Key) != null, "native rescue shade visible " + step);
                    var shade = service.Get(peer.Key).transform.position;
                    var stand = new[] { shade + Vector3.right * 2, shade + Vector3.left * 2 }.First(p => {
                        var support = Physics2D.Raycast(p, Vector2.down, 8, 1 << 8);
                        return support.collider != null && support.normal.y > .5f && !support.collider.isTrigger;
                    });
                    Log("RESCUER SUPPORT " + step + " position=" + stand);
                    hero.transform.position = stand; body.velocity = Vector2.zero;
                    if (stand.x > shade.x) hero.FaceLeft(); else hero.FaceRight();
                    hero.Attack(GlobalEnums.AttackDirection.normal); Physics2D.SyncTransforms();
                    yield return Until(() => runtime.Peers[peer.Key].Life == Life.Alive, "actual nail rescues ordinary death " + step);
                    var avatar = runtime.PlayerObject(peer.Key);
                    yield return Until(() => avatar != null && avatar.GetComponent<InvulnerablePulse>() != null &&
                        Field<bool>(avatar.GetComponent<InvulnerablePulse>(), "pulsing"), "teammate sees native rescue flash " + step);
                    var normal = avatar.GetComponent<tk2dSprite>().color;
                    yield return new WaitForSeconds(.12f);
                    Check(avatar.GetComponent<tk2dSprite>().color != normal, "remote rescue pulse actually animates " + step);
                    yield return Until(() => avatar.GetComponent<InvulnerablePulse>() == null, "remote pulse cleaned up " + step);
                    Check(!avatar.GetComponent<Renderer>().forceRenderingOff, "remote rescued knight remains visible " + step);
                }
                yield return Together(step + ".complete");
                Log("NATIVE ROOM " + step + " actual=" + GameManager.instance.sceneName + " hero=" + hero.transform.position);
                Check(GameManager.instance.sceneName == scene, "native rescue stays in current room " + step);
                // The expiry check deliberately applies real recoil beside a
                // ledge. Park both test actors on the native entry support so
                // that recoil cannot cause a later, unrelated dream fall.
                hero.transform.position = entry; body.velocity = Vector2.zero;
                Physics2D.SyncTransforms();
                yield return Together(step + ".parked");
            }
        }
    }
}
