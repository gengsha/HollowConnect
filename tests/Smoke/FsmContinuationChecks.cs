using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using System.Linq;
using Hkmp.Game.Client.Entity;
using Hkmp.Game.Client.Entity.Component;
using Hkmp.Networking.Client;
using Hkmp.Networking.Packet;
using Hkmp.Networking.Packet.Data;
using Hkmp.Networking.Packet.Update;
using HutongGames.PlayMaker;
using HutongGames.PlayMaker.Actions;
using UnityEngine;
using Object = UnityEngine.Object;

namespace HollowConnect.Smoke;

// Real PlayMaker actions through production Entity.MakeHost. These synthetic
// objects exercise continuation without setting any boss state or physics.
internal static class FsmContinuationChecks {
    internal static IEnumerator SnapshotBounds(Action<string> mark, bool baseline) {
        var net = new NetClient(new PacketManager());
        var sources = new List<GameObject>(); var entities = new List<Entity>();
        try {
            foreach (var replicaOnly in new[] { true, false }) {
                var source = new GameObject("HollowConnect snapshot bounds " + replicaOnly); source.SetActive(false); sources.Add(source);
                var wait = new Wait(); wait.Reset(); wait.time = 1000;
                var fsm = Create(source, null, wait);
                void Populate(FsmVariables variables, int count) {
                    variables.FloatVariables = Enumerable.Range(0, count).Select(i => new FsmFloat("Float" + i) { Value = -1 }).ToArray();
                    variables.IntVariables = Enumerable.Range(0, count).Select(i => new FsmInt("Int" + i) { Value = -1 }).ToArray();
                    variables.BoolVariables = Enumerable.Range(0, count).Select(i => new FsmBool("Bool" + i)).ToArray();
                    variables.StringVariables = Enumerable.Range(0, count).Select(i => new FsmString("String" + i) { Value = "untouched" }).ToArray();
                    variables.Vector2Variables = Enumerable.Range(0, count).Select(i => new FsmVector2("Vector2" + i)).ToArray();
                    variables.Vector3Variables = Enumerable.Range(0, count).Select(i => new FsmVector3("Vector3" + i)).ToArray();
                }
                Populate(fsm.FsmVariables, replicaOnly ? 0 : 1);
                var entity = new Entity(net, replicaOnly ? 0xffffffffffffffa0UL : 0xffffffffffffffa1UL, EntityType.DynamicHazard, source);
                entities.Add(entity); entity.InitializeClient();
                var replica = entity.Object.Client.GetComponent<PlayMakerFSM>();
                Populate(replica.FsmVariables, replicaOnly ? 1 : 2);
                if (!replicaOnly) Populate(fsm.FsmVariables, 2);
                var data = new EntityHostFsmData();
                data.Types.UnionWith(new[] { EntityHostFsmData.Type.Floats, EntityHostFsmData.Type.Ints, EntityHostFsmData.Type.Bools,
                    EntityHostFsmData.Type.Strings, EntityHostFsmData.Type.Vector2s, EntityHostFsmData.Type.Vector3s });
                var bad = (byte)(replicaOnly ? 0 : 1);
                data.Floats[bad] = 7; data.Ints[bad] = 7; data.Bools[bad] = true; data.Strings[bad] = "orphan";
                data.Vec2s[bad] = new Hkmp.Math.Vector2(7, 7); data.Vec3s[bad] = new Hkmp.Math.Vector3(7, 7, 7);
                if (replicaOnly) {
                    data.Types.Clear(); data.Types.Add(EntityHostFsmData.Type.Strings);
                } else {
                    data.Floats[0] = 3; data.Ints[0] = 4; data.Bools[0] = true; data.Strings[0] = "valid";
                    data.Vec2s[0] = new Hkmp.Math.Vector2(2, 3); data.Vec3s[0] = new Hkmp.Math.Vector3(2, 3, 4);
                }
                var threw = false;
                try { entity.UpdateHostFsmData(new Dictionary<byte, EntityHostFsmData> { [0] = data }); }
                catch (IndexOutOfRangeException) when (baseline) { threw = true; }
                if (baseline) {
                    if (!threw) throw new Exception("Baseline did not reproduce snapshot overflow");
                    mark("BASELINE CONFIRMED snapshot overflow " + (replicaOnly ? "replica-only variable" : "runtime-expanded variables"));
                    continue;
                }
                if (replica.FsmVariables.StringVariables[bad].Value != "untouched" || replica.FsmVariables.FloatVariables[bad].Value != -1 ||
                    replica.FsmVariables.IntVariables[bad].Value != -1 || replica.FsmVariables.BoolVariables[bad].Value ||
                    replica.FsmVariables.Vector2Variables[bad].Value != Vector2.zero || replica.FsmVariables.Vector3Variables[bad].Value != Vector3.zero)
                    throw new Exception("Unmapped variable partially changed before canonical snapshot validation");
                if (!replicaOnly) {
                    entity.MakeHost();
                    foreach (var current in new[] { fsm, replica })
                        if (current.FsmVariables.StringVariables[0].Value != "valid" || current.FsmVariables.FloatVariables[0].Value != 3 ||
                            current.FsmVariables.IntVariables[0].Value != 4 || !current.FsmVariables.BoolVariables[0].Value ||
                            current.FsmVariables.Vector2Variables[0].Value != new Vector2(2, 3) || current.FsmVariables.Vector3Variables[0].Value != new Vector3(2, 3, 4))
                            throw new Exception("Valid scalar updates or takeover failed after an unmapped field");
                    // A later native schema contraction must also survive
                    // takeover and sampling without manufacturing variables.
                    Populate(fsm.FsmVariables, 0);
                    entity.MakeHost();
                    source.SetActive(true);
                    typeof(Entity).GetField("nextFsmSample", BindingFlags.Instance | BindingFlags.NonPublic).SetValue(entity, 0f);
                    typeof(Entity).GetMethod("OnUpdate", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(entity, null);
                }
                mark("PASS snapshot bounds preserve all six scalar types " + (replicaOnly ? "replica-only variable" : "runtime-expanded variables and valid takeover"));
            }
            yield return null;
        } finally {
            foreach (var entity in entities) entity.Destroy();
            foreach (var source in sources) Object.Destroy(source);
            net.UpdateManager.StopUpdates(); net.Disconnect();
        }
    }
    internal static IEnumerator StringSnapshots(Action<string> mark) {
        // An unconnected sender makes packet sampling deterministic, without
        // publishing fixture IDs into the real room or racing its send timer.
        var net = new NetClient(new PacketManager());
        var sources = new List<GameObject>();
        var entities = new List<Entity>();
        Entity Make(string name, ulong id) {
            var source = new GameObject(name); source.SetActive(false); sources.Add(source);
            var wait = new Wait(); wait.Reset(); wait.time = 1000;
            var fsm = Create(source, null, wait);
            fsm.FsmVariables.StringVariables = new[] { new FsmString("Attack label") { Value = null } };
            var e = new Entity(net, id, EntityType.DynamicHazard, source); entities.Add(e);
            return e;
        }
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
        var packetField = typeof(ClientUpdateManager).BaseType.GetField("CurrentUpdatePacket", flags);
        var sampleField = typeof(Entity).GetField("nextFsmSample", flags);
        var tick = typeof(Entity).GetMethod("OnUpdate", flags);
        FsmString Variable(GameObject go) => go.GetComponent<PlayMakerFSM>().FsmVariables.StringVariables[0];
        EntityHostFsmData Roundtrip(EntityHostFsmData data) {
            var packet = new Packet(); data.WriteData(packet);
            var copy = new EntityHostFsmData(); copy.ReadData(new Packet(packet.ToArray())); return copy;
        }
        try {
            var sender = Make("HollowConnect string sender", 0xffffffffffffffc0UL); sender.InitializeHost();
            var receiver = Make("HollowConnect string receiver", 0xffffffffffffffc1UL); receiver.InitializeClient();
            var cache = new EntityHostFsmData();
            foreach (var value in new[] { "armed", "phase-two", null }) {
                packetField.SetValue(net.UpdateManager, new ServerUpdatePacket());
                Variable(sender.Object.Host).Value = value;
                sampleField.SetValue(sender, 0f); tick.Invoke(sender, null);
                var outgoing = (ServerUpdatePacket)packetField.GetValue(net.UpdateManager);
                if (!outgoing.TryGetSendingPacketData(ServerUpdatePacketId.ReliableEntityUpdate, out var raw))
                    throw new Exception("String transition did not publish a reliable update");
                var update = ((RawPacketDataCollection)raw).DataInstances.Cast<ReliableEntityUpdate>().Single(u => u.Id == sender.Id);
                if (!update.HostFsmData.TryGetValue(0, out var data) || !data.Types.Contains(EntityHostFsmData.Type.Strings) ||
                    !data.Strings.TryGetValue(0, out var sent) || sent != (value ?? string.Empty))
                    throw new Exception("Null-initialized FSM string transition was omitted or not wire-safe");
                cache.MergeData(Roundtrip(data));
                receiver.UpdateHostFsmData(new Dictionary<byte, EntityHostFsmData> { [0] = Roundtrip(data) });
                if (Variable(receiver.Object.Host).Value != (value ?? string.Empty) || Variable(receiver.Object.Client).Value != (value ?? string.Empty))
                    throw new Exception("String transition did not reach native and replica FSMs");
                mark("PASS FSM string live transition " + (value ?? "null-to-empty"));
            }
            packetField.SetValue(net.UpdateManager, new ServerUpdatePacket());
            sampleField.SetValue(sender, 0f); tick.Invoke(sender, null);
            var unchanged = (ServerUpdatePacket)packetField.GetValue(net.UpdateManager);
            if (unchanged.TryGetSendingPacketData(ServerUpdatePacketId.ReliableEntityUpdate, out var same) &&
                ((RawPacketDataCollection)same).DataInstances.Cast<ReliableEntityUpdate>().Any(u => u.Id == sender.Id &&
                    u.HostFsmData.Values.Any(d => d.Types.Contains(EntityHostFsmData.Type.Strings))))
                throw new Exception("Unchanged null string was published repeatedly");
            var late = Make("HollowConnect string late receiver", 0xffffffffffffffc2UL); late.InitializeClient();
            late.UpdateHostFsmData(new Dictionary<byte, EntityHostFsmData> { [0] = Roundtrip(cache) });
            if (Variable(late.Object.Host).Value != string.Empty || Variable(late.Object.Client).Value != string.Empty)
                throw new Exception("Late string snapshot did not preserve the clear");
            Variable(late.Object.Host).Value = "stale local value";
            late.MakeHost();
            if (Variable(late.Object.Host).Value != string.Empty)
                throw new Exception("Takeover did not restore the cleared string snapshot");
            mark("PASS FSM string null deduplication, late snapshot and takeover");
            yield return null;
        } finally {
            foreach (var e in entities) e.Destroy();
            foreach (var source in sources) Object.Destroy(source);
            net.UpdateManager.StopUpdates(); net.Disconnect();
        }
    }

    private static PlayMakerFSM Create(GameObject source, FsmFloat variable, params FsmStateAction[] actions) {
        var fsm = source.AddComponent<PlayMakerFSM>();
        fsm.Reset();
        fsm.FsmName = "Continuation";
        if (variable != null) fsm.Fsm.Variables.FloatVariables = new[] { variable };
        var moving = new FsmState(fsm.Fsm) { Name = "Moving", Actions = actions };
        var done = new FsmState(fsm.Fsm) { Name = "Done", Actions = Array.Empty<FsmStateAction>() };
        moving.Transitions = new[] { new FsmTransition { FsmEvent = FsmEvent.Finished, ToState = "Done" } };
        done.Transitions = Array.Empty<FsmTransition>();
        fsm.Fsm.States = new[] { moving, done };
        fsm.Fsm.StartState = "Moving";
        fsm.Fsm.Events = new[] { FsmEvent.Finished };
        foreach (var state in fsm.FsmStates) {
            foreach (var action in state.Actions) action.Init(state);
            state.SaveActions();
        }
        fsm.Fsm.Reinitialize();
        fsm.Fsm.Init(fsm);
        return fsm;
    }

    internal static IEnumerator Run(NetClient net, Action<string> mark) {
        if (Environment.GetEnvironmentVariable("HOLLOWCONNECT_COMBAT_SNAPSHOT_CHECK") == "1") {
            yield return CombatSnapshots(net, mark);
            if (Environment.GetEnvironmentVariable("HOLLOWCONNECT_FSM_CONTINUATION_ONLY") == "combat") yield break;
        }
        if (Environment.GetEnvironmentVariable("HOLLOWCONNECT_FSM_ENTRY_CHECK") == "1") {
            yield return Entry(net, mark);
            if (Environment.GetEnvironmentVariable("HOLLOWCONNECT_FSM_CONTINUATION_ONLY") == "entry") yield break;
        }
        GameObject source = null;
        Entity entity = null;
        try {
            PlayMakerFSM fsm;
            if (Environment.GetEnvironmentVariable("HOLLOWCONNECT_FSM_HIERARCHY_CHECK") == "1") {
                yield return Hierarchy(net, mark);
                if (Environment.GetEnvironmentVariable("HOLLOWCONNECT_FSM_CONTINUATION_ONLY") == "hierarchy") yield break;
            }
            if (Environment.GetEnvironmentVariable("HOLLOWCONNECT_FSM_SNAPSHOT_CHECK") == "1") {
                source = new GameObject("HollowConnect state publication fixture"); source.SetActive(false);
                var wait = new Wait(); wait.Reset(); wait.time = 10; wait.finishEvent = FsmEvent.Finished;
                fsm = Create(source, null, wait); source.SetActive(true);
                yield return null;
                const ulong stateId = 0xffffffffffffffe3UL;
                entity = new Entity(net, stateId, EntityType.DynamicHazard, source);
                entity.InitializeHost();
                // The next native state starts inside the variable sampling window.
                typeof(Entity).GetField("nextFsmSample", BindingFlags.Instance | BindingFlags.NonPublic)
                    .SetValue(entity, Time.unscaledTime + 1);
                fsm.SetState("Done");
                var outgoing = (ServerUpdatePacket)typeof(ClientUpdateManager).BaseType
                    .GetField("CurrentUpdatePacket", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(net.UpdateManager);
                if (!outgoing.TryGetSendingPacketData(ServerUpdatePacketId.ReliableEntityUpdate, out var raw) ||
                    !((PacketDataCollection<ReliableEntityUpdate>)raw).DataInstances.Cast<ReliableEntityUpdate>().Any(u => u.Id == stateId &&
                        u.HostFsmData.TryGetValue(0, out var d) && d.Types.Contains(EntityHostFsmData.Type.State) && d.CurrentState == 1))
                    throw new Exception("Native state transition was not published before another Unity update/sample");
                mark("PASS native state transition publishes inside variable sampling window");
                entity.InitializeClient();
                var empty = new EntityHostFsmData { CurrentState = byte.MaxValue };
                empty.Types.Add(EntityHostFsmData.Type.State);
                entity.UpdateHostFsmData(new Dictionary<byte, EntityHostFsmData> { [0] = empty });
                var snapshots = (List<FsmSnapshot>)typeof(Entity).GetField("_fsmSnapshots", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(entity);
                if (!string.IsNullOrEmpty(snapshots[0].CurrentState))
                    throw new Exception("Empty native state retained a previous attack's takeover state");
                mark("PASS empty native state clears the previous attack snapshot");
                entity.Destroy(); entity = null; Object.Destroy(source); source = null;

                source = new GameObject("HollowConnect reactivation snapshot fixture"); source.SetActive(false);
                wait = new Wait(); wait.Reset(); wait.time = 10; wait.finishEvent = FsmEvent.Finished;
                fsm = Create(source, null, wait); source.SetActive(true);
                yield return null;
                source.SetActive(false);
                const ulong activationId = 0xffffffffffffffe4UL;
                entity = new Entity(net, activationId, EntityType.DynamicHazard, source);
                entity.InitializeHost();
                source.SetActive(true);
                outgoing = (ServerUpdatePacket)typeof(ClientUpdateManager).BaseType
                    .GetField("CurrentUpdatePacket", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(net.UpdateManager);
                if (!outgoing.TryGetSendingPacketData(ServerUpdatePacketId.ReliableEntityUpdate, out raw) ||
                    !((PacketDataCollection<ReliableEntityUpdate>)raw).DataInstances.Cast<ReliableEntityUpdate>().Any(u =>
                        u.Id == activationId && u.UpdateTypes.Contains(EntityUpdateType.Active) && u.IsActive &&
                        u.HostFsmData.TryGetValue(0, out var d) && d.Types.Contains(EntityHostFsmData.Type.State) && d.CurrentState == 0))
                    throw new Exception("Native reactivation published a new FSM state with the old inactive snapshot");
                mark("PASS native reactivation publishes active and its new FSM state together");
                entity.Destroy(); entity = null; Object.Destroy(source); source = null;
                if (Environment.GetEnvironmentVariable("HOLLOWCONNECT_FSM_CONTINUATION_ONLY") == "snapshot") yield break;
            }
            if (Environment.GetEnvironmentVariable("HOLLOWCONNECT_FSM_CONTINUATION_ONLY") != "deceleration") {
                source = new GameObject("HollowConnect continuation fixture"); source.SetActive(false);
                var tween = new iTweenMoveTo(); tween.Reset();
                tween.gameObject = new FsmOwnerDefault { OwnerOption = OwnerDefaultOption.UseOwner };
                tween.vectorPosition = source.transform.position + new Vector3(6, 0, 0);
                tween.time = .8f; tween.orientToPath = false;
                tween.finishEvent = FsmEvent.Finished;
                fsm = Create(source, null, tween);
                source.SetActive(true);
                yield return null; yield return null;
                if (fsm.ActiveStateName != "Moving") throw new Exception("Tween continuation fixture never started");
                entity = new Entity(net, 0xffffffffffffffe0UL, EntityType.DynamicHazard, source);
                entity.InitializeClient(); entity.UpdateIsActive(true);
                entity.MakeHost();
                mark("TWEEN TAKEOVER state="+fsm.ActiveStateName+" pos="+source.transform.position);
                if (fsm.ActiveStateName != "Moving") throw new Exception("Takeover skipped unfinished tween and emitted completion before return");
                var tweenEnd = Time.realtimeSinceStartup + 3;
                while (fsm.ActiveStateName == "Moving" && Time.realtimeSinceStartup < tweenEnd) yield return null;
                if (fsm.ActiveStateName != "Done" || Math.Abs(source.transform.position.x - 6) > .1f)
                    throw new Exception("Restored tween did not reach its destination and native completion");
                mark("PASS unfinished absolute tween resumes before native completion");
                entity.Destroy(); entity = null;
                iTween.Stop(source); Object.Destroy(source);
            }

            if (Environment.GetEnvironmentVariable("HOLLOWCONNECT_FSM_CONTINUATION_ONLY") != "deceleration") {
                source = new GameObject("HollowConnect dormant continuation fixture"); source.SetActive(false);
                var tween = new iTweenMoveTo(); tween.Reset();
                tween.gameObject = new FsmOwnerDefault { OwnerOption = OwnerDefaultOption.UseOwner };
                tween.vectorPosition = new Vector3(6, 0, 0); tween.time = .8f; tween.orientToPath = false;
                tween.finishEvent = FsmEvent.Finished;
                fsm = Create(source, null, tween); source.SetActive(true);
                yield return null; yield return null;
                if (fsm.ActiveStateName != "Moving") throw new Exception("Dormant continuation fixture never started");
                entity = new Entity(net, 0xffffffffffffffe2UL, EntityType.DynamicHazard, source);
                entity.InitializeClient(); // Authoritative snapshot remains inactive, with the last Moving state.
                entity.MakeHost();
                yield return null;
                mark("DORMANT TAKEOVER active="+source.activeInHierarchy+" tweens="+source.GetComponents<iTween>().Length);
                if (source.activeInHierarchy || source.GetComponents<iTween>().Length != 0)
                    throw new Exception("Inactive takeover created or retained a native tween controller");
                source.SetActive(true);
                var dormantEnd = Time.realtimeSinceStartup + 3;
                while (fsm.ActiveStateName != "Done" && Time.realtimeSinceStartup < dormantEnd) yield return null;
                if (fsm.ActiveStateName != "Done" || Math.Abs(source.transform.position.x - 6) > .1f)
                    throw new Exception("Native activation after dormant takeover did not complete normally");
                mark("PASS dormant takeover stays stopped until native activation and then completes");
                entity.Destroy(); entity = null; iTween.Stop(source); Object.Destroy(source);
            }

            source = new GameObject("HollowConnect deceleration fixture"); source.SetActive(false);
            var body = source.AddComponent<Rigidbody2D>(); body.gravityScale = 0;
            var speed = new FsmFloat { Name = "Speed", UseVariable = true, Value = 38 };
            var brake = new DecelerateV2(); brake.Reset();
            brake.gameObject = new FsmOwnerDefault { OwnerOption = OwnerDefaultOption.UseOwner };
            brake.deceleration = .8f;
            var launch = new SetVelocity2d(); launch.Reset(); launch.gameObject = brake.gameObject;
            launch.vector = new FsmVector2 { UseVariable = true };
            launch.x = 38; launch.y = 0;
            var measure = new GetSpeed2d(); measure.Reset(); measure.gameObject = brake.gameObject;
            measure.storeResult = speed; measure.everyFrame = true;
            var compare = new FloatCompare(); compare.Reset();
            compare.float1 = speed; compare.float2 = 0; compare.tolerance = .5f;
            compare.equal = FsmEvent.Finished; compare.everyFrame = true;
            fsm = Create(source, speed, launch, brake, measure, compare);
            source.SetActive(true);
            yield return null;
            if (fsm.ActiveStateName != "Moving") throw new Exception("Deceleration continuation fixture never started");
            entity = new Entity(net, 0xffffffffffffffe1UL, EntityType.DynamicHazard, source);
            entity.InitializeClient(); entity.UpdateIsActive(true);
            // Feed the same authoritative velocity packet as a remote entity.
            var velocity = new EntityNetworkData { Type = EntityComponentType.Velocity };
            var velocityPacket = new Packet(); velocityPacket.Write(38f); velocityPacket.Write(0f);
            velocity.Packet = new Packet(velocityPacket.ToArray());
            entity.UpdateData(new List<EntityNetworkData> { velocity }, true);
            entity.MakeHost();
            var initial = body.velocity.magnitude;
            mark("DECEL TAKEOVER state="+fsm.ActiveStateName+" speed="+initial);
            if (initial < 5) throw new Exception("Deceleration fixture lost its moving snapshot");
            var end = Time.realtimeSinceStartup + 3;
            while (fsm.ActiveStateName == "Moving" && Time.realtimeSinceStartup < end) yield return null;
            if (fsm.ActiveStateName != "Done" || body.velocity.magnitude > .5f)
                throw new Exception("Takeover dropped native fixed-update deceleration");
            mark("PASS native fixed-update deceleration resumes and completes");
        } finally {
            entity?.Destroy();
            if (source != null) { iTween.Stop(source); Object.Destroy(source); }
        }
    }

    private static IEnumerator Entry(NetClient net, Action<string> mark) {
        var parent = new GameObject("HollowConnect state entry fixture"); parent.SetActive(false);
        var child = new GameObject("HollowConnect state entry child"); child.SetActive(false);
        Entity parentEntity = null, childEntity = null;
        EntityHooks.Register();
        try {
            var childWait = new Wait(); childWait.Reset(); childWait.time = 10;
            var childFsm = Create(child, null, childWait); child.SetActive(true); yield return null; child.SetActive(false);
            var wait = new Wait(); wait.Reset(); wait.time = 10;
            var fsm = Create(parent, null, wait);
            var activate = new ActivateGameObject(); activate.Reset();
            activate.gameObject = new FsmOwnerDefault { OwnerOption = OwnerDefaultOption.SpecifyGameObject, GameObject = child };
            activate.activate = true;
            var done = fsm.FsmStates[1]; done.Actions = new FsmStateAction[] { activate }; activate.Init(done); done.SaveActions();
            parent.SetActive(true); yield return null;
            const ulong parentId = 0xffffffffffffffe6UL, childId = 0xffffffffffffffe7UL;
            parentEntity = new Entity(net, parentId, EntityType.DynamicHazard, parent); parentEntity.InitializeHost();
            childEntity = new Entity(net, childId, EntityType.DynamicHazard, child); childEntity.InitializeHost();
            EntityReferences.Register(parentEntity); EntityReferences.Register(childEntity);
            var updateType = typeof(ClientUpdateManager).BaseType;
            var sendGate = updateType.GetField("Lock", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(net.UpdateManager);
            var currentPacket = updateType.GetField("CurrentUpdatePacket", BindingFlags.Instance | BindingFlags.NonPublic);
            bool partial = false, workerFinished = false;
            fsm.Fsm.StateChanged += state => {
                if (state.Name != "Done") return;
                // At the exact native boundary, let an independent sender try
                // the same lock as the production timer. No network delay or
                // scheduling assumption determines whether it sees a partial entry.
                var sender = new System.Threading.Thread(() => {
                    if (!System.Threading.Monitor.TryEnter(sendGate)) { workerFinished = true; return; }
                    try {
                        var packet = (ServerUpdatePacket)currentPacket.GetValue(net.UpdateManager);
                        if (!packet.TryGetSendingPacketData(ServerUpdatePacketId.ReliableEntityUpdate, out var raw)) return;
                        var updates = ((RawPacketDataCollection)raw).DataInstances.Cast<ReliableEntityUpdate>().ToArray();
                        var p = updates.Single(u => u.Id == parentId); var c = updates.Single(u => u.Id == childId);
                        partial = p.HostFsmData.TryGetValue(0, out var data) && data.Types.Contains(EntityHostFsmData.Type.State) &&
                            data.CurrentState == 1 && !c.IsActive;
                    } finally { System.Threading.Monitor.Exit(sendGate); workerFinished = true; }
                });
                sender.Start();
                if (!sender.Join(2000)) throw new Exception("State entry sender fixture did not finish");
            };
            fsm.SetState("Done");
            if (!workerFinished || partial) throw new Exception("Sender observed the new parent FSM state before its native child activation");
            var outgoing = (ServerUpdatePacket)currentPacket.GetValue(net.UpdateManager);
            if (!outgoing.TryGetSendingPacketData(ServerUpdatePacketId.ReliableEntityUpdate, out var finalRaw) ||
                !((RawPacketDataCollection)finalRaw).DataInstances.Cast<ReliableEntityUpdate>().Any(u => u.Id == childId && u.IsActive))
                throw new Exception("Completed native state entry did not publish its child activation");
            mark("PASS native state entry and child activation cannot be split by the sender");
        } finally {
            childEntity?.Destroy(); parentEntity?.Destroy(); EntityHooks.Unregister();
            Object.Destroy(child); Object.Destroy(parent);
        }
    }

    internal static IEnumerator CombatSnapshots(Action<string> mark) {
        var net = new NetClient(new PacketManager());
        try { yield return CombatSnapshots(net, mark); }
        finally { net.UpdateManager.StopUpdates(); net.Disconnect(); }
    }

    private static IEnumerator CombatSnapshots(NetClient net, Action<string> mark) {
        var source = new GameObject("HollowConnect combat ordering fixture"); source.SetActive(false);
        var child = new GameObject("Child"); child.transform.SetParent(source.transform, false);
        for (var i = 0; i < 6; i++) new GameObject("Visual " + i).transform.SetParent(source.transform, false);
        var collider = source.AddComponent<BoxCollider2D>();
        Entity entity = null;
        var receiver = new Hkmp.Networking.Server.ServerUpdateManager();
        try {
            const ulong id = 0xffffffffffffffe6UL;
            entity = new Entity(net, id, EntityType.DynamicHazard, source);
            EntityReferences.Register(entity); entity.InitializeClient();
            var components = (Dictionary<EntityComponentType, EntityComponent>)typeof(Entity)
                .GetField("_components", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(entity);
            var combat = (CombatStateComponent)components[EntityComponentType.CombatState];
            var tick = typeof(CombatStateComponent).GetMethod("Tick", BindingFlags.Instance | BindingFlags.NonPublic);
            var current = typeof(ClientUpdateManager).BaseType.GetField("CurrentUpdatePacket", BindingFlags.Instance | BindingFlags.NonPublic);
            byte[] Capture(bool enabled) {
                collider.enabled = enabled; child.SetActive(enabled);
                combat.IsControlled = false;
                try { combat.InitializeHost(); tick.Invoke(combat, null); }
                finally { combat.IsControlled = true; }
                var outgoing = (ServerUpdatePacket)current.GetValue(net.UpdateManager);
                if (!outgoing.TryGetSendingPacketData(ServerUpdatePacketId.ReliableEntityUpdate, out var raw))
                    throw new Exception("Combat ordering fixture did not publish a snapshot");
                return ((RawPacketDataCollection)raw).DataInstances.Cast<ReliableEntityUpdate>().Single(u => u.Id == id)
                    .GenericData.Last(d => d.Type == EntityComponentType.CombatState).Packet.ToArray();
            }
            var older = Capture(false); var newer = Capture(true);
            var published = (ServerUpdatePacket)current.GetValue(net.UpdateManager);
            published.TryGetSendingPacketData(ServerUpdatePacketId.ReliableEntityUpdate, out var publishedRaw);
            var publishedData = ((RawPacketDataCollection)publishedRaw).DataInstances.Cast<ReliableEntityUpdate>()
                .Single(u => u.Id == id).GenericData.Last(d => d.Type == EntityComponentType.CombatState);
            // Force only the sampling deadline, preserving production history.
            // Two unchanged samples reuse scratch storage; a later mutation
            // must neither resend unchanged state nor alter the queued payload.
            void Sample() {
                typeof(CombatStateComponent).GetField("next", BindingFlags.Instance | BindingFlags.NonPublic).SetValue(combat, 0f);
                combat.IsControlled = false;
                try { tick.Invoke(combat, null); } finally { combat.IsControlled = true; }
            }
            current.SetValue(net.UpdateManager, new ServerUpdatePacket()); Sample(); Sample();
            var unchanged = (ServerUpdatePacket)current.GetValue(net.UpdateManager);
            if (unchanged.TryGetSendingPacketData(ServerUpdatePacketId.ReliableEntityUpdate, out _))
                throw new Exception("Unchanged combat snapshot was sent repeatedly");
            collider.enabled = false; Sample();
            var changed = (ServerUpdatePacket)current.GetValue(net.UpdateManager);
            if (!changed.TryGetSendingPacketData(ServerUpdatePacketId.ReliableEntityUpdate, out _) || !publishedData.Packet.ToArray().SequenceEqual(newer))
                throw new Exception("Combat scratch reuse missed a change or mutated a published snapshot");
            mark("PASS unchanged combat sampling and changed scratch preserve queued snapshots");
            var manager = new PacketManager();
            manager.RegisterServerUpdatePacketHandler<ReliableEntityUpdate>(ServerUpdatePacketId.ReliableEntityUpdate,
                (_, update) => entity.UpdateData(update.GenericData, false));
            ServerUpdatePacket Snapshot(ushort sequence, byte[] bytes) {
                var packet = new ServerUpdatePacket { Sequence = sequence };
                var update = new ReliableEntityUpdate { Id = id }; update.UpdateTypes.Add(EntityUpdateType.Data);
                update.GenericData.Add(new EntityNetworkData { Type = EntityComponentType.CombatState, Packet = new Packet(bytes) });
                var collection = new PacketDataCollection<ReliableEntityUpdate>(); collection.DataInstances.Add(update);
                packet.SetSendingPacketData(ServerUpdatePacketId.ReliableEntityUpdate, collection); return packet;
            }
            void Deliver(ServerUpdatePacket outgoing) {
                var wire = new Packet(); outgoing.CreatePacket(wire);
                var incoming = new ServerUpdatePacket();
                if (!incoming.ReadPacket(PacketManager.ReadDatagram(wire.ToArray()))) throw new Exception("Combat ordering datagram did not decode");
                receiver.OnReceivePacket<ServerUpdatePacket, ServerUpdatePacketId>(incoming);
                manager.HandleServerUpdatePacket(2, incoming);
            }
            receiver.StartUpdates();
            var late = Snapshot(65534, older);
            Deliver(Snapshot(0, newer)); Deliver(late);
            var carrier = new ServerUpdatePacket { Sequence = 1 }; carrier.SetLostReliableData(late); Deliver(carrier);
            var replicaChild = entity.Object.Client.transform.Find("Child").gameObject;
            if (!collider.enabled || !entity.Object.Client.GetComponent<BoxCollider2D>().enabled || !replicaChild.activeSelf)
                throw new Exception("Delayed combat snapshot disabled a newer native/replica collider or child");
            entity.UpdateData(new List<EntityNetworkData> { Hkmp.Game.Server.SnapshotPayload.Copy(publishedData) }, true);
            if (!collider.enabled || !replicaChild.activeSelf) throw new Exception("Complete cached combat snapshot lost collider or child state");
            if (Environment.GetEnvironmentVariable("HOLLOWCONNECT_SCENE_SNAPSHOT_ASSERTIONS") == "1")
                yield return NestedCombatSnapshots(entity, older, newer, mark);
            entity.UpdateIsActive(true); entity.MakeHost();
            if (!collider.enabled || !child.activeSelf)
                throw new Exception("Takeover restored stale combat collider or child state");
            mark("PASS delayed combat snapshots preserve native and replica colliders and child activation through takeover");
            yield return null;
        } finally {
            receiver.StopUpdates(); entity?.Destroy();
            if (entity?.Object.Client != null) Object.Destroy(entity.Object.Client);
            Object.Destroy(source);
        }
    }

    private static IEnumerator NestedCombatSnapshots(Entity entity, byte[] older, byte[] newer, Action<string> mark) {
        foreach (var transfer in new[] { false, true }) foreach (var snapshotNewer in new[] { false, true }) {
            var receiver = new ClientUpdateManager();
            ClientUpdatePacket Make(ushort sequence, bool nested, bool active, byte[] bytes) {
                var packet = new ClientUpdatePacket { Sequence = sequence };
                var update = new ReliableEntityUpdate { Id = entity.Id, IsActive = active };
                update.UpdateTypes.UnionWith(new[] { EntityUpdateType.Active, EntityUpdateType.Data });
                update.GenericData.Add(new EntityNetworkData { Type = EntityComponentType.CombatState, Packet = new Packet(bytes) });
                if (nested) {
                    var scene = new ClientPlayerAlreadyInScene(); scene.ReliableEntityUpdateList.Add(update);
                    if (transfer) packet.SetSendingPacketData(ClientUpdatePacketId.SceneHostTransfer, new HostTransfer { SceneName = "fixture", Snapshot = scene });
                    else packet.SetSendingPacketData(ClientUpdatePacketId.PlayerAlreadyInScene, scene);
                } else {
                    var batch = new PacketDataCollection<ReliableEntityUpdate>(); batch.DataInstances.Add(update);
                    packet.SetSendingPacketData(ClientUpdatePacketId.ReliableEntityUpdate, batch);
                }
                return packet;
            }
            void Apply(ReliableEntityUpdate update, bool snapshot) {
                if (update.UpdateTypes.Contains(EntityUpdateType.Active)) entity.UpdateIsActive(update.IsActive);
                if (update.UpdateTypes.Contains(EntityUpdateType.Data)) entity.UpdateData(update.GenericData, snapshot);
            }
            void Deliver(ClientUpdatePacket outgoing) {
                var wire = new Packet(); outgoing.CreatePacket(wire);
                var incoming = new ClientUpdatePacket();
                if (!incoming.ReadPacket(PacketManager.ReadDatagram(wire.ToArray()))) throw new Exception("Nested combat datagram did not decode");
                receiver.OnReceivePacket<ClientUpdatePacket, ClientUpdatePacketId>(incoming);
                foreach (var data in incoming.GetPacketData().Values) {
                    var scene = data is HostTransfer handoff ? handoff.Snapshot : data as ClientPlayerAlreadyInScene;
                    if (scene != null) foreach (var update in scene.ReliableEntityUpdateList) Apply(update, true);
                    else if (data is RawPacketDataCollection batch) foreach (var update in batch.DataInstances.OfType<ReliableEntityUpdate>()) Apply(update, false);
                }
            }
            receiver.StartUpdates();
            try {
                var latest = Make(0, snapshotNewer, true, newer);
                var stale = Make(65534, !snapshotNewer, false, older);
                Deliver(latest);
                var carrier = new ClientUpdatePacket { Sequence = 1 }; carrier.SetLostReliableData(stale);
                Deliver(carrier); Deliver(stale); Deliver(latest);
                if (!entity.Object.Client.activeSelf || !entity.Object.Host.GetComponent<BoxCollider2D>().enabled ||
                    !entity.Object.Client.GetComponent<BoxCollider2D>().enabled || !entity.Object.Client.transform.Find("Child").gameObject.activeSelf)
                    throw new Exception("Nested room snapshot/live update rolled back native/replica collider, child or visibility");
                mark("PASS nested room combat order transfer=" + transfer + " snapshotNewer=" + snapshotNewer);
                yield return null;
            } finally { receiver.StopUpdates(); }
        }
    }

    private static IEnumerator Hierarchy(NetClient net, Action<string> mark) {
        var parent = new GameObject("HollowConnect hierarchy fixture parent");
        parent.transform.position = new Vector3(10, 20, 0);
        var source = new GameObject("HollowConnect hierarchy fixture"); source.SetActive(false);
        var replica = new GameObject("HollowConnect hierarchy fixture replica"); replica.SetActive(false);
        Entity entity = null;
        try {
            var tween = new iTweenMoveTo(); tween.Reset();
            tween.gameObject = new FsmOwnerDefault { OwnerOption = OwnerDefaultOption.UseOwner };
            tween.space = Space.Self; tween.vectorPosition = new Vector3(6, 0, 0);
            tween.time = .8f; tween.orientToPath = false; tween.finishEvent = FsmEvent.Finished;
            var fsm = Create(source, null, tween); Create(replica, null, new Wait { time = 10 });
            source.SetActive(true); yield return null; yield return null;
            replica.transform.SetParent(parent.transform, true); replica.transform.position = source.transform.position;
            const ulong id = 0xffffffffffffffe5UL;
            entity = new Entity(net, id, EntityType.DynamicHazard, source, replica);
            EntityReferences.Register(entity);
            var components = (Dictionary<EntityComponentType, EntityComponent>)typeof(Entity)
                .GetField("_components", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(entity);
            var combat = (CombatStateComponent)components[EntityComponentType.CombatState];
            // Capture the source payload without promoting this paused receiver
            // to authority; InitializeClient does not demote an existing host.
            combat.IsControlled = false; combat.InitializeHost();
            typeof(CombatStateComponent).GetMethod("Tick", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(combat, null);
            combat.IsControlled = true;
            var outgoing = (ServerUpdatePacket)typeof(ClientUpdateManager).BaseType
                .GetField("CurrentUpdatePacket", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(net.UpdateManager);
            if (!outgoing.TryGetSendingPacketData(ServerUpdatePacketId.ReliableEntityUpdate, out var raw))
                throw new Exception("Hierarchy fixture did not publish combat state");
            var data = ((PacketDataCollection<ReliableEntityUpdate>)raw).DataInstances.Cast<ReliableEntityUpdate>()
                .Single(u => u.Id == id).GenericData.Last(d => d.Type == EntityComponentType.CombatState);
            entity.InitializeClient(); entity.UpdateIsActive(true);
            // This peer's dormant native hierarchy predates the remote detach.
            source.transform.SetParent(parent.transform, true);
            entity.UpdateData(new List<EntityNetworkData> { new EntityNetworkData {
                Type = data.Type, Packet = new Packet(data.Packet.ToArray())
            } }, true);
            if (replica.transform.parent != null || source.transform.parent != parent.transform)
                throw new Exception("Root parent snapshot did not detach only the replica while native simulation was paused");
            var position = new Hkmp.Math.Vector2(2, 0); entity.UpdatePosition(position);
            if (Vector2.Distance(replica.transform.position, new Vector2(2, 0)) > .01f)
                throw new Exception("Detached registered child interpreted world position in its previous parent space");
            entity.MakeHost();
            if (source.transform.parent != null || Vector2.Distance(source.transform.position, new Vector2(2, 0)) > .01f)
                throw new Exception("Takeover kept a stale native root parent or changed world position");
            var end = Time.realtimeSinceStartup + 3;
            while (fsm.ActiveStateName != "Done" && Time.realtimeSinceStartup < end) yield return null;
            if (fsm.ActiveStateName != "Done" || Vector2.Distance(source.transform.position, new Vector2(6, 0)) > .1f)
                throw new Exception("Local-space tween resumed in a stale parent coordinate system");
            mark("PASS detached entity root restores parent and world position before native local tween completion");
        } finally {
            entity?.Destroy(); iTween.Stop(source); Object.Destroy(source); Object.Destroy(replica); Object.Destroy(parent);
        }
    }
}
