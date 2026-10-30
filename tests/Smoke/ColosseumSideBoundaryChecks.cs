using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using Hkmp.Game.Client.Entity;
using UnityEngine;

namespace HollowConnect.Smoke;

public sealed partial class ColosseumProbe {
    private IEnumerator ColosseumSideBoundaryChecks() {
        Require(trial == "Gold", "side contact uses original Gold contraction");
        var hero = HeroController.instance;
        var body = hero.GetComponent<Rigidbody2D>();
        hero.AffectedByGravity(false); body.velocity = Vector2.zero;
        hero.transform.position = new Vector3(108, 14, 0);
        var battle = ArenaEntity.Object.Host.GetComponents<PlayMakerFSM>().Single(f => f.FsmName == "Battle Control");
        if (ArenaManager.IsSceneHost) battle.Fsm.ManualUpdate = false;
        var deadline = Time.realtimeSinceStartup + 300;
        var prior = "";
        while (ArenaState("Battle Control") != "Wave 29") {
            var state = ArenaState("Battle Control");
            if (state != prior) { prior = state; Log("SIDE NATIVE PROGRESS " + state); SaveWallActors(state); }
            if (state.StartsWith("Wave ") && int.TryParse(state.Substring(5), out var wave)) wallWave = wave;
            if (Time.realtimeSinceStartup > deadline) throw new TimeoutException("Side contraction preflight stalled at " + state);
            ApproachCurrentWallWave(); DamageCurrentWallWave();
            yield return new WaitForSecondsRealtime(.1f);
        }
        var left = host;
        var wall = LocalSideWalls().Single(t => t.name.Replace("(Clone)", "") == "Colosseum Wall " + (left ? "L" : "R"));
        var slab = wall.Find("Wall").GetComponent<Collider2D>();
        Physics2D.SyncTransforms();
        var heroCollider = hero.GetComponent<Collider2D>();
        Require(slab.enabled && !slab.isTrigger && slab.bounds.size.y > 20 && !Physics2D.GetIgnoreCollision(heroCollider, slab),
            "native side slab solid and entrance collision restored " + WallPath(slab.transform) + " " + slab.bounds);
        var initial = slab.bounds.center.x;
        var bounds = heroCollider.bounds;
        var position = hero.transform.position;
        position.x += left ? slab.bounds.max.x - bounds.min.x + .3f : slab.bounds.min.x - bounds.max.x - .3f;
        position.y = 14;
        hero.transform.position = position; body.velocity = Vector2.zero; Physics2D.SyncTransforms();
        Require(left ? heroCollider.bounds.min.x > slab.bounds.max.x : heroCollider.bounds.max.x < slab.bounds.min.x,
            "local knight starts entirely inside native " + (left ? "left" : "right") + " wall");
        var child = Field<Dictionary<ulong, Entity>>(ArenaManager, "_entities").Values
            .Single(e => e.Object.Client == slab.gameObject || e.Object.Host == slab.gameObject);
        var held = 0; var released = false; var releaseAt = 0f;
        var pending = default(Hkmp.Math.Vector2);
        using var deliveryGap = new MonoMod.RuntimeDetour.Hook(typeof(Entity).GetMethod("UpdatePosition"),
            new Action<Action<Entity, Hkmp.Math.Vector2, bool>, Entity, Hkmp.Math.Vector2, bool>((orig, entity, update, snapshot) => {
                if (!ArenaManager.IsSceneHost && entity == child && !released &&
                    (left ? update.X > initial + .2f : update.X < initial - .2f)) {
                    if (held++ == 0) releaseAt = Time.realtimeSinceStartup + .35f;
                    pending = update; return;
                }
                orig(entity, update, snapshot);
            }));
        Flag(role + ".side-staged");
        yield return Until(() => Has("host.side-staged") && Has("guest.side-staged"), "both side contact preflights complete");
        wallWave = 29;
        while (ArenaState("Battle Control") == "Wave 29") {
            if (Time.realtimeSinceStartup > deadline) throw new TimeoutException("Wave 29 did not finish naturally");
            DamageCurrentWallWave(); yield return new WaitForSecondsRealtime(.1f);
        }
        var samples = new List<object>();
        using (new MonoMod.RuntimeDetour.Hook(typeof(HeroController).GetMethod("LookForInput", BindingFlags.Instance | BindingFlags.NonPublic),
            new Action<Action<HeroController>, HeroController>((orig, self) => { orig(self); if (self.acceptingInput) self.move_input = left ? -1 : 1; }))) {
            var end = Time.realtimeSinceStartup + 7;
            var sample = 0f;
            while (Time.realtimeSinceStartup < end) {
                if (held > 0 && !released && Time.realtimeSinceStartup >= releaseAt) {
                    released = true; child.UpdatePosition(pending);
                    Log("SIDE INJECTED position delivery gap 350ms held=" + held);
                }
                if (Time.realtimeSinceStartup >= sample) {
                    sample = Time.realtimeSinceStartup + .1f;
                    samples.Add(new { time = Time.realtimeSinceStartup, hero = hero.transform.position.ToString(),
                        min = slab.bounds.min.x, max = slab.bounds.max.x, velocity = body.velocity.ToString() });
                }
                yield return null;
            }
        }
        hero.move_input = 0;
        var inside = left ? heroCollider.bounds.min.x >= slab.bounds.max.x - .15f : heroCollider.bounds.max.x <= slab.bounds.min.x + .15f;
        var moved = left ? slab.bounds.center.x - initial : initial - slab.bounds.center.x;
        File.WriteAllText(Path.Combine(root, role + "-side-contact.json"), Newtonsoft.Json.JsonConvert.SerializeObject(new {
            left, authority = ArenaManager.IsSceneHost, inside, moved, held, hero = hero.transform.position.ToString(), samples
        }, Newtonsoft.Json.Formatting.Indented));
        Log("SIDE CONTACT RESULT left=" + left + " authority=" + ArenaManager.IsSceneHost + " inside=" + inside + " moved=" + moved +
            " hero=" + hero.transform.position + " slab=" + slab.bounds);
        Flag(role + ".side-observed");
        yield return Until(() => Has("host.side-observed") && Has("guest.side-observed"), "both side contact observations captured");
        Require(moved > 10, "native Wave 30 really contracts side wall");
        if (!ArenaManager.IsSceneHost) Require(held > 0 && released, "side position gap exercised on replica");
        Require(inside, "contracting wall leaves local knight inside combat space");
        Require(slab.enabled && !slab.isTrigger && !Physics2D.GetIgnoreCollision(heroCollider, slab) && hero.acceptingInput,
            "side wall retains solid collision and local control");
        hero.AffectedByGravity(true);
        using (new MonoMod.RuntimeDetour.Hook(typeof(InControl.OneAxisInputControl).GetProperty("IsPressed").GetGetMethod(),
            new Func<Func<InControl.OneAxisInputControl, bool>, InControl.OneAxisInputControl, bool>((orig, self) =>
                ReferenceEquals(self, left ? InputHandler.Instance.inputActions.left : InputHandler.Instance.inputActions.right) || orig(self))))
        using (new MonoMod.RuntimeDetour.Hook(typeof(HeroController).GetMethod("LookForInput", BindingFlags.Instance | BindingFlags.NonPublic),
            new Action<Action<HeroController>, HeroController>((orig, self) => { orig(self); if (self.acceptingInput) self.move_input = left ? -1 : 1; }))) {
            Log("SIDE SLIDE PREFLIGHT ground=" + hero.cState.onGround + " slide=" + hero.cState.wallSliding +
                " hero=" + hero.transform.position + " velocity=" + body.velocity + " slab=" + slab.bounds);
            // Native contraction can leave the knight on the floor. Sliding
            // requires an airborne body; reach it with an actual jump input.
            if (hero.cState.onGround) {
                var groundY = hero.transform.position.y;
                yield return Press(InputHandler.Instance.inputActions.jump);
                yield return Until(() => !hero.cState.onGround && hero.transform.position.y > groundY + .3f,
                    "native ground jump establishes airborne side contact", 2);
            }
            yield return Until(() => hero.cState.wallSliding, "real native slide on contracted " + (left ? "left" : "right") + " wall", 4);
            var beforeJump = hero.transform.position.x;
            yield return Press(InputHandler.Instance.inputActions.jump);
            yield return Until(() => (hero.transform.position.x - beforeJump) * (left ? 1 : -1) > .4f,
                "real native wall jump moves inward from " + (left ? "left" : "right") + " wall", 3);
        }
        hero.move_input = 0;
        Flag(role + ".side-wall-jump");
        yield return Until(() => Has("host.side-wall-jump") && Has("guest.side-wall-jump"), "both native side slides and wall jumps complete");
    }
}
