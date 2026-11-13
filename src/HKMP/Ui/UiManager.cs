using System;
using System.Collections;
using System.Collections.Generic;
using GlobalEnums;
using Hkmp.Api.Client;
using Hkmp.Game.Settings;
using Hkmp.Networking.Client;
using Hkmp.Ui.Chat;
using Hkmp.Util;
using Modding;
using Modding.Menu;
using Modding.Menu.Config;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;
using Logger = Hkmp.Logging.Logger;
using Object = UnityEngine.Object;

namespace Hkmp.Ui;

/// <inheritdoc />
internal partial class UiManager : IUiManager {
    #region Internal UI manager variables and properties

    /// <summary>
    /// The font size of header text.
    /// </summary>
    public const int HeaderFontSize = 34;

    /// <summary>
    /// The font size of normal text.
    /// </summary>
    public const int NormalFontSize = 24;

    /// <summary>
    /// The font size of the chat text.
    /// </summary>
    public const int ChatFontSize = 22;

    /// <summary>
    /// The font size of sub text.
    /// </summary>
    public const int SubTextFontSize = 22;
    
    /// <summary>
    /// The address to connect to the local device.
    /// </summary>
    private const string LocalhostAddress = "127.0.0.1";

    /// <summary>
    /// Expression for the GameManager instance.
    /// </summary>
    // ReSharper disable once InconsistentNaming
    private static GameManager GM => GameManager.instance;
    
    /// <summary>
    /// Expression for the UIManager instance.
    /// </summary>
    // ReSharper disable once InconsistentNaming
    private static UIManager UM => UIManager.instance;
    
    /// <summary>
    /// Expression for the InputHandler instance.
    /// </summary>
    // ReSharper disable once InconsistentNaming
    private static InputHandler IH => InputHandler.Instance;

    /// <summary>
    /// The global GameObject in which all UI is created.
    /// </summary>
    internal static GameObject UiGameObject;

    /// <summary>
    /// The chat box instance.
    /// </summary>
    internal static ChatBox InternalChatBox;
    
    /// <summary>
    /// Event that is fired when a server is requested to be hosted from the UI.
    /// </summary>
    public event Action<int> RequestServerStartHostEvent;
    internal event Action RequestLoopbackHostEvent;
    internal Func<bool> ExternalConnectionReserved;

    /// <summary>
    /// Event that is fired when a server is requested to be stopped.
    /// </summary>
    public event Action RequestServerStopHostEvent;

    /// <summary>
    /// Event that is fired when a connection is requested with the given username, IP, port and whether it was a
    /// connection from hosting.
    /// </summary>
    public event Action<string, int, string, bool> RequestClientConnectEvent;

    /// <summary>
    /// Event that is fired when a disconnect is requested.
    /// </summary>
    public event Action RequestClientDisconnectEvent;

    /// <summary>
    /// The mod settings for HKMP.
    /// </summary>
    private readonly ModSettings _modSettings;

    /// <summary>
    /// The net client to check if we are connected to a server or not.
    /// </summary>
    private readonly NetClient _netClient;

    /// <summary>
    /// The connect interface.
    /// </summary>
    private ConnectInterface _connectInterface;

    /// <summary>
    /// The menu screen for the connection UI.
    /// </summary>
    private MenuScreen _connectMenu;
    
    /// <summary>
    /// The ping interface.
    /// </summary>
    private PingInterface _pingInterface;
    
    /// <summary>
    /// Optional transport menu and the origin of native save selection.
    /// </summary>
    private MenuScreen _externalMenu;
    private Action _openExternalMenu;
    private bool _returnToExternalMenu, _connectionMenuBusy;

    /// <summary>
    /// List of event trigger entries for the original back triggers for the save selection screen. These triggers are
    /// stored when they are override to get back to our connection menu.
    /// </summary>
    private List<EventTrigger.Entry> _originalBackTriggers;

    /// <summary>
    /// Callback action to execute when save slot selection is finished. The boolean parameter indicates whether a
    /// save slot was selected (true) or the menu was exited through the back button (false). 
    /// </summary>
    private Action<bool> _saveSlotSelectedAction;

    #endregion

    #region IUiManager properties

    /// <inheritdoc />
    public IChatBox ChatBox => InternalChatBox;

    #endregion

    public UiManager(ModSettings modSettings, NetClient netClient, Func<bool> isHosting) {
        _modSettings = modSettings;
        _netClient = netClient;
        _isHosting = isHosting;
    }
    
    /// <summary>
    /// Initialize the UI manager by creating UI and register various hooks.
    /// </summary>
    public void Initialize() {
        // First we create a gameObject that will hold all other objects of the UI
        UiGameObject = new GameObject();

        // Create event system object
        var eventSystemObj = new GameObject("EventSystem");

        var eventSystem = eventSystemObj.AddComponent<EventSystem>();
        eventSystem.sendNavigationEvents = true;
        eventSystem.pixelDragThreshold = 10;

        eventSystemObj.AddComponent<StandaloneInputModule>();

        Object.DontDestroyOnLoad(eventSystemObj);

        // Make sure that our UI is an overlay on the screen
        UiGameObject.AddComponent<Canvas>().renderMode = RenderMode.ScreenSpaceOverlay;

        // Also scale the UI with the screen size
        var canvasScaler = UiGameObject.AddComponent<CanvasScaler>();
        canvasScaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
        canvasScaler.referenceResolution = new Vector2(1920f, 1080f);
        canvasScaler.screenMatchMode = CanvasScaler.ScreenMatchMode.Expand;

        UiGameObject.AddComponent<GraphicRaycaster>();

        Object.DontDestroyOnLoad(UiGameObject);

        var uiGroup = new ComponentGroup();

        _connectInterface = new ConnectInterface(_modSettings);

        var inGameGroup = new ComponentGroup(parent: uiGroup);

        var infoBoxGroup = new ComponentGroup(parent: inGameGroup);

        InternalChatBox = new ChatBox(infoBoxGroup, _modSettings);

        var pingGroup = new ComponentGroup(parent: inGameGroup);

        _pingInterface = new PingInterface(
            pingGroup,
            _modSettings,
            _netClient
        );

        // Register callbacks to make sure the UI is hidden and shown at correct times
        On.UIManager.SetState += (orig, self, state) => {
            orig(self, state);

            if (state == UIState.PAUSED) {
                inGameGroup.SetActive(false);
            } else {
                // Only show chat box UI in gameplay scenes
                if (!SceneUtil.IsNonGameplayScene(SceneUtil.GetCurrentSceneName())) {
                    inGameGroup.SetActive(true);
                }
            }
        };
        UnityEngine.SceneManagement.SceneManager.activeSceneChanged += (_, newScene) => {
            var isNonGamePlayScene = SceneUtil.IsNonGameplayScene(newScene.name);
            
            eventSystem.enabled = !isNonGamePlayScene;
            inGameGroup.SetActive(!isNonGamePlayScene);
        };

        ModHooks.LanguageGetHook += (key, sheet, orig) => {
            if (key == "StartMultiplayerBtn" && sheet == "MainMenu") {
                return "局域网联机";
            }
            
            if (key == "StartSteamMultiplayerBtn" && sheet == "MainMenu") return "Steam 联机";

            if (key == "MODAL_PROGRESS" && sheet == "MainMenu" && _netClient.IsConnected) {
                return "You will be disconnected";
            }

            return orig;
        };
        
        On.UIManager.UIGoToMainMenu += (orig, self) => {
            orig(self);

            TryAddMultiplayerScreen();
        };
        
        TryAddMultiplayerScreen();

        _connectInterface.StartHostButtonPressed += (username, port) => {
            _returnToExternalMenu = false;
            if (ExternalConnectionReserved?.Invoke() == true) return;
            OpenSaveSlotSelection(saveSelected => {
                if (!saveSelected) {
                    return;
                }

                RequestServerStartHostEvent?.Invoke(port);
                RequestClientConnectEvent?.Invoke(LocalhostAddress, port, username, true);
            });
        };

        _connectInterface.ConnectButtonPressed += (address, port, username) => {
            _returnToExternalMenu = false;
            if (ExternalConnectionReserved?.Invoke() == true) return;
            RequestClientConnectEvent?.Invoke(address, port, username, false);
        };

        On.UIManager.ReturnToMainMenu += (orig, self) => {
            RequestClientDisconnectEvent?.Invoke();
            RequestServerStopHostEvent?.Invoke();
            
            return orig(self);
        };

        InitializeEndings();
    }

    internal void ConnectExternal(int port, string username, bool hosting) {
        _returnToExternalMenu = true;
        RequestClientConnectEvent?.Invoke(LocalhostAddress, port, username, hosting);
    }

    internal void StartExternalHost() => RequestLoopbackHostEvent?.Invoke();
    internal void StopExternalHost() => RequestServerStopHostEvent?.Invoke();
    internal void DisconnectExternal() => RequestClientDisconnectEvent?.Invoke();
    internal void CancelExternalSelection(Action<bool> callback) {
        if (_saveSlotSelectedAction == callback) CloseSaveSlotSelection();
    }
    internal bool HasSaveSelection => _saveSlotSelectedAction != null;
    internal void SetExternalReservationCheck(Func<bool> reserved) => _connectInterface.ConnectionReserved = reserved;
    internal void AddExternalMenuEntry(Action open) {
        _openExternalMenu = open;
        if (UM != null && UM.mainMenuButtons != null) TryAddMultiplayerScreen();
    }
    internal void SetExternalMenu(MenuScreen menu) => _externalMenu = menu;
    internal void OpenExternalSaveSlotSelection(Action<bool> callback) {
        _returnToExternalMenu = true;
        OpenSaveSlotSelection(callback);
    }
    internal void ShowExternalMenu(MenuScreen menu) {
        _externalMenu = menu; _returnToExternalMenu = true;
        UM.StartCoroutine(GoToConnectionMenu(menu));
    }

    /// <summary>
    /// Enter the game with the current PlayerData from the multiplayer menu. This assumes that the PlayerData
    /// instance is populated with values already.
    /// </summary>
    public void EnterGameFromMultiplayerMenu(bool newGame) {
        IH.StopUIInput();


        UM.uiAudioPlayer.PlayStartGame();
        if (MenuStyles.Instance) {
            MenuStyles.Instance.StopAudio();
        }

        if (newGame) {
            Logger.Debug("Entering game from MP menu for new game");
            GM.StartCoroutine(GM.RunStartNewGame());
        } else {
            Logger.Debug("Entering game from MP menu for a continued game");
            GM.ContinueGame();
        }
    }
    
    /// <summary>
    /// Return to the main menu from in-game. Used whenever the player disconnects from the current server.
    /// </summary>
    private bool _returningToMenu;
    public void ReturnToMainMenuFromGame() {
        // A server shutdown must leave the local ending/credits in control.
        if (GM != null && (SceneUtil.IsEndingScene(GM.sceneName) || SceneUtil.IsEndingScene(SceneUtil.GetCurrentSceneName()))) return;
        // Connecting from the title/slot menu has no loaded local save. Never
        // save its temporary PlayerData over a previously selected profile.
        if (_returningToMenu || GM == null || GM.gameState == GameState.MAIN_MENU || GM.gameState == GameState.INACTIVE) return;
        _returningToMenu = true;
        GM.StartCoroutine(ReturnToMenuWithSave());
    }

    private IEnumerator ReturnToMenuWithSave() {
        var gm = GM;
        var pd = PlayerData.instance;
        var slot = gm.profileID;
        var deadline = Time.realtimeSinceStartup + 30f;
        try {
            // Disconnect cleanup has already restored local boss context and
            // released rescue locks. Let an accepted native scene load finish
            // before SaveLevelState samples that scene's persistent objects.
            while (gm.IsInSceneTransition && Time.realtimeSinceStartup < deadline) yield return null;
            if (gm != GM || pd != PlayerData.instance || gm.profileID != slot ||
                !Platform.IsSaveSlotIndexValid(slot) || gm.gameState == GameState.MAIN_MENU ||
                gm.gameState == GameState.INACTIVE || gm.IsInSceneTransition || HeroController.instance == null) {
                ExitSaveFailed();
                yield break;
            }
            IH.StopUIInput();
            // Native code waits for the asynchronous save callback, including
            // mod-local settings, and cancels the unload if the write fails.
            yield return gm.ReturnToMainMenu(GameManager.ReturnToMainMenuSaveModes.SaveAndCancelOnFail, saved => {
                IH.StartUIInput();
                if (saved) UM.returnMainMenuPrompt.HighlightDefault();
                else ExitSaveFailed();
            });
        } finally {
            _returningToMenu = false;
            IH.StartUIInput();
        }
    }

    private static void ExitSaveFailed() {
        Logger.Warn("Disconnect exit cancelled: saving the loaded profile failed or was unsafe");
        InternalChatBox?.AddMessage(Localize.T("保存未完成，已取消退出。请在暂停菜单重试“保存并退出”，进度仍保留在当前游戏中。"));
    }

    /// <summary>
    /// Open the save slot selection screen (from the multiplayer connect menu) and execute the given callback when
    /// a save is selected.
    /// </summary>
    /// <param name="callback">The action to execute when save slot selection is finished. The boolean parameter
    /// indicates whether a save slot is selected (true) or the back button was pressed (false). Can be null, in which
    /// case no callback is executed.</param>
    /// <param name="confirmJoin">Require confirmation before opening any selected join save.</param>
    public void OpenSaveSlotSelection(Action<bool> callback = null, bool confirmJoin = false) {
        CloseSaveSlotSelection();
        _confirmJoinSave = confirmJoin;
        _saveSlotSelectedAction = callback;

        On.GameManager.StartNewGame += OnStartNewGame;
        On.GameManager.ContinueGame += OnContinueGame;
        On.GameManager.LoadGameFromUI += OnLoadJoinSave;
        On.UIManager.StartNewGame += OnStartJoinSave;

        _saveMenuTransition = UM.StartCoroutine(GoToSaveMenu());
    }
    
    /// <summary>
    /// Callback method for when a new game is started. This is used to check when to start a hosted server from
    /// the save menu.
    /// </summary>
    private void OnStartNewGame(On.GameManager.orig_StartNewGame orig, GameManager self, bool permaDeathMode, bool bossRushMode) {
        EnterSelectedSave(() => orig(self, permaDeathMode, bossRushMode));
    }

    /// <summary>
    /// Callback method for when a save file is continued. This is used to check when to start a hosted server from
    /// the save menu.
    /// </summary>
    private void OnContinueGame(On.GameManager.orig_ContinueGame orig, GameManager self) {
        EnterSelectedSave(() => orig(self));
    }

    private void TryAddMultiplayerScreen() {
        if (UM == null || UM.mainMenuButtons == null || UM.mainMenuButtons.startButton == null) return;
        var parent = UM.mainMenuButtons.gameObject;
        var lan = AddTitleConnectionButton(parent, "StartMultiplayerButton", "StartMultiplayerBtn", 1, () => {
            _returnToExternalMenu = false;
            UM.StartCoroutine(GoToMultiplayerMenu());
        });
        var steam = _openExternalMenu != null
            ? AddTitleConnectionButton(parent, "StartSteamMultiplayerButton", "StartSteamMultiplayerBtn", 2, () => _openExternalMenu?.Invoke())
            : null;
        FixMultiplayerButtonNavigation(lan, steam);
        UM.StartCoroutine(WaitForInput());
        IEnumerator WaitForInput() {
            yield return new WaitUntil(() => IH.acceptingInput);
            if (lan != null) FixMultiplayerButtonNavigation(lan, steam);
        }
        if (_connectMenu == null) _connectMenu = _connectInterface.CreateMenu(() => UM.StartCoroutine(ReturnFromConnectionMenu()));
    }

    private GameObject AddTitleConnectionButton(GameObject parent, string name, string key, int index, Action open) {
        var button = parent.FindGameObjectInChildren(name);
        if (button == null) {
            button = Object.Instantiate(UM.mainMenuButtons.startButton.gameObject, parent.transform);
            button.name = name;
            var trigger = button.GetComponent<EventTrigger>();
            if (trigger != null) trigger.triggers.Clear();
        }
        button.transform.SetSiblingIndex(index);
        var localized = button.GetComponent<AutoLocalizeTextUI>();
        localized.textKey = key; localized.RefreshTextFromLocalization();
        foreach (var text in button.GetComponentsInChildren<Text>(true)) {
            text.font = MenuResources.NotoSerifCJKSCRegular;
            text.horizontalOverflow = HorizontalWrapMode.Overflow;
            text.verticalOverflow = VerticalWrapMode.Overflow;
            Localize.Attach(text);
        }
        var native = button.GetComponent<MenuButton>();
        native.buttonType = MenuButton.MenuButtonType.CustomSubmit;
        native.submitAction = _ => open(); native.proceed = true;
        return button;
    }

    private void FixMultiplayerButtonNavigation(GameObject lanObject, GameObject steamObject) {
        var lan = lanObject.GetComponent<MenuButton>();
        var steam = steamObject != null ? steamObject.GetComponent<MenuButton>() : null;
        var start = UM.mainMenuButtons.startButton;
        var options = UM.mainMenuButtons.optionsButton;
        var nav = start.navigation; nav.selectOnDown = lan; start.navigation = nav;
        nav = lan.navigation; nav.selectOnUp = start; nav.selectOnDown = steam != null ? steam : options; lan.navigation = nav;
        if (steam != null) {
            nav = steam.navigation; nav.selectOnUp = lan; nav.selectOnDown = options; steam.navigation = nav;
        }
        nav = options.navigation; nav.selectOnUp = steam != null ? steam : lan; options.navigation = nav;
    }

    // Save selection returns to the transport that opened it, including the
    // connection-failure path which interrupts the native join confirmation.
    private IEnumerator GoToMultiplayerMenu() => GoToConnectionMenu(
        _returnToExternalMenu && _externalMenu != null ? _externalMenu : _connectMenu);

    private IEnumerator GoToConnectionMenu(MenuScreen menu) {
        if (_connectionMenuBusy || menu == null) yield break;
        _connectionMenuBusy = true;
        try {
            IH.StopUIInput();
            if (UM.menuState == MainMenuState.MAIN_MENU) {
                UM.StartCoroutine(ReflectionHelper.CallMethod<UIManager, IEnumerator>(UM, "FadeOutSprite", UM.gameTitle));
                UM.subtitleFSM.SendEvent("FADE OUT");
                yield return UM.FadeOutCanvasGroup(UM.mainMenuScreen);
            } else if (UM.menuState == MainMenuState.SAVE_PROFILES) yield return UM.HideSaveProfileMenu();
            else yield return UM.HideCurrentMenu();
            yield return UM.ShowMenu(menu);
            // Main-menu sprite fades run independently of CanvasGroup fades.
            // A quick return/open must not leave the large game logo over the page.
            UM.gameTitle.gameObject.SetActive(false);
            UM.currentDynamicMenu = menu;
            UM.menuState = MainMenuState.DYNAMIC_MENU;
            IH.StartUIInput();
            menu.HighlightDefault();
        } finally { _connectionMenuBusy = false; }
    }

    private IEnumerator ReturnFromConnectionMenu() {
        if (_connectionMenuBusy) yield break;
        _connectionMenuBusy = true;
        try {
            IH.StopUIInput();
            yield return UM.HideCurrentMenu();
            UM.UIGoToMainMenu();
        } finally { _connectionMenuBusy = false; }
    }

    /// <summary>
    /// Coroutine to go to the saves menu from the multiplayer menu. Used whenever the user selects to host a server.
    /// </summary>
    private IEnumerator GoToSaveMenu() {

        yield return UM.HideCurrentMenu();
        yield return UM.GoToProfileMenu();
        
        var saveProfilesBackBtn = UM.saveProfileControls.gameObject.FindGameObjectInChildren("BackButton");
        if (!saveProfilesBackBtn) {
            Logger.Info("saveProfilesBackBtn is null");
            yield break;
        }

        var eventTrigger = saveProfilesBackBtn.GetComponent<EventTrigger>();
        RestoreSaveMenuBackButton();
        _saveMenuBackTrigger = eventTrigger;
        _originalBackTriggers = eventTrigger.triggers;

        eventTrigger.triggers = [];
        ChangeBtnTriggers(eventTrigger, () => {
            FinishSaveSlotSelection(false);
            
            UM.StartCoroutine(GoToMultiplayerMenu());
        });
        _saveMenuTransition = null;
    }

    /// <summary>
    /// Change the triggers on a button with the given event trigger.
    /// </summary>
    /// <param name="eventTrigger">The event trigger of the button to change.</param>
    /// <param name="action">The action that should be executed whenever the button is triggered.</param>
    private void ChangeBtnTriggers(EventTrigger eventTrigger, Action action) {
        var entry = new EventTrigger.Entry {
            eventID = EventTriggerType.Submit
        };
        entry.callback.AddListener(_ => action.Invoke());
        eventTrigger.triggers.Add(entry);
        
        var entry2 = new EventTrigger.Entry {
            eventID = EventTriggerType.PointerClick
        };
        entry2.callback.AddListener(_ => action.Invoke());
        eventTrigger.triggers.Add(entry2);
    }
    
    #region Internal UI manager methods

    /// <summary>
    /// Callback method for when the client successfully connects.
    /// </summary>
    public void OnSuccessfulConnect() {
        _connectInterface.OnSuccessfulConnect();
        _pingInterface.SetEnabled(true);
    }

    /// <summary>
    /// Callback method for when the client fails to connect.
    /// </summary>
    /// <param name="result">The result of the failed connection.</param>
    public void OnFailedConnect(ConnectionFailedResult result) {
        _connectInterface.OnFailedConnect(result);
    }

    /// <summary>
    /// Callback method for when the client disconnects.
    /// </summary>
    public void OnClientDisconnect() {
        var selectingJoinSave = _confirmJoinSave;
        CloseSaveSlotSelection();
        if (selectingJoinSave) UM.StartCoroutine(ReturnFromInterruptedJoinSelection());
        _connectInterface.OnClientDisconnect();
        _pingInterface.SetEnabled(false);
    }

    #endregion

    #region IUiManager methods

    /// <inheritdoc />
    public void DisableTeamSelection() {
    }

    /// <inheritdoc />
    public void EnableTeamSelection() {
    }

    /// <inheritdoc />
    public void DisableSkinSelection() {
        // SettingsInterface.OnAddonSetSkinSelection(false);
    }

    /// <inheritdoc />
    public void EnableSkinSelection() {
        // SettingsInterface.OnAddonSetSkinSelection(true);
    }

    #endregion
}
