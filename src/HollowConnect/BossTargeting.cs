using HollowConnect.Core;
using Hkmp;
using UnityEngine;

namespace HollowConnect;

public sealed partial class CoopRuntime {
    private ushort? bossTargetId;
    internal bool flexibleBossTargeting;

    private int AttackSourcePlayer(GameObject source) {
        if (source == null || !Connected || Resetting) return -1;
        var hero = HeroController.instance;
        if (hero != null && source.transform.IsChildOf(hero.transform)) return LocalId;
        foreach (var id in Peers.Keys) {
            var avatar = PlayerObject(id);
            if (avatar != null && source.transform.IsChildOf(avatar.transform)) return id;
        }
        var owner = Hkmp.Animation.AttackOwnership.RemoteOwner(source);
        if (owner >= 0) return owner;
        return -1;
    }

    private GameObject BossTargetObject(int id) {
        if (!Connected || Resetting || Returning || changingScene || !Playable || id < 0 || id > ushort.MaxValue) return null;
        if (id == LocalId) {
            var hero = HeroController.instance;
            return !Down && !Observing && !hero.cState.dead && !hero.cState.hazardDeath && !hero.cState.hazardRespawning ? hero.gameObject : null;
        }
        if (!Peers.TryGetValue((ushort)id, out var peer) || peer.State == null ||
            (peer.Life != Life.Alive && peer.Life != Life.Loading) || peer.State.Scene != CoopContext.LocalScene ||
            Now - peer.Seen >= 2 || Now < peer.Seen) return null;
        var avatar = PlayerObject((ushort)id);
        return avatar != null && avatar.activeInHierarchy ? avatar : null;
    }

    private int ChooseBossTarget(GameObject boss, int currentId, bool mayRetarget) {
        if (!flexibleBossTargeting || !Connected || boss == null) return -1;
        var current = BossTargetObject(currentId);
        if (current != null && !mayRetarget) return currentId;
        var origin = boss.transform.position;
        float Distance(GameObject target) => ((Vector2)(target.transform.position - origin)).sqrMagnitude;
        var bestId = -1;
        var bestDistance = float.MaxValue;
        void Consider(int id) {
            var target = BossTargetObject(id);
            if (target == null) return;
            var distance = Distance(target);
            if (distance < bestDistance || distance == bestDistance && id < bestId) {
                bestId = id; bestDistance = distance;
            }
        }
        Consider(LocalId);
        foreach (var id in Peers.Keys) Consider(id);
        // Compare squared distances: a challenger must be at least 25% closer.
        return current != null && bestId != currentId && bestDistance > Distance(current) * .75f * .75f ? currentId : bestId;
    }

    // AI reads use the actual remote avatar, including HKMP interpolation.
    // Never move the hidden local hero, camera, or the rescue anchor to aim.
    private GameObject LivingBossTarget() {
        var targetScene = Observing ? observationScene : deathScene;
        if (!Connected || !(Down || Observing) || Resetting || Returning || changingScene || !Playable ||
            CoopContext.LocalScene != targetScene) { bossTargetId = null; return null; }
        var now = Now;
        GameObject Candidate(ushort id, Peer peer) {
            // Loading also covers temporary input locks (recoil, dialogue,
            // pause); warp readiness is deliberately stricter than AI life.
            // HKMP removes/deactivates the avatar on actual room departure.
            if ((peer.Life != Life.Alive && peer.Life != Life.Loading) || peer.State == null || peer.State.Scene != targetScene ||
                now - peer.Seen >= 2 || now < peer.Seen) return null;
            var avatar = PlayerObject(id);
            return avatar != null && avatar.activeInHierarchy ? avatar : null;
        }
        if (bossTargetId.HasValue && Peers.TryGetValue(bossTargetId.Value, out var current)) {
            var avatar = Candidate(bossTargetId.Value, current);
            if (avatar != null) return avatar;
        }
        GameObject selected = null;
        bossTargetId = null;
        foreach (var pair in Peers) {
            if (bossTargetId.HasValue && pair.Key >= bossTargetId.Value) continue;
            var avatar = Candidate(pair.Key, pair.Value);
            if (avatar == null) continue;
            selected = avatar; bossTargetId = pair.Key;
        }
        return selected;
    }
}
