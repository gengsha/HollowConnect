using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using Hkmp.Game.Client.Entity;
using Hkmp.Game.Client.Entity.Component;
using Hkmp.Networking.Packet.Data;
using HollowConnect;
using Newtonsoft.Json;

namespace HollowConnect.Smoke;

public sealed partial class P5WatchersReproProbe {
    private bool ContextChecksEnabled => Environment.GetEnvironmentVariable("HOLLOWCONNECT_P5_CONTEXT_CHECK") == "1";
    private readonly HashSet<int> completionChecks = new();
    private static readonly System.Reflection.FieldInfo CompleteCallbacks = typeof(BossSceneController).GetField("OnBossSceneComplete", InstanceFlags);
    private static readonly System.Reflection.FieldInfo DeadCallbacks = typeof(BossSceneController).GetField("OnBossesDead", InstanceFlags);
    private static int SequenceCallbacks(BossSceneController controller) =>
        ((Delegate)CompleteCallbacks.GetValue(controller))?.GetInvocationList().Count(d => (d.Method.DeclaringType?.FullName ?? "").StartsWith("BossSequenceController+")) ?? 0;

    // Synchronous fault injections are restored before the next simulation frame.
    // The following native fight still exercises the unmodified real room state.
    private void CheckLiveContext() {
        var controller = BossSceneController.Instance;
        var rollback = BossTravel.SaveState();
        var restoreField = typeof(BossTravel).GetField("transientRestore", StaticFlags);
        var priorRestore = restoreField.GetValue(null);
        var complete = CompleteCallbacks.GetValue(controller);
        var dead = DeadCallbacks.GetValue(controller);
        var dreamEvent = controller.DreamReturnEvent;
        var indexField = typeof(BossSequenceController).GetField("bossIndex", StaticFlags);
        var valid = BossTravel.CaptureContext();
        Require(valid != null && BossSequenceController.BossIndex == 31, "valid fifth context before guard injections");
        try {
            BossTravel.CommitTransientContext();
            indexField.SetValue(null, 30);
            Require(BossTravel.CaptureContext() == null, "mismatched sequence is not captured as Hall context");
            Require(BossTravel.ApplyContext(valid) && BossSequenceController.BossIndex == 31, "valid context replaces a stale index");
            var empty = JsonConvert.SerializeObject(new Message { Scene = Scene, BossLevel = controller.BossLevel });
            Require(!BossTravel.ApplyContext(empty) && BossSequenceController.IsInSequence && BossSequenceController.BossIndex == 31,
                "empty same-room context cannot erase the accepted Pantheon or roll back its index");
            var invalid = JsonConvert.DeserializeObject<Message>(valid); invalid.BossPlan = "[]";
            Require(!BossTravel.ApplyContext(JsonConvert.SerializeObject(invalid)) && BossSequenceController.BossIndex == 31 && BossTravel.HasTransientContext,
                "invalid plan preserves the previous accepted overlay");
            CompleteCallbacks.SetValue(controller, null);
            Require(BossTravel.ApplyContext(valid) && SequenceCallbacks(controller) == 1 && controller.DreamReturnEvent == "DREAM EXIT",
                "missing completion callback repaired on the current controller");
            Require(BossTravel.ApplyContext(valid) && SequenceCallbacks(controller) == 1 && BossSequenceController.BossIndex == 31,
                "repeated context does not duplicate completion or advance the index");
            BossTravel.RestoreTransientContext();
            Require(BossSequenceController.BossIndex == 30 && !BossTravel.HasTransientContext,
                "cancelled uncommitted overlay still restores its prior state");
        } finally {
            rollback(); restoreField.SetValue(null, priorRestore);
            CompleteCallbacks.SetValue(controller, complete); DeadCallbacks.SetValue(controller, dead);
            controller.DreamReturnEvent = dreamEvent;
        }
        Require(BossSequenceController.BossIndex == 31 && SequenceCallbacks(controller) == 1, "real room restored after guard injections");
    }

    private IEnumerator CheckCompletedContext(string oldContext) {
        yield return null; // BossRoomSynchronizer commits after EndBossScene returns.
        try {
            var room = Field<BossRoomSynchronizer>(manager, "_bossRoom");
            var index = BossSequenceController.BossIndex;
            Require(Field<bool>(room, "completionApplied") && !BossTravel.HasTransientContext, "native completion commits context on both roles");
            foreach (var type in new[] { EntityComponentType.BossSceneContext, EntityComponentType.BossSceneComplete }) {
                var packet = new EntityNetworkData { Type = type };
                packet.Packet.Write(oldContext); packet.Packet = new Hkmp.Networking.Packet.Packet(packet.Packet.ToArray());
                room.Receive(packet);
            }
            Require(Field<string>(room, "latest") == null && Field<string>(room, "pending") == null && !BossTravel.HasTransientContext,
                "late heartbeat and duplicate completion cannot reopen a finished room context");
            Require(!BossTravel.ApplyContext(oldContext) && BossSequenceController.BossIndex == index && !BossTravel.HasTransientContext,
                "ended controller rejects direct context reconfiguration");
            Flag(role + ".completion-checked");
        } catch (Exception e) { DumpFailure(e); }
    }
}
