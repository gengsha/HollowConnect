using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using Hkmp.Networking;
using Hkmp.Networking.Packet.Update;
using MonoMod.RuntimeDetour;
using Org.BouncyCastle.Tls;

namespace HollowConnect.Smoke;

// Test-only faults before DTLS encryption. Buffer complete application messages
// per transport so fragments can never move to a different peer or session.
internal static class ProbeNetworkFaults {
    private sealed class Peer {
        public int Messages, Dropped, Reordered, Duplicated;
        public int NextDrop = 13, DropThrough, NextHold = 9, NextDuplicate = 17;
        public uint Schedule = 0x6D2B79F5;
        public readonly List<byte[]> Current = new();
        public List<byte[]> Held;
    }
    private static readonly object Gate = new();
    private static readonly Dictionary<DtlsTransport, Peer> Peers = new();
    private static readonly List<IDisposable> Hooks = new();
    private static int _nestedResends;
    private static bool _periodic;

    internal static void Install() {
        var mode = Environment.GetEnvironmentVariable("HOLLOWCONNECT_NETWORK_FAULTS");
        if (mode != "1" && mode != "periodic") return;
        _periodic = mode == "periodic";
        Hooks.Add(new Hook(typeof(DtlsTransport).GetMethod("Send", new[] { typeof(byte[]), typeof(int), typeof(int) }),
            (Action<Action<DtlsTransport, byte[], int, int>, DtlsTransport, byte[], int, int>)Send));
        ObserveResends<ServerUpdatePacketId>();
        ObserveResends<ClientUpdatePacketId>();
    }

    private static void ObserveResends<TId>() where TId : Enum {
        var type = typeof(UpdatePacket<TId>);
        var builtIn = type.GetField("ResendPacketData", BindingFlags.Instance | BindingFlags.NonPublic);
        var addon = type.GetField("ResendAddonPacketData", BindingFlags.Instance | BindingFlags.NonPublic);
        Hooks.Add(new Hook(type.GetMethod("SetLostReliableData"),
            (Action<Action<UpdatePacket<TId>, UpdatePacket<TId>>, UpdatePacket<TId>, UpdatePacket<TId>>)
            ((orig, packet, lost) => {
                if (((System.Collections.IDictionary)builtIn.GetValue(lost)).Count != 0 ||
                    ((System.Collections.IDictionary)addon.GetValue(lost)).Count != 0)
                    Interlocked.Increment(ref _nestedResends);
                orig(packet, lost);
            })));
    }

    private static void Send(Action<DtlsTransport, byte[], int, int> orig, DtlsTransport transport,
        byte[] buffer, int offset, int length) {
        var frame = new byte[length]; Buffer.BlockCopy(buffer, offset, frame, 0, length);
        if (!DatagramFraming.ReadHeader(frame, length, out _, out _, out var index, out var count)) {
            orig(transport, buffer, offset, length); return;
        }
        Peer peer;
        lock (Gate) {
            if (!Peers.TryGetValue(transport, out peer)) { peer = new Peer(); Peers[transport] = peer; }
        }
        lock (peer) {
            if (index == 0) { peer.Messages++; peer.Current.Clear(); }
            peer.Current.Add(frame);
            if (index != count - 1) return;
            // Keep the periodic pattern available as a separate starvation
            // diagnostic. The main schedule has the same mean intervals but
            // varies them with a fixed seed, independently of packet content.
            var remainder = peer.Messages % 13;
            var drop = remainder == 0 || remainder == 1 && peer.Messages > 1;
            var hold = peer.Messages % 9 == 0;
            var duplicate = peer.Messages % 17 == 0;
            if (!_periodic) {
                if (peer.Messages == peer.NextDrop) {
                    peer.DropThrough = peer.Messages + 1;
                    peer.NextDrop += Interval(peer, 10, 7);
                }
                drop = peer.Messages <= peer.DropThrough;
                hold = peer.Messages == peer.NextHold;
                if (hold) peer.NextHold += Interval(peer, 6, 7);
                duplicate = peer.Messages == peer.NextDuplicate;
                if (duplicate) peer.NextDuplicate += Interval(peer, 12, 11);
            }
            if (drop) {
                ProbePresenceTrace.Write("DROP transport=" + transport.GetHashCode() + " message=" + peer.Messages + " seq=" + BitConverter.ToUInt16(peer.Current[0], DatagramFraming.HeaderSize + 4));
                peer.Dropped++; peer.Current.Clear(); return;
            }
            if (hold && peer.Held == null) {
                ProbePresenceTrace.Write("HOLD transport=" + transport.GetHashCode() + " message=" + peer.Messages + " seq=" + BitConverter.ToUInt16(peer.Current[0], DatagramFraming.HeaderSize + 4));
                peer.Held = new List<byte[]>(peer.Current); peer.Current.Clear(); return;
            }
            Emit(peer.Current);
            if (duplicate) { Emit(peer.Current); peer.Duplicated++; }
            if (peer.Held != null) { Emit(peer.Held); peer.Held = null; peer.Reordered++; }
            peer.Current.Clear();
        }
        void Emit(IEnumerable<byte[]> frames) {
            foreach (var data in frames) orig(transport, data, 0, data.Length);
        }
    }

    private static int Interval(Peer peer, int minimum, uint range) {
        var state = peer.Schedule;
        state ^= state << 13; state ^= state >> 17; state ^= state << 5;
        peer.Schedule = state;
        return minimum + (int)(state % range);
    }

    internal static void Verify(string root, string role) {
        if (Hooks.Count == 0) return;
        int sent = 0, dropped = 0, reordered = 0, duplicated = 0;
        Peer[] peers;
        lock (Gate) peers = Peers.Values.ToArray();
        foreach (var peer in peers) lock (peer) {
            sent += peer.Messages; dropped += peer.Dropped; reordered += peer.Reordered; duplicated += peer.Duplicated;
        }
        File.AppendAllText(Path.Combine(root, role + ".log"), DateTime.UtcNow.ToString("O") +
            $" NETWORK FAULTS pattern={(_periodic ? "periodic" : "fixed-seed")} messages={sent} dropped={dropped} reordered={reordered} duplicated={duplicated} nestedResends={_nestedResends}\n");
        if (dropped == 0 || reordered == 0 || duplicated == 0) {
            File.WriteAllText(Path.Combine(root, role + ".failed"), "Network fault injection did not exercise every configured fault");
            throw new InvalidOperationException("Network fault injection did not exercise every configured fault");
        }
    }
}
