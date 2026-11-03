using System.Collections.Generic;
using UnityEngine;

namespace Hkmp.Game.Client.Entity;

// A Great/Dash Slash may contact several colliders belonging to one enemy,
// or stay in contact after an alternate native hit animation. Count the actual
// HealthManager once per active slash, before damage and impact feedback.
// Each new slash activation resets this window; Cyclone keeps its native hits.
internal sealed class NailArtContactWindow : MonoBehaviour {
    private readonly HashSet<int> targets = new();
    private Collider2D collider;
    private bool colliderEnabled;

    private void Awake() => collider = GetComponent<Collider2D>();
    private void OnEnable() {
        targets.Clear();
        colliderEnabled = collider != null && collider.enabled;
    }
    private void OnDisable() => targets.Clear();
    private void Update() => RefreshCollider();
    private void RefreshCollider() {
        var current = collider != null && collider.enabled;
        if (current == colliderEnabled) return;
        targets.Clear(); colliderEnabled = current;
    }
    internal static bool Accept(HealthManager target, HitInstance hit) {
        if (hit.AttackType != AttackTypes.Nail || hit.Source == null ||
            EntityReferences.Owner(target.gameObject) == null) return true;
        var source = hit.Source;
        var window = source.GetComponent<NailArtContactWindow>();
        if (window == null) {
            var name = source.name.Replace("(Clone)", "");
            if (name != "Great Slash" && name != "Dash Slash") return true;
            window = source.AddComponent<NailArtContactWindow>();
        }
        window.RefreshCollider();
        return window.targets.Add(target.GetInstanceID());
    }
}
