using System;
using System.Linq;
using HollowConnect.Core;
using UnityEngine;
using UnityEngine.UI;

namespace HollowConnect;

internal sealed class LinkedRestView : IDisposable {
    private readonly GameObject root;
    private readonly Text label;
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
        label.fontSize = 32; label.color = new Color(.91f, .90f, .85f, .96f); label.alignment = TextAnchor.MiddleCenter;
        label.raycastTarget = false; label.supportRichText = false;
        var rect = label.rectTransform; rect.anchorMin = rect.anchorMax = new Vector2(.5f, 0);
        rect.pivot = new Vector2(.5f, 0); rect.anchoredPosition = new Vector2(0, 145); rect.sizeDelta = new Vector2(1000, 180);
        label.resizeTextForBestFit = true; label.resizeTextMinSize = 18; label.resizeTextMaxSize = 32;
        var shadow = obj.GetComponent<Shadow>(); shadow.effectColor = new Color(0, 0, 0, .75f); shadow.effectDistance = new Vector2(1, -2);
    }
    internal void Show(LinkedRestCount count, string missing) {
        label.text = count.Label + " " + count.Resting + "/" + count.Total + (string.IsNullOrEmpty(missing) ? "" : "\n等待：" + missing);
        root.SetActive(true);
    }
    internal void Hide() => root.SetActive(false);
    public void Dispose() { if (root != null) UnityEngine.Object.Destroy(root); }
}
