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
        var message = host ? "Hello 测试聊天 主机" : "Hello 测试聊天 客机";
        var chatEvents = 0;
        string lastSent = null;
        chat.ChatInputEvent += text => { chatEvents++; lastSent = text; };
        void EnterText(string text) { keyboard.text = text; keyboard.caretPosition = text.Length; keyboard.ForceLabelUpdate(); }
        void Key(string key) => Check(keyboard.ProcessChatEvent(Event.KeyboardEvent(key)), "Unity chat handles key " + key);
        void CheckVisibleText(Text label, string expected, string context) {
            Canvas.ForceUpdateCanvases();
            var vertices = label.cachedTextGenerator.verts;
            var glyphs = 0;
            for (var i = 0; i + 3 < vertices.Count; i += 4) {
                var diagonal = vertices[i].position - vertices[i + 2].position;
                if (diagonal.x != 0 && diagonal.y != 0 && vertices[i].color.a != 0) glyphs++;
            }
            var details = $"font={label.font.name} rect={label.rectTransform.rect.size} " +
                          $"scale={label.pixelsPerUnit:R} preferredHeight={label.preferredHeight:R} " +
                          $"overflow={label.verticalOverflow} glyphs={glyphs}";
            Log($"chat text render {context}: {details}");
            Check(label.isActiveAndEnabled && !label.canvasRenderer.cull && label.text == expected &&
                  glyphs == expected.Count(ch => !char.IsWhiteSpace(ch)), context + " renders every glyph: " + details);
        }
        IEnumerator Open() {
            chat.OpenChatInput();
            return Until(() => keyboard.isFocused, "chat receives text focus", 8);
        }

        yield return Until(() => runtime.Peers.Count == 1 && runtime.Peers.Values.Single().State != null && runtime.WarpEnabled,
            "online teammate is available for chat completion");
        if (System.Environment.GetEnvironmentVariable("HOLLOWCONNECT_RENDERED_TEST") == "1") {
            Screen.SetResolution(1908, 1035, false);
            yield return Until(() => Screen.width == 1908 && Screen.height == 1035, "1908x1035 chat reproduction window");
        }
        yield return Open();
        foreach (var ch in "Hello") {
            var key = Event.KeyboardEvent(char.ToLowerInvariant(ch).ToString());
            key.character = ch;
            Check(keyboard.ProcessChatEvent(key), "native field processes a typed Latin character");
        }
        Check(keyboard.text == "Hello", "native field receives typed Latin text");
        CheckVisibleText(keyboard.textComponent, "Hello", "typed Latin text");
        EnterText(message);
        if (System.Environment.GetEnvironmentVariable("HOLLOWCONNECT_RENDERED_TEST") == "1") yield return CaptureStatusHud("聊天输入");
        CheckVisibleText(keyboard.textComponent, message, "mixed Latin and Chinese input");
        Key("return");
        Check(chatEvents == 1 && lastSent == message && !keyboard.isActiveAndEnabled, "Enter submits chat exactly once and closes editor");

        yield return Open();
        EnterText("草稿");
        Key("up");
        Check(keyboard.text == message && keyboard.caretPosition == message.Length && chatEvents == 1,
            "Up recalls chat without sending and leaves caret at end");
        CheckVisibleText(keyboard.textComponent, message, "history recall");
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
        CheckVisibleText(keyboard.textComponent, "/tp " + peer.Substring(0, 1), "typed command prefix");
        CheckVisibleText(ghost, peer.Substring(1), "inline command suffix");
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
