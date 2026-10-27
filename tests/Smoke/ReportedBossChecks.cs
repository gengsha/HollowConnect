using System;
using System.Collections;
using System.Linq;
using System.Reflection;
using Hkmp;
using Hkmp.Game.Client.Entity;
using Hkmp.Util;
using HollowConnect;
using UnityEngine;
using Object = UnityEngine.Object;

namespace HollowConnect.Smoke;

public sealed partial class RescueProbe {
    private IEnumerator ReportedBossChecks() {
        using var noContact = IgnoreExternalCombatDamage();
        var selected = Environment.GetEnvironmentVariable("HOLLOWCONNECT_REPORTED_BOSS_CHECK");
        var hero = HeroController.instance;
        if (selected == "hazard-risk") {
            CheckDamageRankingShortcut();
            yield return NativeTerrainRescueRiskChecks();
            yield break;
        }
        var sawFixedZap = false;
        using var placement = new MonoMod.RuntimeDetour.Hook(typeof(CoopRuntime).GetMethod("TickStoryRescuePlacement", BindingFlags.Instance | BindingFlags.NonPublic),
            new Action<Action<CoopRuntime>, CoopRuntime>((orig, self) => {
                if (self == runtime && runtime.Down && !sawFixedZap) {
                    var size = Field<Vector2>(runtime, "rescueBodySize");
                    var offset = Field<Vector2>(runtime, "rescueBodyOffset");
                    var zap = Physics2D.OverlapBoxAll((Vector2)runtime.DeathPosition + offset, size * .94f, 0).FirstOrDefault(c =>
                        c.name.Contains("Multizap") && EntityReferences.Owner(c.gameObject) == null &&
                        c.GetComponentInParent<DamageHero>() is { enabled: true, hazardType: 1 });
                    if (zap != null) {
                        sawFixedZap = true; Flag("uumuu.fixed-zap-covered-shade");
                        Log("NATIVE FIXED ZAP COVERS SHADE " + zap.name + " serial=" + runtime.DeathSerial + " bounds=" + zap.bounds);
                    }
                }
                orig(self);
            }));
        using var outgoing = new MonoMod.RuntimeDetour.Hook(typeof(CoopRuntime).GetMethod("Send", BindingFlags.Instance | BindingFlags.NonPublic),
            new Action<Action<CoopRuntime, Message>, CoopRuntime, Message>((orig, self, m) => {
                if (self == runtime && m.Kind is Kind.Down or Kind.GiveUpRequest or Kind.RescueUnavailable or Kind.RescueAnchorRequest)
                    Log("RESCUE SEND " + m.Kind + " serial=" + m.Serial + " aux=" + m.Aux + " anchor=" + runtime.DeathPosition + " used=" + runtime.RescuesUsed);
                if (self == runtime && m.Kind is Kind.GiveUpRequest or Kind.RescueUnavailable) {
                    Log("RESCUE RETURN STACK " + Environment.StackTrace);
                    Log("RESCUE RETURN EVENTS " + string.Join(";", Field<HollowConnect.Core.DebugEventRing>(runtime, "debugEvents").Snapshot().Reverse().Take(6).Select(e => e.Code)));
                    foreach (var d in Object.FindObjectsOfType<DamageHero>().Where(d => d.enabled && StoryFixtureScene(d.gameObject, GameManager.instance.sceneName) &&
                        Vector2.Distance(d.transform.position, runtime.DeathPosition) < 8))
                        Log("RESCUE RETURN HAZARD " + d.name + " owner=" + EntityReferences.Owner(d.gameObject)?.Type + " hp=" + d.GetComponentInParent<HealthManager>()?.name +
                            " hazard=" + d.hazardType + " pos=" + d.transform.position + " colliders=" + string.Join(";", d.GetComponents<Collider2D>().Select(c => c.enabled + ":" + c.bounds)));
                    Log("RESCUE RETURN PEERS " + string.Join(";", runtime.Peers.Select(p => p.Key + ":" + p.Value.Life + ":" + p.Value.State?.Scene + ":" + p.Value.State?.X + "," + p.Value.State?.Y)));
                }
                orig(self, m);
            }));
        using var incoming = new MonoMod.RuntimeDetour.Hook(typeof(CoopRuntime).GetMethod("Receive", BindingFlags.Instance | BindingFlags.NonPublic),
            new Action<Action<CoopRuntime, Message>, CoopRuntime, Message>((orig, self, m) => {
                if (self == runtime && m.Kind is Kind.Revived or Kind.Returning or Kind.Wipe or Kind.GiveUpDenied)
                    Log("RESCUE RECEIVE " + m.Kind + " id=" + m.Id + " serial=" + m.Serial + " aux=" + m.Aux + " reason=" + m.Name);
                orig(self, m);
            }));
        var scenes = selected.Contains("uumuu") ? new[] { "Fungus3_archive_02" } : selected == "hornet" ? new[] { "GG_Hornet_2" } : new[] { "GG_Hornet_2", "Fungus3_archive_02" };
        foreach (var scene in scenes) {
            if (!host) yield return Until(() => Has(scene + ".first"), "authority enters " + scene);
            yield return RuleEnter(scene, scene.StartsWith("GG_") ? "door_dreamEnter" : "top1");
            if (host) Flag(scene + ".first");
            yield return StoryTogether(scene + ".entered");
            yield return new WaitForSecondsRealtime(2);
            Log("REPORTED SCENE " + scene + " hero=" + hero.transform.position);
            foreach (var f in Object.FindObjectsOfType<PlayMakerFSM>(true).Where(f => selected.StartsWith("schema") &&
                StoryFixtureScene(f.gameObject, scene) && (f.name.Contains("Barb") || f.name.Contains("Jelly") || f.name.Contains("Hornet")))) {
                Log("FSM " + f.name + "/" + f.FsmName + " enabled=" + f.enabled + " state=" + f.ActiveStateName +
                    " states=" + string.Join(";", f.FsmStates.Select(s => s.Name + "[" + string.Join(",", s.Transitions.Select(t => t.EventName + "->" + t.ToState)) + "]")));
            }
            if (scene == "GG_Hornet_2") {
                var boss = EntityReferences.Entities.Values.Single(e => e.Type == EntityType.Hornet);
                if (host) {
                    var hp = boss.Object.Host.GetComponent<HealthManager>();
                    yield return Until(() => !hp.IsInvincible, "native Hornet fight starts");
                    hp.ApplyExtraDamage(hp.hp * 2 / 3);
                }
                yield return Until(() => EntityReferences.Entities.Values.Any(e => e.Type == EntityType.HornetSentinelSpike &&
                    (host ? e.Object.Host : e.Object.Client).activeInHierarchy), "natural Hornet barbs", 65);
                foreach (var e in EntityReferences.Entities.Values.Where(e => e.Type == EntityType.HornetSentinelSpike).ToArray()) {
                    var go = host ? e.Object.Host : e.Object.Client;
                    foreach (var f in go.GetComponentsInChildren<PlayMakerFSM>(true))
                        Log("BARB " + e.Id + " host=" + host + " " + f.FsmName + " state=" + f.ActiveStateName +
                            " states=" + string.Join(";", f.FsmStates.Select(s => s.Name + "[" + string.Join(",", s.Transitions.Select(t => t.EventName + "->" + t.ToState)) + "] actions=" + string.Join(",", s.Actions.Select(a => a.GetType().Name)))));
                    foreach (var t in go.GetComponentsInChildren<TinkEffect>(true)) Log("BARB TINK event=" + t.FSMEvent + " sends=" + t.sendFSMEvent);
                }
                if (selected == "hornet") {
                    foreach (var hitter in new[] { "host", "guest" }) {
                        Entity target = null;
                        yield return Until(() => (target = EntityReferences.Entities.Values.FirstOrDefault(e => e.Type == EntityType.HornetSentinelSpike &&
                            (host ? e.Object.Host : e.Object.Client) is { activeInHierarchy: true } go &&
                            go.GetComponentsInChildren<TinkEffect>().Any(t => t.GetComponent<Collider2D>() is { enabled: true }))) != null, "active barb for " + hitter, 45);
                        // Both endpoints agree on the lifetime before a real input swing.
                        if (host) System.IO.File.WriteAllText(System.IO.Path.Combine(root, hitter + ".barb"), target.Id.ToString());
                        yield return Until(() => Has(hitter + ".barb"), "barb identity for " + hitter);
                        var id = ulong.Parse(System.IO.File.ReadAllText(System.IO.Path.Combine(root, hitter + ".barb")));
                        yield return Until(() => EntityReferences.Entities.ContainsKey(id), "matching barb replica for " + hitter);
                        target = EntityReferences.Entities[id];
                        var go = host ? target.Object.Host : target.Object.Client;
                        var start = go.transform.position;
                        if (role == hitter) {
                            hero.transform.position = start + Vector3.left * 1.6f;
                            hero.GetComponent<Rigidbody2D>().velocity = Vector2.zero; hero.AffectedByGravity(false); hero.FaceRight();
                        } else { hero.transform.position = start + Vector3.left * 6; hero.AffectedByGravity(false); }
                        Physics2D.SyncTransforms();
                        yield return StoryTogether(hitter + ".aimed"); yield return new WaitForSecondsRealtime(.7f);
                        if (role == hitter) hero.Attack(GlobalEnums.AttackDirection.normal);
                        yield return new WaitForSecondsRealtime(1);
                        var visible = go != null && go.activeInHierarchy && go.GetComponentsInChildren<TinkEffect>().Any(t => t.GetComponent<Collider2D>() is { enabled: true });
                        Check(!visible || Vector2.Distance(go.transform.position, start) > 1, "real " + hitter + " nail breaks synchronized barb");
                        yield return StoryTogether(hitter + ".broken");
                    }
                }
            } else {
                if (selected.StartsWith("uumuu")) {
                    Check(runtime.RescueEnabled && runtime.RescueLimit == 0, "explicit unlimited rescue active");
                    var battles = Object.FindObjectsOfType<PlayMakerFSM>(true).Where(f => f.name == "Battle Scene" && StoryFixtureScene(f.gameObject, scene)).ToArray();
                    Check(battles.Any(f => f.FsmName == "Control"), "native Uumuu Battle Scene/Control present; actual=" +
                        string.Join(";", battles.Select(f => f.FsmName + ":" + f.ActiveStateName)));
                    var battle = battles.Single(f => f.FsmName == "Control");
                    var trigger = battle.GetComponent<Collider2D>();
                    Check(trigger != null && trigger.isTrigger && trigger.enabled, "native Uumuu battle entry trigger " + battle.ActiveStateName);
                    Log("UUMUU ENTRY " + trigger.bounds);
                    hero.transform.position = trigger.bounds.center; Physics2D.SyncTransforms();
                    yield return new WaitForSecondsRealtime(.4f);
                    var platform = Object.FindObjectsOfType<Collider2D>().Single(c => c.name == "fung_temple_plat_float (5)" &&
                        c.gameObject.scene.name == scene && c.gameObject.layer == 8 && !c.isTrigger);
                    var body = hero.GetComponent<Rigidbody2D>();
                    var box = hero.GetComponent<Collider2D>().bounds;
                    var height = box.extents.y - (box.center - hero.transform.position).y;
                    var stand = new Vector3(platform.bounds.center.x, platform.bounds.max.y + height + .08f, 0);
                    hero.transform.position = stand; body.velocity = Vector2.zero; hero.AffectedByGravity(true); Physics2D.SyncTransforms();
                    yield return Until(() => hero.cState.onGround, "native Uumuu platform supports hero");
                    yield return new WaitForSecondsRealtime(.7f);
                    yield return StoryTogether("uumuu.platform");
                    var boss = EntityReferences.Entities.Values.Single(e => e.Type == EntityType.Uumuu);
                    yield return Until(() => boss.Object.Host.LocateMyFSM("Mega Jellyfish").ActiveStateName != "Sleep" || !host,
                        "native Uumuu encounter wakes", 25);
                    foreach (var hazard in Object.FindObjectsOfType<DamageHero>(true).Where(d => StoryFixtureScene(d.gameObject, scene) &&
                        d.name.IndexOf("zap", StringComparison.OrdinalIgnoreCase) >= 0 && EntityReferences.Owner(d.gameObject) == null))
                        Log("UUMUU FIXED HAZARD " + hazard.name + " position=" + hazard.transform.position + " hazard=" + hazard.hazardType +
                            " colliders=" + string.Join(";", hazard.GetComponents<Collider2D>().Select(c => c.GetType().Name + ":" + c.enabled + ":" + c.isTrigger)));
                    var findLanding = typeof(CoopRuntime).GetMethod("FindRescuePosition", BindingFlags.Instance | BindingFlags.NonPublic);
                    var lastAttackState = "";
                    foreach (var victimRole in selected == "uumuu-fixed" ? new[] { "guest" } : new[] { "guest", "host" }) for (var attempt = 0; attempt < (selected == "uumuu-repeat" ? 5 : selected == "uumuu-fixed" ? 1 : 2); attempt++) {
                        var step = victimRole + "." + attempt; var victim = role == victimRole;
                        hero.transform.position = stand + Vector3.right * (victim ? .5f : -.9f);
                        body.velocity = Vector2.zero; hero.AffectedByGravity(true); Physics2D.SyncTransforms();
                        yield return Until(() => hero.cState.onGround && hero.acceptingInput, "Uumuu ready " + step);
                        // This fixture tests repeated death/return state on a
                        // platform. Keep native nail recoil from pushing its
                        // waiting rescuer off the edge before the real swing.
                        var constraints = body.constraints;
                        if (selected == "uumuu-repeat") body.constraints |= RigidbodyConstraints2D.FreezePositionX;
                        yield return new WaitForSecondsRealtime(.7f);
                        yield return StoryTogether(step + ".ready");
                        var serial = runtime.DeathSerial; var quota = runtime.RescuesUsed;
                        if (victim) {
                            // The native aimed lightning must really cover the
                            // standing body; no fabricated hazard or FSM state.
                            if (selected != "uumuu-repeat" || attempt == 0 && victimRole == "guest")
                                yield return Until(() => {
                                    if (selected == "uumuu-fixed") {
                                        var attack = boss.Object.Host.LocateMyFSM("Mega Jellyfish").ActiveStateName;
                                        if (attack != lastAttackState) {
                                            lastAttackState = attack;
                                            Log("UUMUU NATIVE ATTACK " + attack + " active fixed=" + Object.FindObjectsOfType<DamageHero>().Count(d =>
                                                d.name.Contains("Multizap") && d.enabled && d.GetComponents<Collider2D>().Any(c => c.enabled)));
                                        }
                                    }
                                    return Physics2D.OverlapBoxAll(hero.GetComponent<Collider2D>().bounds.center,
                                hero.GetComponent<Collider2D>().bounds.size, 0).Any(c =>
                                    c.name.IndexOf("zap", StringComparison.OrdinalIgnoreCase) >= 0 &&
                                    (selected != "uumuu-fixed" || EntityReferences.Owner(c.gameObject) == null) &&
                                    c.GetComponentInParent<DamageHero>() is { enabled: true, hazardType: 1 });
                                }, "native Uumuu lightning covers platform " + step, selected == "uumuu-fixed" ? 90 : 40);
                            foreach (var c in Physics2D.OverlapBoxAll(hero.GetComponent<Collider2D>().bounds.center,
                                hero.GetComponent<Collider2D>().bounds.size, 0).Where(c => c.GetComponentInParent<DamageHero>() != null))
                                Log("UUMUU HAZARD " + c.name + " owner=" + EntityReferences.Owner(c.gameObject)?.Type + " hp=" + c.GetComponentInParent<HealthManager>()?.name);
                            Log("UUMUU LETHAL landing=" + findLanding.Invoke(runtime, new object[] { hero }) + " hero=" + hero.transform.position);
                            PlayerData.instance.SetInt("health", 1); PlayerData.instance.SetInt("healthBlue", 0);
                            hero.takeNoDamage = false; hero.cState.invulnerable = false;
                            hero.TakeDamage(null, GlobalEnums.CollisionSide.left, 1, 1);
                            yield return Until(() => runtime.Down || runtime.Returning, "Uumuu lethal intercepted " + step, 5);
                            yield return new WaitForSecondsRealtime(2);
                            Check(runtime.Down && !runtime.Returning && GameManager.instance.sceneName == scene,
                                "Uumuu lightning cannot force an unused rescue back to bench " + step);
                            Check(runtime.RescuesUsed == quota, "waiting for rescue does not charge quota " + step);
                            Flag(step + ".down");
                            yield return Until(() => !runtime.Down && !runtime.Returning && PlayerData.instance.health == 1 && hero.acceptingInput,
                                "Uumuu real nail rescue " + step, 15);
                            Check(runtime.DeathSerial == serial + 1 && runtime.RescuesUsed == quota + 1, "Uumuu one death one rescue charge " + step);
                            Check(!hero.cState.dead && !hero.cState.hazardDeath && !hero.cState.hazardRespawning && body.simulated && !HeroBox.inactive,
                                "native living state restored " + step);
                            Log("RESCUE RESTORED " + step + " position=" + hero.transform.position + " gravity=" + body.gravityScale +
                                " used=" + runtime.RescuesUsed + " anchorFailures=" + Field<int>(runtime, "rescueAnchorFailures"));
                            hero.takeNoDamage = true;
                        } else {
                            yield return Until(() => Has(step + ".down"), "Uumuu teammate awaits real rescue " + step, 45);
                            var peer = runtime.Peers.Single(); var service = Field<ShadeService>(runtime, "shades");
                            yield return Until(() => service.Get(peer.Key) != null, "Uumuu shade visible " + step);
                            hero.FaceRight(); hero.Attack(GlobalEnums.AttackDirection.normal);
                            yield return Until(() => peer.Value.Life == HollowConnect.Core.Life.Alive && peer.Value.State?.Scene == scene, "Uumuu nail accepted " + step, 8);
                        }
                        yield return StoryTogether(step + ".rescued");
                        body.constraints = constraints;
                    }
                    if (selected == "uumuu-repeat") Check(Has("uumuu.fixed-zap-covered-shade"), "native fixed lightning covered a waiting shade during repeated rescues");
                    continue;
                }
                foreach (var c in Object.FindObjectsOfType<Collider2D>().Where(c => StoryFixtureScene(c.gameObject, scene) && c.enabled && !c.isTrigger && c.gameObject.layer == 8))
                    Log("NATIVE SUPPORT " + c.name + " bounds=" + c.bounds + " damage=" + c.GetComponentInParent<DamageHero>()?.hazardType);
                var find = typeof(CoopRuntime).GetMethod("FindRescuePosition", BindingFlags.Instance | BindingFlags.NonPublic);
                Log("NATIVE LANDING " + find.Invoke(runtime, new object[] { hero }));
            }
            yield return StoryTogether(scene + ".checked");
        }
    }
}
