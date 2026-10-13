using System;
using System.Linq;
using Hkmp.Networking;
using Hkmp.Networking.Chunk;
using Hkmp.Networking.Packet.Data;

internal static class ChunkReceiverProbe {
    private sealed class Receiver : ChunkReceiver {
        protected override void SetSliceAckData(byte chunkId, ushort numSlices, bool[] acked) { }
    }
    public static void FutureSlice() {
        var receiver = new Receiver(); byte[] completed = null;
        receiver.ChunkReceivedEvent += p => completed = p.ToArray();
        receiver.ProcessReceivedData(new SliceData { ChunkId = 0, SliceId = 0, NumSlices = 2, Data = Enumerable.Repeat((byte)1, ConnectionManager.MaxSliceSize).ToArray() });
        receiver.ProcessReceivedData(new SliceData { ChunkId = 1, SliceId = 1, NumSlices = 2, Data = new byte[] { 99 } });
        if (completed != null) throw new Exception("Future chunk slice contaminated the current chunk");
        receiver.ProcessReceivedData(new SliceData { ChunkId = 0, SliceId = 1, NumSlices = 2, Data = new byte[] { 2 } });
        if (completed == null || completed.Length != 1025 || completed.Last() != 2) throw new Exception("Current chunk did not complete intact");
    }
    public static void ResetFromCallback() {
        var receiver = new Receiver(); var completed = 0;
        receiver.ChunkReceivedEvent += p => {
            completed++;
            if (completed != 1) return;
            receiver.Reset();
            receiver.ProcessReceivedData(new SliceData { ChunkId = 0, SliceId = 0, NumSlices = 2,
                Data = new byte[ConnectionManager.MaxSliceSize] });
        };
        receiver.ProcessReceivedData(new SliceData { ChunkId = 0, SliceId = 0, NumSlices = 1, Data = new byte[] { 1 } });
        receiver.ProcessReceivedData(new SliceData { ChunkId = 0, SliceId = 1, NumSlices = 2, Data = new byte[] { 2 } });
        if (completed != 2) throw new Exception("Completion overwrote receiver state reset by a disconnect/reconnect callback");
    }
    public static void MalformedSlices() {
        var receiver = new Receiver(); byte[] completed = null;
        receiver.ChunkReceivedEvent += p => completed = p.ToArray();
        foreach (var slice in new[] {
            new SliceData { ChunkId = 0, SliceId = 0, NumSlices = 0, Data = new byte[] { 1 } },
            new SliceData { ChunkId = 0, SliceId = 2, NumSlices = 2, Data = new byte[] { 1 } },
            new SliceData { ChunkId = 0, SliceId = 0, NumSlices = 2, Data = new byte[] { 1 } },
            new SliceData { ChunkId = 0, SliceId = 0, NumSlices = 1, Data = new byte[1025] },
            new SliceData { ChunkId = 0, SliceId = 0, NumSlices = 1, Data = Array.Empty<byte>() }
        }) receiver.ProcessReceivedData(slice);
        if (completed != null) throw new Exception("Malformed slice was accepted");
        receiver.ProcessReceivedData(new SliceData { ChunkId = 0, SliceId = 0, NumSlices = 1, Data = new byte[] { 7 } });
        if (completed == null || !completed.SequenceEqual(new byte[] { 7 })) throw new Exception("Malformed slice corrupted the following chunk");
    }
}
