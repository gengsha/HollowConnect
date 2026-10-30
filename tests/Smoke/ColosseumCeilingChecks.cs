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
    private Entity CeilingEntity => Field<Dictionary<ulong, Entity>>(ArenaManager, "_entities").Values
        .Single(e => e.Type == EntityType.ColosseumWall && e.Object.Host != null && e.Object.Host.name == "Colosseum Wall C");

    private IEnumerator ColosseumCeilingChecks() {
        var ceiling = CeilingEntity;
        var lines = new List<string>();
        foreach (var rootObject in new[] { ceiling.Object.Host, ceiling.Object.Client }) {
            foreach (var t in rootObject.GetComponentsInChildren<Transform>(true)) {
                lines.Add("OBJECT " + WallPath(t) + " active=" + t.gameObject.activeInHierarchy + " position=" + t.position +
                    " components=" + string.Join(",", t.GetComponents<Component>().Select(c => c == null ? "missing" : c.GetType().Name)));
                foreach (var c in t.GetComponents<Collider2D>())
                    lines.Add("COLLIDER enabled=" + c.enabled + " trigger=" + c.isTrigger + " effector=" + c.usedByEffector +
                        " bounds=" + c.bounds + " offset=" + c.offset);
                foreach (var fsm in t.GetComponents<PlayMakerFSM>()) {
                    lines.Add("FSM " + fsm.FsmName + " enabled=" + fsm.enabled + " state=" + fsm.ActiveStateName);
                    foreach (var state in fsm.FsmStates) {
                        lines.Add("STATE " + state.Name + " -> " + string.Join(",", state.Transitions.Select(v => v.EventName + ":" + v.ToState)));
                        foreach (var action in state.Actions)
                            lines.Add("ACTION " + action.GetType().Name + " " + string.Join(";", action.GetType()
                                .GetFields(BindingFlags.Instance | BindingFlags.Public).Select(f => f.Name + "=" + Describe(f.GetValue(action)))));
                    }
                }
            }
        }
        File.WriteAllLines(Path.Combine(root, role + "-ceiling-schema.txt"), lines);
        Log("CEILING SCHEMA authority=" + ArenaManager.IsSceneHost + " native=" + ceiling.Object.Host.activeInHierarchy +
            " replica=" + ceiling.Object.Client.activeInHierarchy);
        Flag(role + ".ceiling-schema");
        yield return Until(() => Has("host.ceiling-schema") && Has("guest.ceiling-schema"), "both ceiling schemas captured");
        if (testCase == "ceiling-schema") yield break;
        Require(testCase is "ceiling-motion" or "ceiling-gap", "native moving ceiling case selected");
        var hero = HeroController.instance;
        var body = hero.GetComponent<Rigidbody2D>();
        hero.AffectedByGravity(false); body.velocity = Vector2.zero;
        hero.transform.position = new Vector3(108, 14, 0);
        var battle = ArenaEntity.Object.Host.GetComponents<PlayMakerFSM>().Single(f => f.FsmName == "Battle Control");
        if (ArenaManager.IsSceneHost) battle.Fsm.ManualUpdate = false;
        var deadline = Time.realtimeSinceStartup + 150;
        var prior = "";
        while (ArenaState("Battle Control") != "Wave 5") {
            var state = ArenaState("Battle Control");
            if (state != prior) { prior = state; Log("CEILING NATIVE PROGRESS " + state); }
            if (state.StartsWith("Wave ") && int.TryParse(state.Substring(5), out var wave)) wallWave = wave;
            if (Time.realtimeSinceStartup > deadline) throw new TimeoutException("Ceiling preflight stalled at " + state);
            ApproachCurrentWallWave(); DamageCurrentWallWave();
            yield return new WaitForSecondsRealtime(.1f);
        }
        var local = ArenaManager.IsSceneHost ? ceiling.Object.Host : ceiling.Object.Client;
        var slab = local.transform.Find("Wall").GetComponent<Collider2D>();
        Physics2D.SyncTransforms();
        Require(slab.enabled && !slab.isTrigger && slab.bounds.size.x > 30 && slab.bounds.size.y > 1,
            "native ceiling slab is solid and spans arena " + slab.bounds);
        var initialBottom = slab.bounds.min.y;
        var gap = testCase == "ceiling-gap" && !ArenaManager.IsSceneHost;
        var held = 0;
        var releaseAt = 0f;
        var released = false;
        var pendingPosition = default(Hkmp.Math.Vector2);
        var child = Field<Dictionary<ulong, Entity>>(ArenaManager, "_entities").Values
            .Single(e => e.Object.Client == slab.gameObject || e.Object.Host == slab.gameObject);
        using var missedUpdates = new MonoMod.RuntimeDetour.Hook(typeof(Entity).GetMethod("UpdatePosition"),
            new Action<Action<Entity, Hkmp.Math.Vector2, bool>, Entity, Hkmp.Math.Vector2, bool>((orig, entity, position, snapshot) => {
                if (gap && entity == child && !released && position.Y < initialBottom + .6f) {
                    if (held++ == 0) releaseAt = Time.realtimeSinceStartup + .35f;
                    pendingPosition = position;
                    return;
                }
                orig(entity, position, snapshot);
            }));
        var heroBounds = hero.GetComponent<Collider2D>().bounds;
        hero.transform.position = new Vector3(103 + (host ? -2 : 2), initialBottom - heroBounds.extents.y - .3f, 0);
        body.velocity = Vector2.zero; Physics2D.SyncTransforms();
        Require(hero.GetComponent<Collider2D>().bounds.max.y < initialBottom, "airborne hero starts entirely below actual ceiling");
        Flag(role + ".ceiling-staged");
        yield return Until(() => Has("host.ceiling-staged") && Has("guest.ceiling-staged"), "both airborne heroes below native ceiling");
        // Kill only current original enemies. The battle controller itself
        // enters Ceiling 1 and commands its actual MOVE action.
        wallWave = 5;
        while (ArenaState("Battle Control") == "Wave 5") {
            if (Time.realtimeSinceStartup > deadline) throw new TimeoutException("Ceiling Wave 5 enemies did not finish");
            DamageCurrentWallWave(); yield return new WaitForSecondsRealtime(.1f);
        }
        hero.AffectedByGravity(true); body.velocity = Vector2.up * 15;
        var observations = new List<object>();
        var end = Time.realtimeSinceStartup + 6;
        var sample = 0f;
        while (Time.realtimeSinceStartup < end) {
            if (held > 0 && !released && Time.realtimeSinceStartup >= releaseAt) {
                released = true;
                child.UpdatePosition(pendingPosition);
                Log("CEILING INJECTED position delivery gap 350ms; held=" + held);
            }
            if (Time.realtimeSinceStartup >= sample) {
                sample = Time.realtimeSinceStartup + .1f;
                observations.Add(new { time = Time.realtimeSinceStartup, hero = hero.transform.position.ToString(),
                    bottom = slab.bounds.min.y, top = slab.bounds.max.y, velocity = body.velocity.ToString(), ground = hero.cState.onGround });
            }
            yield return null;
        }
        var below = hero.GetComponent<Collider2D>().bounds.max.y <= slab.bounds.min.y + .15f;
        var moved = initialBottom - slab.bounds.min.y;
        File.WriteAllText(Path.Combine(root, role + "-ceiling-contact.json"), Newtonsoft.Json.JsonConvert.SerializeObject(new {
            authority = ArenaManager.IsSceneHost, initialBottom, finalBottom = slab.bounds.min.y, moved, below,
            hero = hero.transform.position.ToString(), held, observations
        }, Newtonsoft.Json.Formatting.Indented));
        Log("CEILING CONTACT RESULT authority=" + ArenaManager.IsSceneHost + " moved=" + moved + " below=" + below +
            " hero=" + hero.transform.position + " slab=" + slab.bounds);
        Flag(role + ".ceiling-observed");
        yield return Until(() => Has("host.ceiling-observed") && Has("guest.ceiling-observed"), "both native ceiling contact observations captured");
        Require(moved > 8, "native ceiling really descended through original wave progression");
        if (gap) Require(held > 0 && released, "explicit ceiling position delivery gap exercised");
        Require(below, "descending ceiling leaves airborne local player below in combat space");
        Require(slab.enabled && !slab.isTrigger && !Physics2D.GetIgnoreCollision(hero.GetComponent<Collider2D>(), slab),
            "ceiling keeps actual solid local collision");
        hero.AffectedByGravity(true);
        var bounds = hero.GetComponent<Collider2D>().bounds;
        var position = hero.transform.position;
        position.y += slab.bounds.min.y - bounds.max.y - .5f;
        hero.transform.position = position;
        body.velocity = Vector2.up * 15;
        yield return new WaitForSecondsRealtime(.4f);
        Require(hero.GetComponent<Collider2D>().bounds.max.y <= slab.bounds.min.y + .15f,
            "upward airborne contact still blocks beneath lowered ceiling");
        hero.AffectedByGravity(false); body.velocity = Vector2.zero;
        hero.transform.position = new Vector3(103, 14, 0);
        Flag(role + ".ceiling-head-checked");
        yield return Until(() => Has("host.ceiling-head-checked") && Has("guest.ceiling-head-checked"), "both native ceiling head contacts complete");
        deadline = Time.realtimeSinceStartup + 60;
        while (slab.bounds.min.y < initialBottom - .2f) {
            var state = ArenaState("Battle Control");
            if (state.StartsWith("Wave ") && int.TryParse(state.Substring(5), out var wave)) wallWave = wave;
            if (Time.realtimeSinceStartup > deadline) throw new TimeoutException("Native ceiling reset stalled at " + state);
            ApproachCurrentWallWave(); DamageCurrentWallWave();
            yield return new WaitForSecondsRealtime(.1f);
        }
        Require(hero.acceptingInput && !GameManager.instance.IsInSceneTransition && !runtime.Down,
            "native following waves retract ceiling without losing player control");
        Log("CEILING RESET RESULT authority=" + ArenaManager.IsSceneHost + " bottom=" + slab.bounds.min.y);
        Flag(role + ".ceiling-reset");
        yield return Until(() => Has("host.ceiling-reset") && Has("guest.ceiling-reset"), "both native ceiling resets complete");
    }
}
