using System;
using System.Collections.Generic;
using System.Linq;
using HollowConnect.Core;
using UnityEngine;
using UnityEngine.UI;
using Object = UnityEngine.Object;

namespace HollowConnect;

// No raycaster, selectable controls or input capture: the battle stays playable.
internal sealed class DamageRankingView : IDisposable {
    private static readonly Color Ivory = new(.90f, .89f, .83f, .94f);
    private static readonly Color Muted = new(.70f, .76f, .80f, .85f);
    private readonly GameObject root;
    private readonly RectTransform panel;
    private readonly Text title, context;
    private readonly Font textFont, numeralFont;
    private readonly List<(GameObject Root, Text Name, Text Percent, RectTransform Bar)> rows = new();
    private uint board, revision;
    private ushort local;
    internal DamageRankingView(Transform parent, Font fallback) {
        textFont = Resources.FindObjectsOfTypeAll<Font>().FirstOrDefault(f => f.name == "NotoSerifCJKsc-Regular") ?? fallback;
        numeralFont = Hkmp.Ui.Resources.FontManager.UIFontRegular ?? fallback;
        root = new GameObject("HollowConnect Damage Ranking", typeof(RectTransform), typeof(Canvas), typeof(CanvasScaler), typeof(CanvasGroup));
        root.transform.SetParent(parent, false);
        var canvas = root.GetComponent<Canvas>(); canvas.renderMode = RenderMode.ScreenSpaceOverlay; canvas.sortingOrder = 20;
        var scale = root.GetComponent<CanvasScaler>(); scale.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
        scale.referenceResolution = new Vector2(1920, 1080); scale.matchWidthOrHeight = 1;
        var group = root.GetComponent<CanvasGroup>(); group.interactable = false; group.blocksRaycasts = false;
        panel = Rect("Leaderboard", root.transform, 0, 0, 230, 250);
        panel.anchorMin = panel.anchorMax = new Vector2(1, 1); panel.pivot = new Vector2(1, 1);
        panel.anchoredPosition = new Vector2(-30, -245);
        Fill("Translucent wash", panel, -14, -14, 258, 250, new Color(.025f, .035f, .055f, .16f));
        // Reuse a native menu divider when the game has already loaded it.
        // Keep native assets owned by the game; only our Image is destroyed.
        bool Ornament(string name) => name.IndexOf("fleur", StringComparison.OrdinalIgnoreCase) >= 0 ||
            name.IndexOf("divider", StringComparison.OrdinalIgnoreCase) >= 0 || name.IndexOf("flourish", StringComparison.OrdinalIgnoreCase) >= 0;
        var native = Resources.FindObjectsOfTypeAll<Image>().FirstOrDefault(i => i.sprite != null && (Ornament(i.name) || Ornament(i.sprite.name)))?.sprite
            ?? Resources.FindObjectsOfTypeAll<Sprite>().FirstOrDefault(s => Ornament(s.name));
        if (native != null) {
            var decoration = Fill("Native menu ornament", panel, 0, -3, 230, 24, Ivory);
            decoration.sprite = native; decoration.preserveAspect = true;
            decoration.rectTransform.pivot = new Vector2(.5f, .5f);
            decoration.rectTransform.anchoredPosition = new Vector2(115, -9);
        }
        Fill("Divider left", panel, 0, 8, 91, 1, new Color(.75f, .77f, .74f, .65f));
        Fill("Divider right", panel, 139, 8, 91, 1, new Color(.75f, .77f, .74f, .65f));
        title = Label("Title", panel, 0, 23, 230, 40, 22, TextAnchor.MiddleCenter, textFont, Ivory);
        context = Label("Context", panel, 0, 66, 230, 24, 15, TextAnchor.MiddleCenter, textFont, Muted);
    }
    internal void Show(DamageRankingSnapshot value, ushort localId) {
        root.SetActive(true);
        if (board == value.Board && revision == value.Revision && local == localId) return;
        board = value.Board; revision = value.Revision; local = localId;
        title.text = value.Finished ? value.Completed ? "本轮结算" : "本轮结束" : "伤害占比";
        var sequence = SequenceLabel(value.Sequence);
        context.text = sequence.Length > 0 ? sequence + (value.Finished ? " · " + (value.Completed ? "已完成" : "已结束") : " · 累计")
            : value.Finished ? (value.Completed ? "Boss 已击败" : "战斗已结束") : "Boss 战";
        // Large parties stay bounded: show leaders plus the local player's row.
        var visible = value.Players.Take(8).ToList();
        var own = value.Players.FirstOrDefault(p => p.Id == localId);
        if (own != null && !visible.Contains(own)) visible[visible.Count - 1] = own;
        while (rows.Count < visible.Count) {
            var holder = Rect("Player", panel, 0, 0, 230, 47);
            var name = Label("Name", holder, 0, 0, 170, 29, 18, TextAnchor.MiddleLeft, textFont, Ivory);
            var percent = Label("Percent", holder, 172, 0, 58, 29, 22, TextAnchor.MiddleRight, numeralFont, Ivory);
            Fill("Track", holder, 0, 33, 230, 2, new Color(.45f, .53f, .60f, .20f));
            var bar = Fill("Contribution", holder, 0, 33, 230, 2, Ivory).rectTransform;
            rows.Add((holder.gameObject, name, percent, bar));
        }
        var total = value.Total;
        for (var i = 0; i < rows.Count; i++) {
            var row = rows[i]; row.Root.SetActive(i < visible.Count);
            if (i >= visible.Count) continue;
            var player = visible[i]; var ownRow = player.Id == localId;
            var ratio = total > 0 ? (double)player.Damage / total : 0;
            ((RectTransform)row.Root.transform).anchoredPosition = new Vector2(0, -101 - i * 47);
            var name = player.Name; var suffix = ownRow ? " · 你" : "";
            row.Name.text = name + suffix;
            while (row.Name.preferredWidth > 170 && name.Length > 1) {
                name = name.Substring(0, name.Length - 1);
                if (name.Length > 0 && char.IsHighSurrogate(name[name.Length - 1])) name = name.Substring(0, name.Length - 1);
                row.Name.text = name + "…" + suffix;
            }
            row.Name.color = ownRow ? Ivory : new Color(.80f, .83f, .84f, .9f);
            row.Percent.text = (ratio * 100).ToString("0.0", System.Globalization.CultureInfo.InvariantCulture) + "%";
            row.Bar.sizeDelta = new Vector2((float)(230 * ratio), 2);
            row.Bar.GetComponent<Image>().color = ownRow ? Ivory : new Color(.65f, .74f, .79f, .82f);
        }
        var height = 110 + visible.Count * 47;
        panel.sizeDelta = new Vector2(230, height + 32);
        ((RectTransform)panel.GetChild(0)).sizeDelta = new Vector2(258, height + 52);
        var hint = panel.Find("Shortcut");
        var shortcut = hint != null ? hint.GetComponent<Text>() : Label("Shortcut", panel, 0, height, 230, 20, 15, TextAnchor.MiddleCenter, numeralFont, Muted);
        shortcut.rectTransform.anchoredPosition = new Vector2(0, -height);
        shortcut.text = "F1";
    }
    internal void Hide() { if (root != null) root.SetActive(false); }
    internal static string SequenceLabel(string sequence) {
        if (string.IsNullOrEmpty(sequence)) return "";
        for (var i = 1; i <= 5; i++) if (sequence.EndsWith(i.ToString(), StringComparison.Ordinal)) return new[] { "第一门", "第二门", "第三门", "第四门", "第五门" }[i - 1];
        return "万神殿";
    }
    private static RectTransform Rect(string name, Transform parent, float x, float y, float width, float height) {
        var obj = new GameObject(name, typeof(RectTransform)); obj.transform.SetParent(parent, false);
        var rect = (RectTransform)obj.transform; rect.anchorMin = rect.anchorMax = rect.pivot = new Vector2(0, 1);
        rect.anchoredPosition = new Vector2(x, -y); rect.sizeDelta = new Vector2(width, height); return rect;
    }
    private static Image Fill(string name, Transform parent, float x, float y, float w, float h, Color color) {
        var image = Rect(name, parent, x, y, w, h).gameObject.AddComponent<Image>(); image.color = color; image.raycastTarget = false; return image;
    }
    private static Text Label(string name, Transform parent, float x, float y, float w, float h, int size, TextAnchor align, Font font, Color color) {
        var text = Rect(name, parent, x, y, w, h).gameObject.AddComponent<Text>();
        text.font = font; text.fontSize = size; text.alignment = align; text.color = color;
        text.supportRichText = false; text.raycastTarget = false; text.horizontalOverflow = HorizontalWrapMode.Wrap;
        text.verticalOverflow = VerticalWrapMode.Truncate; return text;
    }
    public void Dispose() { if (root != null) Object.Destroy(root); }
}
