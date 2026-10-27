using System;
using Hkmp.Game.Client.Entity;
using UnityEngine;

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
        On.PlayVibration.Play += PlayVibration;
        On.StopVibration.OnEnter += StopVibration;
        On.NailSlash.OnTriggerEnter2D += NailContact;
    }

    internal static void Unregister() {
        On.VibrationEffect.OnEnable -= VibrationEffectEnable;
        On.VibrationPlayer.Play -= VibrationPlayerPlay;
        On.PlayVibration.Play -= PlayVibration;
        On.StopVibration.OnEnter -= StopVibration;
        On.NailSlash.OnTriggerEnter2D -= NailContact;
    }

    private static void VibrationEffectEnable(On.VibrationEffect.orig_OnEnable orig, VibrationEffect self) {
        if (!IsRemote(self.gameObject)) orig(self);
    }

    private static void VibrationPlayerPlay(On.VibrationPlayer.orig_Play orig, VibrationPlayer self) {
        if (!IsRemote(self.gameObject)) orig(self);
    }

    private static void PlayVibration(On.PlayVibration.orig_Play orig, global::PlayVibration action, bool looping) {
        if (!IsRemote(action.Owner)) orig(action, looping);
    }

    private static void StopVibration(On.StopVibration.orig_OnEnter orig, global::StopVibration action) {
        if (IsRemote(action.Owner)) action.Finish();
        else orig(action);
    }

    private static void NailContact(On.NailSlash.orig_OnTriggerEnter2D orig, NailSlash self, Collider2D collider) {
        // Destroy in SlashBase is deferred. Until it completes, a cloned
        // NailSlash can recoil its cached local HeroController on wall contact.
        // The separate damages_enemy FSM still handles remote attack damage.
        if (!IsRemote(self.gameObject)) orig(self, collider);
    }
}
