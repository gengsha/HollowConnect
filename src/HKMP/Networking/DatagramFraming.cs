using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;

namespace Hkmp.Networking;

/// <summary>
/// Application datagrams inside DTLS. Fragments carry their own message ID and
/// offset; a missing fragment cannot consume a later heartbeat or another peer.
/// Wire revision 2 in HollowConnect 0.1.8 supports large combat snapshots.
/// </summary>
internal static class DatagramFraming {
    internal const int HeaderSize = 16;
    internal const int PayloadSize = 1200 - HeaderSize;
    internal const int MaxMessageSize = 1024 * 1024;
    private const uint Magic = 0x32464348; // HCF2, little endian

    internal static IEnumerable<byte[]> Fragment(byte[] message, uint id) {
        if (message == null || message.Length < 4 || message.Length > MaxMessageSize)
            throw new ArgumentException("Invalid application datagram length");
        var count = (message.Length + PayloadSize - 1) / PayloadSize;
        for (var index = 0; index < count; index++) {
            var length = System.Math.Min(PayloadSize, message.Length - index * PayloadSize);
            var data = new byte[HeaderSize + length];
            Write32(data, 0, Magic); Write32(data, 4, id); Write32(data, 8, (uint)message.Length);
            Write16(data, 12, index); Write16(data, 14, count);
            Buffer.BlockCopy(message, index * PayloadSize, data, HeaderSize, length);
            yield return data;
        }
    }

    internal static bool ReadHeader(byte[] data, int length, out uint id, out int total, out int index, out int count) {
        id = 0; total = index = count = 0;
        if (data == null || length > data.Length || length <= HeaderSize || length > 1200 || Read32(data, 0) != Magic) return false;
        id = Read32(data, 4);
        var size = Read32(data, 8);
        if (size < 4 || size > MaxMessageSize) return false;
        total = (int)size; index = Read16(data, 12); count = Read16(data, 14);
        return count == (total + PayloadSize - 1) / PayloadSize && index < count &&
            length - HeaderSize == System.Math.Min(PayloadSize, total - index * PayloadSize);
    }

    private static uint Read32(byte[] d, int o) => (uint)(d[o] | d[o + 1] << 8 | d[o + 2] << 16 | d[o + 3] << 24);
    private static int Read16(byte[] d, int o) => d[o] | d[o + 1] << 8;
    private static void Write32(byte[] d, int o, uint v) { for (var n = 0; n < 4; n++) d[o + n] = (byte)(v >> (8 * n)); }
    private static void Write16(byte[] d, int o, int v) { d[o] = (byte)v; d[o + 1] = (byte)(v >> 8); }
}

/// <summary>One instance per DTLS session, with bounded memory and expiry.</summary>
internal sealed class DatagramReassembler {
    private sealed class Pending {
        internal byte[] Data;
        internal bool[] Seen;
        internal int Received;
        internal double Created;
    }
    private readonly Dictionary<uint, Pending> pending = new Dictionary<uint, Pending>();
    private readonly HashSet<uint> completed = new HashSet<uint>();
    private readonly Queue<uint> recent = new Queue<uint>();
    private readonly Func<double> now;
    internal DatagramReassembler(Func<double> now = null) {
        this.now = now ?? (() => (double)Stopwatch.GetTimestamp() / Stopwatch.Frequency);
    }

    internal bool Accept(byte[] data, int length, out byte[] message) {
        message = null;
        if (!DatagramFraming.ReadHeader(data, length, out var id, out var total, out var index, out var count)) return false;
        if (completed.Contains(id)) return false;
        var time = now();
        foreach (var stale in pending.Where(p => time - p.Value.Created >= 2).Select(p => p.Key).ToArray()) pending.Remove(stale);
        if (!pending.TryGetValue(id, out var part)) {
            if (pending.Count >= 16) pending.Remove(pending.OrderBy(p => p.Value.Created).First().Key);
            part = new Pending { Data = new byte[total], Seen = new bool[count], Created = time };
            pending.Add(id, part);
        }
        if (part.Data.Length != total || part.Seen.Length != count || part.Seen[index]) return false;
        Buffer.BlockCopy(data, DatagramFraming.HeaderSize, part.Data, index * DatagramFraming.PayloadSize, length - DatagramFraming.HeaderSize);
        part.Seen[index] = true;
        if (++part.Received != count) return false;
        pending.Remove(id); completed.Add(id); recent.Enqueue(id);
        while (recent.Count > 128) completed.Remove(recent.Dequeue());
        message = part.Data;
        return true;
    }
}

