using System;
using System.Collections;
using System.Linq;
using System.Reflection;
using Hkmp.Game.Client.Entity;
using HutongGames.PlayMaker;
using UnityEngine;
using Object = UnityEngine.Object;

namespace HollowConnect.Smoke;

public sealed partial class RescueProbe {
    private IEnumerator StoryBossGateChecks() {
        if (Environment.GetEnvironmentVariable("HOLLOWCONNECT_STORY_GATE_CHECK").StartsWith("mantis")) {
            yield return MantisStoryGateChecks(); yield break;
        }
        const string room = "Ruins2_03";
        var mode = Environment.GetEnvironmentVariable("HOLLOWCONNECT_STORY_GATE_CHECK");
        var authorityRole = Environment.GetEnvironmentVariable("HOLLOWCONNECT_STORY_GATE_AUTHORITY") ?? "host";
        var authority = role == authorityRole;
        var peer = host ? "guest" : "host";
        using var noContact = IgnoreExternalCombatDamage();
        if (!authority) yield return Until(() => Has("gates.first"), "first native story arena entrant");
        TransitionPoint.lastEntered = "top1";
        yield return RuleEnter(room, "top1");
        if (authority) Flag("gates.first");
        yield return Until(() => EntityReferences.Entities.Values.Count(e => e.Type == EntityType.WatcherKnight) == 6,
            "six native additive knights registered");
        var knights = EntityReferences.Entities.Values.Where(e => e.Type == EntityType.WatcherKnight).ToArray();
        Check(knights.All(e => e.SourceScene == room + "_boss" && e.IsNativeAuthority == authority),
            "all six native knights have expected additive scene and authority");
        var fsms = Object.FindObjectsOfType<PlayMakerFSM>(true).Where(f => f.gameObject.scene.name == room + "_boss").ToArray();
        var battle = fsms.Single(f => f.FsmName == "Battle Control" && LocalBattleCompletion.IsLocal(f.gameObject));
        var gates = fsms.Where(f => f.FsmName == "BG Control").ToArray();
        EncounterSchema(battle);
        foreach (var fsm in gates) EncounterSchema(fsm);
        foreach (var fsm in knights[0].Object.Host.GetComponents<PlayMakerFSM>()) EncounterSchema(fsm);
        Check(gates.Length == 2, "two native local Watcher arena gates; actual=" + gates.Length);
        var hero = HeroController.instance;
        var body = hero.GetComponent<Rigidbody2D>();
        var trigger = battle.GetComponent<Collider2D>();
        Check(trigger != null && trigger.enabled && trigger.isTrigger, "native Watcher entry trigger exists");
        hero.transform.position = trigger.bounds.center;
        body.velocity = Vector2.zero; hero.AffectedByGravity(true); Physics2D.SyncTransforms();
        bool Blocks(PlayMakerFSM gate) => gate.GetComponentsInChildren<Collider2D>(true)
            .Any(c => c.enabled && !c.isTrigger && c.gameObject.activeInHierarchy);
        yield return Until(() => gates.All(Blocks), "both native local Watcher gates really close");
        var gatePositions = gates.Select(g => g.GetComponentsInChildren<Collider2D>(true)
            .First(c => c.enabled && !c.isTrigger).bounds.center).ToArray();
        Flag(role + ".gates.ready");
        yield return Until(() => Has(peer + ".gates.ready"), "both real Watcher entries complete");
        if (mode == "watchers-schema") yield break;
        var battleField = typeof(HealthManager).GetField("battleScene", BindingFlags.Instance | BindingFlags.NonPublic);
        var notifiedField = typeof(HealthManager).GetField("notifiedBattleScene", BindingFlags.Instance | BindingFlags.NonPublic);
        var end = Time.realtimeSinceStartup + 90;
        var previous = "";
        while (Time.realtimeSinceStartup < end) {
            var living = knights.Where(e => e.Object.Host != null && e.Object.Host.GetComponent<HealthManager>() is { isDead: false, hp: > 0 }).ToArray();
            var sample = "battle=" + battle.ActiveStateName + " counter=" + battle.FsmVariables.FindFsmInt("Battle Enemies")?.Value +
                " living=" + living.Length + " gates=" + string.Join(",", gates.Select(g => g.ActiveStateName + ":" + Blocks(g)));
            if (sample != previous) { previous = sample; Log("STORY GATE SAMPLE " + sample); }
            if (living.Length == 0) break;
            if (authority) {
                var next = living.Select(e => e.Object.Host.GetComponent<HealthManager>())
                    .FirstOrDefault(h => h.gameObject.activeInHierarchy && !h.IsInvincible);
                if (next != null) {
                    Log("STORY NATIVE DAMAGE " + next.name + " hp=" + next.hp + " battle=" + battleField.GetValue(next) +
                        " notified=" + notifiedField.GetValue(next));
                    next.ApplyExtraDamage(next.hp);
                }
            }
            yield return new WaitForSecondsRealtime(.2f);
        }
        Check(knights.All(e => e.Object.Host == null || e.Object.Host.GetComponent<HealthManager>() is { isDead: true, hp: <= 0 }),
            "all six knights finish through native wave activation and health death");
        // Native Pause 5 waits five seconds, then Music End waits two more;
        // the doors still have their own opening animation after those states.
        for (var i = 0; i < 100 && gates.Any(Blocks); i++) {
            Log("STORY GATE FINAL WAIT battle=" + battle.ActiveStateName + " counter=" + battle.FsmVariables.FindFsmInt("Battle Enemies")?.Value +
                " gates=" + string.Join(",", gates.Select(g => g.ActiveStateName + ":" + Blocks(g))));
            yield return new WaitForSecondsRealtime(.2f);
        }
        Check(gates.All(g => !Blocks(g)), "both native Watcher gates open after final death; battle=" + battle.ActiveStateName +
            " counter=" + battle.FsmVariables.FindFsmInt("Battle Enemies")?.Value);
        for (var i = 0; i < gates.Length; i++) {
            var center = gatePositions[i];
            var direction = center.x < trigger.bounds.center.x ? -1 : 1;
            hero.transform.position = new Vector3(center.x - direction * 1.5f, hero.transform.position.y, hero.transform.position.z);
            body.velocity = Vector2.zero; Physics2D.SyncTransforms();
            using (new MonoMod.RuntimeDetour.Hook(typeof(HeroController).GetMethod("LookForInput", BindingFlags.Instance | BindingFlags.NonPublic),
                new Action<Action<HeroController>, HeroController>((orig, self) => { orig(self); if (self.acceptingInput) self.move_input = direction; })))
                yield return Until(() => direction * (hero.transform.position.x - center.x) > 1,
                    "actual native movement crosses open Watcher gate " + i, 5);
        }
        hero.move_input = 0;
        Flag(role + ".gates.complete");
        yield return Until(() => Has(peer + ".gates.complete"), "both native gate checks complete");
    }
}
