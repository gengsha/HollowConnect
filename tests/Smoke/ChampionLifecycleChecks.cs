using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using Hkmp.Game.Client.Entity;
using HutongGames.PlayMaker.Actions;
using UnityEngine;

namespace HollowConnect.Smoke;

public sealed partial class ProgressProbe {
    private IEnumerator ChampionLifecycle() {
        var manager = Field<EntityManager>(engine.ClientManager, "_entityManager");
        var entities = Field<Dictionary<ulong, Entity>>(manager, "_entities");
        var bodyEntity = entities.Values.Single(e => e.Type == EntityType.FalseKnight);
        var headEntity = entities.Values.Single(e => e.Type == EntityType.FalseKnightHead);
        var initialOrbs = PlayerData.instance.GetInt("dreamOrbs");
        Mark("CHAMPION registered body=" + bodyEntity.Id + " head=" + headEntity.Id + " initialOrbs=" + initialOrbs);
        Flag(role + ".champion-parts");
        yield return Until(() => Has((host ? "guest" : "host") + ".champion-parts"), "both registered champion body and head");
        if (manager.IsSceneHost) {
            var body = bodyEntity.Object.Host.GetComponent<HealthManager>();
            var head = headEntity.Object.Host.GetComponent<HealthManager>();
            var control = body.GetComponents<PlayMakerFSM>().Single(f => f.FsmName == "FalseyControl");
            var previous = ""; var opened = false;
            var deadline = Time.realtimeSinceStartup + 110;
            while (GameManager.instance.sceneName == "Dream_01_False_Knight" && Time.realtimeSinceStartup < deadline) {
                var state = control.ActiveStateName;
                if (state != previous) { Mark("CHAMPION native state=" + state + " bodyHP=" + body.hp + " headHP=" + head.hp); previous = state; }
                if (state == "Opened" || state == "Opened 2") opened = true;
                if (state == "Set PlayerData" || state == "Death Anim Start" || state == "Steam" || state == "Ready" || state == "Dream Return" || state == "Blow") break;
                if (head.gameObject.activeInHierarchy && head.hp > 0 && !head.IsInvincible) head.ApplyExtraDamage(head.hp);
                // Let native recovery and rage finish before the next armor
                // break; forcing damage during Idle Pause skips its counter.
                else if (state == "Idle" && body.gameObject.activeInHierarchy && body.hp > 0 && !body.IsInvincible) body.ApplyExtraDamage(body.hp);
                yield return new WaitForSecondsRealtime(.2f);
            }
            if (!opened || Time.realtimeSinceStartup >= deadline) throw new Exception("Champion native armor/head phases did not reach final death");
            Mark("PASS champion native armor/head phases reach terminal animation");
        }
        yield return Until(() => GameManager.instance.sceneName == "Crossroads_10", "champion native dream return loads personal room", 100);
        var wake = HeroController.instance.GetComponents<PlayMakerFSM>().Single(f => f.FsmName == "Dream Return");
        yield return Until(() => wake.ActiveStateName == "Ready", "native dream wake waits for player input", 20);
        // Native rematch return deliberately stays prostrate until a button
        // press. Use its actual input action; do not bypass control recovery.
        wake.Fsm.Event(wake.Fsm.ActiveState.Actions.OfType<ListenForJump>().Single().wasPressed);
        yield return Until(() => GameManager.instance.sceneName == "Crossroads_10" && AliveScene("Crossroads_10"),
            "champion native wake input restores personal control", 20);
        foreach (var f in UnityEngine.Object.FindObjectsOfType<PlayMakerFSM>(true).Where(f => f.gameObject.scene.name.StartsWith("Crossroads_10", StringComparison.Ordinal) &&
            (f.name.IndexOf("Ghost", StringComparison.OrdinalIgnoreCase) >= 0 || f.FsmStates.SelectMany(s => s.Actions).Any(a =>
                a is SetPlayerDataBool b && b.boolName.Value == "falseKnightOrbsCollected" ||
                a is PlayerDataIntAdd i && i.intName.Value == "dreamOrbs")))) {
            Mark("CHAMPION reward FSM " + f.name + "/" + f.FsmName + " active=" + f.gameObject.activeInHierarchy + " state=" + f.ActiveStateName);
            Dump(f);
        }
        var rewardNpc = UnityEngine.Object.FindObjectsOfType<PlayMakerFSM>(true).Single(f => f.name == "Ghost False Knight NPC" && f.FsmName == "Conversation Control");
        if (host) {
            if (!rewardNpc.gameObject.activeInHierarchy) throw new Exception("Unclaimed champion reward ghost is absent");
            var control = rewardNpc.GetComponents<PlayMakerFSM>().Single(f => f.FsmName == "npc_control");
            HeroController.instance.transform.position = rewardNpc.transform.position + Vector3.right;
            Freeze();
            Physics2D.SyncTransforms();
            yield return Until(() => control.ActiveStateName == "In Range", "native champion spirit interaction range", 10);
            control.Fsm.Event(control.Fsm.ActiveState.Actions.OfType<ListenForUp>().Single().wasPressed);
            yield return Until(() => rewardNpc.ActiveStateName == "Defeated", "native champion spirit reward conversation", 10);
            rewardNpc.SendEvent("CONVO_FINISH");
        } else if (rewardNpc.gameObject.activeInHierarchy) throw new Exception("Claimed champion spirit became interactive again");
        var expected = initialOrbs + (host ? 300 : 0);
        yield return Until(() => PlayerData.instance.GetInt("dreamOrbs") == expected && PlayerData.instance.GetBool("falseKnightOrbsCollected"),
            "champion personal essence history expected=" + expected, 30);
        yield return Until(() => HeroController.instance.acceptingInput && !HeroController.instance.controlReqlinquished &&
            !PlayerData.instance.GetBool("disablePause"), "native champion reward conversation releases control", 10);
        Flag(role + ".champion-returned");
        yield return Until(() => Has((host ? "guest" : "host") + ".champion-returned"), "both native champion returns complete");
        yield return new WaitForSecondsRealtime(2);
        if (PlayerData.instance.GetInt("dreamOrbs") != expected || !PlayerData.instance.GetBool("falseKnightOrbsCollected") ||
            (!host && rewardNpc.gameObject.activeInHierarchy))
            throw new Exception("Champion personal reward changed after teammate finished claiming");
        Mark("PASS champion personal reward stable after both claims; orbs=" + expected);
    }
}
