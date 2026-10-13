using System;
using System.Linq;
using Hkmp.Networking;
using Hkmp.Networking.Packet;
using Hkmp.Networking.Packet.Data;
using Hkmp.Networking.Packet.Update;
using Hkmp.Game.Client.Entity.Component;

internal static class DatagramProbe {
    private static void Check(bool ok) { if (!ok) throw new Exception("Datagram assertion failed"); }
    private static byte[] Message(int size, byte marker) {
        var data = Enumerable.Repeat(marker, size).ToArray();
        Buffer.BlockCopy(BitConverter.GetBytes((uint)(size-4)),0,data,0,4);
        return data;
    }
    public static void Reorder() {
        var data = Message(DatagramFraming.MaxMessageSize, 31); var frames = DatagramFraming.Fragment(data, uint.MaxValue).ToArray();
        var reader = new DatagramReassembler(); byte[] got = null;
        foreach (var frame in frames.Reverse()) {
            var ready = reader.Accept(frame, frame.Length, out var value);
            if (ready) got = value;
            Check(!reader.Accept(frame, frame.Length, out _)); // duplicate fragment
        }
        Check(got != null && got.SequenceEqual(data));
        Check(PacketManager.ReadDatagram(got).Length == data.Length - 4);
    }
    public static void LargeBossBatch() {
        var boss=new ReliableEntityUpdate{Id=42,SceneName="GG_Hollow_Knight",Visit=3,IsActive=true};
        boss.UpdateTypes.Add(EntityUpdateType.Data);boss.UpdateTypes.Add(EntityUpdateType.Active);
        for(var i=0;i<1024;i++){
            var item=new EntityNetworkData{Type=EntityComponentType.Fsm};item.Packet.Write(i);item.Packet.Write(new byte[128]);boss.GenericData.Add(item);
        }
        var collection=new PacketDataCollection<ReliableEntityUpdate>();collection.DataInstances.Add(boss);
        var update=new ServerUpdatePacket{Sequence=71};update.SetSendingPacketData(ServerUpdatePacketId.ReliableEntityUpdate,collection);
        var packet=new Packet();update.CreatePacket(packet);Check(packet.Length>65537);
        var reader=new DatagramReassembler();byte[] assembled=null;
        foreach(var frame in DatagramFraming.Fragment(packet.ToArray(),73).Reverse())if(reader.Accept(frame,frame.Length,out var data))assembled=data;
        Check(assembled!=null);var decoded=PacketManager.ReadDatagram(assembled);Check(decoded!=null);
        var received=new ServerUpdatePacket();Check(received.ReadPacket(decoded));
        var receivedCollection=(PacketDataCollection<ReliableEntityUpdate>)received.GetPacketData()[ServerUpdatePacketId.ReliableEntityUpdate];
        var receivedBoss=(ReliableEntityUpdate)receivedCollection.DataInstances.Single();Check(receivedBoss.Id==42&&receivedBoss.IsActive&&receivedBoss.GenericData.Count==1024);
        for(var i=0;i<1024;i++)Check(receivedBoss.GenericData[i].Packet.ReadInt()==i);
    }
    public static void MissingFragment() {
        var reader = new DatagramReassembler();
        var old = DatagramFraming.Fragment(Message(6000, 41), 7).ToArray();
        Check(!reader.Accept(old[0], old[0].Length, out _));
        foreach (var f in old.Skip(2)) Check(!reader.Accept(f, f.Length, out _));
        var next = Message(30, 55); var heartbeat = DatagramFraming.Fragment(next, 8).Single();
        Check(reader.Accept(heartbeat, heartbeat.Length, out var got) && got.SequenceEqual(next));
        Check(reader.Accept(old[1], old[1].Length, out got) && got.SequenceEqual(Message(6000, 41)));
    }
    public static void PeerIsolation() {
        var peers = Enumerable.Range(0, 5).Select(_ => new DatagramReassembler()).ToArray();
        // Identical IDs and simultaneous fragments from five different sessions.
        var messages = Enumerable.Range(0, 5).Select(n => Message(4000, (byte)(n + 1))).ToArray();
        var frames = messages.Select(m => DatagramFraming.Fragment(m, 0).Reverse().ToArray()).ToArray();
        for (var f = 0; f < frames[0].Length; f++)
            for (var p = 0; p < peers.Length; p++) {
                var ready = peers[p].Accept(frames[p][f], frames[p][f].Length, out var got);
                Check(ready == (f == frames[p].Length - 1));
                if (ready) Check(got.SequenceEqual(messages[p]));
            }
    }
    public static void ExpiryAndBounds() {
        var time = 0d; var reader = new DatagramReassembler(() => time);
        var frames = DatagramFraming.Fragment(Message(5000, 3), 1).ToArray();
        reader.Accept(frames[0], frames[0].Length, out _); time = 3;
        foreach (var f in frames.Skip(1)) Check(!reader.Accept(f, f.Length, out _));
        // The pre-expiry prefix was discarded; resending it completes the new assembly.
        Check(reader.Accept(frames[0], frames[0].Length, out _));
        for (uint n = 2; n < 1000; n++) {
            var f = DatagramFraming.Fragment(Message(5000, 3), n).First();
            Check(!reader.Accept(f, f.Length, out _));
        }
        var heartbeat = DatagramFraming.Fragment(Message(20, 9), 1001).Single();
        Check(reader.Accept(heartbeat, heartbeat.Length, out _));
    }
    public static void RejectMalformed() {
        var reader = new DatagramReassembler(); var f = DatagramFraming.Fragment(Message(30, 3), 1).Single();
        for (var n = 0; n < f.Length; n++) Check(!reader.Accept(f, n, out _));
        Check(!reader.Accept(f, f.Length + 1, out _));
        f[12] = 1; Check(!reader.Accept(f, f.Length, out _)); f[12] = 0;
        f[8] = 255; f[9] = f[10] = f[11] = 255; Check(!reader.Accept(f, f.Length, out _));
        Check(PacketManager.ReadDatagram(new byte[] { 10, 0, 1 }) == null);
        var valid = DatagramFraming.Fragment(Message(30, 4), 2).Single();
        Check(reader.Accept(valid, valid.Length, out var got) && PacketManager.ReadDatagram(got) != null);
    }
}
