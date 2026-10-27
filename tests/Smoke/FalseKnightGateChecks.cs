using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Hkmp.Game.Client.Entity;
using Hkmp.Util;
using UnityEngine;
using Object = UnityEngine.Object;

namespace HollowConnect.Smoke;

public sealed partial class RescueProbe {
    private IEnumerator FalseKnightGateChecks() {
        using var noContact = IgnoreExternalCombatDamage();
        var roles = (Environment.GetEnvironmentVariable("HOLLOWCONNECT_SMOKE_ROLES") ?? "host,guest").Split(',');
        var mode = Environment.GetEnvironmentVariable("HOLLOWCONNECT_FK_GATE_CHECK");
        var authorityRole = Environment.GetEnvironmentVariable("HOLLOWCONNECT_FK_AUTHORITY") ?? "host";
        var authority = role == authorityRole;
        var late = mode == "late-trigger" && role == roles.Last();
        bool All(string suffix) => roles.All(r => Has(r + suffix));
        yield return Until(() => All(".connected") && runtime.Peers.Count == roles.Length - 1, "complete real multiplayer roster");
        Check(runtime.Shared == (Environment.GetEnvironmentVariable("HOLLOWCONNECT_FK_SHARED") == "1"), "negotiated progress option");
        if (!authority) yield return Until(() => Has("fk.first"), "chosen first entrant");
        yield return RuleEnter("Crossroads_10");
        var manager = Field<EntityManager>(engine.ClientManager, "_entityManager");
        yield return Until(() => manager.IsSceneHostDetermined && manager.IsSceneHost == authority &&
            EntityReferences.Entities.Values.Any(e => e.Type == EntityType.FalseKnight), "native additive boss and authority");
        if (authority) Flag("fk.first");
        var boss = EntityReferences.Entities.Values.Single(e => e.Type == EntityType.FalseKnight);
        var control = boss.Object.Host.GetComponents<PlayMakerFSM>().Single(f => f.FsmName == "FalseyControl");
        var battle = Object.FindObjectsOfType<PlayMakerFSM>(true).Single(f => f.FsmName == "Battle Control" && f.gameObject.scene.name == "Crossroads_10_boss");
        var trigger = battle.GetComponent<Collider2D>();
        var gates = Object.FindObjectsOfType<PlayMakerFSM>(true).Where(f => f.FsmName == "BG Control" && f.gameObject.scene.name == "Crossroads_10").ToArray();
        var counter = battle.FsmVariables.GetFsmInt("Battle Enemies");
        var hero = HeroController.instance; var body = hero.GetComponent<Rigidbody2D>();
        using var completionTrace = new MonoMod.RuntimeDetour.Hook(typeof(LocalBattleCompletion).GetMethod("ObserveScriptedCompletion", BindingFlags.Static | BindingFlags.NonPublic),
            new Action<Action<Entity, HutongGames.PlayMaker.FsmState>, Entity, HutongGames.PlayMaker.FsmState>((orig, e, state) => {
                var inspect = e.Type == EntityType.FalseKnight && (state.Name == "Decrement Battle Enemies" || state.Name == "Cough");
                if (inspect) {
                    var found = typeof(LocalBattleCompletion).GetMethod("FalseKnightBattle", BindingFlags.Static | BindingFlags.NonPublic).Invoke(null, new object[] { e, state }) as GameObject;
                    var terminal = state.Fsm.States.Single(s => s.Name == "Decrement Battle Enemies");
                    Log("FK RECEIPT state=" + state.Name + " source=" + e.SourceScene + " parent=" + e.Object.Host.transform.parent?.name +
                        " found=" + found + " counter=" + counter.Value + " terminal=" + string.Join(",", terminal.Actions.Select(a => a.GetType().Name + "/" + a.Enabled + "/" + a.Fsm?.Name + "/" + a.State?.Name)));
                }
                orig(e, state);
                if (inspect) Log("FK RECEIPT AFTER counter=" + counter.Value + " battle=" + battle.ActiveStateName);
            }));
        Log("FK PREFLIGHT authority=" + authority + " boss=" + control.ActiveStateName + " battle=" + battle.ActiveStateName + "/" + counter.Value +
            " trigger=" + trigger.bounds + " gates=" + string.Join(",", gates.Select(g => g.name + "@" + g.transform.position + ":" + g.ActiveStateName)));
        Check(trigger != null && trigger.isTrigger && gates.Length >= 2, "native additive trigger and main-scene gates count=" + gates.Length);
        if (!late) yield return Trigger();
        Flag(role + ".fk-ready"); yield return Until(() => All(".fk-ready"), "all entrants staged");
        if (authority) {
            foreach (var enemy in EntityReferences.Entities.Values.Where(e => !BossDefeatSignals.IsBoss(e.Type) && e.Type != EntityType.FalseKnightHead &&
                e.Object.Host != null && e.Object.Host.activeInHierarchy && e.Object.Host.GetComponent<HealthManager>() != null).ToArray())
                enemy.Object.Host.GetComponent<HealthManager>().ApplyExtraDamage(1000);
        }
        string Phase() {
            if (boss.IsNativeAuthority) return control.ActiveStateName;
            var fsms = Field<HostClientPair<List<PlayMakerFSM>>>(boss, "_fsms").Host;
            return Field<List<FsmSnapshot>>(boss, "_fsmSnapshots")[fsms.FindIndex(f => f.FsmName == "FalseyControl")].CurrentState;
        }
        yield return Until(() => Phase() == "Idle", "native prebattle enemies and boss intro finish", 60);
        Log("FK INTRO battle=" + battle.ActiveStateName + "/" + counter.Value + " gates=" + string.Join(",", gates.Select(g => g.name + ":" + g.ActiveStateName)));
        // Prayer gates at the lower exit start in Double Close; the arena's
        // own entrance gates start Opened and close after the local trigger.
        gates = gates.Where(g => g.name == "Battle Gate" || g.name == "Battle Gate 1" || g.name == "Battle Gate 3").ToArray();
        Check(gates.Length == 3, "three native arena gates identified");
        if (!late) Check(counter.Value == 1 && gates.All(g => g.ActiveStateName == "Close 2"), "prebattle deaths leave one boss and closed arena gates");
        Flag(role + ".fk-intro"); yield return Until(() => All(".fk-intro"), "all clients inspect native intro");
        var hp = boss.Object.Host.GetComponent<HealthManager>();
        if (authority) hp.ApplyExtraDamage(hp.hp);
        yield return Until(() => Phase() == "Opened", "first armour break replicated", 30);
        Check(!PlayerData.instance.GetBool("killedFalseKnight") && (late || gates.All(g => g.ActiveStateName == "Close 2")), "armour break keeps gates closed");
        Flag(role + ".fk-armour"); yield return Until(() => All(".fk-armour"), "all clients reject premature completion");
        if (mode == "schema") yield break;
        if (mode == "handoff") {
            if (authority) { yield return RuleEnter("Town"); Flag("fk.authority-left"); }
            yield return Until(() => Has("fk.authority-left"), "original authority left arena");
            if (!authority) yield return Until(() => manager.IsSceneHostDetermined, "survivor authority determined");
            if (!authority && manager.IsSceneHost) Flag("fk.handoff-ready");
            yield return Until(() => Has("fk.handoff-ready"), "new native authority ready");
            if (authority) {
                yield return RuleEnter("Crossroads_10");
                battle = Object.FindObjectsOfType<PlayMakerFSM>(true).Single(f => f.FsmName == "Battle Control" && f.gameObject.scene.name == "Crossroads_10_boss");
                yield return Trigger();
            }
            boss = EntityReferences.Entities.Values.Single(e => e.Type == EntityType.FalseKnight);
            control = boss.Object.Host.GetComponents<PlayMakerFSM>().Single(f => f.FsmName == "FalseyControl");
            hp = boss.Object.Host.GetComponent<HealthManager>();
            if (authority) {
                battle = Object.FindObjectsOfType<PlayMakerFSM>(true).Single(f => f.FsmName == "Battle Control" && f.gameObject.scene.name == "Crossroads_10_boss");
                counter = battle.FsmVariables.GetFsmInt("Battle Enemies");
                gates = Object.FindObjectsOfType<PlayMakerFSM>(true).Where(f => f.FsmName == "BG Control" && f.gameObject.scene.name == "Crossroads_10" &&
                    (f.name == "Battle Gate" || f.name == "Battle Gate 1" || f.name == "Battle Gate 3")).ToArray();
            }
            Flag(role + ".fk-handoff"); yield return Until(() => All(".fk-handoff"), "all players back after authority transfer");
        }
        var end = Time.realtimeSinceStartup + 120; var nextTrace = 0f;
        while (!Has("fk.terminal") && Time.realtimeSinceStartup < end) {
            if (Time.realtimeSinceStartup >= nextTrace) {
                nextTrace = Time.realtimeSinceStartup + 2;
                Log("FK PHASE native=" + boss.IsNativeAuthority + " state=" + control.ActiveStateName + " battle=" + battle.ActiveStateName + "/" + counter.Value +
                    " gates=" + string.Join(",", gates.Select(g => g.ActiveStateName)));
            }
            if (boss.IsNativeAuthority) {
                var head = EntityReferences.Entities.Values.Single(e => e.Type == EntityType.FalseKnightHead).Object.Host.GetComponent<HealthManager>();
                if (control.ActiveStateName.StartsWith("Opened", StringComparison.Ordinal) && head.hp > 0) head.ApplyExtraDamage(head.hp);
                else if (control.ActiveStateName == "Idle" && hp.hp > 0) hp.ApplyExtraDamage(hp.hp);
                if (control.ActiveStateName == "Cough") Flag("fk.terminal");
            }
            yield return new WaitForSecondsRealtime(.15f);
        }
        Check(Has("fk.terminal"), "native final landing and scripted decrement completed");
        if (late) yield return Trigger();
        gates = Object.FindObjectsOfType<PlayMakerFSM>(true).Where(f => f.FsmName == "BG Control" && f.gameObject.scene.name == "Crossroads_10").ToArray();
        Log("FK TERMINAL phase=" + Phase() + " battle=" + battle.ActiveStateName + "/" + counter.Value + " gates=" + string.Join(",", gates.Select(g => g.ActiveStateName)));
        yield return Until(() => battle.ActiveStateName == "End" && counter.Value == 0 && gates.All(g => g.ActiveStateName == "Open"),
            "local counter zero and all native gates open", 20);
        yield return Until(() => gates.All(g => g.GetComponentsInChildren<Collider2D>(true).All(c => !c.enabled || c.isTrigger || !c.gameObject.activeInHierarchy)),
            "all native gates finish opening and remove blocking collision", 5);
        // Repeated final state observations must never decrement again.
        for (var i = 0; i < 3; i++) LocalBattleCompletion.ObserveScriptedCompletion(boss, control.FsmStates.Single(s => s.Name == "Cough"));
        Check(counter.Value == 0, "duplicate terminal receipts are idempotent");
        foreach (var gate in gates) {
            var x = gate.transform.position.x;
            hero.transform.position = hero.FindGroundPoint(new Vector3(x - 2, gate.transform.position.y + 2, 0), true) + Vector3.up * .1f;
            body.velocity = Vector2.zero; hero.AffectedByGravity(true); Physics2D.SyncTransforms();
            yield return Until(() => hero.cState.onGround, "native ground beside " + gate.name);
            using (new MonoMod.RuntimeDetour.Hook(typeof(HeroController).GetMethod("LookForInput", BindingFlags.Instance | BindingFlags.NonPublic),
                new Action<Action<HeroController>, HeroController>((orig, self) => { orig(self); if (self.acceptingInput) self.move_input = 1f; })))
                yield return Until(() => hero.transform.position.x > x + 1, "actual input walks through " + gate.name, 8);
        }
        Check(!runtime.Down && hero.acceptingInput && !hero.controlReqlinquished, "living participant keeps native control");
        Flag(role + ".fk-complete"); yield return Until(() => All(".fk-complete"), "every participant walked out");

        IEnumerator Trigger() {
            // Placement enters the real trigger; the gate check later uses real walking.
            trigger = battle.GetComponent<Collider2D>();
            hero.transform.position = trigger.bounds.center; body.velocity = Vector2.zero; hero.AffectedByGravity(false); Physics2D.SyncTransforms();
            yield return new WaitForSecondsRealtime(1);
            hero.transform.position = new Vector3(25 + Array.IndexOf(roles, role), 33, 0); body.velocity = Vector2.zero; hero.AffectedByGravity(true);
            yield return Until(() => hero.cState.onGround && hero.acceptingInput, "native arena floor after trigger");
        }
    }
}
