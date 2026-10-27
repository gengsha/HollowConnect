using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Hkmp.Animation;

// Only HKMP's remote body/effect creation sites call these helpers. Detached
// spells keep their own tint component; no scene-wide renderer scans/material edits.
internal static class RemotePlayerVisuals {
    private static readonly HashSet<RemoteVisualOpacity> tracked = new();
    static RemotePlayerVisuals() => CoopContext.PlayerVisualSettingsChanged += Refresh;
    internal static void Register(RemoteVisualOpacity visual) => tracked.Add(visual);
    internal static void Forget(RemoteVisualOpacity visual) => tracked.Remove(visual);
    private static void Refresh() {
        foreach (var visual in tracked) if (visual != null) visual.Refresh();
    }
    internal static GameObject Track(GameObject obj, GameObject origin = null) {
        if (obj != null && obj.GetComponent<RemoteVisualOpacity>() == null)
            obj.AddComponent<RemoteVisualOpacity>();
        var owner = AttackOwnership.CreatingPlayer >= 0 ? AttackOwnership.CreatingPlayer : AttackOwnership.RemoteOwner(origin);
        if (obj != null && owner >= 0) obj.GetComponent<RemoteVisualOpacity>().PlayerId = owner;
        return obj;
    }
    internal static GameObject Instantiate(GameObject source) {
        using var scope = RemotePlayerFeedback.BeginCreation();
        return Track(Object.Instantiate(source), source);
    }
    internal static GameObject Instantiate(GameObject source, Transform parent) {
        using var scope = RemotePlayerFeedback.BeginCreation();
        return Track(Object.Instantiate(source, parent), source);
    }
    internal static GameObject Instantiate(GameObject source, Vector3 position, Quaternion rotation) {
        using var scope = RemotePlayerFeedback.BeginCreation();
        return Track(Object.Instantiate(source, position, rotation), source);
    }
    internal static GameObject Instantiate(GameObject source, Vector3 position, Quaternion rotation, Transform parent) {
        using var scope = RemotePlayerFeedback.BeginCreation();
        return Track(Object.Instantiate(source, position, rotation, parent), source);
    }
    internal static ParticleSystem Instantiate(ParticleSystem source, Vector3 position, Quaternion rotation) {
        using var scope = RemotePlayerFeedback.BeginCreation();
        var particle = Object.Instantiate(source, position, rotation); Track(particle.gameObject, source.gameObject); return particle;
    }
    internal static ParticleSystem Instantiate(ParticleSystem source, Transform parent) {
        using var scope = RemotePlayerFeedback.BeginCreation();
        var particle = Object.Instantiate(source, parent); Track(particle.gameObject, source.gameObject); return particle;
    }
}

[DefaultExecutionOrder(-10000)]
internal sealed class RemoteVisualOpacity : MonoBehaviour {
    [SerializeField] internal int PlayerId = -1;
    private tk2dBaseSprite[] sprites;
    private SpriteRenderer[] images;
    private ParticleSystem[] particles;
    private Color[] spriteColors, imageColors;
    private ParticleSystem.MinMaxGradient[] particleColors;
    private bool tinted;

    private void Awake() {
        sprites = GetComponentsInChildren<tk2dBaseSprite>(true);
        images = GetComponentsInChildren<SpriteRenderer>(true);
        particles = GetComponentsInChildren<ParticleSystem>(true);
        spriteColors = new Color[sprites.Length]; imageColors = new Color[images.Length];
        particleColors = particles.Select(p => p.main.startColor).ToArray();
        RemotePlayerVisuals.Register(this);
        Refresh();
    }
    internal void Refresh() {
        // Keep default/disabled visuals out of Unity's per-frame update loop.
        enabled = (CoopContext.RemotePlayerOpacity?.Invoke() ?? 1) < 1;
        if (enabled) ApplyParticles();
    }
    private void OnEnable() => ApplyParticles();
    private void ApplyParticles() {
        var opacity = CoopContext.RemotePlayerOpacity?.Invoke() ?? 1;
        for (var i = 0; i < particles.Length; i++) {
            if (particles[i] == null) continue;
            var main = particles[i].main; main.startColor = Fade(particleColors[i], opacity);
        }
    }
    private void OnDestroy() => RemotePlayerVisuals.Forget(this);
    // Restore before native colour/fade animation, then multiply once after it.
    // Never multiply last frame's already faded alpha.
    private void Update() => RestoreSprites();
    private void LateUpdate() {
        var opacity = CoopContext.RemotePlayerOpacity?.Invoke() ?? 1;
        for (var i = 0; i < sprites.Length; i++) if (sprites[i] != null) {
            spriteColors[i] = sprites[i].color; sprites[i].color = Fade(spriteColors[i], opacity);
        }
        for (var i = 0; i < images.Length; i++) if (images[i] != null) {
            imageColors[i] = images[i].color; images[i].color = Fade(imageColors[i], opacity);
        }
        tinted = true;
    }
    private void RestoreSprites() {
        if (!tinted) return;
        for (var i = 0; i < sprites.Length; i++) if (sprites[i] != null) sprites[i].color = spriteColors[i];
        for (var i = 0; i < images.Length; i++) if (images[i] != null) images[i].color = imageColors[i];
        tinted = false;
    }
    private void OnDisable() {
        RestoreSprites();
        for (var i = 0; i < particles.Length; i++) if (particles[i] != null) {
            var main = particles[i].main; main.startColor = particleColors[i];
        }
    }
    private static Color Fade(Color color, float opacity) { color.a *= opacity; return color; }
    private static Gradient Fade(Gradient source, float opacity) {
        var gradient = new Gradient { mode = source.mode };
        var alpha = source.alphaKeys;
        for (var i = 0; i < alpha.Length; i++) alpha[i].alpha *= opacity;
        gradient.SetKeys(source.colorKeys, alpha); return gradient;
    }
    private static ParticleSystem.MinMaxGradient Fade(ParticleSystem.MinMaxGradient value, float opacity) {
        switch (value.mode) {
            case ParticleSystemGradientMode.Color: value.color = Fade(value.color, opacity); break;
            case ParticleSystemGradientMode.TwoColors:
                value.colorMin = Fade(value.colorMin, opacity); value.colorMax = Fade(value.colorMax, opacity); break;
            case ParticleSystemGradientMode.TwoGradients:
                value.gradientMin = Fade(value.gradientMin, opacity); value.gradientMax = Fade(value.gradientMax, opacity); break;
            default: value.gradient = Fade(value.gradient, opacity); break;
        }
        return value;
    }
}
