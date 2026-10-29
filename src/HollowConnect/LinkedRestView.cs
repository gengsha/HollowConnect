using System;
using System.Linq;
using HollowConnect.Core;
using UnityEngine;
using UnityEngine.UI;

namespace HollowConnect;

internal sealed class LinkedRestView : IDisposable {
    private readonly GameObject root;
    private readonly Text label;
    private readonly LinkedRestMotion motion;
    internal LinkedRestView(Transform parent) {
        root = new GameObject("HollowConnect Rest Progress", typeof(RectTransform), typeof(Canvas), typeof(CanvasScaler), typeof(CanvasGroup));
        root.transform.SetParent(parent, false);
        var canvas = root.GetComponent<Canvas>(); canvas.renderMode = RenderMode.ScreenSpaceOverlay; canvas.sortingOrder = 20;
        var scale = root.GetComponent<CanvasScaler>(); scale.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
        scale.referenceResolution = new Vector2(1920, 1080); scale.matchWidthOrHeight = 1;
        var group = root.GetComponent<CanvasGroup>(); group.interactable = group.blocksRaycasts = false;
        var obj = new GameObject("Rest progress", typeof(RectTransform), typeof(Text), typeof(Shadow));
        obj.transform.SetParent(root.transform, false);
        label = obj.GetComponent<Text>();
        label.font = Resources.FindObjectsOfTypeAll<Font>().FirstOrDefault(f => f.name == "NotoSerifCJKsc-Regular") ?? Modding.Menu.MenuResources.Perpetua;
        label.fontSize = 32; label.color = new Color(.91f, .90f, .85f, .96f); label.alignment = TextAnchor.MiddleLeft;
        label.raycastTarget = false; label.supportRichText = false;
        var rect = label.rectTransform; rect.anchorMin = rect.anchorMax = new Vector2(0, .60f);
        rect.pivot = new Vector2(0, .5f); rect.sizeDelta = new Vector2(600, 180);
        label.resizeTextForBestFit = true; label.resizeTextMinSize = 24; label.resizeTextMaxSize = 32;
        label.horizontalOverflow = HorizontalWrapMode.Wrap;
        var shadow = obj.GetComponent<Shadow>(); shadow.effectColor = new Color(0, 0, 0, .75f); shadow.effectDistance = new Vector2(1, -2);
        motion = root.AddComponent<LinkedRestMotion>(); motion.Initialize(rect);
        root.SetActive(false);
    }
    internal void Show(LinkedRestCount count, string missing) {
        label.text = count.Label + " " + count.Resting + "/" + count.Total + (string.IsNullOrEmpty(missing) ? "" : "\n等待：" + missing);
        motion.Visible = true; root.SetActive(true);
    }
    internal void Hide() => motion.Visible = false;
    public void Dispose() { if (root != null) UnityEngine.Object.Destroy(root); }
}

// Show/Hide are called every frame. Animate a target so repeated calls and a
// new rest report during exit never restart or snap the notification.
internal sealed class LinkedRestMotion : MonoBehaviour {
    private RectTransform label;
    private float progress;
    internal bool Visible;
    internal void Initialize(RectTransform value) { label = value; Position(); }
    private void Update() {
        progress = Mathf.MoveTowards(progress, Visible ? 1 : 0, Time.unscaledDeltaTime / .35f);
        Position();
        if (!Visible && progress == 0) gameObject.SetActive(false);
    }
    private void Position() {
        var eased = progress * progress * (3 - 2 * progress);
        label.anchoredPosition = new Vector2(Mathf.Lerp(-label.rect.width - 24, 64, eased), 0);
    }
}
