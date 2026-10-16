using System;
using System.Collections;
using System.Linq;
using System.Reflection;
using HollowConnect;
using HollowConnect.Core;
using UnityEngine;

namespace HollowConnect.Smoke;

public sealed partial class RescueProbe {
    private IEnumerator DeathCameraRescueChecks() {
        var hero = HeroController.instance;
        var pd = PlayerData.instance;
        var body = hero.GetComponent<Rigidbody2D>();
        var other = host ? "guest" : "host";
        if (pd.GetBool("atBench")) {
            var bench = UnityEngine.Object.FindObjectsOfType<PlayMakerFSM>().Single(f => f.name == "RestBench" && f.FsmName == "Bench Control");
            yield return Until(() => bench.ActiveStateName == "Resting", "camera fixture native starting bench ready");
            bench.Fsm.Event(bench.Fsm.ActiveState.Actions.OfType<HutongGames.PlayMaker.Actions.ListenForJump>().Single().wasPressed);
            yield return Until(() => !pd.GetBool("atBench") && hero.acceptingInput && !body.isKinematic,
                "camera fixture native starting bench get-up");
        }
        GameManager.instance.BeginSceneTransition(new GameManager.SceneLoadInfo {
            SceneName = "Crossroads_01", EntryGateName = "left1",
            Visualization = GameManager.SceneLoadVisualizations.Default, WaitForSceneTransitionCameraFade = true
        });
        yield return Until(() => runtime.Playable && GameManager.instance.sceneName == "Crossroads_01" &&
            hero.acceptingInput && !hero.cState.transitioning && engine.ClientManager.SceneSnapshotReady &&
            GameCameras.instance.cameraController.mode != CameraController.CameraMode.FROZEN, "camera fixture native room and snapshot ready");
        var safe = hero.FindGroundPoint(pd.GetVector3("hazardRespawnLocation"), true);
        hero.transform.position = safe + Vector3.up * .1f; hero.takeNoDamage = true;
        body.velocity = Vector2.zero; hero.AffectedByGravity(true); Physics2D.SyncTransforms();
        yield return Until(() => hero.cState.onGround, "camera fixture native safe checkpoint grounded");
        safe = hero.transform.position; hero.SetHazardRespawn(safe, true);
        var cameraController = GameCameras.instance.cameraController;
        var markers = GameObject.FindGameObjectsWithTag("Shade Marker").Where(g => g.scene.name == "Crossroads_01").ToArray();
        var candidates = new System.Collections.Generic.List<(Vector3 Origin, Vector3 Safe, float Distance)>();
        for (var x = 10f; x < cameraController.sceneWidth - 10; x += 7)
            for (var y = 10f; y < cameraController.sceneHeight - 8; y += 7) {
                var origin = new Vector3(x, y, hero.transform.position.z);
                if (Physics2D.OverlapBox(origin, new Vector2(2, 3), 0, 1 << 8) != null) continue;
                var marker = markers.OrderBy(g => (g.transform.position - origin).sqrMagnitude).FirstOrDefault();
                if (marker == null) continue;
                var hit = Physics2D.Raycast(marker.transform.position, Vector2.down, 50, 1 << 8);
                if (hit.collider == null || hit.distance == 0 || hit.collider.isTrigger || hit.normal.y < .5f) continue;
                var ground = hero.FindGroundPoint(marker.transform.position, true);
                var args = new object[] { (Vector2)ground, "Crossroads_01", null };
                if (!(bool)typeof(CoopRuntime).GetMethod("TryWarpLanding", BindingFlags.Static | BindingFlags.NonPublic).Invoke(null, args)) continue;
                var supported = (Vector2)args[2];
                var anchor = new Vector3(supported.x, supported.y, origin.z);
                candidates.Add((origin, anchor, Vector2.Distance(origin, anchor)));
            }
        Check(candidates.Count > 0, "native story room has clear death positions and supported Shade Marker");
        var ordered = candidates.OrderByDescending(c => Math.Abs(c.Origin.x - c.Safe.x)).ThenByDescending(c => c.Distance).ToArray();
        var source = ordered[0];
        Log("CAMERA STORY GEOMETRY size=" + cameraController.sceneWidth + "x" + cameraController.sceneHeight +
            " origin=" + source.Origin + " markerSafe=" + source.Safe + " distance=" + source.Distance);
        foreach (var victimRole in new[] { "guest", "host" }) {
            var step = "camera." + victimRole;
            var victim = role == victimRole;
            hero.RegainControl(); hero.StartAnimationControl(); hero.takeNoDamage = true;
            hero.cState.invulnerable = hero.cState.recoiling = false;
            pd.SetInt("health", 5); pd.SetInt("healthBlue", 0); pd.SetBool("isInvincible", false);
            hero.SetDamageMode(GlobalEnums.DamageMode.FULL_DAMAGE);
            body.velocity = Vector2.zero; hero.AffectedByGravity(false);
            hero.transform.position = victim ? source.Origin : source.Safe + Vector3.right * 2;
            Physics2D.SyncTransforms();
            Flag(role + "." + step + ".ready");
            yield return Until(() => Has(other + "." + step + ".ready") && runtime.Peers.Count == 1,
                "camera fixture both ready " + victimRole);
            if (victim) {
                var camera = GameCameras.instance.cameraController;
                CoverageDeathCameraChecks.BeforeDown before = null;
                // Native camera locks can make geometric distance misleading.
                // Observe the real view before damage; never alter its mode or
                // snap the camera to manufacture an offscreen checkpoint.
                foreach (var candidate in ordered.Take(12)) {
                    hero.transform.position = candidate.Origin; body.velocity = Vector2.zero; Physics2D.SyncTransforms();
                    camera.PositionToHero(false);
                    yield return new WaitForSecondsRealtime(.3f);
                    yield return Until(() => camera.mode != CameraController.CameraMode.FROZEN, "native death-origin camera positioned " + victimRole);
                    var observation = CoverageDeathCameraChecks.Capture();
                    var heroView = camera.cam.WorldToViewportPoint(hero.transform.position);
                    if (heroView.x <= .01f || heroView.x >= .99f || heroView.y <= .01f || heroView.y >= .99f ||
                        observation.VisibleWorld.Contains(new Vector2(candidate.Safe.x, candidate.Safe.y + .5f))) continue;
                    source = candidate; before = observation; break;
                }
                Check(before != null, "natural native camera shows death origin but not its safe Shade Marker " + victimRole);
                Log("DEATH CAMERA SOURCE " + victimRole + " hero=" + hero.transform.position + " checkpoint=" + safe +
                    " view=" + before.VisibleWorld + " camera=" + camera.transform.position);
                pd.SetInt("health", 1); hero.takeNoDamage = false;
                hero.TakeDamage(null, GlobalEnums.CollisionSide.left, 1, 1);
                yield return Until(() => runtime.Down, "actual native lethal damage enters rescue " + victimRole);
                Check(Vector2.Distance(runtime.DeathPosition, source.Safe) < .2f, "native Shade Marker selected for far death " + victimRole);
                yield return CoverageDeathCameraChecks.WhileDown(runtime, before, victimRole, Log);
                Flag(step + ".view-checked");
                yield return Until(() => !runtime.Down && pd.health == 1, "real nail rescue returns one health " + victimRole);
                yield return CoverageDeathCameraChecks.AfterRescue(runtime, before, victimRole, Log);
                hero.takeNoDamage = true; hero.AffectedByGravity(true);
                yield return Until(() => hero.cState.onGround && hero.acceptingInput && !hero.controlReqlinquished &&
                    body.simulated && !hero.GetComponent<Renderer>().forceRenderingOff, "rescued hero controllable on native support " + victimRole);
                Check(camera.mode == before.Mode, "rescue restores original native camera mode " + victimRole);
                Flag(step + ".rescued");
            } else {
                yield return Until(() => Has(step + ".view-checked") && runtime.Peers.Values.Any(p => p.Life == Life.Down),
                    "far death view was checked before rescuer swings " + victimRole);
                var peer = runtime.Peers.Single(); var shades = Field<ShadeService>(runtime, "shades");
                yield return Until(() => shades.Get(peer.Key) != null, "real rescue shade exists " + victimRole);
                var anchor = shades.Get(peer.Key).transform.position;
                hero.transform.position = anchor + Vector3.right * 2; body.velocity = Vector2.zero;
                hero.RegainControl(); hero.StartAnimationControl(); hero.FaceLeft();
                hero.Attack(GlobalEnums.AttackDirection.normal); Physics2D.SyncTransforms();
                yield return Until(() => runtime.Peers[peer.Key].Life == Life.Alive, "real local nail accepted over UDP " + victimRole);
                yield return Until(() => Has(step + ".rescued"), "victim observed camera and input restoration " + victimRole);
            }
            Flag(role + "." + step + ".done");
            yield return Until(() => Has(other + "." + step + ".done"), "both complete camera rescue " + victimRole);
        }
    }
}
