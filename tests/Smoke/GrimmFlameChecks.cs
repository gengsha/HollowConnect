using System;
using System.Collections;
using System.Linq;
using HutongGames.PlayMaker.Actions;
using UnityEngine;
using Object = UnityEngine.Object;

namespace HollowConnect.Smoke;

public sealed partial class ProgressProbe {
    private IEnumerator GrimmFlameChecks() {
        if (Environment.GetEnvironmentVariable("HOLLOWCONNECT_GRIMM_FLAME") == "split-flow") {
            yield return GrimmSplitFlowChecks(); yield break;
        }
        var round = 0;
        foreach (var room in new[] { "Mines_10", "Fungus1_10" }) {
        var leader = round == 0 ? host : !host;
        if (!leader) yield return Until(() => Has("flame.entered." + round), "authority entered first");
        Enter(room);
        yield return Until(() => AliveScene(room), "native flame room " + room, 45);
        if (leader) Flag("flame.entered." + round);
        HeroController.instance.takeNoDamage = true;
        var spawner = Object.FindObjectsOfType<PlayMakerFSM>(true).First(f => f.name == "Flamebearer Spawn" && f.FsmName == "Spawn Control");
        HeroController.instance.transform.position = spawner.transform.position + new Vector3(0, 1, 0);
        Flag(role + ".flame.ready." + round);
        yield return Until(() => Has((host ? "guest" : "host") + ".flame.ready." + round), "both present before kill");
        if (leader) {
            yield return Until(() => spawner.ActiveStateName == "Start Fight", "native Grimmkin proximity trigger", 12);
            var enemy = spawner.FsmVariables.FindFsmGameObject("Flamebearer").Value;
            var health = enemy.GetComponent<HealthManager>();
            yield return Until(() => !health.IsInvincible && health.hp > 0, "native Grimmkin vulnerable", 10);
            health.Hit(new HitInstance { Source = HeroController.instance.gameObject, DamageDealt = 999, Multiplier = 1, AttackType = AttackTypes.Nail, IgnoreInvulnerable = true });
        }
        yield return Until(() => PlayerData.instance.GetInt("flamesCollected") == round + 1 && PlayerData.instance.scenesFlameCollected.Contains(room), "each peer receives distinct flame " + (round + 1), 18);
        Mark("FLAME count=" + PlayerData.instance.GetInt("flamesCollected") + " scenes=" + string.Join(",",PlayerData.instance.scenesFlameCollected));
        if (Has(role + ".failed")) throw new Exception("Grimm flame count/list mismatch");
        Flag(role + ".flame.done." + round);
        yield return Until(() => Has((host ? "guest" : "host") + ".flame.done." + round), "both flame claims complete");
        round++;
        }
        PlayerData.instance.SetInt("flamesCollected", 1);
        yield return Until(() => PlayerData.instance.GetInt("flamesCollected") == 2, "repair saved two-location one-flame mismatch", 5);
        var receipt = Object.FindObjectsOfType<PlayMakerFSM>(true).First(f => f.name == "Flamebearer Spawn" && f.FsmName == "Spawn Control");
        var increment = receipt.FsmStates.Single(s => s.Name == "Get").Actions.OfType<IncrementPlayerDataInt>().Single();
        increment.OnEnter(); increment.OnEnter();
        ResourceCheck(PlayerData.instance.GetInt("flamesCollected") == 2, "repeated native increment does not duplicate the collected location");
        Flag(role + ".flame.final");
        yield return Until(() => Has((host ? "guest" : "host") + ".flame.final"), "both final flame assertions");
    }
}

