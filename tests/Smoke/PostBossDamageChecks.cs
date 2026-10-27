using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using Hkmp.Game.Client.Entity;
using UnityEngine;

namespace HollowConnect.Smoke;

public sealed partial class RescueProbe {
    private bool PostBossDamageCheck => Environment.GetEnvironmentVariable("HOLLOWCONNECT_POST_BOSS_DAMAGE_CHECK") == "1";
    private string postBossPlayerDamage;
    private static string PlayerDamageSignature() => string.Join(",", new[] {
        "nailDamage", "nailSmithUpgrades", "fireballLevel", "quakeLevel", "screamLevel"
    }.Select(n => n + "=" + PlayerData.instance.GetInt(n)));
    private static void NativeDamage(HealthManager health, AttackTypes type, int amount, float multiplier = 1) =>
        typeof(HealthManager).GetMethod("TakeDamage", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(health, new object[] {
            new HitInstance { Source = HeroController.instance.gameObject, AttackType = type, DamageDealt = amount,
                Multiplier = multiplier, IgnoreInvulnerable = true }
        });
    private static void NativePlayerExtra(HealthManager health, int amount) =>
        typeof(ExtraDamageable).GetMethod("ApplyExtraDamageToHealthManager", BindingFlags.Instance | BindingFlags.NonPublic)
            .Invoke(health.GetComponent<ExtraDamageable>(), new object[] { amount });
    private void CheckNativeBossDamage(HealthManager mother) {
        Check(Math.Abs(runtime.CurrentBossHealthMultiplier() - 1.6) < .00001, "two real participants enable boss scaling before actual death");
        var before = mother.hp;
        NativeDamage(mother, AttackTypes.Nail, 16);
        Check(mother.hp == before - 10, "living mother receives scaled nail damage");
        before = mother.hp; NativeDamage(mother, AttackTypes.Spell, 8, 2);
        Check(mother.hp == before - 10, "living mother receives scaled spell damage");
        Check(mother.GetComponent<ExtraDamageable>() != null, "mother has native player extra-damage receiver");
        before = mother.hp; NativePlayerExtra(mother, 8);
        Check(mother.hp == before - 5, "living mother receives scaled player extra damage");
        Check(PlayerDamageSignature() == postBossPlayerDamage, "boss scaling never changes player damage properties");
    }
    private IEnumerator PostBossOrdinaryDamageChecks(HealthManager[] children, Dictionary<ulong, Entity> entities, bool authority) {
        var other = host ? "guest" : "host";
        Check(PlayerDamageSignature() == postBossPlayerDamage, "actual boss death leaves player damage properties unchanged");
        Check(Math.Abs(runtime.CurrentBossHealthMultiplier() - 1.6) < .00001, "ordinary damage check runs while two-player boss setting remains enabled");
        if (authority) {
            var child = children.OrderBy(h => EntityReferences.Owner(h.gameObject).Id).First();
            var id = EntityReferences.Owner(child.gameObject).Id;
            var before = child.hp;
            Check(before > 4, "native ordinary child survives all nonfatal damage checks");
            NativeDamage(child, AttackTypes.Nail, 1);
            Check(child.hp == before - 1, "after actual boss death ordinary nail damage is full 1 not scaled to zero");
            NativeDamage(child, AttackTypes.Spell, 1, 2);
            Check(child.hp == before - 3, "after actual boss death ordinary spell damage keeps native multiplier");
            Check(child.GetComponent<ExtraDamageable>() != null, "ordinary child has native player extra-damage receiver");
            NativePlayerExtra(child, 1);
            Check(child.hp == before - 4, "after actual boss death ordinary player extra damage is full 1");
            File.WriteAllText(Path.Combine(root, "postboss.damage-result"), id + "," + child.hp);
        } else {
            yield return Until(() => Has("postboss.damage-result"), "authority checked ordinary damage after real boss death");
            var result = File.ReadAllText(Path.Combine(root, "postboss.damage-result")).Split(',');
            var id = ulong.Parse(result[0]); var hp = int.Parse(result[1]);
            yield return Until(() => entities.TryGetValue(id, out var e) && e.Object.Client != null &&
                e.Object.Client.GetComponent<HealthManager>().hp == hp, "ordinary enemy native damage converges on remote replica");
        }
        Flag(role + ".postboss-damage");
        yield return Until(() => Has(other + ".postboss-damage"), "both observe ordinary damage before child wave ends");
    }
    private IEnumerator PostBossRoomCleanupChecks() {
        Check(PlayerDamageSignature() == postBossPlayerDamage, "completed encounter leaves player damage properties unchanged");
        var hero = HeroController.instance;
        GameManager.instance.BeginSceneTransition(new GameManager.SceneLoadInfo { SceneName = "Town", EntryGateName = "right1",
            Visualization = GameManager.SceneLoadVisualizations.Default, WaitForSceneTransitionCameraFade = true });
        yield return Until(() => runtime.Playable && GameManager.instance.sceneName == "Town" &&
            !GameManager.instance.IsInSceneTransition && hero.acceptingInput && engine.ClientManager.SceneSnapshotReady,
            "native return from completed boss arena");
        var targets = (IDictionary)typeof(BossDamageScaling).GetField("Targets", BindingFlags.Static | BindingFlags.NonPublic).GetValue(null);
        if (targets.Count != 0) foreach (DictionaryEntry target in targets) {
            var component = ((Delegate)target.Value).Target;
            var health = Field<HostClientPair<HealthManager>>(component, "_healthManager").Host;
            Log("POSTBOSS REMAINING target=" + target.Key + " boss=" + Field<EntityType>(component, "_entityType") +
                " id=" + Field<ulong>(component, "_bossId") + " object=" + (health == null ? "destroyed" : health.name + "@" + health.gameObject.scene.name));
        }
        yield return Until(() => targets.Count == 0, "arena unload removes all boss damage registrations", 3);
        Check(PlayerDamageSignature() == postBossPlayerDamage, "leaving boss room leaves player damage properties unchanged");
    }
}
