using System;
using System.Reflection;
using Modding;
using MonoMod.RuntimeDetour;
using UnityEngine;
using UnitySceneManager = UnityEngine.SceneManagement.SceneManager;

namespace HollowConnect;

public sealed partial class CoopRuntime {
    private Hook benchwarpRespawnHook, benchwarpDoorHook;
    private float benchwarpStartDeadline;
    private int benchwarpOrigin;

    private bool BenchwarpStarting => benchwarpStartDeadline > Time.realtimeSinceStartup &&
        UnitySceneManager.GetActiveScene().handle == benchwarpOrigin;

    private void RegisterBenchwarp() {
        var mod = ModHooks.GetMod("Benchwarp", onlyEnabled: true);
        if (mod == null) return;
        var type = mod.GetType().Assembly.GetType("Benchwarp.ChangeScene");
        const BindingFlags flags = BindingFlags.Public | BindingFlags.Static;
        var respawn = type?.GetMethod("WarpToRespawn", flags, null, Type.EmptyTypes, null);
        var door = type?.GetMethod("ChangeToScene", flags, null, new[] { typeof(string), typeof(string), typeof(float) }, null);
        if (respawn?.ReturnType != typeof(void) || door?.ReturnType != typeof(void)) {
            CoopMod.Instance?.Log("Benchwarp compatibility: unsupported ChangeScene interface");
            return;
        }
        try {
            benchwarpRespawnHook = new Hook(respawn, (Action<Action>)(orig => RunBenchwarp(orig, true)));
            benchwarpDoorHook = new Hook(door, (Action<Action<string, string, float>, string, string, float>)
                ((orig, scene, gate, delay) => RunBenchwarp(() => orig(scene, gate, delay), false)));
        } catch (Exception e) {
            UnregisterBenchwarp();
            CoopMod.Instance?.Log("Benchwarp compatibility: " + e.Message);
        }
    }

    private void RunBenchwarp(Action original, bool respawn) {
        if (api?.NetClient.IsConnected != true) { original(); return; }
        var gm = GameManager.instance;
        var hero = HeroController.instance;
        if (!Connected || !Playable || emergencyBenchActive || BenchwarpStarting || Down || Resetting || Returning ||
            changingScene || Observing || BossSettlementPending || BossSequenceController.IsInSequence ||
            NativeSceneLoad.GetValue(gm) != null || hero.cState.dead || hero.cState.hazardDeath ||
            hero.cState.hazardRespawning || hero.cState.transitioning ||
            hero.transitionState != GlobalEnums.HeroTransitionState.WAITING_TO_TRANSITION) {
            ShowWarpNotice("当前无法使用 Benchwarp，请等切图、救援或挑战结束");
            return;
        }
        // Benchwarp goes through native Respawn, which writes full health
        // directly before the room's linked-rest rules see the player.
        if (respawn && LinkedHealthRuleEnabled) {
            ShowWarpNotice("共享血量房间不支持 Benchwarp 回椅，请正常坐椅");
            return;
        }
        benchwarpOrigin = UnitySceneManager.GetActiveScene().handle;
        // The mod starts with a half-second fade before ReadyForRespawn.
        // Cover that gap until the native scene transition owns the load.
        benchwarpStartDeadline = Time.realtimeSinceStartup + 5f;
        pendingWarp = null;
        try { original(); }
        catch { benchwarpStartDeadline = 0; throw; }
    }

    private void UnregisterBenchwarp() {
        benchwarpDoorHook?.Dispose(); benchwarpDoorHook = null;
        benchwarpRespawnHook?.Dispose(); benchwarpRespawnHook = null;
        benchwarpStartDeadline = 0;
    }
}
