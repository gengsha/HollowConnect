using System;
using System.Collections;
using System.Linq;
using HutongGames.PlayMaker.Actions;
using UnityEngine;
using Object = UnityEngine.Object;

namespace HollowConnect.Smoke;

public sealed partial class ProgressProbe {
    private bool CatchupKin => Environment.GetEnvironmentVariable("HOLLOWCONNECT_DREAM_CATCHUP") == "Dream_03_Infected_Knight";
    private bool CatchupChampion => Environment.GetEnvironmentVariable("HOLLOWCONNECT_DREAM_CATCHUP") == "Dream_01_False_Knight";
    private string CatchupDefeat => CatchupKin ? "infectedKnightDreamDefeated" : CatchupChampion ? "falseKnightDreamDefeated" : "mageLordDreamDefeated";
    private string CatchupCollected => CatchupKin ? "infectedKnightOrbsCollected" : CatchupChampion ? "falseKnightOrbsCollected" : "mageLordOrbsCollected";
    private string CatchupRoom => CatchupKin ? "Abyss_19" : CatchupChampion ? "Crossroads_10" : "Ruins1_24";
    private void PrepareDreamCatchup() {
        var pd = PlayerData.instance;
        pd.SetBool(CatchupDefeat, host);
        pd.SetBool(CatchupCollected, host || Environment.GetEnvironmentVariable("HOLLOWCONNECT_DREAM_SETTLEMENT_CASE") == "claimed");
        pd.SetBool("killedInfectedKnight", true); pd.SetBool("infectedKnightDefeated", true);
        pd.SetBool("killedMageLord", true); pd.SetBool("mageLordDefeated", true); pd.SetInt("quakeLevel", 1);
        pd.SetBool("killedFalseKnight", true); pd.SetBool("falseKnightDefeated", true);
        pd.SetInt("dreamOrbs", host ? 100 : 230);
    }
    private PlayMakerFSM[] CatchupFsms() => Object.FindObjectsOfType<PlayMakerFSM>(true).Where(f =>
        f.gameObject.scene.name.StartsWith(CatchupRoom, StringComparison.Ordinal)).ToArray();
    private PlayMakerFSM CatchupReward() => CatchupFsms().Single(f => f.FsmName == "Conversation Control" &&
        f.FsmStates.SelectMany(s => s.Actions).OfType<SetPlayerDataBool>().Any(a => a.boolName.Value == CatchupCollected));
    private void TraceCatchup(string phase) {
        Mark("CATCHUP " + phase + " defeat=" + PlayerData.instance.GetBool(CatchupDefeat) + " collected=" + PlayerData.instance.GetBool(CatchupCollected));
        foreach (var gate in Object.FindObjectsOfType<DeactivateIfPlayerdataFalse>(true).Where(c => c.gameObject.scene.name.StartsWith(CatchupRoom, StringComparison.Ordinal)))
            Mark("CATCHUP NATIVE GATE " + gate.name + " bool=" + gate.boolName + " activeSelf=" + gate.gameObject.activeSelf);
        foreach (var f in CatchupFsms().Where(f => f.name.Contains("Ghost") || f.FsmStates.SelectMany(s => s.Actions)
            .OfType<PlayerDataBoolTest>().Any(a => a.boolName.Value == CatchupDefeat))) {
            Mark("CATCHUP FSM " + f.gameObject.scene.name + "/" + f.name + "/" + f.FsmName + " active=" + f.gameObject.activeInHierarchy + " self=" + f.gameObject.activeSelf + " parent=" + f.transform.parent?.name + " state=" + f.ActiveStateName);
            Dump(f);
            foreach (var c in f.GetComponents<Component>())
                Mark("CATCHUP COMPONENT " + f.name + " " + c.GetType().Name + " " + string.Join(";", c.GetType().GetFields(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance)
                    .Where(v => v.FieldType == typeof(string) || v.FieldType == typeof(bool)).Select(v => v.Name + "=" + v.GetValue(c))));
        }
    }
    private IEnumerator DreamCatchup() {
        var pd = PlayerData.instance;
        var before = pd.GetInt("dreamOrbs"); var claimed = pd.GetBool(CatchupCollected);
        Enter(CatchupRoom);
        yield return Until(() => AliveScene(CatchupRoom) && engine.ClientManager.SceneSnapshotReady, "native catchup entrance room loaded");
        Freeze(); yield return new WaitForSecondsRealtime(2);
        if (!host) {
            TraceCatchup("before source joins");
            if (pd.GetBool(CatchupDefeat)) throw new Exception("Guest fixture already has completion before source joins");
            Flag("guest.catchup.loaded");
        }
        yield return Until(() => pd.GetBool(CatchupDefeat) && runtime.Peers.Count == 1, "missing dream completion repaired through actual UDP join");
        yield return new WaitForSecondsRealtime(2);
        TraceCatchup("after union");
        var sharedAmount = !host && !claimed && runtime.Shared ? CatchupKin ? 400 : 300 : 0;
        yield return Until(() => pd.GetInt("dreamOrbs") == before + sharedAmount, "late join receives the missing boss essence source", 12);
        if (pd.GetBool(CatchupCollected) != claimed)
            throw new Exception("Shared essence changed the local native conversation history");
        var fsms = CatchupFsms();
        var entrance = fsms.Single(f => f.FsmName == "Control" && f.FsmStates.SelectMany(s => s.Actions)
            .OfType<PlayerDataBoolTest>().Any(a => a.boolName.Value == CatchupDefeat));
        var dreamEnter = entrance.FsmVariables.GetFsmGameObject("Dream Enter")?.Value;
        if (entrance.ActiveStateName != "Off" || dreamEnter == null || dreamEnter.activeInHierarchy)
            throw new Exception("Loaded native dream entrance stayed active after shared completion: " + entrance.name + "/" + entrance.ActiveStateName);
        var reward = CatchupReward();
        if (!claimed) {
            if (!reward.gameObject.activeInHierarchy) throw new Exception("Unclaimed dream spirit stayed hidden after shared completion: " + reward.name + "/" + reward.ActiveStateName);
            var interaction = reward.GetComponents<PlayMakerFSM>().Single(f => f.FsmName == "npc_control");
            HeroController.instance.transform.position = reward.transform.position + Vector3.right;
            Freeze(); Physics2D.SyncTransforms();
            yield return Until(() => interaction.ActiveStateName == "In Range", "native repaired dream spirit interaction range", 10);
            interaction.Fsm.Event(interaction.Fsm.ActiveState.Actions.OfType<ListenForUp>().Single().wasPressed);
            var deadline = Time.realtimeSinceStartup + 30;
            while (!pd.GetBool(CatchupCollected) && Time.realtimeSinceStartup < deadline) {
                if (!CatchupKin && reward.Fsm.ActiveState?.Transitions.Any(t => t.EventName == "CONVO_FINISH") == true) reward.SendEvent("CONVO_FINISH");
                yield return null;
            }
        } else if (reward.gameObject.activeInHierarchy) throw new Exception("Already claimed reward reappeared after completion union");
        var expected = before + (claimed ? 0 : CatchupKin ? 400 : 300);
        yield return Until(() => pd.GetBool(CatchupCollected) && pd.GetInt("dreamOrbs") == expected && HeroController.instance.acceptingInput &&
            !HeroController.instance.controlReqlinquished && !pd.GetBool("disablePause"), "native catchup essence and personal control expected=" + expected, 30);
        Flag(role + ".catchup.claimed");
        yield return Until(() => Has((host ? "guest" : "host") + ".catchup.claimed"), "both finish catchup personal reward check");
        // Reload the same room through the normal loader: no reward or entrance may return.
        Enter("Town"); yield return Until(() => AliveScene("Town"), "catchup room exit");
        Flag(role + ".catchup.exited");
        yield return Until(() => Has((host ? "guest" : "host") + ".catchup.exited"), "both exit before reward reentry");
        Enter(CatchupRoom); yield return Until(() => AliveScene(CatchupRoom), "catchup reward room reentry");
        yield return new WaitForSecondsRealtime(2);
        if (CatchupReward().gameObject.activeInHierarchy || pd.GetInt("dreamOrbs") != expected)
            throw new Exception("Catchup reward duplicated on room reentry");
        Mark("PASS shared dream catchup without rematch; personal essence=" + expected + " claimedBefore=" + claimed);
    }
}
