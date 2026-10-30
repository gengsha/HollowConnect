using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Hkmp.Game.Client.Entity;
using Hkmp.Game.Client.Entity.Component;
using Hkmp.Networking.Packet.Data;
using HutongGames.PlayMaker;
using HutongGames.PlayMaker.Actions;
using UnityEngine;
using Object = UnityEngine.Object;

namespace HollowConnect.Smoke;

public sealed partial class RescueProbe {
    private IEnumerator LocalGruzBattleChecks() {
        On.HutongGames.PlayMaker.Fsm.hook_ProcessEvent trace = (orig, fsm, ev, data) => {
            if (ev?.Name == "START" && fsm.GameObject != null && fsm.GameObject.scene.name == "Crossroads_04") {
                var source = typeof(LocalBarrierEvents).GetField("replaySource", BindingFlags.Static | BindingFlags.NonPublic).GetValue(null) as Entity;
                Log("MOTHER START receiver=" + fsm.GameObject.name + "/" + fsm.Name + ":" + fsm.ActiveStateName +
                    " source=" + source?.Type + ":" + source?.SourceScene + " ignored=" + LocalBarrierEvents.Ignore(fsm, ev.Name));
            }
            orig(fsm, ev, data);
        };
        On.HutongGames.PlayMaker.Fsm.ProcessEvent += trace;
        try { yield return LocalGruzBattleChecksInner(); }
        finally { On.HutongGames.PlayMaker.Fsm.ProcessEvent -= trace; }
    }
    private IEnumerator LocalGruzBattleChecksInner() {
        const string scene = "Crossroads_04";
        Check(runtime.Shared == (Environment.GetEnvironmentVariable("HOLLOWCONNECT_MOTHER_SHARED") != "0"),
            "expected permanent progress sharing negotiated before connection");
        if (runtime.Shared) LocalBarrierChecks.Run(Log);
        var authority = role == (Environment.GetEnvironmentVariable("HOLLOWCONNECT_LOCAL_BATTLE_AUTHORITY") ?? "host");
        var lateWake = Environment.GetEnvironmentVariable("HOLLOWCONNECT_MOTHER_ENTRY") == "woken";
        var other = host ? "guest" : "host";
        var hero = HeroController.instance;
        if (PlayerData.instance.GetBool("atBench")) {
            var bench = Object.FindObjectsOfType<PlayMakerFSM>().Single(f => f.name == "RestBench" && f.FsmName == "Bench Control");
            yield return Until(() => bench.ActiveStateName == "Resting", "initial native bench ready");
            bench.Fsm.Event(bench.Fsm.ActiveState.Actions.OfType<ListenForJump>().Single().wasPressed);
            yield return Until(() => !PlayerData.instance.GetBool("atBench") && hero.acceptingInput, "native bench get-up");
        }
        if (!authority) yield return Until(() => Has(lateWake ? "mother.woken" : "mother.first"), "first entrant or native mother wake before later entry");
        GameManager.instance.BeginSceneTransition(new GameManager.SceneLoadInfo { SceneName = scene, EntryGateName = "left1",
            Visualization = GameManager.SceneLoadVisualizations.Default, WaitForSceneTransitionCameraFade = true });
        var manager = Field<EntityManager>(engine.ClientManager, "_entityManager");
        var entities = Field<Dictionary<ulong, Entity>>(manager, "_entities");
        yield return Until(() => runtime.Playable && GameManager.instance.sceneName == scene && !GameManager.instance.IsInSceneTransition &&
            hero.acceptingInput && engine.ClientManager.SceneSnapshotReady && manager.IsSceneHostDetermined && manager.IsSceneHost == authority,
            "native Gruz arena and scene snapshot ready");
        hero.takeNoDamage = true;
        var battle = Object.FindObjectsOfType<PlayMakerFSM>(true).Single(f => f.gameObject.scene.name == scene && f.FsmName == "Battle Control");
        EncounterSchema(battle);
        var gates = Object.FindObjectsOfType<PlayMakerFSM>(true).Where(f => f.gameObject.scene.name == scene && f.FsmName == "BG Control").ToArray();
        foreach (var state in battle.FsmStates)
            Log("MOTHER SCHEMA " + state.Name + " -> " + string.Join(",", state.Transitions.Select(t => t.EventName + ":" + t.ToState)) +
                " actions=" + string.Join(",", state.Actions.Select(a => a.GetType().Name)));
        Check(LocalBattleCompletion.IsLocal(battle.gameObject), "native Gruz Battle Control stays local");
        Check(gates.Length > 0, "native Gruz gates exist");
        if (!lateWake) Check(battle.ActiveStateName == "Detect", "sleeping Mother snapshot does not start local battle early");
        if (authority) Flag("mother.first");
        var nativeMother = entities.Values.Single(e => e.Type == EntityType.GruzMother);
        var childArenaMinY = nativeMother.Object.Host.transform.position.y - 5;
        hero.transform.position = nativeMother.Object.Host.transform.position + new Vector3(-4, 0, 0); hero.AffectedByGravity(false);
        hero.GetComponent<Rigidbody2D>().velocity = Vector2.zero; Physics2D.SyncTransforms();
        yield return new WaitForSecondsRealtime(1);
        Flag(role + ".mother.entered");
        if (authority) {
            if (!lateWake) yield return Until(() => Has(other + ".mother.entered"), "both players are present before native wake damage");
            yield return Until(() => entities.Values.Any(e => e.Type == EntityType.GruzMother && e.Object.Host != null && e.Object.Host.activeInHierarchy),
                "native mother registered and active");
            var mother = nativeMother.Object.Host.GetComponent<HealthManager>();
            var control = mother.GetComponents<PlayMakerFSM>().Single(f => f.FsmName == "Big Fly Control");
            foreach (var state in control.FsmStates.Where(s => s.Name == "Invincible" || s.Name.Contains("Sleep")))
                Log("MOTHER WAKE SCHEMA " + state.Name + " -> " + string.Join(",", state.Transitions.Select(t => t.EventName + ":" + t.ToState)) +
                    " actions=" + string.Join(",", state.Actions.Select(a => a.GetType().Name)));
            var wakeEnd = Time.realtimeSinceStartup + 10;
            var initialMotherHealth = mother.hp;
            hero.transform.position = mother.GetComponent<Collider2D>().bounds.center + new Vector3(-3, -.5f, 0);
            hero.GetComponent<Rigidbody2D>().velocity = Vector2.zero;
            hero.RegainControl(); hero.StartAnimationControl(); Physics2D.SyncTransforms();
            while (battle.ActiveStateName != "Start" && Time.realtimeSinceStartup < wakeEnd) {
                Log("MOTHER WAKE state=" + control.ActiveStateName + " invincible=" + mother.IsInvincible + " hp=" + mother.hp);
                // Only the real nail collision runs DamageEnemies.DoDamage,
                // including its TAKE DAMAGE event before HealthManager.Hit.
                hero.FaceRight(); hero.Attack(GlobalEnums.AttackDirection.normal); Physics2D.SyncTransforms();
                yield return new WaitForSecondsRealtime(.05f);
                Log("MOTHER NAIL hero=" + hero.transform.position + " target=" + mother.GetComponent<Collider2D>().bounds + " slashes=" +
                    string.Join(",", hero.GetComponentsInChildren<Collider2D>().Where(c => c.name.Contains("Slash")).Select(c => c.name + ":" + c.enabled + ":" + c.bounds)));
                yield return new WaitForSecondsRealtime(.45f);
            }
            Check(battle.ActiveStateName == "Start" && mother.hp > 0 && mother.hp < initialMotherHealth,
                "native nonfatal nail damage wakes mother and starts local battle");
            Flag("mother.woken");
            yield return Until(() => Has(other + ".mother.started"), "replica wake starts teammate's native local battle");
            yield return Until(() => gates.All(g => g.GetComponentsInChildren<Collider2D>(true).Any(c =>
                c.enabled && !c.isTrigger && c.gameObject.activeInHierarchy)), "native Mother entry closes both solid arena gates");
            Flag(role + ".mother.closed");
            yield return Until(() => Has(other + ".mother.closed"), "both players have closed local gates before Mother dies");
            if (PostBossDamageCheck) CheckNativeBossDamage(mother);
            mother.ApplyExtraDamage(mother.hp); Flag("mother.defeated");
        } else {
            yield return Until(() => Has("mother.woken") && battle.ActiveStateName == "Start",
                "native replicated wake starts local battle without a fabricated entry collider");
            Flag(role + ".mother.started");
            yield return Until(() => gates.All(g => g.GetComponentsInChildren<Collider2D>(true).Any(c =>
                c.enabled && !c.isTrigger && c.gameObject.activeInHierarchy)), "native Mother entry closes both solid arena gates");
            Flag(role + ".mother.closed");
        }
        var gateBounds = gates.Select(g => g.GetComponentsInChildren<Collider2D>(true)
            .First(c => c.enabled && !c.isTrigger && c.gameObject.activeInHierarchy).bounds).ToArray();
        var gateCenters = gateBounds.Select(b => b.center).ToArray();
        Log("MOTHER GATE CENTERS " + string.Join(",", gates.Select((g, i) => g.name + ":" + gateCenters[i])));
        yield return Until(() => Has("mother.defeated") && Has(other + ".mother.entered"), "both present after actual mother defeat");
        var observedChildren = false;
        var checkedPostBossDamage = false;
        var end = Time.realtimeSinceStartup + 40;
        var previous = "";
        while (battle.ActiveStateName != "End" && Time.realtimeSinceStartup < end) {
            if (previous != battle.ActiveStateName) {
                previous = battle.ActiveStateName;
                Log("MOTHER STATE " + previous + " counter=" + battle.FsmVariables.GetFsmInt("Battle Enemies").Value);
            }
            var childObjects = runtime.Shared
                ? entities.Values.Where(e => e.Type != EntityType.GruzMother).Select(e => authority ? e.Object.Host : e.Object.Client)
                : Object.FindObjectsOfType<HealthManager>().Where(h => h.gameObject.scene.name == scene).Select(h => h.gameObject);
            var liveChildren = childObjects.Where(o => o != null && o.activeInHierarchy && o.transform.position.y > childArenaMinY)
                .Select(o => o.GetComponent<HealthManager>()).Where(h => h != null && !h.isDead && h.hp > 0 &&
                    typeof(HealthManager).GetField("battleScene", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(h) as GameObject == battle.gameObject).ToArray();
            if (liveChildren.Length > 0) {
                if (!observedChildren) {
                    observedChildren = true; Flag(role + ".mother.children");
                    Log("MOTHER ACTUAL CHILDREN " + string.Join(",", liveChildren.Select(h => h.name + "@" + h.transform.position)));
                    Check(battle.ActiveStateName != "End", "native child wave prevents premature local completion");
                    Check(gates.All(g => g.GetComponentsInChildren<Collider2D>(true).Any(c =>
                        c.enabled && !c.isTrigger && c.gameObject.activeInHierarchy)), "Mother gates remain solid during living child wave");
                }
                if (authority) yield return Until(() => Has(other + ".mother.children"), "both observe actual native child wave before final kills");
                if (PostBossDamageCheck && !checkedPostBossDamage) {
                    yield return PostBossOrdinaryDamageChecks(liveChildren, entities, authority);
                    checkedPostBossDamage = true;
                }
                foreach (var child in liveChildren)
                    if (authority || !runtime.Shared && EntityReferences.Owner(child.gameObject) == null) child.ApplyExtraDamage(child.hp);
            }
            yield return new WaitForSecondsRealtime(.2f);
        }
        Log("MOTHER RESULT state=" + battle.ActiveStateName + " counter=" + battle.FsmVariables.GetFsmInt("Battle Enemies").Value +
            " children=" + observedChildren + " gates=" + string.Join(",", gates.Select(f => f.ActiveStateName)));
        Check(observedChildren, "actual second wave of native children was observed");
        Check(battle.ActiveStateName == "End" && battle.FsmVariables.GetFsmInt("Battle Enemies").Value == 0,
            "native mother and child waves settle on both endpoints");
        yield return Until(() => gates.All(f => !f.GetComponents<Collider2D>().Any(c => c.enabled && !c.isTrigger)), "native Gruz gates open after both waves");
        hero.RegainControl(); hero.AffectedByGravity(true);
        // Door sprites span walls and ledges. Locate an unobstructed passage
        // within the closed door's actual bounds before testing native input.
        hero.AffectedByGravity(false);
        var traversed = 0;
        for (var i = 0; i < gateCenters.Length; i++) {
            var center = gateCenters[i]; var direction = center.x < gateCenters.Average(c => c.x) ? -1 : 1;
            var x = center.x - direction * 1.5f;
            var bodyBounds = hero.GetComponent<Collider2D>().bounds;
            var candidates = Enumerable.Range(0, Math.Max(1, (int)(gateBounds[i].size.y * 4)))
                .Select(n => gateBounds[i].min.y + n * .25f)
                .OrderBy(y => Math.Abs(y - (childArenaMinY + 5)))
                .Where(y => new[] { x, x + direction * 3 }.All(px =>
                    !Physics2D.OverlapBoxAll(new Vector2(px, y), bodyBounds.size, 0, 1 << 8).Any(c => !c.isTrigger)) &&
                    !Physics2D.BoxCastAll(new Vector2(x, y), bodyBounds.size, 0,
                    Vector2.right * direction, 3, 1 << 8).Any(h => !h.collider.isTrigger)).ToArray();
            Log("MOTHER EXIT GEOMETRY " + i + " closed=" + gateBounds[i] + " body=" + bodyBounds.size + " passages=" + string.Join(",", candidates));
            if (candidates.Length == 0) continue;
            hero.transform.position = new Vector3(x, candidates[0] - (bodyBounds.center.y - hero.transform.position.y), 0);
            hero.GetComponent<Rigidbody2D>().velocity = Vector2.zero; Physics2D.SyncTransforms();
            using (new MonoMod.RuntimeDetour.Hook(typeof(HeroController).GetMethod("LookForInput", BindingFlags.Instance | BindingFlags.NonPublic),
                new Action<Action<HeroController>, HeroController>((orig, self) => { orig(self); if (self.acceptingInput) self.move_input = direction; })))
                yield return Until(() => direction * (hero.transform.position.x - center.x) > 1,
                    "actual native movement crosses open Mother gate " + i, 5);
            traversed++;
        }
        Check(traversed > 0, "each player physically exits through an open native Mother doorway");
        hero.move_input = 0; hero.AffectedByGravity(true);
        Flag(role + ".mother.checked"); yield return Until(() => Has(other + ".mother.checked"), "both finish native multi-wave encounter");
    }

    private static IDictionary LocalBattleEncounters() => (IDictionary)typeof(LocalBattleCompletion)
        .GetField("Encounters", BindingFlags.Static | BindingFlags.NonPublic).GetValue(null);
    private static int LocalBattlePending(PlayMakerFSM battle) {
        var value = LocalBattleEncounters()[battle.gameObject.GetInstanceID()];
        if (value == null) return 0;
        return ((HashSet<ulong>)value.GetType().GetField("EarlyDeaths", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(value)).Count;
    }

    // Explicit receiver/action boundary fixture. Native full encounters below
    // independently verify real death callbacks, gates, rewards and movement.
    private void LocalBattlePacketRiskChecks() {
        const ulong id = 0x7fff000000000001;
        var arena = new GameObject("Local battle receipt boundary fixture"); arena.SetActive(false);
        var enemy = new GameObject("Health reset receiver fixture"); enemy.SetActive(false);
        HealthManagerComponent component = null;
        try {
            var battle = arena.AddComponent<PlayMakerFSM>(); battle.Reset(); battle.FsmName = "Battle Control";
            var counter = new FsmInt("Battle Enemies") { Value = 0 };
            battle.FsmVariables.IntVariables = new[] { counter };
            var init = new SetIntValue { intVariable = counter, intValue = 2, everyFrame = false };
            var state = new FsmState(battle.Fsm) { Name = "Counter fixture", Actions = new FsmStateAction[] { init }, Transitions = Array.Empty<FsmTransition>() };
            battle.Fsm.States = new[] { state }; battle.Fsm.StartState = state.Name;
            init.Init(state); state.SaveActions(); battle.Fsm.Reinitialize(); battle.Fsm.Init(battle);
            var hp = enemy.AddComponent<HealthManager>(); hp.hp = 100;
            var notified = typeof(HealthManager).GetField("notifiedBattleScene", BindingFlags.Instance | BindingFlags.NonPublic);
            component = new HealthManagerComponent(null, id, new HostClientPair<GameObject> { Host = enemy },
                new HostClientPair<HealthManager> { Host = hp }, EntityType.ShrumalOgre);
            void Receive(EntityComponentType type, uint revision, int value = 100, byte flags = 16, string battleKey = null) {
                var data = new EntityNetworkData { Type = type }; data.Packet.Write(revision);
                if (type == EntityComponentType.Death) {
                    data.Packet.Write(false); data.Packet.Write((byte)AttackTypes.Generic); data.Packet.Write(true);
                } else {
                    data.Packet.Write(value); data.Packet.Write(false); data.Packet.Write(-1); data.Packet.Write(flags);
                    data.Packet.Write(""); data.Packet.Write(battleKey ?? EntityReferences.Capture(arena));
                }
                data.Packet = new Hkmp.Networking.Packet.Packet(data.Packet.ToArray()); component.Update(data, false);
            }
            hp.SetBattleScene(arena);
            Receive(EntityComponentType.Health, 1, battleKey: "");
            Check((GameObject)typeof(HealthManager).GetField("battleScene", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(hp) == arena,
                "early unbound authority snapshot preserves native local battle association");
            Check(!(bool)notified.GetValue(hp), "local arena does not import authority notification receipt");
            Receive(EntityComponentType.Death, 3);
            for (uint n = 0; n < 30; n++) Receive(EntityComponentType.Death, 4 + n);
            Check(LocalBattlePending(battle) == 1, "duplicate terminal packets retain only one early receipt");
            Receive(EntityComponentType.Health, 4);
            Receive(EntityComponentType.HealthReset, 2);
            Check(hp.hp == 0 && hp.isDead && LocalBattlePending(battle) == 1, "ordinary health and stale reset cannot revive or erase a tombstone");
            Receive(EntityComponentType.HealthReset, 5, 0);
            Check(hp.isDead && LocalBattlePending(battle) == 1, "zero-health reset cannot erase pending terminal death");
            Receive(EntityComponentType.HealthReset, 6);
            Check(hp.hp == 100 && !hp.isDead && LocalBattlePending(battle) == 0 && !(bool)notified.GetValue(hp),
                "accepted positive reset restores life and removes its unconsumed death receipt");
            Receive(EntityComponentType.Death, 3);
            Check(!hp.isDead && LocalBattlePending(battle) == 0, "old terminal packet cannot undo accepted life reset");
            init.OnEnter();
            Check(counter.Value == 2, "reset opponent is counted alive at native counter initialization");
            // New battle lifetime, then a new terminal revision after reset.
            LocalBattleEncounters().Remove(arena.GetInstanceID()); counter.Value = 0;
            Receive(EntityComponentType.Death, 7);
            init.OnEnter();
            Check(counter.Value == 1 && LocalBattlePending(battle) == 0, "new death after reset consumes exactly one first-wave slot");
            init.intValue = 5; init.OnEnter();
            Check(counter.Value == 5, "subsequent wave initialization never replays the previous wave death");
            init.intValue = 0; init.OnEnter();
            Check(counter.Value == 0, "native zero counter assignment remains unchanged");
            Receive(EntityComponentType.HealthReset, 8);
            notified.SetValue(hp, true); Receive(EntityComponentType.Health, 9, 100, 0);
            Check((bool)notified.GetValue(hp), "later health cannot clear an already-issued local battle receipt");
            notified.SetValue(hp, false); counter.Value = 0;
            Receive(EntityComponentType.Death, 10);
            init.intValue = 5; init.OnEnter();
            Check(counter.Value == 4 && LocalBattlePending(battle) == 0,
                "death between waves is consumed once by the next positive wave initialization");
            Receive(EntityComponentType.HealthReset, 11); counter.Value = 0;
            hp.SetBattleScene(null); Receive(EntityComponentType.Death, 12);
            hp.SetBattleScene(arena);
            Check(LocalBattlePending(battle) == 1 && (bool)notified.GetValue(hp), "death before native battle binding retains a local receipt");
            init.intValue = 2; init.OnEnter(); hp.SetBattleScene(arena);
            Check(counter.Value == 1 && LocalBattlePending(battle) == 0, "deferred binding and duplicate binding count one opponent once");
            Receive(EntityComponentType.HealthReset, 13); counter.Value = 2;
            hp.SetBattleScene(null); Receive(EntityComponentType.Death, 14); hp.SetBattleScene(arena);
            Check(counter.Value == 1 && (bool)notified.GetValue(hp), "binding after counter initialization supplies the missing native death decrement");
            Receive(EntityComponentType.HealthReset, 15); counter.Value = 2;
            hp.SetBattleScene(null); Receive(EntityComponentType.Death, 16);
            Receive(EntityComponentType.HealthReset, 17); hp.SetBattleScene(arena);
            Check(counter.Value == 2 && LocalBattlePending(battle) == 0, "life reset cancels an unbound death before later binding");
            if (Environment.GetEnvironmentVariable("HOLLOWCONNECT_LOCAL_BATTLE_LATE_BINDING") == "1") {
                Receive(EntityComponentType.HealthReset, 18); counter.Value = 2; hp.SetBattleScene(null);
                Receive(EntityComponentType.Death, 20);
                // Separate reliable streams can deliver the terminal receipt
                // before the final pre-death health/battle association.
                Receive(EntityComponentType.Health, 19);
                Check(hp.hp == 0 && hp.isDead && (bool)notified.GetValue(hp) && counter.Value == 1,
                    "late pre-death battle metadata completes its local counter without reviving the enemy");
                Receive(EntityComponentType.Health, 19); Receive(EntityComponentType.Health, 21);
                Check(counter.Value == 1 && hp.hp == 0 && hp.isDead,
                    "duplicate and newer terminal metadata cannot decrement twice or revive health");
                Receive(EntityComponentType.HealthReset, 22); counter.Value = 2; hp.SetBattleScene(null);
                Receive(EntityComponentType.Death, 24); Receive(EntityComponentType.Health, 21);
                Check(counter.Value == 2 && !(bool)notified.GetValue(hp),
                    "previous-life health metadata cannot bind the new terminal receipt");
                Receive(EntityComponentType.Health, 23);
                Check(counter.Value == 1 && hp.hp == 0 && hp.isDead,
                    "current-life metadata still binds after a stale previous-life packet");
            }
            Log("BOUNDARY FIXTURE ONLY receiver ordering and native SetIntValue hook; no full-wave combat claim");
        } finally {
            component?.Destroy(); LocalBattleCompletion.ResetDeath(id);
            LocalBattleEncounters().Remove(arena.GetInstanceID()); Object.Destroy(enemy); Object.Destroy(arena);
        }
    }
}
