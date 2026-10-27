using System;
using System.Collections;
using System.Linq;
using HutongGames.PlayMaker;
using UnityEngine;
using Object = UnityEngine.Object;

namespace HollowConnect.Smoke;

public sealed partial class ProgressProbe {
    private int WarriorGuestHistory => int.TryParse(Environment.GetEnvironmentVariable("HOLLOWCONNECT_DREAM_GUEST_HISTORY"), out var value) ? value : 0;

    private void PrepareWarriorHistory() {
        if (WarriorGuestHistory == 0) return;
        if (Environment.GetEnvironmentVariable("HOLLOWCONNECT_DREAM_SUMMONER") != "host")
            throw new Exception("Mixed history probe requires an unbeaten host to summon");
        foreach (var scene in DreamWarriorScenes())
            PlayerData.instance.SetInt(Hkmp.Game.Client.Entity.DreamWarriorSynchronizer.Keys[scene], host ? 0 : WarriorGuestHistory);
        if (!host) PlayerData.instance.SetInt("dreamOrbs", 230);
        Mark("WARRIOR HISTORY FIXTURE guest=" + WarriorGuestHistory + " ownOrbs=" + PlayerData.instance.GetInt("dreamOrbs"));
    }

    private void WarriorTextTrace(string phase) {
        foreach (var f in Object.FindObjectsOfType<PlayMakerFSM>(true).Where(f => f.name == "Area Title" ||
            f.name == "Text Dream" || f.name == "Text" && f.FsmName == "Dialogue Page Control")) {
            Mark("WARRIOR UI " + phase + " " + f.name + "/" + f.FsmName + " active=" + f.gameObject.activeInHierarchy + " state=" + f.ActiveStateName);
            foreach (var v in f.FsmVariables.StringVariables) Mark("WARRIOR UI VAR " + v.Name + "=" + v.Value);
        }
        foreach (var c in Object.FindObjectsOfType<Component>(true).Where(c => c != null && c.GetType().Name == "TextMeshPro")) {
            var value = c.GetType().GetProperty("text")?.GetValue(c, null) as string;
            if (string.IsNullOrEmpty(value)) continue;
            var render = c.GetComponent<Renderer>();
            Mark("WARRIOR TEXT " + phase + " " + c.name + " active=" + c.gameObject.activeInHierarchy + " renderer=" + render?.enabled + " text=" + value.Replace("\n", "\\n"));
            if (phase == "tombstone-interaction" && c.name == "Text" && c.gameObject.activeInHierarchy && render != null && render.enabled && value == "#!##!#")
                throw new Exception("Native tombstone displayed the empty dialogue placeholder");
        }
    }

    private IEnumerator WarriorClaimedInteraction(string scene, PlayMakerFSM npc, PlayMakerFSM control) {
        var hero = HeroController.instance;
        hero.transform.position = npc.transform.position + new Vector3(1, 0, 0);
        Freeze();
        WarriorTextTrace("before-interaction");
        // Choose the scene's available interaction. A collected ghost should
        // remain inert; its ordinary tombstone must still be readable.
        if (npc.ActiveStateName != "Inert") {
            Mark("WARRIOR INTERACT unexpected active collected ghost");
            control.SetState("Take Control");
            yield return new WaitForSecondsRealtime(1);
            WarriorTextTrace("ghost-interaction");
            Dump(npc);
            throw new Exception("Collected warrior reactivated its uninitialized conversation; title=" +
                npc.FsmVariables.GetFsmString("NPC Title")?.Value + "; repeat=" + npc.FsmVariables.GetFsmString("Convo Repeat")?.Value);
        }
        var inspect = Object.FindObjectsOfType<PlayMakerFSM>(true).Single(f => f.gameObject.scene.name == scene &&
            f.name == "Inspect Region Ghost" && f.FsmName == "inspect_region");
        if (!inspect.gameObject.activeInHierarchy) throw new Exception("Collected warrior tombstone inspection was disabled");
        hero.transform.position = inspect.transform.position + new Vector3(1, 0, 0);
        inspect.SetState("Take Control");
        yield return Until(() => inspect.ActiveStateName == "Read", "claimed guest reads native tombstone after encounter", 10);
        if (Has(role + ".failed")) yield break;
        WarriorTextTrace("tombstone-interaction");
        if (npc.ActiveStateName != "Inert" || string.IsNullOrEmpty(inspect.FsmVariables.GetFsmString("Game Text Convo")?.Value))
            throw new Exception("Tombstone interaction woke collected ghost or used an empty text key");
        inspect.SendEvent("CONVO_FINISH");
        yield return Until(() => hero.acceptingInput && !hero.controlReqlinquished && !PlayerData.instance.GetBool("disablePause"),
            "claimed guest closes inspection and regains input", 10);
    }
}
