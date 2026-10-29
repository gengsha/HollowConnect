using System;
using System.Collections.Generic;
using System.Linq;
using Hkmp;
using Modding;
using Modding.Menu;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace HollowConnect;

public sealed partial class CoopMod {
    internal const string RoomSettingLockHint = "联机中已锁定。请先断开联机；房主还需停止开房，再返回本模组配置修改。";
    internal const string SharedHealthOptionName = "共享血量（房主）";
    internal static bool RoomSettingsLocked => CoopContext.RoomSettingsLocked?.Invoke() == true;
    internal static Func<string, string> ActiveRoomSettingLabel;
    internal static int RoomRuleRevision;
    internal static bool IsRoomSetting(string name) => name.EndsWith("房主）", StringComparison.Ordinal);

    private static List<IMenuMod.MenuEntry> GuardRoomSettings(List<IMenuMod.MenuEntry> entries) => entries.ConvertAll(entry => {
        if (!IsRoomSetting(entry.Name)) return entry;
        var save = entry.Saver;
        entry.Saver = index => { if (!RoomSettingsLocked) save(index); };
        entry.Description = RoomSettingLockHint + "\n" + entry.Description;
        return entry;
    });

    public MenuScreen GetMenuScreen(MenuScreen modListMenu, ModToggleDelegates? toggleDelegates) {
        var entries = GetMenuData(null);
        var screen = MenuUtils.CreateMenuScreen("HollowConnect", entries, modListMenu);
        Hkmp.Menu.MenuPointerNavigation.Attach(screen);
        foreach (var option in screen.GetComponentsInChildren<MenuOptionHorizontal>(true)) {
            var entry = entries.FirstOrDefault(e => e.Name == option.gameObject.name);
            if (entry.Name == BossHealthParameterRow.OptionName) option.gameObject.AddComponent<BossHealthParameterRow>().Initialize(option, screen);
            else if (entry.Name == LinkedHealthBonusRow.OptionName) option.gameObject.AddComponent<LinkedHealthBonusRow>().Initialize(option, screen);
            else if (entry.Loader != null && IsRoomSetting(entry.Name)) option.gameObject.AddComponent<CoopMenuLockRow>().Initialize(option, entry);
        }
        screen.gameObject.AddComponent<CoopMenuHelpPanel>().Initialize(screen);
        GroupSharedHealth(screen);
        return screen;
    }

    private static void GroupSharedHealth(MenuScreen screen) {
        var options = screen.GetComponentsInChildren<MenuOptionHorizontal>(true);
        var mode = (RectTransform)options.Single(o => o.name == SharedHealthOptionName).transform;
        var bonus = (RectTransform)options.Single(o => o.name == LinkedHealthBonusRow.OptionName).transform;
        const float gap = 52;
        var scroll = screen.GetComponentInChildren<ScrollRect>(true);
        scroll.content.sizeDelta += new Vector2(0, gap);
        mode.anchoredPosition -= new Vector2(0, gap);
        bonus.anchoredPosition -= new Vector2(0, gap);
        var line = new GameObject("Shared health divider", typeof(RectTransform), typeof(CanvasRenderer), typeof(Image));
        line.transform.SetParent(mode.parent, false);
        var rect = (RectTransform)line.transform;
        rect.anchorMin = mode.anchorMin; rect.anchorMax = mode.anchorMax; rect.pivot = mode.pivot;
        rect.anchoredPosition = mode.anchoredPosition + new Vector2(0, 82);
        rect.sizeDelta = new Vector2(mode.sizeDelta.x, 1.5f);
        var image = line.GetComponent<Image>(); image.color = new Color(.8f, .8f, .8f, .35f); image.raycastTarget = false;
        // Indent the child setting without changing its identity, navigation,
        // lock handling or the common value column.
        var label = bonus.Find("Label").GetComponent<Text>();
        label.rectTransform.offsetMin = new Vector2(64, 0);
        label.fontSize = 36; label.resizeTextMaxSize = 36;
    }
}

// The API's inline descriptions overflow the fixed-height rows, and their
// show/hide animations overlap during navigation. Give all rows one reserved
// help area outside the scrolling viewport instead.
internal sealed class CoopMenuHelpPanel : MonoBehaviour {
    private readonly Dictionary<MenuOptionHorizontal, Text> descriptions = new();
    private Text help;
    internal Text Help => help;

    internal void Initialize(MenuScreen screen) {
        const float helpHeight = 260, gap = 35, bottom = 14;
        var content = (RectTransform)screen.content.transform;
        var scroll = content.GetComponentInChildren<ScrollRect>(true);
        var viewport = scroll.viewport;
        viewport.offsetMin = new Vector2(viewport.offsetMin.x, helpHeight + gap + bottom);
        var bar = scroll.verticalScrollbar;
        var barRect = (RectTransform)bar.transform;
        barRect.sizeDelta = new Vector2(barRect.sizeDelta.x, content.rect.height - helpHeight - gap - bottom);
        var track = (RectTransform)bar.transform.Find("Background");
        track.anchorMin = new Vector2(.5f, 0); track.anchorMax = new Vector2(.5f, 1);
        track.sizeDelta = new Vector2(track.sizeDelta.x, 0); track.anchoredPosition = Vector2.zero;

        var panel = new GameObject("Selected option help", typeof(RectTransform), typeof(CanvasRenderer), typeof(Text));
        panel.transform.SetParent(content, false);
        var rect = (RectTransform)panel.transform;
        rect.anchorMin = rect.anchorMax = rect.pivot = new Vector2(.5f, 0);
        rect.anchoredPosition = new Vector2(0, bottom); rect.sizeDelta = new Vector2(1240, helpHeight);
        help = panel.GetComponent<Text>();
        help.font = Resources.FindObjectsOfTypeAll<Font>().FirstOrDefault(f => f.name == "NotoSerifCJKsc-Regular") ?? Modding.Menu.MenuResources.Perpetua;
        help.fontSize = 34; help.resizeTextForBestFit = true; help.resizeTextMinSize = 26; help.resizeTextMaxSize = 34;
        help.alignment = TextAnchor.UpperLeft; help.color = new Color(.85f, .86f, .87f, 1);
        help.supportRichText = false; help.raycastTarget = false;
        help.horizontalOverflow = HorizontalWrapMode.Wrap; help.verticalOverflow = VerticalWrapMode.Truncate;

        foreach (var option in screen.GetComponentsInChildren<MenuOptionHorizontal>(true)) {
            var description = option.transform.Find("Description")?.GetComponent<Text>();
            if (description != null) {
                descriptions[option] = description;
                // Leave the animator object alive for native selection callbacks.
                description.enabled = false; description.raycastTarget = false;
            }
            FitColumn(option.transform.Find("Label")?.GetComponent<Text>(), 0, .68f);
            FitColumn(option.optionText, .71f, 1);
        }
    }
    private static void FitColumn(Text text, float left, float right) {
        if (text == null) return;
        var rect = text.rectTransform;
        rect.anchorMin = new Vector2(left, 0); rect.anchorMax = new Vector2(right, 1);
        rect.offsetMin = rect.offsetMax = Vector2.zero;
        text.resizeTextForBestFit = true; text.resizeTextMinSize = 28; text.resizeTextMaxSize = text.fontSize;
        text.horizontalOverflow = HorizontalWrapMode.Wrap; text.verticalOverflow = VerticalWrapMode.Truncate;
    }
    private void LateUpdate() => Refresh();
    internal void Refresh() {
        if (help == null) return;
        var selected = EventSystem.current?.currentSelectedGameObject;
        var row = selected != null ? selected.GetComponentInParent<MenuOptionHorizontal>() : null;
        var value = row != null && descriptions.TryGetValue(row, out var description) ? description.text : "";
        if (help.text != value) help.text = value;
    }
    private void OnDisable() { if (help != null) help.text = ""; }
}

// Keep locked rows focusable so keyboard/controller users can read the reason.
// A single displayed value prevents both arrow and mouse changes. The guarded
// saver independently protects callers holding an entry built before connecting.
internal sealed class CoopMenuLockRow : MonoBehaviour {
    private MenuOptionHorizontal option;
    private IMenuMod.MenuEntry entry;
    private Text label, description;
    private Color normalLabel, normalValue;
    private bool? locked;
    private int ruleRevision;
    internal void Initialize(MenuOptionHorizontal value, IMenuMod.MenuEntry data) {
        option = value; entry = data;
        label = transform.Find("Label")?.GetComponent<Text>();
        description = transform.Find("Description")?.GetComponent<Text>();
        if (label != null) normalLabel = label.color;
        normalValue = option.optionText.color;
        Refresh();
    }
    private void OnEnable() => Refresh();
    private void Update() { if (option != null && (locked != CoopMod.RoomSettingsLocked || ruleRevision != CoopMod.RoomRuleRevision)) Refresh(); }
    internal void Refresh() {
        if (option == null) return;
        var isLocked = CoopMod.RoomSettingsLocked;
        locked = isLocked;
        ruleRevision = CoopMod.RoomRuleRevision;
        var index = Math.Max(0, Math.Min(entry.Values.Length - 1, entry.Loader()));
        var roomValue = CoopMod.ActiveRoomSettingLabel?.Invoke(entry.Name) ?? entry.Values[index];
        option.SetOptionList(isLocked ? new[] { roomValue + "（已锁定）" } : entry.Values);
        option.SetOptionTo(isLocked ? 0 : index);
        option.menuSetting.customApplySetting = (_, i) => {
            if (CoopMod.RoomSettingsLocked) { Refresh(); return; }
            entry.Saver(i);
        };
        option.menuSetting.customRefreshSetting = (setting, _) => Refresh();
        if (label != null) {
            label.text = entry.Name;
            label.color = isLocked ? new Color(.65f, .65f, .65f, normalLabel.a) : normalLabel;
        }
        option.optionText.color = isLocked ? new Color(.65f, .65f, .65f, normalValue.a) : normalValue;
        if (description != null) description.text = isLocked ? entry.Description : entry.Description.Replace(CoopMod.RoomSettingLockHint + "\n", "");
    }
}
