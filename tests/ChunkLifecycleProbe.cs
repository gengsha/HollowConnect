using System;
using System.Collections.Concurrent;
using System.Threading;
using Hkmp.Networking.Chunk;
using Hkmp.Networking.Packet;
using Hkmp.Networking.Packet.Data;

internal static class ChunkLifecycleProbe {
    private sealed class Sender : ChunkSender {
        internal readonly ConcurrentQueue<byte[]> Slices = new ConcurrentQueue<byte[]>();
        protected override void SetSliceData(byte chunkId, byte sliceId, byte numSlices, byte[] data) => Slices.Enqueue(data);
    }
    public static void Run() {
        var sender = new Sender();
        try {
            sender.Start(); sender.EnqueuePacket(new Packet(new byte[] { 12 }));
            sender.EnqueuePacket(new Packet(new byte[] { 13 })); // queued in the old session
            if (!SpinWait.SpinUntil(() => sender.Slices.Count > 0, 1000)) throw new Exception("Old chunk did not start");
            sender.Stop(); sender.Stop();
            while (sender.Slices.TryDequeue(out _)) { }
            sender.Start(); sender.EnqueuePacket(new Packet(new byte[] { 42 }));
            if (!SpinWait.SpinUntil(() => sender.Slices.Count > 0, 1000)) throw new Exception("New chunk did not start");
            sender.Stop();
            foreach (var slice in sender.Slices)
                if (slice.Length != 1 || slice[0] != 42) throw new Exception("Retired chunk leaked into the new session");
        } finally { sender.Stop(); }
    }

    public static void AcknowledgedReconnect() {
        var sender = new Sender();
        try {
            for (var session = 0; session < 2; session++) {
                while (sender.Slices.TryDequeue(out _)) { }
                sender.Start(); sender.EnqueuePacket(new Packet(new byte[] { 42 }));
                if (!SpinWait.SpinUntil(() => sender.Slices.Count > 0, 1000)) throw new Exception("Chunk did not start");
                var finished = 0;
                sender.FinishSendingData(() => Interlocked.Exchange(ref finished, 1));
                sender.ProcessReceivedData(new SliceAckData { ChunkId = 0, NumSlices = 1, Acked = new[] { true } });
                if (!SpinWait.SpinUntil(() => Volatile.Read(ref finished) == 1, 1000)) throw new Exception("Previous ack prevented completion in session " + session);
                sender.Stop();
            }
        } finally { sender.Stop(); }
    }
}
