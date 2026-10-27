using System;
using Hkmp.Api.Command.Client;
using Hkmp.Ui;

namespace HollowConnect;

// Client-only command. Recovery is deliberately a two-step operation so a
// mistyped chat command cannot change native control state while playing.
internal sealed class CoopDebugCommand : IClientCommand {
    private readonly CoopRuntime runtime;
    internal CoopDebugCommand(CoopRuntime runtime) { this.runtime = runtime; }
    public string Trigger => "/hc_debug";
    public string[] Aliases => new[] { "/hcdebug" };

    public void Execute(string[] arguments) {
        if (arguments.Length < 2 || string.Equals(arguments[1], "show", StringComparison.OrdinalIgnoreCase)) {
            runtime.ToggleDebugPanel();
            return;
        }
        switch (arguments[1].ToLowerInvariant()) {
            case "export":
                var path = runtime.ExportDebugReport();
                UiManager.InternalChatBox.AddMessage(path.Length == 0 ? "HollowConnect：诊断导出失败，请查看日志" : "HollowConnect：诊断已导出到 " + path);
                break;
            case "recover":
                if (arguments.Length < 3 || !string.Equals(arguments[2], "confirm", StringComparison.OrdinalIgnoreCase)) {
                    UiManager.InternalChatBox.AddMessage("HollowConnect：恢复临时状态请使用 /hc_debug recover confirm（不会修改原版进度）");
                } else {
                    runtime.RecoverTransientState();
                    UiManager.InternalChatBox.AddMessage("HollowConnect：已执行受控临时状态恢复；若仍卡住请导出诊断");
                }
                break;
            default:
                UiManager.InternalChatBox.AddMessage("用法：/hc_debug [show|export|recover confirm]");
                break;
        }
    }
}
