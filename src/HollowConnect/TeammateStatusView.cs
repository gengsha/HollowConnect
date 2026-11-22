using System;
using System.Collections.Generic;
using System.Globalization;
using UnityEngine;
using UnityEngine.UI;
using Object = UnityEngine.Object;

namespace HollowConnect;

internal readonly struct TeammateStatusRow : IEquatable<TeammateStatusRow> {
    internal readonly string Name;
    internal readonly int White, Blue;
    internal readonly int? Rtt;
    internal readonly bool Own, Down, Offline;

    internal TeammateStatusRow(string name, int white, int blue, int? rtt, bool own, bool down, bool offline) {
        Name = name; White = white; Blue = blue; Rtt = rtt;
        Own = own; Down = down; Offline = offline;
    }

    public bool Equals(TeammateStatusRow other) => Name == other.Name && White == other.White && Blue == other.Blue &&
        Rtt == other.Rtt && Own == other.Own && Down == other.Down && Offline == other.Offline;
}

internal sealed class TeammateStatusView : IDisposable {
    internal const int MaxRows = 8;
    private const float Width = 330, Right = 28, Top = 25, RowHeight = 29, RowGap = 3;
    private const float MaskSize = 19, MaskStep = 18, MaskLine = 20, PrimaryColumnHeight = 126, OverflowColumnHeight = 580;
    private const int MasksPerLine = 10;
    private static readonly Color Ivory = new(.94f, .91f, .85f, 1f);
    private static readonly Color Muted = new(.76f, .76f, .73f, 1f);
    private static readonly Color Accent = new(.66f, .82f, .96f, .92f);
    private static readonly Color PingBlue = new Color32(103, 141, 184, 255);
    private static readonly Color DownText = new Color32(221, 168, 152, 255);
    private static readonly Color DownLine = new Color32(166, 114, 103, 160);
    private readonly GameObject root;
    private readonly RectTransform panel;
    private readonly List<Row> rows = new();
    private tk2dSpriteDefinition whiteMask, blueMask;
    private string language;

    private sealed class Row {
        internal RectTransform Rect;
        internal CanvasGroup Group;
        internal Image Marker;
        internal RectTransform DownBadge;
        internal Text Name, Ping, DownLabel;
        internal readonly List<NativeMaskGraphic> Masks = new();
        internal TeammateStatusRow Value;
    }

    internal TeammateStatusView(Transform parent) {
        root = new GameObject("HollowConnect Teammate Status", typeof(RectTransform), typeof(Canvas), typeof(CanvasScaler));
        root.transform.SetParent(parent, false);
        var canvas = root.GetComponent<Canvas>();
        canvas.renderMode = RenderMode.ScreenSpaceOverlay;
        canvas.sortingOrder = 20;
        var scale = root.GetComponent<CanvasScaler>();
        scale.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
        scale.referenceResolution = new Vector2(1280, 720);
        scale.screenMatchMode = CanvasScaler.ScreenMatchMode.Expand;
        panel = Rect("Party", root.transform, 0, 0, Width, PrimaryColumnHeight);
        panel.anchorMin = panel.anchorMax = panel.pivot = Vector2.one;
        panel.anchoredPosition = new Vector2(-Right, -Top);
    }

    internal void Show(IReadOnlyList<TeammateStatusRow> players) {
        if (whiteMask?.material == null || blueMask?.material == null) {
            var nativeHud = NativeMaskGraphic.FindHudAnimator();
            whiteMask = NativeMaskGraphic.ReadFrame(nativeHud, "Health Idle");
            blueMask = NativeMaskGraphic.ReadFrame(nativeHud, "Blue Idle");
            if (whiteMask == null || blueMask == null) { Hide(); return; }
        }
        root.SetActive(true);
        var font = Hkmp.Ui.Localize.Font;
        var currentLanguage = Hkmp.Ui.Localize.Language;
        float y = 0;
        int column = 0;
        for (var i = 0; i < players.Count; i++) {
            var value = players[i];
            int count = value.Down ? 0 : value.White + value.Blue;
            float height = RowHeight + MaskLine * (Math.Max(1, (count + MasksPerLine - 1) / MasksPerLine) - 1);
            var columnHeight = column == 0 ? PrimaryColumnHeight : OverflowColumnHeight;
            if (y + height > columnHeight) { column++; y = 0; }
            bool isNew = rows.Count <= i;
            if (isNew) rows.Add(CreateRow(font));
            var row = rows[i];
            row.Rect.gameObject.SetActive(true);
            row.Rect.anchoredPosition = new Vector2(-column * (Width + 10), -y);
            row.Rect.sizeDelta = new Vector2(Width, height);
            y += height + RowGap;
            if (isNew || row.Name.font != font || row.Value.Name != value.Name) {
                row.Name.font = row.Ping.font = row.DownLabel.font = font;
                SetName(row.Name, value.Name);
            }
            if (isNew || language != currentLanguage || !row.Value.Equals(value)) Apply(row, value, count);
        }
        language = currentLanguage;
        for (var i = players.Count; i < rows.Count; i++) rows[i].Rect.gameObject.SetActive(false);
    }

    private Row CreateRow(Font font) {
        var rect = Rect("Teammate", panel, 0, 0, Width, RowHeight);
        var row = new Row { Rect = rect, Group = rect.gameObject.AddComponent<CanvasGroup>() };
        row.Group.blocksRaycasts = false;
        var background = rect.gameObject.AddComponent<Image>();
        background.color = new Color(0, 0, 0, .18f);
        background.raycastTarget = false;
        var marker = Rect("Local player marker", rect, 0, 7, 2, 15).gameObject.AddComponent<Image>();
        marker.color = Accent;
        marker.raycastTarget = false;
        row.Marker = marker;
        row.Name = Label("Name", rect, 10, 0, 76, 17, TextAnchor.MiddleLeft, font);
        row.Ping = Label("Ping", rect, 87, 0, 49, 14, TextAnchor.MiddleRight, font);
        row.DownBadge = Rect("Down status", rect, Width - 110, 0, 102, RowHeight);
        row.DownLabel = Label("Label", row.DownBadge, 16, 0, 70, 15, TextAnchor.MiddleCenter, font);
        row.DownLabel.color = DownText;
        foreach (var x in new[] { 0f, 90f }) {
            var line = Rect("Rule", row.DownBadge, x, (RowHeight - 1) * .5f, 12, 1).gameObject.AddComponent<Image>();
            line.color = DownLine;
            line.raycastTarget = false;
        }
        return row;
    }

    private void Apply(Row row, TeammateStatusRow value, int count) {
        row.Value = value;
        row.Marker.gameObject.SetActive(value.Own);
        row.Group.alpha = value.Offline ? .42f : 1;
        row.Name.color = value.Down ? Muted : Ivory;
        var ping = value.Offline ? null : value.Rtt;
        row.Ping.text = ping.HasValue ? Math.Min(9999, ping.Value).ToString(CultureInfo.InvariantCulture) + " ms" : "—";
        row.Ping.color = ping.HasValue ? PingBlue : Muted;
        var showDown = value.Down && !value.Offline;
        row.DownBadge.gameObject.SetActive(showDown);
        if (showDown) row.DownLabel.text = Hkmp.Ui.Localize.T("暗影");
        while (row.Masks.Count < count) {
            var icon = Rect("Mask", row.Rect, 0, 0, MaskSize, MaskSize).gameObject.AddComponent<NativeMaskGraphic>();
            icon.raycastTarget = false;
            row.Masks.Add(icon);
        }
        for (var i = 0; i < row.Masks.Count; i++) {
            var icon = row.Masks[i];
            icon.gameObject.SetActive(i < count);
            if (i >= count) continue;
            icon.SetFrame(i < value.White ? whiteMask : blueMask);
            int index = i % MasksPerLine;
            int line = i / MasksPerLine;
            int onLine = Math.Min(MasksPerLine, count - line * MasksPerLine);
            icon.rectTransform.anchoredPosition = new Vector2(Width - 8 - (onLine - index - 1) * MaskStep - MaskSize,
                -(RowHeight - MaskSize) * .5f - line * MaskLine);
        }
    }

    private static void SetName(Text label, string name) {
        label.text = name;
        var width = label.rectTransform.rect.width;
        if (label.preferredWidth <= width) return;
        var elements = StringInfo.ParseCombiningCharacters(name);
        for (var i = elements.Length - 1; i >= 0; i--) {
            label.text = name.Substring(0, elements[i]) + "…";
            if (label.preferredWidth <= width) break;
        }
    }

    private static RectTransform Rect(string name, Transform parent, float x, float y, float width, float height) {
        var obj = new GameObject(name, typeof(RectTransform));
        obj.transform.SetParent(parent, false);
        var rect = (RectTransform)obj.transform;
        rect.anchorMin = rect.anchorMax = rect.pivot = new Vector2(0, 1);
        rect.anchoredPosition = new Vector2(x, -y);
        rect.sizeDelta = new Vector2(width, height);
        return rect;
    }

    private static Text Label(string name, Transform parent, float x, float y, float width, int size, TextAnchor align, Font font) {
        var rect = Rect(name, parent, x, y, width, RowHeight);
        var text = rect.gameObject.AddComponent<Text>();
        text.font = font;
        text.fontSize = size;
        text.alignment = align;
        text.color = Ivory;
        text.supportRichText = false;
        text.raycastTarget = false;
        text.horizontalOverflow = HorizontalWrapMode.Overflow;
        text.verticalOverflow = VerticalWrapMode.Overflow;
        var shadow = rect.gameObject.AddComponent<Shadow>();
        shadow.effectColor = new Color(0, 0, 0, .8f);
        shadow.effectDistance = new Vector2(1, -1);
        return text;
    }

    internal void Hide() => root.SetActive(false);
    public void Dispose() {
        Hide();
        Object.Destroy(root);
    }
}
