using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace HollowConnect;

// Keep the native mask animations and spacing. Shared capacity can arrive
// after the HUD's initial maxHealth read, or exceed its eleven preset masks.
internal sealed class LinkedHealthHud : MonoBehaviour {
    private readonly List<GameObject> added = new();
    private int capacity;
    private bool refreshBlue, releasing;

    internal void SetCapacity(int value) {
        if (capacity == value) return;
        capacity = value;
        var masks = GetComponentsInChildren<PlayMakerFSM>(true).Where(f => f.FsmName == "health_display").ToArray();
        var template = masks.OrderBy(f => f.FsmVariables.FindFsmInt("Health Number").Value).Last();
        var last = template.FsmVariables.FindFsmInt("Health Number").Value;
        var first = masks.First(f => f.FsmVariables.FindFsmInt("Health Number").Value == 1);
        var step = (template.transform.localPosition - first.transform.localPosition) / (last - 1);
        if (last < value) {
            var staging = new GameObject("Shared mask staging"); staging.SetActive(false);
            for (var number = last + 1; number <= value; number++) {
                var mask = Instantiate(template.gameObject, staging.transform, false);
                mask.name = "Health " + number; mask.SetActive(false);
                var display = mask.GetComponents<PlayMakerFSM>().First(f => f.FsmName == "health_display");
                display.FsmVariables.FindFsmInt("Health Number").Value = number;
                mask.transform.SetParent(transform, false);
                mask.transform.localPosition = first.transform.localPosition + step * (number - 1);
                added.Add(mask); mask.SetActive(true);
            }
            Destroy(staging);
        }
        // This native HUD event rechecks both capacity and fill, in either
        // direction, without sending charm/gameplay events to the Knight.
        foreach (var mask in GetComponentsInChildren<PlayMakerFSM>(true).Where(f => f.FsmName == "health_display"))
            mask.SendEvent("CHARM INDICATOR CHECK");
        refreshBlue = true;
    }

    private void Update() {
        if (!refreshBlue) return;
        var blue = GetComponents<PlayMakerFSM>().FirstOrDefault(f => f.FsmName == "Blue Health Control");
        if (blue == null) return;
        if (blue.ActiveStateName != "Wait" && blue.ActiveStateName != "Idle") return;
        // UPDATE BLUE HEALTH also recalculates gameplay blue health from
        // charms, discarding cocoon masks. Move existing visuals only; future
        // native additions already read the shared maxHealth through the hook.
        var first = transform.Find("Health 1");
        var step = transform.Find("Health 2").localPosition.x - first.localPosition.x;
        foreach (var mask in GetComponentsInChildren<PlayMakerFSM>(true).Where(f => f.FsmName == "blue_health_display")) {
            var number = mask.FsmVariables.FindFsmInt("Health Number").Value;
            if (number <= 0) continue;
            var position = mask.transform.localPosition;
            position.x = first.localPosition.x + step * (capacity + number - 1);
            mask.transform.localPosition = position;
        }
        refreshBlue = false;
        if (releasing) Destroy(this);
    }

    internal void Restore() {
        foreach (var mask in added) if (mask != null) { mask.SetActive(false); Destroy(mask); }
        added.Clear();
        capacity = 0; releasing = true;
        SetCapacity(PlayerData.instance.GetInt("maxHealth"));
    }
}
