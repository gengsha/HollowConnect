using Hkmp;
using UnityEngine;

namespace HollowConnect;

public sealed partial class CoopRuntime {
    private void RefreshStagNest() {
        if (!Shared || CoopContext.LocalScene != "Cliffs_03" || !PlayerData.instance.GetBool("openedStagNest")) return;
        // The Nest has no bell. A teammate warp bypasses native Stag travel,
        // leaving this player's Stag away and its interaction collider off.
        // Ask the idle native controller to bring him in, just as a bell does;
        // keep each player's travel position and active conversations local.
        foreach (var fsm in Object.FindObjectsOfType<PlayMakerFSM>()) {
            if (fsm.gameObject.scene.name != "Cliffs_03" || fsm.name != "Stag" ||
                fsm.FsmName != "Stag Control" || fsm.ActiveStateName != "Set Can Talk") continue;
            var stationFlag = fsm.FsmVariables.FindFsmString("Station Opened Bool");
            if (stationFlag != null && PlayerData.instance.GetBool(stationFlag.Value)) fsm.SendEvent("STAG BELL RUNG");
        }
    }
}
