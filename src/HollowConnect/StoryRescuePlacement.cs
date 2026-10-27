using System;
using System.Collections.Generic;
using System.Linq;
using Hkmp;
using HollowConnect.Core;
using UnityEngine;
using UnitySceneManager = UnityEngine.SceneManagement.SceneManager;

namespace HollowConnect;

public sealed partial class CoopRuntime {
    private sealed class RescueFootprint {
        internal Vector2 Position, LocalSupportPosition;
        internal Collider2D Support;
    }
    private readonly List<RescueFootprint> rescueTrail = new List<RescueFootprint>();
    private int rescueTrailScene = int.MinValue;
    private uint rescueTrailEpoch, rescueAnchorRequest, rescueAnchorAccepted;
    private float nextRescueSample, nextRescueValidation, rescueAnchorDeadline, rescueGateIsolationSince = -1;
    private bool rescueAnchorPending;
    private bool rescueHazardDeath, rescueAirAnchor;
    private int rescueAnchorFailures;
    private Vector2 rescueBodySize, rescueBodyOffset;
    private Collider2D rescueSupport;
    private Vector2 rescueSupportLocal;

    internal bool IsGodhomeRescueScene() => UnitySceneManager.GetActiveScene().name.StartsWith("GG_", StringComparison.Ordinal) ||
        GameManager.UnsafeInstance != null && GameManager.UnsafeInstance.GetCurrentMapZone() == "GODS_GLORY";

    private void ResetStoryRescuePlacement() {
        rescueTrail.Clear(); rescueTrailScene = int.MinValue;
        rescueSupport = null; rescueAirAnchor = false; rescueAnchorRequest = rescueAnchorAccepted = 0;
        rescueAnchorPending = false; rescueAnchorFailures = 0; nextRescueSample = nextRescueValidation = 0;
        rescueGateIsolationSince = -1;
    }

    private void BeginStoryRescuePlacement(HeroController hero) {
        if (IsGodhomeRescueScene()) return;
        var scene = UnitySceneManager.GetActiveScene();
        if (rescueTrailScene != scene.handle || rescueTrailEpoch != CoopContext.Epoch) rescueTrail.Clear();
        rescueTrailScene = scene.handle; rescueTrailEpoch = CoopContext.Epoch;
        // Lock disables Rigidbody2D simulation. Collider.bounds can then be
        // empty, so retain the real standing body before entering Down.
        StoryBody(hero, out rescueBodySize, out rescueBodyOffset);
        rescueHazardDeath = takingDamage && (damageHazard == 2 || damageHazard == 3 || damageHazard == 5);
        rescueSupport = null; rescueAirAnchor = false; rescueAnchorRequest = rescueAnchorAccepted = 0;
        rescueAnchorPending = false; rescueAnchorFailures = 0; nextRescueValidation = Time.unscaledTime + .5f;
        rescueGateIsolationSince = -1;
    }

    private void TickStoryRescuePlacement() {
        // Do not sample, validate or relocate Godhome. Its established native
        // checkpoint/ground strategy and fixed anchors remain independent.
        if (IsGodhomeRescueScene()) { if (rescueTrail.Count != 0) rescueTrail.Clear(); return; }
        var scene = UnitySceneManager.GetActiveScene();
        if (rescueTrailScene != scene.handle || rescueTrailEpoch != CoopContext.Epoch) {
            rescueTrail.Clear(); rescueTrailScene = scene.handle; rescueTrailEpoch = CoopContext.Epoch;
            rescueSupport = null;
        }
        var hero = HeroController.instance;
        if (hero == null || !Playable || Resetting || Returning || changingScene || GameManager.instance.isPaused) return;
        if (!Down) {
            if (Time.unscaledTime < nextRescueSample || !hero.acceptingInput || hero.controlReqlinquished ||
                hero.cState.dead || hero.cState.transitioning || hero.cState.hazardDeath || hero.cState.hazardRespawning) return;
            nextRescueSample = Time.unscaledTime + .15f;
            var position = (Vector2)hero.transform.position;
            // A teleport is not a walkable trail. Never use it as proof of a
            // route through a wall or through a previous arena entrance.
            if (rescueTrail.Count > 0 && Vector2.Distance(rescueTrail[rescueTrail.Count - 1].Position, position) > 12) rescueTrail.Clear();
            var footprint = new RescueFootprint { Position = position };
            if (hero.cState.onGround && TryStoryLanding(hero, position, .4f, false, out var supported, out var support) &&
                Vector2.Distance(position, supported) < .5f) {
                footprint.Support = support;
                footprint.LocalSupportPosition = support.transform.InverseTransformPoint(supported);
            }
            if (rescueTrail.Count == 160) rescueTrail.RemoveAt(0);
            rescueTrail.Add(footprint);
            return;
        }
        if (rescueAnchorPending && Time.unscaledTime >= rescueAnchorDeadline) RescueAnchorFailed();
        if (Spectating || !RescueAvailable || giveUpRequested || rescueAnchorPending ||
            scene.name != deathScene || Time.unscaledTime < nextRescueValidation) return;
        nextRescueValidation = Time.unscaledTime + .5f;
        Physics2D.SyncTransforms();
        // Battle gates are local to each player. A survivor can be in the same
        // scene yet outside the sealed arena. Never pull a shade through that
        // gate or leave it waiting indefinitely for an impossible rescue.
        if (StoryIsolatedByBattleGate(hero)) {
            if (rescueGateIsolationSince < 0) rescueGateIsolationSince = Time.unscaledTime;
            if (Time.unscaledTime - rescueGateIsolationSince >= 3) {
                DebugEvent("rescue-anchor-gate-isolated", DebugEventSeverity.Warning);
                RequestPersonalReturn();
                Notice("战斗封门已隔开所有可救援队友，正在返回自己的复活点…");
            }
            return;
        }
        rescueGateIsolationSince = -1;
        // A fixed point must still have its own support and room for a real
        // nail strike. Looking for another floor below it would hide a loss.
        if (TryStoryLanding(hero, DeathPosition, .25f, true, out var same, out var sameSupport) &&
            Vector2.Distance(same, DeathPosition) < .3f) { rescueAirAnchor = false; return; }
        if (rescueAirAnchor && StoryAirSpace(hero, DeathPosition)) return;
        var replacement = FindStoryRescuePosition(hero);
        if (!replacement.HasValue) {
            DebugEvent("rescue-anchor-unavailable", DebugEventSeverity.Warning);
            RequestPersonalReturn();
            Notice("当前位置已无法安全救援，正在按原版死亡规则返回…");
            return;
        }
        if (rescueAnchorRequest >= int.MaxValue) { RequestPersonalReturn(); return; }
        rescueAnchorPending = true;
        rescueAnchorDeadline = Time.unscaledTime + 3;
        Send(new Message { Kind = Kind.RescueAnchorRequest, Serial = downSerial, Aux = ++rescueAnchorRequest,
            Scene = deathScene, X = replacement.Value.x, Y = replacement.Value.y });
        DebugEvent("rescue-anchor-proposed", DebugEventSeverity.Info);
    }

    private Vector3? FindStoryRescuePosition(HeroController hero) {
        Physics2D.SyncTransforms();
        var origin = Down ? (Vector2)DeathPosition : (Vector2)hero.transform.position;
        // An accepted real nail hit already proves access to this anchor.
        // Revalidate its recovery space before searching afresh: the rescuer
        // can be mid-swing beside a hazard in the next presence sample, which
        // must not discard a still-safe, confirmed aerial revival point.
        if (Down && rescueAirAnchor && StoryAirSpace(hero, origin))
            return new Vector3(origin.x, origin.y, hero.transform.position.z);
        bool Accept(Vector2 candidate, float drop, out Vector3 result, Collider2D carriedBy = null) {
            result = default;
            if (!TryStoryLanding(hero, candidate, drop, true, out var supported, out var support) ||
                !StoryRescueRoute(hero, origin, supported, carriedBy)) return false;
            rescueAirAnchor = false;
            rescueSupport = support; rescueSupportLocal = support.transform.InverseTransformPoint(supported);
            result = new Vector3(supported.x, supported.y, hero.transform.position.z);
            return true;
        }
        // Revalidate the actual route travelled by this player, in reverse.
        // New gates/phase geometry invalidate earlier footsteps at the first
        // blocked segment. A native respawn marker is never presumed reachable.
        Vector3? FollowTrail() {
            var previous = origin;
            for (var i = rescueTrail.Count - 1; i >= 0; i--) {
                var footprint = rescueTrail[i];
                if (Vector2.Distance(previous, footprint.Position) > 12 || !StoryRescueRoute(hero, previous, footprint.Position)) break;
                previous = footprint.Position;
                if (footprint.Support == null || !footprint.Support.enabled || !footprint.Support.gameObject.activeInHierarchy) continue;
                var candidate = (Vector2)footprint.Support.transform.TransformPoint(footprint.LocalSupportPosition);
                if (Vector2.Distance(footprint.Position, candidate) > 2 ||
                    !TryStoryLanding(hero, candidate, .6f, true, out var supported, out var support) ||
                    !StoryRescueRoute(hero, footprint.Position, supported)) continue;
                rescueAirAnchor = false;
                rescueSupport = support; rescueSupportLocal = support.transform.InverseTransformPoint(supported);
                return new Vector3(supported.x, supported.y, hero.transform.position.z);
            }
            return null;
        }
        // A lethal spike/acid/fall should return to the player's last safe
        // footing on this side of current geometry, rather than into a lower
        // pocket at the bottom of the hazard. Ordinary hits prefer local floor.
        if (rescueHazardDeath) { var safeTrail = FollowTrail(); if (safeTrail.HasValue) return safeTrail; }
        if (Accept(origin, 20, out var nearby)) return nearby;
        if (!rescueHazardDeath) { var safeTrail = FollowTrail(); if (safeTrail.HasValue) return safeTrail; }
        // Nearby living players supply an observed safe area, rather than an
        // arbitrary level-wide marker. Reject any route across solid geometry.
        foreach (var peer in Peers.Values.Where(p => p.Life == Life.Alive && p.State != null &&
            p.State.Scene == deathScene && Now - p.Seen < 2)
            .OrderBy(p => (new Vector2(p.State.X, p.State.Y) - origin).sqrMagnitude)) {
            var point = new Vector2(peer.State.X, peer.State.Y);
            if (Vector2.Distance(point, origin) <= 24 && Accept(point, 4, out var teammate)) return teammate;
        }
        // Following a vanished lift blindly could strand the shade above the
        // survivors. Carry only when a living peer is observed on that same
        // support, and no closer safe floor/trail is available. The carrier
        // itself may sweep over the old anchor; all other solids still block.
        if (Down && rescueSupport != null && rescueSupport.enabled && rescueSupport.gameObject.activeInHierarchy) {
            var carrier = rescueSupport;
            var carried = (Vector2)carrier.transform.TransformPoint(rescueSupportLocal);
            if (Vector2.Distance(carried, origin) <= 24 && Peers.Values.Any(p => p.Life == Life.Alive && p.State != null &&
                p.State.Scene == deathScene && Now - p.Seen < 2 &&
                TryStoryLanding(hero, new Vector2(p.State.X, p.State.Y), 1, false, out _, out var peerSupport) && peerSupport == carrier) &&
                Accept(carried, 1, out var moving, carrier)) return moving;
        }
        // A shade does not need a floor: a living player can strike while
        // wall-jumping or airborne. Keep grounded placement as the first
        // choice, then require an observed nearby survivor and a real safe
        // recovery route before accepting an aerial anchor.
        return FindStoryAirRescuePosition(hero, origin);
    }

    private Vector3? FindStoryAirRescuePosition(HeroController hero, Vector2 origin) {
        foreach (var peer in Peers.Values.Where(p => p.Life == Life.Alive && p.State != null &&
            p.State.Scene == deathScene && Now - p.Seen < 2)
            .OrderBy(p => (new Vector2(p.State.X, p.State.Y) - origin).sqrMagnitude)) {
            var witness = new Vector2(peer.State.X, peer.State.Y);
            if (Vector2.Distance(origin, witness) > 24 || !StoryRescueRoute(hero, origin, witness)) continue;
            foreach (var seed in new[] { origin, witness })
                foreach (var dy in new[] { 0f, 2f, 4f })
                    foreach (var dx in new[] { 0f, -1.5f, 1.5f, -3f, 3f, -6f, 6f, -9f, 9f }) {
                        var point = seed + new Vector2(dx, dy);
                        if (Vector2.Distance(point, origin) > 24 || Vector2.Distance(point, witness) > 12 ||
                            !StoryRescueRoute(hero, origin, point) || !StoryAirRoute(hero, witness, point) ||
                            !StoryAirSpace(hero, point)) continue;
                        rescueSupport = null; rescueAirAnchor = true;
                        return new Vector3(point.x, point.y, hero.transform.position.z);
                    }
        }
        return null;
    }

    private bool StoryAirRoute(HeroController hero, Vector2 from, Vector2 to) {
        if (StoryOccupied(hero, from) || StoryOccupied(hero, to)) return false;
        StoryBody(hero, out var size, out var offset);
        var delta = to - from;
        if (delta.magnitude < .1f) return true;
        return !Physics2D.BoxCastAll(from + offset, size * .94f, 0, delta.normalized, delta.magnitude)
            .Any(h => StoryScene(h.collider) && !h.collider.transform.IsChildOf(hero.transform) &&
                (StoryDamage(h.collider) || StorySolid(h.collider, hero)) && h.distance < delta.magnitude - .05f);
    }

    private bool StoryAirSpace(HeroController hero, Vector2 point) {
        StoryBody(hero, out var size, out var offset);
        if (size.x < .1f || size.y < .1f || !StoryAirRoute(hero, point, point + Vector2.down * 3)) return false;
        // At least one nail approach needs body clearance, not a neighbouring
        // floor. Persistent spikes/acid/beams still block every air query.
        if (!new[] { -.7f, .7f, -1.4f, 1.4f }.Any(dx => StoryAirRoute(hero, point, point + Vector2.right * dx))) return false;
        foreach (var dx in new[] { 0f, -2f, 2f }) {
            var target = point + Vector2.right * dx;
            if (TryStoryLanding(hero, target, 12, false, out var floor, out _) && StoryAirRoute(hero, point, floor)) return true;
        }
        // Unsupported space over a lethal void is insufficient. A wall is an
        // exit only with the actual wall-jump ability, a non-damaging vertical
        // face, and enough free space below both the anchor and wall approach.
        if (!PlayerData.instance.GetBool("hasWalljump")) return false;
        foreach (var direction in new[] { Vector2.left, Vector2.right })
            foreach (var hit in Physics2D.RaycastAll(point + offset, direction, 3).OrderBy(h => h.distance)) {
                if (!StorySolid(hit.collider, hero) || StoryDamage(hit.collider) || hit.collider.gameObject.layer != 8 ||
                    hit.collider.GetComponent<NonSlider>() != null ||
                    Math.Abs(hit.normal.x) < .9f || hit.distance <= size.x / 2) continue;
                var wall = new Vector2(hit.point.x + hit.normal.x * (size.x / 2 + .1f), point.y);
                if (StoryAirRoute(hero, point, wall) && StoryAirRoute(hero, wall, wall + Vector2.down * 3)) return true;
            }
        return false;
    }

    private static bool StoryDamage(Collider2D collider) {
        var damage = collider.GetComponentInParent<DamageHero>();
        // Native terrain spikes and laser traps can also use hazardType 1.
        // Keep them dangerous unless this is an identified combat attack.
        return damage != null && damage.enabled && damage.damageDealt > 0 &&
            collider.GetComponentInParent<HealthManager>() == null &&
            !(damage.hazardType == 1 && StoryCombatAttack(damage, collider));
    }
    private static bool StoryCombatAttack(DamageHero damage, Collider2D collider) {
        if (Hkmp.Game.Client.Entity.EntityReferences.Owner(collider.gameObject) != null) return true;
        // Story Uumuu activates these native scene objects rather than spawning
        // registered projectiles. Scope the exception to this verified family;
        // unrelated Zap Clouds, terrain spikes and beams retain their checks.
        return StoryScene(collider) && collider.gameObject.scene.name == "Fungus3_archive_02" &&
            (damage.name == "Mega Jelly Multizap" || damage.name.StartsWith("Mega Jelly Multizap (", StringComparison.Ordinal));
    }
    private static bool StoryScene(Collider2D collider) {
        var current = UnitySceneManager.GetActiveScene().name;
        var name = collider.gameObject.scene.name;
        // Native boss content can live in an additive <room>_boss scene.
        // Its gates/platforms are part of this room's physical geometry.
        return name == current || name.StartsWith(current + "_", StringComparison.Ordinal);
    }
    private bool StorySolid(Collider2D collider, HeroController hero) {
        if (collider == null || !collider.enabled || !collider.gameObject.activeInHierarchy ||
            !StoryScene(collider) ||
            collider.isTrigger || collider.transform.IsChildOf(hero.transform) ||
            collider.GetComponentInParent<ShadeView>() != null || collider.GetComponentInParent<HealthManager>() != null ||
            collider.gameObject.layer == (int)GlobalEnums.PhysLayers.ENEMIES ||
            collider.GetComponentInParent<Hkmp.Animation.RemoteVisualOpacity>() != null) return false;
        return !Physics2D.GetIgnoreLayerCollision(hero.gameObject.layer, collider.gameObject.layer);
    }
    private void StoryBody(HeroController hero, out Vector2 size, out Vector2 offset) {
        if (Down) { size = rescueBodySize; offset = rescueBodyOffset; return; }
        var bounds = hero.GetComponent<Collider2D>().bounds;
        size = bounds.size; offset = bounds.center - hero.transform.position;
        if (size.x < .1f || size.y < .1f) {
            // Native hazards may disable collision before invoking Die.
            // BoxCollider metadata remains available while bounds is empty.
            var box = hero.GetComponent<BoxCollider2D>();
            if (box != null) {
                var scale = box.transform.lossyScale;
                size = new Vector2(Math.Abs(box.size.x * scale.x), Math.Abs(box.size.y * scale.y));
                offset = box.transform.TransformPoint(box.offset) - hero.transform.position;
            }
        }
    }
    private bool StoryIsolatedByBattleGate(HeroController hero) {
        var survivors = Peers.Values.Where(p => p.Life == Life.Alive).ToArray();
        if (survivors.Length == 0) return false; // Room failure owns the no-survivor case.
        StoryBody(hero, out var size, out var offset);
        foreach (var peer in survivors) {
            if (peer.State == null || peer.State.Scene != deathScene || Now - peer.Seen >= 2) return false;
            var delta = new Vector2(peer.State.X, peer.State.Y) - (Vector2)DeathPosition;
            if (!Physics2D.BoxCastAll((Vector2)DeathPosition + offset, size * .9f, 0, delta.normalized, delta.magnitude)
                .Any(h => StorySolid(h.collider, hero) && h.distance < delta.magnitude - .12f &&
                    StoryBattleGate(h.collider) && StoryOppositeSides(h.collider.bounds, DeathPosition, new Vector2(peer.State.X, peer.State.Y)))) return false;
        }
        return true;
    }
    private static bool StoryBattleGate(Collider2D collider) {
        // Camera lock volumes are local barriers for network isolation, but
        // they do not seal a rescue route. Require a physical battle barrier.
        if (!Hkmp.Game.Client.Entity.EntityRegistry.IsLocalOnlyBarrier(collider.gameObject)) return false;
        for (var t = collider.transform; t != null; t = t.parent)
            if (Hkmp.Game.Client.Entity.EntityRegistry.TryGetEntry(t.gameObject, out var entry) &&
                (entry.Type == Hkmp.Game.Client.Entity.EntityType.BattleGate || entry.Type == Hkmp.Game.Client.Entity.EntityType.MantisLordCage ||
                 entry.Type == Hkmp.Game.Client.Entity.EntityType.ColosseumWall)) return true;
        return false;
    }
    private static bool StoryOppositeSides(Bounds gate, Vector2 from, Vector2 to) => gate.size.y >= gate.size.x
        ? from.x < gate.min.x && to.x > gate.max.x || to.x < gate.min.x && from.x > gate.max.x
        : from.y < gate.min.y && to.y > gate.max.y || to.y < gate.min.y && from.y > gate.max.y;
    private bool StoryOccupied(HeroController hero, Vector2 point) {
        StoryBody(hero, out var size, out var offset);
        return Physics2D.OverlapBoxAll(point + offset, size * .94f, 0)
            .Any(c => StoryScene(c) &&
                !c.transform.IsChildOf(hero.transform) && (StoryDamage(c) || StorySolid(c, hero)));
    }
    private bool StoryRescueRoute(HeroController hero, Vector2 from, Vector2 to, Collider2D carriedBy = null) {
        var delta = to - from; var distance = delta.magnitude;
        if (distance < .1f) return true;
        StoryBody(hero, out var size, out var offset);
        // This is a conservative swept-body clearance check, not a promise
        // that a straight line is a platforming route. Historical segments
        // provide the player's real route around corners and over platforms.
        return !Physics2D.BoxCastAll(from + offset, size * .9f, 0, delta / distance, distance)
            .Any(h => h.collider != carriedBy && StorySolid(h.collider, hero) && h.distance < distance - .12f);
    }
    private bool TryStoryLanding(HeroController hero, Vector2 target, float drop, bool strike,
        out Vector2 landing, out Collider2D support) {
        landing = default; support = null;
        StoryBody(hero, out var size, out var offset);
        if (size.x < .1f || size.y < .1f) return false;
        foreach (var dx in new[] { 0f, .75f, -.75f, 1.5f, -1.5f }) {
            var ray = new Vector2(target.x + dx, target.y + .25f);
            foreach (var hit in Physics2D.RaycastAll(ray, Vector2.down, Math.Max(.1f, drop + size.y / 2 - offset.y + .35f))
                .OrderBy(h => h.distance)) {
                if (!StorySolid(hit.collider, hero) || StoryDamage(hit.collider) || hit.normal.y < .7f || hit.distance == 0) continue;
                var point = new Vector2(ray.x, hit.point.y + size.y / 2 - offset.y + .07f);
                if (point.y > target.y + .4f || target.y - point.y > drop || StoryOccupied(hero, point)) continue;
                if (strike && !StoryStrikeSpace(hero, point) && !StoryAirStrikeSpace(hero, point)) continue;
                landing = point; support = hit.collider; return true;
            }
        }
        return false;
    }
    private bool StoryStrikeSpace(HeroController hero, Vector2 anchor) {
        foreach (var dx in new[] { -.7f, .7f, -1.4f, 1.4f }) {
            var target = anchor + Vector2.right * dx;
            if (TryStoryLanding(hero, target, .6f, false, out var stand, out _) &&
                Math.Abs(stand.x - anchor.x) >= .45f && Math.Abs(stand.y - anchor.y) < .6f &&
                StoryRescueRoute(hero, anchor, stand)) return true;
        }
        return false;
    }

    private bool StoryAirStrikeSpace(HeroController hero, Vector2 anchor) {
        foreach (var dx in new[] { -1.4f, 1.4f }) {
            var approach = anchor + Vector2.right * dx;
            if (!StoryAirRoute(hero, anchor, approach)) continue;
            if (Peers.Values.Any(p => p.Life == Life.Alive && p.State != null && p.State.Scene == deathScene && Now - p.Seen < 2 &&
                Vector2.Distance(new Vector2(p.State.X, p.State.Y), approach) <= 12 &&
                StoryAirRoute(hero, new Vector2(p.State.X, p.State.Y), approach))) return true;
        }
        return false;
    }

    private void RescueAnchorFailed() {
        rescueAnchorPending = false;
        nextRescueValidation = Time.unscaledTime + .5f;
        DebugEvent("rescue-anchor-retry", DebugEventSeverity.Warning);
        if (++rescueAnchorFailures >= 3 && Down && !Returning && !giveUpRequested) RequestPersonalReturn();
    }

    private void ReceiveRescueAnchor(Message m) {
        // Negative Value acknowledges a rejected proposal; it is never a
        // coordinate update. Timeouts also cover a disconnected server.
        if (m.Id == LocalId && m.Value < 0 && m.Value != int.MinValue && !IsGodhomeRescueScene()) {
            if (Down && !Returning && !Resetting && m.Serial == downSerial && m.Scene == deathScene &&
                rescueAnchorPending && (uint)-m.Value == rescueAnchorRequest) RescueAnchorFailed();
            return;
        }
        if (IsGodhomeRescueScene() || Resetting || m.Serial == 0 || m.Value <= 0 ||
            float.IsNaN(m.X) || float.IsInfinity(m.X) || float.IsNaN(m.Y) || float.IsInfinity(m.Y)) return;
        if (m.Id == LocalId) {
            if (!Down || Spectating || Returning || m.Serial != downSerial || m.Scene != deathScene ||
                (uint)m.Value <= rescueAnchorAccepted || (uint)m.Value > rescueAnchorRequest) return;
            rescueAnchorAccepted = (uint)m.Value; rescueAnchorPending = false; rescueAnchorFailures = 0;
            DeathPosition = new Vector3(m.X, m.Y, DeathPosition.z);
            HeroController.instance.transform.position = DeathPosition;
            Physics2D.SyncTransforms();
            if (deathCamera != null) {
                var view = deathCamera.KeepWithinSceneBounds(DeathPosition);
                deathCamera.SnapTo(view.x, view.y); cameraPosition = deathCamera.transform.position;
            }
            shades.Synchronize();
            DebugEvent("rescue-anchor-confirmed", DebugEventSeverity.Info);
            return;
        }
        if (!Peers.TryGetValue(m.Id, out var peer) || peer.Life != Life.Down || peer.DownSerial != m.Serial ||
            peer.State == null || peer.State.Scene != m.Scene || m.Aux < peer.LifeVersion) return;
        peer.LifeVersion = m.Aux; peer.State.X = m.X; peer.State.Y = m.Y; peer.Seen = Now;
        shades.Synchronize();
    }
}
