using System;
using System.Collections;
using System.Linq;
using UnityEngine;
using Object = UnityEngine.Object;

namespace HollowConnect.Smoke;

public sealed partial class RescueProbe {
    private static string EdgeScene => UnityEngine.SceneManagement.SceneManager.GetActiveScene().name;
    private IEnumerator EdgeRescueChecks() {
        var hero = HeroController.instance;
        var body = hero.GetComponent<Rigidbody2D>();
        hero.takeNoDamage = true;
        if (PlayerData.instance.GetBool("atBench")) {
            var bench = Object.FindObjectsOfType<PlayMakerFSM>().Single(f => f.name == "RestBench" && f.FsmName == "Bench Control");
            yield return Until(() => bench.ActiveStateName == "Resting", "edge starting bench ready");
            bench.Fsm.Event(bench.Fsm.ActiveState.Actions.OfType<HutongGames.PlayMaker.Actions.ListenForJump>().Single().wasPressed);
            yield return Until(() => !PlayerData.instance.GetBool("atBench") && hero.acceptingInput && !body.isKinematic, "edge bench get-up");
        }
        var mode = Environment.GetEnvironmentVariable("HOLLOWCONNECT_RESCUE_EDGE_CHECK");
        var rooms = mode == "exception-placement" || mode == "room-placement" ? new[] { Environment.GetEnvironmentVariable("HOLLOWCONNECT_DEATH_SCENE") } :
            mode.StartsWith("faults") || mode == "native-lift" ? new[] { "White_Palace_01" } :
            new[] { "White_Palace_01", "White_Palace_18", "White_Palace_19", "White_Palace_20", "White_Palace_17" };
        foreach (var scene in rooms) {
            GameManager.instance.BeginSceneTransition(new GameManager.SceneLoadInfo {
                SceneName = scene, EntryGateName = scene.StartsWith("Dream_") ? "door1" : scene == "White_Palace_18" ? "top1" : scene == "White_Palace_20" ? "bot1" : scene == "White_Palace_17" ? "right1" : "left1",
                Visualization = scene.StartsWith("Dream_") ? GameManager.SceneLoadVisualizations.Dream : GameManager.SceneLoadVisualizations.Default,
                WaitForSceneTransitionCameraFade = true
            });
            yield return Until(() => runtime.Playable && EdgeScene == scene && hero.acceptingInput &&
                !hero.cState.transitioning && hero.transitionState == GlobalEnums.HeroTransitionState.WAITING_TO_TRANSITION,
                "edge native entrance complete " + scene);
            hero.takeNoDamage = true; hero.AffectedByGravity(false); body.velocity = Vector2.zero;
            yield return Until(() => engine.ClientManager.SceneSnapshotReady && EdgeScene == scene,
                "edge room ready " + scene);
            Log("EDGE ROOM " + scene + " zone=" + GameManager.instance.GetCurrentMapZone() + " hero=" + hero.transform.position +
                " checkpoint=" + PlayerData.instance.GetVector3("hazardRespawnLocation") + " dreamReturn=" + PlayerData.instance.GetString("dreamReturnScene"));
            foreach (var gate in Object.FindObjectsOfType<TransitionPoint>().Where(g => g.gameObject.scene.name == scene))
                Log("EDGE GATE " + scene + " " + gate.name + "=" + gate.transform.position);
            foreach (var marker in GameObject.FindGameObjectsWithTag("Shade Marker").Where(g => g.scene.name == scene))
                Log("EDGE MARKER " + scene + " " + marker.name + "=" + marker.transform.position);
            foreach (var point in Object.FindObjectsOfType<HazardRespawnMarker>().Where(g => g.gameObject.scene.name == scene).Take(12))
                Log("EDGE CHECKPOINT " + scene + " " + point.name + "=" + point.transform.position);
            foreach (var platform in Object.FindObjectsOfType<Collider2D>().Where(c => c.gameObject.scene.name == scene && !c.isTrigger &&
                (c.name.IndexOf("plat", StringComparison.OrdinalIgnoreCase) >= 0 || c.name.IndexOf("lift", StringComparison.OrdinalIgnoreCase) >= 0)).Take(20))
                Log("EDGE PLATFORM " + scene + " " + platform.name + " layer=" + platform.gameObject.layer + " bounds=" + platform.bounds +
                    " scripts=" + string.Join(",", platform.GetComponentsInParent<MonoBehaviour>().Select(c => c.GetType().Name)));
            if (mode == "placement" || mode == "native-lift" || mode == "exception-placement" || mode == "room-placement") yield return EdgePlacement(scene);
            if (mode.StartsWith("faults")) yield return EdgeSupportFaults(scene);
        }
    }

    // Deliberate support faults, not a claim of native moving-platform coverage.
    private IEnumerator EdgeSupportFaults(string scene) {
        var hero = HeroController.instance; var pd = PlayerData.instance;
        var body = hero.GetComponent<Rigidbody2D>(); var other = host ? "guest" : "host";
        var entry = hero.transform.position;
        var mode = Environment.GetEnvironmentVariable("HOLLOWCONNECT_RESCUE_EDGE_CHECK");
        var fixedCheck = mode != "faults";
        foreach (var fault in (fixedCheck ? new[] { mode.Contains("no-support") ? "no-support" : "support-moves" } : new[] { "support-moves", "no-support" })) {
            foreach (var actor in (fixedCheck ? new[] { mode.EndsWith("guest") ? "guest" : "host" } : new[] { "guest", "host" })) {
                var step = fault + "." + actor;
                var origin = new Vector3(10000, 42, 0);
                var floor = new GameObject("Audit injected support " + step); floor.layer = 8;
                floor.transform.position = origin + Vector3.down * 2;
                floor.AddComponent<BoxCollider2D>().size = new Vector2(8, 1);
                floor.SetActive(fault == "support-moves");
                hero.takeNoDamage = true; hero.transform.position = origin; body.velocity = Vector2.zero;
                pd.SetInt("health", 5); hero.cState.invulnerable = false;
                pd.SetInt("geo", 321); pd.SetInt("geoPool", 67); pd.SetString("shadeScene", "Crossroads_10");
                hero.AffectedByGravity(false); hero.SetHazardRespawn(origin, true); Physics2D.SyncTransforms();
                Flag(role + ".edge.fault.ready." + step);
                yield return Until(() => Has(other + ".edge.fault.ready." + step), "fault both ready " + step);
                if (role == actor) {
                    hero.takeNoDamage = false; pd.SetInt("health", 1); hero.AffectedByGravity(true);
                    hero.TakeDamage(null, GlobalEnums.CollisionSide.left, 1, 1);
                    yield return Until(() => runtime.Down || runtime.Returning, "fault produces temporary down or native return " + step);
                    Log("FAULT ANCHOR " + step + "=" + runtime.DeathPosition);
                    floor.transform.position += Vector3.right * 20; Physics2D.SyncTransforms();
                    var fallback = fixedCheck && mode.Contains("fallback");
                    if (fallback) hero.SetHazardRespawn(entry, true);
                    Flag(role + ".edge.fault.moved." + step);
                    if (fallback) {
                        yield return Until(() => !runtime.Down && pd.health == 1, "moved support rescue uses fresh native checkpoint " + step);
                        Check(Vector2.Distance(hero.transform.position, hero.FindGroundPoint(entry, true)) < 3.05f && hero.cState.invulnerable,
                            "fresh supported fallback keeps rescue protection " + step);
                        hero.takeNoDamage = true; hero.AffectedByGravity(true);
                        yield return Until(() => hero.cState.onGround && hero.acceptingInput, "fallback rescue stands on actual support " + step);
                        Check(!runtime.Returning && !runtime.Resetting && EdgeScene == scene && pd.GetInt("geo") == 321 && pd.GetInt("geoPool") == 67,
                            "supported fallback rescues without formal death " + step);
                        Flag(role + ".edge.returned." + step);
                    } else if (fixedCheck) {
                        yield return new WaitForSecondsRealtime(2);
                        Log("FAULT RETURN STATE " + step + " down=" + runtime.Down + " returning=" + runtime.Returning + " resetting=" + runtime.Resetting +
                            " scene=" + EdgeScene + " input=" + hero.acceptingInput + " health=" + pd.health + " serial=" + runtime.DeathSerial);
                        yield return new WaitForSecondsRealtime(6);
                        Log("FAULT LATE RETURN " + step + " down=" + runtime.Down + " returning=" + runtime.Returning + " scene=" + EdgeScene +
                            " active=" + UnityEngine.SceneManagement.SceneManager.GetActiveScene().name + " bench=" + pd.GetString("respawnScene") + "/" + pd.GetString("respawnMarkerName") +
                            " atBench=" + pd.GetBool("atBench") + " dead=" + hero.cState.dead + " input=" + hero.acceptingInput + " position=" + hero.transform.position);
                        yield return Until(() => !runtime.Down && !runtime.Returning && !runtime.Resetting && EdgeScene == "Town" &&
                            hero.isHeroInPosition && !hero.cState.dead && !GameManager.instance.IsInSceneTransition,
                            "unsupported rescue returns through native personal death " + step, 75);
                        if (pd.GetBool("atBench")) {
                            var bench = Object.FindObjectsOfType<PlayMakerFSM>().Single(f => f.name == "RestBench" && f.FsmName == "Bench Control");
                            yield return Until(() => bench.ActiveStateName == "Resting", "unsupported return native bench ready " + step);
                            bench.Fsm.Event(bench.Fsm.ActiveState.Actions.OfType<HutongGames.PlayMaker.Actions.ListenForJump>().Single().wasPressed);
                        }
                        yield return Until(() => !pd.GetBool("atBench") && hero.acceptingInput && !hero.controlReqlinquished,
                            "unsupported return releases native bench control " + step);
                        Check(Hkmp.CoopContext.Epoch == 0 && runtime.ReleasedRound == 0, "unsupported individual return does not wipe party " + step);
                        Check(pd.GetInt("geo") == 321 && pd.GetInt("geoPool") == 67 && pd.GetString("shadeScene") == "Crossroads_10",
                            "unsupported palace return preserves money and prior shade " + step);
                        Check(!Object.FindObjectsOfType<HollowConnect.ShadeView>().Any(), "unsupported return clears temporary shade " + step);
                        Flag(role + ".edge.returned." + step);
                    } else {
                        yield return Until(() => !runtime.Down && pd.health == 1, "fault actual rescue accepted " + step);
                        hero.takeNoDamage = true;
                        yield return new WaitForSeconds(.5f);
                        var support = Physics2D.Raycast(hero.transform.position, Vector2.down, 4, 1 << 8);
                        Check(!hero.cState.onGround && support.collider == null && !runtime.Returning && !runtime.Resetting,
                            "reproduced unsupported live rescue " + step);
                        Log("CONFIRMED UNSAFE RESCUE " + step + " hero=" + hero.transform.position + " velocity=" + body.velocity);
                    }
                } else {
                    if (!fixedCheck || fault == "support-moves") {
                        yield return Until(() => Has(actor + ".edge.fault.moved." + step) && runtime.Peers.Values.Any(p => p.Life == HollowConnect.Core.Life.Down), "fault support removed " + step);
                        floor.transform.position += Vector3.right * 20;
                        var peer = runtime.Peers.Single(); var anchor = new Vector3(peer.Value.State.X, peer.Value.State.Y, 0);
                        hero.transform.position = anchor + Vector3.right * 2; body.velocity = Vector2.zero;
                        yield return new WaitForSecondsRealtime(.2f);
                        hero.FaceLeft(); hero.Attack(GlobalEnums.AttackDirection.normal); Physics2D.SyncTransforms();
                        if (!fixedCheck) yield return Until(() => runtime.Peers[peer.Key].Life == HollowConnect.Core.Life.Alive, "fault physical nail rescue " + step);
                        else Check(hero.cState.attacking, "unsupported rescue still uses actual nail swing " + step);
                    }
                    if (fixedCheck) {
                        yield return Until(() => Has(actor + ".edge.returned." + step), "other player completes native unsupported return " + step, 75);
                        Check(EdgeScene == scene && !runtime.Resetting && Hkmp.CoopContext.Epoch == 0,
                            "survivor continues original room after unsupported return " + step);
                    }
                }
                Flag(role + ".edge.fault.complete." + step);
                yield return Until(() => Has(other + ".edge.fault.complete." + step), "fault both observed " + step);
                Object.Destroy(floor); hero.transform.position = entry; body.velocity = Vector2.zero;
                hero.takeNoDamage = true; hero.AffectedByGravity(false); Physics2D.SyncTransforms();
                yield return new WaitForSeconds(hero.INVUL_TIME + .3f);
            }
        }
    }

    private IEnumerator EdgePlacement(string scene) {
        var hero = HeroController.instance; var pd = PlayerData.instance;
        var body = hero.GetComponent<Rigidbody2D>(); var other = host ? "guest" : "host";
        var safe = hero.FindGroundPoint(pd.GetVector3("hazardRespawnLocation"), true);
        // A test actor parked beside a bottom entrance can walk back through
        // its portal while swapping rescuer roles. Use a real in-room marker.
        var gates = Object.FindObjectsOfType<TransitionPoint>().Where(g => g.gameObject.scene.name == scene).ToArray();
        foreach (var marker in Object.FindObjectsOfType<HazardRespawnMarker>().Where(m => m.gameObject.scene.name == scene &&
            gates.All(g => Vector2.Distance(m.transform.position, g.transform.position) > 10)).OrderBy(m => Vector2.Distance(m.transform.position, safe))) {
            var hit = Physics2D.Raycast(marker.transform.position, Vector2.down, 50, 1 << 8);
            if (hit.collider == null || hit.distance == 0 || hit.collider.isTrigger) continue;
            var grounded = hero.FindGroundPoint(marker.transform.position, true);
            var args = new object[] { (Vector2)grounded, scene, null };
            if (!(bool)typeof(CoopRuntime).GetMethod("TryWarpLanding", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static).Invoke(null, args)) continue;
            var landing = (Vector2)args[2]; safe = new Vector3(landing.x, landing.y, hero.transform.position.z);
            Log("EDGE FIXTURE IN-ROOM MARKER " + scene + " marker=" + marker.transform.position + " support=" + safe);
            break;
        }
        hero.transform.position = safe; body.velocity = Vector2.zero; hero.AffectedByGravity(true); Physics2D.SyncTransforms();
        yield return Until(() => hero.cState.onGround, "edge native checkpoint support " + scene);
        safe = hero.transform.position;
        foreach (var actor in new[] { "guest", "host" }) {
            var step = scene + "." + actor;
            hero.takeNoDamage = true; hero.transform.position = safe; body.velocity = Vector2.zero;
            hero.SetHazardRespawn(safe, true); pd.SetInt("health", 5); pd.SetInt("geo", 321);
            pd.SetInt("geoPool", 67); pd.SetString("shadeScene", "Crossroads_10");
            Flag(role + ".edge.ready." + step); yield return Until(() => Has(other + ".edge.ready." + step), "edge both ready " + step);
            if (role == actor) {
                var nativeLift = Environment.GetEnvironmentVariable("HOLLOWCONNECT_RESCUE_EDGE_CHECK") == "native-lift";
                if (nativeLift) {
                    var lift = Object.FindObjectsOfType<Collider2D>().First(c => c.gameObject.scene.name == scene && c.name == "White Palace Lift" && !c.isTrigger);
                    var before = lift.bounds.center;
                    hero.transform.position = new Vector3(before.x, lift.bounds.max.y + 3, 0); body.velocity = Vector2.zero;
                    hero.AffectedByGravity(true); Physics2D.SyncTransforms();
                    yield return Until(() => hero.cState.onGround && Math.Abs(hero.GetComponent<Collider2D>().bounds.min.y - lift.bounds.max.y) < .5f,
                        "actual knight lands on native palace lift " + step);
                    yield return Until(() => Vector2.Distance(lift.bounds.center, before) > .3f,
                        "native palace lift moves after player landing " + step, 10);
                    Log("NATIVE MOVING LIFT " + step + " from=" + before + " to=" + lift.bounds.center + " hero=" + hero.transform.position);
                } else { hero.transform.position = safe + Vector3.up * 3; Physics2D.SyncTransforms(); }
                yield return Until(() => hero.acceptingInput && !hero.cState.transitioning &&
                    hero.transitionState == GlobalEnums.HeroTransitionState.WAITING_TO_TRANSITION &&
                    !hero.cState.recoiling && !hero.cState.hazardDeath && !hero.cState.hazardRespawning,
                    "edge victim ready for lethal hit " + step);
                Check(EdgeScene == scene, "edge damage stays in intended room " + step);
                hero.takeNoDamage = false; pd.SetInt("health", 1); hero.cState.invulnerable = false;
                hero.SetDamageMode(GlobalEnums.DamageMode.FULL_DAMAGE);
                var lethalPosition = hero.transform.position;
                Log("EDGE LETHAL PRE " + step + " input=" + hero.acceptingInput + " state=" + hero.hero_state + " transition=" + hero.transitionState +
                    " damage=" + typeof(HeroController).GetMethod("CanTakeDamage", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance).Invoke(hero, null) +
                    " invincible=" + pd.GetBool("isInvincible") + " dead=" + hero.cState.dead + " health=" + pd.health + " blue=" + pd.GetInt("healthBlue"));
                hero.TakeDamage(null, GlobalEnums.CollisionSide.left, 1, 1);
                Log("EDGE LETHAL POST " + step + " health=" + pd.health + " down=" + runtime.Down + " returning=" + runtime.Returning +
                    " invulnerable=" + hero.cState.invulnerable + " recoil=" + hero.cState.recoiling);
                Check(runtime.Down, "direct test hit causes supported down " + step);
                yield return Until(() => runtime.Down, "edge ordinary lethal hit " + step);
                // Native hazard triggers can advance the checkpoint while the
                // actors move, notably at the Path of Pain's vertical entrance.
                var checkpoint = pd.GetVector3("hazardRespawnLocation");
                var ground = hero.FindGroundPoint(checkpoint, true);
                var zone = GameManager.instance.GetCurrentMapZone();
                var shadeMarker = zone == "WHITE_PALACE" || zone == "DREAM_WORLD" || zone == "GODS_GLORY" ? null :
                    GameObject.FindGameObjectsWithTag("Shade Marker").Where(g => g.scene.name == scene)
                        .OrderBy(g => (g.transform.position - lethalPosition).sqrMagnitude).FirstOrDefault();
                if (shadeMarker != null) ground = hero.FindGroundPoint(shadeMarker.transform.position, true);
                Log("EDGE ANCHOR " + step + "=" + runtime.DeathPosition + " initial=" + safe + " current=" + checkpoint + " ground=" + ground);
                Check(Vector2.Distance(runtime.DeathPosition, ground) < 3.05f, "edge uses supported native death marker or checkpoint " + step);
                yield return Until(() => !runtime.Down && pd.health == 1, "edge actual rescue restores one health " + step);
                Check(hero.cState.invulnerable, "edge native rescue protection " + step);
                hero.takeNoDamage = true;
                yield return new WaitForSeconds(hero.INVUL_TIME + hero.DAMAGE_FREEZE_DOWN + .2f);
                yield return Until(() => hero.cState.onGround && hero.acceptingInput, "edge rescued knight supported and controllable " + step);
                Check(!hero.cState.dead && !hero.cState.hazardDeath && !hero.cState.hazardRespawning && EdgeScene == scene,
                    "edge rescue neither dies again nor leaves room " + step);
                Check(pd.GetInt("geo") == 321 && pd.GetInt("geoPool") == 67 && pd.GetString("shadeScene") == "Crossroads_10",
                    "edge rescue preserves personal death state " + step);
            } else {
                yield return Until(() => runtime.Peers.Values.Any(p => p.Life == HollowConnect.Core.Life.Down), "edge teammate down " + step);
                var peer = runtime.Peers.Single(); var anchor = new Vector3(peer.Value.State.X, peer.Value.State.Y, 0);
                hero.transform.position = anchor + Vector3.right * 2; body.velocity = Vector2.zero;
                hero.AffectedByGravity(false); hero.FaceLeft(); hero.Attack(GlobalEnums.AttackDirection.normal); Physics2D.SyncTransforms();
                yield return Until(() => runtime.Peers[peer.Key].Life == HollowConnect.Core.Life.Alive, "edge physical nail rescue " + step);
            }
            Flag(role + ".edge.complete." + step); yield return Until(() => Has(other + ".edge.complete." + step), "edge both complete " + step);
            hero.transform.position = safe; body.velocity = Vector2.zero; hero.AffectedByGravity(true); Physics2D.SyncTransforms();
        }
    }
}
