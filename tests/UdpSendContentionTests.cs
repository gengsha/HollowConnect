using System;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Hkmp.Networking;
using Hkmp.Networking.Packet;
using Hkmp.Networking.Packet.Data;
using Hkmp.Networking.Packet.Update;
using Hkmp.Networking.Server;

internal static class UdpSendContentionTests {
    private sealed class ControlledPacket : ServerUpdatePacket {
        internal ManualResetEventSlim Encoding, Release;
        internal bool Fail;
        public ControlledPacket() { }
        public override void CreatePacket(Packet packet) {
            Encoding?.Set();
            if (Release != null && !Release.Wait(2000)) throw new TimeoutException("test encoder release");
            if (Fail) throw new InvalidOperationException("injected encoder failure");
            base.CreatePacket(packet);
        }
    }
    private sealed class Manager : UdpUpdateManager<ControlledPacket, ServerUpdatePacketId> {
        internal ControlledPacket Pending => CurrentUpdatePacket;
        internal void Position(float x) {
            lock (Lock) {
                var data = new PlayerUpdate { Position = new Hkmp.Math.Vector2(x, 0) };
                data.UpdateTypes.Add(PlayerUpdateType.Position);
                CurrentUpdatePacket.SetSendingPacketData(ServerUpdatePacketId.PlayerUpdate, data);
            }
        }
        internal void Receipt(string text) {
            lock (Lock) CurrentUpdatePacket.SetSendingPacketData(ServerUpdatePacketId.ChatMessage, new ChatMessage { Message = text });
        }
        public override void ResendReliableData(ControlledPacket lost) {
            lock (Lock) CurrentUpdatePacket.SetLostReliableData(lost);
        }
    }
    private static ServerUpdatePacket Read(Packet packet) {
        var result = new ServerUpdatePacket();
        if (packet == null || !result.ReadPacket(PacketManager.ReadDatagram(packet.ToArray()))) throw new Exception("invalid datagram");
        return result;
    }
    internal static void Register(Action<string, Action> test) {
        test("entity FSM fanout isolates one two and three recipients including detached batches", () => {
            var pending = typeof(ServerUpdateManager).BaseType.GetField("CurrentUpdatePacket", BindingFlags.Instance | BindingFlags.NonPublic);
            EntityHostFsmData State(ServerUpdateManager manager) {
                var packet = (ClientUpdatePacket)pending.GetValue(manager);
                if (!packet.TryGetSendingPacketData(ClientUpdatePacketId.ReliableEntityUpdate, out var data)) throw new Exception("missing entity batch");
                return ((PacketDataCollection<ReliableEntityUpdate>)data).DataInstances.Cast<ReliableEntityUpdate>().Single().HostFsmData[0];
            }
            byte[] Encode(EntityHostFsmData state) { var packet = new Packet(); state.WriteData(packet); return packet.ToArray(); }
            foreach (var count in new[] { 1, 2, 3 }) {
                var recipients = Enumerable.Range(0, count).Select(_ => new ServerUpdateManager()).ToArray();
                var source = new EntityHostFsmData { CurrentState = 17 };
                source.Types.UnionWith(new[] { EntityHostFsmData.Type.State, EntityHostFsmData.Type.Ints,
                    EntityHostFsmData.Type.Vector2s, EntityHostFsmData.Type.Vector3s });
                source.Ints[0] = 8; source.Vec2s[0] = new Hkmp.Math.Vector2(2, 3); source.Vec3s[0] = new Hkmp.Math.Vector3(4, 5, 6);
                foreach (var recipient in recipients) recipient.AddEntityHostFsmData(123, 0, source);
                var states = recipients.Select(State).ToArray();
                var frozen = states.Select(Encode).ToArray();
                // Detach each recipient exactly as the sender swaps its next
                // batch, while a different recipient can still merge updates.
                pending.SetValue(recipients[count - 1], new ClientUpdatePacket());
                var update = new EntityHostFsmData { CurrentState = 29 }; update.Types.Add(EntityHostFsmData.Type.State);
                recipients[0].AddEntityHostFsmData(123, 0, update);
                if (source.CurrentState != 17) throw new Exception("recipient merge changed the authority input");
                for (var i = 1; i < count; i++)
                    if (!frozen[i].SequenceEqual(Encode(states[i]))) throw new Exception("another recipient changed a queued or detached batch");
                if (count == 1 && !frozen[0].SequenceEqual(Encode(states[0]))) throw new Exception("new batch changed the old single-recipient batch");
                source.Ints[0] = 99; source.Vec2s[0].X = 99; source.Vec3s[0].Z = 99;
                if (states.Any(s => s.Ints[0] != 8 || s.Vec2s[0].X != 2 || s.Vec3s[0].Z != 6))
                    throw new Exception("sender-owned dictionaries or vectors leaked into a recipient");
                update.CurrentState = 31;
                if (State(recipients[0]).CurrentState != 29) throw new Exception("caller changed a merged or inserted next batch");
            }
        });
        test("outgoing encoding allows next gameplay updates without changing the detached batch", () => {
            var manager = new Manager(); manager.Position(1);
            using var entered = new ManualResetEventSlim(); using var release = new ManualResetEventSlim();
            manager.Pending.Encoding = entered; manager.Pending.Release = release;
            var sender = Task.Run(() => manager.BuildUpdatePacket()); Task publisher = null;
            try {
                if (!entered.Wait(2000)) throw new Exception("encoder did not start");
                publisher = Task.Run(() => { manager.Position(2); manager.Receipt("next receipt"); });
                if (!publisher.Wait(500)) throw new Exception("gameplay blocked behind packet encoding");
            } finally { release.Set(); sender.Wait(2500); publisher?.Wait(2500); }
            var first = Read(sender.Result); var next = Read(manager.BuildUpdatePacket());
            if (((PlayerUpdate)first.GetPacketData()[ServerUpdatePacketId.PlayerUpdate]).Position.X != 1 ||
                ((PlayerUpdate)next.GetPacketData()[ServerUpdatePacketId.PlayerUpdate]).Position.X != 2 ||
                ((ChatMessage)next.GetPacketData()[ServerUpdatePacketId.ChatMessage]).Message != "next receipt" ||
                first.Sequence != 0 || next.Sequence != 1)
                throw new Exception("encoding mixed concurrent updates or reordered batches");
        });
        test("failed detached encoding preserves reliable data for a subsequent carrier", () => {
            var manager = new Manager(); manager.Receipt("first receipt"); manager.Pending.Fail = true;
            if (manager.BuildUpdatePacket() != null) throw new Exception("failed encoding returned bytes");
            manager.Position(3);
            var next = Read(manager.BuildUpdatePacket());
            if (((ChatMessage)next.GetPacketData()[ServerUpdatePacketId.ChatMessage]).Message != "first receipt" ||
                ((PlayerUpdate)next.GetPacketData()[ServerUpdatePacketId.PlayerUpdate]).Position.X != 3 || next.Sequence != 1)
                throw new Exception("failed encoding lost the reliable receipt");
        });
    }
}
