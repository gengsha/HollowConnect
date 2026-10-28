using System;
using System.Collections;
using System.Linq;
using System.Reflection;
using Hkmp.Game.Client;
using Hkmp.Game.Client.Entity;
using HollowConnect;
using UnityEngine;
using Object = UnityEngine.Object;

namespace HollowConnect.Smoke;

public sealed partial class ProgressProbe {
    private IEnumerator GrubChecks() {
        var pd = PlayerData.instance;
        var sawPopup = false;
        On.HutongGames.PlayMaker.Fsm.EnterState += Observe;
        void Observe(On.HutongGames.PlayMaker.Fsm.orig_EnterState orig, HutongGames.PlayMaker.Fsm fsm, HutongGames.PlayMaker.FsmState state) {
            if (fsm.GameObject != null && fsm.GameObject.name == ResourceNotifications.PopupName && state.Name == "Up") {
                var text = fsm.GameObject.transform.Find("Text").GetComponent<TMPro.TextMeshPro>().text;
                if (text.StartsWith("共享幼虫", StringComparison.Ordinal)) {
                    var point = GameCameras.instance.hudCamera.WorldToViewportPoint(fsm.GameObject.transform.position);
                    ResourceCheck(point.x >= 0 && point.x <= .3f && point.y >= 0 && point.y <= .3f && point.z > 0,
                        "shared grub notice appears in lower-left HUD");
                    sawPopup = true;
                }
            }
            orig(fsm, state);
        }
        try {
            yield return Room("Crossroads_03");
            var bottle = Object.FindObjectsOfType<PlayMakerFSM>().Single(f => f.FsmName == "Bottle Control");
            if (host) yield return BreakGrub(bottle);
            yield return Until(() => pd.GetInt("grubsCollected") == 1 && LocalResourceClaims.Contains("grub:Crossroads_03/Grub Bottle"), "first grub shares once", 15);
            if (!host) {
                yield return Until(() => sawPopup, "remote grub HUD notification", 10);
                ResourceCheck(bottle.ActiveStateName == "Idle", "shared grub keeps local bottle interaction available");
                yield return BreakGrub(bottle);
                ResourceCheck(pd.GetInt("grubsCollected") == 1, "native collection of shared grub does not count twice");
            }
            yield return ResourceBarrier("same-grub-collected");
            yield return Room("Ruins2_11");
            yield return CompleteCollectorForGrubs();
            // Reload through the ordinary scene-conditionals after the native
            // victory; this also verifies that the real defeat persisted.
            yield return Room("Town");
            yield return Room("Ruins2_11");
            var candidates = Object.FindObjectsOfType<PlayMakerFSM>(true).Where(f => f.FsmName == "Bottle Control").ToArray();
            Mark("COLLECTOR BOTTLES " + string.Join(",", candidates.Select(f => f.gameObject.scene.name + "/" + f.name + " active=" + f.gameObject.activeInHierarchy)));
            var bottles = candidates.Where(f => f.gameObject.scene.name == "Ruins2_11" &&
                    f.GetComponent<PersistentBoolItem>()?.persistentBoolData.id == "Grub Bottle")
                .OrderBy(f => f.transform.position.x).ThenBy(f => f.transform.position.y).ToArray();
            ResourceCheck(bottles.Length == 3, "three native Collector bottles available; actual=" + bottles.Length);
            yield return BreakGrub(bottles[host ? 0 : 1]);
            yield return Until(() => pd.GetInt("grubsCollected") == 3, "different Collector bottles share independently", 15);
            ResourceCheck(!pd.scenesGrubRescued.Contains("Ruins2_11"), "partial Collector collection keeps map marker");
            yield return ResourceBarrier("collector-partial");
            if (host) yield return BreakGrub(bottles[2]);
            yield return Until(() => pd.GetInt("grubsCollected") == 4 && pd.scenesGrubRescued.Contains("Ruins2_11"), "all Collector receipts complete map marker", 15);
            yield return ResourceBarrier("collector-complete");
            if (!host) {
                engine.ClientManager.Disconnect();
                yield return Until(() => !runtime.Connected, "grub guest disconnect");
                // Disconnect clears this flag. The disposable character is
                // already loaded, so reconnect through the in-game path.
                typeof(ClientManager).GetField("_autoConnect", BindingFlags.Instance | BindingFlags.NonPublic).SetValue(engine.ClientManager, true);
                typeof(ClientManager).GetMethod("Connect", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(engine.ClientManager,
                    new object[] { "127.0.0.1", 29650, "progressguest" });
                yield return Until(() => runtime.Connected, "grub guest reconnect");
                yield return new WaitForSecondsRealtime(2);
                ResourceCheck(pd.GetInt("grubsCollected") == 4 && LocalResourceClaims.Claimed.Count(id => id.StartsWith("grub:")) == 4,
                    "reconnect keeps unique grub count and receipts");
                Flag("grub.reconnect-checked");
            } else yield return Until(() => Has("grub.reconnect-checked"), "guest grub reconnect checked", 30);
        } finally { On.HutongGames.PlayMaker.Fsm.EnterState -= Observe; }
    }
    private IEnumerator BreakGrub(PlayMakerFSM bottle) {
        var hero = HeroController.instance;
        var body = hero.GetComponent<Rigidbody2D>();
        body.isKinematic = false; hero.AffectedByGravity(true); body.gravityScale = 0; body.velocity = Vector2.zero;
        // Enter the actual range above the solid bottle, with native trigger
        // contact enabled; placing the hero inside the bottle can eject it.
        var range = bottle.transform.Find("Hero Range").GetComponent<Collider2D>();
        hero.transform.position = range.bounds.center;
        Mark("GRUB ENTRY " + bottle.gameObject.scene.name + "/" + bottle.name + " state=" + bottle.ActiveStateName +
            " range=" + range.bounds + " hero=" + hero.transform.position + " kinematic=" + body.isKinematic);
        yield return new WaitForSecondsRealtime(.25f);
        yield return Until(() => bottle.FsmVariables.FindFsmBool("Hero Range").Value, "native grub attack range", 8);
        ResourceCheck(!Has(role + ".failed"), "native bottle in attack range before input");
        bottle.SendEvent("NAIL HIT");
        yield return ResourceIdle("grub bottle");
    }
    private IEnumerator CompleteCollectorForGrubs() {
        yield return Until(() => EntityReferences.Entities.Values.Any(e => e.Type == EntityType.TheCollector), "native Collector registered", 15);
        ResourceCheck(!Has(role + ".failed"), "Collector fixture has native entity");
        var entity = EntityReferences.Entities.Values.Single(e => e.Type == EntityType.TheCollector);
        var battle = Object.FindObjectsOfType<PlayMakerFSM>().Single(f => f.name == "Battle Scene" && f.FsmName == "Control" &&
            f.gameObject.scene.name.StartsWith("Ruins2_11", StringComparison.Ordinal));
        var gates = Object.FindObjectsOfType<PlayMakerFSM>().Where(f => f.FsmName == "BG Control" &&
            f.gameObject.scene.name.StartsWith("Ruins2_11", StringComparison.Ordinal)).ToArray();
        ResourceCheck(gates.Length == 2, "native Collector gates available");
        var hero = HeroController.instance;
        hero.transform.position = battle.GetComponent<Collider2D>().bounds.center;
        hero.AffectedByGravity(true); hero.GetComponent<Rigidbody2D>().gravityScale = 1; Physics2D.SyncTransforms();
        yield return ResourceBarrier("collector-arena-ready");
        yield return Until(() => gates.All(g => g.ActiveStateName == "Close 2"), "native Collector encounter begins", 20);
        ResourceCheck(!Has(role + ".failed"), "Collector native entry conditions met");
        var end = Time.realtimeSinceStartup + 70;
        while (Time.realtimeSinceStartup < end && !(PlayerData.instance.GetBool("collectorDefeated") && gates.All(g => g.ActiveStateName == "Open"))) {
            if (entity.IsNativeAuthority && entity.Object.Host != null) {
                var health = entity.Object.Host.GetComponent<HealthManager>();
                var death = entity.Object.Host.GetComponents<PlayMakerFSM>().Single(f => f.FsmName == "Death");
                if (death.ActiveStateName == "Death Wound")
                    health.Hit(new HitInstance { Source = hero.gameObject, AttackType = AttackTypes.Nail, DamageDealt = 1, Multiplier = 1, IgnoreInvulnerable = true });
                else if (health != null && !health.IsInvincible && health.hp > 0) health.ApplyExtraDamage(health.hp);
            }
            yield return new WaitForSecondsRealtime(.5f);
        }
        ResourceCheck(PlayerData.instance.GetBool("collectorDefeated") && gates.All(g => g.ActiveStateName == "Open"),
            "native Collector death and gates complete before grub collection; battle=" + battle.ActiveStateName);
        yield return ResourceBarrier("collector-native-victory");
    }
}
