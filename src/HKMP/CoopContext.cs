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
    public static Func<bool> HostLocalWorldOnly;
    public static Func<bool> RoomSettingsLocked;
    public static bool LocalWorldOnly;
    public static Func<string> CaptureBossContext;
    public static Func<string, bool> ApplyBossContext;
    public static Action CommitBossContext;
    public static Action RestoreBossContext;
    public static Func<bool> BossTransitionReady;
    public static Action AwaitBossSettlement;
    public static Action ApplyBossEntityComplete;
    public static Func<bool> LocalRescueDown;
    public static Func<UnityEngine.GameObject> LivingBossTarget;
    public static Func<bool> FlexibleBossTargetingOn;
    public static Func<string> BossTargetSession;
    public static Func<UnityEngine.GameObject, int, bool, int> ChooseBossTarget;
    public static Func<int, UnityEngine.GameObject> BossTargetObject;
    public static Func<UnityEngine.GameObject, int> AttackSourcePlayer;
    public static Func<float> RemotePlayerOpacity;
    public static Func<ushort, UnityEngine.Color> PlayerNameColor;
    public static event Action PlayerVisualSettingsChanged;
    public static void RefreshPlayerVisuals() => PlayerVisualSettingsChanged?.Invoke();
    public static Func<double> BossHealthMultiplier;
    public static Action<ulong> BossDefeated;
    public static Action BossEncounter;
    public static Action<int, UnityEngine.GameObject> BossDamage;
    public static Func<string, bool> FinishRadianceIntro;
    public static void EnterScene(string scene) {
        LocalScene = scene;
        unchecked { LocalVisit++; }
    }
}
