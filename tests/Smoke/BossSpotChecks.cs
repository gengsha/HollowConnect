using System;
using System.Collections;
using System.Linq;
using System.Reflection;
using Hkmp.Game.Client.Entity;
using UnityEngine;
using Object = UnityEngine.Object;

namespace HollowConnect.Smoke;

public sealed partial class RescueProbe {
    private IEnumerator BossSpotChecks() {
        if (Environment.GetEnvironmentVariable("HOLLOWCONNECT_BOSS_SPOT") != "schema") {
            yield return BossSpotEncounterChecks(); yield break;
        }
        using var noContact = IgnoreExternalCombatDamage();
        var selected = Environment.GetEnvironmentVariable("HOLLOWCONNECT_BOSS_SPOT");
        var rooms = selected == "schema" ? (Environment.GetEnvironmentVariable("HOLLOWCONNECT_BOSS_SPOT_SCHEMA_ROOMS") ??
            "Mines_18,Abyss_19,Deepnest_32").Split(',') : new[] { selected };
        var authority = role == (Environment.GetEnvironmentVariable("HOLLOWCONNECT_BOSS_SPOT_AUTHORITY") ?? "host");
        var peer = host ? "guest" : "host";
        foreach (var scene in rooms) {
            var type = scene == "Mines_18" ? EntityType.CrystalGuardian : scene == "Abyss_19" ? EntityType.BrokenVessel : EntityType.Nosk;
            if (!authority) yield return Until(() => Has("spot.first." + scene), "selected first entrant " + scene);
            var entry = scene == "Abyss_19" ? "right1" : "left1";
            TransitionPoint.lastEntered = entry;
            yield return RuleEnter(scene, entry);
            var manager = Field<EntityManager>(engine.ClientManager, "_entityManager");
            yield return Until(() => manager.IsSceneHostDetermined && manager.IsSceneHost == authority &&
                EntityReferences.Entities.Values.Any(e => e.Type == type), "native spot boss and authority " + scene);
            if (authority) Flag("spot.first." + scene);
            var boss = EntityReferences.Entities.Values.Single(e => e.Type == type);
            var hero = HeroController.instance;
            hero.AffectedByGravity(false); hero.GetComponent<Rigidbody2D>().velocity = Vector2.zero;
            yield return new WaitForSecondsRealtime(1);
            Log("SPOT PREFLIGHT " + scene + " authority=" + authority + " input=" + hero.acceptingInput + " boss=" + boss.Object.Host.transform.position);
            var fsms = Object.FindObjectsOfType<PlayMakerFSM>(true).Where(f => f.gameObject.scene.name == scene ||
                f.gameObject.scene.name.StartsWith(scene + "_boss", StringComparison.Ordinal))
                .Concat(boss.Object.Host.GetComponentsInChildren<PlayMakerFSM>(true)).Distinct().ToArray();
            foreach (var fsm in fsms) {
                var signals = fsm.FsmStates.SelectMany(s => s.Transitions).Concat(fsm.Fsm.GlobalTransitions).Select(t => t.EventName).Distinct();
                Log("SPOT ROUTE " + EncounterPath(fsm.gameObject) + "/" + fsm.FsmName + "=" + fsm.ActiveStateName + " active=" +
                    fsm.gameObject.activeInHierarchy + " enabled=" + fsm.enabled + " events=" + string.Join(",", signals));
                if (signals.Contains("IK GATE OPEN")) EncounterSchema(fsm);
            }
            foreach (var fsm in fsms.Where(f => EntityReferences.Owner(f.gameObject)?.Object.Client != f.gameObject &&
                (f.name.Contains("Battle") || f.name.Contains("Gate") || f.transform.IsChildOf(boss.Object.Host.transform)))) EncounterSchema(fsm);
            foreach (var collider in fsms.SelectMany(f => f.GetComponents<Collider2D>()).Concat(boss.Object.Host.GetComponentsInChildren<Collider2D>(true)).Distinct())
                Log("SPOT COLLIDER " + EntityReferences.Capture(collider.gameObject).Replace('\n', '|') + " trigger=" + collider.isTrigger +
                    " enabled=" + collider.enabled + " active=" + collider.gameObject.activeInHierarchy + " bounds=" + collider.bounds);
            Log("SPOT ENTRIES " + string.Join(",", Object.FindObjectsOfType<TransitionPoint>(true).Where(t => t.gameObject.scene.name == scene)
                .Select(t => t.name + "@" + t.transform.position)));
            Flag(role + ".spot.schema." + scene);
            yield return Until(() => Has(peer + ".spot.schema." + scene), "both native spot schemas " + scene);
            Check(selected == "schema", "verification fixture not prepared for " + scene);
        }
    }

    // Entry setup only positions the fixture on native terrain. Combat starts
    // through native collision / HealthManager.Hit, and ends through native
    // damage and corpse actions; no state jumps or synthetic gate events.
    private IEnumerator BossSpotEncounterChecks() {
        var scene = Environment.GetEnvironmentVariable("HOLLOWCONNECT_BOSS_SPOT");
        Check(new[] { "Mines_18", "Abyss_19", "Deepnest_32" }.Contains(scene), "supported spot room " + scene);
        var authority = role == (Environment.GetEnvironmentVariable("HOLLOWCONNECT_BOSS_SPOT_AUTHORITY") ?? "host");
        var peer = host ? "guest" : "host";
        var type = scene == "Mines_18" ? EntityType.CrystalGuardian : scene == "Abyss_19" ? EntityType.BrokenVessel : EntityType.Nosk;
        var mainName = scene == "Mines_18" ? "Beam Miner" : scene == "Abyss_19" ? "IK Control" : "Mimic Spider";
        using var noContact = IgnoreExternalCombatDamage();
        On.HutongGames.PlayMaker.Fsm.hook_EnterState trace = (orig, fsm, state) => {
            if (fsm.GameObject != null && StoryFixtureScene(fsm.GameObject, scene) &&
                (fsm.Name == mainName || fsm.Name == "BG Control" || fsm.Name == "Battle Scene" ||
                 fsm.Name == "Battle Start" || fsm.GameObject.name == "infected_door" || fsm.GameObject.name.StartsWith("Corpse", StringComparison.Ordinal) || fsm.GameObject.name == "corpse"))
                Log("SPOT STATE " + EncounterPath(fsm.GameObject) + "/" + fsm.Name + "=" + state.Name);
            orig(fsm, state);
        };
        On.HutongGames.PlayMaker.Fsm.EnterState += trace;
        On.HealthManager.hook_Die deathTrace = (orig, self, direction, attack, ignore) => {
            if (StoryFixtureScene(self.gameObject, scene)) {
                var target = typeof(HealthManager).GetField("sendKilledTo", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(self) as GameObject;
                Log("SPOT DEATH BEFORE " + EncounterPath(self.gameObject) + " hp=" + self.hp + " dead=" + self.isDead + " target=" + EncounterPath(target));
            }
            orig(self, direction, attack, ignore);
            if (self != null && StoryFixtureScene(self.gameObject, scene)) Log("SPOT DEATH AFTER " + EncounterPath(self.gameObject) + " hp=" + self.hp + " dead=" + self.isDead);
        };
        On.HealthManager.Die += deathTrace;
        try {
            if (!authority) yield return Until(() => Has("spot.woken"), "boss really fighting before late entrant", 65);
            var entry = scene == "Abyss_19" ? "right1" : "left1";
            TransitionPoint.lastEntered = entry;
            yield return RuleEnter(scene, entry);
            var manager = Field<EntityManager>(engine.ClientManager, "_entityManager");
            yield return Until(() => manager.IsSceneHostDetermined && manager.IsSceneHost == authority &&
                EntityReferences.Entities.Values.Any(e => e.Type == type), "spot native boss and first-entrant authority");
            var entity = EntityReferences.Entities.Values.Single(e => e.Type == type);
            Check(entity.IsNativeAuthority == authority, "spot entity authority agrees with scene");
            var native = authority ? entity.Object.Host : entity.Object.Client;
            var health = native.GetComponent<HealthManager>();
            var main = native.GetComponents<PlayMakerFSM>().Single(f => f.FsmName == mainName);
            void Signals() {
                if (entity.Object.Host == null) return;
                var target = typeof(HealthManager).GetField("sendKilledTo", BindingFlags.Instance | BindingFlags.NonPublic)
                    .GetValue(entity.Object.Host.GetComponent<HealthManager>()) as GameObject;
                Log("SPOT SIGNAL TARGET role=" + role + " target=" + EncounterPath(target) + " receipts=" + string.Join(",",
                    entity.Object.Host.GetComponents<PlayMakerFSM>().SelectMany(f => f.FsmVariables.BoolVariables)
                        .Where(v => v.Name.StartsWith("HollowConnect Battle Start ")).Select(v => v.Name + "=" + v.Value)));
                if (target != null) foreach (var fsm in target.GetComponents<PlayMakerFSM>())
                    Log("SPOT SIGNAL RECEIVER " + fsm.FsmName + "=" + fsm.ActiveStateName + " eligible=" +
                        typeof(LocalBattleSignals).GetMethod("Starts", BindingFlags.Static | BindingFlags.NonPublic)
                            .Invoke(null, new object[] { fsm.Fsm, "BATTLE START", false }));
                if (scene == "Abyss_19") foreach (var fsm in Object.FindObjectsOfType<PlayMakerFSM>(true).Where(f => f.name == "infected_door"))
                    Log("SPOT EXIT SCHEMA " + fsm.FsmName + "=" + fsm.ActiveStateName + " owner=" + EntityReferences.Owner(fsm.gameObject)?.Type + " terminal=" +
                        typeof(BossGateCompletion).GetMethod("DefeatGuardedOpening", BindingFlags.Static | BindingFlags.NonPublic)
                            .Invoke(null, new object[] { fsm.Fsm, "IK GATE OPEN" }));
            }
            var hero = HeroController.instance;
            var body = hero.GetComponent<Rigidbody2D>();
            var gates = Object.FindObjectsOfType<PlayMakerFSM>(true).Where(f => StoryFixtureScene(f.gameObject, scene) &&
                (scene == "Abyss_19" ? f.FsmName == "Control" && f.name == "infected_door" :
                    f.FsmName == "BG Control" && (scene != "Deepnest_32" || f.name == "Battle Gate Deepnest (5)"))).ToArray();
            Check(gates.Length == (scene == "Mines_18" ? 2 : 1), "expected native arena gates; actual=" + gates.Length);
            bool Blocks(PlayMakerFSM gate) => gate.GetComponentsInChildren<Collider2D>(true)
                .Any(c => c.enabled && !c.isTrigger && c.gameObject.activeInHierarchy &&
                    (scene != "Abyss_19" || c.name == "Collider"));
            if (authority) {
                if (scene == "Mines_18") {
                    Check(main.ActiveStateName == "Sleep" && gates.All(g => !Blocks(g)), "sleeping Guardian leaves gates open");
                    hero.transform.position = StoryFloor(native.transform.position, scene) + Vector3.left * 3;
                    body.velocity = Vector2.zero; hero.AffectedByGravity(true); Physics2D.SyncTransforms();
                    yield return new WaitForSecondsRealtime(.4f);
                    Log("SPOT NATIVE WAKE HIT " + main.ActiveStateName + " invincible=" + health.IsInvincible);
                    health.Hit(new HitInstance { Source = hero.gameObject, AttackType = AttackTypes.Nail,
                        DamageDealt = 1, Multiplier = 1, Direction = 0, MagnitudeMultiplier = 1 });
                } else if (scene == "Abyss_19") {
                    var start = Object.FindObjectsOfType<PlayMakerFSM>(true).Single(f => f.gameObject.scene.name == scene && f.FsmName == "Battle Start");
                    var trigger = start.GetComponent<Collider2D>();
                    Check(trigger != null && trigger.enabled && trigger.isTrigger, "native Broken Vessel entry trigger active");
                    hero.transform.position = new Vector3(trigger.bounds.center.x, StoryFloor(native.transform.position, scene).y, 0);
                    body.velocity = Vector2.zero; hero.AffectedByGravity(true); Physics2D.SyncTransforms();
                } else {
                    Check(main.ActiveStateName == "Hollow Idle", "Nosk starts in native disguise");
                    hero.transform.position = StoryFloor(native.transform.position, scene) + Vector3.left * 4;
                    body.velocity = Vector2.zero; hero.AffectedByGravity(true); Physics2D.SyncTransforms();
                }
                var nextSample = 0f;
                yield return Until(() => {
                    if (Time.realtimeSinceStartup >= nextSample) {
                        nextSample = Time.realtimeSinceStartup + 2;
                        Log("SPOT COMBAT SAMPLE state=" + main.ActiveStateName + " hp=" + health.hp + " invincible=" + health.IsInvincible +
                            " active=" + native.activeInHierarchy + " gates=" + string.Join(",", gates.Select(g => g.ActiveStateName + ":" + Blocks(g))) +
                            " healths=" + string.Join(",", entity.Object.Host.GetComponents<HealthManager>().Select(h => h.hp + ":" + h.IsInvincible)));
                    }
                    var fighting = scene == "Deepnest_32"
                        ? main.ActiveStateName is "Charge L" or "Charge R" or "Spit 1"
                        : main.ActiveStateName == "Idle";
                    return !health.IsBlockingByDirection(0, AttackTypes.Nail) && health.hp > 0 && fighting && gates.All(Blocks);
                },
                    "native wake / transformation reaches live combat and closed gates", 50);
                // Let the native intro finish before admitting the late player.
                yield return Until(() => hero.acceptingInput && !hero.controlReqlinquished,
                    "native intro releases first entrant control", 15);
                Log("SPOT LIVE authority=" + role + " state=" + main.ActiveStateName + " hp=" + health.hp);
                Signals();
                Flag("spot.woken");
            } else {
                hero.transform.position = StoryFloor(entity.Object.Host.transform.position, scene) + Vector3.left * (scene == "Deepnest_32" ? 4 : 3);
                if (scene == "Abyss_19") {
                    var trigger = Object.FindObjectsOfType<PlayMakerFSM>(true)
                        .Single(f => f.gameObject.scene.name == scene && f.FsmName == "Battle Start").GetComponent<Collider2D>();
                    hero.transform.position = new Vector3(trigger.bounds.center.x, hero.transform.position.y, 0);
                }
                body.velocity = Vector2.zero; hero.AffectedByGravity(true); Physics2D.SyncTransforms();
                yield return Until(() => native.activeInHierarchy && health.hp > 0 && !health.IsBlockingByDirection(0, AttackTypes.Nail),
                    "late entrant receives living, damageable boss replica", 12);
            }
            // Guardian's wake closes only the waking player's doors. Remote
            // entrance broadcasts deliberately cannot lock a late player's
            // private doors; both sets still need a native final open/bench.
            if (scene != "Mines_18" || authority)
                yield return Until(() => gates.All(Blocks), "native local arena gates physically close", 15);
            else Check(gates.All(g => !Blocks(g)), "remote Guardian wake preserves late player's local entrance gates");
            var centers = gates.Select(g => {
                var collider = g.GetComponentsInChildren<Collider2D>(true).First(c => !c.isTrigger &&
                    (scene != "Abyss_19" || c.name == "Collider"));
                return collider.enabled ? collider.bounds.center : collider.transform.TransformPoint(collider.offset);
            }).ToArray();
            Log("SPOT LOCAL LIVE role=" + role + " state=" + main.ActiveStateName + " hp=" + health.hp + " gates=" + string.Join(",", gates.Select(g => g.ActiveStateName)));
            Signals();
            Flag(role + ".spot.ready");
            yield return Until(() => Has(peer + ".spot.ready"), "both spot entrants ready before lethal damage");
            if (authority) {
                yield return new WaitForSecondsRealtime(2);
                yield return Until(() => !health.IsBlockingByDirection(0, AttackTypes.Nail) && health.hp > 0, "native combat permits horizontal nail damage");
                var before = health.hp;
                health.Hit(new HitInstance { Source = hero.gameObject, AttackType = AttackTypes.Nail,
                    DamageDealt = 5, Multiplier = 1, Direction = 0, MagnitudeMultiplier = 1 });
                Check(health.hp < before && health.hp > 0, "ordinary horizontal nail hit damages living spot boss");
                yield return new WaitForSecondsRealtime(.3f);
                Log("SPOT NATIVE LETHAL hp=" + health.hp + " state=" + main.ActiveStateName);
                health.ApplyExtraDamage(health.hp);
                Flag("spot.lethal");
            }
            yield return Until(() => Has("spot.lethal"), "authority applied native lethal damage");
            if (scene == "Mines_18") {
                var battle = Object.FindObjectsOfType<PlayMakerFSM>(true).Single(f => StoryFixtureScene(f.gameObject, scene) && f.FsmName == "Battle Scene");
                yield return Until(() => battle.ActiveStateName == "Open", "Guardian corpse escape reaches native local completion", 35);
            }
            var completionSample = 0f;
            yield return Until(() => {
                if (Time.realtimeSinceStartup >= completionSample) {
                    completionSample = Time.realtimeSinceStartup + 8;
                    Log("SPOT END SAMPLE " + string.Join(",", new[] { entity.Object.Host, entity.Object.Client }.Select(go => {
                        var hm = go == null ? null : go.GetComponent<HealthManager>();
                        return go == null ? "destroyed" : EncounterPath(go) + " hp=" + hm?.hp + " dead=" + hm?.isDead + " active=" + go.activeInHierarchy;
                    })));
                    Signals();
                }
                return gates.All(g => !Blocks(g));
            }, "native death continuation physically opens every selected gate", 35);
            yield return Until(() => hero.acceptingInput && !hero.controlReqlinquished,
                "native completion leaves usable local controls", 8);
            Log("SPOT FINISH role=" + role + " gates=" + string.Join(",", gates.Select(g => g.ActiveStateName)));
            if (scene == "Mines_18") {
                var bench = Object.FindObjectsOfType<PlayMakerFSM>(true).Single(f => f.gameObject.scene.name == scene && f.name == "RestBench" && f.FsmName == "Bench Control");
                Check(bench.gameObject.activeInHierarchy, "Guardian native completion restores real bench");
            }
            for (var i = 0; i < gates.Length; i++) {
                var center = centers[i];
                var direction = scene == "Abyss_19" || scene == "Mines_18" && center.x < 29 ? -1 : 1;
                var seed = new Vector3(center.x - direction * 1.5f, center.y + 2, 0);
                var floor = StoryFloor(seed, scene);
                hero.transform.position = new Vector3(seed.x, floor.y, 0);
                body.velocity = Vector2.zero; hero.AffectedByGravity(true); Physics2D.SyncTransforms();
                using (new MonoMod.RuntimeDetour.Hook(typeof(HeroController).GetMethod("LookForInput", BindingFlags.Instance | BindingFlags.NonPublic),
                    new Action<Action<HeroController>, HeroController>((orig, self) => { orig(self); if (self.acceptingInput) self.move_input = direction; })))
                    yield return Until(() => direction * (hero.transform.position.x - center.x) > 1,
                        "actual player movement crosses spot gate " + i, 6);
                hero.move_input = 0;
                Log("SPOT WALK role=" + role + " gate=" + i + " pos=" + hero.transform.position);
            }
            Flag(role + ".spot.complete");
            yield return Until(() => Has(peer + ".spot.complete"), "both spot death and exit checks complete");
        } finally { On.HutongGames.PlayMaker.Fsm.EnterState -= trace; On.HealthManager.Die -= deathTrace; }
    }
}
