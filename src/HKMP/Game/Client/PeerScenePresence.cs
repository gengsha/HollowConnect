namespace Hkmp.Game.Client;

// Reject a teammate's old enter/leave after their new visit to the same room.
internal sealed class PeerScenePresence {
    internal uint Visit { get; private set; }
    internal bool Present { get; private set; }
    private bool known;
    internal bool CanEnter(uint visit) => !known || visit > Visit;
    internal bool Enter(uint visit) {
        if (!CanEnter(visit)) return false;
        known = Present = true; Visit = visit; return true;
    }
    internal bool Leave(uint visit) {
        if (known && visit < Visit) return false;
        var remove = Present; known = true; Present = false; Visit = visit; return remove;
    }
    internal void Reset() { known = Present = false; Visit = 0; }
}
