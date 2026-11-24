using System;
using System.Reflection;
using Modding;

namespace HollowConnect;

public sealed partial class CoopRuntime {
    private const string QolCutsceneModule = "SkipCutscenes";
    private const string QolDreamerSkip = "DreamersGet";
    private Action<string, string> restoreQolSetting;

    private void SuspendQolDreamerSkip() {
        if (restoreQolSetting != null) return;
        var qol = ModHooks.GetMod("QoL", onlyEnabled: true);
        var settings = qol?.GetType().Assembly.GetType("QoL.SettingsOverride");
        if (settings == null) return;
        const BindingFlags flags = BindingFlags.Public | BindingFlags.Static;
        var suspend = settings.GetMethod("OverrideSettingToggle", flags, null,
            new[] { typeof(string), typeof(string), typeof(bool) }, null);
        var restore = settings.GetMethod("RemoveSettingOverride", flags, null,
            new[] { typeof(string), typeof(string) }, null);
        if (suspend?.ReturnType != typeof(void) || restore?.ReturnType != typeof(void)) return;
        try {
            var apply = (Action<string, string, bool>)Delegate.CreateDelegate(typeof(Action<string, string, bool>), suspend);
            var remove = (Action<string, string>)Delegate.CreateDelegate(typeof(Action<string, string>), restore);
            // QoL's Dreamer shortcut replaces the native destination and writes
            // the count and full health directly. Keep native absorption in a room.
            apply(QolCutsceneModule, QolDreamerSkip, false);
            restoreQolSetting = remove;
        } catch (Exception e) {
            CoopMod.Instance?.Log("QoL Dreamer compatibility: " + e.Message);
        }
    }

    private void RestoreQolDreamerSkip() {
        var restore = restoreQolSetting;
        restoreQolSetting = null;
        restore?.Invoke(QolCutsceneModule, QolDreamerSkip);
    }
}
