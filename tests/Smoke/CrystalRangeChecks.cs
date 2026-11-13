using System;
using System.Collections;
using System.Linq;
using Hkmp.Game.Client.Entity;
using UnityEngine;

namespace HollowConnect.Smoke;

public sealed partial class CoverageProbe {
    private static bool CrystalRangeCase => !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("HOLLOWCONNECT_CRYSTAL_RANGE"));
    private static bool CrystalTarget(Entity e) => e.Type is EntityType.CrystallisedHusk or EntityType.Shardmite or
        EntityType.Glimback or EntityType.HuskMiner or EntityType.CrystalHunter or EntityType.CrystalCrawler or EntityType.CrystalTurret;

    private IEnumerator CrystalRangeChecks() {
        var first = Environment.GetEnvironmentVariable("HOLLOWCONNECT_COVERAGE_AUTHORITY") ?? "host";
        var other = host ? "guest" : "host";
        var scene = Environment.GetEnvironmentVariable("HOLLOWCONNECT_CRYSTAL_SCENE") ?? "Mines_20";
        var assert = !Environment.GetEnvironmentVariable("HOLLOWCONNECT_CRYSTAL_RANGE").StartsWith("baseline");
        if (Environment.GetEnvironmentVariable("HOLLOWCONNECT_CRYSTAL_RANGE").StartsWith("assets")) {
            Enter(scene);
            yield return Until(() => UnityEngine.SceneManagement.SceneManager.GetSceneByName(scene).isLoaded, "crystal assets inspection");
            Mark("CRYSTAL ASSETS " + scene + " gates=" + string.Join(",", UnityEngine.Object.FindObjectsOfType<TransitionPoint>(true)
                .Where(g => g.gameObject.scene.name == scene).Select(g => g.name)) + " types=" + string.Join(",", Entities.Values.Select(e => e.Type).Distinct()));
            yield break;
        }
        if (role != first) yield return Until(() => Has(first + ".crystal.entered"), "first player owns " + scene);
        Enter(scene, "right1");
        yield return Until(() => UnityEngine.SceneManagement.SceneManager.GetSceneByName(scene).isLoaded, "crystal native assets loaded");
        Mark("CRYSTAL gates=" + string.Join(",", UnityEngine.Object.FindObjectsOfType<TransitionPoint>(true)
            .Where(g => g.gameObject.scene.name == scene).Select(g => g.name)));
        yield return Until(() => In(scene) && manager.IsSceneHostDetermined, "crystal native scene ready");
        Freeze();
        HeroController.instance.IgnoreInputWithoutReset();
        HeroController.instance.transform.position = new Vector3(100, 100, 0);
        if (manager.IsSceneHost != (role == first)) throw new Exception("Crystal authority differs from first entrant");
        Flag(role + ".crystal.entered");
        yield return Until(() => Has(other + ".crystal.entered") && engine.ClientManager.SceneSnapshotReady &&
            runtime.Peers.Count == 1 && runtime.PlayerObject(runtime.Peers.Keys.Single()) != null, "both crystal peers and snapshots ready");
        var targets = Entities.Values.Where(CrystalTarget).OrderBy(e => e.Id).ToArray();
        Mark("CRYSTAL setup shared=" + CoopMod.Settings.ShareProgress + " scaling=" + CoopMod.Settings.BossHealthScaling +
            " authority=" + manager.IsSceneHost + " targets=" + targets.Length);
        foreach (var region in UnityEngine.Object.FindObjectsOfType<ActiveRegion>())
            Mark("CRYSTAL active region " + EntityIdentity.Path(region.transform) + " bounds=" + region.GetComponent<BoxCollider2D>()?.bounds +
                " size=" + region.GetComponent<BoxCollider2D>()?.size + " offset=" + region.GetComponent<BoxCollider2D>()?.offset);
        if (!targets.Any(e => e.Type == EntityType.CrystalHunter) || !targets.Any(e => e.Type == EntityType.CrystalCrawler))
            throw new Exception("Missing native CrystalHunter/CrystalCrawler in " + scene + ": " + string.Join(",", targets.Select(e => e.Type)));
        if (manager.IsSceneHost && Environment.GetEnvironmentVariable("HOLLOWCONNECT_CRYSTAL_RANGE").StartsWith("inspect"))
            foreach (var e in targets.GroupBy(e => e.Type).Select(g => g.First())) Dump(e.Object.Host);
        var home = HeroController.instance.transform.position;
        foreach (var stage in new[] { "follower", "authority" }) {
            if (stage == "authority" && !manager.IsSceneHost) HeroController.instance.transform.position = home;
            foreach (var e in targets.Where(e => e.Type is EntityType.CrystalHunter or EntityType.CrystalCrawler).GroupBy(e => e.Type).Select(g => g.First())) {
                if (stage == "authority" && e.Type == EntityType.CrystalHunter) continue;
                var key = stage + "." + e.Type;
                var approaching = (stage == "authority") == manager.IsSceneHost;
                if (approaching) HeroController.instance.transform.position =
                    (manager.IsSceneHost ? e.Object.Host : e.Object.Client).transform.position + new Vector3(2, 0, 0);
                Flag(role + ".crystal." + key);
                yield return Until(() => Has(other + ".crystal." + key), "both positioned " + key);
                var end = Time.realtimeSinceStartup + 8;
                var sample = 0f;
                var states = new System.Collections.Generic.HashSet<string>();
                var attackVisible = false;
                var awake = false;
                var origin = (manager.IsSceneHost ? e.Object.Host : e.Object.Client).transform.position;
                var moved = false;
                while (Time.realtimeSinceStartup < end) {
                    var current = manager.IsSceneHost ? e.Object.Host : e.Object.Client;
                    foreach (var f in current.GetComponents<PlayMakerFSM>()) states.Add(f.ActiveStateName);
                    moved |= Vector2.Distance(origin, current.transform.position) > 1;
                    if (e.Type == EntityType.CrystalHunter) awake |= !current.GetComponent<HealthManager>().IsInvincible;
                    else attackVisible |= current.GetComponentsInChildren<Collider2D>(true).Any(c => c.name == "Beam" &&
                        c.enabled && c.gameObject.activeInHierarchy && c.GetComponent<DamageHero>() != null && c.GetComponent<Renderer>()?.enabled == true);
                    if (Time.realtimeSinceStartup >= sample) {
                        sample = Time.realtimeSinceStartup + 1;
                        foreach (var target in new[] { e }) {
                            var go = manager.IsSceneHost ? target.Object.Host : target.Object.Client;
                            if (go == null) continue;
                            var hp = go.GetComponent<HealthManager>();
                            Mark("CRYSTAL " + key + " id=" + target.Id + " type=" + target.Type + " pos=" + go.transform.position +
                                " active=" + go.activeInHierarchy + " hp=" + hp?.hp + " inv=" + hp?.IsInvincible +
                                " hero=" + HeroController.instance.transform.position +
                                " state=" + string.Join("|", go.GetComponents<PlayMakerFSM>().Select(f => f.FsmName + "/" + f.ActiveStateName)));
                        }
                    }
                    Freeze();
                    if (approaching) HeroController.instance.transform.position =
                        (manager.IsSceneHost ? e.Object.Host : e.Object.Client).transform.position + new Vector3(2, 0, 0);
                    yield return null;
                }
                Mark("CRYSTAL states " + key + "=" + string.Join(",", states) + " awake=" + awake + " moved=" + moved + " beam=" + attackVisible);
                if (assert && (e.Type == EntityType.CrystalCrawler ? !attackVisible || manager.IsSceneHost && !states.Contains("Beam") :
                    !awake || !moved || manager.IsSceneHost && !states.Any(s => !string.IsNullOrEmpty(s))))
                    throw new Exception("Native crystal perception failed " + key + ": " + string.Join(",", states) + " beam=" + attackVisible + " awake=" + awake);
                if (assert) Mark("PASS native crystal perception " + key);
                Flag(role + ".crystal.sample." + key);
                yield return Until(() => Has(other + ".crystal.sample." + key), "both sampled " + key);
            }
        }
        var miner = targets.First(e => e.Type == EntityType.CrystalHunter);
        var minerObject = manager.IsSceneHost ? miner.Object.Host : miner.Object.Client;
        var health = minerObject.GetComponent<HealthManager>();
        var before = health.hp;
        if (manager.IsSceneHost) HeroController.instance.transform.position = home;
        Flag(role + ".crystal.attack-ready");
        yield return Until(() => Has(other + ".crystal.attack-ready"), "both ready for remote native nail attacks");
        var attackEnd = Time.realtimeSinceStartup + 10;
        var nextAttack = 0f;
        while (health != null && health.hp > 0 && Time.realtimeSinceStartup < attackEnd) {
            if (!manager.IsSceneHost) {
                var hero = HeroController.instance;
                hero.transform.position = minerObject.transform.position + new Vector3(1.4f, 0, 0);
                hero.FaceLeft();
                if (Time.realtimeSinceStartup >= nextAttack) {
                    nextAttack = Time.realtimeSinceStartup + .5f;
                    hero.Attack(GlobalEnums.AttackDirection.normal);
                }
            }
            Freeze(); Physics2D.SyncTransforms(); yield return null;
        }
        Mark("CRYSTAL remote nail hp=" + before + "->" + health?.hp + " dead=" + (health == null || health.hp <= 0));
        if (assert && health != null && health.hp > 0) throw new Exception("Remote native nail did not kill CrystalHunter; hp=" + health.hp);
        if (assert) Mark("PASS remote native nail kills shared CrystalHunter while authority stays far away");
        Flag(role + ".crystal.attack-done");
        yield return Until(() => Has(other + ".crystal.attack-done"), "both native nail observations complete");
    }
}
