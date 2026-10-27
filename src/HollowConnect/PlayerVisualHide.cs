using System.Collections.Generic;
using UnityEngine;

namespace HollowConnect;

// Shade and spectator presentation can overlap while roster and reliable life
// events arrive. Restore the original flag only after both owners release it.
internal sealed class PlayerVisualHide {
    private sealed class HiddenRenderer {
        internal bool Original;
        internal int Owners;
    }
    private static readonly Dictionary<Renderer, HiddenRenderer> shared = new();
    private readonly HashSet<Renderer> renderers = new();
    private GameObject root;

    internal void Hide(GameObject target) {
        if (!ReferenceEquals(root, target)) Restore();
        root = target;
        if (target == null) return;
        foreach (var renderer in target.GetComponentsInChildren<Renderer>(true)) {
            if (renderers.Add(renderer)) {
                if (!shared.TryGetValue(renderer, out var hidden))
                    shared[renderer] = hidden = new HiddenRenderer { Original = renderer.forceRenderingOff };
                hidden.Owners++;
            }
        }
        Apply();
    }

    internal void Apply() {
        foreach (var renderer in renderers) if (renderer != null) renderer.forceRenderingOff = true;
    }

    internal void Restore() {
        foreach (var renderer in renderers) {
            if (!shared.TryGetValue(renderer, out var hidden)) continue;
            if (--hidden.Owners == 0) {
                if (renderer != null) renderer.forceRenderingOff = hidden.Original;
                shared.Remove(renderer);
            } else if (renderer != null) renderer.forceRenderingOff = true;
        }
        renderers.Clear(); root = null;
    }
}
