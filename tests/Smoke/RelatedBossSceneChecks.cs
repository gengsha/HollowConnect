using System;
using System.Collections;
using System.Linq;
using System.Reflection;
using Hkmp;
using Hkmp.Game.Client.Entity;
using HollowConnect;
using UnityEngine;
using UnitySceneManager = UnityEngine.SceneManagement.SceneManager;
using Object = UnityEngine.Object;

namespace HollowConnect.Smoke;

public sealed partial class RescueProbe {
    private IEnumerator RelatedBossSceneChecks() {
        const string room = "Ruins2_03";
        using var noContact = IgnoreExternalCombatDamage();
        if (!host) yield return Until(() => Has("related.first"), "authority first native entry");
        // Match the incoming native door contract for the conditional loader.
        TransitionPoint.lastEntered = "top1";
        yield return RuleEnter(room, "top1");
        if (host) Flag("related.first");
        yield return Until(() => UnitySceneManager.GetSceneByName(room + "_boss").isLoaded &&
            EntityReferences.Entities.Values.Count(e => e.Type == EntityType.WatcherKnight) == 6,
            "six native additive Watcher Knights registered");
        var hero = HeroController.instance;
        var battle = Object.FindObjectsOfType<PlayMakerFSM>().First(f => f.gameObject.scene.name == room + "_boss" && f.FsmName == "Battle Control");
        var trigger = battle.GetComponent<Collider2D>();
        Check(trigger != null && trigger.isTrigger, "native battle entry trigger");
        hero.transform.position = trigger.bounds.center; hero.GetComponent<Rigidbody2D>().velocity = Vector2.zero;
        hero.AffectedByGravity(true); Physics2D.SyncTransforms();
        yield return Until(() => Object.FindObjectsOfType<Collider2D>().Any(c => c.gameObject.scene.name == room + "_boss" &&
            c.name.StartsWith("Battle Gate", StringComparison.Ordinal) && !c.isTrigger && c.enabled), "native additive battle gate present");
        var blocked = typeof(CoopRuntime).GetMethod("IsWarpBlocked", BindingFlags.Static | BindingFlags.NonPublic);
        var gate = Object.FindObjectsOfType<Collider2D>().First(c => c.gameObject.scene.name == room + "_boss" &&
            c.name.StartsWith("Battle Gate", StringComparison.Ordinal) && !c.isTrigger && c.gameObject.layer == 8);
        Check((bool)blocked.Invoke(null, new object[] { gate, hero, room }), "warp obstruction includes actual native additive gate");
        Check(!(bool)blocked.Invoke(null, new object[] { gate, hero, "Town" }), "warp obstruction excludes unrelated room gate");

        // A tiny isolated physics fixture proves additive support is queried,
        // without altering native terrain, gates or the hero's actual position.
        var support = new GameObject("Related additive support check") { layer = 8 };
        UnitySceneManager.MoveGameObjectToScene(support, UnitySceneManager.GetSceneByName(room + "_boss"));
        support.transform.position = new Vector3(-500, -500, 0);
        var box = support.AddComponent<BoxCollider2D>(); box.size = new Vector2(16, 1);
        Physics2D.SyncTransforms();
        var landing = typeof(CoopRuntime).GetMethod("TryWarpLanding", BindingFlags.Static | BindingFlags.NonPublic);
        try {
            var args = new object[] { new Vector2(-500, -498.5f), room, Vector2.zero };
            Check((bool)landing.Invoke(null, args), "warp landing accepts supported additive floor");
            var pos = (Vector2)args[2];
            Check(Mathf.Abs(pos.x + 500) < 4 && pos.y > box.bounds.max.y, "accepted additive landing is above its support");
        } finally { Object.Destroy(support); }
        Flag(role + ".related-ready");
        yield return Until(() => Has((host ? "guest" : "host") + ".related-ready"), "both direct collision checks complete");
        if (host) {
            var reports = 0; var original = CoopContext.BossDefeated;
            CoopContext.BossDefeated = id => { reports++; original?.Invoke(id); };
            try {
                var killed = 0; var end = Time.realtimeSinceStartup + 65;
                while (killed < 6 && Time.realtimeSinceStartup < end) {
                    var live = EntityReferences.Entities.Values.Where(e => e.Type == EntityType.WatcherKnight && e.Object.Host != null)
                        .Select(e => e.Object.Host.GetComponent<HealthManager>()).Where(h => h != null && !h.isDead && h.hp > 0).ToArray();
                    var active = live.FirstOrDefault(h => h.gameObject.activeInHierarchy && !h.IsInvincible);
                    if (active == null) { yield return null; continue; }
                    active.ApplyExtraDamage(active.hp);
                    if (!active.isDead) { yield return null; continue; }
                    killed++;
                    Log("RELATED native knight death=" + killed + " remaining=" + (live.Length - 1) + " victoryReports=" + reports);
                    if (killed < 6) Check(reports == 0, "no settlement while other native knights live (" + killed + ")");
                    yield return new WaitForSecondsRealtime(.3f);
                }
                Check(killed == 6, "native waves allow all six knight deaths; state=" + battle.ActiveStateName);
                yield return Until(() => reports > 0, "last native knight reports victory");
                Flag("related.settlement-checked");
            } finally { CoopContext.BossDefeated = original; }
        } else yield return Until(() => Has("related.settlement-checked"), "authority completed grouped death check", 85);
        Flag(role + ".related-complete");
        yield return Until(() => Has((host ? "guest" : "host") + ".related-complete"), "both short checks complete");
    }
}
