using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Hkmp;
using Hkmp.Game.Client.Entity;
using HollowConnect;
using HollowConnect.Core;
using MonoMod.RuntimeDetour;
using UnityEngine;

namespace HollowConnect.Smoke;

public sealed partial class RescueProbe {
    private bool MossRetargetCheck => !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("HOLLOWCONNECT_MOSS_RETARGET"));

    private IEnumerator MossRetargetChecks() {
        using var noContact = IgnoreExternalCombatDamage();
        var authority = role == (Environment.GetEnvironmentVariable("HOLLOWCONNECT_MOSS_AUTHORITY") ?? "host");
        var other = host ? "guest" : "host";
        var manager = Field<EntityManager>(engine.ClientManager, "_entityManager");
        var entities = Field<Dictionary<ulong, Entity>>(manager, "_entities");
        using var bindingTrace = new Hook(typeof(EntityInitializer).GetMethod("InitializeFsm"),
            new Action<Action<PlayMakerFSM>, PlayMakerFSM>((orig, fsm) => {
                var relevant = fsm != null && fsm.FsmName == "Mossy Control" && fsm.name == "Mega Moss Charger";
                void Sample(string when) {
                    Log("MOSS BIND " + when + " active=" + fsm.gameObject.activeInHierarchy + " id=" + fsm.gameObject.GetInstanceID() +
                        " children=" + string.Join(",", fsm.GetComponentsInChildren<AlertRange>(true).Select(r => r.name + ":" + r.GetInstanceID())) +
                        " objects=" + string.Join(",", fsm.FsmVariables.GameObjectVariables.Where(v => v.Name.Contains("Range")).Select(v => v.Name + ":" + v.Value + ":" + v.Value?.GetInstanceID())) +
                        " range=" + fsm.FsmStates.SelectMany(s => s.Actions).OfType<CheckAlertRange>().SingleOrDefault()?.alertRange?.Value);
                }
                if (relevant) Sample("before");
                orig(fsm);
                if (relevant) Sample("after");
            }));
        var mode = Environment.GetEnvironmentVariable("HOLLOWCONNECT_MOSS_RETARGET");
        var remoteVictim = Environment.GetEnvironmentVariable("HOLLOWCONNECT_MOSS_VICTIM") == "remote";
        var victim = remoteVictim ? !authority : authority;
        var sequence = Resources.LoadAll<BossSequence>("GG").Single(s => s.name == "Boss Sequence Tier 1");
        var scene = mode == "schema" ? sequence.GetSceneAt(3) : "Fungus1_29";
        if (!authority) yield return Until(() => Has("moss.first-entered"), "Moss authority enters first");
        BossSequenceController.Reset(); PlayerData.instance.SetInt("bossStatueTargetLevel", 0);
        yield return RuleEnter(scene, mode == "schema" ? "door_dreamEnter" : "left1");
        yield return Until(() => manager.IsSceneHostDetermined && manager.IsSceneHost == authority &&
            entities.Values.Any(e => e.Type == EntityType.MassiveMossCharger), "native giant Moss room and authority");
        var hero = HeroController.instance;
        hero.takeNoDamage = true; hero.AffectedByGravity(true);
        yield return Until(() => hero.cState.onGround && hero.acceptingInput && !hero.cState.transitioning,
            "native Moss entry finishes on floor");
        hero.AffectedByGravity(false); hero.GetComponent<Rigidbody2D>().velocity = Vector2.zero;
        if (mode != "schema") {
            var floor = StoryFloor(entities.Values.Single(e => e.Type == EntityType.MassiveMossCharger).Object.Host.transform.position, scene);
            hero.transform.position = floor + Vector3.right * (remoteVictim ? authority ? -14 : 0 : authority ? -2 : 5);
            Physics2D.SyncTransforms();
        }
        if (authority) Flag("moss.first-entered");
        Flag(role + ".moss-ready");
        yield return Until(() => Has(other + ".moss-ready") && runtime.Peers.Values.Single().Life == HollowConnect.Core.Life.Alive,
            "both native Moss entrants ready");
        var boss = entities.Values.Single(e => e.Type == EntityType.MassiveMossCharger);
        var control = boss.Object.Host.GetComponents<PlayMakerFSM>().Single(f => f.FsmName == "Mossy Control");
        foreach (var fsm in boss.Object.Host.GetComponentsInChildren<PlayMakerFSM>(true)) NoskDump(fsm);
        Log("MOSS PREFLIGHT scene=" + scene + " authority=" + authority + " hero=" + hero.transform.position +
            " boss=" + boss.Object.Host.transform.position + " scope=" + boss.BossTargetScope +
            " control=" + boss.BossTargetControl?.Name + " receipt=" + boss.BossTargetReceipt?.Value);
        if (mode != "schema") {
            var rangeAction = control.FsmStates.SelectMany(s => s.Actions).OfType<CheckAlertRange>().Single();
            AlertRange Range() => rangeAction.alertRange.Value as AlertRange;
            if (authority) Check(Range() != null, "native Moss attack range resolved");
            foreach (var box in Range()?.GetComponents<Collider2D>() ?? Array.Empty<Collider2D>())
                Log("MOSS RANGE " + box.GetType().Name + " enabled=" + box.enabled + " bounds=" + box.bounds);
            var states = new HashSet<string>();
            var stage = "before-death"; var next = 0f;
            using var trace = new Hook(typeof(CheckAlertRange).GetMethod("Apply", BindingFlags.Instance | BindingFlags.NonPublic),
                new Action<Action<CheckAlertRange>, CheckAlertRange>((orig, action) => {
                    orig(action);
                    if (!boss.IsNativeAuthority || EntityReferences.Owner(action.Fsm.GameObject) != boss || Time.realtimeSinceStartup < next) return;
                    next = Time.realtimeSinceStartup + .5f;
                    Log("MOSS RANGE READ stage=" + stage + " state=" + control.ActiveStateName + " native=" + Range()?.IsHeroInRange +
                        " result=" + action.storeResult.Value + " hero=" + hero.transform.position + " peer=" + runtime.Peers.Values.Single().Life +
                        "/" + runtime.Peers.Values.Single().State.Scene + " receipt=" + boss.BossTargetReceipt?.Value);
                }));
            if (authority) {
                yield return Until(() => control.ActiveStateName == "Charge", "natural story Moss charge before death", 60);
                Log("MOSS TARGET BEFORE DEATH local=" + runtime.LocalId + " victim=" + (victim ? runtime.LocalId : runtime.Peers.Single().Key) + " selected=" +
                    BossTargetCommitment.Decode(boss.BossTargetReceipt.Value, runtime.Session, CoopContext.Epoch).Player +
                    " receipt=" + boss.BossTargetReceipt.Value);
                Flag("moss.charged");
            }
            yield return Until(() => Has("moss.charged"), "authority observes natural Moss attack", 90);
            if (victim) {
                hero.AffectedByGravity(true);
                Die();
                Flag("moss.death-started");
                yield return Until(() => GameManager.instance.sceneName == "Town" && runtime.Playable && !GameManager.instance.IsInSceneTransition,
                    "native Moss target death returns to own Town bench", 70);
                Flag("moss.returned");
                yield return Until(() => Has("moss.survivor-observed"), "remaining player observes post-death Moss", 90);
            } else {
                yield return Until(() => Has("moss.death-started"), "native target begins death", 90);
                yield return Until(() => Has("moss.returned") && manager.IsSceneHost && boss.IsNativeAuthority,
                    "survivor takes native Moss authority after target returns", 90);
                if (mode != "baseline") {
                    Check(Range() != null, "takeover retains native detached attack range");
                    Log("MOSS TAKEOVER RANGE active=" + Range().gameObject.activeInHierarchy + " native=" + Range().IsHeroInRange);
                    foreach (var collider in Range().GetComponents<Collider2D>())
                        Log("MOSS TAKEOVER COLLIDER enabled=" + collider.enabled + " bounds=" + collider.bounds);
                }
                stage = "after-return";
                var stop = Time.realtimeSinceStartup + 16;
                var last = "";
                while (Time.realtimeSinceStartup < stop) {
                    states.Add(control.ActiveStateName);
                    if (last != control.ActiveStateName) {
                        last = control.ActiveStateName;
                        Log("MOSS POST state=" + last + " hero=" + hero.transform.position + " boss=" + boss.Object.Host.transform.position +
                            " native=" + Range()?.IsHeroInRange + " receipt=" + boss.BossTargetReceipt?.Value);
                    }
                    yield return null;
                }
                Log("MOSS OBSERVATION states=" + string.Join(",", states) + " native=" + Range()?.IsHeroInRange + " receipt=" + boss.BossTargetReceipt?.Value);
                if (mode != "baseline") {
                    Check(states.Contains("Charge") || states.Contains("Leap"), "surviving player gets natural post-return attack");
                    var inside = hero.transform.position;
                    // Native opening roar can restore gravity for the first
                    // entrant. Freeze only this geometry check after combat.
                    hero.AffectedByGravity(false);
                    hero.transform.position = inside + Vector3.up * 10;
                    hero.GetComponent<Rigidbody2D>().velocity = Vector2.zero; Physics2D.SyncTransforms();
                    yield return Until(() => !Range().IsHeroInRange && control.ActiveStateName == "Hidden",
                        "survivor leaving actual range lets Moss hide normally", 20);
                    yield return new WaitForSecondsRealtime(1.2f);
                    Check(control.ActiveStateName == "Hidden", "Moss stays hidden while survivor is out of range");
                    hero.transform.position = inside; Physics2D.SyncTransforms();
                    yield return Until(() => Range().IsHeroInRange && control.ActiveStateName == "Charge",
                        "native collider reentry wakes Moss again", 20);
                    Check(control.FsmVariables.FindFsmGameObject("Hero").Value == hero.gameObject,
                        "Moss FSM retains its native Hero binding after target departure");
                }
                Flag("moss.survivor-observed");
            }
            yield break;
        }
        var end = Time.realtimeSinceStartup + 6;
        while (Time.realtimeSinceStartup < end) {
            Log("MOSS SAMPLE state=" + control.ActiveStateName + " boss=" + boss.Object.Host.transform.position +
                " hero=" + hero.transform.position + " bools=" + string.Join(",", control.FsmVariables.BoolVariables.Select(v => v.Name + "=" + v.Value)) +
                " floats=" + string.Join(",", control.FsmVariables.FloatVariables.Select(v => v.Name + "=" + v.Value)));
            yield return new WaitForSecondsRealtime(.5f);
        }
    }
}
