using System;
using System.Collections.Generic;
using System.Linq;
using HollowConnect.Core;
using HutongGames.PlayMaker.Actions;
using UnityEngine;
using Object = UnityEngine.Object;

namespace HollowConnect;

// Presentation only. Grants and source deduplication remain in ResourceShareService.
internal sealed class ResourceNotifications {
    internal const string PopupName = "HollowConnect resource notification";
    private static GameObject template;
    private static readonly Dictionary<ResourceKind, Sprite> icons = new Dictionary<ResourceKind, Sprite>();
    private static readonly Dictionary<ResourceKind, string> names = new Dictionary<ResourceKind, string>();
    private readonly Queue<ResourceKind> order = new Queue<ResourceKind>();
    private readonly Dictionary<ResourceKind, int> pending = new Dictionary<ResourceKind, int>();
    private GameObject active;

    internal static void Preload(Dictionary<string, Dictionary<string, GameObject>> objects) {
        var chest = objects["Tutorial_01"]["_Props/Chest"];
        var shop = objects["Room_shop"]["Shop Menu"];
        try {
            var shiny = chest.transform.Find("Item/Shiny Item (1)").GetComponents<PlayMakerFSM>()
                .Single(f => f.FsmName == "Shiny Control");
            var popup = shiny.FsmStates.Single(s => s.Name == "Trink Flash").Actions.OfType<SpawnObjectFromGlobalPool>()
                .Single(a => a.gameObject.Value != null && a.gameObject.Value.name == "Relic Get Msg").gameObject.Value;
            // Keep the template inactive from creation; no popup FSM runs during preload.
            var holder = new GameObject("HollowConnect notification preload");
            holder.SetActive(false);
            template = Object.Instantiate(popup, holder.transform, false);
            template.SetActive(false);
            template.transform.SetParent(null, false);
            Object.DontDestroyOnLoad(template);
            Object.Destroy(holder);
            foreach (var kind in new[] { ResourceKind.CharmNotch, ResourceKind.PaleOre }) {
                var state = shiny.FsmStates.Single(s => s.Name == (kind == ResourceKind.PaleOre ? "Ore" : "Notch"));
                icons[kind] = (Sprite)state.Actions.OfType<SetSpriteRendererSprite>().Single().sprite.Value;
                names[kind] = state.Actions.OfType<GetLanguageString>().Single().convName.Value;
            }
            var stock = shop.GetComponent<ShopMenuStock>().stock;
            var key = stock.First(g => g != null && g.GetComponent<ShopItemStats>().playerDataBoolName == "slySimpleKey");
            icons[ResourceKind.SimpleKey] = key.transform.Find("Item Sprite").GetComponent<SpriteRenderer>().sprite;
            names[ResourceKind.SimpleKey] = key.GetComponent<ShopItemStats>().nameConvo;
            foreach (var kind in new[] { ResourceKind.MaskShard, ResourceKind.VesselFragment }) {
                var item = stock.First(g => g != null && g.GetComponent<ShopItemStats>().specialType ==
                    (kind == ResourceKind.MaskShard ? 1 : 3));
                icons[kind] = item.transform.Find("Item Sprite").GetComponent<SpriteRenderer>().sprite;
                names[kind] = item.GetComponent<ShopItemStats>().nameConvo;
            }
        } finally {
            // Only the notification and its sprite references are retained, never a pickup or collection UI.
            Object.Destroy(chest); Object.Destroy(shop);
        }
    }

    internal void Enqueue(ResourceSource source) {
        if (source == null) return;
        if (!pending.ContainsKey(source.Kind)) { pending[source.Kind] = 0; order.Enqueue(source.Kind); }
        pending[source.Kind]++;
    }

    internal void Clear() {
        order.Clear(); pending.Clear();
        if (active != null) Object.Destroy(active);
        active = null;
    }

    internal void Tick(bool ready) {
        // The original Relic Msg FSM fades out and recycles itself. Wait for it
        // before displaying the next kind; a full catch-up never drops awards.
        if (active != null && active.activeSelf) return;
        if (active != null) Object.Destroy(active);
        active = null;
        if (!ready || template == null || order.Count == 0) return;
        var kind = order.Dequeue();
        var count = pending[kind]; pending.Remove(kind);
        active = Object.Instantiate(template);
        active.name = PopupName;
        Object.DontDestroyOnLoad(active);
        var text = Language.Language.Get(names[kind], "UI");
        active.transform.Find("Text").GetComponent<TMPro.TextMeshPro>().text = text + (count > 1 ? " ×" + count : "");
        var icon = active.transform.Find("Icon").GetComponent<SpriteRenderer>();
        icon.sprite = icons[kind]; icon.sortingOrder = 1;
        active.SetActive(true);
    }
}
