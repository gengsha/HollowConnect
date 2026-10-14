using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using Hkmp.Game.Client;
using Hkmp.Networking;
using Hkmp.Networking.Packet;
using Hkmp.Networking.Packet.Data;
using Hkmp.Networking.Packet.Update;
using MonoMod.RuntimeDetour;

namespace HollowConnect.Smoke;

internal static class ProbePresenceTrace {
    private static readonly List<IDisposable> Hooks = new();
    private static readonly object Gate = new();
    internal static void Install() {
        if (Environment.GetEnvironmentVariable("HOLLOWCONNECT_NETWORK_TRACE") != "1") return;
        Observe<ServerUpdatePacketId>(); Observe<ClientUpdatePacketId>();
        Hooks.Add(new Hook(typeof(ClientManager).GetMethod("OnPlayerEnterScene", BindingFlags.Instance | BindingFlags.NonPublic),
            (Action<Action<ClientManager, ClientPlayerEnterScene>, ClientManager, ClientPlayerEnterScene>)((orig, manager, data) => {
                Write("GAME " + Describe(data) + " local=" + Hkmp.CoopContext.LocalScene + "/" + Hkmp.CoopContext.LocalVisit + "/" + Hkmp.CoopContext.Epoch);
                orig(manager, data);
            })));
    }
    private static void Observe<TId>() where TId : Enum {
        Hooks.Add(new Hook(typeof(UpdatePacket<TId>).GetMethod("CreatePacket"),
            (Action<Action<UpdatePacket<TId>, Packet>, UpdatePacket<TId>, Packet>)((orig, packet, bytes) => {
                Trace("SEND", packet); orig(packet, bytes);
            })));
        Hooks.Add(new Hook(typeof(UpdatePacket<TId>).GetMethod("FilterReceivedData", BindingFlags.Instance | BindingFlags.NonPublic),
            (Action<Action<UpdatePacket<TId>, SequenceWindow, EntitySnapshotHistory>, UpdatePacket<TId>, SequenceWindow, EntitySnapshotHistory>)
            ((orig, packet, origins, snapshots) => {
                Trace("RECEIVE", packet); orig(packet, origins, snapshots); Trace("KEPT", packet);
            })));
    }
    private static void Trace<TId>(string stage, UpdatePacket<TId> packet) where TId : Enum {
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
        var own = (Dictionary<TId, IPacketData>)typeof(BasePacket<TId>).GetField("NormalPacketData", flags).GetValue(packet);
        var resends = (Dictionary<ushort, Dictionary<TId, IPacketData>>)typeof(UpdatePacket<TId>).GetField("ResendPacketData", flags).GetValue(packet);
        var items = new List<string>();
        foreach (var data in own.Values) Add(packet.Sequence, data);
        foreach (var pair in resends) foreach (var data in pair.Value.Values) Add(pair.Key, data);
        if (items.Count != 0) Write(stage + " " + typeof(TId).Name + " seq=" + packet.Sequence + " " + string.Join("; ", items));
        void Add(ushort sequence, IPacketData data) {
            if (data is RawPacketDataCollection collection) { foreach (var value in collection.DataInstances) Add(sequence, value); return; }
            var text = Describe(data); if (text.Length != 0) items.Add("origin=" + sequence + " " + text);
        }
    }
    private static string Describe(IPacketData data) => data switch {
        ClientPlayerEnterScene p => $"enter peer={p.Id} scene={p.SceneName}/{p.Visit}/{p.Epoch} sourceVisit={p.PlayerVisit}",
        ClientPlayerLeaveScene p => $"leave peer={p.Id} scene={p.SceneName}/{p.Visit}/{p.Epoch} sourceVisit={p.PlayerVisit}",
        ServerPlayerEnterScene p => $"enter scene={p.SceneName}/{p.Visit}/{p.Epoch}",
        ServerPlayerLeaveScene p => $"leave scene={p.SceneName}/{p.Visit}/{p.Epoch}",
        _ => ""
    };
    internal static void Write(string message) {
        if (Environment.GetEnvironmentVariable("HOLLOWCONNECT_NETWORK_TRACE") != "1") return;
        var root = Environment.GetEnvironmentVariable("HOLLOWCONNECT_SMOKE_ROOT");
        var role = Environment.GetEnvironmentVariable("HOLLOWCONNECT_SMOKE_ROLE");
        lock (Gate) File.AppendAllText(Path.Combine(root, role + ".presence.log"), DateTime.UtcNow.ToString("O") + " " + message + "\n");
    }
}
