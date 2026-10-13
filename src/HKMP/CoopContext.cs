#pragma warning disable CS1591
using System;

namespace Hkmp;

// Shared by the companion addon and the engine. Every state packet belongs to a
// particular party attempt and scene visit; retries must never accept old data.
public static class CoopContext {
    public static uint Epoch;
    public static uint LocalVisit;
    public static string LocalScene = "";
    public static bool Resetting;
    public static Func<string> CaptureBossContext;
    public static Func<string, bool> ApplyBossContext;
    public static Func<bool> BossTransitionReady;
    public static Func<bool> LocalRescueDown;
    public static void EnterScene(string scene) {
        LocalScene = scene;
        unchecked { LocalVisit++; }
    }
}
