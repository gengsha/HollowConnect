using System;

namespace Hkmp.Game.Client.Entity;

// Pure arithmetic shared by the runtime and focused boundary checks.
internal static class BossHealthScale {
    internal static double Multiplier(int players, double parameter) {
        if (double.IsNaN(parameter) || double.IsInfinity(parameter)) return 1;
        parameter = System.Math.Round(System.Math.Max(0, System.Math.Min(4, parameter)), 6);
        var total = 1d; var term = parameter;
        for (var i = 1; i < System.Math.Min(256, players); i++) {
            total += term;
            if (total >= 10) return 10;
            term *= parameter;
        }
        return total;
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
