using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using Hkmp.Networking;
using MonoMod.RuntimeDetour;
using Newtonsoft.Json;
using Org.BouncyCastle.Tls;

namespace HollowConnect.Smoke;

// Test-only, per-DTLS-session network profile. Encrypt only when a frame is
// actually emitted, so this does not manufacture DTLS replay-window failures.
internal static class ProbeRelayProfile {
    private sealed class Peer {
        internal double Next;
        internal uint Random = 0xA71F392D;
        internal long Frames, Bytes, Dropped, Emitted, Errors;
        internal double MaxQueueMs;
    }
    private sealed class Pending {
        internal double Due;
        internal Peer Peer;
        internal DtlsTransport Transport;
        internal byte[] Data;
        internal bool Drop;
        internal Action<DtlsTransport, byte[], int, int> Send;
    }
    private static readonly object Gate = new();
    private static readonly Dictionary<DtlsTransport, Peer> Peers = new();
    private static readonly List<Pending> Queue = new();
    private static Hook hook;
    private static Func<DtlsTransport, bool> bypass;
    private static long bypassed;
    internal static void ConfigureHost(Hkmp.Game.GameManager engine, HollowConnect.CoopRuntime runtime, string role) {
        // A hosted game's own client/server link remains local. Only the
        // guest link traverses Steam/FRP; impairing the host loopback as well
        // models a separate remote-authority bottleneck instead.
        var server = (Hkmp.Networking.Server.NetServer)typeof(Hkmp.Game.Server.ServerManager)
            .GetField("_netServer", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic).GetValue(engine.ServerManager);
        if (role == "host") bypass = transport => ReferenceEquals(transport, engine.NetClient.UpdateManager.DtlsTransport) ||
            ReferenceEquals(transport, server.GetUpdateManagerForClient(runtime.LocalId)?.DtlsTransport);
    }
    private const int BytesPerSecond = 128 * 1024;
    private static double Now => (double)Stopwatch.GetTimestamp() / Stopwatch.Frequency;

    internal static void Install() {
        if (Environment.GetEnvironmentVariable("HOLLOWCONNECT_RELAY_PROFILE") != "1") return;
        hook = new Hook(typeof(DtlsTransport).GetMethod("Send", new[] { typeof(byte[]), typeof(int), typeof(int) }),
            (Action<Action<DtlsTransport, byte[], int, int>, DtlsTransport, byte[], int, int>)Send);
        new Thread(Pump) { IsBackground = true, Name = "Smoke relay profile" }.Start();
    }
    private static void Send(Action<DtlsTransport, byte[], int, int> orig, DtlsTransport transport, byte[] buffer, int offset, int length) {
        if (bypass?.Invoke(transport) == true) { Interlocked.Increment(ref bypassed); orig(transport, buffer, offset, length); return; }
        if (offset != 0 || !DatagramFraming.ReadHeader(buffer, length, out _, out _, out _, out _)) {
            orig(transport, buffer, offset, length); return;
        }
        var data = new byte[length]; Buffer.BlockCopy(buffer, offset, data, 0, length);
        lock (Gate) {
            if (!Peers.TryGetValue(transport, out var peer)) Peers[transport] = peer = new Peer();
            var now = Now;
            peer.Frames++; peer.Bytes += length;
            // Finite two-second egress queue, with independent seeded loss of
            // individual frames, including initial room snapshots.
            if (peer.Next - now > 2) { peer.Dropped++; return; }
            peer.Next = Math.Max(now, peer.Next) + (double)length / BytesPerSecond;
            peer.MaxQueueMs = Math.Max(peer.MaxQueueMs, (peer.Next - now) * 1000);
            var state = peer.Random; state ^= state << 13; state ^= state >> 17; state ^= state << 5; peer.Random = state;
            Queue.Add(new Pending { Due = peer.Next + .04, Peer = peer, Transport = transport, Data = data, Drop = state % 100 == 0, Send = orig });
        }
    }
    private static void Pump() {
        while (true) {
            Pending[] ready;
            lock (Gate) {
                var now = Now;
                ready = Queue.Where(p => p.Due <= now).OrderBy(p => p.Due).ToArray();
                Queue.RemoveAll(p => p.Due <= now);
            }
            foreach (var frame in ready) {
                if (frame.Drop) { lock (Gate) frame.Peer.Dropped++; continue; }
                try { frame.Send(frame.Transport, frame.Data, 0, frame.Data.Length); lock (Gate) frame.Peer.Emitted++; }
                catch (Exception) { lock (Gate) frame.Peer.Errors++; }
            }
            Thread.Sleep(2);
        }
    }
    internal static void Report(string root, string role) {
        if (hook == null) return;
        lock (Gate) File.WriteAllText(Path.Combine(root, role + ".relay-profile.json"), JsonConvert.SerializeObject(new {
            fixture = 2, bypassed, oneWayDelayMs = 40, bytesPerSecond = BytesPerSecond, fragmentLossPercent = 1, queueLimitMs = 2000,
            pending = Queue.Count, peers = Peers.Values.Select(p => new { p.Frames, p.Bytes, p.Dropped, p.Emitted, p.Errors, p.MaxQueueMs })
        }, Formatting.Indented));
    }
}
