using HutongGames.PlayMaker;

namespace Hkmp.Game.Client.Entity;

// Hit Around disables the NPC child without closing its dialogue. A peer can
// still swing while the local hero has relinquished control, leaving that
// dialogue with an inactive requester. Keep the native conversation alive
// until its own Convo End clears Talking, then allow hits/geo as usual.
internal static class BankerDialogue {
    internal static bool Ignore(HutongGames.PlayMaker.Fsm receiver, string signal) {
        if (signal != "TAKE DAMAGE") return false;
        var banker = receiver.GameObject;
        if (banker == null || banker.name != "Banker Spa NPC" || banker.scene.name != "Ruins_Bathhouse") return false;
        var npc = banker.transform.Find("NPC");
        if (npc == null || !npc.gameObject.activeInHierarchy) return false;
        foreach (var control in npc.GetComponents<PlayMakerFSM>())
            if (control.FsmName == "npc_control" && control.FsmVariables.FindFsmBool("Talking")?.Value == true)
                return true;
        return false;
    }
}
