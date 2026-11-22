using System;
using System.Collections;
using System.Linq;
using Hkmp.Ui;
using Hkmp.Ui.Component;
using UnityEngine;
using UnityEngine.UI;

namespace HollowConnect.Smoke;

public sealed partial class RescueProbe {
    private IEnumerator ChatEditingChecks() {
        var chat = UiManager.InternalChatBox;
        var input = Field<ChatInputComponent>(chat, "_chatInput");
        var keyboard = Field<ChatKeyboardField>(input, "_keyboard");
        var ghost = keyboard.transform.GetComponentsInChildren<Text>(true).Single(t => t.name == "Command completion");
        var peer = host ? "旅人玩家" : "小骑士";
        var completed = host ? "/tp 旅人玩家" : "/tp 小骑士";
        var message = host ? "测试聊天 主机" : "测试聊天 客机";
        var chatEvents = 0;
        string lastSent = null;
        chat.ChatInputEvent += text => { chatEvents++; lastSent = text; };
        void EnterText(string text) { keyboard.text = text; keyboard.caretPosition = text.Length; keyboard.ForceLabelUpdate(); }
        void Key(string key) => Check(keyboard.ProcessChatEvent(Event.KeyboardEvent(key)), "Unity chat handles key " + key);
        IEnumerator Open() {
            chat.OpenChatInput();
            return Until(() => keyboard.isFocused, "chat receives text focus", 8);
        }

        yield return Until(() => runtime.Peers.Count == 1 && runtime.Peers.Values.Single().State != null && runtime.WarpEnabled,
            "online teammate is available for chat completion");
        if (System.Environment.GetEnvironmentVariable("HOLLOWCONNECT_RENDERED_TEST") == "1") {
            Screen.SetResolution(1280, 720, false);
            yield return Until(() => Screen.width == 1280 && Screen.height == 720, "1280x720 chat demo window");
        }
        yield return Open();
        EnterText(message);
        Key("return");
        Check(chatEvents == 1 && lastSent == message && !keyboard.isActiveAndEnabled, "Enter submits chat exactly once and closes editor");

        yield return Open();
        EnterText("草稿");
        Key("up");
        Check(keyboard.text == message && keyboard.caretPosition == message.Length && chatEvents == 1,
            "Up recalls chat without sending and leaves caret at end");
        Key("down");
        Check(keyboard.text == "草稿", "Down restores the unsent draft");
        EnterText("/");
        Key("tab");
        var first = keyboard.text;
        Key("tab");
        Check(first == "/addon" && keyboard.text != first && chatEvents == 1, "repeated Tab cycles matching commands");
        EnterText("/t");
        yield return Until(() => ghost.enabled && ghost.text == "p ", "command suffix shows inline", 8);
        Check(ghost.color.a > 0 && ghost.color.a < 1 && !ghost.raycastTarget && ghost.font == keyboard.textComponent.font,
            "completion uses translucent native-font text");
        Key("tab");
        Check(keyboard.text == "/tp " && chatEvents == 1, "Tab accepts the command and adds an argument space");
        yield return Until(() => ghost.enabled && ghost.text == peer, "online Chinese teammate appears as the next inline suggestion", 8);
        keyboard.caretPosition = 1;
        keyboard.ForceLabelUpdate();
        yield return Until(() => !ghost.enabled, "caret inside the command hides the suffix", 8);
        EnterText("/tp " + peer.Substring(0, 1));
        yield return Until(() => ghost.enabled && ghost.text == peer.Substring(1), "partially typed Chinese name shows only its suffix", 8);
        if (System.Environment.GetEnvironmentVariable("HOLLOWCONNECT_RENDERED_TEST") == "1") yield return CaptureStatusHud("聊天补全");
        Key("tab");
        Check(keyboard.text == completed && chatEvents == 1, "Tab inserts the complete target without sending");
        runtime.Selected = runtime.LocalId;
        Key("return");
        Check(chatEvents == 2 && lastSent == completed && !keyboard.isActiveAndEnabled,
            "Enter dispatches the completed teleport command through the live chat");
        yield return Until(() => runtime.Selected == runtime.Peers.Keys.Single(), "completed teleport command resolves the live teammate", 10);

        yield return Open();
        Key("up");
        Check(keyboard.text == completed, "history recalls sent command first");
        Key("up");
        Check(keyboard.text == message, "history recalls earlier chat second");
        Key("escape");
        Check(chatEvents == 2 && !keyboard.isActiveAndEnabled, "Escape closes chat without sending the recalled text");
        yield return Open();
        Check(keyboard.text == "", "reopening chat starts with empty input");
        Key("escape");
    }
}
