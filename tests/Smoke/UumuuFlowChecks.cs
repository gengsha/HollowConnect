using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using Hkmp.Game.Client.Entity;
using UnityEngine;
using Object = UnityEngine.Object;

namespace HollowConnect.Smoke;

public sealed partial class RescueProbe {
    private IEnumerator UumuuFlowChecks() {
        const string scene = "Fungus3_archive_02";
        var authority = role == (Environment.GetEnvironmentVariable("HOLLOWCONNECT_EXTRA_AUTHORITY") ?? "host");
        var peer = host ? "guest" : "host";
        var observed = new HashSet<string>();
        On.HutongGames.PlayMaker.Fsm.hook_EnterState trace = (orig, fsm, state) => {
            if (fsm.GameObject != null && StoryFixtureScene(fsm.GameObject, scene) &&
                (fsm.Name == "Mega Jellyfish" || fsm.Name == "BG Control" || fsm.GameObject.name == "Battle Scene")) {
                Log("UUMUU FLOW STATE " + EncounterPath(fsm.GameObject) + "/" + fsm.Name + "=" + state.Name);
                if (fsm.Name == "Mega Jellyfish") observed.Add(state.Name);
            }
            orig(fsm, state);
        };
        On.HutongGames.PlayMaker.Fsm.EnterState += trace;
        try {
            if (!authority) yield return Until(() => Has("uumuu.live"), "native Uumuu is fighting before late entrant", 60);
            TransitionPoint.lastEntered = "top1";
            yield return RuleEnter(scene, "top1");
            Check(UnityEngine.SceneManagement.SceneManager.GetSceneByName(scene + "_boss").isLoaded, "native Uumuu additive scene loaded");
            var manager = Field<EntityManager>(engine.ClientManager, "_entityManager");
            yield return Until(() => manager.IsSceneHostDetermined && manager.IsSceneHost == authority &&
                EntityReferences.Entities.Values.Any(e => e.Type == EntityType.Uumuu), "Uumuu first entrant authority ready");
            var entity = EntityReferences.Entities.Values.Single(e => e.Type == EntityType.Uumuu);
            Check(entity.IsNativeAuthority == authority, "Uumuu entity has expected authority");
            var go = authority ? entity.Object.Host : entity.Object.Client;
            var health = go.GetComponent<HealthManager>();
            var main = go.GetComponents<PlayMakerFSM>().Single(f => f.FsmName == "Mega Jellyfish");
            var battle = Object.FindObjectsOfType<PlayMakerFSM>(true).Single(f => f.gameObject.scene.name == scene + "_boss" && f.name == "Battle Scene" && f.FsmName == "Control");
            var gates = Object.FindObjectsOfType<PlayMakerFSM>(true).Where(f => StoryFixtureScene(f.gameObject, scene) && f.FsmName == "BG Control").ToArray();
            Check(gates.Length == 3, "Uumuu has three native arena barriers");
            var hero = HeroController.instance; var body = hero.GetComponent<Rigidbody2D>();
            var trigger = battle.GetComponent<Collider2D>();
            Check(trigger != null && trigger.enabled && trigger.isTrigger, "native Uumuu battle collision trigger exists");
            hero.AffectedByGravity(false); hero.transform.position = trigger.bounds.center; body.velocity = Vector2.zero; Physics2D.SyncTransforms();
            yield return Until(() => battle.ActiveStateName == "Start" && gates.All(ExtraBlocks), "Uumuu own native trigger closes every local gate");
            var centers = gates.Select(ExtraGateCenter).ToArray();
            var platform = Object.FindObjectsOfType<Collider2D>().Single(c => c.gameObject.scene.name == scene &&
                c.name == "fung_temple_plat_float (5)" && c.gameObject.layer == 8 && !c.isTrigger);
            var heroBounds = hero.GetComponent<Collider2D>().bounds;
            var stand = new Vector3(platform.bounds.center.x, platform.bounds.max.y + heroBounds.extents.y -
                (heroBounds.center.y - hero.transform.position.y) + .08f, 0);
            hero.transform.position = stand; body.velocity = Vector2.zero; hero.AffectedByGravity(true); Physics2D.SyncTransforms();
            yield return Until(() => hero.cState.onGround && hero.acceptingInput && !hero.controlReqlinquished, "real native Uumuu platform supports controllable hero");
            if (authority) {
                yield return Until(() => main.ActiveStateName == "Idle" && health.IsInvincible, "native Uumuu shielded combat really starts", 25);
                var hp = health.hp;
                health.Hit(new HitInstance { Source = hero.gameObject, AttackType = AttackTypes.Nail, DamageDealt = 5,
                    Multiplier = 1, Direction = 0, MagnitudeMultiplier = 1 });
                Check(health.hp == hp, "intact Uumuu membrane blocks ordinary nail damage");
                Flag("uumuu.live");
            }
            Flag(role + ".uumuu.ready"); yield return Until(() => Has(peer + ".uumuu.ready"), "both Uumuu entrants ready");
            Check(EntityReferences.Entities.Values.Count(e => e.Type == EntityType.UumuuQuirrel) == 1, "exactly one networked Quirrel helper on each client");
            var nextSample = 0f;
            void Sample() {
                if (Time.realtimeSinceStartup < nextSample) return;
                nextSample = Time.realtimeSinceStartup + 6;
                Log("UUMUU FLOW SAMPLE role=" + role + " state=" + main.ActiveStateName + " hp=" + health.hp + " shield=" + health.IsInvincible +
                    " helper=" + string.Join(",", EntityReferences.Entities.Values.Where(e => e.Type == EntityType.UumuuQuirrel).SelectMany(e =>
                        (authority ? e.Object.Host : e.Object.Client).GetComponents<PlayMakerFSM>().Select(f => f.FsmName + ":" + f.ActiveStateName))));
            }
            if (authority) {
                yield return Until(() => { Sample(); return main.ActiveStateName == "Wounded" && !health.IsInvincible; },
                    "native Quirrel slash naturally opens first Uumuu damage window", 65);
                Check(observed.Contains("Slash"), "native Quirrel slash ran before membrane opened");
                var hp = health.hp;
                health.Hit(new HitInstance { Source = hero.gameObject, AttackType = AttackTypes.Nail, DamageDealt = 5,
                    Multiplier = 1, Direction = 0, MagnitudeMultiplier = 1 });
                Check(health.hp < hp && health.hp > 0, "normal nail damages Uumuu only inside native vulnerability window");
                System.IO.File.WriteAllText(System.IO.Path.Combine(root, "uumuu.damaged"), health.hp.ToString());
            }
            yield return Until(() => Has("uumuu.damaged") && health.hp == int.Parse(System.IO.File.ReadAllText(System.IO.Path.Combine(root, "uumuu.damaged"))),
                "Quirrel-window nail damage replicated to both clients", 80);
            Flag(role + ".uumuu.damaged");
            yield return Until(() => Has(peer + ".uumuu.damaged"), "both confirm same nonlethal Uumuu health");
            if (authority) {
                yield return Until(() => { Sample(); return observed.Contains("Recover") && health.IsInvincible; }, "native membrane regenerates after first vulnerability", 12);
                Check(gates.All(ExtraBlocks) && battle.ActiveStateName == "Start", "first Quirrel window never finishes battle or opens doors");
                var hp = health.hp;
                health.Hit(new HitInstance { Source = hero.gameObject, AttackType = AttackTypes.Nail, DamageDealt = 5,
                    Multiplier = 1, Direction = 0, MagnitudeMultiplier = 1 });
                Check(health.hp == hp, "regenerated membrane again blocks normal damage");
                Flag("uumuu.recovered");
                yield return Until(() => { Sample(); return main.ActiveStateName == "Wounded" && !health.IsInvincible; },
                    "native Quirrel produces a second independent vulnerability window", 65);
                health.Hit(new HitInstance { Source = hero.gameObject, AttackType = AttackTypes.Nail, DamageDealt = health.hp + 1,
                    Multiplier = 1, Direction = 0, MagnitudeMultiplier = 1 });
                Flag("uumuu.lethal");
            }
            yield return Until(() => Has("uumuu.recovered"), "authority confirms native membrane regeneration", 20);
            yield return Until(() => Has("uumuu.lethal"), "authority uses native second-window lethal hit", 80);
            yield return Until(() => battle.ActiveStateName == "End" && gates.All(g => !ExtraBlocks(g)), "Uumuu native death finishes both local arenas and opens three gates", 20);
            Check(PlayerData.instance.GetBool("defeatedMegaJelly"), "local Uumuu native defeat flag recorded");
            Check(hero.acceptingInput && !hero.controlReqlinquished && !PlayerData.instance.GetBool("monomonDefeated"), "postbattle controls usable and dreamer progression remains separate");
            var sideIndices = centers.Select((c, i) => i).Where(i => centers[i].y < 120).ToArray();
            Check(sideIndices.Length == 2, "two horizontal Uumuu arena passages selected from native closed barrier geometry");
            yield return ExtraWalkGates(scene, sideIndices.Select(i => gates[i]).ToArray(), sideIndices.Select(i => centers[i]).ToArray());
            Flag(role + ".uumuu.complete"); yield return Until(() => Has(peer + ".uumuu.complete"), "both Uumuu clients complete real postbattle passage checks");
        } finally { On.HutongGames.PlayMaker.Fsm.EnterState -= trace; }
    }
}
