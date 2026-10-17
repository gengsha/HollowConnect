using System.Collections.Generic;
using Hkmp.Api.Client;
using HollowConnect.Core;
using Modding;
using UnityEngine;

namespace HollowConnect;

public sealed class Settings {
    public bool ShareProgress = false;
    public bool OffscreenNames = true;
    public bool Rescue = true;
    public bool EntryProtection = true;
    public bool AllowTeleport = true;
    public KeyCode SelectTeammateKey = KeyCode.F7;
    public KeyCode TeleportKey = KeyCode.F8;
    public KeyCode GiveUpKey = KeyCode.F6;
    public KeyCode DeathLeaderboardKey = KeyCode.F5;
}

public sealed class CoopMod : Mod, IGlobalSettings<Settings>, ILocalSettings<DeathStatistics>, IMenuMod {
    public static CoopMod Instance;
    public static Settings Settings = new Settings();
    public static DeathStatistics Statistics = new DeathStatistics();
    public const string Version = "0.1.20";
    public CoopMod() : base("HollowConnect") { Instance = this; }
    public override string GetVersion() => Version;
    public override void Initialize() {
        Log("HollowConnect initialized; compatible engine: HKMP 3.0.0-hc11");
        // The API skips OnLoadLocal when a slot has no data for this mod.
        // Reset before every load so switching to an older save cannot inherit another slot's board.
        On.GameManager.LoadGame += (orig, self, slot, callback) => { Statistics = new DeathStatistics(); orig(self, slot, callback); };
        ModHooks.NewGameHook += () => Statistics = new DeathStatistics();
    }
    public void OnLoadLocal(DeathStatistics value) => Statistics = value?.Copy() ?? new DeathStatistics();
    public DeathStatistics OnSaveLocal() => Statistics;
    public void OnLoadGlobal(Settings value) => Settings = value ?? new Settings();
    public Settings OnSaveGlobal() => Settings;
    public bool ToggleButtonInsideMenu => false;
    public List<IMenuMod.MenuEntry> GetMenuData(IMenuMod.MenuEntry? toggle) => new List<IMenuMod.MenuEntry> {
        new IMenuMod.MenuEntry("共享永久进度（房主）", new[]{"关闭", "开启"}, "开房前设置。能力、法术、骨钉技、护符、护符槽、面具/容器碎片、苍白矿石及列出的世界标记；资源按地点去重，骨钉升级和矿石消费各自计算。", i=>Settings.ShareProgress=i==1, ()=>Settings.ShareProgress?1:0),
        new IMenuMod.MenuEntry("屏外队友方位", new[]{"关闭", "开启"}, "显示屏外队友名字和箭头；其他房间显示房间名。", i=>Settings.OffscreenNames=i==1, ()=>Settings.OffscreenNames?1:0),
        new IMenuMod.MenuEntry("暗影救援（房主）", new[]{"关闭", "开启"}, "击破暗影后一血复活，救人者获得 33 点灵魂；按 " + Settings.GiveUpKey + " 放弃或全队倒地时正式死亡，回各自复活点。", i=>Settings.Rescue=i==1, ()=>Settings.Rescue?1:0),
        new IMenuMod.MenuEntry("入场免伤（房主）", new[]{"关闭", "开启"}, "场景、战斗同步和镜头就绪并恢复操作后，再保留 1.5 秒反应时间。", i=>Settings.EntryProtection=i==1, ()=>Settings.EntryProtection?1:0)
    };
}

public sealed class CoopClientAddon : ClientAddon {
    protected override string Name => "HollowConnect";
    protected override string Version => CoopMod.Version;
    public override bool NeedsNetwork => true;
    public override void Initialize(IClientApi api) {
        var go = new GameObject("HollowConnect runtime");
        Object.DontDestroyOnLoad(go);
        go.AddComponent<CoopRuntime>().Initialize(api, this);
        CoopMod.Instance?.Log("HollowConnect client addon ready");
    }
}
