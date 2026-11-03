using System;
using System.Collections;
using System.Linq;
using Hkmp.Game;
using Hkmp.Ui;
using Modding;
using UnityEngine.EventSystems;

namespace HollowConnect.Smoke;

public sealed partial class JoinProbe {
    private IEnumerator CheckExternalGateway() {
        yield return Until(() => ExternalConnectionGateway.Current != null && ExternalConnectionGateway.AtTitle,
            "external connection gateway waits for native title");
        if (Has(role + ".failed")) yield break;
        var gateway = ExternalConnectionGateway.Current;
        var ui = Field<UiManager>(engine.ClientManager, "_uiManager");
        var settings = Field<Hkmp.Game.Settings.ModSettings>(engine.ClientManager, "_modSettings");
        var address = settings.ConnectAddress; var port = settings.ConnectPort; var username = settings.Username;
        var lease = new object(); var stranger = new object();
        JoinCheck(gateway.Reserve(lease) && !gateway.Reserve(stranger), "Steam lease excludes concurrent connection ownership");
        var completed = false; var selected = true;
        gateway.SelectHostSave(lease, value => { completed = true; selected = value; });
        yield return Until(() => UIManager.instance.menuState == GlobalEnums.MainMenuState.SAVE_PROFILES &&
            InputHandler.Instance.acceptingInput, "Steam host uses native save selection");
        if (Has(role + ".failed")) { gateway.Release(lease); yield break; }
        var trigger = Field<EventTrigger>(ui, "_saveMenuBackTrigger");
        JoinCheck(trigger != null && trigger.triggers.Count > 0, "native save back trigger available");
        if (Has(role + ".failed")) { gateway.Release(lease); yield break; }
        ExecuteEvents.Execute(trigger.gameObject, new BaseEventData(EventSystem.current), ExecuteEvents.submitHandler);
        yield return Until(() => completed, "native back cancels Steam host callback");
        JoinCheck(!selected && !gateway.ClientActive && !gateway.ServerActive, "cancelled Steam host starts no game server");
        gateway.Release(stranger);
        JoinCheck(!gateway.IsIdle, "foreign cleanup cannot release Steam lease");
        gateway.Release(lease);
        yield return Until(() => gateway.IsIdle && InputHandler.Instance.acceptingInput,
            "Steam cancellation restores connection menu and releases lease");
        var second = new object();
        JoinCheck(gateway.Reserve(second), "Steam can reserve again after cancellation");
        gateway.SelectHostSave(second, _ => { throw new Exception("Cancelled selection callback ran"); });
        yield return Until(() => UIManager.instance.menuState == GlobalEnums.MainMenuState.SAVE_PROFILES &&
            InputHandler.Instance.acceptingInput, "second native save selection opened");
        gateway.Release(second);
        JoinCheck(!ui.HasSaveSelection && gateway.IsIdle, "explicit Steam cancellation removes only owned selection hooks");
        // Restore menu through the native UI before the ordinary join regression.
        UIManager.instance.UIGoToMainMenu();
        yield return Until(() => InputHandler.Instance.acceptingInput, "native menu input restored");
        JoinCheck(settings.ConnectAddress == address && settings.ConnectPort == port && settings.Username == username,
            "Steam menu and cancellation preserve direct connection history");
        JoinCheck(ModHooks.GetMod("HollowConnect Steam") != null,
            "optional Steam module loads alongside original IP mode");
    }
}
