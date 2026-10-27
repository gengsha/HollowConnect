using System;
using System.Collections.Generic;
using System.Linq;
using HollowConnect.Core;
using UnityEngine;

namespace HollowConnect;

public sealed partial class CoopRuntime {
    private static readonly Vector2 WarpBodyClearance = new(.03f, .01f);

    private static bool TryWarpBody(HeroController hero, out Vector2 size, out Vector2 offset) {
        var collider = hero.GetComponent<Collider2D>();
        if (collider is BoxCollider2D box) {
            // Native loading can disable the collider or body simulation, when
            // bounds is empty. Transform metadata still preserves facing,
            // offset, parent scale and rotation without enabling physics.
            var x = box.transform.TransformVector(new Vector3(box.size.x, 0, 0));
            var y = box.transform.TransformVector(new Vector3(0, box.size.y, 0));
            size = new Vector2(Math.Abs(x.x) + Math.Abs(y.x), Math.Abs(x.y) + Math.Abs(y.y));
            offset = box.transform.TransformPoint(box.offset) - hero.transform.position;
        } else {
            var bounds = collider != null ? collider.bounds : default;
            size = bounds.size; offset = bounds.center - hero.transform.position;
        }
        return Party.Finite(size.x) && Party.Finite(size.y) && Party.Finite(offset.x) && Party.Finite(offset.y) &&
            size.x > .01f && size.y > .01f;
    }

    private static IEnumerable<float> WarpSideOffsets(Vector2 target, Vector2 size, Vector2 offset, HeroController hero, string scene) {
        yield return 0;
        var corrections = new List<float>();
        foreach (var side in new[] { -1f, 1f }) {
            // Sweep the full body from each nearby side towards the requested
            // position. Contact normals identify which way leaves the solid,
            // including slanted or polygon platforms whose AABB is misleading.
            var origin = target + offset + Vector2.right * (side * 2);
            foreach (var hit in Physics2D.BoxCastAll(origin, size + WarpBodyClearance * 2, 0, Vector2.left * side, 2)) {
                if (hit.distance <= 0 || hit.normal.x * side < .5f || !IsWarpBlocked(hit.collider, hero, scene)) continue;
                var dx = hit.centroid.x - offset.x - target.x + side * .02f;
                if (Math.Abs(dx) <= 2) corrections.Add(dx);
            }
        }
        foreach (var dx in corrections.OrderBy(Math.Abs)) yield return dx;
        foreach (var dx in new[] { .5f, -.5f, 1f, -1f, 1.5f, -1.5f, 2f, -2f }) yield return dx;
    }

    private static bool WarpPlacementBlocked(Vector2 position, Vector2 size, Vector2 offset, HeroController hero, string scene,
        Collider2D support = null) => Physics2D.OverlapBoxAll(position + offset, size + WarpBodyClearance * 2, 0)
        .Any(c => (support == null || c != support && !c.transform.IsChildOf(support.transform) &&
            !support.transform.IsChildOf(c.transform)) && IsWarpBlocked(c, hero, scene));
}
