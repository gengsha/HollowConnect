using System;
using System.Collections;
using System.Linq;
using System.Reflection;
using Hkmp.Game.Client.Entity;
using HollowConnect;
using UnityEngine;

namespace HollowConnect.Smoke;

public sealed partial class CoverageProbe {
    private IEnumerator WarpAssertions(int round) {
        var hero = HeroController.instance;
        var body = hero.GetComponent<Rigidbody2D>();
        var scene = GameManager.instance.sceneName;
        DreamPlatform platform = null;
        Collider2D FindFloor() {
            if (scene == "Dream_Nailcollection") {
                platform = UnityEngine.Object.FindObjectsOfType<DreamPlatform>().Single(p =>
                    p.gameObject.scene.name == scene && p.name == "dream_plat_small");
                return platform.GetComponentsInChildren<Collider2D>().First(c => !c.isTrigger && c.enabled);
            }
            var found = UnityEngine.Object.FindObjectsOfType<Collider2D>().SingleOrDefault(c => c.gameObject.scene.name == scene && c.name == "fung_plat_float_12");
            if (found == null) throw new Exception("Warp fixture missing native platform in " + scene +
                "; active=" + UnityEngine.SceneManagement.SceneManager.GetActiveScene().name + "; gm=" + GameManager.instance.sceneName +
                "; transition=" + GameManager.instance.IsInSceneTransition + "; snapshot=" + engine.ClientManager.SceneSnapshotReady +
                "; candidates=" + string.Join(",", UnityEngine.Object.FindObjectsOfType<Collider2D>(true)
                    .Where(c => c.name == "fung_plat_float_12").Select(c => c.name + "@" + c.gameObject.scene.name +
                        "/active=" + c.gameObject.activeInHierarchy + "/enabled=" + c.enabled)));
            return found;
        }
        var floor = FindFloor();
        var other = host ? "guest" : "host";
        bool Can(string method) => (bool)typeof(HeroController).GetMethod(method, BindingFlags.Instance | BindingFlags.NonPublic).Invoke(hero, null);
        string State() => "scene=" + GameManager.instance.sceneName + " pos=" + hero.transform.position + " velocity=" + body.velocity +
            " floor=" + (floor == null ? "missing" : floor.bounds.ToString()) +
            " gravity=" + body.gravityScale + " cachedGravity=" + Field<float>(hero, "prevGravityScale") + " body=" + body.bodyType + "/" + body.simulated +
            " ground=" + hero.cState.onGround + " input=" + hero.acceptingInput + " relinquished=" + hero.controlReqlinquished +
            " hazard=" + hero.cState.hazardDeath + "/" + hero.cState.hazardRespawning + " state=" + hero.hero_state +
            " map=" + hero.CanQuickMap() + " pauseDisabled=" + PlayerData.instance.GetBool("disablePause") +
            " hazardPoint=" + PlayerData.instance.GetVector3("hazardRespawnLocation");
        void Move(Vector3 position) { Freeze(); hero.transform.position = position; Physics2D.SyncTransforms(); }
        IEnumerator Check(Func<bool> ready, string label, float seconds = 12) {
            var deadline = Time.realtimeSinceStartup + seconds;
            while (!ready() && Time.realtimeSinceStartup < deadline) yield return null;
            if (!ready()) throw new Exception("Warp " + label + " " + State());
            Mark("PASS warp " + label);
        }
        IEnumerator Together(string label) {
            var key = ".warp." + round + "." + label;
            Flag(role + key); yield return Check(() => Has(other + key), "both " + label, 20);
        }
        IEnumerator Land() {
            floor = FindFloor();
            Move(new Vector3(floor.bounds.center.x, floor.bounds.max.y + 3, hero.transform.position.z));
            hero.cState.onGround = false; hero.AffectedByGravity(true);
            yield return Check(() => {
                // Native/client platform roles can swap while the other actor
                // leaves for Town. Reacquire the collider used by this peer.
                floor = FindFloor();
                return hero.cState.onGround && Math.Abs(hero.GetComponent<Collider2D>().bounds.min.y - floor.bounds.max.y) < .4f;
            },
                "target naturally stands on narrow native platform");
            Freeze();
        }
        yield return Check(() => hero.acceptingInput && !hero.cState.transitioning && engine.ClientManager.SceneSnapshotReady,
            "native entry and snapshot ready");
        Move((platform != null ? platform.innerCollider.GetComponent<Collider2D>().bounds.center : floor.bounds.center) + Vector3.up * 4);
        if (platform != null) yield return Check(() => Field<bool>(platform, "visible"), "platform appears through native proximity");
        yield return Together("visible");
        Mark("WARP PLATFORM " + floor.bounds);
        foreach (var actor in new[] { "host", "guest" }) {
          var attempts = Environment.GetEnvironmentVariable("HOLLOWCONNECT_WARP_UNSUPPORTED") == "1" ? 5 : 3;
          for (var repeat = 0; repeat < attempts; repeat++) {
            var step = actor + "-" + repeat;
            if (role != actor) {
                if (repeat < 3) yield return Land();
                else Move(new Vector3(floor.bounds.center.x, floor.bounds.max.y + 150, hero.transform.position.z));
            } else if ((repeat == 0 || repeat == 4) && Environment.GetEnvironmentVariable("HOLLOWCONNECT_WARP_CROSS_ROOM") == "1") {
                GameManager.instance.BeginSceneTransition(new GameManager.SceneLoadInfo {
                    SceneName = "Town", EntryGateName = "left1", Visualization = GameManager.SceneLoadVisualizations.Default,
                    WaitForSceneTransitionCameraFade = true
                });
                yield return Check(() => In("Town") && hero.acceptingInput && !hero.cState.transitioning, actor + " starts in another room");
                Freeze();
            } else Move(new Vector3(floor.bounds.center.x + 5, floor.bounds.max.y + 4, hero.transform.position.z));
            yield return Together(step + "-ready");
            yield return new WaitForSecondsRealtime(.5f);
            if (role == actor) {
                runtime.Selected = runtime.Peers.Keys.Single();
                var before = Field<uint>(runtime, "handledWarp");
                var originalPosition = hero.transform.position;
                var originalCheckpoint = PlayerData.instance.GetVector3("hazardRespawnLocation");
                var pressed = false;
                runtime.KeyPressed = key => {
                    if (key != KeyCode.F8 || pressed) return false;
                    pressed = true;
                    if (repeat != 3) { hero.AffectedByGravity(true); hero.takeNoDamage = false; }
                    return true;
                };
                yield return Check(() => pressed && Field<uint>(runtime, "handledWarp") > before, actor + " F8 accepted over real UDP");
                runtime.KeyPressed = _ => false;
                yield return Check(() => !Field<bool>(runtime, "changingScene"), actor + " relocation completed");
                Mark("WARP RELOCATED " + State() + " active=" + UnityEngine.SceneManagement.SceneManager.GetActiveScene().name +
                    " loaded=" + UnityEngine.SceneManagement.SceneManager.GetSceneByName(scene).isLoaded +
                    " transition=" + GameManager.instance.IsInSceneTransition + " snapshot=" + engine.ClientManager.SceneSnapshotReady);
                yield return Check(() => In(scene) && !hero.cState.transitioning && engine.ClientManager.SceneSnapshotReady,
                    actor + " native destination and snapshot ready");
                floor = FindFloor();
                Mark("WARP ARRIVAL " + State());
                yield return new WaitForSecondsRealtime(1);
                Mark("WARP SETTLED " + State());
                if (repeat >= 3) {
                    if (repeat == 3) {
                        if (Vector2.Distance(originalPosition, hero.transform.position) > .1f ||
                            PlayerData.instance.GetVector3("hazardRespawnLocation") != originalCheckpoint)
                            throw new Exception("Unsupported same-room warp moved player or checkpoint " + State());
                        Mark("PASS warp unsupported same-room destination preserves player and checkpoint");
                    } else {
                        yield return Check(() => hero.cState.onGround && hero.acceptingInput && hero.CanQuickMap() &&
                            !hero.cState.hazardDeath && !hero.cState.hazardRespawning, "unsupported cross-room destination uses playable native entrance");
                        if (Vector2.Distance(hero.transform.position, floor.bounds.center + Vector3.up * 150) < 3)
                            throw new Exception("Unsupported cross-room warp retained unsupported destination " + State());
                    }
                } else {
                if (Environment.GetEnvironmentVariable("HOLLOWCONNECT_WARP_DIAGNOSTIC") == "1") {
                    for (var sample = 0; sample < 12; sample++) {
                        yield return new WaitForSecondsRealtime(.5f);
                        Mark("WARP DIAGNOSTIC " + State());
                    }
                }
                if (!hero.cState.onGround || Math.Abs(hero.GetComponent<Collider2D>().bounds.min.y - floor.bounds.max.y) > .4f)
                    throw new Exception("Warp placed player off narrow platform " + State());
                if (!hero.CanQuickMap() || !Can("CanAttack") || !Can("CanJump") || !hero.acceptingInput)
                    throw new Exception("Warp left native map/action eligibility blocked " + State());
                Mark("PASS warp grounded with native map, attack and jump available " + actor);
                if (repeat == 2 && scene == "Fungus1_02") {
                    // Step off the actual platform into native acid. The new
                    // checkpoint must recover naturally, even if F8 is pressed
                    // or a previously accepted reply arrives during recovery.
                    hero.takeNoDamage = false;
                    hero.transform.position += Vector3.right * 1.5f;
                    hero.AffectedByGravity(true); Physics2D.SyncTransforms();
                    yield return Check(() => hero.cState.hazardDeath, "native acid death begins");
                    var requestBefore = Field<uint>(runtime, "warpSerial");
                    var handledBefore = Field<uint>(runtime, "handledWarp");
                    pressed = false;
                    runtime.KeyPressed = key => key == KeyCode.F8 && !pressed && (pressed = true);
                    typeof(CoopRuntime).GetMethod("Receive", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(runtime, new object[] {
                        new Message { Kind = Kind.Warp, Session = runtime.Session, Round = Hkmp.CoopContext.Epoch,
                            Serial = handledBefore + 100000, Target = runtime.Selected, Scene = scene, X = floor.bounds.center.x, Y = floor.bounds.max.y + 2 }
                    });
                    yield return Check(() => pressed, "F8 attempted during actual acid recovery");
                    runtime.KeyPressed = _ => false;
                    if (Field<uint>(runtime, "warpSerial") != requestBefore || Field<uint>(runtime, "handledWarp") != handledBefore)
                        throw new Exception("Warp interrupted native hazard recovery " + State());
                    Mark("PASS warp request and injected delayed reply refused during native acid recovery");
                    yield return Check(() => !hero.cState.hazardDeath && !hero.cState.hazardRespawning && hero.cState.onGround &&
                        hero.acceptingInput && !hero.controlReqlinquished && hero.CanQuickMap(), "acid recovery returns to supported checkpoint");
                    if (Math.Abs(hero.GetComponent<Collider2D>().bounds.min.y - floor.bounds.max.y) > .4f)
                        throw new Exception("Acid checkpoint missed original platform " + State());
                    for (var jump = 0; jump < 2; jump++) {
                        typeof(HeroController).GetMethod("HeroJump", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(hero, null);
                        typeof(HeroController).GetMethod("Jump", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(hero, null);
                        if (body.velocity.y <= 0) throw new Exception("Native jump did not rise after acid recovery");
                        yield return Check(() => body.velocity.y < -1, "native jump falls after acid recovery");
                        yield return Check(() => hero.cState.onGround && hero.CanQuickMap(), "native jump lands with map available");
                    }
                }
                }
                Freeze();
            }
            yield return Together(step + "-arrived");
            yield return new WaitForSecondsRealtime(3.1f);
          }
        }
    }
}
