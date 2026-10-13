using System;

namespace Hkmp.Game.Client.Entity.Action;

// Native targets may disappear before network state exits. Once cancelled,
// reentrant or repeated room cleanup must not execute the exit callback again.
internal sealed class ActionLifetime {
    private bool finished;
    internal void Finish(System.Action stop, System.Action exit) {
        if (finished) return;
        finished = true;
        try { stop?.Invoke(); }
        finally { exit?.Invoke(); }
    }
}
