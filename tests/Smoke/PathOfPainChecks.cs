using System;
using System.Collections;
using System.Linq;
using System.Reflection;
using Hkmp.Game.Client.Entity;
using HollowConnect;
using UnityEngine;
using Object = UnityEngine.Object;

namespace HollowConnect.Smoke;

public sealed partial class RescueProbe {
    private IEnumerator PathOfPainChecks() {
        const string scene = "White_Palace_20";
        var late = Environment.GetEnvironmentVariable("HOLLOWCONNECT_PATH_OF_PAIN_CHECK") == "late";
        var hero = HeroController.instance;
        var body = hero.GetComponent<Rigidbody2D>();
        using var damage = IgnoreExternalCombatDamage();
        var manager = Field<EntityManager>(engine.ClientManager, "_entityManager");
        var other = host ? "guest" : "host";
        if (!host) {
            yield return RuleEnter(scene, "bot1");
            yield return Until(() => manager.IsSceneHostDetermined && manager.IsSceneHost, "guest enters palace first as scene authority");
            DumpPalace();
            var nativeBattle = Object.FindObjectsOfType<PlayMakerFSM>(true).Single(f => f.gameObject.scene.name == scene && f.FsmName == "Battle Control");
            var trigger = nativeBattle.GetComponent<Collider2D>();
            Check(trigger != null && trigger.isTrigger, "native palace battle trigger exists");
            hero.AffectedByGravity(false); body.velocity = Vector2.zero;
            hero.transform.position = trigger.bounds.center; Physics2D.SyncTransforms();
            yield return new WaitForSecondsRealtime(1);
            DumpPalace();
            if (late) {
                var guards = EntityReferences.Entities.Values.Where(e => e.Type == EntityType.Kingsmould && e.SourceScene == scene && e.Object.Host != null).ToArray();
                Check(guards.Length == 2, "two native guardians before late arrival");
                foreach (var guard in guards) {
                    var hp = guard.Object.Host.GetComponent<HealthManager>(); hp.ApplyExtraDamage(hp.hp);
                    yield return new WaitForSecondsRealtime(.5f);
                }
                yield return Until(() => nativeBattle.ActiveStateName == "Open", "authority completes native battle before late arrival");
                Flag("palace.guards-defeated");
            }
            Flag("palace.guest-triggered");
        } else {
            yield return Until(() => Has("palace.guest-triggered"), "guest starts native guardian battle before host teleport");
            yield return Until(() => runtime.Peers.Values.Any(p => p.State?.Scene == scene && p.State.IsReady), "guest advertises warp readiness inside palace battle");
            runtime.Selected = runtime.Peers.Single().Key;
            var pressed = false;
            runtime.KeyPressed = key => key == CoopMod.Settings.TeleportKey && !pressed && (pressed = true);
            yield return Until(() => runtime.Playable && GameManager.instance.sceneName == scene && hero.acceptingInput &&
                !GameManager.instance.IsInSceneTransition && engine.ClientManager.SceneSnapshotReady, "host joins through actual configured teammate teleport key");
            runtime.KeyPressed = _ => false; hero.AffectedByGravity(false); body.velocity = Vector2.zero;
            DumpPalace();
        }
        Flag(role + ".palace.joined");
        yield return Until(() => Has(other + ".palace.joined"), "both present in guardian arena");
        var battle = Object.FindObjectsOfType<PlayMakerFSM>(true).Single(f => f.gameObject.scene.name == scene && f.FsmName == "Battle Control");
        if (Environment.GetEnvironmentVariable("HOLLOWCONNECT_PATH_OF_PAIN_CHECK") == "schema") yield break;
        if (!late && !host) {
            var guards = EntityReferences.Entities.Values.Where(e => e.Type == EntityType.Kingsmould && e.SourceScene == scene && e.Object.Host != null).ToArray();
            Check(guards.Length == 2, "exactly two native Kingsmould entities");
            foreach (var guard in guards) {
                var hp = guard.Object.Host.GetComponent<HealthManager>(); hp.ApplyExtraDamage(hp.hp);
                if (!Has("palace.first-guard-defeated")) {
                    Flag("palace.first-guard-defeated");
                    yield return Until(() => Has("host.palace.first-checked"), "host checks partial guardian defeat before final kill");
                }
                yield return new WaitForSecondsRealtime(.5f);
            }
            Flag("palace.guards-defeated");
        } else if (!late) {
            yield return Until(() => Has("palace.first-guard-defeated"), "first native guardian killed");
            yield return new WaitForSecondsRealtime(.4f);
            Check(battle.ActiveStateName != "Open" && battle.ActiveStateName != "End Pause" && battle.ActiveStateName != "Sting",
                "one dead guardian never completes the two-guardian battle");
            Flag("host.palace.first-checked");
        }
        yield return Until(() => Has("palace.guards-defeated"), "guest authority defeats guardians");
        yield return new WaitForSecondsRealtime(7); DumpPalace();
        Check(battle.ActiveStateName == "Open", "native guardian battle reaches Open on both endpoints");
        var gate = Object.FindObjectsOfType<PlayMakerFSM>(true).Single(f => f.gameObject.scene.name == scene && f.name == "Gate" && f.FsmName == "FSM");
        Check(!gate.GetComponents<Collider2D>().Any(c => c.enabled && !c.isTrigger), "native right seal collider opens locally");
        hero.transform.position = hero.FindGroundPoint(new Vector3(243, 124, 0), true) + Vector3.up * .1f;
        body.velocity = Vector2.zero; hero.AffectedByGravity(true); Physics2D.SyncTransforms();
        yield return Until(() => hero.cState.onGround, "native floor before palace seal");
        yield return StoryWalk(250, "through the native palace seal");
        Flag(role + ".palace.passed");
        yield return Until(() => Has(other + ".palace.passed"), "both players physically pass the opened seal");
        // Return to the supported arena floor before the ordinary shade death.
        hero.transform.position = hero.FindGroundPoint(new Vector3(230, 124, 0), true) + Vector3.up * .1f;
        body.velocity = Vector2.zero; Physics2D.SyncTransforms();
        yield return Until(() => hero.cState.onGround, "native guardian platform supports shade episode");
        if (host) {
            var round = Hkmp.CoopContext.Epoch;
            Die(); yield return Until(() => runtime.Down && !runtime.Spectating, "host dies into ordinary palace shade");
            var serial = Field<uint>(runtime, "downSerial");
            Flag("palace.host-shade");
            yield return Until(() => Has("palace.guest-hub") && runtime.Peers.Values.Single().State?.Scene == "White_Palace_03_hub",
                "shade sees last teammate in White Palace hub");
            var pressed = false;
            runtime.KeyPressed = key => key == CoopMod.Settings.GiveUpKey && !pressed && (pressed = true);
            yield return RuleArrived("F6 without same-room spectator target");
            runtime.KeyPressed = _ => false;
            Check(Field<uint>(runtime, "downSerial") == serial && Hkmp.CoopContext.Epoch == round && runtime.RescuesUsed == 0,
                "F6 fallback preserves death identity round and rescue budget");
            Flag("palace.shade-returned");
        } else {
            yield return Until(() => Has("palace.host-shade"), "host remains shade before teammate leaves");
            yield return RuleEnter("White_Palace_03_hub", "left1"); Flag("palace.guest-hub");
            yield return Until(() => Has("palace.shade-returned"), "F6 returns abandoned shade without a full-party reset", 80);
            Check(runtime.Playable && !runtime.Down && GameManager.instance.sceneName == "White_Palace_03_hub",
                "living hub teammate remains in own room");
        }
    }

    private void DumpPalace() {
        foreach (var f in Object.FindObjectsOfType<PlayMakerFSM>(true).Where(f => f.gameObject.scene.name == "White_Palace_20" &&
            (f.name.Contains("Battle") || f.name.Contains("Gate") || f.name.Contains("Gaurd")))) {
            Log("PALACE FSM " + f.name + "/" + f.FsmName + "=" + f.ActiveStateName + " pos=" + f.transform.position +
                " ints=" + string.Join(",", f.FsmVariables.IntVariables.Select(v => v.Name + "=" + v.Value)) +
                " cols=" + string.Join(",", f.GetComponents<Collider2D>().Select(c => c.enabled + ":" + c.isTrigger + ":" + c.bounds)));
            foreach (var a in f.FsmStates.SelectMany(s => s.Actions).OfType<HutongGames.PlayMaker.Actions.GameObjectHasChildren>()) {
                var target = f.Fsm.GetOwnerDefaultTarget(a.gameObject);
                Log("PALACE CHILD CHECK target=" + target + " children=" + (target == null ? "null" :
                    string.Join(",", Enumerable.Range(0, target.transform.childCount).Select(i => {
                        var child = target.transform.GetChild(i); var hp = child.GetComponent<HealthManager>();
                        return child.name + ":active=" + child.gameObject.activeSelf + ":hp=" + hp?.hp + ":dead=" + hp?.isDead + ":entity=" + EntityReferences.Owner(child.gameObject)?.Type;
                    }))));
            }
            if (Environment.GetEnvironmentVariable("HOLLOWCONNECT_PATH_OF_PAIN_CHECK") != "schema") continue;
            foreach (var state in f.FsmStates) {
                Log("PALACE SCHEMA " + f.name + "/" + state.Name + " -> " + string.Join(",", state.Transitions.Select(t => t.EventName + ":" + t.ToState)));
                foreach (var a in state.Actions) Log("PALACE ACTION " + state.Name + "/" + a.GetType().Name + " " +
                    string.Join(";", a.GetType().GetFields(BindingFlags.Instance | BindingFlags.Public).Select(v => v.Name + "=" + v.GetValue(a))));
            }
        }
    }
}
