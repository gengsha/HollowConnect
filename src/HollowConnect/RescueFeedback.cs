using System.Linq;
using HutongGames.PlayMaker.Actions;
using UnityEngine;
using Object = UnityEngine.Object;

namespace HollowConnect;

internal static class RescueFeedback {
    // Native PlayerData.focusMP_amount is 33 (Quick/Deep Focus change time or
    // masks healed, not the cost). Use the native API for orb/reserve/bindings.
    internal const int SoulReward = 33;
    private static GameObject nativeDeath;

    internal static void Play(Vector3 position) {
        if (nativeDeath == null) nativeDeath = Resources.FindObjectsOfTypeAll<GameObject>()
            .FirstOrDefault(g => g.name == "Hollow Shade Death" && g.GetComponent<tk2dSpriteAnimator>() != null);
        if (nativeDeath == null) return;
        // InitData deserializes asset references without running any state.
        // The native death FSM also recovers geo and clears shadeScene; none of
        // those actions, events, scripts or colliders may run on a rescue.
        var control = nativeDeath.GetComponents<PlayMakerFSM>().FirstOrDefault(f => f.FsmName == "Shade Control");
        control?.Fsm.InitData();
        var sounds = control?.FsmStates.FirstOrDefault(s => s.Name == "Death Start")?.Actions
            .OfType<AudioPlayerOneShotSingle>().ToArray();
        var staging = new GameObject("HollowConnect rescue staging"); staging.SetActive(false);
        var go = Object.Instantiate(nativeDeath, staging.transform, false);
        go.name = "HollowConnect rescue break"; go.SetActive(false);
        foreach (var script in go.GetComponentsInChildren<MonoBehaviour>(true)) {
            if (script is tk2dBaseSprite) continue;
            if (script is tk2dSpriteAnimator animator) {
                animator.enabled = false; animator.playAutomatically = false;
                animator.AnimationCompleted = null; animator.AnimationEventTriggered = null;
                continue;
            }
            // Disabled scripts can still Awake on activation. Remove only
            // components on this inactive visual clone before activating it.
            script.enabled = false; Object.DestroyImmediate(script);
        }
        foreach (var collider in go.GetComponentsInChildren<Collider2D>(true)) { collider.enabled = false; Object.DestroyImmediate(collider); }
        foreach (var body in go.GetComponentsInChildren<Rigidbody2D>(true)) { body.simulated = false; Object.DestroyImmediate(body); }
        foreach (var audio in go.GetComponentsInChildren<AudioSource>(true)) { audio.enabled = false; Object.DestroyImmediate(audio); }
        foreach (var particle in go.GetComponentsInChildren<ParticleSystem>(true)) {
            particle.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);
            var main = particle.main; main.loop = false; main.playOnAwake = false;
            main.useUnscaledTime = true; main.maxParticles = 96;
            var emission = particle.emission; emission.enabled = false;
        }
        go.transform.SetParent(null, false); go.transform.position = position;
        foreach (var t in go.GetComponentsInChildren<Transform>(true)) t.gameObject.layer = (int)GlobalEnums.PhysLayers.DEFAULT;
        go.SetActive(true);
        go.AddComponent<RescueBreakEffect>();
        if (sounds != null) foreach (var sound in sounds) {
            if (sound.audioClip.Value == null) continue;
            var source = go.AddComponent<AudioSource>(); source.playOnAwake = false;
            source.outputAudioMixerGroup = sound.audioPlayer.Value?.GetComponent<AudioSource>()?.outputAudioMixerGroup;
            source.clip = sound.audioClip.Value as AudioClip; source.volume = sound.volume.Value;
            source.pitch = sound.pitchMin.Value; source.Play();
        }
        Object.Destroy(staging);
    }
}

// Run the original visual sequence in real time, independently of gameplay
// pause/hit-stop. Native gameplay scripts are removed before activation.
internal sealed class RescueBreakEffect : MonoBehaviour {
    private float started;
    private bool exploded;
    private tk2dSpriteAnimator[] animators;
    private void Awake() {
        started = Time.realtimeSinceStartup;
        animators = GetComponentsInChildren<tk2dSpriteAnimator>(true);
        GetComponent<tk2dSpriteAnimator>()?.Play("Death Start");
        var p = transform.Find("Shade Particles")?.GetComponent<ParticleSystem>(); p?.Emit(10);
    }
    private void Update() {
        var elapsed = Time.realtimeSinceStartup - started;
        if (!exploded && elapsed >= .5f) {
            exploded = true; GetComponent<tk2dSpriteAnimator>()?.Play("Death");
            var glow = transform.Find("Death Glow");
            if (glow != null) { glow.gameObject.SetActive(true); glow.GetComponent<tk2dSpriteAnimator>()?.Play("Death Glow"); }
            transform.Find("Particle Wave")?.GetComponent<ParticleSystem>()?.Emit(32);
            transform.Find("Depart Particles")?.GetComponent<ParticleSystem>()?.Emit(16);
        }
        foreach (var animator in animators) if (animator != null && animator.gameObject.activeInHierarchy) animator.UpdateAnimation(Time.unscaledDeltaTime);
        if (elapsed >= .75f) transform.Find("Death Glow")?.gameObject.SetActive(false);
        if (elapsed >= 1.25f) GetComponent<Renderer>().enabled = false;
        if (elapsed >= 2.5f) Object.Destroy(gameObject);
    }
}
