using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using HollowConnect;
using UnityEngine;
using Object = UnityEngine.Object;

namespace HollowConnect.Smoke;

public sealed partial class RescueProbe {
    private IEnumerator WarpCollisionChecks() {
        var baseline = Environment.GetEnvironmentVariable("HOLLOWCONNECT_WARP_COLLISION_CHECK") == "baseline";
        using var noContact = IgnoreExternalCombatDamage();
        UnityEngine.SceneManagement.SceneManager.sceneLoaded += (loaded, _) => {
            if (!loaded.name.StartsWith("Waterways_", StringComparison.Ordinal) && !loaded.name.StartsWith("Abyss_", StringComparison.Ordinal)) return;
            foreach (var entrance in loaded.GetRootGameObjects().SelectMany(g => g.GetComponentsInChildren<TransitionPoint>(true)))
                Log("WARP PREFLIGHT GATE " + loaded.name + "/" + entrance.name + " -> " + entrance.targetScene + "/" + entrance.entryPoint);
        };
        yield return RuleBenchUp();
        yield return RuleEnter("Waterways_05", "right1");
        var exits = Object.FindObjectsOfType<TransitionPoint>().Where(t => t.gameObject.scene.name == "Waterways_05" &&
            !string.IsNullOrEmpty(t.targetScene)).OrderByDescending(t => t.transform.position.x).ToArray();
        foreach (var exit in exits) Log("WARP EXIT " + exit.name + " pos=" + exit.transform.position + " -> " + exit.targetScene + "/" + exit.entryPoint);
        var right = exits.First(t => t.name.StartsWith("right", StringComparison.Ordinal));
        var scene = right.targetScene; var gate = right.entryPoint;
        yield return RuleEnter(scene, gate);
        HeroController.instance.takeNoDamage = true;
        HeroController.instance.AffectedByGravity(false);
        HeroController.instance.GetComponent<Rigidbody2D>().velocity = Vector2.zero;
        Flag(role + ".warp-collision-room");
        yield return Until(() => Has((host ? "guest" : "host") + ".warp-collision-room"), "both in native right-side Waterways room");
        var platforms = Object.FindObjectsOfType<Collider2D>().Where(c => c.gameObject.scene.name == scene && c.enabled &&
            !c.isTrigger && c.gameObject.layer == 8 && c.bounds.size.x > 2 && c.bounds.size.x < 15 &&
            c.bounds.size.y > .15f && c.bounds.size.y < 5).OrderByDescending(c => c.bounds.size.x).ToArray();
        foreach (var platform in platforms) Log("WARP NATIVE PLATFORM " + platform.name + " type=" + platform.GetType().Name + " bounds=" + platform.bounds);
        Check(platforms.Length > 0, "native right-side room contains bounded floating platforms: " + scene);
        var floor = platforms.FirstOrDefault(c => c is BoxCollider2D) ?? platforms.First();
        var hero = HeroController.instance; var body = hero.GetComponent<Rigidbody2D>();
        var box = hero.GetComponent<BoxCollider2D>();
        Check(box != null, "native hero uses a BoxCollider2D");
        var savedPosition = hero.transform.position;
        var savedScale = hero.transform.localScale;
        var savedFacing = hero.cState.facingRight;
        var savedOffset = box.offset;
        var savedSimulated = body.simulated;
        var savedEnabled = box.enabled;
        var resolve = typeof(CoopRuntime).GetMethod("TryWarpLanding", BindingFlags.Static | BindingFlags.NonPublic);
        var results = new List<object>();
        var unsafeCount = 0; var disabledFailures = 0; var wallFailures = 0;
        bool Resolve(Vector2 target, out Vector2 position) {
            var args = new object[] { target, scene, null };
            var found = (bool)resolve.Invoke(null, args); position = (Vector2)args[2]; return found;
        }
        bool Penetrates(Vector2 position) {
            body.simulated = true; box.enabled = true;
            hero.transform.position = new Vector3(position.x, position.y, hero.transform.position.z);
            Physics2D.SyncTransforms();
            return Physics2D.OverlapBoxAll(box.bounds.center, box.bounds.size, 0).Any(c => c != box && !c.transform.IsChildOf(hero.transform) &&
                c.gameObject.scene.name == scene && c.enabled && !c.isTrigger &&
                !Physics2D.GetIgnoreLayerCollision(hero.gameObject.layer, c.gameObject.layer) && Physics2D.Distance(box, c).isOverlapped);
        }
        try {
            foreach (var facingRight in new[] { false, true }) {
                // A nonzero body offset makes mirrored facing meaningful.
                box.offset = savedOffset + new Vector2(.2f, 0);
                if (facingRight) hero.FaceRight(); else hero.FaceLeft();
                body.simulated = true; box.enabled = true; Physics2D.SyncTransforms();
                var size = box.bounds.size;
                var offset = box.bounds.center - hero.transform.position;
                var target = new Vector2(floor.bounds.center.x - offset.x, floor.bounds.max.y + size.y * .5f - offset.y + .05f);
                foreach (var state in new[] { "active", "unsimulated", "disabled" }) {
                    hero.transform.position = savedPosition;
                    body.simulated = state != "unsimulated"; box.enabled = state != "disabled"; Physics2D.SyncTransforms();
                    var queriedBounds = box.bounds;
                    var found = Resolve(target, out var position);
                    var overlap = found && Penetrates(position);
                    if (overlap) unsafeCount++;
                    if (!found && state != "active") disabledFailures++;
                    results.Add(new { check = "body-" + state, facingRight, found, overlap, target = target.ToString(), position = position.ToString(), bounds = queriedBounds.ToString() });
                }
                body.simulated = true; box.enabled = true;
                hero.transform.position = savedPosition; Physics2D.SyncTransforms();
                var wallObject = new GameObject("warp collision thin wall fixture"); wallObject.layer = 8;
                var wall = wallObject.AddComponent<BoxCollider2D>(); wall.size = new Vector2(.3f, 3);
                foreach (var side in new[] { -1, 1 }) {
                    wall.transform.position = new Vector3(target.x + offset.x + side * (size.x * .5f + .15f - .003f), target.y + offset.y + .1f, 0);
                    hero.transform.position = savedPosition; Physics2D.SyncTransforms();
                    var found = Resolve(target, out var position);
                    var overlap = found && Penetrates(position);
                    if (overlap) unsafeCount++;
                    if (!found || (position.x - target.x) * side >= -.02f || Math.Abs(position.x - target.x) > 2) wallFailures++;
                    results.Add(new { check = "thin-wall", facingRight, side, found, overlap, target = target.ToString(), position = position.ToString() });
                }
                Object.DestroyImmediate(wallObject);
            }
        } finally {
            body.simulated = savedSimulated; box.enabled = savedEnabled; box.offset = savedOffset;
            hero.transform.position = savedPosition; hero.transform.localScale = savedScale; hero.cState.facingRight = savedFacing;
            Physics2D.SyncTransforms();
        }
        File.WriteAllText(Path.Combine(root, role + ".warp-collision.json"), Newtonsoft.Json.JsonConvert.SerializeObject(new {
            scene, platform = floor.name, unsafeCount, disabledFailures, wallFailures, results
        }, Newtonsoft.Json.Formatting.Indented));
        Log("WARP COLLISION RESULT scene=" + scene + " unsafe=" + unsafeCount + " disabledBodyFailures=" + disabledFailures);
        if (!baseline) {
            Check(unsafeCount == 0, "full hero collider never penetrates a wall in either facing direction");
            Check(disabledFailures == 0, "entry-time disabled physics retains a usable full body shape");
            Check(wallFailures == 0, "contact normals move away from either wall within two units");
            yield return WarpWaterwaysTransfers(scene, gate, floor.name, floor.bounds.center);
        }
    }

    private IEnumerator WarpWaterwaysTransfers(string scene, string gate, string platformName, Vector3 platformCenter) {
        var hero = HeroController.instance; var body = hero.GetComponent<Rigidbody2D>();
        var other = host ? "guest" : "host";
        Collider2D Platform() => Object.FindObjectsOfType<Collider2D>().Where(c => c.gameObject.scene.name == scene &&
            c.name == platformName && c.enabled && !c.isTrigger).OrderBy(c => Vector2.Distance(c.bounds.center, platformCenter)).First();
        IEnumerator Together(string step) {
            Flag(role + ".waterways-warp." + step);
            yield return Until(() => Has(other + ".waterways-warp." + step), "both Waterways warp " + step);
        }
        foreach (var actor in new[] { "host", "guest" }) foreach (var cross in new[] { false, true }) {
            var step = actor + (cross ? "-cross" : "-same");
            if (role != actor) {
                var platform = Platform();
                hero.transform.position = new Vector3(platform.bounds.center.x, platform.bounds.max.y + 2, hero.transform.position.z);
                body.velocity = Vector2.zero; hero.AffectedByGravity(true); Physics2D.SyncTransforms();
                yield return Until(() => hero.cState.onGround && hero.acceptingInput, "native Waterways platform supports target " + step);
                hero.takeNoDamage = true;
            } else if (cross) yield return RuleEnter("Town");
            else {
                var platform = Platform();
                hero.transform.position = new Vector3(platform.bounds.center.x - 3, platform.bounds.max.y + 3, hero.transform.position.z);
                body.velocity = Vector2.zero; hero.AffectedByGravity(false); Physics2D.SyncTransforms();
            }
            yield return Together(step + "-ready");
            yield return new WaitForSecondsRealtime(.6f);
            if (role == actor) {
                runtime.Selected = runtime.Peers.Keys.Single();
                var before = Field<uint>(runtime, "handledWarp");
                var pressed = false;
                runtime.KeyPressed = key => {
                    if (key != CoopMod.Settings.TeleportKey || pressed) return false;
                    pressed = true; hero.AffectedByGravity(true); return true;
                };
                yield return Until(() => pressed && Field<uint>(runtime, "handledWarp") > before, "actual F8 accepted over UDP " + step);
                runtime.KeyPressed = _ => false;
                yield return Until(() => !Field<bool>(runtime, "changingScene") && GameManager.instance.sceneName == scene &&
                    HeroController.instance.acceptingInput, "actual F8 finishes native arrival " + step);
                hero.AffectedByGravity(true);
                yield return Until(() => hero.cState.onGround, "actual F8 lands on native Waterways support " + step);
                var box = hero.GetComponent<Collider2D>();
                Check(!Physics2D.OverlapBoxAll(box.bounds.center, box.bounds.size, 0).Any(c => c != box &&
                    !c.transform.IsChildOf(hero.transform) && c.gameObject.scene.name == scene && !c.isTrigger &&
                    !Physics2D.GetIgnoreLayerCollision(hero.gameObject.layer, c.gameObject.layer) &&
                    Physics2D.Distance(box, c).isOverlapped && Physics2D.Distance(box, c).distance < -.01f),
                    "actual F8 full body clears native platform and walls " + step);
                Check(Vector2.Distance(hero.transform.position, Platform().bounds.center) < 5,
                    "actual F8 stays beside the requested Waterways platform " + step);
            }
            yield return Together(step + "-finished");
            yield return new WaitForSecondsRealtime(3.1f);
        }
    }
}
