using System.Collections.Generic;
using Hkmp.Api.Command.Client;
using Hkmp.Game.Command.Client;
using Hkmp.Ui;

namespace HollowConnect;

internal sealed class TeammateTeleportCommand : IClientCommand, IClientCommandCompletion {
    private readonly CoopRuntime runtime;
    internal TeammateTeleportCommand(CoopRuntime runtime) { this.runtime = runtime; }
    public string Trigger => "/tp";
    public string[] Aliases => new[] { "/teleport" };

    public IEnumerable<string> CompleteFirstArgument() => runtime.TeammateWarpCompletions();

    public void Execute(string[] arguments) {
        var target = arguments.Length > 1 ? arguments[1] : "";
        if (arguments.Length > 2) {
            UiManager.InternalChatBox.AddMessage("用法：/tp <队友名>；名字含空格时加引号");
            return;
        }
        UiManager.InternalChatBox.AddMessage(runtime.RequestTeammateWarp(target));
    }
}
