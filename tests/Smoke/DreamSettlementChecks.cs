using System;
using System.Collections;
using System.Linq;
using Hkmp.Game.Client.Entity;
using HutongGames.PlayMaker.Actions;
using UnityEngine;
using Object = UnityEngine.Object;

namespace HollowConnect.Smoke;

public sealed partial class ProgressProbe {
    private static string DreamDefeatKey(string scene) => scene == "Dream_02_Mage_Lord" ? "mageLordDreamDefeated" :
        scene == "Dream_04_White_Defender" ? "whiteDefenderDefeated" : "greyPrinceDefeated";
    private static string DreamCollectedKey(string scene) => scene == "Dream_02_Mage_Lord" ? "mageLordOrbsCollected" :
        scene == "Dream_04_White_Defender" ? "whiteDefenderOrbsCollected" : "greyPrinceOrbsCollected";
    private static string DreamReturnRoom(string scene) => scene == "Dream_02_Mage_Lord" ? "Ruins1_24" :
        scene == "Dream_04_White_Defender" ? "Waterways_15" : "Room_Bretta_Basement";
    private static EntityType DreamBodyType(string scene) => scene == "Dream_02_Mage_Lord" ? EntityType.SoulTyrant :
        scene == "Dream_04_White_Defender" ? EntityType.WhiteDefender : EntityType.GreyPrinceZote;
    private void PrepareDreamSettlement(string scene) {
        if (scene != "Dream_02_Mage_Lord" && scene != "Dream_04_White_Defender" && scene != "Dream_Mighty_Zote")
            throw new Exception("Settlement fixture is scoped to Soul Tyrant, White Defender and Grey Prince");
        var pd = PlayerData.instance;
        var claimed = !host && Environment.GetEnvironmentVariable("HOLLOWCONNECT_DREAM_SETTLEMENT") == "claimed";
        pd.SetBool(DreamDefeatKey(scene), claimed); pd.SetBool(DreamCollectedKey(scene), claimed);
        pd.SetInt("whiteDefenderDefeats", claimed ? 2 : 0); pd.SetInt("greyPrinceDefeats", claimed ? 2 : 0);
        pd.SetBool("killedMageLord", true); pd.SetBool("mageLordDefeated", true); pd.SetInt("quakeLevel", 1);
        pd.SetBool("killedDungDefender", true); pd.SetBool("defeatedDungDefender", true); pd.SetBool("dungDefenderEncounterReady", true);
        pd.SetBool("maskBrokenLurien", true); pd.SetBool("maskBrokenMonomon", true); pd.SetBool("maskBrokenHegemol", true);
        pd.SetBool("dungDefenderAwoken", false); pd.SetBool("dungDefenderLeft", false);
        pd.SetBool("dungDefenderSleeping", true);
        pd.SetBool("brettaRescued", true); pd.SetBool("brettaLeftTown", false); pd.SetBool("zoteDefeated", true);
        pd.SetString("dreamReturnScene", DreamReturnRoom(scene)); pd.SetString("dreamReturnGate", "door_dreamReturn");
    }
    private void TraceDreamSettlement(string scene, string phase) {
        Mark("SETTLEMENT " + phase + " scene=" + GameManager.instance.sceneName + " defeat=" + PlayerData.instance.GetBool(DreamDefeatKey(scene)) +
            " collected=" + PlayerData.instance.GetBool(DreamCollectedKey(scene)) + " orbs=" + PlayerData.instance.GetInt("dreamOrbs") +
            " whiteCount=" + PlayerData.instance.GetInt("whiteDefenderDefeats") + " greyCount=" + PlayerData.instance.GetInt("greyPrinceDefeats"));
        foreach (var f in Object.FindObjectsOfType<PlayMakerFSM>(true).Where(f => f.gameObject.scene.name.StartsWith(GameManager.instance.sceneName, StringComparison.Ordinal) &&
            (f.name.Contains("Corpse") || f.name.Contains("Ghost") || f.name.Contains("Bretta") || f.name.Contains("Statue") ||
                f.name.Contains("Dream") || f.name.Contains("Dung Defender")))) {
            Mark("SETTLEMENT FSM " + f.name + "/" + f.FsmName + " active=" + f.gameObject.activeInHierarchy + " enabled=" + f.enabled + " state=" + f.ActiveStateName);
            Dump(f);
        }
    }
    private IEnumerator DreamSettlement(string scene) {
        var pd = PlayerData.instance;
        var initialOrbs = pd.GetInt("dreamOrbs");
        var alreadyClaimed = pd.GetBool(DreamCollectedKey(scene));
        var counter = scene == "Dream_04_White_Defender" ? "whiteDefenderDefeats" : scene == "Dream_Mighty_Zote" ? "greyPrinceDefeats" : null;
        var initialWins = counter == null ? 0 : pd.GetInt(counter);
        var manager = Field<EntityManager>(engine.ClientManager, "_entityManager");
        var type = DreamBodyType(scene);
        var entity = EntityReferences.Entities.Values.Single(e => e.Type == type && e.SourceScene == scene && e.Object.Host != null && e.Object.Host.GetComponent<HealthManager>() != null);
        var body = (manager.IsSceneHost ? entity.Object.Host : entity.Object.Client).GetComponent<HealthManager>();
        var control = body.GetComponents<PlayMakerFSM>().Single(f => f.FsmName == (scene == "Dream_02_Mage_Lord" ? "Mage Lord" : scene == "Dream_04_White_Defender" ? "Dung Defender" : "Control"));
        Mark("SETTLEMENT PREFLIGHT " + body.name + " authority=" + manager.IsSceneHost + " active=" + body.gameObject.activeInHierarchy + " hp=" + body.hp + " state=" + control.ActiveStateName);
        Dump(control);
        foreach (var f in Object.FindObjectsOfType<PlayMakerFSM>(true).Where(f => f.gameObject.scene.name == scene && EntityReferences.Owner(f.gameObject) == null)) {
            Mark("SETTLEMENT LOCAL " + f.name + "/" + f.FsmName + " active=" + f.gameObject.activeInHierarchy + " state=" + f.ActiveStateName);
            if (f.FsmName == "Battle Control") Dump(f);
        }
        var triggers = Object.FindObjectsOfType<Collider2D>(true).Where(c => c.gameObject.scene.name == scene && c.isTrigger && c.enabled && c.gameObject.activeInHierarchy &&
            (c.name.Contains("Battle") || c.name.Contains("Boss") || c.name.Contains("Start") || c.GetComponents<PlayMakerFSM>().Any(f => f.FsmName == "Battle Control"))).ToArray();
        Mark("SETTLEMENT native triggers=" + string.Join(",", triggers.Select(t => t.name + "=" + t.bounds)));
        HeroController.instance.transform.position = triggers.Length > 0 ? triggers.OrderByDescending(t => t.bounds.center.y).First().bounds.center : body.transform.position + Vector3.left * 4;
        Freeze(); Physics2D.SyncTransforms();
        yield return Until(() => body.gameObject.activeInHierarchy && !body.IsInvincible && body.hp > 0 &&
            control.ActiveStateName != "Dormant" && control.ActiveStateName != "Sleep", "native dream boss activates " + scene, 30);
        Flag(role + ".settlement-battle");
        yield return Until(() => Has((host ? "guest" : "host") + ".settlement-battle"), "both participate in native dream battle");
        if (manager.IsSceneHost) body.ApplyExtraDamage(body.hp);
        if (scene == "Dream_02_Mage_Lord") {
            var phase2 = EntityReferences.Entities.Values.Single(e => e.Type == EntityType.SoulTyrantPhase2 && e.SourceScene == scene);
            var hp2 = (manager.IsSceneHost ? phase2.Object.Host : phase2.Object.Client).GetComponent<HealthManager>();
            yield return Until(() => hp2.gameObject.activeInHierarchy && !hp2.IsInvincible && hp2.hp > 0,
                "Soul Tyrant native phase two ready", 40);
            if (pd.GetBool("mageLordDreamDefeated") && Environment.GetEnvironmentVariable("HOLLOWCONNECT_DREAM_SETTLEMENT") != "claimed")
                throw new Exception("Soul Tyrant first phase incorrectly completed whole battle");
            Flag(role + ".settlement-phase2");
            yield return Until(() => Has((host ? "guest" : "host") + ".settlement-phase2"), "both reject premature first-phase completion");
            if (manager.IsSceneHost) hp2.ApplyExtraDamage(hp2.hp);
        }
        var deadline = Time.realtimeSinceStartup + 85; var nextTrace = Time.realtimeSinceStartup + 15;
        while (GameManager.instance.sceneName == scene && Time.realtimeSinceStartup < deadline) {
            if (scene == "Dream_04_White_Defender") foreach (var f in Object.FindObjectsOfType<PlayMakerFSM>(true).Where(f =>
                f.gameObject.scene.name == scene && f.name.StartsWith("Corpse White Defender", StringComparison.Ordinal) && f.enabled && f.gameObject.activeInHierarchy &&
                (f.ActiveStateName == "Outro Msg 1a" || f.ActiveStateName == "Outro Msg 1b" || f.ActiveStateName == "Outro Msg Repeat")))
                    f.SendEvent("CONVO_FINISH"); // Only dismiss the actual native outro dialogue.
            if (Time.realtimeSinceStartup >= nextTrace) { TraceDreamSettlement(scene, "await native return"); nextTrace += 35; }
            yield return null;
        }
        TraceDreamSettlement(scene, "after death");
        if (GameManager.instance.sceneName != DreamReturnRoom(scene)) throw new Exception("Native dream return failed: expected " + DreamReturnRoom(scene));
        var wake = HeroController.instance.GetComponents<PlayMakerFSM>().Single(f => f.FsmName == "Dream Return");
        yield return Until(() => wake.ActiveStateName == "Ready" || AliveScene(DreamReturnRoom(scene)), "native dream wake awaits input", 20);
        if (wake.ActiveStateName == "Ready") wake.Fsm.Event(wake.Fsm.ActiveState.Actions.OfType<ListenForJump>().Single().wasPressed);
        yield return Until(() => AliveScene(DreamReturnRoom(scene)), "native dream wake restores personal input", 20);
        TraceDreamSettlement(scene, "returned with personal control");
        if (!pd.GetBool(DreamDefeatKey(scene))) throw new Exception("Dream participant missing native defeat metadata after return: " + scene);
        if (counter != null && pd.GetInt(counter) != initialWins + 1) throw new Exception("Personal dream win count missing or duplicated: " + counter +
            " expected=" + (initialWins + 1) + " actual=" + pd.GetInt(counter));
        if (Environment.GetEnvironmentVariable("HOLLOWCONNECT_DREAM_SETTLEMENT") == "inspect") {
            Mark("PASS dream native death and return inspection " + scene); yield break;
        }
        var expected = initialOrbs + (alreadyClaimed ? 0 : 300);
        if (!alreadyClaimed && scene == "Dream_02_Mage_Lord") {
            var reward = Object.FindObjectsOfType<PlayMakerFSM>(true).Single(f =>
                f.gameObject.scene.name.StartsWith(DreamReturnRoom(scene), StringComparison.Ordinal) && f.FsmName == "Conversation Control" &&
                f.FsmStates.SelectMany(s => s.Actions).OfType<SetPlayerDataBool>().Any(a => a.boolName.Value == DreamCollectedKey(scene)));
            if (!reward.gameObject.activeInHierarchy) throw new Exception("Unclaimed Soul Tyrant reward spirit absent");
            var interaction = reward.GetComponents<PlayMakerFSM>().Single(f => f.FsmName == "npc_control");
            HeroController.instance.transform.position = reward.transform.position + Vector3.right;
            Freeze(); Physics2D.SyncTransforms();
            yield return Until(() => interaction.ActiveStateName == "In Range", "native Soul Tyrant spirit interaction range", 10);
            interaction.Fsm.Event(interaction.Fsm.ActiveState.Actions.OfType<ListenForUp>().Single().wasPressed);
            var rewardDeadline = Time.realtimeSinceStartup + 30;
            while (!pd.GetBool(DreamCollectedKey(scene)) && Time.realtimeSinceStartup < rewardDeadline) {
                if (reward.Fsm.ActiveState?.Transitions.Any(t => t.EventName == "CONVO_FINISH") == true) reward.SendEvent("CONVO_FINISH");
                yield return null;
            }
        }
        yield return Until(() => pd.GetBool(DreamCollectedKey(scene)) && pd.GetInt("dreamOrbs") == expected,
            "personal dream essence granted once expected=" + expected + " scene=" + scene, 25);
        yield return Until(() => HeroController.instance.acceptingInput && !HeroController.instance.controlReqlinquished && !pd.GetBool("disablePause"),
            "dream reward releases personal input", 10);
        Flag(role + ".settlement-claimed");
        yield return Until(() => Has((host ? "guest" : "host") + ".settlement-claimed"), "both complete personal dream rewards");
        yield return new WaitForSecondsRealtime(2);
        if (pd.GetInt("dreamOrbs") != expected || counter != null && pd.GetInt(counter) != initialWins + 1)
            throw new Exception("Personal dream reward or win count changed after teammate settlement");
        Mark("PASS dream settlement reward stable " + scene + " orbs=" + expected + " wins=" + (counter == null ? 0 : pd.GetInt(counter)));
    }
}
