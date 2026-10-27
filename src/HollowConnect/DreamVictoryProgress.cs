using System;
using System.Linq;
using HutongGames.PlayMaker.Actions;
using UnityEngine;
using Object = UnityEngine.Object;

namespace HollowConnect;

internal static class DreamVictoryProgress {
    internal static void Refresh(string key) {
        var room = key == "infectedKnightDreamDefeated" ? "Abyss_19" : key == "mageLordDreamDefeated" ? "Ruins1_24" :
            key == "falseKnightDreamDefeated" ? "Crossroads_10" : null;
        if (room == null || GameManager.instance.sceneName != room || !PlayerData.instance.GetBool(key)) return;
        var collected = key == "infectedKnightDreamDefeated" ? "infectedKnightOrbsCollected" :
            key == "mageLordDreamDefeated" ? "mageLordOrbsCollected" : "falseKnightOrbsCollected";

        // These native remains only check completion on room entry. Recheck
        // their idle glow when a remote permanent-progress grant arrives.
        // Never restart a dream-entry sequence or an NPC conversation.
        foreach (var fsm in Object.FindObjectsOfType<PlayMakerFSM>().Where(f => InRoom(f.gameObject, room) &&
            f.FsmName == "Control" && f.ActiveStateName == "Glow" && f.FsmStates.Any(s => s.Name == "Check" &&
                s.Actions.OfType<PlayerDataBoolTest>().Any(a => a.boolName.Value == key))))
            fsm.SetState("Check");

        // Only revive the dormant native reward NPC whose Init state checks
        // both this victory and its personal claim. Its original dialogue,
        // essence award and control recovery remain local.
        if (PlayerData.instance.GetBool(collected)) return;
        foreach (var npc in Object.FindObjectsOfType<PlayMakerFSM>(true).Where(f => InRoom(f.gameObject, room) &&
            f.FsmName == "Conversation Control" && !f.gameObject.activeSelf && string.IsNullOrEmpty(f.ActiveStateName) &&
            f.FsmStates.Any(s => s.Name == "Init" && s.Actions.OfType<PlayerDataBoolTest>().Any(a => a.boolName.Value == key) &&
                s.Actions.OfType<PlayerDataBoolTest>().Any(a => a.boolName.Value == collected))))
            npc.gameObject.SetActive(true);
    }

    private static bool InRoom(GameObject owner, string room) => owner.scene.name == room ||
        owner.scene.name.StartsWith(room + "_", StringComparison.Ordinal);
}
