using System;
using System.Linq;
using UnityEngine;
using Object = UnityEngine.Object;

namespace HollowConnect;

internal static class DownFeedback {
    internal static void Play(Vector3 position, bool showParticles, bool playSound) {
        var hero = HeroController.instance;
        if (hero == null) return;
        if (playSound) hero.GetComponent<HeroAudioController>()?.PlaySound((GlobalEnums.HeroSounds)8);
        if (!showParticles) return;
        // Reuse native damage particles without copying damage FSMs or gameplay.
        var source = hero.GetComponentsInChildren<ParticleSystem>(true).FirstOrDefault(p => p.name == "Hit Pt 2");
        if (source == null) source = hero.GetComponentsInChildren<ParticleSystem>(true).FirstOrDefault(p => p.name == "Hit Pt 1");
        if (source == null) return;
        var staging = new GameObject("HollowConnect down particle staging"); staging.SetActive(false);
        var go = Object.Instantiate(source.gameObject, staging.transform, false);
        go.name = "HollowConnect down particles"; go.SetActive(false);
        foreach (var script in go.GetComponentsInChildren<MonoBehaviour>(true)) { script.enabled = false; Object.Destroy(script); }
        foreach (var collider in go.GetComponentsInChildren<Collider2D>(true)) { collider.enabled = false; Object.Destroy(collider); }
        foreach (var body in go.GetComponentsInChildren<Rigidbody2D>(true)) Object.Destroy(body);
        go.transform.SetParent(null, false); go.transform.position = position;
        go.transform.localScale = Vector3.one * .7f;
        foreach (var particle in go.GetComponentsInChildren<ParticleSystem>(true)) {
            particle.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);
            var main = particle.main; main.loop = false; main.playOnAwake = false;
            main.startLifetime = .45f; main.maxParticles = 16; main.useUnscaledTime = true;
            var emission = particle.emission; emission.enabled = false;
        }
        go.SetActive(true); go.GetComponent<ParticleSystem>()?.Emit(12);
        go.AddComponent<DownParticleLifetime>();
        Object.Destroy(staging);
    }
}

// Network events still arrive during hit-stop/pause. Always retire the small
// burst in real time rather than accumulating effects on a stopped game clock.
internal sealed class DownParticleLifetime : MonoBehaviour {
    private float expires;
    private void Awake() => expires = Time.realtimeSinceStartup + 1.2f;
    private void Update() { if (Time.realtimeSinceStartup >= expires) Object.Destroy(gameObject); }
}
