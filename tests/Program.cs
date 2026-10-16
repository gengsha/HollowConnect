using System;
using System.Linq;
using System.Collections.Generic;
using System.IO;
using Hkmp.Game.Client.Entity;
using Hkmp.Game.Client.Entity.Component;
using Hkmp.Game.Server;
using Hkmp.Networking.Packet;
using Hkmp.Networking.Packet.Data;
using Hkmp.Networking.Packet.Update;
using HollowConnect;
using HollowConnect.Core;

internal static class Program {
    private static int passed, failed;
    private static readonly HashSet<string> selected = Environment.GetEnvironmentVariable("HOLLOWCONNECT_TEST_SELECTION") is string selectionPath
        ? new HashSet<string>(File.ReadAllLines(selectionPath).Where(s => s.Length > 0)) : null;
    private static void Test(string name, Action test) {
        if (selected != null && !selected.Remove(name)) return;
        try { test(); passed++; Console.WriteLine("PASS " + name); }
        catch (Exception e) { failed++; Console.WriteLine("FAIL " + name + ": " + e); }
    }
    private static void Check(bool value, string message = "invariant failed") { if (!value) throw new Exception(message); }
    private static void Near(float actual, float expected) => Check(Math.Abs(actual - expected) < .0001, actual + " != " + expected);
    private static T Roundtrip<T>(T value) where T : IPacketData, new() {
        var packet = new Packet(); value.WriteData(packet);
        var result = new T(); result.ReadData(new Packet(packet.ToArray())); return result;
    }
    private static Party Two() {
        var p = new Party(); p.Join(1, "host"); p.Join(2, "guest");
        p.Presence(1, 0, 1, "Boss", 0, 0, true, true, 1, 2, 100);
        p.Presence(2, 0, 1, "Boss", 2, 0, true, true, 1, 2, 100);
        p.Members[1].CheckpointScene = "Town"; p.Members[1].CheckpointMarker = "Bench";
        return p;
    }
    private static bool Down(Party party, ushort id, uint round, uint serial) {
        var m = party.Members[id];
        return party.Down(id, round, serial, m.Scene, m.X, m.Y, m.Seen);
    }
    private static bool Ready(Party p, ushort id, uint round) => p.Ready(id, round,
        p.Members[id].ReturnScene.Length != 0 ? p.Members[id].ReturnScene : "Town");
    private static Party Returnable() {
        var p = Two(); p.Members[2].CheckpointScene = "GuestBenchRoom";
        p.Members[2].CheckpointMarker = "GuestBench"; p.Members[2].CheckpointRespawnType = 1;
        Down(p, 2, 0, 1); return p;
    }
    private static int Main() {
        Test("main-thread backlog yields between complete callbacks and preserves FIFO", ClientSessionProbe.BacklogBudget);
        Test("scalar packet encoding preserves BitConverter wire bytes and floating-point bits", PacketEncodingProbe.WireFormat);
        Test("reused scratch snapshots do not change published packets", PacketEncodingProbe.ScratchOwnership);
        Test("compressed combat updates preserve independent full snapshots and reject invalid lengths", PacketEncodingProbe.CompleteCombatPayload);
        foreach (var sequence in new ushort[] { 0, 65523 }) {
            foreach (var step in new[] { 12, 13, 90, 91 })
                Test($"periodic loss delivers both enter directions once: start={sequence} lostStep={step}",
                    () => SelectiveAckProbe.PeriodicDelivery(sequence, step));
            var origin = unchecked((ushort)(sequence + 12));
            Test("selective ACK loss evidence tolerates reordering and duplicate reports: " + origin,
                () => SelectiveAckProbe.AckEvidence(origin));
        }
        Test("periodic loss delivers at congested send rate", () => SelectiveAckProbe.PeriodicDelivery(0, 90, 50));
        Test("reliable loss retains its timeout fallback without newer ACK evidence", SelectiveAckProbe.TimeoutFallback);
        foreach (var sequence in new ushort[] { 22, 1 })
            Test("nested reliable retransmissions retain origin across sequence " + sequence, () => ReliabilityProbe.NestedResends(sequence));
        Test("nested addon retransmissions retain origin across sequence wrap", ReliabilityProbe.NestedAddonResends);
        Test("consumed retransmission origins and delayed originals execute once", () => ReliabilityProbe.RepeatedOrigins(20, 0));
        Test("consumed origins survive beyond the 64-packet ACK history and sequence wrap", () => ReliabilityProbe.RepeatedOrigins(65530, 100));
        Test("consumed addon origins and duplicate carriers execute once", ReliabilityProbe.RepeatedAddonOrigins);
        foreach (var sequence in new ushort[] { 22, 1 }) {
            Test("delayed datagrams keep newer sparse entity snapshots across sequence " + sequence, () => ReliabilityProbe.ReorderedSnapshots(sequence, false));
            Test("delayed resends keep newer sparse entity snapshots across sequence " + sequence, () => ReliabilityProbe.ReorderedSnapshots(sequence, true));
        }
        Test("consumed origins allow fresh payloads after a complete ushort sequence wrap", ReliabilityProbe.CompleteSequenceWrap);
        foreach (var sequence in new ushort[] { 20, 1 }) {
            Test("delayed combat snapshots preserve newer collider state across sequence " + sequence, () => ReliabilityProbe.ReorderedCombatSnapshots(sequence, false));
            Test("resent combat snapshots preserve newer collider state across sequence " + sequence, () => ReliabilityProbe.ReorderedCombatSnapshots(sequence, true));
        }
        Test("client receive and main-thread dispatch preserve reordered fragmented entity updates", ReliabilityProbe.ClientReceivePath);
        foreach (var sequence in new ushort[] { 20, 0 })
            foreach (var transfer in new[] { false, true })
                foreach (var snapshotNewer in new[] { false, true })
                    foreach (var resend in new[] { false, true })
                        Test($"room snapshot/live order: sequence={sequence} transfer={transfer} snapshotNewer={snapshotNewer} resend={resend}",
                            () => SceneSnapshotProbe.Ordering(sequence, transfer, snapshotNewer, resend));
        foreach (var sequence in new ushort[] { 22, 1 }) Test("reliable resend order preserves snapshots and events across sequence " + sequence, () => {
            ServerUpdatePacket StatePacket(ushort seq, byte state, bool active) {
                var result = new ServerUpdatePacket { Sequence = seq };
                var update = new ReliableEntityUpdate { Id = 17, IsActive = active };
                update.UpdateTypes.UnionWith(new[] { EntityUpdateType.Active, EntityUpdateType.HostFsm });
                var fsm = new EntityHostFsmData { CurrentState = state }; fsm.Types.Add(EntityHostFsmData.Type.State);
                update.HostFsmData[0] = fsm;
                var events = new EntityNetworkData { Type = EntityComponentType.Fsm }; events.Packet.Write(state);
                update.UpdateTypes.Add(EntityUpdateType.Data); update.GenericData.Add(events);
                var batch = new PacketDataCollection<ReliableEntityUpdate>(); batch.DataInstances.Add(update);
                result.SetSendingPacketData(ServerUpdatePacketId.ReliableEntityUpdate, batch);
                return result;
            }
            var oldest = StatePacket(unchecked((ushort)(sequence - 2)), 1, false);
            var older = StatePacket(unchecked((ushort)(sequence - 1)), 2, false);
            var outgoing = StatePacket(sequence, 3, true);
            // Queue the resends in reverse order, as the concurrent loss queue may do.
            outgoing.SetLostReliableData(older); outgoing.SetLostReliableData(oldest);
            var bytes = new Packet(); outgoing.CreatePacket(bytes);
            var incoming = new ServerUpdatePacket();
            Check(incoming.ReadPacket(PacketManager.ReadDatagram(bytes.ToArray())), "resend datagram did not decode");
            var applied = new List<byte>(); var active = false; byte state = 0;
            var manager = new PacketManager();
            manager.RegisterServerUpdatePacketHandler<ReliableEntityUpdate>(ServerUpdatePacketId.ReliableEntityUpdate,
                (sender, update) => { active = update.IsActive; state = update.HostFsmData[0].CurrentState;
                    applied.Add(update.GenericData[0].Packet.ReadByte()); });
            manager.HandleServerUpdatePacket(2, incoming);
            Check(state == 3 && active, "old resend rolled back the newest activation/FSM snapshot");
            Check(applied.SequenceEqual(new byte[] { 1, 2, 3 }), "reliable action events were lost, duplicated, or reordered");
            Check(outgoing.GetPacketData()[ServerUpdatePacketId.ReliableEntityUpdate] is RawPacketDataCollection merged && merged.DataInstances.Count == 3);
            Check(outgoing.TryGetSendingPacketData(ServerUpdatePacketId.ReliableEntityUpdate, out var raw) &&
                ((RawPacketDataCollection)raw).DataInstances.Count == 1, "reading merged data mutated the original send collection");
            bytes = new Packet(); outgoing.CreatePacket(bytes); incoming = new ServerUpdatePacket();
            Check(incoming.ReadPacket(PacketManager.ReadDatagram(bytes.ToArray())));
            Check(((RawPacketDataCollection)incoming.GetPacketData()[ServerUpdatePacketId.ReliableEntityUpdate]).DataInstances.Count == 3,
                "serializing after reading the cache duplicated reliable events");
        });
        Test("addon resends preserve chronological collections and newest scalar without mutating sends", () => {
            const byte addonId = 243;
            BasePacket<ServerUpdatePacketId>.AddonPacketInfoDict[addonId] = new AddonPacketInfo(id =>
                id == 0 ? (IPacketData)new PacketDataCollection<Message>() : new ChatMessage(), 2);
            try {
                ServerUpdatePacket AddonPacket(ushort sequence, uint serial) {
                    var packet = new ServerUpdatePacket { Sequence = sequence }; var addon = new AddonPacketData(2);
                    var events = new PacketDataCollection<Message>(); events.DataInstances.Add(new Message { Kind = Kind.Down, Serial = serial });
                    addon.PacketData[0] = events; addon.PacketData[1] = new ChatMessage { Message = serial.ToString() };
                    packet.SetSendingAddonPacketData(addonId, addon); return packet;
                }
                var outgoing = AddonPacket(1, 3); outgoing.SetLostReliableData(AddonPacket(0, 2));
                outgoing.SetLostReliableData(AddonPacket(ushort.MaxValue, 1));
                var bytes = new Packet(); outgoing.CreatePacket(bytes); var incoming = new ServerUpdatePacket();
                Check(incoming.ReadPacket(PacketManager.ReadDatagram(bytes.ToArray())));
                var merged = incoming.GetAddonPacketData()[addonId].PacketData;
                Check(((RawPacketDataCollection)merged[0]).DataInstances.Cast<Message>().Select(m => m.Serial).SequenceEqual(new uint[] { 1, 2, 3 }));
                Check(((ChatMessage)merged[1]).Message == "3", "old addon scalar replaced current value");
                outgoing.GetAddonPacketData();
                Check(outgoing.TryGetSendingAddonPacketData(addonId, out var original) && ((RawPacketDataCollection)original.PacketData[0]).DataInstances.Count == 1);
            } finally { BasePacket<ServerUpdatePacketId>.AddonPacketInfoDict.Remove(addonId); }
        });
        Test("final entity snapshot is consumed before same-datagram disconnect", () => {
            var manager = new PacketManager(); var connected = true; var applied = false;
            manager.RegisterServerUpdatePacketHandler(ServerUpdatePacketId.PlayerDisconnect, (ushort _) => connected = false);
            manager.RegisterServerUpdatePacketHandler<ReliableEntityUpdate>(ServerUpdatePacketId.ReliableEntityUpdate,
                (sender, update) => { if (connected && update.Id == 17) applied = true; });
            var outgoing = new ServerUpdatePacket();
            outgoing.SetSendingPacketData(ServerUpdatePacketId.PlayerDisconnect, new EmptyData());
            var updates = new PacketDataCollection<ReliableEntityUpdate>();
            var update = new ReliableEntityUpdate { Id = 17 };
            var state = new EntityHostFsmData { CurrentState = 3 }; state.Types.Add(EntityHostFsmData.Type.State);
            update.UpdateTypes.Add(EntityUpdateType.HostFsm); update.HostFsmData[0] = state;
            updates.DataInstances.Add(update);
            outgoing.SetSendingPacketData(ServerUpdatePacketId.ReliableEntityUpdate, updates);
            var bytes = new Packet(); outgoing.CreatePacket(bytes);
            var incoming = new ServerUpdatePacket();
            Check(incoming.ReadPacket(PacketManager.ReadDatagram(bytes.ToArray())), "final datagram did not decode");
            manager.HandleServerUpdatePacket(2, incoming);
            Check(applied && !connected, "disconnect removed authority before its final entity state");
        });
        Test("replica cancellation remains final after a destroyed-target exit failure", () => {
            var life=new Hkmp.Game.Client.Entity.Action.ActionLifetime();var stops=0;var exits=0;
            try { life.Finish(()=>stops++,()=>{exits++;throw new InvalidOperationException("target destroyed");}); }
            catch(InvalidOperationException) { }
            life.Finish(()=>stops++,()=>exits++);Check(stops==1&&exits==1);
        });
        Test("replica exit still detaches observers when coroutine cancellation fails", () => {
            var life=new Hkmp.Game.Client.Entity.Action.ActionLifetime();var exits=0;
            try { life.Finish(()=>throw new InvalidOperationException("coroutine disposed"),()=>exits++); }
            catch(InvalidOperationException) { }
            life.Finish(null,()=>exits++);Check(exits==1);
        });
        Test("reentrant replica cleanup cannot run the same exit twice", () => {
            var life=new Hkmp.Game.Client.Entity.Action.ActionLifetime();var exits=0;
            life.Finish(()=>life.Finish(null,()=>exits+=100),()=>{exits++;life.Finish(null,()=>exits+=100);});
            Check(exits==1);
        });
        Test("reliable revive identifies the accepted rescuer independently of victim", () => {
            var m = Roundtrip(new Message { Kind = Kind.Revived, Id = 23, Target = 17, Serial = 4, Aux = 8, Round = 2, Session = "party", Scene = "Boss", X = 9, Y = 6 });
            Check(m.Id == 23 && m.Target == 17 && m.Serial == 4 && m.Aux == 8 && m.Round == 2 && m.IsReliable && !m.DropReliableDataIfNewerExists);
        });
        Test("personal checkpoint rejects invalid types and permits native implicit destinations", () => {
            Check(Party.ValidCheckpoint("", "", 2)); Check(!Party.ValidCheckpoint("Town", "Bench", -1));
            Check(!Party.ValidCheckpoint("Town", "Bench", 3)); Check(!Party.ValidCheckpoint(null, "", 1));
            var p = Returnable(); p.Members[2].CheckpointScene = ""; p.Members[2].CheckpointMarker = "";
            p.Members[2].CheckpointRespawnType = 2; Check(p.GiveUp(2, 0, 1));
            Check(!p.FinishReturn(2, 0, 1, 3, "", 0, 0, 100));
            Check(p.FinishReturn(2, 0, 1, 3, "NativeReturn", 0, 0, 100)); Check(p.Round == 0);
        });
        Test("entry protection waits for actual readiness and a reaction interval", () => {
            var window = new EntryProtectionWindow(); window.Begin();
            Check(window.Update(false, 0)); Check(window.Update(false, 100));
            Check(window.Update(true, 100)); Check(window.Update(true, 101.49));
            Check(!window.Update(true, 101.5));
        });
        Test("interrupted arrival restarts grace but combat input loss cannot rearm it", () => {
            var window = new EntryProtectionWindow(); window.Begin();
            Check(window.Update(true, 1)); Check(window.Update(false, 2));
            Check(window.Update(true, 30)); Check(window.Update(true, 31));
            Check(!window.Update(true, 31.5)); Check(!window.Update(false, 50));
            window.Begin(); Check(window.Update(false, 60)); window.Clear(); Check(!window.Update(false, 61));
        });
        Test("wipe freezes a separate respawn destination and type for every member", () => {
            var p = Two(); p.Members[2].CheckpointScene = "DreamReturn"; p.Members[2].CheckpointMarker = "door_dreamReturn";
            p.Members[2].CheckpointRespawnType = 2; Down(p, 1, 0, 1); Down(p, 2, 0, 1); Check(p.BeginWipe());
            Check(p.Members[1].ReturnScene == "Town" && p.Members[2].ReturnScene == "DreamReturn");
            Check(p.Members[2].ReturnRespawnType == 2); p.Members[2].CheckpointScene = "Changed";
            Check(!p.Ready(2, 1, "Town")); Check(!p.Ready(2, 1, "Changed")); Check(p.Ready(2, 1, "DreamReturn"));
            Check(!p.TryRelease()); Check(p.Ready(1, 1, "Town")); Check(p.TryRelease());
        });
        Test("native implicit returns need no invented common bench or marker", () => {
            var p = Two(); p.Members[1].CheckpointScene = ""; p.Members[1].CheckpointMarker = "";
            p.Members[2].CheckpointScene = ""; Down(p, 1, 0, 1); Down(p, 2, 0, 1); Check(p.BeginWipe());
            Check(!p.Ready(1, 0, "Town")); Check(!p.Ready(1, 1, "")); Check(p.Ready(1, 1, "NativeReturnA"));
            Check(p.Ready(2, 1, "NativeReturnB")); Check(p.TryRelease());
        });
        Test("personal dream return accepts the native gate and does not reset survivors", () => {
            var p = Returnable(); p.Members[2].CheckpointScene = "DreamReturn"; p.Members[2].CheckpointMarker = "";
            p.Members[2].CheckpointRespawnType = 2; Check(p.GiveUp(2, 0, 1));
            Check(p.Members[2].ReturnRespawnType == 2 && p.Round == 0 && p.Members[1].Life == Life.Alive);
        });
        Test("avatar room and both participants' visits survive all scene packets", () => {
            var enter=Roundtrip(new ClientPlayerEnterScene{Id=7,Username="probe",Position=Hkmp.Math.Vector2.Zero,SceneName="GG_Hornet_1",Visit=11,Epoch=3,PlayerVisit=13});
            Check(enter.SceneName=="GG_Hornet_1"&&enter.Visit==11&&enter.Epoch==3&&enter.PlayerVisit==13);
            var leave=Roundtrip(new ClientPlayerLeaveScene{Id=7,SceneName="GG_Hornet_1",Visit=11,Epoch=3,PlayerVisit=13});
            Check(leave.SceneName==enter.SceneName&&leave.Visit==11&&leave.Epoch==3&&leave.PlayerVisit==13);
            var update=Roundtrip(new PlayerUpdate{Id=7,SceneName="GG_Hornet_1",Visit=11,Epoch=3,PlayerVisit=13});
            Check(update.SceneName==enter.SceneName&&update.Visit==11&&update.Epoch==3&&update.PlayerVisit==13);
        });
        Test("late teammate leave and duplicate enter cannot remove a newer avatar", () => {
            var presence=new Hkmp.Game.Client.PeerScenePresence();
            Check(presence.Enter(10)&&!presence.Enter(10));Check(presence.Enter(12));
            Check(!presence.Leave(10)&&presence.Present&&presence.Visit==12);
            Check(presence.Leave(12)&&!presence.Present&&!presence.Enter(12));
            Check(!presence.Leave(14)&&!presence.Enter(13)&&!presence.Enter(14));Check(presence.Enter(15));
            presence.Reset();Check(presence.Enter(15));
        });
        Test("queued snapshots freeze generic data, FSM dictionaries and scale", () => {
            var state=new EntityHostFsmData{CurrentState=2};state.Types.Add(EntityHostFsmData.Type.Ints);state.Ints[1]=8;
            var frozen=SnapshotPayload.Copy(state);state.Ints[1]=99;state.Ints[2]=7;Check(frozen.Ints.Count==1&&frozen.Ints[1]==8);
            var data=new EntityNetworkData{Type=EntityComponentType.CombatState};data.Packet.Write(8);var copy=SnapshotPayload.Copy(data);data.Packet.Write(99);
            Check(copy.Packet.Length==4&&copy.Packet.ReadInt()==8);
            var scale=new EntityUpdate.ScaleData{origin=true,x=true,y=true,z=true,xScale=2,yScale=3,zScale=1};var cloned=SnapshotPayload.Copy(scale);
            scale.xScale=0;Near(cloned.xScale,2);Near(cloned.yScale,3);
        });
        Test("pre-entry movement keeps newest peer visit and independent changed fields", () => {
            var pending=new Hkmp.Game.Client.PendingPeerState();
            var first=new PlayerUpdate{Id=7,PlayerVisit=4,Position=new Hkmp.Math.Vector2(8,9)};first.UpdateTypes.Add(PlayerUpdateType.Position);pending.Add(first);
            var scale=new PlayerUpdate{Id=7,PlayerVisit=4,Scale=true};scale.UpdateTypes.Add(PlayerUpdateType.Scale);pending.Add(scale);
            var state=pending.Take(7,4);Check(state.UpdateTypes.Contains(PlayerUpdateType.Position)&&state.UpdateTypes.Contains(PlayerUpdateType.Scale)&&state.Scale);Near(state.Position.X,8);
            Check(pending.Take(7,4)==null);
            first.PlayerVisit=6;pending.Add(first);scale.PlayerVisit=5;pending.Add(scale);pending.Drop(7,5);
            Check(pending.Take(7,5)==null);Check(pending.Take(7,6)!=null);
            pending.Add(first);pending.Clear();Check(pending.Take(7,6)==null);
        });
        Test("pre-entry animation retains visual frame without replaying attack effects", () => {
            var pending=new Hkmp.Game.Client.PendingPeerState();var update=new PlayerUpdate{Id=7,PlayerVisit=3};
            update.UpdateTypes.Add(PlayerUpdateType.Animation);
            update.AnimationInfos.Add(new AnimationInfo{ClipId=2,Frame=3,EffectInfo=new[]{true}});
            update.AnimationInfos.Add(new AnimationInfo{ClipId=5,Frame=7,EffectInfo=new[]{true}});pending.Add(update);
            var state=pending.Take(7,3);Check(state.AnimationInfos.Count==1&&state.AnimationInfos[0].ClipId==5&&state.AnimationInfos[0].Frame==7&&state.AnimationInfos[0].EffectInfo==null);
        });
        Test("wide entity IDs survive position, reliable state and spawn serialization", () => {
            foreach (var id in new[] { 65536UL, 0x123456789abcdef0UL, ulong.MaxValue }) {
                Check(Roundtrip(new EntityUpdate { Id = id }).Id == id);
                Check(Roundtrip(new ReliableEntityUpdate { Id = id }).Id == id);
                Check(Roundtrip(new EntitySpawn { Id = id, SourceId = id - 1 }).SourceId == id - 1);
            }
        });
        Test("dense boss action batches retain more than 255 ordered updates and following fields", () => {
            foreach(var count in new[]{255,256,512,4096}){
                var update=new ReliableEntityUpdate{Id=17,IsActive=true};
                update.UpdateTypes.Add(EntityUpdateType.Data);update.UpdateTypes.Add(EntityUpdateType.Active);update.UpdateTypes.Add(EntityUpdateType.HostFsm);
                var state=new EntityHostFsmData{CurrentState=19};state.Types.Add(EntityHostFsmData.Type.State);update.HostFsmData[0]=state;
                for(var i=0;i<count;i++){var item=new EntityNetworkData{Type=EntityComponentType.Fsm};item.Packet.Write(i);update.GenericData.Add(item);}
                var got=Roundtrip(update);Check(got.IsActive&&got.HostFsmData[0].CurrentState==19&&got.GenericData.Count==count,"action batch truncated");
                for(var i=0;i<count;i++)Check(got.GenericData[i].Packet.ReadInt()==i,"action order changed");
            }
        });
        Test("oversized entity action batches and component payloads reject partial serialization", () => {
            var update=new ReliableEntityUpdate();update.UpdateTypes.Add(EntityUpdateType.Data);
            var item=new EntityNetworkData();for(var i=0;i<=ushort.MaxValue;i++)update.GenericData.Add(item);
            var rejected=false;try{Roundtrip(update);}catch(InvalidOperationException){rejected=true;}Check(rejected,"oversized action batch accepted");
            item.Packet.Write(new byte[ushort.MaxValue+1]);rejected=false;
            try{item.WriteData(new Packet());}catch(InvalidOperationException){rejected=true;}Check(rejected,"oversized component truncated");
        });
        Test("same-type spawn sources retain exact owner, action, prefab and origin", () => {
            var spawn = Roundtrip(new EntitySpawn { Id = 0xf000000000000001UL, SourceId = 0xa000000000000002UL,
                SpawningType = EntityType.WhiteDefender, SpawnedType = EntityType.LargeDungBall,
                Fsm = "Dung Defender", FsmPath="Attack#0/Same%2Fname#1", State = "Throw 1", ActionIndex = 17, Prefab = "Dung Ball Large", X = 8.25f, Y = -3, Z = .01f,Active=false });
            Check(spawn.SourceId == 0xa000000000000002UL && spawn.ActionIndex == 17 && spawn.Fsm == "Dung Defender" && spawn.FsmPath=="Attack#0/Same%2Fname#1" && spawn.State == "Throw 1" && spawn.Prefab == "Dung Ball Large");
            Near(spawn.X, 8.25f); Near(spawn.Y, -3); Near(spawn.Z, .01f);
            Check(!spawn.Active);
        });
        Test("malformed spawn descriptors cannot enter a deferred queue", () => {
            foreach (var spawn in new[] { new EntitySpawn { X = float.NaN }, new EntitySpawn { Y = float.PositiveInfinity },
                new EntitySpawn { ActionIndex = -2 }, new EntitySpawn { Prefab = new string('x', 257) }, new EntitySpawn{FsmPath=new string('x',2049)} }) {
                var rejected = false; try { Roundtrip(spawn); } catch (FormatException) { rejected = true; } Check(rejected);
            }
        });
        Test("static identity is stable when unrelated entities are omitted or reordered", () => {
            var keys = Enumerable.Range(0, 2000).Select(i => "GG_Test/root/enemy#" + i + ":Control").ToArray();
            var table = keys.ToDictionary(k => k, EntityIdentity.Hash);
            Check(table.Values.Distinct().Count() == table.Count && table.Values.All(i => i != 0 && (i >> 63) == 0));
            foreach (var key in keys.Where((k, i) => i % 7 != 0).Reverse()) Check(EntityIdentity.Hash(key) == table[key]);
            Check(EntityIdentity.Hash("sceneA/enemy") != EntityIdentity.Hash("sceneB/enemy"));
        });
        Test("pooled spawn lifetimes cannot reuse a static or earlier dynamic identity", () => {
            var ids = Enumerable.Range(0, 10000).Select(_ => EntityIdentity.Dynamic()).ToArray();
            Check(ids.All(i => (i >> 63) == 1) && ids.Distinct().Count() == ids.Length);
        });
        Test("server entity key equality retains high bits and scene scope", () => {
            Check(!new ServerEntityKey("room", 1).Equals(new ServerEntityKey("room", 0x100000001)));
            Check(!new ServerEntityKey("room", ulong.MaxValue).Equals(new ServerEntityKey("other", ulong.MaxValue)));
        });
        Test("only a newer explicit health reset opens a death tombstone", () => {
            var state = new ServerEntityData(); Check(state.AcceptHealth(EntityComponentType.Death, 10));
            Check(!state.AcceptHealth(EntityComponentType.HealthReset, 9)); Check(!state.AcceptHealth(EntityComponentType.Health, 11));
            Check(state.AcceptHealth(EntityComponentType.HealthReset, 12)); Check(!state.AcceptHealth(EntityComponentType.Death, 11));
            Check(state.AcceptHealth(EntityComponentType.Health, 13)); Check(state.AcceptHealth(EntityComponentType.Death, 14));
        });
        Test("object-reference snapshots preserve clearing, state and the eighth flag bit", () => {
            var refs = new EntityHostFsmData(); refs.Types.Add(EntityHostFsmData.Type.Objects); refs.Types.Add(EntityHostFsmData.Type.State);
            refs.CurrentState = 15; refs.Objects[0] = "e:18446744073709551615"; refs.Objects[7] = ""; refs.Objects[2] = "@local";
            var update = new ReliableEntityUpdate { Id = ulong.MaxValue }; update.UpdateTypes.Add(EntityUpdateType.HostFsm); update.HostFsmData[2] = refs;
            var got = Roundtrip(update).HostFsmData[2]; Check(got.CurrentState == 15 && got.Objects[7] == "" && got.Objects[0] == refs.Objects[0] && got.Objects[2] == "@local");
            var merged = new EntityHostFsmData(); merged.MergeData(got); refs.Objects[0] = ""; merged.MergeData(refs); Check(merged.Objects[0] == "");
        });
        Test("retired projectile snapshots keep only dependencies needed by a live child", () => {
            var data = new Dictionary<ServerEntityKey, ServerEntityData>();
            for (ulong i = 1; i <= 2000; i++) data[new ServerEntityKey("boss", i)] = new ServerEntityData { Retired = true };
            data[new ServerEntityKey("boss", 3000)] = new ServerEntityData { Spawned = true, Spawn = new EntitySpawn { SourceId = 5 } };
            data[new ServerEntityKey("boss", 5)].Spawned = true; data[new ServerEntityKey("boss", 5)].Spawn = new EntitySpawn { SourceId = 6 };
            data[new ServerEntityKey("other", 4000)] = new ServerEntityData();
            var ids = EntitySnapshotGraph.Select(data, "boss").Select(p => p.Key.EntityId).OrderBy(i => i).ToArray();
            Check(ids.SequenceEqual(new[] { 5UL, 6UL, 3000UL }));
        });
        Test("spawn dependency cycles are bounded during late join", () => {
            var data = new Dictionary<ServerEntityKey, ServerEntityData> {
                [new ServerEntityKey("boss", 1)] = new ServerEntityData { Spawned = true, Spawn = new EntitySpawn { SourceId = 2 } },
                [new ServerEntityKey("boss", 2)] = new ServerEntityData { Retired = true, Spawned = true, Spawn = new EntitySpawn { SourceId = 1 } }
            };
            Check(EntitySnapshotGraph.Select(data, "boss").Count() == 2);
        });
        Test("Hall of Gods tier, return gate and authoritative itinerary survive addon messages", () => {
            var m = Roundtrip(new Message { Kind = Kind.Warp, BossLevel = 2, BossStatueKey = "statueStateGrimm", BossReturnGate = "door_dreamReturn",
                BossPlan = "[{\"Name\":\"Spa Boss Scene\",\"Scene\":\"GG_Spa\",\"Load\":true}]" });
            Check(m.BossLevel == 2 && m.BossReturnGate == "door_dreamReturn" && m.BossStatueKey == "statueStateGrimm" && m.BossPlan.Contains("GG_Spa"));
        });
        Test("invalid challenge tier and oversized itinerary are rejected", () => {
            foreach (var m in new[] { new Message { BossLevel = 3 }, new Message { BossLevel = -2 }, new Message { BossPlan = new string('x', 24577) }, new Message { BossReturnGate = new string('x', 129) } }) {
                var rejected = false; try { Roundtrip(m); } catch (FormatException) { rejected = true; } Check(rejected);
            }
        });
        Test("new game's early hero event waits for matching scene preparation", () => {
            var gate = new Hkmp.Game.Client.SceneEntryGate();
            Check(!gate.Prepare(10)); // Opening_Sequence
            Check(!gate.Request(11)); // Tutorial hero starts before activeSceneChanged
            Check(gate.Prepare(11));
            Check(gate.Request(11));
        });
        Test("ordinary transitions publish after preparation and distinguish reentry", () => {
            var gate = new Hkmp.Game.Client.SceneEntryGate();
            Check(!gate.Prepare(10)); Check(gate.Request(10));
            Check(!gate.Prepare(11)); Check(gate.Request(11));
            Check(!gate.Request(12)); Check(gate.Prepare(12)); // same room, new Unity scene handle
        });
        Test("old scene requests cannot publish into a different scene", () => {
            var gate = new Hkmp.Game.Client.SceneEntryGate();
            Check(!gate.Request(10)); Check(!gate.Prepare(11)); Check(!gate.Prepare(10));
        });
        Test("disconnect discards pending and prepared scene entry", () => {
            var gate = new Hkmp.Game.Client.SceneEntryGate();
            gate.Prepare(10); gate.Request(11); gate.Reset();
            Check(!gate.Prepare(11)); gate.Reset(); Check(!gate.Request(10));
        });
        Test("Mantis Claw is idempotent", () => {
            var l = new ProgressLedger(); Check(l.Merge("hasWalljump", 1)); Check(!l.Merge("hasWalljump", 1)); Check(l.Get("hasWalljump") == 1);
        });
        Test("abilities never regress", () => { var l = new ProgressLedger(); l.Merge("hasDash", 1); Check(!l.Merge("hasDash", 0)); Check(l.Get("hasDash") == 1); });
        Test("spells take maximum, not sum", () => { var l = new ProgressLedger(); l.Merge("fireballLevel", 2); Check(!l.Merge("fireballLevel", 1)); Check(l.Get("fireballLevel") == 2); });
        Test("invalid progress is rejected", () => { var l = new ProgressLedger(); Check(!l.Merge("hasWalljump", -1)); Check(!l.Merge("fireballLevel", 99)); Check(!l.Merge(null, 1)); });
        Test("maps, currency, health and equipment are private", () => {
            var l = new ProgressLedger(); foreach (var key in new[] { "mapCrossroads", "hasMap", "health", "geo", "simpleKeys", "equippedCharm_2", "respawnScene" }) Check(!l.Merge(key, 1), key);
        });
        Test("all progression keys exist in this game build", () => {
            foreach (var pair in ProgressCatalog.Limits) {
                var field = typeof(PlayerData).GetField(pair.Key);
                Check(field != null, "missing " + pair.Key);
                Check(field.FieldType == (pair.Value == 1 ? typeof(bool) : typeof(int)), "wrong type " + pair.Key);
            }
        });
        Test("progress union survives duplicates and random ordering", () => {
            var random = new Random(42);
            for (var i = 0; i < 1000; i++) {
                var l = new ProgressLedger(); foreach (var value in new[] { 0, 1, 2, 2, 1 }.OrderBy(_ => random.Next())) l.Merge("screamLevel", value);
                Check(l.Get("screamLevel") == 2);
            }
        });
        Test("late heartbeat cannot revive downed guest", () => {
            var p = Two(); Down(p, 2, 0, 1); p.Presence(2, 0, 2, "Boss", 2, 0, true, true, 1, 2, 101); Check(p.Members[2].Life == Life.Down);
        });
        Test("guest down then host down creates one wipe", () => {
            var p = Two(); Down(p, 2, 0, 1); Check(!p.BeginWipe()); Down(p, 1, 0, 1);
            Check(p.BeginWipe()); Check(p.Members[1].ReturnScene == "Town" && p.Members[1].ReturnMarker == "Bench" && p.Round == 1); Check(!p.BeginWipe());
        });
        Test("host down then guest down creates one wipe", () => {
            var p = Two(); Down(p, 1, 0, 1); Down(p, 2, 0, 1); Check(p.BeginWipe());
        });
        Test("new attempt rejects stale down and presence", () => {
            var p = Two(); Down(p, 1, 0, 1); Down(p, 2, 0, 1); p.BeginWipe();
            Check(!Down(p, 1, 0, 9)); Check(!p.Presence(1, 0, 99, "Boss", 0, 0, true, true, 0, 0, 103));
        });
        Test("retry barrier waits for everyone", () => {
            var p = Two(); Down(p, 1, 0, 1); Down(p, 2, 0, 1); p.BeginWipe();
            Ready(p, 1, 1); Check(!p.TryRelease()); Ready(p, 2, 1); Check(p.TryRelease()); Check(!p.TryRelease());
        });
        Test("disconnect releases a pending retry barrier", () => {
            var p = Two(); Down(p, 1, 0, 1); Down(p, 2, 0, 1); p.BeginWipe(); Ready(p, 1, 1); p.Leave(2); Check(p.TryRelease());
        });
        Test("late join participates in retry barrier", () => {
            var p = Two(); Down(p, 1, 0, 1); Down(p, 2, 0, 1); p.BeginWipe(); p.Join(3, "late");
            Ready(p, 1, 1); Ready(p, 2, 1); Check(!p.TryRelease()); Ready(p, 3, 1); Check(p.TryRelease());
        });
        Test("personal retry destination is stable when another player disconnects", () => {
            var p = Two(); p.Members[2].CheckpointScene = "Other"; p.Members[2].CheckpointMarker = "DifferentBench";
            Down(p, 1, 0, 1); Down(p, 2, 0, 1); p.BeginWipe(); p.Leave(1); p.Join(3, "late");
            Check(p.Members[2].ReturnScene == "Other" && p.Members[2].ReturnMarker == "DifferentBench");
        });
        Test("loading player prevents false team wipe", () => {
            var p = Two(); p.Join(3, "loading"); Down(p, 1, 0, 1); Down(p, 2, 0, 1); Check(!p.BeginWipe());
        });
        Test("rescue validates distance and death episode", () => {
            var p = Two(); Down(p, 2, 0, 1); Check(p.CanRescue(1, 2, 0, 1, 101)); Check(!p.CanRescue(1, 2, 0, 0, 101));
            p.Members[2].X = 50; Check(!p.CanRescue(1, 2, 0, 1, 101));
        });
        Test("rescue validates scene, life and freshness", () => {
            var p = Two(); Down(p, 2, 0, 1); Check(!p.CanRescue(1, 2, 0, 1, 103));
            p.Members[2].Scene = "Other"; Check(!p.CanRescue(1, 2, 0, 1, 101));
            p.Members[2].Scene = "Boss"; Down(p, 1, 0, 1); Check(!p.CanRescue(1, 2, 0, 1, 101));
        });
        Test("rescue cannot happen twice", () => { var p = Two(); Down(p, 2, 0, 1); Check(p.Revive(1, 2, 0, 1, 101)); Check(!p.Revive(1, 2, 0, 1, 101)); });
        Test("old down event cannot undo successful rescue", () => {
            var p = Two(); Down(p, 2, 0, 1); p.Revive(1, 2, 0, 1, 101); Check(!Down(p, 2, 0, 1)); Check(p.Members[2].Life == Life.Alive); Check(p.Members[2].LifeVersion == 2);
        });
        Test("reliable death anchor overrides last heartbeat and cannot drift", () => {
            var p = Two(); Check(p.Down(2, 0, 1, "Boss", 3, 1, 101));
            Check(p.Presence(2, 0, 2, "EarlierRoom", 60, 90, true, true, 1, 2, 101.1));
            Check(p.Members[2].Scene == "Boss"); Near(p.Members[2].X, 3); Near(p.Members[2].Y, 1);
            Check(p.Revive(1, 2, 0, 1, 101.1));
            p.Presence(2, 0, 3, "Boss", 4, 2, true, true, 1, 2, 101.2); Near(p.Members[2].X, 4);
        });
        Test("second death event cannot move an existing shade", () => {
            var p = Two(); Down(p, 2, 0, 1);
            Check(!p.Down(2, 0, 2, "Other", 20, 30, 101)); Near(p.Members[2].X, 2);
        });
        Test("shade hit cannot revive another death episode", () => {
            var p = Two(); Down(p, 2, 0, 1); Check(p.Revive(1, 2, 0, 1, 100));
            Down(p, 2, 0, 2); Check(!p.Revive(1, 2, 0, 1, 100)); Check(p.Members[2].Life == Life.Down);
            Check(p.Revive(1, 2, 0, 2, 100));
        });
        Test("self hits and reset-round hits never revive", () => {
            var p = Two(); Down(p, 2, 0, 1); Check(!p.Revive(2, 2, 0, 1, 100));
            Down(p, 1, 0, 1); p.BeginWipe();
            Check(!p.Revive(1, 2, 0, 1, 100)); Check(!p.Revive(1, 2, 1, 1, 100));
        });
        Test("death anchor rejects nonfinite positions and invalid scene", () => {
            var p = Two(); Check(!p.Down(2, 0, 1, "Boss", float.NaN, 0, 100));
            Check(!p.Down(2, 0, 1, "", 0, 0, 100)); Check(p.Members[2].Life == Life.Alive);
        });
        Test("give up uses personal bench without resetting the surviving teammate", () => {
            var p = Returnable(); Check(p.GiveUp(2, 0, 1));
            Check(p.Members[2].ReturnScene == "GuestBenchRoom" && p.Members[2].ReturnMarker == "GuestBench" && p.Members[2].ReturnRespawnType == 1);
            Check(p.Members[1].Life == Life.Alive && p.Members[1].Scene == "Boss" && p.Round == 0 && !p.Resetting);
            Check(!p.BeginWipe());
        });
        Test("alive players and missing checkpoints cannot give up", () => {
            var p = Two(); Check(!p.GiveUp(1, 0, 0)); Down(p, 2, 0, 1); Check(!p.GiveUp(2, 0, 1));
            p.Members[2].CheckpointScene = new string('x', 129); Check(!p.GiveUp(2, 0, 1)); Check(p.Members[2].Life == Life.Down);
        });
        Test("duplicate give up and new down events cannot interrupt returning", () => {
            var p = Returnable(); Check(p.GiveUp(2, 0, 1)); Check(!p.GiveUp(2, 0, 1)); Check(!Down(p, 2, 0, 2));
            p.Presence(2, 0, 2, "Boss", 2, 0, true, true, 1, 2, 101);
            Check(p.Members[2].Life == Life.Returning && !p.Members[2].HasMap);
        });
        Test("rescue and give up accept only the first successful action", () => {
            var p = Returnable(); Check(p.Revive(1, 2, 0, 1, 100)); Check(!p.GiveUp(2, 0, 1));
            p = Returnable(); Check(p.GiveUp(2, 0, 1)); Check(!p.Revive(1, 2, 0, 1, 100));
        });
        Test("old death and round cannot request or finish a bench return", () => {
            var p = Returnable(); Check(!p.GiveUp(2, 0, 0)); Check(!p.GiveUp(2, 1, 1));
            Check(p.GiveUp(2, 0, 1)); Check(!p.FinishReturn(2, 1, 1, 9, "GuestBenchRoom", 10, 20, 101));
            Check(!p.FinishReturn(2, 0, 0, 9, "GuestBenchRoom", 10, 20, 101));
        });
        Test("bench destination stays frozen during return", () => {
            var p = Returnable(); Check(p.GiveUp(2, 0, 1));
            p.Members[2].CheckpointScene = "Other"; p.Members[2].CheckpointMarker = "OtherBench";
            Check(p.Members[2].ReturnScene == "GuestBenchRoom" && p.Members[2].ReturnMarker == "GuestBench");
        });
        Test("bench arrival requires the authorized scene and finite position", () => {
            var p = Returnable(); p.GiveUp(2, 0, 1);
            Check(!p.FinishReturn(2, 0, 1, 9, "Boss", 10, 20, 101));
            Check(!p.FinishReturn(2, 0, 1, 9, "GuestBenchRoom", float.NaN, 20, 101));
            Check(p.FinishReturn(2, 0, 1, 9, "GuestBenchRoom", 10, 20, 101));
            Check(p.Members[2].Life == Life.Alive && p.Members[2].LifeVersion == 3);
            Check(!p.FinishReturn(2, 0, 1, 9, "GuestBenchRoom", 10, 20, 101));
        });
        Test("late old-room heartbeat cannot overwrite a completed bench return", () => {
            var p = Returnable(); p.GiveUp(2, 0, 1); p.FinishReturn(2, 0, 1, 9, "GuestBenchRoom", 10, 20, 101);
            Check(!p.Presence(2, 0, 8, "Boss", 2, 0, true, true, 1, 2, 101.1));
            Check(p.Members[2].Scene == "GuestBenchRoom"); Near(p.Members[2].X, 10);
        });
        Test("later bench heartbeat arriving before acknowledgement does not block arrival", () => {
            var p = Returnable(); p.GiveUp(2, 0, 1);
            p.Presence(2, 0, 10, "GuestBenchRoom", 10, 20, false, false, 0, 0, 101);
            Check(p.FinishReturn(2, 0, 1, 9, "GuestBenchRoom", 10, 20, 101.1)); Check(p.Members[2].Sequence == 10);
        });
        Test("returning teammate prevents a false full-team failure", () => {
            var p = Returnable(); p.GiveUp(2, 0, 1); Down(p, 1, 0, 1);
            Check(!p.BeginWipe()); p.FinishReturn(2, 0, 1, 9, "GuestBenchRoom", 10, 20, 101);
            Check(!p.BeginWipe()); Check(p.Round == 0);
        });
        Test("give up cannot bypass full-team failure", () => {
            var p = Returnable(); Down(p, 1, 0, 1); Check(!p.GiveUp(2, 0, 1)); Check(p.BeginWipe());
            Check(!p.GiveUp(2, 1, 1)); Check(p.Round == 1);
        });
        Test("out-of-order position is ignored", () => {
            var p = Two(); p.Presence(2, 0, 3, "New", 10, 20, true, true, 3, 4, 101);
            Check(!p.Presence(2, 0, 2, "Old", 0, 0, true, true, 0, 0, 102)); Check(p.Members[2].Scene == "New");
        });
        Test("invalid and unavailable map coordinates never display", () => {
            var p = Two(); p.Presence(2, 0, 2, "Boss", 2, 0, false, true, 0, 0, 101); Check(!p.Members[2].HasMap);
            p.Presence(2, 0, 3, "Boss", 2, 0, true, true, float.NaN, 0, 102); Check(!p.Members[2].HasMap);
        });
        Test("map center and edges use actual room geometry", () => {
            Check(Geometry.TryProject(10, 20, 4, 6, 100, 200, 0, 0, 50, 100, out var x, out var y)); Near(x, 10); Near(y, 20);
            Geometry.TryProject(10, 20, 4, 6, 100, 200, 0, 0, 0, 0, out x, out y); Near(x, 8); Near(y, 17);
        });
        Test("custom map origin offset is respected", () => {
            Geometry.TryProject(0, 0, 4, 4, 100, 100, 10, -10, 40, 60, out var x, out var y); Near(x, 0); Near(y, 0);
        });
        Test("invalid map dimensions do not produce stale positions", () => {
            Check(!Geometry.TryProject(0, 0, 1, 1, 0, 100, 0, 0, 1, 1, out _, out _));
            Check(!Geometry.TryProject(0, 0, 1, 1, 100, 100, 0, 0, float.PositiveInfinity, 1, out _, out _));
        });
        Test("offscreen diagonal arrow stays within viewport", () => {
            Geometry.Edge(100, 200, 80, 60, out var x, out var y); Near(x, 30); Near(y, 60);
            Geometry.Edge(-100, -200, 80, 60, out x, out y); Near(x, -30); Near(y, -60);
        });
        Test("message roundtrip preserves Chinese names and life version", () => {
            var m = Roundtrip(new Message { Kind = Kind.Roster, Name = "队友甲", Scene = "Fungus2_14", Session = "abc", Round = 7, Serial = 19, Aux = 3, X = 3.5f, Y = 4.5f, HasMap = true, Value = 2 });
            Check(m.Name == "队友甲" && m.Round == 7 && m.Aux == 3 && m.HasMap); Near(m.X, 3.5f);
        });
        Test("events require reliable non-dropping delivery", () => {
            foreach (Kind kind in Enum.GetValues(typeof(Kind))) { var m = new Message { Kind = kind }; Check(m.IsReliable == (kind != Kind.Roster && kind != Kind.Presence)); Check(!m.DropReliableDataIfNewerExists); }
        });
        Test("warp roundtrip preserves pantheon runtime context", () => {
            var m = Roundtrip(new Message { Kind = Kind.Warp, Scene = "GG_Galien", BossSequence = "Boss Sequence Tier 3",
                BossIndex = 7, BossDoor = "bossDoorStateTier3", BossBindings = 15 });
            Check(m.BossIndex == 7 && m.BossSequence == "Boss Sequence Tier 3" && m.BossDoor == "bossDoorStateTier3" && m.BossBindings == 15);
        });
        Test("malformed pantheon context is rejected before use", () => {
            foreach (var bad in new[] { new Message { BossIndex = -1 }, new Message { BossIndex = 257 }, new Message { BossBindings = 16 },
                new Message { BossSequence = new string('x', 129) }, new Message { BossDoor = new string('x', 129) } }) {
                var rejected = false; try { Roundtrip(bad); } catch (FormatException) { rejected = true; } Check(rejected);
            }
        });
        Test("native boss completion is reliable and retained in room snapshots", () => {
            var update = new ReliableEntityUpdate { Id = 2, SceneName = "GG_Grimm", Visit = 10, Epoch = 0 };
            update.UpdateTypes.Add(EntityUpdateType.Data);
            var completion = new EntityNetworkData { Type = EntityComponentType.BossSceneComplete }; completion.Packet.Write("{\"Scene\":\"GG_Grimm\",\"BossIndex\":4}");
            update.GenericData.Add(completion);
            var got = Roundtrip(update);
            Check(got.IsReliable && got.GenericData.Single().Type == EntityComponentType.BossSceneComplete && got.GenericData.Single().Packet.ReadString() == "{\"Scene\":\"GG_Grimm\",\"BossIndex\":4}");
        });
        Test("HKMP addon collection decodes all messages using the live receiver factory", () => {
            var batch = new PacketDataCollection<Message>();
            batch.DataInstances.Add(new Message { Kind = Kind.Welcome, Session = "party", Id = 1 });
            batch.DataInstances.Add(new Message { Kind = Kind.ProgressGrant, Key = "hasWalljump", Value = 1 });
            var packet = new Packet(); batch.WriteData(packet);
            var incoming = Wire.CreateCollection(Channel.Event); incoming.ReadData(new Packet(packet.ToArray()));
            var messages = ((PacketDataCollection<Message>)incoming).DataInstances.Cast<Message>().ToArray();
            Check(messages.Length == 2 && messages[0].Kind == Kind.Welcome && messages[1].Key == "hasWalljump");
        });
        Test("malformed protocol message is rejected", () => {
            var rejected = false; try { Roundtrip(new Message { Kind = (Kind)255 }); } catch (FormatException) { rejected = true; } Check(rejected);
        });
        Test("entity position preserves room visit and attempt", () => {
            var value = new EntityUpdate { Id = 42, SceneName = "Boss", Visit = 17, Epoch = 3, Position = new Hkmp.Math.Vector2(12, 34) };
            value.UpdateTypes.Add(EntityUpdateType.Position); var got = Roundtrip(value);
            Check(got.Id == 42 && got.Visit == 17 && got.Epoch == 3 && got.SceneName == "Boss"); Near(got.Position.X, 12);
        });
        Test("entity spawn preserves scene scope", () => {
            var m = Roundtrip(new EntitySpawn { Id = 4, SceneName = "Room", Visit = 8, Epoch = 2 }); Check(m.Visit == 8 && m.Epoch == 2 && m.SceneName == "Room");
        });
        Test("authority transfer carries health and terminal death snapshot", () => {
            var update = new ReliableEntityUpdate { Id = 9, SceneName = "Boss", Visit = 4, Epoch = 1 };
            update.UpdateTypes.Add(EntityUpdateType.Data);
            var death = new EntityNetworkData { Type = EntityComponentType.Death }; death.Packet.Write((uint)3); death.Packet.Write(false); death.Packet.Write((byte)0); death.Packet.Write(true);
            update.GenericData.Add(death);
            var transfer = new HostTransfer { SceneName = "Boss", Snapshot = new ClientPlayerAlreadyInScene { SceneName = "Boss", Visit = 4, Epoch = 1 } };
            transfer.Snapshot.ReliableEntityUpdateList.Add(update);
            var got = Roundtrip(transfer); Check(got.Snapshot.Visit == 4); Check(got.Snapshot.ReliableEntityUpdateList.Single().GenericData.Single().Type == EntityComponentType.Death);
        });
        Test("server health cache rejects stale values", () => {
            var state = new ServerEntityData(); Check(state.AcceptHealth(EntityComponentType.Health, 10)); Check(!state.AcceptHealth(EntityComponentType.Health, 8)); Check(!state.AcceptHealth(EntityComponentType.Health, 10));
        });
        Test("terminal death is an immutable tombstone", () => {
            var state = new ServerEntityData(); Check(state.AcceptHealth(EntityComponentType.Death, 10)); Check(!state.AcceptHealth(EntityComponentType.Health, 11)); Check(!state.AcceptHealth(EntityComponentType.Death, 10));
        });
        Test("server health snapshot converges under random reordering", () => {
            var random = new Random(10);
            for (var i = 0; i < 500; i++) {
                var state = new ServerEntityData(); uint accepted = 0;
                foreach (var n in Enumerable.Range(1, 25).OrderBy(_ => random.Next())) if (state.AcceptHealth(EntityComponentType.Health, (uint)n)) accepted = (uint)n;
                Check(accepted == 25);
            }
        });
        Test("five simultaneous real DTLS clients remain isolated and listener restarts", TransportProbe.Run);
        Test("stopping after a peer closes DTLS permits reopening the same server port", NetworkLifecycleProbe.Run);
        Test("DTLS close retires a peer after its final snapshot without waiting for heartbeat timeout", NetworkLifecycleProbe.ClosedTransport);
        Test("failed bind can be stopped safely and retried on the same port", NetworkLifecycleProbe.FailedBind);
        Test("maximum-size fragmented datagram survives reverse order and duplicates", DatagramProbe.Reorder);
        Test("boss action packets larger than 64 KiB survive framing and live update decoding", DatagramProbe.LargeBossBatch);
        Test("lost fragment does not consume a later heartbeat", DatagramProbe.MissingFragment);
        Test("five simultaneous fragmented peers with equal message IDs remain isolated", DatagramProbe.PeerIsolation);
        Test("fragment expiry and bounded pending messages allow later heartbeats", DatagramProbe.ExpiryAndBounds);
        Test("malformed or truncated datagrams cannot corrupt the following packet", DatagramProbe.RejectMalformed);
        Test("five real DTLS peers keep heartbeats after interleaved reordered and lost fragments", NetworkTrafficProbe.Run);
        Test("stopped chunk worker and queued old chunks cannot leak into a new session", ChunkLifecycleProbe.Run);
        Test("acknowledged chunk flags are reset on reconnect and completion cannot spin", ChunkLifecycleProbe.AcknowledgedReconnect);
        Test("one received datagram commits parent, child and addon updates in the same Unity frame", ClientSessionProbe.CompleteDatagram);
        Test("queued callbacks from a retired client session never execute", ClientSessionProbe.OldCallbacks);
        Test("initial player messages wait for queued game setup and cannot survive disconnect", ClientSessionProbe.EarlyUpdates);
        Test("server rejection fully releases the client DTLS session", ClientSessionProbe.RejectedConnection);
        Test("future chunk fragments cannot contaminate the active chunk", ChunkReceiverProbe.FutureSlice);
        Test("rapid handshake cancellation, rejection and old timeouts cannot affect a new DTLS session", ClientSessionProbe.CancelReconnect);
        Test("room shutdown cancels receiving before waiting for the final outgoing update", ClientSessionProbe.ShutdownReceive);
        Test("active UDP receive failure remains an error and cannot fabricate a received datagram", ClientSessionProbe.ReceiveFailureNeverBecomesData);
        Test("disconnect or reconnect inside a completed chunk callback preserves the new receiver", ChunkReceiverProbe.ResetFromCallback);
        Test("malformed slice bounds and lengths cannot corrupt a following valid chunk", ChunkReceiverProbe.MalformedSlices);
        if (selected != null) foreach (var name in selected) { failed++; Console.WriteLine("FAIL selected test not found: " + name); }
        Console.WriteLine("RESULT " + passed + " passed, " + failed + " failed");
        return failed == 0 ? 0 : 1;
    }
}
