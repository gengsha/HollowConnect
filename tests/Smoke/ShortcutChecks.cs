using System;
using System.Collections;
using System.Linq;
using System.Reflection;
using HollowConnect;
using HollowConnect.Core;
using HollowConnect.Steam;
using MonoMod.RuntimeDetour;
using UnityEngine;

namespace HollowConnect.Smoke;

public sealed partial class ProgressProbe {
    private bool ShortcutCase => Environment.GetEnvironmentVariable("HOLLOWCONNECT_SHORTCUT_UI") == "1";

    private IEnumerator TapShortcut(KeyCode code) {
        var previous = runtime.KeyPressed;
        var sent = false;
        runtime.KeyPressed = key => key == code && !sent && (sent = true);
        try {
            yield return Until(() => sent, "runtime polls " + code, 4);
        } finally {
            runtime.KeyPressed = previous;
        }
    }

    private IEnumerator ShortcutChecks() {
        yield return Until(() => runtime.Peers.Count == 1 && runtime.Peers.Single().Value.State != null &&
            engine.ClientManager.SceneSnapshotReady, "shortcut peers and scene ready");
        var peer = runtime.Peers.Single().Key;
        runtime.Selected = runtime.LocalId;
        yield return TapShortcut(KeyCode.F7);
        ResourceCheck(runtime.Selected == peer, "F7 selects the remote teammate");
        ResourceCheck(TeammateTeleportCommandProbe.ExecuteText(engine.ClientManager, "/tp \"missing teammate\""),
            "chat /tp command is registered and consumed locally");

        ResourceCheck(runtime.damageRankingEnabled, "host enabled the boss damage board");
        runtime.damageRanking = new DamageRankingSnapshot { Board = 1, Players = new[] {
            new DamageStanding { Id = runtime.LocalId, Name = "shortcut test", Damage = 17 }
        }};
        Canvas Board() => UnityEngine.Object.FindObjectsOfType<Canvas>(true).SingleOrDefault(c => c.name == "HollowConnect Damage Ranking");
        yield return Until(() => Board() is { isActiveAndEnabled: true }, "damage board renders");
        yield return TapShortcut(KeyCode.F1);
        ResourceCheck(runtime.damageRankingHidden && !Board().gameObject.activeSelf, "F1 hides the damage board");
        yield return TapShortcut(KeyCode.F1);
        ResourceCheck(!runtime.damageRankingHidden && Board().gameObject.activeSelf && runtime.damageRanking.Total == 17,
            "F1 restores the damage board and statistics");

        CoopMod.Statistics.Increment("shortcut test");
        var lastBoardFrame = -1;
        var lastDiagnosticFrame = -1;
        var layoutLabel = typeof(Hkmp.Ui.Localize).GetMethod("LayoutLabel", BindingFlags.Static | BindingFlags.Public);
        using (new Hook(layoutLabel, (Action<Action<string, GUIStyle>, string, GUIStyle>)((orig, label, style) => {
            orig(label, style);
            if (label == "本存档累计死亡榜 · 倒地计一次" && Event.current?.type == EventType.Repaint)
                lastBoardFrame = Time.frameCount;
            if (label == "HollowConnect 诊断（只读；F9 开关）" && Event.current?.type == EventType.Repaint)
                lastDiagnosticFrame = Time.frameCount;
        }))) {
            yield return TapShortcut(KeyCode.F9);
            yield return Until(() => Field<bool>(runtime, "debugPanel") && lastDiagnosticFrame >= 0, "F9 opens and renders diagnostics", 8);
            yield return TapShortcut(KeyCode.F9);
            yield return Until(() => !Field<bool>(runtime, "debugPanel") && Time.frameCount > lastDiagnosticFrame + 2,
                "F9 closes diagnostics", 8);
            Flag(role + ".f5-ready");
            yield return Until(() => lastBoardFrame >= 0, "F5 draws the death board on a real repaint", 160);
            yield return Until(() => !Input.GetKey(KeyCode.F5) && Time.frameCount > lastBoardFrame + 2,
                "F5 release removes the death board", 10);
        }

        var steam = UnityEngine.Object.FindObjectOfType<SteamRoomRuntime>();
        ResourceCheck(steam != null, "optional Steam module is attached");
        Flag(role + ".f10-ready");
        yield return Until(() => steam.Visible && InputHandler.Instance.acceptingInput, "F10 opens the Steam menu", 140);
        Flag(role + ".f10-open");
        yield return Until(() => !steam.Visible && GameManager.instance.isPaused && !UIManager.instance.IsAnimatingMenus &&
            InputHandler.Instance.acceptingInput, "F10 closes the Steam menu back to native pause", 120);
        UIManager.instance.TogglePauseGame();
        yield return Until(() => !GameManager.instance.isPaused && runtime.Connected, "native resume preserves the room", 10);

        var chat = typeof(Hkmp.Ui.UiManager).GetField("InternalChatBox", BindingFlags.Static | BindingFlags.NonPublic)?.GetValue(null);
        ResourceCheck(chat != null, "HKMP chat is attached");
        Flag(role + ".chat-ready");
        yield return Until(() => Field<bool>(chat, "_isOpen"), "T opens the HKMP chat input", 120);
        Flag(role + ".chat-open");
        yield return Until(() => !Field<bool>(chat, "_isOpen") && runtime.Connected, "Escape closes chat while staying connected", 120);
        yield return ResourceBarrier("shortcut-ui-complete", 100);
    }
}
