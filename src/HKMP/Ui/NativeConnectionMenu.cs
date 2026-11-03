using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using Modding.Menu;
using Modding.Menu.Config;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace Hkmp.Ui;

// Both transports use the game's menu builder, cursors, sounds and navigation.
internal static class NativeConnectionMenu {
    internal static SingleContentLayout At(float y) => new(new AnchoredPosition(
        new Vector2(.5f, .5f), new Vector2(.5f, .5f), new Vector2(0, y)));

    internal static MenuButton Button(MenuBuilder builder, string name, string label, float y, Action submit, Action back) {
        MenuButton button = null;
        builder.AddContent(At(y), c => c.AddMenuButton(name, new MenuButtonConfig {
            Label = label, SubmitAction = _ => submit(), CancelAction = _ => back()
        }, out button));
        return button;
    }
    internal static Text Text(MenuBuilder builder, string name, string value, float y, float height = 80, int size = 30) {
        Text text = null;
        builder.AddContent(At(y), c => c.AddTextPanel(name, new RelVector2(new Vector2(1200, height)),
            new TextPanelConfig { Text = value, Size = size, Font = TextPanelConfig.TextFont.NotoSerifCJKSCRegular,
                Anchor = TextAnchor.MiddleCenter }, out text));
        text.raycastTarget = false;
        text.supportRichText = false;
        return text;
    }
    internal static InputField Input(MenuBuilder builder, string name, string label, string value, float y,
        int limit, Action back, InputField.OnValidateInput validate = null, Action<string> changed = null, bool concealed = false) {
        InputField input = null;
        var row = Button(builder, name, label, y, () => { input.Select(); input.ActivateInputField(); }, back);
        var caption = row.GetComponentInChildren<Text>(true);
        caption.supportRichText = false;
        caption.alignment = TextAnchor.MiddleLeft;
        caption.fontSize = 36;
        caption.rectTransform.anchorMin = caption.rectTransform.anchorMax = new Vector2(.5f, .5f);
        caption.rectTransform.pivot = new Vector2(.5f, .5f);
        caption.rectTransform.sizeDelta = new Vector2(300, 60);
        caption.rectTransform.anchoredPosition = new Vector2(-260, 0);
        foreach (var nameAndX in new[] { ("CursorLeft", -460f), ("CursorRight", 460f) }) {
            var cursor = caption.transform.Find(nameAndX.Item1);
            if (cursor == null) continue;
            cursor.SetParent(row.transform, false);
            var cursorRect = (RectTransform)cursor;
            cursorRect.anchorMin = cursorRect.anchorMax = new Vector2(.5f, .5f);
            cursorRect.anchoredPosition = new Vector2(nameAndX.Item2, 0);
        }
        var field = new GameObject("Input", typeof(RectTransform), typeof(CanvasRenderer), typeof(InputField));
        field.transform.SetParent(row.transform, false);
        var rect = (RectTransform)field.transform;
        rect.anchorMin = rect.anchorMax = new Vector2(.5f, .5f);
        rect.sizeDelta = new Vector2(470, 60); rect.anchoredPosition = new Vector2(170, 0);
        var textObject = new GameObject("Value", typeof(RectTransform), typeof(CanvasRenderer), typeof(Text));
        textObject.transform.SetParent(field.transform, false);
        var text = textObject.GetComponent<Text>();
        text.font = caption.font; text.color = Color.white; text.supportRichText = false;
        text.alignment = TextAnchor.MiddleRight; text.fontSize = 36;
        text.resizeTextForBestFit = true; text.resizeTextMinSize = 20; text.resizeTextMaxSize = 36;
        text.rectTransform.anchorMin = Vector2.zero; text.rectTransform.anchorMax = Vector2.one;
        text.rectTransform.offsetMin = new Vector2(8, 0); text.rectTransform.offsetMax = new Vector2(-8, 0);
        input = field.GetComponent<InputField>();
        input.textComponent = text; input.targetGraphic = text;
        input.characterLimit = limit; input.lineType = InputField.LineType.SingleLine;
        input.shouldActivateOnSelect = false; input.navigation = new Navigation { mode = Navigation.Mode.None };
        input.onValidateInput = validate; input.text = value ?? "";
        if (changed != null) input.onValueChanged.AddListener(v => changed(v));
        field.AddComponent<NativeConnectionInput>().Initialize(row, input, concealed);
        return input;
    }
    internal static void SetLabel(MenuButton button, string label) {
        var text = button.GetComponentInChildren<Text>(true);
        text.supportRichText = false;
        if (text.text != label) text.text = label;
    }
    internal static void Finish(MenuScreen screen, params MenuButton[] rows) {
        foreach (var text in screen.GetComponentsInChildren<Text>(true)) text.font = MenuResources.NotoSerifCJKSCRegular;
        Relink(screen, rows);
    }
    internal static void Relink(MenuScreen screen, IEnumerable<MenuButton> rows) {
        var active = rows.Where(b => b != null && b.gameObject.activeSelf && b.interactable).ToArray();
        if (active.Length == 0) return;
        for (var i = 0; i < active.Length; i++) active[i].navigation = new Navigation { mode = Navigation.Mode.Explicit,
            selectOnUp = active[(i + active.Length - 1) % active.Length], selectOnDown = active[(i + 1) % active.Length] };
        screen.defaultHighlight = active[0];
        var selected = EventSystem.current?.currentSelectedGameObject;
        if (selected != null && selected.transform.IsChildOf(screen.transform) &&
            (!selected.activeInHierarchy || selected.GetComponent<Selectable>()?.IsInteractable() == false) && screen.gameObject.activeInHierarchy)
            UIManager.instance.StartCoroutine(RestoreSelection(screen, active[0], selected));
    }
    private static IEnumerator RestoreSelection(MenuScreen screen, MenuButton target, GameObject previous) {
        // Newly shown native rows initialize their audio player in Start.
        yield return null;
        if (screen != null && screen.gameObject.activeInHierarchy && target != null && target.IsActive() && target.IsInteractable() &&
            EventSystem.current != null && EventSystem.current.currentSelectedGameObject == previous)
            target.Select();
    }
}

internal sealed class NativeConnectionInput : MonoBehaviour, ISelectHandler, ICancelHandler {
    private MenuButton row;
    private InputField input;
    private bool concealed;
    internal void Initialize(MenuButton owner, InputField field, bool hide) {
        row = owner; input = field; concealed = hide;
        if (concealed) input.inputType = InputField.InputType.Password;
        input.onEndEdit.AddListener(_ => {
            if (concealed) input.inputType = InputField.InputType.Password;
            input.ForceLabelUpdate();
            if (row != null && row.gameObject.activeInHierarchy && EventSystem.current?.currentSelectedGameObject == gameObject) row.Select();
        });
    }
    public void OnSelect(BaseEventData data) {
        if (concealed) { input.inputType = InputField.InputType.Standard; input.ForceLabelUpdate(); }
    }
    public void OnCancel(BaseEventData data) {
        input.DeactivateInputField();
        if (row != null && row.gameObject.activeInHierarchy) row.Select();
        data.Use();
    }
}
