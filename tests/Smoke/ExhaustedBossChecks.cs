using System;
using System.Collections;
using System.Linq;
using Hkmp;
using Hkmp.Game.Client.Entity;
using HollowConnect;
using HollowConnect.Core;
using UnityEngine;
using Object = UnityEngine.Object;

namespace HollowConnect.Smoke;

public sealed partial class RescueProbe {
    private IEnumerator ExhaustedTogether(string step) {
        Flag(role + ".exhausted." + step);
        yield return Until(() => Has((host ? "guest" : "host") + ".exhausted." + step), "both exhausted " + step, 80);
    }

    private IEnumerator ExhaustedEntry(string cycle, bool authority) {
        const string scene = "Fungus3_23";
        // The native story loader checks Shade Cloak. No forced scene load,
        // wave transition or boss activation substitutes for its progression.
        PlayerData.instance.SetBool("hasShadowDash", true);
        if (!authority) yield return Until(() => Has(cycle + ".first"), "first native story entrant");
        yield return RuleEnter(scene);
        var manager = Field<EntityManager>(engine.ClientManager, "_entityManager");
        yield return Until(() => manager.IsSceneHostDetermined && manager.IsSceneHost == authority, "expected story authority");
        if (authority) Flag(cycle + ".first");
        yield return ExhaustedTogether(cycle + ".entered");
        Check(UnityEngine.SceneManagement.SceneManager.GetSceneByName(scene + "_boss").isLoaded, "native additive Traitor Lord scene loaded");
        var hero = HeroController.instance;
        hero.transform.position = StoryFloor(new Vector3(52, 29, 0), scene);
        hero.GetComponent<Rigidbody2D>().velocity = Vector2.zero;
        hero.AffectedByGravity(true); Physics2D.SyncTransforms();
        Entity boss = null;
        var end = Time.realtimeSinceStartup + 40;
        while (Time.realtimeSinceStartup < end) {
            // With personal world progression these prewave enemies are local.
            // Use their normal health/death callbacks on each native wave.
            foreach (var hp in Object.FindObjectsOfType<HealthManager>().Where(h => h.gameObject.scene.name == scene + "_boss" &&
                h.name.StartsWith("Mantis Heavy", StringComparison.Ordinal) && !h.isDead && h.hp > 0)) {
                var owner = EntityReferences.Owner(hp.gameObject);
                if (owner == null || owner.IsNativeAuthority) hp.ApplyExtraDamage(hp.hp);
            }
            boss = EntityReferences.Entities.Values.FirstOrDefault(e => e.Type == EntityType.TraitorLord &&
                e.SourceScene == scene + "_boss" && e.Object.Host != null &&
                (e.Object.Host.activeInHierarchy || e.Object.Client != null && e.Object.Client.activeInHierarchy));
            if (boss != null) break;
            yield return null;
        }
        if (boss == null) foreach (var fsm in Object.FindObjectsOfType<PlayMakerFSM>(true).Where(f => StoryFixtureScene(f.gameObject, scene) && f.FsmName == "Battle Control"))
            Log("EXHAUSTED PREFLIGHT battle=" + fsm.name + " state=" + fsm.ActiveStateName);
        Check(boss != null, "native waves activate registered Traitor Lord");
        Log("EXHAUSTED PREFLIGHT source=" + boss.SourceScene + " bossEncounter=" + runtime.InBossEncounter());
        Check(runtime.InBossEncounter(), "live additive boss belongs to rescue room");
        yield return Until(() => hero.cState.onGround && hero.acceptingInput, "native arena ground and controls");
        hero.SetHazardRespawn(hero.transform.position, true);
        yield return ExhaustedTogether(cycle + ".active");
    }

    private IEnumerator ExhaustedNailRescue(string step, bool victim, int used) {
        if (victim) {
            Die(); yield return Until(() => runtime.Down && !runtime.Spectating, step + " native shade death");
            Flag(step + ".down");
            yield return Until(() => !runtime.Down && !runtime.Returning && HeroController.instance.acceptingInput &&
                PlayerData.instance.health == 1 && runtime.RescuesUsed == used, step + " revival quota=" + used);
        } else {
            yield return Until(() => Has(step + ".down") && runtime.Peers.Values.Single().Life == Life.Down, step + " peer down");
            var peer = runtime.Peers.Single(); var shades = Field<ShadeService>(runtime, "shades");
            yield return Until(() => shades.Get(peer.Key) != null, step + " native shade ready");
            var hero = HeroController.instance;
            hero.transform.position = shades.Get(peer.Key).transform.position + Vector3.left * 2;
            hero.GetComponent<Rigidbody2D>().velocity = Vector2.zero;
            hero.FaceRight(); hero.Attack(GlobalEnums.AttackDirection.normal); Physics2D.SyncTransforms();
            yield return Until(() => runtime.Peers[peer.Key].Life == Life.Alive, step + " actual nail collision");
            Check(runtime.RescuesUsed == 0, "rescuing teammate does not spend rescuer allowance");
        }
        yield return ExhaustedTogether(step);
        yield return new WaitForSecondsRealtime(.6f);
    }

    private IEnumerator ExhaustedRest() {
        yield return RuleBenchUp();
        var bench = Object.FindObjectsOfType<PlayMakerFSM>().First(f => f.name == "RestBench" && f.FsmName == "Bench Control");
        var hero = HeroController.instance;
        hero.transform.position = bench.transform.position + new Vector3(-1, 1.5f, 0);
        hero.GetComponent<Rigidbody2D>().velocity = Vector2.zero; hero.AffectedByGravity(true);
        yield return Until(() => bench.ActiveStateName == "In Range" && hero.cState.onGround, "native bench in range");
        yield return RulePress(InputHandler.Instance.inputActions.up);
        yield return Until(() => bench.ActiveStateName == "Resting" && PlayerData.instance.GetBool("atBench") && runtime.RescuesUsed == 0,
            "real bench rest refills all three rescues");
        yield return RuleBenchUp();
    }

    private IEnumerator ExhaustedBossChecks() {
        var authority = role == (Environment.GetEnvironmentVariable("HOLLOWCONNECT_EXHAUSTED_BOSS_AUTHORITY") ?? "host");
        var victim = !authority;
        using var noContact = IgnoreExternalCombatDamage();
        for (var cycle = 1; cycle <= 2; cycle++) {
            var prefix = "exhausted.cycle" + cycle;
            yield return ExhaustedEntry(prefix, authority);
            Check(runtime.RescueLimit == 3 && runtime.SpectateAfterExhaustion && runtime.RescuesUsed == 0, "negotiated three rescues and spectating enabled");
            for (var used = 1; used <= 3; used++) yield return ExhaustedNailRescue(prefix + ".rescue" + used, victim, used);
            var epoch = CoopContext.Epoch;
            if (victim) {
                Die(); yield return Until(() => runtime.Down && runtime.Spectating, "fourth death spectates without personal return");
                Check(runtime.RescuesUsed == 3 && !runtime.Returning && Object.FindObjectsOfType<ShadeView>().Length == 0,
                    "exhaustion keeps quota and removes rescue shade");
            } else yield return Until(() => runtime.Peers.Values.Single().Life == Life.Spectating, "server confirms exhausted spectator");
            yield return ExhaustedTogether(prefix + ".spectating");
            if (cycle == 1) {
                // Screenshot route: one player leaves the failed encounter;
                // their Alive status at the bench must not strand the last shade.
                if (victim) {
                    RuleReturn(); yield return RuleArrived("exhausted spectator chooses return");
                    Check(runtime.RescuesUsed == 3, "death arrival does not refill allowance");
                    Flag(prefix + ".returned");
                    yield return Until(() => Has(prefix + ".last-returned"), "last fighter returned despite teammate alive at bench", 80);
                } else {
                    yield return Until(() => Has(prefix + ".returned"), "teammate returned before last death", 80);
                    Die(); yield return RuleArrived("last additive boss fighter");
                    Check(CoopContext.Epoch == epoch, "room failure leaves unrelated bench visit and epoch intact");
                    Flag(prefix + ".last-returned");
                }
            } else {
                if (!victim) Die();
                yield return RuleArrived("spectator and last fighter both defeated");
            }
            yield return ExhaustedTogether(prefix + ".arrived");
            Check(!UnityEngine.SceneManagement.SceneManager.GetSceneByName("Fungus3_23_boss").isLoaded, "last exit unloads native boss scene");
            Check(runtime.RescuesUsed == (victim ? 3 : 0), "failure preserves independent personal quotas");
            if (victim) yield return ExhaustedRest();
            else yield return RuleBenchUp();
            yield return ExhaustedTogether(prefix + ".rested");
        }
    }
}
