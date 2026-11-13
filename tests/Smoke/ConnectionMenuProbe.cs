using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using GlobalEnums;
using Hkmp.Game;
using Hkmp.Ui;
using HollowConnect.Steam;
using Modding;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;
using Object = UnityEngine.Object;

namespace HollowConnect.Smoke;

// Focused rendered check. Only disposable menu values are edited; no save is loaded.
public sealed class ConnectionMenuProbe : MonoBehaviour {
    private string root, role;
    private UiManager ui;
    private UIManager Native => UIManager.instance;
    private void Log(string value) => File.AppendAllText(Path.Combine(root, role + ".log"), DateTime.UtcNow.ToString("O") + " " + value + "\n");
    private void Check(bool ok, string name) {
        if (!ok) { File.WriteAllText(Path.Combine(root, role + ".failed"), name); throw new Exception(name); }
        Log("PASS " + name);
    }
    private static T Field<T>(object target, string name) => (T)target.GetType().GetField(name, BindingFlags.NonPublic | BindingFlags.Instance).GetValue(target);
    private static void Set(object target, string name, object value) => target.GetType().GetField(name, BindingFlags.NonPublic | BindingFlags.Instance).SetValue(target, value);
    private IEnumerator Until(Func<bool> condition, string name) {
        var deadline = Time.realtimeSinceStartup + 30;
        while (!condition() && Time.realtimeSinceStartup < deadline) yield return null;
        Check(condition(), name);
    }
    private void Submit(GameObject target) => ExecuteEvents.Execute(target, new BaseEventData(EventSystem.current), ExecuteEvents.submitHandler);
    private MenuButton TitleButton(string name) => Native.mainMenuButtons.GetComponentsInChildren<MenuButton>(true).SingleOrDefault(b => b.name == name);
    private bool At(MenuScreen menu) => Native.currentDynamicMenu == menu && Native.menuState == MainMenuState.DYNAMIC_MENU && InputHandler.Instance.acceptingInput;
    private IEnumerator BackToTitle(MenuScreen menu, string name) {
        var back = menu.GetComponentsInChildren<MenuButton>(true).Single(b => b.name == name);
        Submit(back.gameObject);
        yield return Until(() => Native.menuState == MainMenuState.MAIN_MENU && Native.mainMenuScreen.alpha > .99f && InputHandler.Instance.acceptingInput, "native back returns to title");
    }
    private IEnumerator Start() {
        root = Environment.GetEnvironmentVariable("HOLLOWCONNECT_SMOKE_ROOT"); role = Environment.GetEnvironmentVariable("HOLLOWCONNECT_SMOKE_ROLE");
        Log("Connection menu fixture; no player save loaded or written");
        yield return Until(() => ExternalConnectionGateway.AtTitle && Native != null && Native.menuState == MainMenuState.MAIN_MENU && Native.mainMenuScreen.alpha > .99f && InputHandler.Instance.acceptingInput,
            "native title ready");
        yield return new WaitForSecondsRealtime(1);
        var engine = Field<Hkmp.Game.GameManager>(ModHooks.GetMod("HKMP"), "_gameManager");
        ui = Field<UiManager>(engine.ClientManager, "_uiManager");
        if (Environment.GetEnvironmentVariable("HOLLOWCONNECT_LOCALIZATION_CHECK") == "1") {
            global::Language.Language.SwitchLanguage(global::Language.LanguageCode.ZH);
            yield return null;
        }
        var hasSteam = ModHooks.GetMod("HollowConnect Steam") != null;
        var lan = TitleButton("StartMultiplayerButton"); var steamTitle = TitleButton("StartSteamMultiplayerButton");
        Check(lan != null && lan.GetComponentInChildren<Text>(true).text.Trim() == "局域网联机", "LAN title is a native localized menu button");
        Check(hasSteam == (steamTitle != null), "Steam entry follows optional module availability");
        Check(Native.mainMenuButtons.startButton.navigation.selectOnDown == lan && lan.navigation.selectOnUp == Native.mainMenuButtons.startButton,
            "native start and LAN navigation links");
        Check(lan.navigation.selectOnDown == (steamTitle != null ? steamTitle : Native.mainMenuButtons.optionsButton), "LAN navigation reaches next native menu item");
        Capture("title");
        if (steamTitle != null) Check(steamTitle.GetComponentInChildren<Text>(true).text.Trim() == "Steam 联机" && steamTitle.navigation.selectOnDown == Native.mainMenuButtons.optionsButton &&
            Native.mainMenuButtons.optionsButton.navigation.selectOnUp == steamTitle, "Steam has independent title entry and two-way navigation: label=" +
                steamTitle.GetComponentInChildren<Text>(true).text + "; down=" + steamTitle.navigation.selectOnDown?.name + "; options-up=" + Native.mainMenuButtons.optionsButton.navigation.selectOnUp?.name);
        foreach (var entry in new[] {lan, steamTitle}.Where(b => b != null)) {
            var text = entry.GetComponentInChildren<Text>(true);
            var generator = new TextGenerator(); generator.Populate(text.text, text.GetGenerationSettings(text.rectTransform.rect.size));
            Check(text.enabled && text.canvasRenderer.GetInheritedAlpha() > .9f && generator.characterCountVisible >= text.text.Trim().Length,
                "native title entry renders all characters: " + entry.name);
        }
        Submit(lan.gameObject);
        var menu = Field<MenuScreen>(ui, "_connectMenu");
        yield return Until(() => At(menu), "native LAN page opens from title submit");
        Check(!Native.gameTitle.gameObject.activeInHierarchy, "LAN page hides the native main-menu logo");
        Check(menu.GetComponentsInChildren<MenuButton>(true).Length == 6, "LAN inputs and actions use native menu rows");
        var fields = menu.GetComponentsInChildren<InputField>(true);
        if (Environment.GetEnvironmentVariable("HOLLOWCONNECT_LOCALIZATION_CHECK") == "1") yield return CheckLocalization(menu, fields);
        Check(fields.Length == 3 && fields.All(f => f.textComponent.font == Modding.Menu.MenuResources.NotoSerifCJKSCRegular), "all native LAN text inputs support Chinese");
        var name = fields.Single(f => f.transform.parent.name == "Username");
        Submit(name.GetComponentInParent<MenuButton>().gameObject);
        yield return Until(() => name.isFocused, "keyboard submit activates editable player name");
        name.text = "测试玩家1"; name.DeactivateInputField(); yield return null;
        Check(EventSystem.current.currentSelectedGameObject == name.transform.parent.gameObject, "ending text entry restores native row selection");
        var address = fields.Single(f => f.transform.parent.name == "Address");
        var port = fields.Single(f => f.transform.parent.name == "Port");
        // Exercise a complete native edit/end-edit cycle. Returning from a
        // language menu can leave a field selected before it receives focus.
        Submit(address.GetComponentInParent<MenuButton>().gameObject);
        yield return Until(() => address.isFocused, "server address enters native editing");
        address.text = "127.0.0.1"; port.text = "29650";
        address.DeactivateInputField();
        yield return Until(() => !address.isFocused && address.inputType == InputField.InputType.Password,
            "server address is concealed after native end-edit");
        Capture("lan");
        Submit(menu.GetComponentsInChildren<MenuButton>(true).Single(b => b.name == "Host").gameObject);
        yield return Until(() => Native.menuState == MainMenuState.SAVE_PROFILES && InputHandler.Instance.acceptingInput, "LAN host opens native save selection");
        var back = Field<EventTrigger>(ui, "_saveMenuBackTrigger"); Submit(back.gameObject);
        yield return Until(() => At(menu) && !ui.HasSaveSelection, "cancelling LAN save selection returns to LAN page");
        yield return BackToTitle(menu, "BackButton");
        Check(TitleButton("StartMultiplayerButton") == lan && Native.mainMenuButtons.GetComponentsInChildren<MenuButton>(true).Count(b => b.name == lan.name) == 1,
            "returning to title does not duplicate native entries");
        // Keep optional Steam types out of this iterator's generated state machine.
        // The same fixture must load when that assembly is physically absent.
        if (hasSteam) yield return CheckSteamMenu();
        Log("FINAL PASS native connection menus"); File.WriteAllText(Path.Combine(root, role + ".done"), "pass"); GetComponent<RouteFrameRecorder>()?.Finish(); Application.Quit();
    }
    private IEnumerator CheckSteamMenu() {
            var steam = Object.FindObjectOfType<SteamRoomRuntime>();
            Check(steam != null, "optional Steam runtime is available");
            Submit(TitleButton("StartSteamMultiplayerButton").gameObject);
            yield return Until(() => steam.Visible, "independent Steam entry opens native page");
            Check(!Native.gameTitle.gameObject.activeInHierarchy, "Steam page hides the native main-menu logo after quick return");
            var room = Field<MenuScreen>(steam, "roomMenu");
            Check(typeof(SteamRoomRuntime).GetMethod("OnGUI", BindingFlags.NonPublic | BindingFlags.Instance) == null &&
                room.GetComponentsInChildren<MenuButton>(true).Length > 3, "Steam page uses native menu controls without IMGUI");
            Capture("steam");
            // Deterministic display-only rooms cover pagination without sending
            // an invitation, attempting a join or claiming a real friend exists.
            var rooms = Field<List<(ulong Lobby, string Name)>>(steam, "friendRooms");
            rooms.Clear(); for (var i = 0; i < 5; i++) rooms.Add(((ulong)(100 + i), "显示测试好友" + i));
            Set(steam, "showFriends", true); Set(steam, "nextFriendsRefresh", double.MaxValue);
            typeof(SteamRoomRuntime).GetMethod("RefreshRoomMenu", BindingFlags.NonPublic | BindingFlags.Instance).Invoke(steam, null);
            var page = Field<MenuButton>(steam, "pageButton");
            Check(page.gameObject.activeSelf, "friend room list paginates inside native menu");
            yield return null; // Native MenuSelectable.Start binds its audio player on first activation.
            Submit(page.gameObject); yield return null;
            Check(Field<int>(steam, "friendsPage") == 1 && Field<List<MenuButton>>(steam, "friendButtons").Count(b => b.gameObject.activeSelf) == 2,
                "native next page shows remaining friend rooms");
            Capture("steam-friends");
            if (Environment.GetEnvironmentVariable("HOLLOWCONNECT_LOCALIZATION_CHECK") == "1") {
                foreach (var code in new[] { global::Language.LanguageCode.EN, global::Language.LanguageCode.JA, global::Language.LanguageCode.ZH }) {
                    global::Language.Language.SwitchLanguage(code); yield return null; yield return new WaitForEndOfFrame();
                    Check(room.GetComponentsInChildren<Text>(true).Any(t => t.text.Contains("显示测试好友")), "Steam friend names remain opaque " + code);
                    if (code == global::Language.LanguageCode.EN)
                        Check(room.GetComponentsInChildren<Text>().Where(t => !t.text.Contains("显示测试好友")).All(t => !t.text.Any(c => c >= '\u4e00' && c <= '\u9fff')), "English Steam controls contain no untranslated Chinese: " + string.Join(" | ", room.GetComponentsInChildren<Text>().Where(t => !t.text.Contains("显示测试好友") && t.text.Any(c => c >= '\u4e00' && c <= '\u9fff')).Select(t => t.name + "=" + t.text)));
                    Capture("language-" + code + "-steam");
                }
            }
            rooms.Clear(); Set(steam, "showFriends", false); Set(steam, "nextFriendsRefresh", 0d);
            yield return BackToTitle(room, "Steam back");
    }
    private IEnumerator CheckLocalization(MenuScreen lan, InputField[] fields) {
        var settingMenu = CoopMod.Instance.GetMenuScreen(null, null);
        var canonical = CoopMod.Instance.GetMenuData(null);
        var name = fields.Single(f => f.transform.parent.name == "Username");
        var originalName = name.text;
        name.text = "开启关闭テスト";
        foreach (var code in new[] { global::Language.LanguageCode.EN, global::Language.LanguageCode.JA, global::Language.LanguageCode.ZH }) {
            global::Language.Language.SwitchLanguage(code);
            yield return null; yield return null;
            var expected = code == global::Language.LanguageCode.EN ? "Join game" : code == global::Language.LanguageCode.JA ? "ゲームに参加" : "加入游戏";
            var join = lan.GetComponentsInChildren<MenuButton>(true).Single(b => b.name == "Connect").GetComponentInChildren<Text>(true);
            Check(join.text.Trim() == expected, "live native language switch refreshes existing LAN menu: " + code + "=" + join.text);
            Check(name.text == "开启关闭テスト", "language switch preserves opaque input: " + code);
            Capture("language-" + code + "-lan");
            Native.UIGoToDynamicMenu(settingMenu);
            yield return Until(() => At(settingMenu), "localized settings menu opens " + code);
            yield return null; yield return null;
            foreach (var row in settingMenu.GetComponentsInChildren<MenuOptionHorizontal>(true)) {
                var entry = canonical.FirstOrDefault(e => e.Name == row.gameObject.name);
                if (entry.Loader == null) continue;
                Check(row.transform.Find("Label").GetComponent<Text>().text.Trim() == Localize.T(entry.Name), "canonical row identity and translated label: " + code + "/" + row.name + "=" + row.transform.Find("Label").GetComponent<Text>().text);
            }
            if (code == global::Language.LanguageCode.EN) Check(settingMenu.GetComponentsInChildren<Text>(true).Where(t => t.name != "Selected option help").All(t => !t.text.Any(c => c >= '\u4e00' && c <= '\u9fff')), "English settings contain no untranslated Chinese");
            if (code == global::Language.LanguageCode.JA) Check(Localize.Font.HasCharacter('あ') && Localize.Font.HasCharacter('救'), "Japanese menu font has kana and kanji");
            Capture("language-" + code + "-settings");
            Native.UIGoToDynamicMenu(lan);
            yield return Until(() => At(lan), "return to existing LAN menu " + code);
        }
        name.text = originalName;
        Object.Destroy(settingMenu.gameObject);
        // Test save warnings and Steam rows on the same hot-switched language,
        // with raw friend names preserved by template captures.
        foreach (var code in new[] { global::Language.LanguageCode.EN, global::Language.LanguageCode.JA, global::Language.LanguageCode.ZH }) {
            global::Language.Language.SwitchLanguage(code); yield return null; yield return null;
            typeof(UiManager).GetMethod("CreateJoinSaveMenu", BindingFlags.NonPublic | BindingFlags.Instance).Invoke(ui, null);
            var confirm = Field<MenuScreen>(ui, "_joinSaveMenu");
            confirm.gameObject.SetActive(true); yield return null; yield return null;
            Check(Field<Text>(ui, "_joinSaveWarningText").text == Localize.T(UiManager.JoinSaveWarning), "save warning follows language " + code);
            confirm.gameObject.SetActive(false);
        }
        Log("PASS all three live languages, canonical identities, opaque input and save warning");
    }
    private void Capture(string name) {
        Canvas.ForceUpdateCanvases();
        var target = new RenderTexture(1920, 1080, 24); var pixels = new Texture2D(1920, 1080, TextureFormat.RGB24, false);
        var prior = RenderTexture.active;
        try {
            foreach (var camera in Object.FindObjectsOfType<Camera>().Where(c => c.enabled).OrderBy(c => c.depth)) {
                var original = camera.targetTexture; var rect = camera.rect; var projection = camera.projectionMatrix;
                try { camera.targetTexture = target; camera.rect = new Rect(0, 0, 1, 1); camera.projectionMatrix = projection; camera.Render(); }
                finally { camera.targetTexture = original; camera.rect = rect; camera.projectionMatrix = projection; }
            }
            RenderTexture.active = target; pixels.ReadPixels(new Rect(0, 0, 1920, 1080), 0, 0); pixels.Apply();
            File.WriteAllBytes(Path.Combine(root, role + "-" + name + ".png"), pixels.EncodeToPNG());
        } finally { RenderTexture.active = prior; Object.Destroy(pixels); Object.Destroy(target); }
    }
}
