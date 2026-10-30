using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Hkmp.Game.Client.Entity;
using UnityEngine;
using Object = UnityEngine.Object;

namespace HollowConnect.Smoke;

public sealed partial class RescueProbe {
    private IEnumerator ExtraEncounterChecks() {
        var selected = Environment.GetEnvironmentVariable("HOLLOWCONNECT_EXTRA_ENCOUNTER");
        using var contactGuard = IgnoreExternalCombatDamage();
        if (selected != "schema") {
            if (selected == "Fungus3_archive_02") yield return UumuuFlowChecks();
            else if (selected.StartsWith("Fungus3_", StringComparison.Ordinal)) yield return ExtraGardenChecks(selected);
            else yield return ExtraBossChecks(selected);
            yield break;
        }
        var rooms = (Environment.GetEnvironmentVariable("HOLLOWCONNECT_EXTRA_ROOMS") ??
            "Waterways_05:right1,Hive_05:left1,Fungus3_05:left1,Fungus3_10:bot1,Fungus3_39:left1").Split(',');
        var peer = host ? "guest" : "host";
        foreach (var room in rooms) {
            var split = room.Split(':'); var scene = split[0];
            if (!host) yield return Until(() => Has("extra.first." + scene), "native first entrant " + scene);
            TransitionPoint.lastEntered = split[1];
            yield return RuleEnter(scene, split[1]);
            var hero = HeroController.instance;
            hero.AffectedByGravity(false); hero.GetComponent<Rigidbody2D>().velocity = Vector2.zero;
            yield return new WaitForSecondsRealtime(1);
            var fsms = Object.FindObjectsOfType<PlayMakerFSM>(true).Where(f => StoryFixtureScene(f.gameObject, scene)).ToArray();
            Log("EXTRA ENTRIES " + scene + " " + string.Join(",", Object.FindObjectsOfType<TransitionPoint>(true)
                .Where(t => t.gameObject.scene.name == scene).Select(t => t.name + "@" + t.transform.position)));
            foreach (var fsm in fsms.Where(f => f.name == "Battle Scene" || f.FsmName == "BG Control" ||
                f.name == "Mega Jellyfish" || f.name.Contains("Quirrel") ||
                f.name == "Dung Corpse BG" || f.name == "Dung Defender" || f.name == "Hive Knight" || f.FsmName == "summon")) {
                if (EntityReferences.Owner(fsm.gameObject)?.Object.Client == fsm.gameObject) continue;
                EncounterSchema(fsm);
                foreach (var c in fsm.GetComponents<Collider2D>()) Log("EXTRA COLLIDER " + EncounterPath(c.gameObject) +
                    " enabled=" + c.enabled + " trigger=" + c.isTrigger + " bounds=" + c.bounds);
            }
            foreach (var e in EntityReferences.Entities.Values.Where(e => e.Object.Host != null && StoryFixtureScene(e.Object.Host, scene))) {
                var h = e.Object.Host.GetComponent<HealthManager>();
                if (h == null) continue;
                Log("EXTRA ENTITY " + e.Id + ":" + e.Type + " " + EncounterPath(e.Object.Host) + " pos=" + e.Object.Host.transform.position +
                    " active=" + e.Object.Host.activeInHierarchy + " hp=" + h.hp + " battle=" + EncounterPath(typeof(HealthManager)
                        .GetField("battleScene", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(h) as GameObject) + " killedTo=" +
                    EncounterPath(typeof(HealthManager).GetField("sendKilledTo", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(h) as GameObject));
            }
            if (host) Flag("extra.first." + scene);
            Flag(role + ".extra.schema." + scene);
            yield return Until(() => Has(peer + ".extra.schema." + scene), "both native schemas " + scene);
        }
    }

    private static bool ExtraBlocks(PlayMakerFSM gate) => gate.GetComponentsInChildren<Collider2D>(true)
        .Any(c => c.enabled && !c.isTrigger && c.gameObject.activeInHierarchy);
    private Vector3 ExtraGateCenter(PlayMakerFSM gate) {
        var c = gate.GetComponents<Collider2D>().Single(c => !c.isTrigger);
        return c.enabled ? c.bounds.center : c.transform.TransformPoint(c.offset);
    }
    private IEnumerator ExtraWalkGates(string scene, PlayMakerFSM[] gates, Vector3[] centers) {
        var hero = HeroController.instance; var body = hero.GetComponent<Rigidbody2D>();
        yield return Until(() => hero.acceptingInput && !hero.controlReqlinquished, "local completion retains normal controls");
        for (var i = 0; i < gates.Length; i++) {
            var center = centers[i];
            var direction = center.x < centers.Average(c => c.x) ? -1 : 1;
            var seed = new Vector3(center.x - direction * 1.7f, center.y + 2, 0);
            // Native passages can sit above a step (Defender's left exit).
            // Start inside the visible doorway, then use normal horizontal
            // movement and gravity across it; do not select a lower corridor.
            var terrain = Physics2D.RaycastAll(new Vector2(seed.x, center.y + 4), Vector2.down, 64, 1 << 8)
                .Where(h => h.normal.y > .9f && !h.collider.isTrigger && StoryFixtureScene(h.collider.gameObject, scene)).ToArray();
            Log("EXTRA DOOR SETUP " + scene + " role=" + role + " gate=" + i + " center=" + center + " inside=" + seed +
                " terrain=" + string.Join(",", terrain.Select(h => h.point.ToString())));
            Check(terrain.Length > 0, "native terrain exists below the original doorway; floating Garden passages may have a deep drop");
            hero.transform.position = new Vector3(seed.x, center.y + 1, 0);
            hero.AffectedByGravity(true); body.velocity = Vector2.zero; Physics2D.SyncTransforms();
            var crossed = false;
            using (new MonoMod.RuntimeDetour.Hook(typeof(HeroController).GetMethod("LookForInput", BindingFlags.Instance | BindingFlags.NonPublic),
                new Action<Action<HeroController>, HeroController>((orig, self) => { orig(self); if (self.acceptingInput) self.move_input = direction; })))
                yield return Until(() => {
                    if (!crossed && direction * (hero.transform.position.x - center.x) >= 0) {
                        var box = gates[i].GetComponent<BoxCollider2D>();
                        Check(box != null, "native arena gate has a measurable box collider");
                        var halfHeight = Math.Abs(box.size.y * box.transform.lossyScale.y) * .5f;
                        var heroBounds = hero.GetComponent<Collider2D>().bounds;
                        Check(heroBounds.max.y > center.y - halfHeight && heroBounds.min.y < center.y + halfHeight,
                            "actual crossing overlaps the original closed doorway height; gate=" + center + " half=" + halfHeight + " hero=" + heroBounds);
                        crossed = true;
                    }
                    return crossed && direction * (hero.transform.position.x - center.x) > 1;
                },
                    "actual normal movement crosses extra gate " + i + " in " + scene, 6);
            hero.move_input = 0;
            Log("EXTRA WALK " + scene + " role=" + role + " gate=" + i + " center=" + center + " pos=" + hero.transform.position);
        }
    }

    private IEnumerator ExtraBossChecks(string scene) {
        Check(scene == "Waterways_05" || scene == "Hive_05", "supported extra boss room");
        var authority = role == (Environment.GetEnvironmentVariable("HOLLOWCONNECT_EXTRA_AUTHORITY") ?? "host");
        var peer = host ? "guest" : "host";
        var delayedStart = !authority && scene == "Waterways_05" && Environment.GetEnvironmentVariable("HOLLOWCONNECT_EXTRA_DELAY_START") == "1";
        var type = scene == "Waterways_05" ? EntityType.DungDefender : EntityType.HiveKnight;
        var mainName = scene == "Waterways_05" ? "Dung Defender" : "Control";
        var schemas = new HashSet<int>();
        On.HutongGames.PlayMaker.Fsm.hook_EnterState trace = (orig, fsm, state) => {
            if (fsm.GameObject != null && StoryFixtureScene(fsm.GameObject, scene) &&
                (fsm.GameObject.name == "Battle Scene" || fsm.Name == "BG Control" || fsm.GameObject.name == "Dung Corpse BG" ||
                 fsm.GameObject.name.StartsWith("Corpse", StringComparison.Ordinal)))
                Log("EXTRA STATE " + EncounterPath(fsm.GameObject) + "/" + fsm.Name + "=" + state.Name);
            if (Environment.GetEnvironmentVariable("HOLLOWCONNECT_EXTRA_SCHEMA_DEATH") == "1" && fsm.GameObject != null &&
                StoryFixtureScene(fsm.GameObject, scene) && fsm.GameObject.name.StartsWith("Corpse", StringComparison.Ordinal) &&
                schemas.Add(fsm.GetHashCode())) EncounterSchema((PlayMakerFSM)fsm.Owner);
            orig(fsm, state);
        };
        On.HutongGames.PlayMaker.Fsm.EnterState += trace;
        try {
            if (!authority) yield return Until(() => Has("extra.fighting"), "native boss fighting before late entrant", 65);
            var entry = scene == "Waterways_05" ? "right1" : "left1";
            TransitionPoint.lastEntered = entry;
            yield return RuleEnter(scene, entry);
            var manager = Field<EntityManager>(engine.ClientManager, "_entityManager");
            yield return Until(() => manager.IsSceneHostDetermined && manager.IsSceneHost == authority &&
                EntityReferences.Entities.Values.Any(e => e.Type == type), "new boss additive ownership and entity ready");
            var entity = EntityReferences.Entities.Values.Single(e => e.Type == type);
            Check(entity.IsNativeAuthority == authority, "extra boss agrees with first entrant authority");
            var native = authority ? entity.Object.Host : entity.Object.Client;
            var health = native.GetComponent<HealthManager>();
            var main = native.GetComponents<PlayMakerFSM>().Single(f => f.FsmName == mainName);
            var battle = Object.FindObjectsOfType<PlayMakerFSM>(true).Single(f => f.gameObject.scene.name == scene && f.name == "Battle Scene");
            var gates = Object.FindObjectsOfType<PlayMakerFSM>(true).Where(f => f.gameObject.scene.name == scene && f.FsmName == "BG Control").ToArray();
            Check(gates.Length == 2, "two real boss gates");
            Vector3[] centers;
            var hero = HeroController.instance; var body = hero.GetComponent<Rigidbody2D>();
            var trigger = battle.GetComponent<Collider2D>();
            Check(trigger != null && trigger.enabled && trigger.isTrigger, "new boss native entry trigger ready");
            var floor = StoryFloor(new Vector3(trigger.bounds.center.x, trigger.bounds.center.y, 0), scene);
            if (!delayedStart) hero.transform.position = new Vector3(trigger.bounds.center.x, floor.y, 0);
            hero.AffectedByGravity(true); body.velocity = Vector2.zero; Physics2D.SyncTransforms();
            var nextSample = 0f;
            yield return Until(() => {
                if (Time.realtimeSinceStartup >= nextSample) {
                    nextSample = Time.realtimeSinceStartup + 4;
                    Log("EXTRA LIVE " + scene + " role=" + role + " battle=" + battle.ActiveStateName + " boss=" + main.ActiveStateName +
                        " hp=" + health.hp + " damageable=" + !health.IsBlockingByDirection(0, AttackTypes.Nail) + " gates=" + string.Join(",", gates.Select(g => g.ActiveStateName + ":" + ExtraBlocks(g))));
                }
                return (delayedStart || gates.All(ExtraBlocks)) && health.hp > 0 && !health.IsBlockingByDirection(0, AttackTypes.Nail) &&
                    (authority ? main.ActiveStateName == "Idle" : native.activeInHierarchy) && hero.acceptingInput && !hero.controlReqlinquished &&
                    battle.ActiveStateName == (delayedStart ? "Idle" : scene == "Waterways_05" ? "Dung Start" : "Hive Knight");
            }, "native new boss intro and local gate close finish", 45);
            centers = gates.Select(ExtraGateCenter).ToArray();
            if (authority) Flag("extra.fighting");
            Flag(role + ".extra.ready");
            yield return Until(() => Has(peer + ".extra.ready"), "both first and late entrant in native boss battle");
            if (authority) {
                yield return Until(() => !health.IsBlockingByDirection(0, AttackTypes.Nail), "new boss permits normal horizontal hit");
                var hp = health.hp;
                health.Hit(new HitInstance { Source = hero.gameObject, AttackType = AttackTypes.Nail, DamageDealt = 5,
                    Multiplier = 1, Direction = 0, MagnitudeMultiplier = 1 });
                Check(health.hp < hp && health.hp > 0, "normal nail damage reaches living new boss");
                Flag("extra.hit");
            }
            yield return Until(() => Has("extra.hit") && health.hp < (scene == "Waterways_05" ? 700 : 800), "ordinary damage replicated to both players");
            Flag(role + ".extra.damaged");
            yield return Until(() => Has(peer + ".extra.damaged"), "both confirm replicated damage");
            if (authority) { health.ApplyExtraDamage(health.hp); Flag("extra.lethal"); }
            yield return Until(() => Has("extra.lethal"), "native lethal applied on authority");
            if (scene == "Waterways_05") {
                yield return Until(() => EntityReferences.Entities.Values.Any(e => e.Type == EntityType.DungDefenderCorpse), "registered native Defender corpse");
                var corpse = EntityReferences.Entities.Values.Single(e => e.Type == EntityType.DungDefenderCorpse);
                var corpseFsm = corpse.Object.Host.GetComponents<PlayMakerFSM>().Single(f => f.FsmName == "Corpse");
                var terminal = corpseFsm.FsmStates.Single(s => s.Actions.OfType<HutongGames.PlayMaker.Actions.SendEventByName>()
                    .Any(a => a.sendEvent.Value == "DUNG CORPSE FALL"));
                Check(BossGateCompletion.IsTerminal(corpse.Type, terminal, "DUNG CORPSE FALL"), "native defeat-guarded aftermath route recognized");
                Check(!BossGateCompletion.IsTerminal(EntityType.Crawlid, terminal, "DUNG CORPSE FALL") &&
                    !BossGateCompletion.IsTerminal(corpse.Type, corpseFsm.FsmStates.Single(s => s.Name == "Init"), "DUNG CORPSE FALL") &&
                    !BossGateCompletion.IsTerminal(corpse.Type, terminal, "BG CLOSE"),
                    "ordinary enemy, non-emitting state and close event cannot authorize aftermath");
            }
            if (delayedStart) {
                var presentation = Object.FindObjectsOfType<PlayMakerFSM>(true).Single(f => f.gameObject.scene.name == scene && f.name == "Dung Corpse BG" && f.FsmName == "Fall");
                yield return Until(() => presentation.ActiveStateName == "End" && gates.All(g => !ExtraBlocks(g)),
                    "admitted native corpse presentation really finishes before delayed local trigger", 35);
                Check(battle.ActiveStateName == "Idle" && PlayerData.instance.GetBool("defeatedDungDefender"),
                    "outside late entrant has native defeat metadata without starting own arena; battle=" + battle.ActiveStateName +
                    " defeated=" + PlayerData.instance.GetBool("defeatedDungDefender"));
                hero.transform.position = new Vector3(trigger.bounds.center.x, floor.y, 0);
                body.velocity = Vector2.zero; Physics2D.SyncTransforms();
                yield return Until(() => battle.ActiveStateName == "End" && gates.All(g => !ExtraBlocks(g)),
                    "later native start consumes completed presentation receipt and reopens its gates", 8);
                centers = gates.Select(ExtraGateCenter).ToArray();
            }
            nextSample = 0;
            yield return Until(() => {
                if (Time.realtimeSinceStartup >= nextSample) {
                    nextSample = Time.realtimeSinceStartup + 5;
                    Log("EXTRA END " + scene + " role=" + role + " battle=" + battle.ActiveStateName + " gates=" + string.Join(",", gates.Select(g => g.ActiveStateName + ":" + ExtraBlocks(g))));
                }
                return battle.ActiveStateName == "End" && gates.All(g => !ExtraBlocks(g));
            }, "native corpse completion opens local boss gates", 40);
            yield return ExtraWalkGates(scene, gates, centers);
            Flag(role + ".extra.complete"); yield return Until(() => Has(peer + ".extra.complete"), "both new boss exits really traversed");
        } finally { On.HutongGames.PlayMaker.Fsm.EnterState -= trace; }
    }

    private IEnumerator ExtraGardenChecks(string scene) {
        Check(scene == "Fungus3_05" || scene == "Fungus3_10", "supported native Garden waves");
        var authority = role == (Environment.GetEnvironmentVariable("HOLLOWCONNECT_EXTRA_AUTHORITY") ?? "host");
        var peer = host ? "guest" : "host";
        var waves = new HashSet<string>();
        On.HutongGames.PlayMaker.Fsm.hook_EnterState trace = (orig, fsm, state) => {
            if (fsm.GameObject != null && fsm.GameObject.scene.name == scene && (fsm.Name == "Battle Control" || fsm.Name == "BG Control")) {
                Log("EXTRA GARDEN STATE " + fsm.GameObject.name + "/" + fsm.Name + "=" + state.Name);
                if (fsm.Name == "Battle Control" && state.Name.StartsWith("Wave ", StringComparison.Ordinal)) waves.Add(state.Name);
            }
            orig(fsm, state);
        };
        On.HutongGames.PlayMaker.Fsm.EnterState += trace;
        try {
            if (!authority) yield return Until(() => Has("extra.wave1.dead"), "Garden first wave dead before late entrant", 55);
            var entry = scene == "Fungus3_05" ? "left1" : "bot1";
            TransitionPoint.lastEntered = entry; yield return RuleEnter(scene, entry);
            var manager = Field<EntityManager>(engine.ClientManager, "_entityManager");
            Check(manager.IsSceneHost == authority, "Garden first entrant authority agrees");
            var battle = Object.FindObjectsOfType<PlayMakerFSM>(true).Single(f => f.gameObject.scene.name == scene && f.FsmName == "Battle Control");
            var counter = battle.FsmVariables.FindFsmInt("Battle Enemies");
            var gates = Object.FindObjectsOfType<PlayMakerFSM>(true).Where(f => f.gameObject.scene.name == scene && f.FsmName == "BG Control").ToArray();
            Check(gates.Length == 2 && counter != null, "Garden native counter and two gates");
            Vector3[] centers;
            var trigger = battle.GetComponent<Collider2D>();
            Check(trigger != null && trigger.isTrigger && trigger.enabled, "Garden original collision trigger exists");
            var hero = HeroController.instance; hero.AffectedByGravity(false); hero.GetComponent<Rigidbody2D>().velocity = Vector2.zero;
            hero.transform.position = new Vector3(trigger.bounds.center.x, scene == "Fungus3_05" ? 48 : 11.5f, 0); Physics2D.SyncTransforms();
            yield return Until(() => battle.ActiveStateName != "Init" && gates.All(ExtraBlocks), "native local Garden trigger closes both gates");
            centers = gates.Select(ExtraGateCenter).ToArray();
            Flag(role + ".extra.garden.entered");
            var killed = new HashSet<ulong>(); var previous = ""; var firstWaveKilled = false;
            var battleField = typeof(HealthManager).GetField("battleScene", BindingFlags.Instance | BindingFlags.NonPublic);
            var deadline = Time.realtimeSinceStartup + 80;
            while (Time.realtimeSinceStartup < deadline && battle.ActiveStateName != "End") {
                var live = EntityReferences.Entities.Values.Where(e => e.Object.Host != null && e.IsNativeAuthority &&
                    e.Object.Host.GetComponent<HealthManager>() is { } h && battleField.GetValue(h) as GameObject == battle.gameObject &&
                    e.Object.Host.activeInHierarchy && h.hp > 0 && !h.isDead && !killed.Contains(e.Id) &&
                    !e.Object.Host.GetComponents<PlayMakerFSM>().Any(f => f.ActiveStateName == "Spawn Idle") &&
                    e.Object.Host.GetComponents<Collider2D>().Any(c => c.enabled && !c.isTrigger) &&
                    e.Object.Host.transform.position.x > centers.Min(c => c.x) && e.Object.Host.transform.position.x < centers.Max(c => c.x) &&
                    e.Object.Host.transform.position.y < (scene == "Fungus3_05" ? 75 : 44)).ToArray();
                var observation = battle.ActiveStateName + "/" + counter.Value + "/" + string.Join(",", live.Select(e => e.Id + ":" + e.Type));
                if (previous != observation) { previous = observation; Log("EXTRA GARDEN " + scene + " role=" + role + " state/counter/live=" + observation); }
                if (authority && live.Length > 0 && (!firstWaveKilled || Has(peer + ".extra.garden.entered"))) {
                    Check(gates.All(ExtraBlocks), "remaining native Garden wave keeps local gates closed");
                    foreach (var e in live) {
                        var h = e.Object.Host.GetComponent<HealthManager>();
                        Log("EXTRA GARDEN NATIVE KILL " + e.Id + ":" + e.Type + " at=" + e.Object.Host.transform.position + " state=" + battle.ActiveStateName);
                        h.ApplyExtraDamage(h.hp); killed.Add(e.Id);
                    }
                    if (!firstWaveKilled) { firstWaveKilled = true; Flag("extra.wave1.dead"); }
                }
                yield return new WaitForSecondsRealtime(.15f);
            }
            Check(battle.ActiveStateName == "End" && counter.Value == 0 && gates.All(g => !ExtraBlocks(g)),
                "Garden native final counter zero, End and physically open gates; state=" + battle.ActiveStateName + " counter=" + counter.Value);
            Check(waves.Count == (scene == "Fungus3_05" ? 2 : 6), "all original Garden wave states really ran; waves=" + string.Join(",", waves));
            if (authority) Check(killed.Count == (scene == "Fungus3_05" ? 3 : 11), "every native Garden encounter enemy killed exactly once; count=" + killed.Count);
            yield return ExtraWalkGates(scene, gates, centers);
            Flag(role + ".extra.complete"); yield return Until(() => Has(peer + ".extra.complete"), "both Garden exits really traversed");
        } finally { On.HutongGames.PlayMaker.Fsm.EnterState -= trace; }
    }
}
