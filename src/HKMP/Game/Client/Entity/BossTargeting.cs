using System;
using System.Collections.Generic;
using System.Reflection;
using System.Linq;
using HutongGames.PlayMaker;
using HutongGames.PlayMaker.Actions;
using Mono.Cecil;
using Mono.Cecil.Cil;
using MonoMod.Cil;
using MonoMod.RuntimeDetour;
using UnityEngine;

namespace Hkmp.Game.Client.Entity;

// Resolve only native AI position/direction reads. Persisting remote avatars
// in FSM variables would corrupt snapshots and retain the wrong target after
// revival or authority transfer. GetHero and story/reward actions stay native.
internal static class BossTargeting {
    private static readonly List<ILHook> Hooks = new();

    internal static void Register() {
        if (Hooks.Count != 0) return;
        try {
            Add(typeof(GetPosition), "DoGetPosition");
            Add(typeof(GetDistance), "DoGetDistance");
            Add(typeof(GetXDistance), "DoGetDistance");
            Add(typeof(GetYDistance), "DoGetDistance");
            Add(typeof(CheckTargetDirection), "DoCheckDirection", "target");
            Add(typeof(FaceObject), "DoFace", "objectB");
            Add(typeof(GetAngleToTarget2D), "DoGetAngle", "target");
            Add(typeof(GetAngleToTarget), "DoGetAngleToTarget", "targetObject");
            Add(typeof(FireAtTarget), "DoSetVelocity", "target");
            // NKG's four pillars spawn directly at spawnPoint; they do not
            // read the hero through GetPosition on each repeat.
            Add(typeof(SpawnObjectFromGlobalPool), "OnEnter", "spawnPoint");
            Add(typeof(SpawnObjectFromGlobalPoolOverTime), "OnUpdate", "spawnPoint");
            Add(typeof(CreateObject), "OnEnter", "spawnPoint");
            Add(typeof(ChaseObject), "DoBuzz", "target");
            Add(typeof(ChaseObjectGround), "DoChase", "target");
            Add(typeof(ChaseObjectV2), "DoChase", "target");
            Add(typeof(ChaseObjectVertical), "DoChase", "target");
            Add(typeof(DistanceFly), "DoBuzz", "target");
            Add(typeof(DistanceFlyV2), "DoBuzz", "target");
            Add(typeof(DistanceFlySmooth), "DoChase", "target");
            Add(typeof(DistanceWalk), "DoWalk", "target");
            Add(typeof(RunAway), "DoChase", "target");
            Add(typeof(SmoothFlyTo), "DoChase", "target");
            Add(typeof(IdleBuzz), "OnEnter", "target");
            Add(typeof(IdleBuzz), "DoBuzz", "target");
            Add(typeof(IdleBuzzV2), "OnEnter", "target");
            Add(typeof(IdleBuzzV2), "DoBuzz", "target");
            Add(typeof(IdleBuzzV3), "OnEnter", "target");
            Add(typeof(IdleBuzzV3), "DoBuzz", "target");
            On.HealthManager.TakeDamage += OomaHit;
        } catch {
            Unregister();
            throw;
        }
    }

    internal static void Unregister() {
        On.HealthManager.TakeDamage -= OomaHit;
        foreach (var hook in Hooks) hook.Dispose();
        Hooks.Clear();
    }

    private static void Add(Type type, string method, string targetField = null) {
        var target = type.GetMethod(method, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            ?? throw new MissingMethodException(type.FullName, method);
        Hooks.Add(new ILHook(target, il => {
            var cursor = new ILCursor(il);
            var count = 0;
            while (cursor.TryGotoNext(MoveType.After, i =>
                (i.OpCode == OpCodes.Call || i.OpCode == OpCodes.Callvirt) && i.Operand is MethodReference m &&
                ((m.DeclaringType.FullName == typeof(FsmGameObject).FullName && m.Name == "get_Value" &&
                  (targetField == null || i.Previous?.Operand is FieldReference field && field.Name == targetField)) ||
                 (targetField == null && m.DeclaringType.FullName == typeof(HutongGames.PlayMaker.Fsm).FullName && m.Name == "GetOwnerDefaultTarget")))) {
                cursor.Emit(OpCodes.Ldarg_0);
                cursor.EmitDelegate<Func<GameObject, FsmStateAction, GameObject>>(Resolve);
                count++;
            }
            if (count == 0) throw new InvalidOperationException("No boss target reads found: " + target);
        }));
    }

    internal static GameObject Resolve(GameObject original, FsmStateAction action) {
        var bomb = EntityReferences.Owner(action?.Fsm?.GameObject);
        if (!CoopContext.Resetting && original != null && HeroController.instance != null && original == HeroController.instance.gameObject &&
            bomb?.Type == EntityType.OomaCore && bomb.IsNativeAuthority && !EntityReferences.IsReplica(action.Fsm.GameObject)) {
            var choice = BossTargetCommitment.Decode(bomb.BossTargetReceipt?.Value, CoopContext.BossTargetSession?.Invoke() ?? "", CoopContext.Epoch);
            var hitter = CoopContext.BossTargetObject?.Invoke(choice.Player);
            if (hitter != null) return hitter;
        }
        if (CoopContext.FlexibleBossTargetingOn?.Invoke() == true) {
            if (CoopContext.Resetting || original == null || HeroController.instance == null || original != HeroController.instance.gameObject) return original;
            var flexOwner = action?.Fsm?.GameObject;
            var flexEntity = EntityReferences.Owner(flexOwner);
            if (flexEntity == null || !flexEntity.IsNativeAuthority || !flexEntity.BossTargetScope || EntityReferences.IsReplica(flexOwner) ||
                EntityReferences.Entities == null || !EntityReferences.Entities.TryGetValue(flexEntity.Id, out var flexLive) || flexLive != flexEntity) return original;
            return Target(flexEntity, false) ?? original;
        }
        if (CoopContext.Resetting || CoopContext.LocalRescueDown?.Invoke() != true ||
            original == null || HeroController.instance == null || original != HeroController.instance.gameObject) return original;
        var owner = action?.Fsm?.GameObject;
        var entity = EntityReferences.Owner(owner);
        if (entity == null || !entity.IsNativeAuthority || !entity.BossTargetScope ||
            EntityReferences.Entities == null || !EntityReferences.Entities.TryGetValue(entity.Id, out var live) || live != entity ||
            EntityReferences.IsReplica(owner)) return original;
        var target = CoopContext.LivingBossTarget?.Invoke();
        // No live occupant means room failure is already being resolved. Do
        // not hand native actions a null target during that short teardown.
        return target != null ? target : original;
    }

    private const string ReceiptName = "HollowConnect Attack Target";

    private static void OomaHit(On.HealthManager.orig_TakeDamage orig, HealthManager self, HitInstance hit) {
        var entity = EntityReferences.Owner(self.gameObject);
        if (entity?.Type == EntityType.Ooma && entity.IsNativeAuthority && self.gameObject == entity.Object.Host && entity.BossTargetReceipt != null) {
            var id = CoopContext.AttackSourcePlayer?.Invoke(hit.Source) ?? -1;
            if (id >= 0) {
                var receipt = new BossTargetCommitment();
                receipt.Commit(id, 0, CoopContext.BossTargetSession?.Invoke() ?? "", CoopContext.Epoch);
                entity.BossTargetReceipt.Value = receipt.Encode();
            }
        }
        orig(self, hit);
    }

    internal static void Prepare(Entity entity, PlayMakerFSM[] host, PlayMakerFSM[] client, Entity source) {
        if (entity.Type is EntityType.Ooma or EntityType.OomaCorpse or EntityType.OomaCore &&
            CoopContext.LocalScene.StartsWith("GG_Uumuu", StringComparison.Ordinal)) entity.BossTargetScope = true;
        if (!entity.BossTargetScope || host.Length == 0 || host.Length != client.Length) return;
        // Only the main AI can open a new decision boundary; auxiliary FSMs
        // (including those named Idle) cannot change an attack's target.
        var control = host.FirstOrDefault(f => f.FsmName == "Control") ??
            host.FirstOrDefault(f => f.FsmStates.Any(s => BossTargetCommitment.IsDecisionState(s.Name))) ?? host[0];
        entity.BossTargetControl = control.Fsm;
        entity.BossTargetVisit = CoopContext.LocalVisit;
        entity.InheritedBossTarget = source != null && source.BossTargetScope && !BossDefeatSignals.IsBoss(entity.Type);
        FsmString Append(PlayMakerFSM fsm) {
            var receipt = fsm.FsmVariables.FindFsmString(ReceiptName);
            if (receipt == null) {
                receipt = new FsmString { Name = ReceiptName };
                fsm.FsmVariables.StringVariables = fsm.FsmVariables.StringVariables.Concat(new[] { receipt }).ToArray();
            }
            // A pooled object or reconnect is a new entity lifetime. Never
            // inherit its previous registration's target accidentally.
            receipt.Value = "";
            return receipt;
        }
        entity.BossTargetReceipt = Append(control);
        Append(client[Array.IndexOf(host, control)]);
        if (entity.InheritedBossTarget) {
            if (source.IsNativeAuthority) Target(source, false);
            entity.BossTargetReceipt.Value = source.BossTargetReceipt?.Value ?? "";
        }
    }

    internal static void StateEntered(Entity entity, FsmState state) {
        if (CoopContext.FlexibleBossTargetingOn?.Invoke() == true && entity.IsNativeAuthority &&
            ReferenceEquals(state.Fsm, entity.BossTargetControl) &&
            (BossTargetCommitment.IsDecisionState(state.Name) ||
             (entity.Type == EntityType.Grimm || entity.Type == EntityType.NightmareKingGrimm) && state.Name == "Move Choice")) Target(entity, true);
    }

    private static GameObject Target(Entity entity, bool decisionBoundary) {
        if (entity.BossTargetReceipt == null || CoopContext.ChooseBossTarget == null) return null;
        if (entity.BossTargetVisit != CoopContext.LocalVisit) {
            entity.BossTargetVisit = CoopContext.LocalVisit;
            entity.BossTargetReceipt.Value = "";
        }
        var session = CoopContext.BossTargetSession?.Invoke() ?? "";
        var choice = entity.BossTargetChoice;
        if (choice == null || entity.LastBossTargetReceipt != entity.BossTargetReceipt.Value || choice.Session != session || choice.Epoch != CoopContext.Epoch) {
            choice = BossTargetCommitment.Decode(entity.BossTargetReceipt.Value, session, CoopContext.Epoch);
            // Also remember an empty selection without repeatedly parsing it.
            choice.Session = session; choice.Epoch = CoopContext.Epoch;
            entity.BossTargetChoice = choice; entity.LastBossTargetReceipt = entity.BossTargetReceipt.Value;
        }
        var now = (DateTime.UtcNow - new DateTime(2020, 1, 1)).TotalSeconds;
        var maySwitch = choice.CanSwitch(now, decisionBoundary, entity.InheritedBossTarget);
        var id = CoopContext.ChooseBossTarget(entity.Object.Host, choice.Player, maySwitch);
        if (id != choice.Player || maySwitch) {
            choice.Commit(id, now, session, CoopContext.Epoch);
            entity.BossTargetReceipt.Value = choice.Encode();
            entity.LastBossTargetReceipt = entity.BossTargetReceipt.Value;
        }
        return id >= 0 ? CoopContext.BossTargetObject?.Invoke(id) : null;
    }
}
