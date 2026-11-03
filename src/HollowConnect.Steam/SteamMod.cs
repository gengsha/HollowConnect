using System;
using System.Collections.Generic;
using Modding;
using UnityEngine;

namespace HollowConnect.Steam;

public sealed class SteamMod : Mod, IMenuMod {
    private SteamRoomRuntime runtime;
    public SteamMod() : base("HollowConnect Steam") { }
    public override string GetVersion() => "0.2.7";
    public override void Initialize() {
        if (runtime != null) return;
        var owner = new GameObject("HollowConnect Steam");
        UnityEngine.Object.DontDestroyOnLoad(owner);
        runtime = owner.AddComponent<SteamRoomRuntime>();
        Log("Steam connection module loaded; F10 opens the native Steam menu.");
    }
    public bool ToggleButtonInsideMenu => false;
    public List<IMenuMod.MenuEntry> GetMenuData(IMenuMod.MenuEntry? toggle) => new() {
        new IMenuMod.MenuEntry("Steam 联机（F10）", new[] {"关闭", "打开"},
            "创建好友房间，邀请好友或加入好友主持的游戏。", i => { if (runtime != null) runtime.SetMenuVisible(i == 1); },
            () => runtime != null && runtime.Visible ? 1 : 0)
    };
}
