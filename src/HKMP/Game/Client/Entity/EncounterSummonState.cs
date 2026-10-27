namespace Hkmp.Game.Client.Entity;

// A request is not an enemy snapshot. Only the scene authority can register
// this capability; the server then retains one native summon receipt per ID.
internal sealed class EncounterSummonState {
    internal const byte Register = 0, Request = 1, Accepted = 2;
    internal bool Registered { get; private set; }
    internal bool Requested { get; private set; }
    internal byte State => Requested ? Accepted : Register;
    internal bool Apply(byte action, bool authority) {
        if (action == Register && authority && !Registered) { Registered = true; return true; }
        if (action != Request || !Registered || Requested) return false;
        Requested = true;
        return true;
    }
}
