using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Hkmp.Game.Client.Entity;
using Hkmp.Game.Client.Entity.Component;
using Hkmp.Networking.Client;
using Hkmp.Networking.Packet;
using HollowConnect;
using MonoMod.RuntimeDetour;
using UnityEngine;
using Object = UnityEngine.Object;

namespace HollowConnect.Smoke;

internal static class UnityLifetimeChecks {
    internal static void Run(Action<string> mark) {
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
        var type = typeof(CombatStateComponent).GetNestedType("Parts", BindingFlags.NonPublic);
        var refresh = type.GetMethod("Refresh", flags);
        var owner = new GameObject("Combat lifetime fixture");
        owner.AddComponent<BoxCollider2D>();
        var parts = Activator.CreateInstance(type, flags, null, new object[] { owner.transform }, null);
        Object.DestroyImmediate(owner);
        refresh.Invoke(parts, null);
        foreach (var field in type.GetFields(flags).Where(f => f.FieldType.IsArray))
            if (field.GetValue(parts) is not Array array || array.Length != 0)
                throw new Exception("Destroyed Unity wrapper retained combat components: " + field.Name);
        mark("PASS destroyed Unity Transform Refresh empties every component cache");

        var host = new GameObject("Combat host lifetime fixture");
        var client = new GameObject("Combat client lifetime fixture");
        var net = new NetClient(new PacketManager());
        var combat = new CombatStateComponent(net, 0xffffffffffffffb1UL,
            new HostClientPair<GameObject> { Host = host, Client = client });
        try {
            var nodeType = typeof(CombatStateComponent).GetNestedType("Node", BindingFlags.NonPublic);
            var node = Activator.CreateInstance(nodeType, true);
            nodeType.GetField("Path", flags).SetValue(node, "client-only");
            nodeType.GetField("Client", flags).SetValue(node,
                Activator.CreateInstance(type, flags, null, new object[] { client.transform }, null));
            var nodes = Array.CreateInstance(nodeType, 1); nodes.SetValue(node, 0);
            typeof(CombatStateComponent).GetField("nodes", flags).SetValue(combat, nodes);
            typeof(CombatStateComponent).GetMethod("Build", flags).Invoke(combat, null);
            typeof(CombatStateComponent).GetMethod("Tick", flags).Invoke(combat, null);
            mark("PASS combat hierarchy rebuild tolerates a client-only node");
        } finally { combat.Destroy(); Object.DestroyImmediate(host); Object.DestroyImmediate(client); net.Disconnect(); }
    }

    internal static IEnumerator SampleRuntime(CoopRuntime runtime, Action<string> mark) {
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
        var costs = new List<double>(); var snapshots = 0;
        using (new Hook(typeof(CoopRuntime).GetMethod("Update", flags),
            (Action<Action<CoopRuntime>, CoopRuntime>)((orig, self) => {
                var start = System.Diagnostics.Stopwatch.GetTimestamp(); orig(self);
                if (ReferenceEquals(runtime, self)) costs.Add((System.Diagnostics.Stopwatch.GetTimestamp() - start) * 1000.0 / System.Diagnostics.Stopwatch.Frequency);
            })))
        using (new Hook(typeof(CoopRuntime).GetMethod("BuildDebugSnapshot", flags),
            (Func<Func<CoopRuntime, HollowConnect.Core.SaveSafetySnapshot>, CoopRuntime, HollowConnect.Core.SaveSafetySnapshot>)((orig, self) => {
                if (ReferenceEquals(runtime, self)) snapshots++;
                return orig(self);
            }))) {
            var deadline = Time.realtimeSinceStartup + 8;
            while (Time.realtimeSinceStartup < deadline) yield return null;
        }
        costs.Sort();
        if (costs.Count < 60 || snapshots > 18) throw new Exception("Runtime sample missing frames or debug snapshot still runs each frame");
        var p95 = costs[(costs.Count - 1) * 95 / 100];
        mark("PERFORMANCE CoopRuntime.Update samples=" + costs.Count + " p95Ms=" + p95.ToString("F4") +
            " maxMs=" + costs.Last().ToString("F4") + " debugSnapshots=" + snapshots);
        if (p95 > 2) throw new Exception("Idle co-op Update exceeds 2ms at p95");
        mark("PASS co-op idle CPU budget and diagnostic sample frequency");
    }
}
