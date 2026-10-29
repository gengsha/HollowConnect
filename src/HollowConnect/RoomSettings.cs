namespace HollowConnect;

public sealed partial class CoopRuntime {
    private string ActiveRoomSettingLabel(string name) {
        if (!Connected) return null;
        string Toggle(bool enabled) => enabled ? "开启" : "关闭";
        switch (name) {
            case "共享永久进度（房主）": return Toggle(Shared);
            case "共享护符（房主）": return Toggle(ShareCharms);
            case CoopMod.SharedHealthOptionName: return Toggle(linkedHealthMode);
            case LinkedHealthBonusRow.OptionName: return LinkedHealthBonusRow.Format(linkedHealthBonusMasks);
            case "动态 Boss 血量（房主）": return Toggle(bossHealthScaling);
            case BossHealthParameterRow.OptionName: return BossHealthParameterRow.Format(bossExtraHealth);
            case "Boss 伤害占比榜（房主）": return Toggle(damageRankingEnabled);
            case "Boss 索敌模式（房主）": return flexibleBossTargeting ? "灵活" : "原版";
            case "暗影救援（房主）": return Toggle(RescueEnabled);
            case "每人暗影救援次数（房主）": return RescueLimit == 0 ? "不限次数" : RescueLimit + " 次";
            case "救援耗尽后观战（Beta，房主）": return Toggle(SpectateAfterExhaustion);
            case "Boss击败后解除暗影（房主）": return Toggle(bossVictoryRescue);
            case "入场免伤（房主）": return Toggle(protectEntry);
            default: return null;
        }
    }
}
