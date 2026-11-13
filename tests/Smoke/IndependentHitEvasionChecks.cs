using System;
using System.Collections;
using System.IO;
using System.Linq;
using System.Reflection;
using Hkmp;
using Hkmp.Animation;
using Hkmp.Game.Client.Entity;
using UnityEngine;
using Object = UnityEngine.Object;

namespace HollowConnect.Smoke;

public sealed partial class PantheonProbe {
    private bool HitEvasionCheck => Environment.GetEnvironmentVariable("HOLLOWCONNECT_HIT_EVASION_CHECK") == "1";
    private IEnumerator IndependentHitEvasionChecks() {
        var first = Environment.GetEnvironmentVariable("HOLLOWCONNECT_PANTHEON_AUTHORITY") ?? "host";
        var sequence = Resources.LoadAll<BossSequence>("GG").Single(s => s.name == "Boss Sequence Tier 1");
        BossSequenceController.Reset();
        BossSequenceController.SetupNewSequence(sequence, BossSequenceController.ChallengeBindings.None, "bossDoorStateTier1");
        if (role != first) yield return Until(() => Has(first + ".evasion-entered"), "evasion authority enters first");
        Enter(sequence.GetSceneAt(0));
        yield return Until(() => In(sequence.GetSceneAt(0)) && manager.IsSceneHostDetermined, "evasion native boss ready");
        SafeHero(); Flag(role + ".evasion-entered");
        yield return Both("evasion-room");
        yield return Until(() => engine.ClientManager.SceneSnapshotReady && runtime.Peers.Count == 1 &&
            runtime.PlayerObject(runtime.Peers.Keys.Single()) != null && runtime.CurrentBossHealthMultiplier() > 1.5,
            "evasion peers and dynamic health ready");
        var boss = Entities.Values.First(e => e.Type == EntityType.VengeflyKing);
        var health = (manager.IsSceneHost ? boss.Object.Host : boss.Object.Client).GetComponent<HealthManager>();
        if (manager.IsSceneHost) {
            if (role != first) throw new Exception("Unexpected evasion scene authority");
            yield return Until(() => !health.IsInvincible && Field<float>(health, "evasionByHitRemaining") <= 0,
                "native boss vulnerable for evasion checks");
            var local = HeroController.instance.gameObject;
            var peer = runtime.Peers.Keys.Single();
            var remote = runtime.PlayerObject(peer);
            var detachedLocal = new GameObject("Local detached evasion spell") { layer = 17 };
            var prototype = new GameObject("Remote evasion spell");
            GameObject detachedRemote;
            using (AttackOwnership.Creating(peer)) detachedRemote = RemotePlayerVisuals.Instantiate(prototype);
            try {
                if (CoopContext.AttackSourcePlayer(local) != runtime.LocalId || CoopContext.AttackSourcePlayer(remote) != peer ||
                    CoopContext.AttackSourcePlayer(detachedRemote) != peer) throw new Exception("Evasion attack ownership fixture");
                var expected = health.hp;
                void Hit(GameObject source, int damage, AttackTypes type, int debit, string label, bool direct = false) {
                    var hit = new HitInstance { Source = source, DamageDealt = damage, AttackType = type, Multiplier = 1 };
                    if (direct) typeof(HealthManager).GetMethod("TakeDamage", BindingFlags.Instance | BindingFlags.NonPublic)
                        .Invoke(health, new object[] { hit });
                    else health.Hit(hit);
                    expected -= debit;
                    if (health.hp != expected) throw new Exception(label + ": expected hp " + expected + ", actual " + health.hp);
                    Mark("PASS " + label);
                }
                // 8 / the two-player 1.6 multiplier = 5, with no rounding remainder.
                Hit(local, 8, AttackTypes.Nail, 5, "first player hit with dynamic health");
                Hit(local, 8, AttackTypes.Nail, 0, "same player duplicate remains blocked");
                Hit(remote, 8, AttackTypes.Nail, 5, "second player hits in the same frame");
                Hit(remote, 8, AttackTypes.Nail, 0, "second player has its own duplicate protection");
                Hit(detachedRemote, 8, AttackTypes.Spell, 0, "detached remote spell shares its caster window");
                Hit(detachedLocal, 8, AttackTypes.Spell, 0, "detached local spell shares its caster window");
                var scale = Time.timeScale; var paused = Time.time;
                try {
                    Time.timeScale = 0;
                    yield return new WaitForSecondsRealtime(.25f);
                    if (Math.Abs(Time.time - paused) > .001f) throw new Exception("Evasion pause fixture time advanced");
                    Hit(local, 8, AttackTypes.Nail, 0, "pause retains first player native evasion");
                    Hit(remote, 8, AttackTypes.Nail, 0, "pause retains second player native evasion");
                } finally { Time.timeScale = scale; }
                yield return new WaitForSeconds(.25f);
                Hit(detachedRemote, 8, AttackTypes.Spell, 5, "remote spell hits after caster window expires");
                Hit(detachedLocal, 8, AttackTypes.Spell, 5, "local spell independently follows remote spell");
                yield return new WaitForSeconds(.25f);
                var invincible = health.IsInvincible; var direction = health.InvincibleFromDirection;
                try {
                    health.IsInvincible = true; health.InvincibleFromDirection = 0;
                    Hit(local, 8, AttackTypes.Nail, 0, "phase invincibility blocks first player");
                    Hit(remote, 8, AttackTypes.Nail, 0, "phase invincibility blocks second player");
                    health.InvincibleFromDirection = 1;
                    yield return new WaitForSeconds(.2f);
                    Hit(local, 8, AttackTypes.Nail, 0, "native direction invincibility still blocks");
                } finally { health.IsInvincible = invincible; health.InvincibleFromDirection = direction; }
                yield return new WaitForSeconds(.25f);
                Hit(local, 8, AttackTypes.Nail, 5, "first player native window expires");
                Hit(remote, 8, AttackTypes.Nail, 5, "second player native window expires independently");
                Hit(local, 8, AttackTypes.Nail, 5, "direct native damage retains its evasion bypass", true);
                Hit(local, 8, AttackTypes.Nail, 5, "repeated direct native damage remains unchanged", true);
                yield return new WaitForSeconds(.25f);
                var alternate = typeof(HealthManager).GetField("hasAlternateHitAnimation", BindingFlags.Instance | BindingFlags.NonPublic);
                var animation = typeof(HealthManager).GetField("alternateHitAnimation", BindingFlags.Instance | BindingFlags.NonPublic);
                var hadAlternate = alternate.GetValue(health); var oldAnimation = animation.GetValue(health);
                try {
                    alternate.SetValue(health, true);
                    animation.SetValue(health, health.GetComponent<tk2dSpriteAnimator>().CurrentClip.name);
                    Hit(local, 8, AttackTypes.Nail, 5, "alternate animation accepts native first hit");
                    Hit(local, 8, AttackTypes.Nail, 5, "alternate animation gains no artificial cooldown");
                } finally { alternate.SetValue(health, hadAlternate); animation.SetValue(health, oldAnimation); }
                File.WriteAllText(Path.Combine(root, "evasion.expected-hp"), expected.ToString());
            } finally { Object.Destroy(detachedLocal); Object.Destroy(detachedRemote); Object.Destroy(prototype); }
        }
        yield return Until(() => Has("evasion.expected-hp") && health.hp == int.Parse(File.ReadAllText(Path.Combine(root, "evasion.expected-hp"))),
            "independent hits converge to one authoritative health total");
        yield return Both("evasion-checked");
        Mark("FINAL PASS independent player evasion, dynamic health, spell ownership, pause, invincibility and native bypasses");
        yield return Finish();
    }
}
