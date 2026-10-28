using System;

namespace HollowConnect.Core;

public static class StagStations {
    // The native Remember 3 conversation requires nine original stations.
    // Hidden Station and the Nest are additional destinations, not prerequisites.
    public static readonly string[] Original = { "openedTown", "openedCrossroads", "openedGreenpath",
        "openedFungalWastes", "openedRoyalGardens", "openedRuins1", "openedRuins2", "openedRestingGrounds", "openedDeepnest" };
    public static int Count(Func<string, bool> opened) {
        var count = 0;
        foreach (var key in Original) if (opened(key)) count++;
        return count;
    }
}
