using System;
using System.Collections.Generic;
using System.Linq;
using Hkmp.Game.Client.Entity.Component;
using Hkmp.Networking;
using Hkmp.Networking.Client;
using Hkmp.Networking.Packet;
using Hkmp.Networking.Packet.Data;
using Hkmp.Networking.Packet.Update;

internal static class SceneSnapshotProbe {
    internal static void Ordering(ushort sequence, bool transfer, bool snapshotNewer, bool resend) {
        ClientSessionProbe.DrainMainThread();
        var manager = new PacketManager(); var client = new NetClient(manager);
        var active = false; var state = new EntityHostFsmData();
        var position = 0f; var combat = 0; var events = 0; var envelopes = 0;
        void ApplyReliable(ReliableEntityUpdate update) {
            if (update.UpdateTypes.Contains(EntityUpdateType.Active)) active = update.IsActive;
            if (update.HostFsmData.TryGetValue(0, out var fsm)) state.MergeData(fsm);
            foreach (var data in update.GenericData) {
                if (data.Type == EntityComponentType.CombatState) combat = data.Packet.ReadByte();
                else events++;
            }
        }
        void ApplyMotion(EntityUpdate update) {
            if (update.UpdateTypes.Contains(EntityUpdateType.Position)) position = update.Position.X;
        }
        void ApplyScene(ClientPlayerAlreadyInScene scene) {
            if (scene.SceneName != "Fixture" || scene.Visit != 7 || scene.Epoch != 2 ||
                scene.PlayerEnterSceneList.Count != 1 || scene.EntitySpawnList.Count != 1)
                throw new Exception("Filtering entity fields discarded room presence or spawn metadata");
            envelopes++;
            foreach (var update in scene.EntityUpdateList) ApplyMotion(update);
            foreach (var update in scene.ReliableEntityUpdateList) ApplyReliable(update);
        }
        manager.RegisterClientUpdatePacketHandler<EntityUpdate>(ClientUpdatePacketId.EntityUpdate, ApplyMotion);
        manager.RegisterClientUpdatePacketHandler<ReliableEntityUpdate>(ClientUpdatePacketId.ReliableEntityUpdate, ApplyReliable);
        manager.RegisterClientUpdatePacketHandler<ClientPlayerAlreadyInScene>(ClientUpdatePacketId.PlayerAlreadyInScene, ApplyScene);
        manager.RegisterClientUpdatePacketHandler<HostTransfer>(ClientUpdatePacketId.SceneHostTransfer, data => ApplyScene(data.Snapshot));
        ClientUpdatePacket Make(ushort seq, byte value, bool nested) {
            var packet = new ClientUpdatePacket { Sequence = seq };
            var reliable = new ReliableEntityUpdate { SceneName = "Fixture", Visit = 7, Epoch = 2, Id = 17, IsActive = value == 3 };
            reliable.UpdateTypes.UnionWith(new[] { EntityUpdateType.Active, EntityUpdateType.HostFsm, EntityUpdateType.Data });
            var fsm = new EntityHostFsmData { CurrentState = value };
            fsm.Types.UnionWith(new[] { EntityHostFsmData.Type.State, EntityHostFsmData.Type.Floats, EntityHostFsmData.Type.Strings });
            fsm.Floats[0] = value; fsm.Strings[0] = value == 3 ? "new" : "old";
            if (value == 1) fsm.Floats[1] = 11; // An untouched sparse field must survive.
            reliable.HostFsmData[0] = fsm;
            var full = new EntityNetworkData { Type = EntityComponentType.CombatState }; full.Packet.Write(value);
            reliable.GenericData.Add(full);
            var motion = new EntityUpdate { SceneName = "Fixture", Visit = 7, Epoch = 2, Id = 17, Position = new Hkmp.Math.Vector2(value, 0) };
            motion.UpdateTypes.Add(EntityUpdateType.Position);
            if (nested) {
                var scene = new ClientPlayerAlreadyInScene { SceneName = "Fixture", Visit = 7, Epoch = 2 };
                scene.PlayerEnterSceneList.Add(new ClientPlayerEnterScene { Username = "fixture", Position = new Hkmp.Math.Vector2(0, 0) });
                scene.EntitySpawnList.Add(new EntitySpawn { Id = 18 });
                scene.EntityUpdateList.Add(motion); scene.ReliableEntityUpdateList.Add(reliable);
                if (transfer) packet.SetSendingPacketData(ClientUpdatePacketId.SceneHostTransfer, new HostTransfer { SceneName = "Fixture", Snapshot = scene });
                else packet.SetSendingPacketData(ClientUpdatePacketId.PlayerAlreadyInScene, scene);
            } else {
                var action = new EntityNetworkData { Type = EntityComponentType.Fsm }; action.Packet.Write((byte)1);
                reliable.GenericData.Add(action);
                var reliableBatch = new PacketDataCollection<ReliableEntityUpdate>(); reliableBatch.DataInstances.Add(reliable);
                packet.SetSendingPacketData(ClientUpdatePacketId.ReliableEntityUpdate, reliableBatch);
                var motionBatch = new PacketDataCollection<EntityUpdate>(); motionBatch.DataInstances.Add(motion);
                packet.SetSendingPacketData(ClientUpdatePacketId.EntityUpdate, motionBatch);
            }
            return packet;
        }
        uint message = 0;
        var receive = typeof(NetClient).GetMethod("OnReceiveData", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
        void Deliver(ClientUpdatePacket packet) {
            var wire = new Packet(); packet.CreatePacket(wire);
            foreach (var frame in DatagramFraming.Fragment(wire.ToArray(), message++).Reverse())
                receive.Invoke(client, new object[] { frame, frame.Length });
            ClientSessionProbe.DrainMainThread();
        }
        typeof(NetClient).GetProperty("ConnectionStatus").SetValue(client, ClientConnectionStatus.Connected, null);
        client.UpdateManager.StartUpdates();
        try {
            // Always deliver newer state first, then a late original or its
            // reliable retransmission, followed by duplicate deliveries.
            var newer = Make(sequence, 3, snapshotNewer);
            var older = Make(unchecked((ushort)(sequence - 1)), 1, !snapshotNewer);
            Deliver(newer);
            if (resend) {
                var carrier = new ClientUpdatePacket { Sequence = unchecked((ushort)(sequence + 1)) };
                carrier.SetLostReliableData(older); Deliver(carrier);
            } else Deliver(older);
            Deliver(older); Deliver(newer);
            if (!active || state.CurrentState != 3 || state.Floats[0] != 3 || state.Strings[0] != "new" || combat != 3 || position != 3)
                throw new Exception("A delayed room snapshot/live update rolled back newer active, FSM, position or combat state");
            if (state.Floats[1] != 11 || envelopes != 1 || events != 1)
                throw new Exception("Room snapshot filtering lost sparse fields, metadata or reliable events, or replayed a duplicate");
        } finally { client.Disconnect(); ClientSessionProbe.DrainMainThread(); }
    }
}
