using System;
using System.Collections;
using System.Linq;
using Hkmp.Game.Client.Entity;
using UnityEngine;
using Object = UnityEngine.Object;

namespace HollowConnect.Smoke;

public sealed partial class RescueProbe {
    private IEnumerator GreenpathMossKnightCheck() {
        const string scene = "Fungus1_32";
        var authority = role == (Environment.GetEnvironmentVariable("HOLLOWCONNECT_EXTRA_AUTHORITY") ?? "host");
        var peer = host ? "guest" : "host";
        if (!authority) yield return Until(() => Has("greenpath.authority-ready"), "scene authority enters Greenpath first");
        TransitionPoint.lastEntered = "left1";
        yield return RuleEnter(scene);

        var manager = Field<EntityManager>(engine.ClientManager, "_entityManager");
        yield return Until(() => manager.IsSceneHostDetermined && manager.IsSceneHost == authority, "Greenpath scene authority ready");
        var battle = Object.FindObjectsOfType<PlayMakerFSM>().Single(f => f.gameObject.scene.name == scene && f.FsmName == "Battle Control");
        var gates = Object.FindObjectsOfType<PlayMakerFSM>().Where(f => f.gameObject.scene.name == scene && f.FsmName == "BG Control").ToArray();
        var counter = battle.FsmVariables.FindFsmInt("Battle Enemies");
        var trigger = battle.GetComponent<Collider2D>();
        var knight = EntityReferences.Entities.Values.Single(e => e.Type == EntityType.MossKnight && e.Object.Host.name == "Moss Knight B");
        var native = knight.Object.Host;
        var control = native.LocateMyFSM("Moss Knight Control");
        var health = native.GetComponent<HealthManager>();
        var hero = HeroController.instance;
        var body = hero.GetComponent<Rigidbody2D>();
        hero.AffectedByGravity(false);
        body.velocity = Vector2.zero;
        Check(LocalBattleCompletion.IsLocal(battle.gameObject) && battle.ActiveStateName == "Detect" && counter?.Value == 0 &&
            trigger != null && trigger.isTrigger && trigger.enabled && gates.Length == 2 && gates.All(g => !ExtraBlocks(g)),
            "native Greenpath controller, open gates and enabled entry trigger");
        Check(control != null && control.FsmStates.Single(s => s.Name == "Sleep").Transitions.Any(t => t.EventName == "BATTLE START") &&
            !trigger.bounds.Intersects(hero.GetComponent<Collider2D>().bounds), "original sleeping knight exists and hero starts outside trigger");
        var gateCenters = gates.Select(ExtraGateCenter).ToArray();
        var entryPosition = new Vector3(trigger.bounds.center.x, native.transform.position.y, 0);

        if (authority) {
            yield return Until(() => control.ActiveStateName == "Sleep" && health.IsInvincible, "authority's native Moss Knight is asleep");
            Flag("greenpath.authority-ready");
            yield return Until(() => Has(peer + ".greenpath.closed"), "peer collision closed both native Greenpath gates");
            var deadline = Time.realtimeSinceStartup + 8;
            while ((health.IsInvincible || !native.GetComponents<Collider2D>().Any(c => c.enabled && !c.isTrigger)) &&
                Time.realtimeSinceStartup < deadline) yield return null;
            Log("GREENPATH AUTHORITY battle=" + battle.ActiveStateName + " counter=" + counter.Value + " knight=" + control.ActiveStateName +
                " hp=" + health.hp + " invincible=" + health.IsInvincible + " hero=" + hero.transform.position);
            Check(!health.IsInvincible && health.hp > 0 && native.GetComponents<Collider2D>().Any(c => c.enabled && !c.isTrigger),
                "peer's native trigger wakes Moss Knight while scene authority is outside");
            Check(gates.All(g => !ExtraBlocks(g)) && !trigger.bounds.Intersects(hero.GetComponent<Collider2D>().bounds),
                "authority stays outside with both native gates open");
            yield return Until(() => Has(peer + ".greenpath.awake"), "peer sees awake living Moss Knight before damage");
            health.ApplyExtraDamage(health.hp);
            yield return Until(() => Has(peer + ".greenpath.opened"), "peer's original death counter opens both gates");
        }

        hero.transform.position = entryPosition;
        Physics2D.SyncTransforms();
        if (!authority) {
            yield return Until(() => battle.ActiveStateName == "Start" && counter.Value == 1 && gates.All(ExtraBlocks),
                "native entry sets counter to one and closes both Greenpath gates");
            Flag(role + ".greenpath.closed");
            yield return Until(() => knight.Object.Client is { activeInHierarchy: true } replica &&
                replica.GetComponent<HealthManager>() is { IsInvincible: false, hp: > 0 } &&
                replica.GetComponents<Collider2D>().Any(c => c.enabled && !c.isTrigger), "awake, damageable Moss Knight replicated inside arena", 12);
            Flag(role + ".greenpath.awake");
        }
        yield return Until(() => battle.ActiveStateName == "End" && counter.Value == 0 && gates.All(g => !ExtraBlocks(g)),
            "Greenpath native counter reaches zero and both gates open", 12);
        Flag(role + ".greenpath.opened");
        yield return ExtraWalkGates(scene, gates, gateCenters);
        Flag(role + ".greenpath.complete");
        yield return Until(() => Has(peer + ".greenpath.complete"), "both players could walk through both Greenpath gates");
    }
}
