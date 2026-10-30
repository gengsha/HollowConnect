using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Hkmp;
using Hkmp.Game.Client.Entity;
using HollowConnect;
using HollowConnect.Core;
using HutongGames.PlayMaker.Actions;
using MonoMod.RuntimeDetour;
using UnityEngine;

namespace HollowConnect.Smoke;

public sealed partial class RescueProbe {
    private IEnumerator BossTargetChecks() {
        using var observationContact = Environment.GetEnvironmentVariable("HOLLOWCONNECT_OBSERVATION_BOSS") == "1" ? IgnoreExternalCombatDamage() : null;
        const string scene = "GG_Hornet_1";
        var authority = role == Environment.GetEnvironmentVariable("HOLLOWCONNECT_BOSS_TARGET_AUTHORITY");
        var other = host ? "guest" : "host";
        var manager = Field<EntityManager>(engine.ClientManager, "_entityManager");
        var entities = Field<Dictionary<ulong, Entity>>(manager, "_entities");
        if (!authority) yield return Until(() => Has("target.first"), "target first entrant");
        BossSequenceController.Reset(); PlayerData.instance.SetInt("bossStatueTargetLevel", 0);
        if (Environment.GetEnvironmentVariable("HOLLOWCONNECT_OBSERVATION_BOSS") == "1") {
            BossSceneController.SetupEvent = c => c.BossLevel = 0;
            PlayerData.instance.SetString("dreamReturnScene", "Town");
            PlayerData.instance.SetString("bossReturnEntryGate", "left1");
        }
        HeroController.instance.takeNoDamage = true;
        GameManager.instance.BeginSceneTransition(new GameManager.SceneLoadInfo { SceneName = scene, EntryGateName = "door_dreamEnter",
            Visualization = GameManager.SceneLoadVisualizations.Default, WaitForSceneTransitionCameraFade = true });
        yield return Until(() => runtime.Playable && CoopContext.LocalScene == scene && !GameManager.instance.IsInSceneTransition &&
            engine.ClientManager.SceneSnapshotReady && manager.IsSceneHostDetermined && manager.IsSceneHost == authority &&
            entities.Values.Any(e => e.Type == EntityType.Hornet), "native Hornet room authority " + authority);
        var hero = HeroController.instance; var body = hero.GetComponent<Rigidbody2D>();
        hero.takeNoDamage = true;
        yield return Until(() => hero.acceptingInput && hero.cState.onGround && GameManager.instance.HasFinishedEnteringScene, "native Hornet entrance finished");
        hero.AffectedByGravity(false); body.velocity = Vector2.zero;
        hero.SetHazardRespawn(hero.transform.position, true);
        var floor = hero.transform.position.y;
        hero.transform.position = new Vector3(authority ? 27 : 38, floor, 0);
        if (authority) Flag("target.first");
        Flag(role + ".target-ready"); yield return Until(() => Has(other + ".target-ready") && runtime.Peers.Values.Single().Life == Life.Alive, "two living native entrants");
        var boss = entities.Values.Single(e => e.Type == EntityType.Hornet);
        if (Environment.GetEnvironmentVariable("HOLLOWCONNECT_OBSERVATION_BOSS") == "1") {
            var controller = BossSceneController.Instance;
            var health = boss.Object.Host.GetComponent<HealthManager>();
            Check(controller != null && controller.gameObject.scene.name == scene && controller.bosses.Contains(health) &&
                Field<int>(controller, "bossesLeft") > 0, "native Hornet Setup owns real boss death subscription");
        }
        var stage = 0; var reads = 0; var matching = 0; var wrong = 0;
        Vector3 Expected() => runtime.Down || runtime.Observing ? runtime.PlayerObject(runtime.Peers.Single().Key).transform.position : hero.transform.position;
        using var trace = new Hook(typeof(GetAngleToTarget2D).GetMethod("DoGetAngle", BindingFlags.Instance | BindingFlags.NonPublic),
            new Action<Action<GetAngleToTarget2D>, GetAngleToTarget2D>((orig, action) => {
                var relevant = authority && stage != 0 && action.target.Value == hero.gameObject && EntityReferences.Owner(action.Fsm.GameObject) == boss;
                orig(action);
                if (!relevant) return;
                var self = action.Fsm.GetOwnerDefaultTarget(action.gameObject).transform.position;
                var target = Expected();
                var expected = Mathf.Atan2(target.y + action.offsetY.Value - self.y, target.x + action.offsetX.Value - self.x) * Mathf.Rad2Deg;
                reads++;
                if (Math.Abs(Mathf.DeltaAngle(action.storeAngle.Value, expected)) < .1f) matching++; else wrong++;
                if (reads <= 5) Log("TARGET ANGLE stage=" + stage + " state=" + action.State.Name + " actual=" + action.storeAngle.Value +
                    " expected=" + expected + " hero=" + hero.transform.position + " live=" + target + " peerLife=" + runtime.Peers.Values.Single().Life);
            }));
        var faceReads = 0; var faceWrong = 0;
        using var facing = new Hook(typeof(FaceObject).GetMethod("DoFace", BindingFlags.Instance | BindingFlags.NonPublic),
            new Action<Action<FaceObject>, FaceObject>((orig, action) => {
                var relevant = authority && stage != 0 && action.objectB.Value == hero.gameObject && EntityReferences.Owner(action.Fsm.GameObject) == boss;
                orig(action);
                if (!relevant) return;
                var delta = Expected().x - action.objectA.Value.transform.position.x;
                if (Math.Abs(delta) < .1f) return;
                var expected = Math.Sign(delta) * (action.spriteFacesRight.Value ? 1 : -1);
                faceReads++;
                if (Math.Sign(action.objectA.Value.transform.localScale.x) != expected) faceWrong++;
            }));
        stage = 1;
        if (authority) {
            yield return Until(() => faceReads >= 3, "native Hornet reads target while alive", 60);
            Check(wrong == 0 && faceWrong == 0, "living authority keeps native hero target");
        }
        Flag(role + ".target-alive"); yield return Until(() => Has(other + ".target-alive"), "alive target samples complete", 90);
        if (Environment.GetEnvironmentVariable("HOLLOWCONNECT_OBSERVATION_BOSS") == "1") {
            if (authority) {
                hero.AffectedByGravity(true); CoopMod.Settings.VoluntaryObservation = true;
                yield return ObservationBegin("native Hornet authority observation");
                yield return Until(() => Field<bool>(runtime, "observationAccepted"), "server accepts boss authority observation");
            }
            yield return Until(() => authority ? runtime.Observing : runtime.Peers.Values.Single().Life == Life.Observing, "both see actual observing authority");
            reads = matching = wrong = faceReads = faceWrong = 0; stage = 2;
            if (authority) {
                yield return Until(() => reads >= 1 && faceReads >= 3, "native Hornet reads living teammate while authority observes", 120);
                Check(wrong == 0 && faceWrong == 0 && !body.simulated && !runtime.Down,
                    "real boss redirects to fighter without observer physics or fake death");
                var hm = boss.Object.Host.GetComponent<HealthManager>();
                var attackSource = runtime.PlayerObject(runtime.Peers.Single().Key);
                Log("OBSERVATION ATTACK source=" + attackSource.name + " blocked=" + CoopContext.NonCombatAttack?.Invoke(attackSource) +
                    " remote=" + (attackSource.GetComponentInParent<Hkmp.Animation.RemoteVisualOpacity>() != null) +
                    " hp=" + hm.hp + " invincible=" + hm.IsInvincible + " damageOverride=" + hm.damageOverride);
                Check(CoopContext.NonCombatAttack?.Invoke(attackSource) != true, "living fighter attack ownership remains eligible");
                // Observe native vulnerability/evasion; IgnoreInvulnerable on
                // HitInstance does not bypass HealthManager.Hit's block test.
                yield return Until(() => !hm.IsInvincible && Field<float>(hm, "evasionByHitRemaining") <= 0,
                    "native Hornet vulnerability window for fighter hit", 60);
                var damageDeadline = Time.realtimeSinceStartup + 90;
                while (hm != null && !hm.isDead && hm.hp > 0 && Time.realtimeSinceStartup < damageDeadline) {
                    if (!hm.IsInvincible && Field<float>(hm, "evasionByHitRemaining") <= 0) {
                        var before = hm.hp;
                        hm.Hit(new HitInstance { Source = attackSource, DamageDealt = 9999,
                            AttackType = AttackTypes.Nail, Multiplier = 1, IgnoreInvulnerable = true });
                        Log("OBSERVATION NATIVE HIT hp=" + before + "->" + hm.hp + " dead=" + hm.isDead + " override=" + hm.damageOverride);
                        Check(hm.isDead || hm.hp < before, "eligible fighter hit debits native boss health while authority observes");
                    }
                    yield return new WaitForSecondsRealtime(.25f);
                }
                Check(hm == null || hm.isDead || hm.hp <= 0, "fighter-owned native lethal hit defeats boss despite observing authority");
                yield return Until(() => Field<uint>(runtime, "lastBossVictory") != 0 && Field<double>(runtime, "settlementUntil") == 0,
                    "observing authority receives actual reliable boss settlement without blocking fighter", 20);
                if (runtime.Observing && !runtime.ObservationReturning) yield return ObservationKey(CoopMod.Settings.ObserveKey);
                yield return ObservationBenchArrival(); Flag("observation.boss-finished");
            } else yield return Until(() => Has("observation.boss-finished"), "observing authority completes native boss settlement and bench return", 180);
            stage = 0; yield break;
        }
        if (authority) { Die(); yield return Until(() => runtime.Down, "authority becomes actual rescue shadow"); }
        yield return Until(() => authority ? runtime.Down : runtime.Peers.Values.Single().Life == Life.Down, "both see downed authority");
        if (authority) BossTargetBoundaryChecks(boss, hero);
        reads = matching = wrong = faceReads = faceWrong = 0; stage = 2;
        if (authority) {
            yield return Until(() => faceReads >= 3, "native Hornet keeps reading target after death", 60);
            Check(wrong == 0 && faceWrong == 0, "native Hornet aims at living teammate instead of frozen hero");
            Flag("target.redirected");
        } else yield return Until(() => Has("target.redirected"), "authority confirms redirected target", 90);
        if (!authority) { hero.transform.position = new Vector3(24, floor, 0); body.velocity = Vector2.zero; Flag("target.moved"); }
        yield return Until(() => Has("target.moved"), "living teammate crosses arena");
        yield return new WaitForSecondsRealtime(1);
        reads = matching = wrong = faceReads = faceWrong = 0;
        if (authority) {
            yield return Until(() => reads >= 1 && faceReads >= 3, "native target samples after teammate movement", 120);
            Check(matching >= 1 && wrong == 0 && faceWrong == 0, "native Hornet follows moving teammate while hero stays at death anchor");
            Check(Vector3.Distance(hero.transform.position, runtime.DeathPosition) < .01f, "target correction never moves hidden hero or rescue anchor");
            var avatar = runtime.PlayerObject(runtime.Peers.Single().Key);
            Check(!boss.Object.Host.GetComponentsInChildren<PlayMakerFSM>(true).SelectMany(f => f.FsmVariables.GameObjectVariables).Any(v => v.Value == avatar),
                "native FSM snapshots never persist a remote avatar target");
            Flag("target.movement-checked");
        } else {
            yield return Until(() => Has("target.movement-checked"), "authority checks moving target", 150);
            var peer = runtime.Peers.Single();
            hero.transform.position = new Vector3(peer.Value.State.X - 1, peer.Value.State.Y, 0); body.velocity = Vector2.zero;
            yield return new WaitForSecondsRealtime(.6f);
            Check(runtime.RequestShadeHit(peer.Key, peer.Value.DownSerial), "living teammate sends valid rescue at shadow");
        }
        yield return Until(() => !runtime.Down && runtime.Peers.Values.Single().Life == Life.Alive, "actual network rescue restores authority");
        reads = matching = wrong = faceReads = faceWrong = 0; stage = 3;
        if (authority) {
            yield return Until(() => faceReads >= 3, "native target samples after revival", 60);
            Check(wrong == 0 && faceWrong == 0, "revived authority regains native hero target without cached remote references");
            Check(faceReads > 0 && faceWrong == 0, "native Hornet facing follows eligible target throughout battle");
            Flag("target.revival-checked");
        } else yield return Until(() => Has("target.revival-checked"), "authority checks restored target", 90);
        stage = 0;
    }

    private void BossTargetBoundaryChecks(Entity boss, HeroController hero) {
        // Reflection keeps the same reproduction probe usable with the
        // preserved pre-fix payload, which has no target resolver callback.
        var field = typeof(CoopContext).GetField("LivingBossTarget");
        if (field == null) return;
        var target = (Func<GameObject>)field.GetValue(null);
        var pair = runtime.Peers.Single(); var peer = pair.Value;
        var avatar = runtime.PlayerObject(pair.Key);
        var life = peer.Life; var scene = peer.State.Scene; var seen = peer.Seen;
        try {
            Check(target() == avatar, "AI target callback chooses actual living HKMP avatar");
            peer.Life = Life.Loading;
            Check(target() == avatar, "temporary input lock does not turn a present living teammate into a shadow target");
            foreach (var invalid in new[] { Life.Down, Life.Spectating, Life.Returning, Life.Resetting }) {
                peer.Life = invalid;
                Check(target() == null, "AI excludes teammate life " + invalid);
            }
            peer.Life = life;
            peer.State.Scene = "Town"; Check(target() == null, "AI excludes a teammate in another room");
            peer.State.Scene = scene;
            peer.Seen = CoopRuntime.Now - 3; Check(target() == null, "AI excludes stale teammate presence");
            peer.Seen = seen;
            Check(target() == avatar, "AI chooses eligible teammate again after invalid candidates");
            var type = typeof(Entity).Assembly.GetType("Hkmp.Game.Client.Entity.BossTargeting", true);
            var resolve = type.GetMethod("Resolve", BindingFlags.Static | BindingFlags.NonPublic);
            var action = boss.Object.Host.GetComponents<PlayMakerFSM>().SelectMany(f => f.FsmStates).SelectMany(s => s.Actions).OfType<FaceObject>().First();
            Check((GameObject)resolve.Invoke(null, new object[] { hero.gameObject, action }) == avatar, "authority AI reads redirect to live avatar");
            Check((GameObject)resolve.Invoke(null, new object[] { boss.Object.Host, action }) == boss.Object.Host, "AI retains non-player object reads");
            var replica = boss.Object.Client.GetComponents<PlayMakerFSM>().SelectMany(f => f.FsmStates).SelectMany(s => s.Actions).OfType<FaceObject>().First();
            Check((GameObject)resolve.Invoke(null, new object[] { hero.gameObject, replica }) == hero.gameObject, "AI target redirection excludes replica FSMs");
            var unrelated = new FaceObject();
            unrelated.Init(hero.GetComponents<PlayMakerFSM>().SelectMany(f => f.FsmStates).First());
            Check((GameObject)resolve.Invoke(null, new object[] { hero.gameObject, unrelated }) == hero.gameObject, "AI target redirection excludes player and story FSMs");
        } finally { peer.Life = life; peer.State.Scene = scene; peer.Seen = seen; }
    }
}
