using System;
using System.Collections;
using Hkmp.Game.Server;
using Hkmp.Game.Settings;
using Hkmp.Networking.Client;
using Hkmp.Util;
using Modding.Menu;
using UnityEngine;
using UnityEngine.UI;

namespace Hkmp.Ui;

internal class ConnectInterface {
    private readonly ModSettings _modSettings;
    private InputField _usernameInput, _addressInput, _portInput;
    private MenuButton _connectionButton, _serverButton;
    private Text _feedbackText;
    private Coroutine _feedbackHideCoroutine;
    private MenuScreen _menu;
    public event Action<string, int, string> ConnectButtonPressed;
    public event Action<string, int> StartHostButtonPressed;
    internal Func<bool> ConnectionReserved;

    public ConnectInterface(ModSettings settings) { _modSettings = settings; }

    internal MenuScreen CreateMenu(Action back) {
        if (_menu != null) return _menu;
        var builder = MenuUtils.CreateMenuBuilder("局域网联机");
        _usernameInput = NativeConnectionMenu.Input(builder, "Username", "玩家名", _modSettings.Username, 210, 20, back,
            (_, _, c) => char.IsLetterOrDigit(c) ? c : '\0');
        _addressInput = NativeConnectionMenu.Input(builder, "Address", "服务器地址", _modSettings.ConnectAddress, 110, 253, back,
            (_, _, c) => !char.IsWhiteSpace(c) && !char.IsControl(c) ? c : '\0', concealed: true);
        _portInput = NativeConnectionMenu.Input(builder, "Port", "端口", _modSettings.ConnectPort == -1 ? "" : _modSettings.ConnectPort.ToString(),
            10, 5, back, (_, _, c) => c >= '0' && c <= '9' ? c : '\0');
        _connectionButton = NativeConnectionMenu.Button(builder, "Connect", "加入游戏", -100, OnConnectButtonPressed, back);
        _serverButton = NativeConnectionMenu.Button(builder, "Host", "主持游戏", -190, OnStartButtonPressed, back);
        _feedbackText = NativeConnectionMenu.Text(builder, "Feedback", "支持局域网、直连及内网穿透", -280, 75, 28);
        var returnButton = NativeConnectionMenu.Button(builder, "BackButton", "返回", -380, back, back);
        _menu = builder.Build();
        NativeConnectionMenu.Finish(_menu, _usernameInput.transform.parent.GetComponent<MenuButton>(), _addressInput.transform.parent.GetComponent<MenuButton>(),
            _portInput.transform.parent.GetComponent<MenuButton>(), _connectionButton, _serverButton, returnButton);
        return _menu;
    }

    public void OnClientDisconnect() => RestoreConnectButton();
    public void OnSuccessfulConnect() { SetFeedbackText(Color.white, "已连接"); RestoreConnectButton(); }
    private void RestoreConnectButton() {
        if (_connectionButton == null) return;
        NativeConnectionMenu.SetLabel(_connectionButton, "加入游戏"); _connectionButton.interactable = true;
    }
    public void OnFailedConnect(ConnectionFailedResult result) {
        var reason = result.Reason switch {
            ConnectionFailedReason.InvalidAddons => "双方模组不兼容",
            ConnectionFailedReason.SocketException or ConnectionFailedReason.IOException => "网络错误",
            ConnectionFailedReason.TimedOut => "连接超时",
            ConnectionFailedReason.Other => ((ConnectionFailedMessageResult)result).Message,
            _ => "未知原因"
        };
        SetFeedbackText(Color.white, "连接失败：" + Localize.T(reason)); RestoreConnectButton();
    }
    private void OnConnectButtonPressed() {
        if (ConnectionReserved?.Invoke() == true) { SetFeedbackText(Color.white, "请先离开 Steam 房间"); return; }
        var address = _addressInput.text;
        if (address.Length == 0) { SetFeedbackText(Color.white, "请输入服务器地址"); return; }
        if (!TryPort(out var port) || !ValidateUsername(out var username)) return;
        _modSettings.ConnectAddress = address; _modSettings.ConnectPort = port; _modSettings.Username = username;
        NativeConnectionMenu.SetLabel(_connectionButton, "正在连接…"); _connectionButton.interactable = false;
        ConnectButtonPressed?.Invoke(address, port, username);
    }
    private void OnStartButtonPressed() {
        if (ConnectionReserved?.Invoke() == true) { SetFeedbackText(Color.white, "请先离开 Steam 房间"); return; }
        if (!TryPort(out var port) || !ValidateUsername(out var username)) return;
        StartHostButtonPressed?.Invoke(username, port);
    }
    private bool TryPort(out int port) {
        if (int.TryParse(_portInput.text, out port) && port > 0 && port <= ushort.MaxValue) return true;
        SetFeedbackText(Color.white, "请输入 1–65535 之间的端口"); return false;
    }
    private bool ValidateUsername(out string username) {
        username = _usernameInput.text;
        if (username.Length > 0 && username.Length <= ServerManager.UsernameMaxLength) return true;
        SetFeedbackText(Color.white, "请输入 1–20 位字母、汉字或数字的玩家名"); return false;
    }
    private void SetFeedbackText(Color color, string text) {
        if (_feedbackText == null) return;
        _feedbackText.color = color; _feedbackText.text = text;
        if (_feedbackHideCoroutine != null) MonoBehaviourUtil.Instance.StopCoroutine(_feedbackHideCoroutine);
        _feedbackHideCoroutine = MonoBehaviourUtil.Instance.StartCoroutine(WaitHideFeedbackText());
    }
    private IEnumerator WaitHideFeedbackText() {
        yield return new WaitForSecondsRealtime(10);
        if (_feedbackText != null) _feedbackText.text = "支持局域网、直连及内网穿透";
    }
}
