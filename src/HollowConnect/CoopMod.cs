using System.Collections.Generic;
using System;
using Hkmp.Api.Client;
using HollowConnect.Core;
using Modding;
using UnityEngine;
using Object = UnityEngine.Object;

namespace HollowConnect;

public sealed class Settings {
    public bool ShareProgress = true;
    public bool ShareCharms = true;
    public bool OffscreenNames = true;
    public float OtherPlayerOpacity = 1f;
    public bool ColoredPlayerNames = true;
    public bool BossHealthScaling = false;
    public bool BossDamageRanking = false;
    public int BossTargetingMode = 0;
    public float BossExtraHealthPerPlayer = .6f;
    public bool Rescue = true;
    public int RescueLimit = 0;
    public bool SpectateAfterExhaustion = false;
    public bool BossVictoryRescue = true;
    public bool EntryProtection = true;
    public bool AllowTeleport = true;
    public KeyCode SelectTeammateKey = KeyCode.F7;
    public KeyCode TeleportKey = KeyCode.F8;
    public KeyCode GiveUpKey = KeyCode.F6;
    public KeyCode DeathLeaderboardKey = KeyCode.F5;
}

public sealed partial class CoopMod : Mod, IGlobalSettings<Settings>, ILocalSettings<DeathStatistics>, ICustomMenuMod {
    public static CoopMod Instance;
    public static Settings Settings = new Settings();
    public static DeathStatistics Statistics = new DeathStatistics();
    public const string Version = "0.2.2";
    // Key source receipts and personal locks must not mix with older shared-door clients.
    public const string NetworkVersion = Version + "-l8";
    public CoopMod() : base("HollowConnect") { Instance = this; }
    public override string GetVersion() => Version;
    public override List<(string, string)> GetPreloadNames() => new List<(string, string)> {
        ("Tutorial_01", "_Props/Chest"), ("Room_shop", "Shop Menu")
    };
    public override void Initialize(Dictionary<string, Dictionary<string, GameObject>> preloadedObjects) {
        ResourceNotifications.Preload(preloadedObjects);
        Initialize();
    }
    public override void Initialize() {
        Log("HollowConnect initialized; compatible engine: HKMP 3.0.0-hc13");
        Hkmp.CoopContext.HostLocalWorldOnly = () => !Settings.ShareProgress;
        // The API skips OnLoadLocal when a slot has no data for this mod.
        // Reset before every load so switching to an older save cannot inherit another slot's board.
        On.GameManager.LoadGame += (orig, self, slot, callback) => { Statistics = new DeathStatistics(); orig(self, slot, callback); };
        ModHooks.NewGameHook += () => Statistics = new DeathStatistics();
    }
    public void OnLoadLocal(DeathStatistics value) => Statistics = value?.Copy() ?? new DeathStatistics();
    public DeathStatistics OnSaveLocal() => Statistics;
    public void OnLoadGlobal(Settings value) {
        Settings = value ?? new Settings();
        Hkmp.CoopContext.RefreshPlayerVisuals();
    }
    public Settings OnSaveGlobal() => Settings;
    public bool ToggleButtonInsideMenu => false;
    public List<IMenuMod.MenuEntry> GetMenuData(IMenuMod.MenuEntry? toggle) => GuardRoomSettings(new List<IMenuMod.MenuEntry> {
        new IMenuMod.MenuEntry("共享永久进度（房主）", new[]{"关闭", "开启"}, "默认开启，开房前设置。关闭后普通怪、机关、奖励与剧情各自推进，只保留同房 Boss 合作及队友位置；传送、救援按各自开关。开启时能力和列出的资源、世界标记共享，骨钉升级及矿石消费各自计算。", i=>Settings.ShareProgress=i==1, ()=>Settings.ShareProgress?1:0),
        new IMenuMod.MenuEntry("共享护符（房主）", new[]{"关闭", "开启"}, "开房前设置，仅在共享永久进度开启时生效。共享护符获得状态；护符槽仍按原规则共享，装备各自独立。", i=>Settings.ShareCharms=i==1, ()=>Settings.ShareCharms?1:0),
        new IMenuMod.MenuEntry("屏外队友方位", new[]{"关闭", "开启"}, "显示屏外队友名字和箭头；其他房间显示房间名。", i=>Settings.OffscreenNames=i==1, ()=>Settings.OffscreenNames?1:0),
        OpacityMenuEntry(),
        new IMenuMod.MenuEntry("队友名字配色（本机）", new[]{"关闭", "开启"}, "默认开启。队友头顶名字与地图/方位颜色一致，自己保持白色；关闭后名字为白色。仅影响本机，立即生效。", i=>{ Settings.ColoredPlayerNames=i==1; Hkmp.CoopContext.RefreshPlayerVisuals(); }, ()=>Settings.ColoredPlayerNames?1:0),
        new IMenuMod.MenuEntry("动态 Boss 血量（房主）", new[]{"关闭", "开启"}, "默认关闭，房主开房前设置。使用下方增血参数 p，N 人等效血量为 1+p+p²+…+p^(N-1)，最高十倍；60% 时两人160%、三人196%。队友设置不影响房间。", i=>Settings.BossHealthScaling=i==1, ()=>Settings.BossHealthScaling?1:0),
        new IMenuMod.MenuEntry(BossHealthParameterRow.OptionName, new[]{BossHealthParameterRow.Format(Settings.BossExtraHealthPerPlayer)}, "输入百分比，默认60，范围0–400。60 表示 p=0.6；80 时两人180%、三人244%。回车确认，开房前设置。", _=>{}, ()=>0),
        new IMenuMod.MenuEntry("Boss 伤害占比榜（房主）", new[]{"关闭", "开启"}, "默认关闭。实时显示本场 Boss 伤害占比；神居一至五门整门累计，最后结算。由房主开房前决定，F1 隐藏或显示本机榜单，隐藏时继续统计。", i=>Settings.BossDamageRanking=i==1, ()=>Settings.BossDamageRanking?1:0),
        new IMenuMod.MenuEntry("Boss 索敌模式（房主）", new[]{"原版", "灵活"}, "默认原版。灵活模式优先选择同房存活玩家中最近的一人；至少锁定 4 秒，只在空闲或追逐的决策边界换人，新目标须近 25%。攻击生成物继承目标，目标倒地或离房时立即重选。由房主统一设置。", i=>Settings.BossTargetingMode=i==1?1:0, ()=>Settings.BossTargetingMode==1?1:0),
        new IMenuMod.MenuEntry("暗影救援（房主）", new[]{"关闭", "开启"}, "击破暗影后一血复活，救人者获得 33 点灵魂；按 " + Settings.GiveUpKey + " 放弃。Boss房内无人能继续战斗时，本场失败，不等待赶路队友。开房前设置。", i=>Settings.Rescue=i==1, ()=>Settings.Rescue?1:0),
        RescueLimitMenuEntry(),
        new IMenuMod.MenuEntry("救援耗尽后观战（Beta，房主）", new[]{"关闭", "开启"}, "默认关闭。开启时主动放弃救援直接观战；Boss战救援耗尽或关闭暗影救援时，死亡直接观战。Boss胜利后在本场存活队友处一血复活，不扣次数；房内无人能战斗时返回自己的复活点。观战时按 " + Settings.GiveUpKey + " 可提前返回。开房前设置。", i=>Settings.SpectateAfterExhaustion=i==1, ()=>Settings.SpectateAfterExhaustion?1:0),
        new IMenuMod.MenuEntry("Boss击败后解除暗影（房主）", new[]{"关闭", "开启"}, "默认开启。Boss真正击败时，本场暗影玩家在存活队友处一血复活，不消耗救援次数，参与本地结算。需开启暗影救援；观战者的结算复活由观战开关控制。开房前设置。", i=>Settings.BossVictoryRescue=i==1, ()=>Settings.BossVictoryRescue?1:0),
        new IMenuMod.MenuEntry("入场免伤（房主）", new[]{"关闭", "开启"}, "场景、战斗同步和镜头就绪并恢复操作后，再保留 1.5 秒反应时间。", i=>Settings.EntryProtection=i==1, ()=>Settings.EntryProtection?1:0)
    });

    private static IMenuMod.MenuEntry RescueLimitMenuEntry() {
        var counts = new List<int> { 0, 1, 2, 3, 5, 10 };
        var current = Math.Max(0, Math.Min(999, Settings.RescueLimit));
        if (!counts.Contains(current)) counts.Add(current);
        return new IMenuMod.MenuEntry("每人暗影救援次数（房主）", counts.ConvertAll(n => n == 0 ? "不限次数" : n + " 次").ToArray(),
            "每名玩家可被骨钉救起的次数，坐椅子恢复；Boss结算复活不扣次数。消耗时短暂显示剩余次数。死亡返回、换房和重试不恢复。默认不限次数，开房前设置。",
            i => { if (i >= 0 && i < counts.Count) Settings.RescueLimit = counts[i]; }, () => counts.IndexOf(Math.Max(0, Math.Min(999, Settings.RescueLimit))));
    }

    private static IMenuMod.MenuEntry OpacityMenuEntry() {
        var levels = new List<float> { 1, .75f, .5f, .25f, 0 };
        float Current() => float.IsNaN(Settings.OtherPlayerOpacity) ? 1 : Mathf.Clamp01(Settings.OtherPlayerOpacity);
        // Preserve and display existing JSON values rather than snapping them on menu open.
        if (!levels.Contains(Current())) levels.Add(Current());
        var labels = levels.ConvertAll(v => v == 1 ? "关闭（不透明）" : v == 0 ? "完全透明" : $"{v * 100:0.#}% 不透明");
        return new IMenuMod.MenuEntry("队友透明度（本机）", labels.ToArray(),
            "默认关闭（不透明）。调整本机看到的队友身体、刀光、法术和粒子；名字保持可读，不影响自己或其他玩家，立即生效。",
            i=>{ if (i < 0 || i >= levels.Count) return; Settings.OtherPlayerOpacity=levels[i]; Hkmp.CoopContext.RefreshPlayerVisuals(); },
            ()=>Math.Max(0, levels.IndexOf(Current())));
    }
}

public sealed class CoopClientAddon : ClientAddon {
    protected override string Name => "HollowConnect";
    protected override string Version => CoopMod.NetworkVersion;
    public override bool NeedsNetwork => true;
    public override void Initialize(IClientApi api) {
        var go = new GameObject("HollowConnect runtime");
        Object.DontDestroyOnLoad(go);
        go.AddComponent<CoopRuntime>().Initialize(api, this);
        CoopMod.Instance?.Log("HollowConnect client addon ready");
    }
}
