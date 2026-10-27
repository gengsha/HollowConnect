using System;
using System.Collections;
using System.Linq;
using System.IO;
using System.Reflection;
using Hkmp.Ui;
using Modding.Menu;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;
using Scenes = UnityEngine.SceneManagement.SceneManager;

namespace HollowConnect.Smoke;

public sealed partial class JoinProbe {
    private UiManager JoinUi => Field<UiManager>(engine.ClientManager, "_uiManager");
    private MenuScreen JoinMenu => Field<MenuScreen>(JoinUi, "_joinSaveMenu");
    private bool JoinPromptReady => JoinMenu != null && JoinMenu.gameObject.activeInHierarchy &&
        UIManager.instance.currentDynamicMenu == JoinMenu && UIManager.instance.menuState == GlobalEnums.MainMenuState.DYNAMIC_MENU &&
        !Field<bool>(JoinUi, "_joinMenuBusy");

    private void JoinCheck(bool ok, string label) {
        if (!ok) { Mark("FAIL " + label); Flag(role + ".failed"); throw new Exception(label); }
        Mark("PASS " + label);
    }

    private IEnumerator CheckSaveJoinCancellation(string prefix) {
        yield return Until(() => UIManager.instance.menuState == GlobalEnums.MainMenuState.SAVE_PROFILES,
            prefix + " native save selection ready");
        if (Has(role + ".failed")) yield break;
        var pd = PlayerData.instance;
        var statistics = CoopMod.Statistics;
        var session = runtime.Session;
        var loads = 0;
        On.GameManager.hook_LoadGame loading = (orig, gm, slot, callback) => {
            loads++;
            throw new Exception("Cancelled selection tried to read a save");
        };
        On.GameManager.LoadGame += loading;
        try {
            // No real file is needed: the warning must run before slot 99 can
            // reach LoadGame. Both keyboard submit and Back retain transport.
            GameManager.instance.LoadGameFromUI(99);
            yield return CheckAndCancelJoinPrompt(prefix + " existing slot", false);
            if (Has(role + ".failed")) yield break;
            foreach (var mode in new[] { 0, 1, 2 }) {
                UIManager.instance.StartNewGame(mode == 1, mode == 2);
                yield return CheckAndCancelJoinPrompt(prefix + " new mode " + mode, true);
                if (Has(role + ".failed")) yield break;
            }
            JoinCheck(loads == 0 && ReferenceEquals(pd, PlayerData.instance) && ReferenceEquals(statistics, CoopMod.Statistics),
                prefix + " cancellations never load or replace game/mod save data");
            JoinCheck(runtime.Connected && runtime.Session == session,
                prefix + " all cancellations retain the same connection and room session");
        } finally { On.GameManager.LoadGame -= loading; }
    }

    private IEnumerator CheckAndCancelJoinPrompt(string label, bool back) {
        yield return Until(() => JoinPromptReady, label + " warning displayed");
        if (Has(role + ".failed")) yield break;
        var cancel = JoinMenu.GetComponentsInChildren<MenuButton>(true).Single(b => b.name == "Cancel join");
        JoinCheck(JoinMenu.defaultHighlight == cancel && EventSystem.current.currentSelectedGameObject == cancel.gameObject,
            label + " cancel is selected by default");
        var warning = JoinMenu.GetComponentsInChildren<Text>(true).Single(t => t.name == "Progress warning");
        JoinCheck(warning.text.StartsWith(UiManager.JoinSaveWarning) &&
            warning.text.Contains(UiManager.JoinSaveRoomWarning) == runtime.Shared &&
            !warning.text.Contains("前提") && !warning.text.Contains("!"),
            label + " room-overwrite warning follows the host sharing rule without explanation or exclamation marks");
        var generator = new TextGenerator();
        generator.Populate(warning.text, warning.GetGenerationSettings(warning.rectTransform.rect.size));
        JoinCheck(warning.font == MenuResources.NotoSerifCJKSCRegular && warning.fontSize >= 50 &&
            generator.characterCountVisible >= warning.text.Length, label + " prominent native CJK warning fits");
        JoinCheck(Scenes.GetActiveScene().name == "Menu_Title" && HeroController.instance == null,
            label + " gameplay has not started");
        JoinCheck(UIManager.instance.saveProfileScreen.alpha < .01f &&
            UIManager.instance.saveProfileTitle.alpha < .01f && UIManager.instance.saveProfileControls.alpha < .01f,
            label + " native save profiles are fully hidden behind confirmation");
        if (host && label == "initial existing slot") {
            yield return new WaitForSecondsRealtime(.5f);
            CaptureJoinMenu();
        }
        if (back) ExecuteEvents.Execute(cancel.gameObject, new BaseEventData(EventSystem.current), ExecuteEvents.cancelHandler);
        else ExecuteEvents.Execute(cancel.gameObject, new BaseEventData(EventSystem.current), ExecuteEvents.submitHandler);
        yield return Until(() => UIManager.instance.menuState == GlobalEnums.MainMenuState.SAVE_PROFILES &&
            !Field<bool>(JoinUi, "_joinMenuBusy"), label + " cancellation returns to native save selection");
        JoinCheck(runtime.Connected, label + " cancellation keeps connection");
    }

    private IEnumerator AcceptSaveJoin(string label) {
        yield return Until(() => JoinPromptReady, label + " selected save requires confirmation");
        if (Has(role + ".failed")) yield break;
        var confirm = JoinMenu.GetComponentsInChildren<MenuButton>(true).Single(b => b.name == "Confirm join");
        ExecuteEvents.Execute(confirm.gameObject, new BaseEventData(EventSystem.current), ExecuteEvents.submitHandler);
    }

    private IEnumerator CheckDisconnectDuringJoinPrompt() {
        GameManager.instance.LoadGameFromUI(99);
        yield return Until(() => JoinPromptReady, "disconnect boundary starts at pending warning");
        if (Has(role + ".failed")) yield break;
        engine.ClientManager.Disconnect();
        yield return Until(() => !runtime.Connected && UIManager.instance.currentDynamicMenu ==
            Field<MenuScreen>(JoinUi, "_connectMenu") && InputHandler.Instance.acceptingInput,
            "disconnect at pending warning restores operable connection menu");
        if (Has(role + ".failed")) yield break;
        JoinCheck(Field<Action>(JoinUi, "_joinSaveContinuation") == null,
            "disconnect discards unconfirmed save continuation");
        Connect();
        yield return Until(() => runtime.Connected && UIManager.instance.menuState == GlobalEnums.MainMenuState.SAVE_PROFILES,
            "reconnect after pending warning returns to native save selection");
    }

    private void CaptureJoinMenu() {
        var target = new RenderTexture(1920, 1080, 24);
        var pixels = new Texture2D(1920, 1080, TextureFormat.RGB24, false);
        var prior = RenderTexture.active;
        try {
            foreach (var camera in UnityEngine.Object.FindObjectsOfType<Camera>().Where(c => c.enabled).OrderBy(c => c.depth)) {
                var original = camera.targetTexture; var projection = camera.projectionMatrix; var rect = camera.rect;
                try { camera.targetTexture = target; camera.rect = new Rect(0, 0, 1, 1); camera.projectionMatrix = projection; camera.Render(); }
                finally { camera.targetTexture = original; camera.rect = rect; camera.projectionMatrix = projection; }
            }
            RenderTexture.active = target; pixels.ReadPixels(new Rect(0, 0, 1920, 1080), 0, 0); pixels.Apply();
            File.WriteAllBytes(Path.Combine(root, "join-save-confirmation.png"), pixels.EncodeToPNG());
        } finally { RenderTexture.active = prior; UnityEngine.Object.Destroy(pixels); UnityEngine.Object.Destroy(target); }
    }
}
