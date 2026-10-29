using System;
using System.Globalization;
using UnityEngine;
using UnityEngine.UI;

namespace HollowConnect;

internal sealed class LinkedHealthBonusRow : MonoBehaviour {
    internal const string OptionName = "额外面具（房主）";
    private InputField input;
    private Text description, label;
    private string help;
    private bool? locked;
    private int revision;
    internal static int Normalize(int value) => Mathf.Clamp(value, 0, 10);
    internal static string Format(int value) => Normalize(value).ToString(CultureInfo.InvariantCulture);
    internal static bool TrySave(string text) {
        if (CoopMod.RoomSettingsLocked || !int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var masks) || masks < 0 || masks > 10) return false;
        CoopMod.Settings.LinkedHealthBonusMasks = masks;
        return true;
    }
    internal void Initialize(MenuOptionHorizontal option, MenuScreen screen) {
        description = transform.Find("Description")?.GetComponent<Text>();
        label = transform.Find("Label")?.GetComponent<Text>();
        help = description?.text;
        var target = new GameObject("Linked health mask input", typeof(RectTransform), typeof(CanvasRenderer), typeof(Image), typeof(InputField));
        target.transform.SetParent(transform, false);
        var rect = (RectTransform)target.transform;
        rect.anchorMin = new Vector2(.71f, 0); rect.anchorMax = Vector2.one; rect.offsetMin = rect.offsetMax = Vector2.zero;
        var background = target.GetComponent<Image>(); background.color = new Color(1, 1, 1, .08f);
        var textObject = new GameObject("Text", typeof(RectTransform), typeof(CanvasRenderer), typeof(Text));
        textObject.transform.SetParent(target.transform, false);
        var text = textObject.GetComponent<Text>(); text.font = option.optionText.font; text.fontSize = 36;
        text.alignment = TextAnchor.MiddleCenter; text.color = Color.white; text.raycastTarget = false;
        text.rectTransform.anchorMin = Vector2.zero; text.rectTransform.anchorMax = Vector2.one;
        text.rectTransform.offsetMin = new Vector2(8, 0); text.rectTransform.offsetMax = new Vector2(-8, 0);
        input = target.GetComponent<InputField>(); input.targetGraphic = background; input.textComponent = text;
        input.characterValidation = InputField.CharacterValidation.Integer; input.characterLimit = 2;
        input.shouldActivateOnSelect = false; input.navigation = option.navigation;
        input.onEndEdit.AddListener(value => { TrySave(value); Refresh(); });
        foreach (var selectable in screen.GetComponentsInChildren<Selectable>(true)) {
            if (selectable == input) continue;
            var nav = selectable.navigation;
            if (nav.selectOnUp == option) nav.selectOnUp = input;
            if (nav.selectOnDown == option) nav.selectOnDown = input;
            if (nav.selectOnLeft == option) nav.selectOnLeft = input;
            if (nav.selectOnRight == option) nav.selectOnRight = input;
            selectable.navigation = nav;
        }
        option.SetOptionList(new[] { "" }); option.optionText.enabled = false; option.enabled = false;
        Refresh();
    }
    private void OnEnable() { if (input != null) Refresh(); }
    private void Update() {
        if (input != null && (locked != CoopMod.RoomSettingsLocked || revision != CoopMod.RoomRuleRevision)) Refresh();
    }
    private void Refresh() {
        locked = CoopMod.RoomSettingsLocked; revision = CoopMod.RoomRuleRevision;
        input.readOnly = locked.Value;
        input.textComponent.color = locked.Value ? Color.gray : Color.white;
        if (label != null) label.color = locked.Value ? Color.gray : Color.white;
        if (description != null) description.text = locked.Value ? help : help.Replace(CoopMod.RoomSettingLockHint + "\n", "");
        input.SetTextWithoutNotify(locked.Value ? CoopMod.ActiveRoomSettingLabel?.Invoke(OptionName) ?? Format(CoopMod.Settings.LinkedHealthBonusMasks) : Format(CoopMod.Settings.LinkedHealthBonusMasks));
    }
}
