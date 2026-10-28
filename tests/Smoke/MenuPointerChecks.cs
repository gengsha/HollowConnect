using System;
using System.Collections;
using System.Linq;
using System.Reflection;
using Hkmp.Menu;
using Modding;
using Modding.Menu;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;
using Object = UnityEngine.Object;

namespace HollowConnect.Smoke;

public sealed partial class RescueProbe {
    private bool MenuPointerCheck => Environment.GetEnvironmentVariable("HOLLOWCONNECT_MENU_POINTER_CHECK") == "1";

    private void CheckMenuPointerMenus() {
        ShowPointerMenu(flexibleMenu);
        var entries = CoopMod.Instance.GetMenuData(null);
        foreach (var option in flexibleMenu.GetComponentsInChildren<MenuOptionHorizontal>(true)) {
            var entry = entries.Single(e => e.Name == option.name);
            if (option.name == HollowConnect.BossHealthParameterRow.OptionName) {
                var input = option.GetComponentInChildren<InputField>(true);
                Check(input != null && !input.readOnly, "typed health parameter has editable native input before opening a room");
                continue;
            }
            CheckPointerRow(option, entry.Loader, "HollowConnect " + option.name);
        }
        flexibleMenu.gameObject.SetActive(false);

        var menu = Field<ModMenu>(ModHooks.GetMod("HKMP"), "_modMenu");
        if (Field<MenuScreen>(menu, "_clientSettingsMenu") == null) menu.CreateMenu(null);
        var client = Field<MenuScreen>(menu, "_clientSettingsMenu");
        ShowPointerMenu(client);
        var skinEnabled = Field<bool>(menu, "_skinHorizontalOptionEnabled");
        typeof(ModMenu).GetMethod("ModifySkinHorizontalOption", BindingFlags.Instance | BindingFlags.NonPublic)
            .Invoke(menu, new object[] { true });
        var skin = Field<MenuOptionHorizontal>(menu, "_skinHorizontalOption");
        CheckPointerRow(skin, () => skin.selectedOptionIndex, "HKMP skin");
        typeof(ModMenu).GetMethod("ModifySkinHorizontalOption", BindingFlags.Instance | BindingFlags.NonPublic)
            .Invoke(menu, new object[] { skinEnabled });
        var ping = client.GetComponentsInChildren<MenuOptionHorizontal>(true).Single(o => o.name == "Ping Display");
        var settings = Field<Hkmp.Game.Settings.ModSettings>(engine.ClientManager, "_modSettings");
        CheckPointerRow(ping, () => settings.DisplayPing ? 1 : 0, "HKMP ping");
        client.gameObject.SetActive(false);

        var server = Field<MenuScreen>(menu, "_serverSettingsMenu");
        ShowPointerMenu(server);
        var damage = server.GetComponentsInChildren<MenuOptionHorizontal>(true).Single(o => o.name == "NailDamage");
        CheckPointerRow(damage, () => Field<Hkmp.Game.Settings.ServerSettings>(menu, "_localServerSettings").NailDamage,
            "HKMP server nail damage");
        server.gameObject.SetActive(false);

        // A separate API menu represents another mod. Observe native dispatch,
        // rather than merely checking whether our marker is absent.
        var otherValue = 1;
        var other = MenuUtils.CreateMenuScreen("Unrelated probe menu", new System.Collections.Generic.List<IMenuMod.MenuEntry> {
            new("Unrelated option", new[] { "0", "1", "2" }, "scope check", i => otherValue = i, () => otherValue)
        }, null);
        ShowPointerMenu(other);
        var unrelated = other.GetComponentsInChildren<MenuOptionHorizontal>(true).Single();
        var nativeCalls = 0;
        On.UnityEngine.UI.MenuOptionHorizontal.hook_PointerClickCheckArrows spy = (orig, option, pointer) => {
            if (option == unrelated) nativeCalls++;
            orig(option, pointer);
        };
        On.UnityEngine.UI.MenuOptionHorizontal.PointerClickCheckArrows += spy;
        try {
            ClickMenuHalf(unrelated, true);
            Check(nativeCalls == 1 && unrelated.selectedOptionIndex == otherValue,
                "unrelated CustomSetting menu retains native pointer dispatch and saver");
        } finally {
            On.UnityEngine.UI.MenuOptionHorizontal.PointerClickCheckArrows -= spy;
            Object.Destroy(other.gameObject);
        }
    }

    private void ShowPointerMenu(MenuScreen menu) {
        menu.gameObject.SetActive(true);
        menu.transform.localScale = Vector3.one;
        Canvas.ForceUpdateCanvases();
    }

    private void CheckPointerRow(MenuOptionHorizontal option, Func<int> read, string label) {
        var initial = read();
        var count = option.optionList.Length;
        option.SetOptionTo(0);
        ClickMenuHalf(option, true);
        Check(option.selectedOptionIndex == count - 1 && read() == count - 1, label + " left click wraps backward and saves");
        ClickMenuHalf(option, false);
        Check(option.selectedOptionIndex == 0 && read() == 0, label + " right click wraps forward and saves");
        option.interactable = false;
        ClickMenuHalf(option, false);
        Check(option.selectedOptionIndex == 0 && read() == 0, label + " disabled row ignores pointer changes");
        option.interactable = true;
        option.OnMove(new AxisEventData(EventSystem.current) { moveDir = MoveDirection.Right });
        Check(option.selectedOptionIndex == 1 && read() == 1, label + " keyboard navigation still saves");
        option.menuSetting.customApplySetting(option.menuSetting, initial);
        option.SetOptionTo(initial);
    }

    private void ClickMenuHalf(MenuOptionHorizontal option, bool left) {
        var rect = (RectTransform)option.transform;
        var canvas = option.GetComponentInParent<Canvas>();
        var camera = canvas != null && canvas.renderMode != RenderMode.ScreenSpaceOverlay ? canvas.worldCamera : null;
        var point = RectTransformUtility.WorldToScreenPoint(camera, rect.TransformPoint(new Vector3(
            left ? rect.rect.xMin + 5 : rect.rect.xMax - 5, rect.rect.center.y)));
        option.OnPointerClick(new PointerEventData(EventSystem.current) {
            position = point,
            pointerPressRaycast = new RaycastResult { module = canvas == null ? null : canvas.GetComponent<GraphicRaycaster>() }
        });
    }

    private IEnumerator MenuPointerLockChecks() {
        yield return FlexibleMenuLockChecks();
        ShowPointerMenu(flexibleMenu);
        var before = Newtonsoft.Json.JsonConvert.SerializeObject(CoopMod.Settings);
        foreach (var row in flexibleMenu.GetComponentsInChildren<CoopMenuLockRow>(true)) {
            var option = row.GetComponent<MenuOptionHorizontal>();
            ClickMenuHalf(option, true); ClickMenuHalf(option, false);
            Check(option.selectedOptionIndex == 0 && option.optionList.Length == 1 && option.optionText.text.Contains("已锁定"),
                "both mouse halves preserve connected room lock: " + option.name);
        }
        Check(Newtonsoft.Json.JsonConvert.SerializeObject(CoopMod.Settings) == before, "connected pointer clicks preserve every locked setting");
        var opacity = flexibleMenu.GetComponentsInChildren<MenuOptionHorizontal>(true).Single(o => o.name == "队友透明度（本机）");
        CheckPointerRow(opacity, () => CoopMod.Instance.GetMenuData(null).Single(e => e.Name == opacity.name).Loader(), "connected local opacity");
        flexibleMenu.gameObject.SetActive(false);
    }
}
