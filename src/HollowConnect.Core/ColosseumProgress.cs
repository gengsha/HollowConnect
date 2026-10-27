using System;

namespace HollowConnect.Core;

public static class ColosseumProgress {
    // Completion proves this trial's registration was already paid. It does
    // not pay the next trial's fee or create another completion/reward.
    public static int RepairRegistrations(Func<string, bool> read, Action<string> open) {
        var repaired = 0;
        foreach (var trial in new[] { "Bronze", "Silver", "Gold" }) {
            var prefix = "colosseum" + trial;
            if (!read(prefix + "Completed") || read(prefix + "Opened")) continue;
            open(prefix + "Opened"); repaired++;
        }
        return repaired;
    }
}
