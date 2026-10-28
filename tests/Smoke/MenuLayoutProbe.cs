using System;
using System.Collections;
using System.IO;
using System.Linq;
using Hkmp;
using HollowConnect;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;
using Object = UnityEngine.Object;

namespace HollowConnect.Smoke;

// Focused menu geometry/selection check; uses no network or character save.
public sealed class MenuLayoutProbe : MonoBehaviour {
    private string root, role;
    private void Mark(string value) => File.AppendAllText(Path.Combine(root, role + ".log"), value + "\n");
    private void Check(bool value, string label) {
        if (!value) { File.WriteAllText(Path.Combine(root, role + ".failed"), label); throw new Exception(label); }
        Mark("PASS " + label);
    }
    private IEnumerator Start() {
        root = Environment.GetEnvironmentVariable("HOLLOWCONNECT_SMOKE_ROOT");
        role = Environment.GetEnvironmentVariable("HOLLOWCONNECT_SMOKE_ROLE") ?? "host";
        Mark("Menu layout only; save/global-settings writes intercepted");
        var deadline = Time.realtimeSinceStartup + 60;
        while ((GameManager.instance == null || UIManager.instance == null || CoopMod.Instance == null ||
                UnityEngine.SceneManagement.SceneManager.GetActiveScene().name != "Menu_Title" ||
                UIManager.instance.menuState != GlobalEnums.MainMenuState.MAIN_MENU || UIManager.instance.mainMenuScreen.alpha < .99f) && Time.realtimeSinceStartup < deadline)
            yield return null;
        Check(UIManager.instance != null && EventSystem.current != null, "native menu and event system ready");
        // Startup's own ShowMainMenuHome must finish before navigating away;
        // otherwise it can repaint the title over this directly opened fixture.
        yield return new WaitForSecondsRealtime(1);
        // Dynamic mod menus are normally reached through Options. Its native
        // transition owns the main-menu title fade; GoToDynamicMenu alone
        // only hides a MenuScreen and cannot replace this entry path.
        UIManager.instance.UIGoToOptionsMenu();
        deadline = Time.realtimeSinceStartup + 20;
        while ((UIManager.instance.menuState != GlobalEnums.MainMenuState.OPTIONS_MENU ||
                UIManager.instance.mainMenuScreen.alpha > .01f || UIManager.instance.gameTitle.color.a > .01f) &&
                Time.realtimeSinceStartup < deadline) yield return null;
        Check(UIManager.instance.menuState == GlobalEnums.MainMenuState.OPTIONS_MENU &&
            UIManager.instance.mainMenuScreen.alpha <= .01f && UIManager.instance.gameTitle.color.a <= .01f,
            "native Options transition hides main menu and title");
        var priorLock = CoopContext.RoomSettingsLocked;
        var menu = CoopMod.Instance.GetMenuScreen(null, null);
        var panel = menu.GetComponent<CoopMenuHelpPanel>();
        var scroll = menu.GetComponentInChildren<ScrollRect>(true);
        UIManager.instance.UIGoToDynamicMenu(menu);
        deadline = Time.realtimeSinceStartup + 20;
        while (UIManager.instance.currentDynamicMenu != menu && Time.realtimeSinceStartup < deadline) yield return null;
        Check(UIManager.instance.currentDynamicMenu == menu && UIManager.instance.mainMenuScreen.alpha <= .01f,
            "native dynamic menu navigation completes before capture");
        var options = menu.GetComponentsInChildren<MenuOptionHorizontal>(true);
        try {
            foreach (var locked in new[] { false, true }) {
                CoopContext.RoomSettingsLocked = () => locked;
                foreach (var row in menu.GetComponentsInChildren<CoopMenuLockRow>(true)) row.Refresh();
                foreach (var option in options) {
                    var parameterInput = option.GetComponentInChildren<InputField>(true);
                    EventSystem.current.SetSelectedGameObject(parameterInput != null ? parameterInput.gameObject : option.gameObject);
                    yield return null;
                    panel.Refresh(); Canvas.ForceUpdateCanvases();
                    var inline = option.transform.Find("Description").GetComponent<Text>();
                    Check(panel.Help.text == inline.text && !inline.enabled,
                        (locked ? "locked " : "unlocked ") + "single shared caption: " + option.name);
                    Check(!panel.Help.raycastTarget && !Overlaps(panel.Help.rectTransform, scroll.viewport),
                        "help outside option viewport: " + option.name);
                    Fits(panel.Help, "complete description " + option.name);
                    var label = option.transform.Find("Label").GetComponent<Text>();
                    Check(!Overlaps(label.rectTransform, option.optionText.rectTransform), "label/value columns separated: " + option.name);
                    Fits(label, "label " + option.name);
                    Fits(parameterInput != null ? parameterInput.textComponent : option.optionText, "value " + option.name);
                    if (parameterInput != null) {
                        Check(parameterInput.readOnly == locked, "typed health field follows room lock");
                        if (!locked) {
                            parameterInput.text = "80"; parameterInput.onEndEdit.Invoke("80");
                            Check(parameterInput.text == "80" && Math.Abs(CoopMod.Settings.BossExtraHealthPerPlayer - .8f) < .00001,
                                "native input saves typed health percent");
                        }
                    }
                    if (locked && CoopMod.IsRoomSetting(option.name))
                        Check(panel.Help.text.Contains("断开联机") && option.optionList.Length == 1, "lock reason and fixed value preserved: " + option.name);
                }
            }
            // Rapid pointer/keyboard-style selection changes must replace, not
            // accumulate, the captions (including native animation callbacks).
            for (var i = 0; i < 24; i++) {
                var option = options[i % options.Length];
                ExecuteEvents.Execute(option.gameObject, new PointerEventData(EventSystem.current), ExecuteEvents.pointerEnterHandler);
                EventSystem.current.SetSelectedGameObject(option.gameObject); panel.Refresh();
                Check(panel.Help.text == option.transform.Find("Description").GetComponent<Text>().text,
                    "rapid selection caption " + i);
            }
            EventSystem.current.SetSelectedGameObject(menu.GetComponentInChildren<MenuButton>().gameObject);
            panel.Refresh(); Check(panel.Help.text == "", "back button clears option caption");
            var parameterRow = options.Single(o => o.name == BossHealthParameterRow.OptionName);
            scroll.verticalNormalizedPosition = .5f;
            EventSystem.current.SetSelectedGameObject(parameterRow.GetComponentInChildren<InputField>(true).gameObject);
            panel.Refresh(); Canvas.ForceUpdateCanvases(); yield return null;
            Capture();
            Check(menu.GetComponentsInChildren<MenuOptionHorizontal>(true).All(o => !o.transform.Find("Description").GetComponent<Text>().enabled),
                "all native inline descriptions remain hidden after rapid navigation");
            menu.gameObject.SetActive(false); Check(panel.Help.text == "", "closing menu clears help");
            Mark("FINAL PASS menu selection geometry and typed health input");
            File.WriteAllText(Path.Combine(root, role + ".done"), "Menu selection and geometry passed, no gameplay suite");
        } finally { CoopContext.RoomSettingsLocked = priorLock; Object.Destroy(menu.gameObject); }
        Application.Quit();
    }
    private static bool Overlaps(RectTransform a, RectTransform b) {
        Rect Bounds(RectTransform value) {
            var corners = new Vector3[4]; value.GetWorldCorners(corners);
            return Rect.MinMaxRect(corners.Min(p => p.x), corners.Min(p => p.y), corners.Max(p => p.x), corners.Max(p => p.y));
        }
        return Bounds(a).Overlaps(Bounds(b));
    }
    private void Fits(Text text, string label) {
        var settings = text.GetGenerationSettings(text.rectTransform.rect.size);
        var generator = new TextGenerator(); generator.Populate(text.text, settings);
        Check(generator.characterCountVisible >= text.text.Length, label + " fits without truncation (" + generator.characterCountVisible + "/" + text.text.Length + ")");
    }
    private void Capture() {
        var target = new RenderTexture(1920, 1080, 24);
        var pixels = new Texture2D(1920, 1080, TextureFormat.RGB24, false);
        var prior = RenderTexture.active;
        try {
            foreach (var camera in Object.FindObjectsOfType<Camera>().Where(c => c.enabled).OrderBy(c => c.depth)) {
                var original = camera.targetTexture; var projection = camera.projectionMatrix; var rect = camera.rect;
                try { camera.targetTexture = target; camera.rect = new Rect(0, 0, 1, 1); camera.projectionMatrix = projection; camera.Render(); }
                finally { camera.targetTexture = original; camera.rect = rect; camera.projectionMatrix = projection; }
            }
            RenderTexture.active = target; pixels.ReadPixels(new Rect(0, 0, 1920, 1080), 0, 0); pixels.Apply();
            File.WriteAllBytes(Path.Combine(root, role + "-menu-layout.png"), pixels.EncodeToPNG());
        } finally { RenderTexture.active = prior; Object.Destroy(pixels); Object.Destroy(target); }
    }
}
