using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using Hkmp.Game.Client;
using Hkmp.Ui;
using Hkmp.Game.Server;
using Hkmp.Game.Settings;
using Hkmp.Networking.Client;
using Hkmp.Util;
using Modding;
using Modding.Menu;
using Modding.Menu.Config;
using UnityEngine;
using UnityEngine.UI;
using Logger = Hkmp.Logging.Logger;

namespace Hkmp.Menu;

/// <summary>
/// Class for building the HKMP mod menu.
/// </summary>
internal class ModMenu {
    /// <summary>
    /// The time in seconds that a modified setting needs to be stopped being modified by the user before being
    /// applied.
    /// </summary>
    private const float SettingApplyDelay = 1.5f;

    /// <summary>
    /// The HKMP mod settings instance.
    /// </summary>
    private readonly ModSettings _modSettings;
    
    /// <summary>
    /// The client manager instance.
    /// </summary>
    private readonly ClientManager _clientManager;

    /// <summary>
    /// The server manager instance.
    /// </summary>
    private readonly ServerManager _serverManager;

    /// <summary>
    /// The net client instance.
    /// </summary>
    private readonly NetClient _netClient;

    /// <summary>
    /// List of callbacks that should be fired if the server settings change.
    /// </summary>
    private readonly List<Action<ServerSettings>> _serverSettingsChangedCallbacks;
    
    /// <summary>
    /// The top-level HKMP mod menu.
    /// </summary>
    private MenuScreen _hkmpMenu;
    /// <summary>
    /// The menu containing the client settings. Needs to be a static variable here to allow it to be accessed by
    /// lambdas and modified.
    /// </summary>
    private MenuScreen _clientSettingsMenu;
    private SkinLibraryMenu _skinLibrary;

    /// <summary>
    /// The menu containing the server settings. Needs to be a static variable here to allow it to be accessed by
    /// lambdas and modified.
    /// </summary>
    private MenuScreen _serverSettingsMenu;

    /// <summary>
    /// A local copy of the server settings for modification through the menu that will be used to either network to
    /// the server or modify our own hosted servers.
    /// </summary>
    private ServerSettings _localServerSettings;

    /// <summary>
    /// Coroutine that delays applying new server settings until no more changes are made within a certain time period.
    /// </summary>
    private Coroutine _delayedApplyServerSettingsRoutine;

    /// <summary>
    /// Constructs the mod menu class for HKMP.
    /// </summary>
    /// <param name="modSettings">The mod settings for HKMP.</param>
    /// <param name="clientManager">The client manager to register a callback for when server settings are updated.
    /// </param>
    /// <param name="serverManager">The server manager to get the initial server settings and update them if we are
    /// not connected to a server.</param>
    /// <param name="netClient">The net client to network changes to the server settings if we are connected to a
    /// server.</param>
    public ModMenu(
        ModSettings modSettings, 
        ClientManager clientManager, 
        ServerManager serverManager, 
        NetClient netClient
    ) {
        _modSettings = modSettings;
        _clientManager = clientManager;
        _serverManager = serverManager;
        _netClient = netClient;

        _serverSettingsChangedCallbacks = [];
    }

    /// <summary>
    /// Initialize the mod menu by registering hooks.
    /// </summary>
    public void Initialize() {
        On.UnityEngine.UI.MenuOptionHorizontal.OnPointerClick += (orig, option, pointer) => {
            if (!MenuPointerNavigation.HandleClick(option, pointer)) orig(option, pointer);
        };
        _clientManager.ServerSettingsChangedEvent += newSettings => {
            foreach (var action in _serverSettingsChangedCallbacks) {
                action.Invoke(newSettings);
            }
        };
    }

    /// <summary>
    /// Create the mod menu for HKMP. This consists of client-side settings (such as HUD elements and keybinds) and
    /// server settings.
    /// </summary>
    /// <param name="modListMenu">The MenuScreen for the mod list menu to return to.</param>
    /// <returns>The built HKMP menu screen.</returns>
    /// <exception cref="InvalidOperationException">Thrown when the menu could not be created due to missing
    /// implementation for a type in the server settings.</exception>
    public MenuScreen CreateMenu(MenuScreen modListMenu) {
        var builder = MenuUtils.CreateMenuBuilderWithBackButton("HKMP", modListMenu, out _);

        builder.AddContent(
            RegularGridLayout.CreateVerticalLayout(150f),
            c => {
                c.AddMenuButton("Client Settings", new MenuButtonConfig {
                    Label = "客户端设置",
                    SubmitAction = _ => UIManager.instance.UIGoToDynamicMenu(_clientSettingsMenu),
                    Proceed = true,
                    CancelAction = _ => UIManager.instance.UIGoToDynamicMenu(modListMenu),
                    Description = new DescriptionInfo {
                        Text = "调整本机的联机显示与操作。"
                    }
                });

                c.AddMenuButton("Server Settings", new MenuButtonConfig {
                    Label = "服务器设置",
                    SubmitAction = _ => UIManager.instance.UIGoToDynamicMenu(_serverSettingsMenu),
                    Proceed = true,
                    CancelAction = _ => UIManager.instance.UIGoToDynamicMenu(modListMenu),
                    Description = new DescriptionInfo {
                        Text = "有权限的玩家可以调整房间设置。"
                    }
                });
            }
        );

        _hkmpMenu = MenuPointerNavigation.Attach(builder.Build());
        Localize.Screen(_hkmpMenu);

        _skinLibrary = new SkinLibraryMenu(_clientManager);
        _clientSettingsMenu = CreateClientSettingsMenu();
        _serverSettingsMenu = CreateServerSettingsMenu();

        return _hkmpMenu;
    }

    /// <summary>
    /// Create the client settings menu.
    /// </summary>
    /// <returns>A <see cref="MenuScreen"/> for the client settings menu.</returns>
    private MenuScreen CreateClientSettingsMenu() {
        var builder = MenuUtils.CreateMenuBuilderWithBackButton("HKMP 客户端设置", _hkmpMenu, out _);
        builder.AddContent(
            RegularGridLayout.CreateVerticalLayout(112f),
            c => {
                c.AddMenuButton("SkinManager", new MenuButtonConfig {
                    Label = "皮肤管理器",
                    SubmitAction = _ => _skinLibrary.Open(_clientSettingsMenu),
                    CancelAction = _ => UIManager.instance.UIGoToDynamicMenu(_hkmpMenu),
                    Proceed = true
                }, out var skinButton);
                foreach (var text in skinButton.GetComponentsInChildren<Text>(true)) Localize.Attach(text);
                AddLockedOption(c, "SkinOption", "旧版皮肤");
                AddLockedOption(c, "TeamOption", "队伍");

                MenuUtils.AddModMenuContent(
                    [
                        new IMenuMod.MenuEntry {
                            Name = "延迟显示",
                            Description = "联机时在画面上显示延迟。",
                            Values = ["关闭", "开启"],
                            Saver = index => _modSettings.DisplayPing = index == 1,
                            Loader = () => _modSettings.DisplayPing ? 1 : 0
                        }
                    ],
                    c,
                    _hkmpMenu
                );

                c.AddKeybind(
                    "OpenChatKeybind",
                    _modSettings.Keybinds.OpenChat,
                    new KeybindConfig {
                        Label = "打开聊天的按键",
                        CancelAction = _ => UIManager.instance.UIGoToDynamicMenu(_hkmpMenu)
                    }
                );
            }
        );

        var screen = MenuPointerNavigation.Attach(builder.Build());
        Localize.Screen(screen);
        return screen;
    }

    private void AddLockedOption(ContentArea content, string name, string label) {
        // Keep the original options visible and focusable with a single read-only value.
        content.AddHorizontalOption(name, new HorizontalOptionConfig {
            Label = label,
            Options = ["已锁定"],
            ApplySetting = (_, _) => { },
            RefreshSetting = (setting, _) => setting.optionList.SetOptionTo(0),
            CancelAction = _ => UIManager.instance.UIGoToDynamicMenu(_hkmpMenu)
        }, out var option);
        option.SetOptionTo(0);
    }

    /// <summary>
    /// Create the server settings menu.
    /// </summary>
    /// <returns>A <see cref="MenuScreen"/> for the server settings menu.</returns>
    /// <exception cref="InvalidOperationException">Thrown when the menu could not be created due to missing
    /// implementation for a type in the server settings.</exception>
    private MenuScreen CreateServerSettingsMenu() {
        _serverSettingsChangedCallbacks.Clear();
        
        // We keep track of an instance of server settings specifically for this mod menu
        // This will initially be a copy of the server manager settings, which come from the mod settings
        _localServerSettings = _serverManager.InternalServerSettings.GetCopy();

        var serverSettingsProps = typeof(ServerSettings).GetProperties();

        var builder = MenuUtils.CreateMenuBuilderWithBackButton(
            "HKMP Server Settings", 
            _hkmpMenu, 
            out var serverSettingsBackButton
        );

        builder.AddContent(new NullContentLayout(), nullContentArea => {
            nullContentArea.AddScrollPaneContent(
                new ScrollbarConfig {
                    Navigation = new Navigation {
                        mode = Navigation.Mode.Explicit,
                        selectOnUp = serverSettingsBackButton,
                        selectOnDown = serverSettingsBackButton
                    },
                    Position = new AnchoredPosition {
                        ChildAnchor = new Vector2(0.0f, 1f),
                        ParentAnchor = new Vector2(1f, 1f),
                        Offset = new Vector2(-310f, 0.0f)
                    },
                    CancelAction = _ => UIManager.instance.UIGoToDynamicMenu(_hkmpMenu)
                },
                new RelLength(serverSettingsProps.Length * 150f),
                RegularGridLayout.CreateVerticalLayout(150f),
                scrollGridContentArea => {
                    foreach (var propInfo in serverSettingsProps) {
                        var name = propInfo.Name;
                        var type = propInfo.PropertyType;

                        // Define variables for the eventual creation of an individual setting that depend on the
                        // type of the setting (for example, the options will be different for a byte than for a bool)
                        string[] options;
                        Action<int> saver;
                        Func<int> loader;
                        // This action will be invoked whenever the server settings are changed externally, so outside
                        // the mod menu
                        Action<ServerSettings, MenuOptionHorizontal> serverSettingsChangedAction;

                        if (type == typeof(bool)) {
                            options = ["Off", "On"];

                            saver = i => {
                                ReflectionHelper.SetProperty(_localServerSettings, name, i == 1);
                                HandleUpdateServerSettings();
                            };
                            loader = () => ReflectionHelper.GetProperty<ServerSettings, bool>(_localServerSettings, name)
                                ? 1
                                : 0;

                            serverSettingsChangedAction = (newSettings, horizontalOptionToChange) => {
                                // Get the old and new values and check whether there is a change
                                var oldValue = ReflectionHelper.GetProperty<ServerSettings, bool>(_localServerSettings, name);
                                var newValue = ReflectionHelper.GetProperty<ServerSettings, bool>(newSettings, name);

                                if (oldValue != newValue) {
                                    // Set the mod menu option and update our local server settings instance
                                    horizontalOptionToChange.SetOptionTo(newValue ? 1 : 0);
                                    ReflectionHelper.SetProperty(_localServerSettings, name, newValue);
                                }
                            };
                        } else if (type == typeof(byte) && name.EndsWith("Damage")) {
                            // If the field is for the amount of damage for something, we fill the values with 0 through 20
                            options = new string[21];
                            for (var i = 0; i <= 20; i++) {
                                options[i] = i.ToString();
                            }

                            saver = i => {
                                ReflectionHelper.SetProperty(_localServerSettings, name, (byte) i);
                                HandleUpdateServerSettings();
                            };
                            loader = () => ReflectionHelper.GetProperty<ServerSettings, byte>(_localServerSettings, name);

                            serverSettingsChangedAction = (newSettings, horizontalOptionToChange) => {
                                // Get the old and new values and check whether there is a change
                                var oldValue = ReflectionHelper.GetProperty<ServerSettings, byte>(_localServerSettings, name);
                                var newValue = ReflectionHelper.GetProperty<ServerSettings, byte>(newSettings, name);

                                if (oldValue != newValue) {
                                    // Set the mod menu option and update our local server settings instance
                                    horizontalOptionToChange.SetOptionTo(newValue);
                                    ReflectionHelper.SetProperty(_localServerSettings, name, newValue);
                                }
                            };
                        } else {
                            throw new InvalidOperationException(
                                $"Could not make menu entry for unknown field type: {type}, for field: {name}");
                        }

                        // Try to obtain the label and description of the setting using the mod menu attribute
                        var label = name;
                        DescriptionInfo? descriptionInfo = null;
                        var menuSettingAttr = propInfo.GetCustomAttribute<ModMenuSettingAttribute>();
                        if (menuSettingAttr != null) {
                            label = menuSettingAttr.Name;
                            if (menuSettingAttr.Description != null) {
                                descriptionInfo = new DescriptionInfo {
                                    Text = menuSettingAttr.Description
                                };
                            }
                        }

                        scrollGridContentArea.AddHorizontalOption(
                            name,
                            new HorizontalOptionConfig {
                                Options = options,
                                Label = label,
                                ApplySetting = (_, i) => saver.Invoke(i),
                                RefreshSetting = (s, _) => s.optionList.SetOptionTo(loader.Invoke()),
                                CancelAction = _ => UIManager.instance.UIGoToDynamicMenu(_hkmpMenu),
                                Description = descriptionInfo
                            },
                            out var horizontalOption
                        );
                        horizontalOption.menuSetting.RefreshValueFromGameSettings();

                        _serverSettingsChangedCallbacks.Add(newSettings => {
                            serverSettingsChangedAction.Invoke(newSettings, horizontalOption);
                        });
                    }
                }
            );
        });

        return MenuPointerNavigation.Attach(builder.Build());
    }

    /// <summary>
    /// Run the given action after a delay (defined in <see cref="SettingApplyDelay"/>) and store the delayed execution coroutine in the referenced
    /// variable. If this method is called again with the same referenced coroutine before the action is executed,
    /// it will be cancelled and a new delayed execution is scheduled.
    /// Can be used to apply the changing of certain settings only if the user stops modifying the setting for a
    /// certain period of time.
    /// </summary>
    /// <param name="action">The action to execute delayed if this method is not called again within a certain period
    /// of time.</param>
    /// <param name="coroutine">The coroutine containing the previous delayed execution of the action. This variable
    /// will be used to store the new coroutine.</param>
    private void RunActionWhenNoChanges(Action action, ref Coroutine coroutine) {
        if (coroutine != null) {
            MonoBehaviourUtil.Instance.StopCoroutine(coroutine);
        }

        coroutine = MonoBehaviourUtil.Instance.StartCoroutine(RunActionWithDelay(action, SettingApplyDelay));
    }

    /// <summary>
    /// Handles updates to the server settings. Checks whether we are connected to a server and only sends updates
    /// if no changes were made to the local server settings in this class for a certain period of time to prevent
    /// sending unnecessary updates.
    /// </summary>
    private void HandleUpdateServerSettings() {
        if (!_netClient.IsConnected) {
            // If the client is not connected to any server, we simply update the server settings of the 
            // server manager so that when we host a server, it will have the same settings as in this menu
            _serverManager.InternalServerSettings.SetAllProperties(_localServerSettings);
            return;
        }
        
        RunActionWhenNoChanges(() => {
            _netClient.UpdateManager.SetServerSettingsUpdate(_localServerSettings);
        }, ref _delayedApplyServerSettingsRoutine);
    }

    /// <summary>
    /// Run the given action with the given delay.
    /// </summary>
    /// <param name="action">The action to invoke after the delay.</param>
    /// <param name="delay">The delay in seconds.</param>
    /// <returns>The coroutine of the delayed invocation.</returns>
    private static IEnumerator RunActionWithDelay(Action action, float delay) {
        yield return new WaitForSeconds(delay);
        
        action.Invoke();
    }
}
