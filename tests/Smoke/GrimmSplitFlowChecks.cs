using System;
using System.Collections;
using System.Linq;
using UnityEngine;
using Object = UnityEngine.Object;

namespace HollowConnect.Smoke;

public sealed partial class ProgressProbe {
    private IEnumerator GrimmSplitFlowChecks() {
        var pd = PlayerData.instance;
        var rooms = new[] { "Mines_10", "Ruins1_28", "Fungus1_10" };
        for (var index = 0; index < rooms.Length; index++) {
            var room = rooms[index];
            var collector = index < 2 ? host : !host;
            Mark("SPLIT FLAME " + (index + 1) + " collector=" + (index < 2 ? "host" : "guest") + " room=" + room);
            if (collector) {
                Enter(room);
                yield return Until(() => AliveScene(room), "collector enters native flame room " + room, 45);
                var spawner = Object.FindObjectsOfType<PlayMakerFSM>(true).Single(f =>
                    f.name == "Flamebearer Spawn" && f.FsmName == "Spawn Control");
                var hero = HeroController.instance;
                hero.takeNoDamage = true;
                hero.transform.position = spawner.transform.position + new Vector3(0, 1, 0);
                Physics2D.SyncTransforms();
                yield return Until(() => spawner.ActiveStateName == "Start Fight", "collector triggers native Grimmkin " + (index + 1), 12);
                ResourceCheck(spawner.ActiveStateName == "Start Fight", "native spawner is fighting: " + spawner.ActiveStateName);
                var enemy = spawner.FsmVariables.FindFsmGameObject("Flamebearer").Value;
                var health = enemy.GetComponent<HealthManager>();
                yield return Until(() => !health.IsInvincible && health.hp > 0, "native Grimmkin vulnerable " + (index + 1), 10);
                Mark("SPLIT KILL " + (index + 1) + " scene=" + GameManager.instance.sceneName);
                health.Hit(new HitInstance { Source = hero.gameObject, DamageDealt = 999, Multiplier = 1,
                    AttackType = AttackTypes.Nail, IgnoreInvulnerable = true });
            }
            yield return Until(() => pd.GetInt("flamesCollected") == index + 1 && pd.scenesFlameCollected.Contains(room),
                "collector and offsite peer both receive flame " + (index + 1), 60);
            ResourceCheck(pd.GetInt("flamesCollected") == index + 1 && pd.scenesFlameCollected.Contains(room),
                "distributed flame receipt " + (index + 1));
            Mark("SPLIT RECEIPTS count=" + pd.GetInt("flamesCollected") + " localScene=" + GameManager.instance.sceneName +
                " locations=" + string.Join(",", pd.scenesFlameCollected));
            yield return ResourceBarrier("split-flame-" + index);
        }
        ResourceCheck(pd.GetInt("flamesCollected") == 3 && rooms.All(pd.scenesFlameCollected.Contains),
            "both peers have all three distinct first-round flames");

        var ready = false;
        var upgraded = false;
        var finished = false;
        On.HutongGames.PlayMaker.Fsm.hook_EnterState trace = (orig, fsm, state) => {
            if (fsm.GameObject != null && fsm.GameObject.scene.name == "Grimm_Main_Tent" &&
                fsm.GameObject.name == "Grimm Scene" && fsm.Name == "Initial Scene") {
                Mark("GRIMM NATIVE -> " + state.Name);
                if (state.Name == "1st Ready") ready = true;
                if (state.Name == "Level Up To 2") upgraded = true;
                if (state.Name == "End") finished = true;
            }
            orig(fsm, state);
        };
        On.HutongGames.PlayMaker.Fsm.EnterState += trace;
        try {
            Enter("Grimm_Main_Tent");
            yield return Until(() => GameManager.instance.sceneName == "Grimm_Main_Tent" &&
                !GameManager.instance.IsInSceneTransition && HeroController.instance != null,
                "entered native Grimm tent with three shared flames", 45);
            var scene = Object.FindObjectsOfType<PlayMakerFSM>(true).Single(f =>
                f.gameObject.scene.name == "Grimm_Main_Tent" && f.name == "Grimm Scene" && f.FsmName == "Initial Scene");
            Dump(scene);
            yield return Until(() => ready, "native flame gate selects first upgrade branch", 12);
            ResourceCheck(ready, "native upgrade branch selected; state=" + scene.ActiveStateName);
            yield return ResourceBarrier("split-tent-ready");
            var hero = HeroController.instance;
            hero.AffectedByGravity(true);
            if (scene.ActiveStateName == "1st Ready") {
                var trigger = scene.GetComponent<Collider2D>();
                Mark("GRIMM NATIVE TRIGGER " + trigger.bounds + " hero=" + hero.transform.position);
                hero.transform.position = new Vector3(trigger.bounds.center.x, hero.transform.position.y, 0);
                Physics2D.SyncTransforms();
            }
            // Normal dialogue input only: do not send CONVO_FINISH or set the
            // upgrade state/level/count to make the continuation pass.
            var deadline = Time.realtimeSinceStartup + 90;
            while ((!finished || !upgraded || pd.GetInt("grimmChildLevel") != 2 || !hero.acceptingInput || pd.GetBool("disablePause")) &&
                Time.realtimeSinceStartup < deadline) {
                yield return ResourcePress(InputHandler.Instance.inputActions.jump);
                yield return new WaitForSecondsRealtime(.25f);
            }
            Mark("GRIMM UPGRADE level=" + pd.GetInt("grimmChildLevel") + " count=" + pd.GetInt("flamesCollected") +
                " required=" + pd.GetInt("flamesRequired") + " state=" + scene.ActiveStateName + " input=" + hero.acceptingInput);
            ResourceCheck(finished && upgraded && pd.GetInt("grimmChildLevel") == 2 && pd.GetInt("flamesCollected") == 0 &&
                hero.acceptingInput && !pd.GetBool("disablePause"), "native dialogue upgrades Grimmchild and resets flames with control restored");
        } finally { On.HutongGames.PlayMaker.Fsm.EnterState -= trace; }
        yield return ResourceBarrier("split-native-upgraded");

        var nextRoom = host ? "Deepnest_East_03" : "RestingGrounds_06";
        Enter(nextRoom);
        yield return Until(() => AliveScene(nextRoom), "enter next-round flame room " + nextRoom, 45);
        var nextSpawner = Object.FindObjectsOfType<PlayMakerFSM>(true).Single(f =>
            f.gameObject.scene.name == nextRoom && f.name == "Flamebearer Spawn" && f.FsmName == "Spawn Control");
        yield return Until(() => nextSpawner.FsmVariables.FindFsmGameObject("Flamebearer").Value != null,
            "next-round native Grimmkin spawns at level two", 15);
        ResourceCheck(nextSpawner.FsmVariables.FindFsmGameObject("Flamebearer").Value != null &&
            pd.GetInt("grimmChildLevel") == 2 && pd.GetInt("flamesCollected") == 0,
            "next round is unlocked without reusing first-round receipts");
        Mark("GRIMM NEXT ROUND scene=" + nextRoom + " state=" + nextSpawner.ActiveStateName);
        yield return ResourceBarrier("split-next-round-open", 60);
    }
}
