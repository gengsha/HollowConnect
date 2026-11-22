using System.Reflection;
using Hkmp.Game.Client;
using Hkmp.Ui;
using Hkmp.Ui.Component;
using UnityEngine.UI;

namespace HollowConnect.Smoke;

internal static class TeammateTeleportCommandProbe {
    internal static bool Execute(ClientManager client, ushort targetId) {
        var player = client.GetPlayer(targetId);
        if (player == null) return false;
        return ExecuteText(client, "/tp \"" + player.Username.Replace("\"", "") + "\"");
    }

    internal static bool ExecuteText(ClientManager client, string command) {
        const BindingFlags fields = BindingFlags.Instance | BindingFlags.NonPublic;
        var chat = UiManager.InternalChatBox;
        var input = (ChatInputComponent)chat.GetType().GetField("_chatInput", fields).GetValue(chat);
        var field = (InputField)typeof(InputComponent).GetField("InputField", fields).GetValue(input);
        var messages = (System.Array)chat.GetType().GetField("_messages", fields).GetValue(chat);
        var previous = messages.GetValue(0);
        field.text = command;
        input.Submit();
        return field.text.Length == 0 && !ReferenceEquals(messages.GetValue(0), previous);
    }
}
