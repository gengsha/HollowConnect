using System;
using System.Linq;
using HollowConnect.Localization;

internal static class LocalizationTests {
    internal static void Register(Action<string, Action> test) {
        test("localization follows supported codes with English fallback", () => {
            Equal(Translation.Get("局域网联机", "zh-CN"), "局域网联机");
            Equal(Translation.Get("局域网联机", "EN"), "LAN multiplayer");
            Equal(Translation.Get("局域网联机", "ja-JP"), "LANマルチプレイ");
            Equal(Translation.Get("局域网联机", "DE"), "LAN multiplayer");
            Equal(Translation.Get("unknown scene/user text", "JA"), "unknown scene/user text");
        });
        test("localization keeps player names and server errors opaque", () => {
            Equal(Translation.Get("加入 开启关闭 的游戏", "EN"), "Join 开启关闭's game");
            Equal(Translation.Get("加入 開くキャンセル 的游戏", "JA"), "開くキャンセルのゲームに参加");
            Equal(Translation.Get("Steam 房间连接失败：C:\\玩家\\关闭", "EN"), "Steam room connection failed: C:\\玩家\\关闭");
            Equal(Translation.Get("下一页（2/3）", "JA"), "次のページ（2/3）");
        });
        test("localization keeps room values and locked numeric labels readable", () => {
            Equal(Translation.Get("开启（已锁定）", "EN"), "On (locked)");
            Equal(Translation.Get("12 次（已锁定）", "JA"), "12回（ロック中）");
            Equal(Translation.Get("60%（已锁定）", "EN"), "60% (locked)");
            Equal(Translation.Get("35% 不透明", "JA"), "不透明度35%");
        });
        test("localization catalog is complete and preserves runtime key bindings", () => {
            if (Translation.Sources.Count() < 180) throw new Exception("Incomplete catalog");
            foreach (var source in Translation.Sources) foreach (var code in new[] { "EN", "JA", "ZH" }) {
                if (string.IsNullOrEmpty(Translation.Get(source, code))) throw new Exception("Empty translation: " + source);
            }
            var text = "击破暗影后一血复活，救人者获得 33 点灵魂；按 F6 放弃。Boss房内无人能继续战斗时，本场失败，不等待赶路队友。开房前设置。";
            var english = Translation.Help(text, "EN");
            if (!english.Contains("F6") || english.Any(c => c >= '\u4e00' && c <= '\u9fff')) throw new Exception("Menu help did not preserve/localize bindings");
        });
    }
    private static void Equal(string actual, string expected) { if (actual != expected) throw new Exception(actual + " != " + expected); }
}
