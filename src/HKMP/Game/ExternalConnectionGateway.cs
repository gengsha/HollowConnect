using System;
using GlobalEnums;
using Hkmp.Game.Settings;
using Hkmp.Game.Client;
using Hkmp.Networking.Client;
using Hkmp.Networking.Server;
using Hkmp.Ui;

namespace Hkmp.Game;

// Optional transports use the same UI/client/server path as direct UDP. No
// platform API is referenced by HKMP, and a lease cannot retire another session.
internal sealed class ExternalConnectionGateway {
    internal static ExternalConnectionGateway Current;
    private readonly UiManager ui;
    private readonly NetClient client;
    private readonly NetServer server;
    private readonly ModSettings settings;
    private readonly PauseManager pauseManager;
    private object owner;
    private Action<bool> selection;
    private int serverGeneration, clientGeneration;
    private bool ownsServer, ownsClient;

    internal ExternalConnectionGateway(UiManager ui, NetClient client, NetServer server, ModSettings settings, PauseManager pauseManager) {
        this.ui = ui; this.client = client; this.server = server; this.settings = settings;
        this.pauseManager = pauseManager;
        ui.ExternalConnectionReserved = () => owner != null;
        ui.SetExternalReservationCheck(() => owner != null);
    }

    internal static bool AtTitle => global::GameManager.instance != null &&
        global::GameManager.instance.gameState == GameState.MAIN_MENU &&
        !global::GameManager.instance.IsInSceneTransition &&
        UnityEngine.SceneManagement.SceneManager.GetActiveScene().name == "Menu_Title";
    internal bool IsIdle => owner == null && !server.IsStarted &&
        client.ConnectionStatus == ClientConnectionStatus.NotConnected && !ui.HasSaveSelection;
    internal string Username => settings.Username;
    internal bool ClientActive => client.ConnectionStatus != ClientConnectionStatus.NotConnected;
    internal bool ServerActive => server.IsStarted;
    internal bool IsConnecting => client.ConnectionStatus == ClientConnectionStatus.Connecting;
    internal bool IsConnected => client.IsConnected;
    internal void AddMenuEntry(Action open) => ui.AddExternalMenuEntry(open);
    internal void SetMenu(MenuScreen menu) => ui.SetExternalMenu(menu);
    internal void ShowMenu(MenuScreen menu) => ui.ShowExternalMenu(menu);
    internal void ShowInGameMenu(MenuScreen menu) => pauseManager.OpenDynamicMenu(menu);

    internal bool Reserve(object candidate) {
        if (candidate == null || !IsIdle || !AtTitle) return false;
        owner = candidate; return true;
    }

    private void RequireOwner(object candidate) {
        if (candidate == null || !ReferenceEquals(owner, candidate)) throw new InvalidOperationException("External connection lease expired");
    }

    internal void SelectHostSave(object candidate, Action<bool> completed) {
        RequireOwner(candidate);
        selection = selected => { selection = null; if (ReferenceEquals(owner, candidate)) completed(selected); };
        ui.OpenExternalSaveSlotSelection(selection);
    }

    internal int StartHost(object candidate) {
        RequireOwner(candidate);
        if (server.IsStarted || ClientActive) throw new InvalidOperationException("Connection already in use");
        var before = server.Generation;
        try { ui.StartExternalHost(); }
        finally { ownsServer = server.Generation != before && server.IsStarted; serverGeneration = server.Generation; }
        return server.BoundPort;
    }

    internal void Connect(object candidate, int port, string username, bool hosting) {
        RequireOwner(candidate);
        if (ClientActive) throw new InvalidOperationException("Connection already in use");
        var before = client.SessionVersion;
        try { ui.ConnectExternal(port, username, hosting); }
        finally { ownsClient = client.SessionVersion != before; clientGeneration = client.SessionVersion; }
    }

    internal void Release(object candidate) {
        if (!ReferenceEquals(owner, candidate)) return;
        // Clear only our host-selection hooks. Guest selection belongs to the
        // ordinary client's disconnect path, including its save confirmation.
        try {
            if (selection != null) ui.CancelExternalSelection(selection);
            if (ownsClient && client.SessionVersion == clientGeneration) ui.DisconnectExternal();
        } finally {
            try { if (ownsServer && server.Generation == serverGeneration) ui.StopExternalHost(); }
            finally { selection = null; ownsClient = ownsServer = false; owner = null; }
        }
    }
}
