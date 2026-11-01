using HutongGames.PlayMaker;

namespace Hkmp.Game.Client.Entity;

// These hit reactions disable a child that owns local dialogue input. Peers
// can swing while the reader has relinquished control. Keep the requester
// alive until native cleanup restores control, then allow hits as usual.
internal static class NpcDialogue {
    internal static bool Ignore(HutongGames.PlayMaker.Fsm receiver, string signal) {
        if (signal != "TAKE DAMAGE" && signal != "BLOCKED HIT" && signal != "NAIL HIT") return false;
        var owner = receiver.GameObject;
        if (owner == null) return false;
        // Shatter deactivates Bottle Physical, including its dream requester.
        // A peer may still rescue their own grub and share its receipt; the
        // reader can finish their local bottle after closing the dream.
        if (signal == "NAIL HIT")
            return receiver.Name == "Bottle Control" && DreamReading(owner.transform.Find("Bottle Physical/Dream Dialogue"));
        if (owner.name == "Banker Spa NPC" && owner.scene.name == "Ruins_Bathhouse") {
            if (signal != "TAKE DAMAGE") return false;
        } else if (owner.name == "Happy Spider NPC" && owner.scene.name == "Deepnest_41") {
            // tink_effect converts TAKE DAMAGE into BLOCKED HIT. Guard both
            // its original contact and the Control FSM's retreat event.
            if (signal == "BLOCKED HIT" && receiver.Name != "Control") return false;
        } else return false;
        var npc = owner.transform.Find("NPC");
        if (npc != null && npc.gameObject.activeInHierarchy)
            foreach (var control in npc.GetComponents<PlayMakerFSM>())
                if (control.FsmName == "npc_control" && control.FsmVariables.FindFsmBool("Talking")?.Value == true)
                    return true;
        // Dream reading owns input independently of npc_control.Talking.
        // Block Disable also deactivates this requester, stranding its UI.
        return DreamReading(owner.transform.Find("Dream Dialogue"));
    }
    private static bool DreamReading(UnityEngine.Transform dream) {
        if (dream == null || !dream.gameObject.activeInHierarchy) return false;
        foreach (var control in dream.GetComponents<PlayMakerFSM>())
            if (control.FsmName == "npc_dream_dialogue" && control.ActiveStateName is
                "Take Control" or "Impact" or "Box Up" or "Convo" or "Box Down" or "Relinquish Control")
                return true;
        return false;
    }
}
