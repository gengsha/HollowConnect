using System;
using System.Collections;
using System.Linq;
using System.Reflection;
using Hkmp.Game.Client.Entity;
using HutongGames.PlayMaker;
using HutongGames.PlayMaker.Actions;
using UnityEngine;
using Object = UnityEngine.Object;

namespace HollowConnect.Smoke;

public sealed partial class RescueProbe {
    private IEnumerator BossFlowChecks() {
        if (Environment.GetEnvironmentVariable("HOLLOWCONNECT_BOSS_FLOW_CHECK") != "schema") {
            yield return MawlekLateEntryChecks(); yield break;
        }
        // Inventory only. Loading an arena and inspecting its native schema is
        // not a claim that its combat, rewards or exits have passed.
        var rooms = (Environment.GetEnvironmentVariable("HOLLOWCONNECT_BOSS_FLOW_ROOMS") ??
            "Crossroads_09:left1,Crossroads_04:left1,Crossroads_10:left1,Fungus1_04:left1,Fungus1_29:left1," +
            "Fungus2_15:top1,Ruins1_24:left1,Ruins2_03:top1,Ruins2_11:right1,Waterways_05:right1," +
            "Waterways_12:left1,Mines_18:left1,Deepnest_32:left1,Abyss_19:left1,Fungus3_23:left1,Hive_05:left1," +
            "Deepnest_East_Hornet:left1,Fungus3_archive_02:left1").Split(',');
        foreach (var room in rooms) {
            var split = room.Split(':'); var scene = split[0];
            if (scene == "Fungus3_23") PlayerData.instance.SetBool("hasShadowDash", true);
            if (!host) yield return Until(() => Has("flow.first." + scene), "inventory first entrant " + scene);
            yield return RuleEnter(scene, split[1]);
            if (host) Flag("flow.first." + scene);
            var hero = HeroController.instance; hero.AffectedByGravity(false); hero.GetComponent<Rigidbody2D>().velocity = Vector2.zero;
            yield return new WaitForSecondsRealtime(1);
            var fsms = Object.FindObjectsOfType<PlayMakerFSM>(true).Where(f =>
                f.gameObject.scene.name == scene || f.gameObject.scene.name.StartsWith(scene + "_boss", StringComparison.Ordinal))
                .Concat(EntityReferences.Entities.Values.Where(e => e.SourceScene == scene || e.SourceScene.StartsWith(scene + "_boss", StringComparison.Ordinal))
                    .SelectMany(e => new[] { e.Object.Host, e.Object.Client }).Where(o => o != null)
                    .SelectMany(o => o.GetComponentsInChildren<PlayMakerFSM>(true))).Distinct().ToArray();
            Check(fsms.Length > 0, "native FSM inventory present " + scene);
            foreach (var fsm in fsms) {
                var owner = EntityReferences.Owner(fsm.gameObject);
                if (owner?.Object.Client == fsm.gameObject) continue;
                if (fsm.FsmName == "Battle Control" || fsm.FsmName == "BG Control" || fsm.FsmName.Contains("Challenge") ||
                    owner != null && (BossDefeatSignals.IsBoss(owner.Type) || owner.Type == EntityType.MantisThrone)) EncounterSchema(fsm);
            }
            Log("BOSS FLOW INVENTORY " + scene + " entries=" + string.Join(",", Object.FindObjectsOfType<TransitionPoint>(true)
                .Where(t => t.gameObject.scene.name == scene).Select(t => t.name)) + " entities=" +
                string.Join(",", EntityReferences.Entities.Values.Select(e => e.Type + ":" + e.SourceScene).Distinct()));
            Flag(role + ".flow.schema." + scene);
            yield return Until(() => Has((host ? "guest" : "host") + ".flow.schema." + scene), "both inventories " + scene);
        }
    }

    private IEnumerator MawlekLateEntryChecks() {
        const string scene = "Crossroads_09";
        var authority = role == (Environment.GetEnvironmentVariable("HOLLOWCONNECT_BOSS_FLOW_AUTHORITY") ?? "host");
        var other = host ? "guest" : "host";
        using var noContact = IgnoreExternalCombatDamage();
        var delayInit = !authority && Environment.GetEnvironmentVariable("HOLLOWCONNECT_BOSS_FLOW_DELAY_INIT") == "1";
        NextFrameEvent heldFrame = null;
        Action resume = null;
        var starts = 0;
        On.HutongGames.PlayMaker.Fsm.hook_EnterState trace = (orig, fsm, state) => {
            if (fsm.Name == "Battle Control" && fsm.GameObject != null && fsm.GameObject.scene.name == scene) {
                if (state.Actions.OfType<SetIntValue>().Any(a => a.intVariable.Name == "Battle Enemies" && a.intValue.Value > 0)) starts++;
            }
            orig(fsm, state);
        };
        On.HutongGames.PlayMaker.Actions.NextFrameEvent.hook_OnEnter delay = (orig, action) => {
            if (delayInit && resume == null && action.Fsm.Name == "Battle Control" && action.Fsm.GameObject.scene.name == scene) {
                heldFrame = action;
                resume = () => { delayInit = false; orig(action); };
                Log("BATTLE FLOW fixture holds native NextFrameEvent before initialization"); return;
            }
            orig(action);
        };
        On.HutongGames.PlayMaker.Actions.NextFrameEvent.hook_OnUpdate delayUpdate = (orig, action) => {
            if (!delayInit || !ReferenceEquals(action, heldFrame)) orig(action);
        };
        On.HutongGames.PlayMaker.Fsm.EnterState += trace;
        On.HutongGames.PlayMaker.Actions.NextFrameEvent.OnEnter += delay;
        On.HutongGames.PlayMaker.Actions.NextFrameEvent.OnUpdate += delayUpdate;
        try {
        if (!authority) yield return Until(() => Has("flow.mawlek.woken"), "Mawlek wakes before later entrant");
        yield return RuleEnter(scene);
        var manager = Field<EntityManager>(engine.ClientManager, "_entityManager");
        Check(manager.IsSceneHost == authority, "Mawlek first entrant owns scene authority");
        yield return Until(() => EntityReferences.Entities.Values.Any(e => e.Type == EntityType.BroodingMawlek), "native Mawlek registered");
        var entity = EntityReferences.Entities.Values.Single(e => e.Type == EntityType.BroodingMawlek);
        var body = authority ? entity.Object.Host : entity.Object.Client;
        var battle = Object.FindObjectsOfType<PlayMakerFSM>(true).Single(f => f.gameObject.scene.name == scene && f.FsmName == "Battle Control");
        EncounterSchema(battle);
        foreach (var fsm in entity.Object.Host.GetComponents<PlayMakerFSM>()) EncounterSchema(fsm);
        var hero = HeroController.instance; hero.AffectedByGravity(false); hero.GetComponent<Rigidbody2D>().velocity = Vector2.zero;
        if (authority) {
            Check(battle.ActiveStateName == "Detect", "sleeping Mawlek does not start battle early");
            var receipts = entity.Object.Host.GetComponents<PlayMakerFSM>().SelectMany(f => f.FsmVariables.BoolVariables)
                .Where(b => b.Name.StartsWith("HollowConnect Battle Start ")).ToArray();
            Check(receipts.Length > 0 && receipts.All(b => !b.Value), "sleeping snapshot has no native start receipts");
            var alert = body.GetComponentsInChildren<Collider2D>(true).Single(c => c.name == "Alert Range New" && c.isTrigger);
            Check(alert.enabled && alert.gameObject.activeInHierarchy, "native Mawlek alert trigger active");
            hero.transform.position = alert.bounds.center; Physics2D.SyncTransforms();
            yield return Until(() => battle.ActiveStateName == "Start" && body.GetComponent<HealthManager>() is { IsInvincible: false, hp: > 0 },
                "native alert starts Mawlek battle");
            Flag("flow.mawlek.woken");
        }
        if (resume != null) {
            yield return Until(() => entity.Object.Host.GetComponents<PlayMakerFSM>().SelectMany(f => f.FsmVariables.BoolVariables)
                .Any(b => b.Name.StartsWith("HollowConnect Battle Start ") && b.Value), "durable native start receipt arrives before local initialization");
            Check(battle.ActiveStateName == "Pause" && starts == 0, "receipt waits without skipping native local initialization; actual=" + battle.ActiveStateName + ":" + starts);
            yield return new WaitForSecondsRealtime(.3f);
            resume();
        }
        yield return Until(() => battle.ActiveStateName == "Start", "late snapshot starts native local Mawlek battle", 12);
        Flag(role + ".flow.mawlek.started");
        yield return Until(() => Has(other + ".flow.mawlek.started"), "both local Mawlek battles started");
        if (authority) {
            foreach (var fsm in entity.Object.Host.GetComponents<PlayMakerFSM>()) foreach (var receipt in fsm.FsmVariables.BoolVariables
                .Where(b => b.Name.StartsWith("HollowConnect Battle Start ") && b.Value)) {
                var fsms = Field<HostClientPair<System.Collections.Generic.List<PlayMakerFSM>>>(entity, "_fsms").Host;
                for (var i = 0; i < 5; i++) typeof(Entity).GetMethod("PublishFsmBool", BindingFlags.Instance | BindingFlags.NonPublic)
                    .Invoke(entity, new object[] { fsms.IndexOf(fsm), receipt });
            }
            Flag("flow.mawlek.duplicates");
        }
        yield return Until(() => Has("flow.mawlek.duplicates"), "duplicate native start receipts sent over real transport");
        yield return new WaitForSecondsRealtime(.7f);
        Check(starts == 1 && battle.FsmVariables.FindFsmInt("Battle Enemies").Value == 1,
            "duplicate receipts do not restart or reset initialized native battle");
        if (authority) body.GetComponent<HealthManager>().ApplyExtraDamage(body.GetComponent<HealthManager>().hp);
        yield return Until(() => battle.ActiveStateName == "End", "native Mawlek completion reaches local End", 25);
        var shard = Object.FindObjectsOfType<PlayMakerFSM>(true).Single(f => f.gameObject.scene.name == scene && f.name == "Heart Piece" && f.FsmName == "Heart Container Control");
        yield return Until(() => shard.gameObject.activeInHierarchy && shard.ActiveStateName == "Idle", "native personal Mawlek reward becomes available");
        Check(hero.acceptingInput && !hero.controlReqlinquished, "Mawlek completion preserves usable local control");
        Flag(role + ".flow.mawlek.complete");
        yield return Until(() => Has(other + ".flow.mawlek.complete"), "both native Mawlek continuations complete");
        } finally {
            On.HutongGames.PlayMaker.Fsm.EnterState -= trace;
            On.HutongGames.PlayMaker.Actions.NextFrameEvent.OnEnter -= delay;
            On.HutongGames.PlayMaker.Actions.NextFrameEvent.OnUpdate -= delayUpdate;
        }
    }
}
