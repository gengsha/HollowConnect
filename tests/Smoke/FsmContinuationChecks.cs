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
        GameObject source = null;
        Entity entity = null;
        try {
            PlayMakerFSM fsm;
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
}
