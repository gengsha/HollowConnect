using System;
using System.IO;
using System.Diagnostics;
using System.Linq;
using Hkmp.Game.Client;
using Hkmp.Game.Client.Skin;
using Hkmp.Ui;
using Modding.Menu;
using Modding.Menu.Config;
using UnityEngine;
using UnityEngine.UI;
using Logger = Hkmp.Logging.Logger;

namespace Hkmp.Menu;

internal sealed class SkinLibraryMenu(ClientManager client) {
    private string[] _ids = [""];
    private string _extraId, _optionLanguage;
    private MenuScreen _library, _parent;
    private MenuOptionHorizontal _skinOption, _showTeammateSkins;
    private MenuButton _back;
    private Text _notice;
    private string _message;

    internal void Open(MenuScreen parent) {
        _parent = parent;
        CreateLibrary();
        _message = null;
        RefreshChoices();
        RefreshStatus();
        UIManager.instance.UIGoToDynamicMenu(_library);
    }

    private void CreateLibrary() {
        if (_library != null) return;
        void Back() => UIManager.instance.UIGoToDynamicMenu(_parent);
        var builder = MenuUtils.CreateMenuBuilder("皮肤管理器");
        builder.AddContent(NativeConnectionMenu.At(215), content => content.AddHorizontalOption("LocalSkinChoice",
            new HorizontalOptionConfig {
                Label = "本机皮肤",
                Options = [Localize.T("原版外观")],
                ApplySetting = (_, index) => Select(index),
                RefreshSetting = (_, _) => SyncSelection(),
                CancelAction = _ => Back()
            }, out _skinOption));
        builder.AddContent(NativeConnectionMenu.At(101), content => content.AddHorizontalOption("ShowTeammateSkins",
            new HorizontalOptionConfig {
                Label = "查看队友皮肤",
                Options = ["关闭", "开启"],
                ApplySetting = (_, index) => client.ShowTeammateSkins = index == 1,
                RefreshSetting = (_, _) => _showTeammateSkins.SetOptionTo(client.ShowTeammateSkins ? 1 : 0),
                CancelAction = _ => Back()
            }, out _showTeammateSkins));
        var help = NativeConnectionMenu.Text(builder, "TeammateSkinsHelp",
            "本机装有队友正在用的皮肤包时，才能看到这套皮肤。缺少皮肤包时显示原版。", 24, 78, 34);
        help.resizeTextForBestFit = true;
        help.resizeTextMinSize = 27;
        help.resizeTextMaxSize = 34;
        var folder = NativeConnectionMenu.Button(builder, "OpenSkinFolder", "打开皮肤目录", -75, OpenInstallFolder, Back);
        var scan = NativeConnectionMenu.Button(builder, "RescanSkins", "重新扫描", -162, Rescan, Back);
        _notice = NativeConnectionMenu.Text(builder, "SkinNotice", "", -259, 64, 34);
        _back = NativeConnectionMenu.Button(builder, "BackButton", "返回", -365, Back, Back);
        _library = MenuPointerNavigation.Attach(builder.Build());
        NativeConnectionMenu.Finish(_library, _skinOption, _showTeammateSkins, folder, scan, _back);
        _showTeammateSkins.SetOptionTo(client.ShowTeammateSkins ? 1 : 0);
        var value = _skinOption.optionText;
        value.supportRichText = false;
        value.horizontalOverflow = HorizontalWrapMode.Wrap;
        value.resizeTextForBestFit = true;
        value.resizeTextMinSize = 23;
        value.resizeTextMaxSize = value.fontSize;
        _library.gameObject.AddComponent<SkinMenuRefresh>().Initialize(RefreshStatus);
    }

    private void Select(int index) {
        if (index < 0 || index >= _ids.Length) return;
        var current = CurrentIndex();
        if (index == current) return;
        var direction = index == (current + _ids.Length - 1) % _ids.Length ? -1 : 1;
        string unreadable = null;
        // Try only the entries passed while moving in this direction. A bad PNG
        // must not trap the selector between two otherwise usable skins.
        while (index != current) {
            if (client.TrySelectSkin(_ids[index])) break;
            unreadable ??= _ids[index];
            index = (index + direction + _ids.Length) % _ids.Length;
        }
        _message = unreadable == null ? null : Format("无法读取皮肤「{0}」，已跳过。", SkinName(unreadable));
        RefreshStatus();
    }

    private void Rescan() {
        client.RescanSkins();
        RefreshChoices();
        _message = null;
        RefreshStatus();
    }

    private void RefreshChoices() {
        var installed = AtlasSkinPacks.GetIds();
        var current = client.LocalDisplaySkinId;
        // Custom Knight can apply a skin that is outside the PNG library.
        _extraId = current.Length != 0 && Array.IndexOf(installed, current) < 0 ? current : null;
        _ids = new[] { "" }.Concat(_extraId == null ? installed : installed.Append(_extraId)).ToArray();
        _optionLanguage = Localize.Language;
        _skinOption.SetOptionList(_ids.Select(ChoiceLabel).ToArray());
        _skinOption.SetOptionTo(CurrentIndex());
    }

    private int CurrentIndex() => System.Math.Max(0, Array.IndexOf(_ids, client.LocalDisplaySkinId));

    private void SyncSelection() {
        var index = CurrentIndex();
        if (_skinOption.selectedOptionIndex != index) _skinOption.SetOptionTo(index);
    }

    private void OpenInstallFolder() {
        try {
            var path = AtlasSkinPacks.InstallFolder;
            Directory.CreateDirectory(path);
            if (SystemInfo.operatingSystemFamily == OperatingSystemFamily.Windows) {
                using var process = Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
            } else {
                Application.OpenURL(new Uri(path + Path.DirectorySeparatorChar).AbsoluteUri);
            }
            _message = null;
        } catch (Exception e) {
            Logger.Warn($"Could not open the skin folder: {e.Message}");
            _message = Localize.T("无法打开皮肤目录。");
        }
        RefreshStatus();
    }

    private void RefreshStatus() {
        var current = client.LocalDisplaySkinId;
        if (_optionLanguage != Localize.Language || _extraId != null && _extraId != current || Array.IndexOf(_ids, current) < 0) {
            RefreshChoices();
        } else {
            SyncSelection();
        }
        SetText(_notice, _message ?? (client.SavedAtlasId.Length != 0 && current.Length == 0
            ? Format("已保存的皮肤不可用：{0}", SkinName(client.SavedAtlasId)) : ""));
    }

    private static string Format(string source, params object[] args) => string.Format(Localize.T(source), args);
    private static string SkinName(string id) => string.IsNullOrEmpty(id) ? Localize.T("原版外观") :
        ShortName(AtlasSkinPacks.DisplayName(id), 36);
    private static string ChoiceLabel(string id) => AtlasSkinPacks.IsLegacy(id)
        ? Format("{0}（旧版皮肤）", SkinName(id)) : SkinName(id);
    private static string ShortName(string value, int length) {
        value = string.Concat(value.Select(c => char.IsControl(c) ? ' ' : c));
        if (value.Length <= length) return value;
        return value.Substring(0, char.IsHighSurrogate(value[length - 1]) ? length - 1 : length) + "…";
    }
    private static void SetText(Text text, string value) {
        if (text.text != value) text.text = value;
    }
}

internal sealed class SkinMenuRefresh : MonoBehaviour {
    private Action _refresh;
    private float _next;
    internal void Initialize(Action refresh) => _refresh = refresh;
    private void Update() {
        if (Time.unscaledTime < _next) return;
        _next = Time.unscaledTime + .35f;
        _refresh?.Invoke();
    }
}
