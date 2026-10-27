using System;

namespace Hkmp.Game.Client.Entity;

// Pure arithmetic shared by the runtime and focused boundary checks.
internal static class BossHealthScale {
    internal static double Multiplier(int players, double extra, double decay) {
        if (double.IsNaN(extra) || double.IsInfinity(extra) || double.IsNaN(decay) || double.IsInfinity(decay)) return 1;
        extra = System.Math.Round(System.Math.Max(0, System.Math.Min(4, extra)), 6);
        decay = System.Math.Round(System.Math.Max(0, System.Math.Min(1, decay)), 6);
        var total = 1d;
        for (var i = 1; i < System.Math.Min(256, players); i++) { total += extra; extra *= decay; }
        return System.Math.Min(10, total);
    }
}

internal sealed class BossDamageBudget {
    private double remainder;
    internal int Scale(int damage, double multiplier) {
        if (damage <= 0) return damage;
        if (double.IsNaN(multiplier) || double.IsInfinity(multiplier) || multiplier <= 1) { remainder = 0; return damage; }
        var exact = damage / System.Math.Min(10, multiplier) + remainder;
        var result = (int)System.Math.Floor(exact + 1e-9);
        remainder = System.Math.Max(0, exact - result);
        return result;
    }
}
