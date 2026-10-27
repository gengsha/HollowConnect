using System;
using System.Collections;
using System.Linq;
using Hkmp.Game.Client.Entity;
using HutongGames.PlayMaker;
using UnityEngine;
using Object = UnityEngine.Object;

namespace HollowConnect.Smoke;

public sealed partial class RescueProbe {
    private IEnumerator CollectorGateChecks() {
        using var noContact = IgnoreExternalCombatDamage();
        yield return LocalBarrierEventChecks.Run(Check);
        var other = host ? "guest" : "host";
        On.HutongGames.PlayMaker.Fsm.hook_ProcessEvent trace = (orig, fsm, ev, data) => {
            if (fsm.GameObject != null && fsm.GameObject.scene.name.StartsWith("Ruins2_11", StringComparison.Ordinal) && ev != null &&
                (ev.Name.Contains("BG ") || ev.Name.Contains("BATTLE") || ev.Name.Contains("DIE")))
                Log("COLLECTOR EVENT " + fsm.GameObject.name + "/" + fsm.Name + "=" + fsm.ActiveStateName + " event=" + ev.Name);
            orig(fsm, ev, data);
        };
        On.HutongGames.PlayMaker.Fsm.ProcessEvent += trace;
        try {
            Flag(role + ".collector-entry"); yield return Until(() => Has(other + ".collector-entry"), "both simultaneous entrants");
            yield return RuleEnter("Ruins2_11", "right1");
            yield return Until(() => EntityReferences.Entities.Values.Any(e => e.Type == EntityType.TheCollector), "story Collector registered");
            var entity = EntityReferences.Entities.Values.Single(e => e.Type == EntityType.TheCollector);
            var nativeDeath = entity.Object.Host.GetComponents<PlayMakerFSM>().Single(f => f.FsmName == "Death");
            var terminal = nativeDeath.FsmStates.Single(s => s.Name == "Send Event");
            Check(BossGateCompletion.IsTerminal(entity.Type, terminal, "BATTLE END"), "native final signal follows verified defeat record");
            Check(!BossGateCompletion.IsTerminal(entity.Type, nativeDeath.FsmStates.Single(s => s.Name == "Death Wound"), "BATTLE END") &&
                !BossGateCompletion.IsTerminal(EntityType.SoulMaster, terminal, "BATTLE END") &&
                !BossGateCompletion.IsTerminal(EntityType.Crawlid, terminal, "BATTLE END") &&
                !BossGateCompletion.IsTerminal(entity.Type, terminal, "BG CLOSE"), "wounded boss, phase one, minions and close events cannot unlock gates");
            foreach (var f in entity.Object.Host.GetComponents<PlayMakerFSM>()) NoskDump(f);
            var fsms = Object.FindObjectsOfType<PlayMakerFSM>().Where(f => f.gameObject.scene.name.StartsWith("Ruins2_11", StringComparison.Ordinal)).ToArray();
            foreach (var f in fsms.Where(f => f.name.Contains("Battle") || f.name.Contains("Collector") || f.FsmName.Contains("Battle"))) {
                Log("COLLECTOR OBJECT " + f.name + "/" + f.FsmName + "=" + f.ActiveStateName + " pos=" + f.transform.position +
                    " collider=" + (f.GetComponent<Collider2D>()?.bounds.ToString() ?? "none"));
                NoskDump(f);
            }
            var hero = HeroController.instance;
            var battle = fsms.Single(f => f.name == "Battle Scene" && f.FsmName == "Control");
            hero.transform.position = battle.GetComponent<Collider2D>().bounds.center;
            hero.GetComponent<Rigidbody2D>().velocity = Vector2.zero; hero.AffectedByGravity(true); Physics2D.SyncTransforms();
            Flag(role + ".collector-ready"); yield return Until(() => Has(other + ".collector-ready"), "both arena entrants ready");
            var authority = entity.IsNativeAuthority;
            var gates = fsms.Where(f => f.FsmName == "BG Control").ToArray();
            yield return Until(() => gates.Length == 2 && gates.All(g => g.ActiveStateName == "Close 2"), "both native arena gates closed");
            for (var i = 0; i < 65; i++) {
                if (entity.IsNativeAuthority && i > 5 && entity.Object.Host != null) {
                    var health = entity.Object.Host.GetComponent<HealthManager>();
                    var death = entity.Object.Host.GetComponents<PlayMakerFSM>().Single(f => f.FsmName == "Death");
                    if (death.ActiveStateName == "Death Wound") {
                        Check(gates.All(g => g.ActiveStateName == "Close 2"), "gates stay closed during native finishing hits");
                        health.Hit(new HitInstance { Source = hero.gameObject, AttackType = AttackTypes.Nail, DamageDealt = 1, Multiplier = 1, IgnoreInvulnerable = true });
                    } else if (health != null && !health.IsInvincible && health.hp > 0) {
                        Log("COLLECTOR DAMAGE hp=" + health.hp + " dead=" + health.isDead + " special=" + health.hasSpecialDeath);
                        health.ApplyExtraDamage(health.hp);
                    }
                }
                if (PlayerData.instance.GetBool("collectorDefeated") && gates.All(g => g.ActiveStateName == "Open")) break;
                Log("COLLECTOR SAMPLE authority=" + entity.IsNativeAuthority + " hero=" + hero.transform.position +
                    " states=" + string.Join(",", fsms.Where(f => f != null && (f.name.Contains("Battle") || f.name.Contains("Collector") || f.FsmName.Contains("Battle"))).Select(f => f.name + "/" + f.FsmName + ":" + f.ActiveStateName)));
                yield return new WaitForSecondsRealtime(1);
            }
            Log("COLLECTOR RESULT authority=" + authority + " defeated=" + PlayerData.instance.GetBool("collectorDefeated") + " battle=" + battle.ActiveStateName + " gates=" + string.Join(",", gates.Select(g => g.ActiveStateName)));
            Check(PlayerData.instance.GetBool("collectorDefeated"), "native Collector death continuation completed");
            Check(battle.ActiveStateName == "End" && gates.All(g => g.ActiveStateName == "Open"), "both players' native arena gates open after Collector completion");
            Check(gates.All(g => g.GetComponentsInChildren<Collider2D>(true).All(c => !c.enabled || c.isTrigger || !c.gameObject.activeInHierarchy)), "native open gates have no blocking colliders");
            if (!authority) {
                foreach (var gate in gates) gate.SendEvent("BG CLOSE");
                yield return Until(() => gates.All(g => g.ActiveStateName == "Open"), "late local close reconciles completed encounter", 5);
                var previousVisit = Hkmp.CoopContext.LocalVisit;
                try {
                    Hkmp.CoopContext.LocalVisit++;
                    foreach (var gate in gates) gate.SendEvent("BG CLOSE");
                    yield return Until(() => gates.All(g => g.ActiveStateName == "Close 2"), "new scene visit discards old completion", 5);
                } finally { Hkmp.CoopContext.LocalVisit = previousVisit; }
                foreach (var gate in gates) gate.SendEvent("BG OPEN");
            }
            Flag(role + ".collector-complete"); yield return Until(() => Has(other + ".collector-complete"), "both collector probes finished");
        } finally { On.HutongGames.PlayMaker.Fsm.ProcessEvent -= trace; }
    }
}
