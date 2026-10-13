namespace HollowConnect.Core;

// A single scene visit gets a grace period only after all arrival work is ready.
// Losing input later in combat cannot reopen the protection window.
public sealed class EntryProtectionWindow {
    private double readySince = -1;
    public bool Active { get; private set; }
    public void Begin() { Active = true; readySince = -1; }
    public void Clear() { Active = false; readySince = -1; }
    public bool Update(bool ready, double now, double seconds = 1.5) {
        if (!Active) return false;
        if (!ready) { readySince = -1; return true; }
        if (readySince < 0) readySince = now;
        if (now - readySince >= seconds) Active = false;
        return Active;
    }
}
