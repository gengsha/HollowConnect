using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using GlobalEnums;
using Hkmp.Game;
using Hkmp.Ui;
using Modding.Menu;
using Steamworks;
using UnityEngine;
using UnityEngine.UI;

namespace HollowConnect.Steam;

internal sealed partial class SteamRoomRuntime {
    private MenuScreen roomMenu;
    private Text roomStatus;
    private InputField nameInput;
    private MenuButton nameRow, hostButton, friendsButton, inviteButton, leaveButton, pendingButton, ignoreButton, pageButton, backButton;
    private readonly List<MenuButton> roomRows = new(), friendButtons = new();
    private readonly List<(ulong Lobby, string Name)> friendRooms = new();
    private bool roomOpenRequested, roomUiBusy, showFriends;
    private int friendsPage;
    private double nextUiRefresh, nextFriendsRefresh;
    internal bool Visible {
        get => roomMenu != null && UIManager.instance != null && UIManager.instance.currentDynamicMenu == roomMenu &&
            roomMenu.gameObject.activeInHierarchy && UIManager.instance.menuState == MainMenuState.DYNAMIC_MENU;
        set => roomOpenRequested = value;
    }

    private void EnsureRoomMenu() {
        if (roomMenu != null) { gateway?.SetMenu(roomMenu); return; }
        var builder = MenuUtils.CreateMenuBuilder("Steam 联机");
        roomStatus = NativeConnectionMenu.Text(builder, "Room status", status, 270, 100, 28);
        nameInput = NativeConnectionMenu.Input(builder, "Steam username", "玩家名", username, 160, 20,
            CloseRoomMenu, (_, _, c) => char.IsLetterOrDigit(c) ? c : '\0', value => username = value);
        nameRow = nameInput.transform.parent.GetComponent<MenuButton>(); roomRows.Add(nameRow);
        MenuButton Add(string id, string label, float y, Action action) {
            var button = NativeConnectionMenu.Button(builder, id, label, y, () => Guard(action), CloseRoomMenu);
            roomRows.Add(button); return button;
        }
        hostButton = Add("Create Steam room", "创建好友房间", 60, Host);
        pendingButton = Add("Join invitation", "加入邀请的房间", 60, JoinPending);
        inviteButton = Add("Invite friends", "邀请 Steam 好友", 60, () => SteamFriends.ActivateGameOverlayInviteDialog(lobby));
        friendsButton = Add("Show friend rooms", "查看好友主持的游戏", -30, () => { showFriends = !showFriends; nextFriendsRefresh = 0; RefreshRoomMenu(); });
        leaveButton = Add("Leave Steam room", "离开 Steam 房间", -30, () => Leave("已离开房间"));
        ignoreButton = Add("Ignore invitation", "忽略邀请", -30, () => { pendingLobby = 0; pendingConfirmation = false; RefreshRoomMenu(); });
        for (var i = 0; i < 3; i++) {
            var slot = i;
            friendButtons.Add(Add("Friend room " + i, "", -110 - i * 70, () => {
                var index = friendsPage * 3 + slot;
                if (index >= friendRooms.Count) return;
                pendingLobby = friendRooms[index].Lobby; JoinPending();
            }));
        }
        pageButton = Add("Next friend page", "下一页", -325, () => { friendsPage = (friendsPage + 1) % Math.Max(1, (friendRooms.Count + 2) / 3); RefreshRoomMenu(); });
        backButton = Add("Steam back", "返回", -400, CloseRoomMenu);
        roomMenu = builder.Build();
        NativeConnectionMenu.Finish(roomMenu, roomRows.ToArray());
        gateway?.SetMenu(roomMenu);
        RefreshRoomMenu();
    }

    private void ToggleRoomMenu() {
        if (Visible) CloseRoomMenu(); else OpenRoomMenu();
    }
    internal void SetMenuVisible(bool visible) {
        if (visible) { roomOpenRequested = true; OpenRoomMenu(); }
        else CloseRoomMenu();
    }
    private void OpenRoomMenu() {
        var ui = UIManager.instance; var gm = GameManager.instance;
        if (ui == null || gm == null || gateway == null || roomUiBusy || gm.IsInSceneTransition ||
            !InputHandler.Instance.acceptingInput || ui.IsAnimatingMenus ||
            (ExternalConnectionGateway.AtTitle && ui.menuState == MainMenuState.SAVE_PROFILES) || phase == Phase.SelectingSave) return;
        EnsureRoomMenu(); roomOpenRequested = false; RefreshRoomMenu();
        if (ExternalConnectionGateway.AtTitle) gateway.ShowMenu(roomMenu);
        else if (gm.gameState == GameState.PLAYING) gateway.ShowInGameMenu(roomMenu);
        else if (gm.gameState == GameState.PAUSED) ui.UIGoToDynamicMenu(roomMenu);
    }
    private void CloseRoomMenu() {
        roomOpenRequested = false;
        if (!Visible || roomUiBusy) return;
        StartCoroutine(CloseNativeRoomMenu());
    }
    private IEnumerator CloseNativeRoomMenu() {
        roomUiBusy = true;
        try {
            var ui = UIManager.instance;
            InputHandler.Instance.StopUIInput();
            yield return ui.HideCurrentMenu();
            if (ExternalConnectionGateway.AtTitle) ui.UIGoToMainMenu();
            else ui.UIGoToPauseMenu();
        } finally { roomUiBusy = false; }
    }
    private void UpdateRoomMenu() {
        if (roomOpenRequested && ExternalConnectionGateway.AtTitle) OpenRoomMenu();
        if (!Visible || Now < nextUiRefresh) return;
        nextUiRefresh = Now + .25;
        RefreshRoomMenu();
    }
    private void RefreshRoomMenu() {
        if (roomMenu == null) return;
        var available = ready && gateway != null && gateway.IsIdle && ExternalConnectionGateway.AtTitle && calls.Count == 0 && worker == null;
        var pending = pendingLobby != 0 && !Busy;
        if (!Busy && showFriends && ready && Now >= nextFriendsRefresh) { nextFriendsRefresh = Now + 2; ReadFriendRooms(); }
        void Display(MenuButton button, bool visible, bool enabled = true) {
            button.gameObject.SetActive(visible); button.interactable = enabled;
        }
        Display(nameRow, !Busy && !pending); nameInput.interactable = !Busy && !pending;
        Display(hostButton, !Busy && !pending, available);
        Display(friendsButton, !Busy && !pending, ready);
        NativeConnectionMenu.SetLabel(friendsButton, showFriends ? "收起好友房间" : "查看好友主持的游戏");
        Display(inviteButton, Busy && hosting && phase == Phase.Playing, ready && lobby.m_SteamID != 0);
        Display(leaveButton, Busy);
        NativeConnectionMenu.SetLabel(leaveButton, phase == Phase.SelectingSave ? "取消创建" : "离开 Steam 房间");
        Display(pendingButton, pending, available);
        Display(ignoreButton, pending);
        friendsPage = Math.Min(friendsPage, Math.Max(0, (friendRooms.Count - 1) / 3));
        for (var i = 0; i < friendButtons.Count; i++) {
            var index = friendsPage * 3 + i;
            var visible = !Busy && !pending && showFriends && index < friendRooms.Count;
            Display(friendButtons[i], visible, available);
            if (visible) NativeConnectionMenu.SetLabel(friendButtons[i], "加入 " + friendRooms[index].Name + " 的游戏");
        }
        Display(pageButton, !Busy && !pending && showFriends && friendRooms.Count > 3);
        NativeConnectionMenu.SetLabel(pageButton, "下一页（" + (friendsPage + 1) + "/" + Math.Max(1, (friendRooms.Count + 2) / 3) + "）");
        var detail = lastError.Length != 0 ? lastError : status;
        if (!Busy && !pending && showFriends && ready && friendRooms.Count == 0) detail += "\n未发现好友房间，可让好友从 Steam 列表邀请你";
        if (pendingLobby != 0 && Busy) detail += "\n收到好友邀请；结束当前连接后可加入";
        if (calls.Count != 0 && !Busy) detail += "\n正在等待 Steam 完成上一条请求…";
        roomStatus.text = detail;
        NativeConnectionMenu.Relink(roomMenu, roomRows);
    }
    private void ReadFriendRooms() {
        try {
            var rooms = new List<(ulong Lobby, string Name)>(); var seen = new HashSet<ulong>();
            var count = Math.Min(256, SteamFriends.GetFriendCount(EFriendFlags.k_EFriendFlagImmediate));
            for (var i = 0; i < count; i++) {
                var friend = SteamFriends.GetFriendByIndex(i, EFriendFlags.k_EFriendFlagImmediate);
                if (!SteamFriends.GetFriendGamePlayed(friend, out var info) || info.m_gameID.AppID().m_AppId != SteamRoomRules.AppId) continue;
                SteamFriends.RequestFriendRichPresence(friend);
                var target = info.m_steamIDLobby.m_SteamID;
                if (target == 0 && !SteamRoomRules.TryCommand(SteamFriends.GetFriendRichPresence(friend, "connect"), out target)) continue;
                var room = new CSteamID(target);
                if (!room.IsValid() || !room.IsLobby() || !seen.Add(target)) continue;
                var name = SteamFriends.GetFriendPersonaName(friend).Replace('\n', ' ').Replace('\r', ' ');
                if (name.Length > 18) name = name.Substring(0, 18) + "…";
                rooms.Add((target, name));
            }
            friendRooms.Clear(); friendRooms.AddRange(rooms);
        } catch (Exception e) { lastError = "读取好友列表失败：" + e.Message; }
    }
}
