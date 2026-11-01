using System;
using Hkmp.Util;
using Logger = Hkmp.Logging.Logger;

namespace Hkmp.Ui;

internal partial class UiManager {
    private readonly Func<bool> _isHosting;

    private void InitializeEndings() {
        UnityEngine.SceneManagement.SceneManager.activeSceneChanged += (_, scene) => {
            if (!SceneUtil.IsEndingCinematic(scene.name) || !_netClient.IsConnected || _isHosting()) return;
            // Disconnect only tears down the session. The native cinematic
            // retains its player, scene load, credits and save/title route.
            Logger.Info("Ending cinematic started; leaving room without interrupting the ending");
            RequestClientDisconnectEvent?.Invoke();
        };
        On.CinematicPlayer.FinishVideo += (orig, cinematic) => {
            if (SceneUtil.IsEndingCinematic(cinematic.gameObject.scene.name) && _isHosting()) {
                Logger.Info("Host ending cinematic finished; closing room");
                // Disconnect the local client before shutting down its server,
                // so its own shutdown notification cannot replace the ending.
                RequestClientDisconnectEvent?.Invoke();
                RequestServerStopHostEvent?.Invoke();
            }
            orig(cinematic);
        };
    }
}
