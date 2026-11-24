using System;
using System.Collections.Generic;
using Hkmp.Game.Client;
using UnityEngine;

namespace HollowConnect;

// The game excludes player layer 9 from dream-hitbox layer 17 collisions.
// Check the actual local polygon only while it is active, without changing
// the global collision matrix or adding receivers to pooled remote players.
internal sealed class TeammateDreamNail : IDisposable {
    private readonly CoopRuntime runtime;
    private TeammateDreamSwing swing;
    private bool extra;
    private readonly List<Vector2> path = new();
    internal TeammateDreamNail(CoopRuntime runtime) {
        this.runtime = runtime;
        CustomHooks.HeroControllerStartAction += Bind;
    }
    internal void Reset() => extra = false;
    public void Dispose() {
        CustomHooks.HeroControllerStartAction -= Bind;
        if (swing != null) UnityEngine.Object.Destroy(swing);
    }
    private void Bind() {
        var hero = HeroController.instance;
        var hitbox = hero == null ? null : hero.transform.Find("Dream Effects/Hitbox");
        if (hitbox == null) return;
        swing = hitbox.GetComponent<TeammateDreamSwing>() ?? hitbox.gameObject.AddComponent<TeammateDreamSwing>();
        swing.Owner = this;
    }
    internal bool Hit(PolygonCollider2D hitbox) {
        if (hitbox == null || !hitbox.enabled) return false;
        foreach (var pair in runtime.Peers) {
            if (!runtime.CanDreamNail(pair.Key)) continue;
            var target = runtime.PlayerObject(pair.Key);
            var body = target.GetComponent<BoxCollider2D>();
            if (body == null || !TouchesBody(hitbox, body, path)) continue;
            // Ordinary native Focus costs 33, while soul is integral.
            HeroController.instance.AddMPCharge(extra ? 17 : 16);
            extra = !extra;
            target.GetComponent<SpriteFlash>()?.flashDreamImpact();
            return true;
        }
        return false;
    }
    // Compare the native shapes at their current transforms. Remote avatars are
    // moved by interpolation, and shadow dash temporarily disables the PvP box.
    // Neither the physics broadphase nor PvP invulnerability defines a dream hit.
    internal static bool TouchesBody(PolygonCollider2D hitbox, BoxCollider2D body, List<Vector2> vertices) {
        var hitboxToBody = body.transform.worldToLocalMatrix * hitbox.transform.localToWorldMatrix;
        var halfSize = body.size * .5f;
        for (var pathIndex = 0; pathIndex < hitbox.pathCount; pathIndex++) {
            hitbox.GetPath(pathIndex, vertices);
            if (vertices.Count < 3) continue;
            Vector2 previous = hitboxToBody.MultiplyPoint3x4(vertices[vertices.Count - 1] + hitbox.offset);
            previous -= body.offset;
            var containsCenter = false;
            foreach (var vertex in vertices) {
                Vector2 current = hitboxToBody.MultiplyPoint3x4(vertex + hitbox.offset);
                current -= body.offset;
                if (EdgeTouchesBox(previous, current, halfSize)) return true;
                // An entire body inside the polygon has no intersecting edges.
                if ((previous.y > 0) != (current.y > 0) &&
                    previous.x + (current.x - previous.x) * (-previous.y / (current.y - previous.y)) > 0) {
                    containsCenter = !containsCenter;
                }
                previous = current;
            }
            if (containsCenter) return true;
        }
        return false;
    }

    private static bool EdgeTouchesBox(Vector2 start, Vector2 end, Vector2 halfSize) {
        var delta = end - start;
        float enter = 0, exit = 1;
        return ClipAxis(start.x, delta.x, halfSize.x, ref enter, ref exit) &&
            ClipAxis(start.y, delta.y, halfSize.y, ref enter, ref exit);
    }

    private static bool ClipAxis(float start, float delta, float extent, ref float enter, ref float exit) {
        if (delta == 0) return Mathf.Abs(start) <= extent;
        var first = (-extent - start) / delta;
        var last = (extent - start) / delta;
        enter = Mathf.Max(enter, Mathf.Min(first, last));
        exit = Mathf.Min(exit, Mathf.Max(first, last));
        return enter <= exit;
    }
}

// Sample again after the remote avatar interpolation LateUpdate.
[DefaultExecutionOrder(1000)]
internal sealed class TeammateDreamSwing : MonoBehaviour {
    internal TeammateDreamNail Owner;
    internal bool Spent;
    private PolygonCollider2D hitbox;
    private void Awake() => hitbox = GetComponent<PolygonCollider2D>();
    private void FixedUpdate() => TryHit();
    private void LateUpdate() => TryHit();
    private void TryHit() {
        if (!Spent && Owner != null) Spent = Owner.Hit(hitbox);
    }
    private void OnDisable() => Spent = false;
}
