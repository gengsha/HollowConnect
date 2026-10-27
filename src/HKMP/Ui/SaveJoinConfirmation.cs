using System;
using System.Collections;
using Modding.Menu;
using Modding.Menu.Config;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace Hkmp.Ui;

internal partial class UiManager {
    internal const string JoinSaveWarning = "共享进度可能永久改变存档";
    internal const string JoinSaveRoomWarning = "请确保你选择的存档不会覆盖房间原有进度";
    internal static string CurrentJoinSaveWarning => CoopContext.LocalWorldOnly
        ? JoinSaveWarning : JoinSaveWarning + "\n\n" + JoinSaveRoomWarning;
    private bool _confirmJoinSave, _joinSaveApproved, _joinMenuBusy;
    private int _saveSelectionVersion;
    private Action _joinSaveContinuation;
    private MenuScreen _joinSaveMenu;
    private MenuButton _joinSaveCancel;
    private Text _joinSaveWarningText;
    private EventTrigger _saveMenuBackTrigger;
    private Coroutine _saveMenuTransition;

    // Stop before loading an existing slot or starting a new one. In
    // particular, cancelling must not run LoadGame or its mod-data callbacks.
    private void OnLoadJoinSave(On.GameManager.orig_LoadGameFromUI orig, GameManager self, int slot) {
        if (!_confirmJoinSave) { orig(self, slot); return; }
        ConfirmJoinSave(() => orig(self, slot));
    }

    private void OnStartJoinSave(On.UIManager.orig_StartNewGame orig, UIManager self, bool permaDeath, bool bossRush) {
        if (!_confirmJoinSave) { orig(self, permaDeath, bossRush); return; }
        ConfirmJoinSave(() => orig(self, permaDeath, bossRush));
    }

    private void EnterSelectedSave(Action enter) {
        if (_confirmJoinSave && !_joinSaveApproved) {
            ConfirmJoinSave(() => EnterSelectedSave(enter));
            return;
        }
        enter();
        FinishSaveSlotSelection(true);
    }

    private void ConfirmJoinSave(Action continuation) {
        if (_joinSaveContinuation != null || _joinMenuBusy) return;
        _joinSaveApproved = false;
        _joinSaveContinuation = continuation;
        CreateJoinSaveMenu();
        _joinSaveWarningText.text = CurrentJoinSaveWarning;
        _joinMenuBusy = true;
        _saveMenuTransition = UM.StartCoroutine(ShowJoinSaveMenu(_saveSelectionVersion));
    }

    private void CreateJoinSaveMenu() {
        if (_joinSaveMenu != null) return;
        var builder = MenuUtils.CreateMenuBuilder("确认加入联机");
        SingleContentLayout At(float y) => new(new AnchoredPosition(new Vector2(.5f, .5f),
            new Vector2(.5f, .5f), new Vector2(0, y)));
        builder.AddContent(At(170), c => {
            c.AddTextPanel("Progress warning", new RelVector2(new Vector2(1700, 340)),
                new TextPanelConfig { Text = JoinSaveWarning, Size = 54,
                    Font = TextPanelConfig.TextFont.NotoSerifCJKSCRegular, Anchor = TextAnchor.MiddleCenter }, out _joinSaveWarningText);
            _joinSaveWarningText.raycastTarget = false;
        });
        builder.AddContent(At(-100), c => c.AddMenuButton("Cancel join", new MenuButtonConfig {
            Label = "取消", SubmitAction = _ => ResolveJoinSave(false), CancelAction = _ => ResolveJoinSave(false)
        }, out _joinSaveCancel));
        builder.AddContent(At(-220), c => c.AddMenuButton("Confirm join", new MenuButtonConfig {
            Label = "确认加入", SubmitAction = _ => ResolveJoinSave(true), CancelAction = _ => ResolveJoinSave(false), Proceed = true
        }));
        _joinSaveMenu = builder.Build();
        _joinSaveMenu.defaultHighlight = _joinSaveCancel;
        // The native menu's CJK font keeps the warning and both actions legible
        // regardless of the game's selected language.
        foreach (var text in _joinSaveMenu.GetComponentsInChildren<Text>(true)) {
            text.font = MenuResources.NotoSerifCJKSCRegular;
        }
    }

    private IEnumerator ShowJoinSaveMenu(int version) {
        IH.StopUIInput();
        if (UM.menuState == GlobalEnums.MainMenuState.SAVE_PROFILES)
            yield return UM.HideSaveProfileMenu();
        else yield return UM.HideCurrentMenu();
        if (version != _saveSelectionVersion) yield break;
        UM.currentDynamicMenu = _joinSaveMenu;
        UM.menuState = GlobalEnums.MainMenuState.DYNAMIC_MENU;
        yield return UM.ShowMenu(_joinSaveMenu);
        if (version != _saveSelectionVersion) yield break;
        _joinSaveMenu.HighlightDefault();
        _joinMenuBusy = false;
        _saveMenuTransition = null;
    }

    private void ResolveJoinSave(bool confirmed) {
        if (_joinMenuBusy || _joinSaveContinuation == null) return;
        _joinMenuBusy = true;
        var continuation = _joinSaveContinuation;
        _joinSaveContinuation = null;
        _saveMenuTransition = UM.StartCoroutine(FinishJoinSave(confirmed, continuation, _saveSelectionVersion));
    }

    private IEnumerator FinishJoinSave(bool confirmed, Action continuation, int version) {
        if (confirmed) {
            IH.StopUIInput();
            yield return UM.HideCurrentMenu();
            if (version != _saveSelectionVersion || !_netClient.IsConnected) yield break;
            _joinSaveApproved = true;
            _joinMenuBusy = false;
            _saveMenuTransition = null;
            continuation();
        } else {
            // Keep the selection hooks and the live connection. No completed-
            // selection callback runs until a slot is actually accepted.
            _joinSaveApproved = false;
            yield return GoToSaveMenu();
            if (version == _saveSelectionVersion) _joinMenuBusy = false;
        }
    }

    private void FinishSaveSlotSelection(bool selected) {
        var callback = _saveSlotSelectedAction;
        CloseSaveSlotSelection();
        callback?.Invoke(selected);
    }

    private void RestoreSaveMenuBackButton() {
        if (_saveMenuBackTrigger != null && _originalBackTriggers != null)
            _saveMenuBackTrigger.triggers = _originalBackTriggers;
        _saveMenuBackTrigger = null;
        _originalBackTriggers = null;
    }

    private void CloseSaveSlotSelection() {
        _saveSelectionVersion++;
        if (_saveMenuTransition != null) UM.StopCoroutine(_saveMenuTransition);
        _saveMenuTransition = null;
        _saveSlotSelectedAction = null;
        _joinSaveContinuation = null;
        _confirmJoinSave = _joinSaveApproved = _joinMenuBusy = false;
        On.GameManager.StartNewGame -= OnStartNewGame;
        On.GameManager.ContinueGame -= OnContinueGame;
        On.GameManager.LoadGameFromUI -= OnLoadJoinSave;
        On.UIManager.StartNewGame -= OnStartJoinSave;
        RestoreSaveMenuBackButton();
    }

    private IEnumerator ReturnFromInterruptedJoinSelection() {
        IH.StopUIInput();
        yield return UM.HideCurrentMenu();
        yield return GoToMultiplayerMenu();
    }
}
