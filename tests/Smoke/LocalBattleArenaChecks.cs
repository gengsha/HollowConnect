using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Hkmp.Game.Client.Entity;
using Hkmp.Game.Client.Entity.Component;
using Hkmp.Networking.Packet.Data;
using HollowConnect;
using HutongGames.PlayMaker;
using UnityEngine;
using Object = UnityEngine.Object;

namespace HollowConnect.Smoke;

public sealed partial class RescueProbe {
    private IEnumerator LocalBattleChecks() {
        const string scene = "Fungus2_05";
        var mode = Environment.GetEnvironmentVariable("HOLLOWCONNECT_LOCAL_BATTLE_CHECK");
        if (mode == "mother") { yield return LocalGruzBattleChecks(); yield break; }
        var late = mode == "late";
        var reentry = mode == "reentry";
        var disconnect = mode == "disconnect";
        var partial = mode == "partial" || reentry;
        var outside = mode == "outside" || disconnect;
        Check(runtime.Shared, "permanent progress sharing negotiated before connection");
        LocalBarrierChecks.Run(Log);
        var authority = role == (Environment.GetEnvironmentVariable("HOLLOWCONNECT_LOCAL_BATTLE_AUTHORITY") ?? "host");
        var other = host ? "guest" : "host";
        var hero = HeroController.instance;
        if (PlayerData.instance.GetBool("atBench")) {
            var bench = Object.FindObjectsOfType<PlayMakerFSM>().Single(f => f.name == "RestBench" && f.FsmName == "Bench Control");
            yield return Until(() => bench.ActiveStateName == "Resting", "initial native bench ready");
            bench.Fsm.Event(bench.Fsm.ActiveState.Actions.OfType<HutongGames.PlayMaker.Actions.ListenForJump>().Single().wasPressed);
            yield return Until(() => !PlayerData.instance.GetBool("atBench") && hero.acceptingInput, "native bench get-up");
        }
        if (!authority) yield return Until(() => Has(late ? "arena.killed" : partial ? "arena.first-killed" : "arena.first"), "first arena entrant or late defeated entry");
        GameManager.instance.BeginSceneTransition(new GameManager.SceneLoadInfo { SceneName = scene, EntryGateName = "right1",
            Visualization = GameManager.SceneLoadVisualizations.Default, WaitForSceneTransitionCameraFade = true });
        yield return Until(() => UnityEngine.SceneManagement.SceneManager.GetSceneByName(scene).isLoaded, "fungal scene asset loaded");
        foreach (var gate in Object.FindObjectsOfType<TransitionPoint>(true).Where(g => g.gameObject.scene.name == scene))
            Log("ARENA GATE " + gate.name + " pos=" + gate.transform.position);
        var manager = Field<EntityManager>(engine.ClientManager, "_entityManager");
        var entities = Field<Dictionary<ulong, Entity>>(manager, "_entities");
        yield return Until(() => runtime.Playable && hero.acceptingInput && GameManager.instance.sceneName == scene && !GameManager.instance.IsInSceneTransition &&
            engine.ClientManager.SceneSnapshotReady && manager.IsSceneHostDetermined && manager.IsSceneHost == authority,
            "native fungal arena ready with expected authority");
        hero.takeNoDamage = true;
        var body = hero.GetComponent<Rigidbody2D>();
        if (reentry && !authority) {
            var oldBattle = Object.FindObjectsOfType<PlayMakerFSM>().Single(f => f.FsmName == "Battle Control");
            yield return Until(() => LocalBattlePending(oldBattle) == 1, "one pending death before leaving without triggering battle");
            var oldId = oldBattle.gameObject.GetInstanceID();
            GameManager.instance.BeginSceneTransition(new GameManager.SceneLoadInfo { SceneName = "Town", EntryGateName = "left1",
                Visualization = GameManager.SceneLoadVisualizations.Default, WaitForSceneTransitionCameraFade = true });
            yield return Until(() => GameManager.instance.sceneName == "Town" && !GameManager.instance.IsInSceneTransition && hero.acceptingInput,
                "native leave before local battle initialization");
            Check(!LocalBattleEncounters().Contains(oldId), "unloading room removes pending battle lifetime");
            GameManager.instance.BeginSceneTransition(new GameManager.SceneLoadInfo { SceneName = scene, EntryGateName = "right1",
                Visualization = GameManager.SceneLoadVisualizations.Default, WaitForSceneTransitionCameraFade = true });
            yield return Until(() => runtime.Playable && GameManager.instance.sceneName == scene && !GameManager.instance.IsInSceneTransition &&
                hero.acceptingInput && engine.ClientManager.SceneSnapshotReady && manager.IsSceneHostDetermined && !manager.IsSceneHost,
                "native reentry receives current fight snapshot");
            Check(Object.FindObjectsOfType<PlayMakerFSM>().Single(f => f.FsmName == "Battle Control").gameObject.GetInstanceID() != oldId,
                "reentry owns a new battle object lifetime");
        }
        if (mode == "boundary") {
            LocalBattlePacketRiskChecks();
            // Unity destroys fixture objects at the end of the frame.
            yield return null;
        }
        var fsms = Object.FindObjectsOfType<PlayMakerFSM>(true).Where(f => f.gameObject.scene.name == scene).ToArray();
        foreach (var f in fsms.Where(f => Environment.GetEnvironmentVariable("HOLLOWCONNECT_LOCAL_BATTLE_SCHEMA") == "1" &&
            (f.FsmName == "Battle Control" || f.name.Contains("Gate") || f.name == "Shiny Item"))) {
            Log("ARENA FSM " + f.name + "/" + f.FsmName + "=" + f.ActiveStateName + " pos=" + f.transform.position +
                " ints=" + string.Join(",", f.FsmVariables.IntVariables.Select(v => v.Name + "=" + v.Value)));
            foreach (var state in f.FsmStates) { Log("ARENA SCHEMA " + f.name + "/" + state.Name + " -> " +
                string.Join(",", state.Transitions.Select(t => t.EventName + ":" + t.ToState)) + " actions=" +
                string.Join(",", state.Actions.Select(a => a.GetType().Name)));
                if (f.FsmName == "Battle Control") foreach (var a in state.Actions)
                    Log("ARENA ACTION " + state.Name + "/" + a.GetType().Name + " " + string.Join(";", a.GetType().GetFields(BindingFlags.Instance | BindingFlags.Public).Select(v => v.Name + "=" + v.GetValue(a))));
            }
        }
        var battle = fsms.Single(f => f.FsmName == "Battle Control");
        var trigger = battle.GetComponent<Collider2D>();
        Check(trigger != null && trigger.isTrigger, "native battle entry trigger exists");
        if (outside && !authority) {
            Flag(role + ".arena.outside");
            yield return Until(() => Has(disconnect ? "arena.first-killed" : "arena.killed"), "opponents die while local hero is outside trigger");
            yield return new WaitForSecondsRealtime(.5f);
            if (disconnect) {
                yield return Until(() => LocalBattlePending(battle) == 1, "one early death retained before authority disconnect");
                Flag("arena.pending-ready");
                yield return Until(() => Has("arena.disconnected") && manager.IsSceneHostDetermined && manager.IsSceneHost,
                    "real client disconnect transfers arena authority");
                Check(LocalBattlePending(battle) == 1, "takeover retains the local early-death receipt");
            } else ReplayArenaDeaths(entities, battle);
        }
        hero.transform.position = trigger.bounds.center;
        body.velocity = Vector2.zero; hero.AffectedByGravity(false);
        Physics2D.SyncTransforms();
        yield return new WaitForSecondsRealtime(1);
        Log("ARENA ENTRY state=" + battle.ActiveStateName + " ints=" + string.Join(",", battle.FsmVariables.IntVariables.Select(v => v.Name + "=" + v.Value)));
        if (partial && !authority) Check(battle.FsmVariables.GetFsmInt("Battle Enemies").Value == 1,
            "one early death leaves exactly one living arena opponent");
        if (disconnect && !authority) {
            Check(battle.FsmVariables.GetFsmInt("Battle Enemies").Value == 1, "takeover starts with one remaining opponent");
            Flag("arena.takeover-started");
            yield return Until(() => Has("arena.reconnected"), "old authority reconnects as replica before final kill");
        }
        if (authority) Flag("arena.first");
        Flag(role + ".arena.entered");
        if (!late && !partial && !outside) yield return Until(() => Has(other + ".arena.entered"), "both enter native local battle trigger");
        if (outside && authority) yield return Until(() => Has(other + ".arena.outside"), "teammate is in same scene outside their local arena trigger");
        yield return new WaitForSecondsRealtime(1);
        foreach (var e in entities.Values.Where(e => e.Type == EntityType.ShrumalOgre)) {
            var obj = authority ? e.Object.Host : e.Object.Client;
            if (obj == null) continue;
            var h = obj.GetComponent<HealthManager>();
            Log("ARENA OGRE id=" + e.Id + " hp=" + h.hp + " dead=" + h.isDead + " active=" + h.gameObject.activeInHierarchy +
                " notified=" + typeof(HealthManager).GetField("notifiedBattleScene", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(h) +
                " battle=" + typeof(HealthManager).GetField("battleScene", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(h));
        }
        Flag(role + ".arena.before-kill");
        if (!late && !partial && !outside) yield return Until(() => Has(other + ".arena.before-kill"), "both record pre-kill state");
        if (authority) {
            var enemies = entities.Values.Where(e => e.Type == EntityType.ShrumalOgre && e.Object.Host != null &&
                e.Object.Host.activeInHierarchy && typeof(HealthManager).GetField("battleScene", BindingFlags.Instance | BindingFlags.NonPublic)
                    .GetValue(e.Object.Host.GetComponent<HealthManager>()) as GameObject == battle.gameObject).ToArray();
            Check(enemies.Length == 2, "two actual native Shrumal Ogres registered");
            foreach (var enemy in enemies) {
                var health = enemy.Object.Host.GetComponent<HealthManager>();
                health.ApplyExtraDamage(health.hp);
                if (!Has("arena.first-killed")) {
                    Flag("arena.first-killed");
                    if (partial) yield return Until(() => Has(other + ".arena.entered"), "late player enters with one opponent still alive");
                    if (disconnect) {
                        yield return Until(() => Has("arena.pending-ready"), "teammate has pending receipt before disconnect");
                        engine.ClientManager.Disconnect(); Flag("arena.disconnected");
                        yield return Until(() => Has("arena.takeover-started"), "survivor starts its local battle as new authority");
                        typeof(Hkmp.Game.Client.ClientManager).GetField("_autoConnect", BindingFlags.Instance | BindingFlags.NonPublic).SetValue(engine.ClientManager, true);
                        typeof(Hkmp.Game.Client.ClientManager).GetMethod("Connect", BindingFlags.Instance | BindingFlags.NonPublic)
                            .Invoke(engine.ClientManager, new object[] { "127.0.0.1", 29650, "rescue" + role });
                        yield return Until(() => runtime.Connected && engine.ClientManager.SceneSnapshotReady && manager.IsSceneHostDetermined && !manager.IsSceneHost,
                            "same-room reconnect becomes replica");
                        Flag("arena.reconnected"); break;
                    }
                }
                yield return new WaitForSecondsRealtime(.5f);
            }
            if (!disconnect) Flag("arena.killed");
        }
        if (disconnect && !authority) {
            var remaining = entities.Values.Where(e => e.Type == EntityType.ShrumalOgre && e.Object.Host != null && e.Object.Host.activeInHierarchy &&
                e.Object.Host.GetComponent<HealthManager>().hp > 0 && typeof(HealthManager).GetField("battleScene", BindingFlags.Instance | BindingFlags.NonPublic)
                    .GetValue(e.Object.Host.GetComponent<HealthManager>()) as GameObject == battle.gameObject).ToArray();
            Check(remaining.Length == 1, "new authority has exactly one live native opponent");
            var hp = remaining.Single().Object.Host.GetComponent<HealthManager>(); hp.ApplyExtraDamage(hp.hp);
            Flag("arena.killed");
        }
        yield return Until(() => Has("arena.killed"), "authority kills both native ogres");
        yield return new WaitForSecondsRealtime(3);
        Log("ARENA RESULT state=" + battle.ActiveStateName + " ints=" + string.Join(",", battle.FsmVariables.IntVariables.Select(v => v.Name + "=" + v.Value)));
        foreach (var e in entities.Values.Where(e => e.Type == EntityType.ShrumalOgre))
            foreach (var h in new[] { e.Object.Host, e.Object.Client }.Where(g => g != null).Select(g => g.GetComponent<HealthManager>()))
                Log("ARENA DEATH id=" + e.Id + " obj=" + h.name + " hp=" + h.hp + " dead=" + h.isDead + " notified=" + typeof(HealthManager).GetField("notifiedBattleScene", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(h) + " battle=" + typeof(HealthManager).GetField("battleScene", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(h));
        foreach (var f in fsms.Where(f => f != null && (f.FsmName == "Battle Control" || f.name.Contains("Gate") || f.name == "Shiny Item")))
            Log("ARENA RESULT " + f.name + "/" + f.FsmName + "=" + f.ActiveStateName + " active=" + f.gameObject.activeInHierarchy +
                " colliders=" + string.Join(",", f.GetComponentsInChildren<Collider2D>(true).Select(c => c.name + ":" + c.enabled + ":" + c.isTrigger + ":" + c.gameObject.activeInHierarchy)));
        Check(battle.FsmVariables.GetFsmInt("Battle Enemies")?.Value == 0, "both clients settle native battle enemy counter to zero");
        yield return Until(() => battle.ActiveStateName == "End", "native local encounter reaches completed state", 12);
        var gates = fsms.Where(f => f.FsmName == "BG Control").ToArray();
        Check(gates.Length == 2 && gates.All(f => !f.GetComponents<Collider2D>().Any(c => c.enabled && !c.isTrigger)),
            "both native gate colliders open locally");
        var shiny = fsms.Single(f => f != null && f.FsmName == "Shiny Control" && f.gameObject.activeInHierarchy);
        yield return Until(() => shiny.ActiveStateName == "Idle", "native reward is available for inspection");
        Flag(role + ".arena.reward-ready");
        yield return Until(() => Has(other + ".arena.reward-ready"), "both local rewards are ready");
        var pd = PlayerData.instance;
        if (!authority) {
            yield return Until(() => Has("arena.reward-collected"), "authority takes first reward");
            yield return Until(() => pd.charmSlots == 4, "remote notch arrives before this player's local pickup");
        }
        hero.transform.position = hero.FindGroundPoint(shiny.transform.position, true) + Vector3.up * .1f;
        body.velocity = Vector2.zero; hero.AffectedByGravity(true); Physics2D.SyncTransforms();
        yield return Until(() => hero.cState.onGround, "hero grounded beside actual reward");
        Log("ARENA PICKUP before slots=" + pd.charmSlots + " flag=" + pd.GetBool("notchShroomOgres") + " state=" + shiny.ActiveStateName);
        shiny.SendEvent("START INSPECT");
        yield return Until(() => pd.charmSlots == 4 && pd.GetBool("notchShroomOgres") && hero.acceptingInput &&
            !hero.controlReqlinquished && !pd.GetBool("disablePause"), "native notch pickup completes and releases control", 15);
        if (authority) Flag("arena.reward-collected");
        yield return new WaitForSecondsRealtime(1);
        Check(pd.charmSlots == 4, "shared and local source award exactly one notch");
        if (outside && !disconnect && !authority) ReplayArenaDeaths(entities, battle);
        foreach (var gate in gates) {
            var gateX = gate.transform.position.x;
            hero.transform.position = hero.FindGroundPoint(new Vector3(gateX - 2, gate.transform.position.y + 2, 0), true) + Vector3.up * .1f;
            body.velocity = Vector2.zero; hero.AffectedByGravity(true); Physics2D.SyncTransforms();
            yield return Until(() => hero.cState.onGround, "native support at " + gate.name);
            using (new MonoMod.RuntimeDetour.Hook(typeof(HeroController).GetMethod("LookForInput", BindingFlags.Instance | BindingFlags.NonPublic),
                new Action<Action<HeroController>, HeroController>((orig, self) => { orig(self); if (self.acceptingInput) self.move_input = 1f; })))
                yield return Until(() => hero.transform.position.x > gateX + 1, "native walking crosses " + gate.name, 5);
        }
        Flag(role + ".arena.checked"); yield return Until(() => Has(other + ".arena.checked"), "both arena checks complete");
    }

    private void ReplayArenaDeaths(Dictionary<ulong, Entity> entities, PlayMakerFSM battle) {
        var counter = battle.FsmVariables.GetFsmInt("Battle Enemies");
        var before = counter.Value;
        var deaths = 0;
        foreach (var enemy in entities.Values.Where(e => e.Type == EntityType.ShrumalOgre)) {
            var components = Field<Dictionary<EntityComponentType, EntityComponent>>(enemy, "_components");
            if (!components.TryGetValue(EntityComponentType.Death, out var component) || !Field<bool>(component, "_dead")) continue;
            var data = new EntityNetworkData { Type = EntityComponentType.Death };
            data.Packet.Write(Field<uint>(component, "_receivedRevision")); data.Packet.Write(false);
            data.Packet.Write((byte)AttackTypes.Generic); data.Packet.Write(true);
            data.Packet = new Hkmp.Networking.Packet.Packet(data.Packet.ToArray());
            component.Update(data, true); deaths++;
        }
        Check(deaths == 2 && counter.Value == before, "replayed death snapshots do not change local counter");
    }
}
