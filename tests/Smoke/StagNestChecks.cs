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
