using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using Hkmp.Game.Client.Entity;
using UnityEngine;
using Object = UnityEngine.Object;

namespace HollowConnect.Smoke;

public sealed partial class ProgressProbe {
    private IEnumerator CollectorKeyChecks() {
        const string tower = "Ruins2_11";
        const string fightScene = "Ruins2_11_boss";
        const string doorRoom = "Ruins2_11_b";
        var first = Environment.GetEnvironmentVariable("HOLLOWCONNECT_COLLECTOR_AUTHORITY") ?? "host";
        // Begin with the key already obtained in this unsaved profile. The
        // original door interaction still consumes it separately on each side.
        var arenaOnly = Environment.GetEnvironmentVariable("HOLLOWCONNECT_COLLECTOR_ARENA_ONLY") == "1";
        if (role != first) yield return Until(() => Has(first + ".collector-enter"), "first player owns the tower", 60);
        if (!arenaOnly) {
            PlayerData.instance.SetBool("hasLoveKey", true);
            yield return OpenUniqueDoor(doorRoom, "openedLoveDoor", "hasLoveKey");
        }

        if (GameManager.instance.sceneName != tower) {
            var entry = "right1";
            if (!arenaOnly) {
                var exit = Object.FindObjectsOfType<TransitionPoint>(true).Single(p => p.gameObject.scene.name == doorRoom && p.targetScene == tower);
                ResourceCheck(PlayerData.instance.GetBool("openedLoveDoor") && exit.GetComponent<Collider2D>() is { enabled: true },
                    "native Love Key opens the tower entrance");
                entry = exit.entryPoint;
            }
            TransitionPoint.lastEntered = entry;
            GameManager.instance.BeginSceneTransition(new GameManager.SceneLoadInfo {
                SceneName = tower, EntryGateName = entry, WaitForSceneTransitionCameraFade = true
            });
        }
        yield return Until(() => AliveScene(tower) && engine.ClientManager.SceneSnapshotReady, "native Collector tower entrance", 35);
        Freeze();
        Entity FindBoss() => EntityReferences.Entities.Values.SingleOrDefault(e => e.Type == EntityType.TheCollector && e.SourceScene == fightScene);
        yield return Until(() => FindBoss() != null, "native additive Collector fight registered");
        var boss = FindBoss() ?? throw new Exception("Collector missing from " + fightScene);
        var battle = Object.FindObjectsOfType<PlayMakerFSM>(true).Single(f => f.gameObject.scene.name == fightScene && f.name == "Battle Scene" && f.FsmName == "Control");
        var trigger = battle.GetComponent<Collider2D>();
        var area = trigger.bounds;
        Mark("COLLECTOR preflight battle=" + battle.ActiveStateName + " authority=" + boss.IsNativeAuthority + " boss=" + boss.Object.Host.name + " trigger=" + area);
        ResourceCheck(trigger.isTrigger && boss.IsNativeAuthority == (role == first), "story Collector authority and native arena trigger");
        Flag(role + ".collector-enter");
        yield return ResourceBarrier("collector-roster");
        var hero = HeroController.instance;
        void EnterArena() {
            hero.transform.position = new Vector3(area.center.x, area.center.y, 0);
            Physics2D.SyncTransforms();
        }
        if (role != first) EnterArena();

        var visible = boss.IsNativeAuthority ? boss.Object.Host : boss.Object.Client;
        var controlIndex = boss.GetClientFsms().FindIndex(f => f.FsmName == "Control");
        var snapshots = Field<List<FsmSnapshot>>(boss, "_fsmSnapshots");
        string BossState() => boss.IsNativeAuthority ? boss.Object.Host.LocateMyFSM("Control").ActiveStateName : snapshots[controlIndex].CurrentState;
        bool Awake() => visible != null && visible.activeInHierarchy && visible.GetComponent<HealthManager>().hp > 0 &&
            visible.GetComponentsInChildren<Renderer>().Any(r => r.enabled && !r.forceRenderingOff) &&
            visible.GetComponent<tk2dSpriteAnimator>().CurrentClip != null && BossState() is not (null or "" or "Init" or "Sleep");
        var deadline = Time.realtimeSinceStartup + 14;
        while (!Awake() && Time.realtimeSinceStartup < deadline) yield return null;
        Mark("COLLECTOR wake battle=" + battle.ActiveStateName + " boss=" + BossState() + " visible=" + visible.activeInHierarchy + " hero=" + hero.transform.position);
        ResourceCheck(Awake(), "both players can see an awake, living Collector when the second player triggers the arena");
        yield return ResourceBarrier("collector-awake");
        if (role == first) EnterArena();
        var gates = Object.FindObjectsOfType<PlayMakerFSM>().Where(f => f.gameObject.scene.name.StartsWith(tower, StringComparison.Ordinal) && f.FsmName == "BG Control").ToArray();
        yield return Until(() => gates.Length == 2 && gates.All(g => g.ActiveStateName == "Close 2"), "Collector gates close on local entry");
        yield return ResourceBarrier("collector-local-gates");
        var health = boss.Object.Host.GetComponent<HealthManager>();
        var death = boss.Object.Host.LocateMyFSM("Death");
        var finish = Time.realtimeSinceStartup + 55;
        while (Time.realtimeSinceStartup < finish && (battle.ActiveStateName != "End" || gates.Any(g => g.ActiveStateName != "Open"))) {
            if (boss.IsNativeAuthority && health != null) {
                if (death.ActiveStateName == "Death Wound") {
                    health.Hit(new HitInstance { Source = hero.gameObject, AttackType = AttackTypes.Nail, DamageDealt = 1,
                        Multiplier = 1, IgnoreInvulnerable = true });
                } else if (!health.IsInvincible && health.hp > 0) {
                    health.ApplyExtraDamage(health.hp);
                }
            }
            yield return new WaitForSecondsRealtime(.5f);
        }
        Mark("COLLECTOR finish battle=" + battle.ActiveStateName + " gates=" + string.Join(",", gates.Select(g => g.ActiveStateName)));
        ResourceCheck(battle.ActiveStateName == "End" && PlayerData.instance.GetBool("collectorDefeated") &&
            gates.All(g => g.ActiveStateName == "Open" && g.GetComponentsInChildren<Collider2D>(true)
                .All(c => !c.enabled || c.isTrigger || !c.gameObject.activeInHierarchy)),
            "native Collector defeat opens both gates and removes their blocking colliders");
        yield return ResourceBarrier("collector-complete");
    }
}
