using System;
using System.Collections;
using System.Linq;
using Hkmp.Game.Client.Entity;
using HutongGames.PlayMaker.Actions;
using UnityEngine;
using Object = UnityEngine.Object;

namespace HollowConnect.Smoke;

public sealed partial class ProgressProbe {
    private IEnumerator LostKinLifecycle() {
        const string scene = "Dream_03_Infected_Knight";
        var pd = PlayerData.instance;
        var initialOrbs = pd.GetInt("dreamOrbs");
        var claimed = pd.GetBool("infectedKnightOrbsCollected");
        var manager = Field<EntityManager>(engine.ClientManager, "_entityManager");
        var bossEntity = EntityReferences.Entities.Values.Single(e => e.Type == EntityType.LostKin && e.SourceScene == scene);
        var boss = (manager.IsSceneHost ? bossEntity.Object.Host : bossEntity.Object.Client).GetComponent<HealthManager>();
        var nativeControl = boss.GetComponents<PlayMakerFSM>().Single(f => f.FsmName == "IK Control");
        Mark("KIN PREFLIGHT authority=" + manager.IsSceneHost + " object=" + boss.name + " hp=" + boss.hp +
            " active=" + boss.gameObject.activeInHierarchy + " invincible=" + boss.IsInvincible + " state=" + nativeControl.ActiveStateName);
        Dump(nativeControl);
        HeroController.instance.transform.position = boss.transform.position + Vector3.left * 4;
        Freeze(); Physics2D.SyncTransforms();
        yield return Until(() => boss.gameObject.activeInHierarchy && !boss.IsInvincible && boss.hp > 0,
            "native Lost Kin battle trigger releases registered boss", 20);
        Flag(role + ".kin-battle");
        yield return Until(() => Has((host ? "guest" : "host") + ".kin-battle"), "both participate before Lost Kin death");
        if (Environment.GetEnvironmentVariable("HOLLOWCONNECT_LOST_KIN_LIFECYCLE") == "key") {
            yield return PleasureHouseFeedbackChecks(boss);
            yield break;
        }
        if (Environment.GetEnvironmentVariable("HOLLOWCONNECT_LOST_KIN_LIFECYCLE") == "reentry") {
            if (!host) {
                Enter("Town");
                yield return Until(() => AliveScene("Town") && engine.ClientManager.SceneSnapshotReady, "Lost Kin participant leaves dream mid-fight");
                GameManager.instance.BeginSceneTransition(new GameManager.SceneLoadInfo {
                    SceneName = scene, EntryGateName = "door1", Visualization = GameManager.SceneLoadVisualizations.Default,
                    WaitForSceneTransitionCameraFade = true
                });
                yield return Until(() => AliveScene(scene) && engine.ClientManager.SceneSnapshotReady, "Lost Kin participant rejoins same ongoing battle", 35);
                Freeze();
                Flag("kin.reentered");
            } else yield return Until(() => Has("kin.reentered"), "Lost Kin authority waits for reentered participant", 60);
            yield return new WaitForSecondsRealtime(1);
        }
        if (manager.IsSceneHost) boss.ApplyExtraDamage(boss.hp);
        yield return Until(() => GameManager.instance.sceneName == "Abyss_19", "native Lost Kin corpse returns each player", 70);
        var wake = HeroController.instance.GetComponents<PlayMakerFSM>().Single(f => f.FsmName == "Dream Return");
        yield return Until(() => wake.ActiveStateName == "Ready", "native Lost Kin wake awaits input", 20);
        wake.Fsm.Event(wake.Fsm.ActiveState.Actions.OfType<ListenForJump>().Single().wasPressed);
        yield return Until(() => AliveScene("Abyss_19"), "native Lost Kin wake restores input", 20);
        Mark("KIN RETURN defeated=" + pd.GetBool("infectedKnightDreamDefeated") + " collected=" + pd.GetBool("infectedKnightOrbsCollected") + " orbs=" + pd.GetInt("dreamOrbs"));
        var fsms = Object.FindObjectsOfType<PlayMakerFSM>(true).Where(f => f.gameObject.scene.name.StartsWith("Abyss_19", StringComparison.Ordinal)).ToArray();
        foreach (var f in fsms.Where(f => f.name.Contains("Ghost") || f.FsmStates.SelectMany(s => s.Actions).Any(a =>
            a is SetPlayerDataBool b && b.boolName.Value == "infectedKnightOrbsCollected" ||
            a is PlayerDataBoolTest t && t.boolName.Value == "infectedKnightDreamDefeated"))) {
            Mark("KIN RETURN FSM " + f.name + "/" + f.FsmName + " active=" + f.gameObject.activeInHierarchy + " state=" + f.ActiveStateName);
            Dump(f);
        }
        if (!pd.GetBool("infectedKnightDreamDefeated")) throw new Exception("Lost Kin participant returned without native defeat record");
        var remains = fsms.Single(f => f.name == "IK Remains" && f.FsmName == "Control");
        var entrance = remains.FsmVariables.GetFsmGameObject("Dream Enter").Value;
        if (remains.ActiveStateName != "Off" || entrance == null || entrance.activeInHierarchy)
            throw new Exception("Lost Kin native dream entrance still enabled after completion");
        Mark("PASS native Lost Kin dream entrance disabled after completed fight");
        var reward = fsms.Single(f => f.FsmName == "Conversation Control" && f.FsmStates.SelectMany(s => s.Actions)
            .OfType<SetPlayerDataBool>().Any(a => a.boolName.Value == "infectedKnightOrbsCollected"));
        if (!claimed) {
            if (!reward.gameObject.activeInHierarchy) throw new Exception("Unclaimed Lost Kin reward spirit absent");
            var control = reward.GetComponents<PlayMakerFSM>().Single(f => f.FsmName == "npc_control");
            HeroController.instance.transform.position = reward.transform.position + Vector3.right;
            Freeze(); Physics2D.SyncTransforms();
            yield return Until(() => control.ActiveStateName == "In Range", "native Lost Kin spirit in interaction range", 10);
            control.Fsm.Event(control.Fsm.ActiveState.Actions.OfType<ListenForUp>().Single().wasPressed);
            yield return Until(() => reward.ActiveStateName == "Start Pause" || reward.ActiveStateName == "Bow Anim" ||
                reward.ActiveStateName == "Vanish Glow", "native Lost Kin spirit bows before essence grant", 10);
        } else if (reward.gameObject.activeInHierarchy) throw new Exception("Claimed Lost Kin reward became available twice");
        var expected = initialOrbs + (claimed ? 0 : 400);
        yield return Until(() => pd.GetInt("dreamOrbs") == expected && pd.GetBool("infectedKnightOrbsCollected"),
            "personal Lost Kin essence grant expected=" + expected, 30);
        yield return Until(() => HeroController.instance.acceptingInput && !HeroController.instance.controlReqlinquished &&
            !pd.GetBool("disablePause"), "Lost Kin reward releases personal control", 10);
        Flag(role + ".kin-reward");
        yield return Until(() => Has((host ? "guest" : "host") + ".kin-reward"), "both finish personal Lost Kin settlement");
        yield return new WaitForSecondsRealtime(2);
        if (pd.GetInt("dreamOrbs") != expected) throw new Exception("Lost Kin essence duplicated by teammate claim");
        Mark("PASS Lost Kin defeat, native return and personal reward stable; essence=" + expected);
    }
}
