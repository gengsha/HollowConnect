namespace Hkmp.Game.Client;

// A fresh game's hero can report its position before activeSceneChanged.
// Publish its scene visit only after entities and remote players are prepared.
internal sealed class SceneEntryGate {
    private int? prepared, pending;

    public bool Request(int sceneHandle) {
        if (prepared == sceneHandle) return true;
        pending = sceneHandle;
        return false;
    }

    public bool Prepare(int sceneHandle) {
        prepared = sceneHandle;
        var publish = pending == sceneHandle;
        pending = null;
        return publish;
    }

    public void Reset() { prepared = pending = null; }
}
