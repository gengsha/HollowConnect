using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HutongGames.PlayMaker;
using HutongGames.PlayMaker.Actions;
using UnityEngine;
using UnityEngine.SceneManagement;
using Scenes = UnityEngine.SceneManagement.SceneManager;

namespace Hkmp.Game.Client.Entity;

// Arena doors remain native local objects. A terminal enemy snapshot can
// arrive before this player's entry trigger initializes Battle Enemies.
// Retain only those early deaths until that original initialization runs.
internal static class LocalBattleCompletion {
    private static readonly FieldInfo Battle = typeof(HealthManager).GetField("battleScene", BindingFlags.Instance | BindingFlags.NonPublic);
    private static readonly FieldInfo Notified = typeof(HealthManager).GetField("notifiedBattleScene", BindingFlags.Instance | BindingFlags.NonPublic);
    private sealed class Encounter {
        internal GameObject Owner;
        internal uint Visit, Epoch;
        internal bool Initialized;
        internal readonly HashSet<ulong> EarlyDeaths = new();
        internal readonly HashSet<ulong> ScriptedDeaths = new();
    }
    private sealed class DeferredDeath {
        internal ulong Id;
        internal HealthManager Host, Replica;
        internal Scene Scene;
    }
    private static readonly Dictionary<int, Encounter> Encounters = new();
    private static readonly Dictionary<ulong, DeferredDeath> Deferred = new();
    private static readonly Dictionary<int, DeferredDeath> DeferredByHealth = new();

    internal static bool IsLocal(GameObject battle) => battle != null && EntityReferences.Owner(battle) == null;
    internal static void NativeWaveAction(FsmStateAction action) {
        if (!CoopContext.Resetting && action.Fsm?.Name == "Battle Control" && IsLocal(action.Fsm.GameObject) &&
            action is SendEventByName { everyFrame: false } start && start.sendEvent.Value == "BATTLE START" &&
            start.delay.Value < .001f && start.eventTarget?.target == FsmEventTarget.EventTarget.BroadcastAll &&
            EntityReferences.Entities != null) {
            foreach (var entity in EntityReferences.Entities.Values) entity.RequestBattleStart(action.Fsm);
        }
        if (CoopContext.Resetting || action.Fsm?.Name != "Battle Control" || !IsLocal(action.Fsm.GameObject) ||
            action is not SendEventByName { everyFrame: false } send || send.sendEvent.Value != "SUMMON" ||
            send.delay.Value >= .001f || send.eventTarget?.target != FsmEventTarget.EventTarget.GameObject || !send.eventTarget.sendToChildren.Value) return;
        var group = action.Fsm.GetOwnerDefaultTarget(send.eventTarget.gameObject);
        if (group == null || !group.transform.IsChildOf(action.Fsm.GameObject.transform)) return;
        // Disabled replica FSMs are absent from PlayMaker's event recipients.
        // Observe the actual native wave action rather than relying on their
        // receiving a broadcast that the engine intentionally does not deliver.
        foreach (var fsm in group.GetComponentsInChildren<PlayMakerFSM>(true)) {
            var entity = EntityReferences.Owner(fsm.gameObject);
            if (entity?.Object.Host == fsm.gameObject && fsm.FsmName == "summon") entity.RequestEncounterSummon();
        }
    }
    private static Encounter Get(GameObject battle) {
        var id = battle.GetInstanceID();
        if (!Encounters.TryGetValue(id, out var encounter) || encounter.Owner != battle ||
            encounter.Visit != CoopContext.LocalVisit || encounter.Epoch != CoopContext.Epoch)
            Encounters[id] = encounter = new Encounter { Owner = battle, Visit = CoopContext.LocalVisit, Epoch = CoopContext.Epoch };
        return encounter;
    }
    private static bool IsFalseKnightCounter(FsmStateAction action) => action is SetFsmInt write && action.Enabled &&
        action.Fsm?.Name == "FalseyControl" && action.State.Name == "Decrement Battle Enemies" &&
        write.fsmName.Value == "Battle Control" && write.variableName.Value == "Battle Enemies";
    private static GameObject FalseKnightBattle(Entity entity, FsmState state) {
        if (CoopContext.Resetting || entity?.Type != EntityType.FalseKnight ||
            entity.SourceScene != "Crossroads_10_boss" || CoopContext.LocalScene != "Crossroads_10" ||
            state?.Fsm.Name != "FalseyControl" || entity.Object.Host == null) return null;
        // This boss's terminal continuation decrements a local controller; it
        // does not broadcast BATTLE END. Never infer completion from armour HP.
        var terminal = state.Fsm.States.FirstOrDefault(s => s.Name == "Decrement Battle Enemies" && s.Actions.Any(IsFalseKnightCounter));
        if (terminal == null || state != terminal && !(state.Name == "Cough" &&
            terminal.Transitions.Any(t => t.EventName == "FINISHED" && t.ToState == state.Name) &&
            state.Actions.Any(a => a.Enabled && a is SendHealthManagerDeathEvent))) return null;
        return entity.Object.Host.GetComponentsInParent<PlayMakerFSM>(true)
            .FirstOrDefault(f => f.FsmName == "Battle Control" && f.gameObject.scene.name == entity.SourceScene && IsLocal(f.gameObject))?.gameObject;
    }
    internal static void ObserveScriptedCompletion(Entity entity, FsmState state) {
        var battle = FalseKnightBattle(entity, state);
        var counter = Counter(battle);
        if (counter == null) return;
        var encounter = Get(battle);
        if (!encounter.ScriptedDeaths.Add(entity.Id)) return;
        ResumeFalseKnightCleanup(battle);
        // A terminal snapshot can precede this player's actual arena trigger.
        // Its receipt is consumed by their own native positive initialization.
        if (!encounter.Initialized) encounter.EarlyDeaths.Add(entity.Id);
        else counter.Value = System.Math.Max(0, counter.Value - 1);
    }
    private static void ResumeFalseKnightCleanup(GameObject battle) {
        if (CoopContext.Resetting || CoopContext.LocalScene != "Crossroads_10" || battle == null ||
            battle.scene.name != "Crossroads_10_boss" || !IsLocal(battle)) return;
        var fsm = battle.GetComponents<PlayMakerFSM>().FirstOrDefault(f => f.FsmName == "Battle Control");
        var transition = fsm?.Fsm.ActiveState?.Transitions.FirstOrDefault(t => t.EventName == "KILL ALL ENEMIES");
        var cleanup = fsm?.FsmStates.FirstOrDefault(s => s.Name == transition?.ToState);
        if (cleanup == null || !cleanup.Actions.OfType<IntCompare>().Any(a => a.Enabled && a.everyFrame &&
            a.integer1.Name == "Battle Enemies" && a.integer2.Value == 0 && a.equal?.Name == "END")) return;
        // On replicas the private-world broadcast filter suppressed the boss's
        // Rubble End cleanup event. Start has no zero-counter watcher. Resume
        // that native local continuation, which owns END and the gate opening.
        fsm.SendEvent("KILL ALL ENEMIES");
    }
    internal static bool ApplyScriptedCounter(FsmStateAction action) {
        if (!IsFalseKnightCounter(action)) return false;
        var entity = EntityReferences.Owner(action.Fsm.GameObject);
        if (FalseKnightBattle(entity, action.State) == null) return false;
        ObserveScriptedCompletion(entity, action.State);
        return true;
    }
    internal static bool EnterScriptedCounter(FsmStateAction action) {
        if (!ApplyScriptedCounter(action)) return false;
        // Keep the original FSM continuation and action replication, while
        // applying the native final decrement once in this player's encounter.
        action.Finish();
        return true;
    }
    internal static void Register() {
        On.HutongGames.PlayMaker.Actions.SetIntValue.OnEnter += InitializeCounter;
        On.HealthManager.SetBattleScene += SetBattleScene;
        On.HutongGames.PlayMaker.Actions.GameObjectHasChildren.OnEnter += PalaceWaveCheck;
        Scenes.sceneUnloaded += Unloaded;
    }
    internal static void Unregister() {
        On.HutongGames.PlayMaker.Actions.SetIntValue.OnEnter -= InitializeCounter;
        On.HealthManager.SetBattleScene -= SetBattleScene;
        On.HutongGames.PlayMaker.Actions.GameObjectHasChildren.OnEnter -= PalaceWaveCheck;
        Scenes.sceneUnloaded -= Unloaded;
        Encounters.Clear();
        Deferred.Clear(); DeferredByHealth.Clear();
    }
    internal static void BeforeRemoteDeath(ulong id, HealthManager host, HealthManager replica) {
        var battle = host == null ? null : Battle.GetValue(host) as GameObject;
        if (battle == null && replica != null) battle = Battle.GetValue(replica) as GameObject;
        if (battle != null) {
            var counter = Counter(battle);
            if (counter != null && counter.Value <= 0) Get(battle).EarlyDeaths.Add(id);
            return;
        }
        var owner = host != null ? host : replica;
        if (owner == null || Deferred.ContainsKey(id)) return;
        var pending = new DeferredDeath { Id = id, Host = host, Replica = replica, Scene = owner.gameObject.scene };
        Deferred[id] = pending;
        foreach (var health in new[] { host, replica })
            if (health != null) DeferredByHealth[health.GetInstanceID()] = pending;
    }
    internal static GameObject BeforeNativeDeath(HealthManager health) {
        var battle = health == null ? null : Battle.GetValue(health) as GameObject;
        var counter = Counter(battle);
        return counter != null && counter.Value <= 0 ? battle : null;
    }
    internal static void NativeDeathApplied(ulong id, HealthManager health, GameObject earlyBattle) {
        // Scene authority can be outside its own trigger while a teammate
        // fights. Its native decrement is overwritten by the later Start too.
        if (earlyBattle != null && Battle.GetValue(health) as GameObject == earlyBattle)
            Get(earlyBattle).EarlyDeaths.Add(id);
        else if (Battle.GetValue(health) as GameObject == null)
            BeforeRemoteDeath(id, health, null);
    }
    private static FsmInt Counter(GameObject battle) => IsLocal(battle) ? battle.GetComponents<PlayMakerFSM>()
        .FirstOrDefault(f => f.FsmName == "Battle Control")?.FsmVariables.FindFsmInt("Battle Enemies") : null;
    private static void Forget(DeferredDeath pending) {
        Deferred.Remove(pending.Id);
        foreach (var health in new[] { pending.Host, pending.Replica })
            if (!ReferenceEquals(health, null)) DeferredByHealth.Remove(health.GetInstanceID());
    }
    private static void SetBattleScene(On.HealthManager.orig_SetBattleScene orig, HealthManager health, GameObject battle) {
        orig(health, battle);
        Bound(health, battle);
    }
    internal static void Bound(HealthManager health, GameObject battle) {
        if (health == null || !DeferredByHealth.TryGetValue(health.GetInstanceID(), out var pending)) return;
        var counter = Counter(battle);
        if (counter == null) return;
        Forget(pending);
        if ((bool)Notified.GetValue(health)) return;
        if (counter.Value <= 0) Get(battle).EarlyDeaths.Add(pending.Id);
        // The original Die ran without a battle reference. Issue its native
        // counter receipt now, once for both copies, without replaying death.
        counter.Value--;
        foreach (var copy in new[] { pending.Host, pending.Replica }) {
            if (copy == null) continue;
            if (Battle.GetValue(copy) is GameObject other && other != null && other != battle) continue;
            Battle.SetValue(copy, battle); Notified.SetValue(copy, true);
        }
    }
    internal static void ResetDeath(ulong id) {
        foreach (var encounter in Encounters.Values) encounter.EarlyDeaths.Remove(id);
        if (Deferred.TryGetValue(id, out var pending)) Forget(pending);
    }
    // Path of Pain has no Battle Enemies counter. Its native Check asks
    // whether Wave 1 still has children. A replica deliberately retains the
    // inactive native guards for snapshots/takeover, even after their death.
    // Only those confirmed terminal guards cease to count as wave occupants.
    private static bool IsPalaceBattle(HutongGames.PlayMaker.Fsm fsm) => !CoopContext.Resetting &&
        fsm?.Name == "Battle Control" && fsm.GameObject != null &&
        fsm.GameObject.scene.name == "White_Palace_20" && IsLocal(fsm.GameObject);
    private static GameObject PalaceWave(GameObjectHasChildren action) =>
        IsPalaceBattle(action.Fsm) && action.State.Name == "Check" && !action.everyFrame &&
        action.Fsm.GetOwnerDefaultTarget(action.gameObject) is { name: "Wave 1" } wave ? wave : null;
    private static bool DeadGuard(Transform child) => EntityReferences.Owner(child.gameObject) is { Type: EntityType.Kingsmould } entity &&
        (child.gameObject == entity.Object.Host || child.gameObject == entity.Object.Client) &&
        entity.Object.Host != null && entity.Object.Host.GetComponent<HealthManager>() is { isDead: true, hp: <= 0 };
    private static void PalaceWaveCheck(On.HutongGames.PlayMaker.Actions.GameObjectHasChildren.orig_OnEnter orig, GameObjectHasChildren action) {
        var wave = PalaceWave(action);
        if (wave == null) { orig(action); return; }
        var hasChildren = wave.transform.Cast<Transform>().Any(child => !DeadGuard(child));
        action.storeResult.Value = hasChildren;
        action.Fsm.Event(hasChildren ? action.trueEvent : action.falseEvent);
        action.Finish();
    }
    internal static void LocalStateEntered(HutongGames.PlayMaker.Fsm fsm) {
        if (fsm?.Name == "Battle Control" && IsLocal(fsm.GameObject) &&
            Get(fsm.GameObject).ScriptedDeaths.Count != 0) ResumeFalseKnightCleanup(fsm.GameObject);
        if (!IsPalaceBattle(fsm) || fsm.ActiveStateName != "Start") return;
        var check = fsm.States.SelectMany(s => s.Actions).OfType<GameObjectHasChildren>().FirstOrDefault(a => PalaceWave(a) != null);
        var wave = check == null ? null : PalaceWave(check);
        // Include dormant guards as alive until a terminal death was applied.
        // Late arrivals may reach Start after both packets have already run.
        if (wave == null || wave.transform.childCount == 0 || !wave.transform.Cast<Transform>().All(DeadGuard)) return;
        fsm.Event("ROYAL GUARD KILLED");
    }
    internal static void RemoteDeathApplied(HealthManager host, HealthManager replica) {
        var health = host != null ? host : replica;
        var battle = health == null ? null : Battle.GetValue(health) as GameObject;
        if (battle == null) return;
        foreach (var fsm in battle.GetComponents<PlayMakerFSM>()) LocalStateEntered(fsm.Fsm);
    }
    private static void InitializeCounter(On.HutongGames.PlayMaker.Actions.SetIntValue.orig_OnEnter orig,
        HutongGames.PlayMaker.Actions.SetIntValue action) {
        var fsm = action.Fsm;
        if (fsm?.Name != "Battle Control" || action.intVariable?.Name != "Battle Enemies" ||
            action.intValue.Value <= 0 || !IsLocal(fsm.GameObject)) { orig(action); return; }
        var encounter = Get(fsm.GameObject);
        encounter.Initialized = true;
        orig(action);
        // Consume once at the next positive initialization. This also covers
        // a death arriving between waves; consumed receipts never repeat.
        action.intVariable.Value = System.Math.Max(0, action.intVariable.Value - encounter.EarlyDeaths.Count);
        encounter.EarlyDeaths.Clear();
    }
    private static void Unloaded(Scene scene) {
        foreach (var pair in Encounters.Where(p => p.Value.Owner == null || p.Value.Owner.scene == scene).ToArray())
            Encounters.Remove(pair.Key);
        foreach (var pending in Deferred.Values.Where(p => p.Scene == scene).ToArray()) Forget(pending);
    }
}
