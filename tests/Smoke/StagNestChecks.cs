using System;
using System.Collections;
using System.Linq;
using HollowConnect;
using HutongGames.PlayMaker.Actions;
using UnityEngine;
using Object = UnityEngine.Object;

namespace HollowConnect.Smoke;

public sealed partial class ProgressProbe {
    private bool StagNestCase => Environment.GetEnvironmentVariable("HOLLOWCONNECT_STAG_NEST_CHECK") == "1";
    private IEnumerator StagNestChecks() {
        var pd = PlayerData.instance;
        if (Environment.GetEnvironmentVariable("HOLLOWCONNECT_GRUB_CHECK") == "1") { yield return GrubChecks(); yield break; }
        if (Environment.GetEnvironmentVariable("HOLLOWCONNECT_STAG_SPLIT") == "1") {
            yield return Until(() => HollowConnect.Core.StagStations.Count(pd.GetBool) == 9 && pd.GetInt("stationsOpened") >= 9,
                "split station flags reconstruct native nine-station count", 20);
            ResourceCheck(!pd.GetBool("openedStagNest"), "Nest waits for original Stag conversation");
            yield return Room("Crossroads_47");
            var control = Object.FindObjectsOfType<PlayMakerFSM>().Single(f => f.name == "Stag" && f.FsmName == "Stag Control");
            control.SendEvent("STAG BELL RUNG");
            yield return Until(() => control.ActiveStateName == "Idle" || control.ActiveStateName == "In Range", "native Stag arrives", 15);
            yield return ResourceBarrier("stag-dialogue-ready");
            if (host) {
                HeroController.instance.transform.position = control.transform.position + new Vector3(-2, 0, 0);
                control.SetState("Take Control");
            }
            yield return Until(() => pd.GetBool("openedStagNest"), "native Remember 3 conversation unlocks Nest after split purchases", 25);
            ResourceCheck(!host || pd.GetBool("stagRemember3"), "native dialogue granted Nest");
            yield return ResourceBarrier("stag-dialogue-complete");
            yield break;
        }
        if (Environment.GetEnvironmentVariable("HOLLOWCONNECT_FEEDBACK_INSPECT") == "1") {
            foreach (var scene in new[] { "Crossroads_47", "Crossroads_03", "Ruins2_11" }) {
                yield return Room(scene);
                foreach (var fsm in Object.FindObjectsOfType<PlayMakerFSM>(true).Where(f => f.gameObject.scene.name == scene &&
                    (f.name.IndexOf("Grub", StringComparison.OrdinalIgnoreCase) >= 0 || f.name == "Stag"))) Dump(fsm);
                foreach (var item in Object.FindObjectsOfType<PersistentBoolItem>(true).Where(i => i.gameObject.scene.name == scene &&
                    i.name.IndexOf("Grub", StringComparison.OrdinalIgnoreCase) >= 0))
                    Mark("GRUB RECEIPT " + scene + "/" + item.name + " " + Newtonsoft.Json.JsonConvert.SerializeObject(item.persistentBoolData));
            }
            yield break;
        }
        yield return Until(() => pd.GetBool("openedStagNest") && pd.GetBool("openedTown") && runtime.Peers.Count == 1, "host's existing stag nest and town unlocks catch up over UDP", 15);
        ResourceCheck(!Has(role + ".failed"), "stag nest catch-up completed");
        if (!host) pd.SetBool("openedHiddenStation", true);
        yield return Until(() => pd.GetBool("openedHiddenStation"), "guest's live hidden station unlock shares back to host", 15);
        ResourceCheck(!Has(role + ".failed"), "hidden station live unlock completed");
        yield return Room("Cliffs_03");
        var stag = Object.FindObjectsOfType<PlayMakerFSM>().Single(f => f.gameObject.scene.name == "Cliffs_03" && f.name == "Stag" && f.FsmName == "Stag Control");
        yield return Until(() => stag.ActiveStateName == "Idle" || stag.ActiveStateName == "In Range", "native stag arrives and enables travel interaction without a bell", 12);
        ResourceCheck(!Has(role + ".failed"), "stag arrival completed");
        ResourceCheck(pd.GetInt("stagPosition") == stag.FsmVariables.FindFsmInt("Station Position Number").Value &&
            stag.GetComponent<Renderer>().enabled && stag.transform.Find("Travel Range").GetComponent<Collider2D>().enabled &&
            stag.transform.Find("Travel Range").gameObject.activeInHierarchy,
            "native stag visible with active travel trigger at the Nest");
        Mark("STAG PD opened=" + pd.GetBool("openedStagNest") + " position=" + pd.GetInt("stagPosition") + " state=" + stag.ActiveStateName);
        yield return ResourceBarrier("stag-arrived");
    }
}
