using System;
using Hkmp.Game.Client.Entity;
using UnityEngine;
#if HOLLOW_UNITY6
using OnPlayVibration = On.HutongGames.PlayMaker.Actions.PlayVibration;
using OnStopVibration = On.HutongGames.PlayMaker.Actions.StopVibration;
using PlayVibrationAction = HutongGames.PlayMaker.Actions.PlayVibration;
using StopVibrationAction = HutongGames.PlayMaker.Actions.StopVibration;
#else
using OnPlayVibration = On.PlayVibration;
using OnStopVibration = On.StopVibration;
using PlayVibrationAction = PlayVibration;
using StopVibrationAction = StopVibration;
#endif

namespace Hkmp.Animation;

// Cloned native effects still address the single local controller. Suppress
// their feedback at its source, including OnEnable during Instantiate before
// the clone can receive its remote marker.
internal static class RemotePlayerFeedback {
    private static int creationDepth;

    internal readonly struct CreationScope : IDisposable {
        internal CreationScope(bool begin) { creationDepth++; }
        public void Dispose() { creationDepth--; }
    }

    internal static CreationScope BeginCreation() => new(true);
    private static bool IsRemote(GameObject owner) => creationDepth > 0 || LocalWorldIsolation.IsRemote(owner);

    internal static void Register() {
        On.VibrationEffect.OnEnable += VibrationEffectEnable;
        On.VibrationPlayer.Play += VibrationPlayerPlay;
        OnPlayVibration.Play += PlayVibration;
        OnStopVibration.OnEnter += StopVibration;
#if HOLLOW_UNITY6
        On.HutongGames.PlayMaker.Actions.PlayVibrationV2.OnEnter += PlayVibrationV2;
        On.HutongGames.PlayMaker.Actions.PlayVibrationV3.OnEnter += PlayVibrationV3;
        On.HutongGames.PlayMaker.Actions.StopVibrationV2.OnEnter += StopVibrationV2;
#endif
        On.NailSlash.OnTriggerEnter2D += NailContact;
    }

    internal static void Unregister() {
        On.VibrationEffect.OnEnable -= VibrationEffectEnable;
        On.VibrationPlayer.Play -= VibrationPlayerPlay;
        OnPlayVibration.Play -= PlayVibration;
        OnStopVibration.OnEnter -= StopVibration;
#if HOLLOW_UNITY6
        On.HutongGames.PlayMaker.Actions.PlayVibrationV2.OnEnter -= PlayVibrationV2;
        On.HutongGames.PlayMaker.Actions.PlayVibrationV3.OnEnter -= PlayVibrationV3;
        On.HutongGames.PlayMaker.Actions.StopVibrationV2.OnEnter -= StopVibrationV2;
#endif
        On.NailSlash.OnTriggerEnter2D -= NailContact;
    }

    private static void VibrationEffectEnable(On.VibrationEffect.orig_OnEnable orig, VibrationEffect self) {
        if (!IsRemote(self.gameObject)) orig(self);
    }

    private static void VibrationPlayerPlay(On.VibrationPlayer.orig_Play orig, VibrationPlayer self) {
        if (!IsRemote(self.gameObject)) orig(self);
    }

    private static void PlayVibration(OnPlayVibration.orig_Play orig, PlayVibrationAction action, bool looping) {
        if (!IsRemote(action.Owner)) orig(action, looping);
    }

    private static void StopVibration(OnStopVibration.orig_OnEnter orig, StopVibrationAction action) {
        if (IsRemote(action.Owner)) action.Finish();
        else orig(action);
    }
#if HOLLOW_UNITY6
    private static void PlayVibrationV2(On.HutongGames.PlayMaker.Actions.PlayVibrationV2.orig_OnEnter orig,
        HutongGames.PlayMaker.Actions.PlayVibrationV2 action) {
        if (IsRemote(action.Owner)) action.Finish(); else orig(action);
    }

    private static void PlayVibrationV3(On.HutongGames.PlayMaker.Actions.PlayVibrationV3.orig_OnEnter orig,
        HutongGames.PlayMaker.Actions.PlayVibrationV3 action) {
        if (IsRemote(action.Owner)) action.Finish(); else orig(action);
    }

    private static void StopVibrationV2(On.HutongGames.PlayMaker.Actions.StopVibrationV2.orig_OnEnter orig,
        HutongGames.PlayMaker.Actions.StopVibrationV2 action) {
        if (IsRemote(action.Owner)) action.Finish(); else orig(action);
    }
#endif

    private static void NailContact(On.NailSlash.orig_OnTriggerEnter2D orig, NailSlash self, Collider2D collider) {
        // Destroy in SlashBase is deferred. Until it completes, a cloned
        // NailSlash can recoil its cached local HeroController on wall contact.
        // The separate damages_enemy FSM still handles remote attack damage.
        if (!IsRemote(self.gameObject)) orig(self, collider);
    }
}
