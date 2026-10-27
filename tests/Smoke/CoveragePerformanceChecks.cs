using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Hkmp.Game.Client.Entity;
using Hkmp.Networking.Packet;
using Hkmp.Networking.Client;
using Hkmp.Networking.Packet.Data;
using Hkmp.Util;
using UnityEngine;
using Object = UnityEngine.Object;

namespace HollowConnect.Smoke;

public sealed partial class CoverageProbe {
    private IEnumerator PerformanceAssertions() {
        var hooks = typeof(Hkmp.Game.Client.Entity.Action.FsmActionHooks).GetField("TypeEvents", BindingFlags.Static | BindingFlags.NonPublic);
        var installed = (System.Collections.IDictionary)hooks.GetValue(null);
        foreach (var type in Hkmp.Game.Client.Entity.Action.EntityFsmActions.SupportedActionTypes)
            if (!installed.Contains(type)) throw new Exception("Action detour was not prepared before combat: " + type.Name);
        Mark("PASS all supported FSM action detours installed before native attacks");
        var frames = new List<int>(); var order = new List<int>();
        for (var i = 0; i < 6; i++) {
            var index = i;
            ThreadUtil.RunActionOnMainThread(() => {
                order.Add(index); frames.Add(Time.frameCount);
                var timer = System.Diagnostics.Stopwatch.StartNew();
                while (timer.ElapsedMilliseconds < 4) System.Threading.Thread.SpinWait(100);
            });
        }
        yield return Until(() => order.Count == 6, "main-thread backlog drains");
        if (!order.SequenceEqual(Enumerable.Range(0, 6)) || frames.Distinct().Count() != 6)
            throw new Exception("Main-thread backlog did not preserve FIFO across Unity frames");
        Mark("PASS main-thread expensive callbacks drain across six Unity frames in FIFO order");

        var source = new GameObject("HollowConnect missing resource fixture"); source.SetActive(false);
        var net = new NetClient(new PacketManager()); Entity entity = null;
        var cache = typeof(SpawnDescriptor).GetField("resourceCandidates", BindingFlags.Static | BindingFlags.NonPublic);
        try {
            entity = new Entity(net, 0xffffffffffffffb0UL, EntityType.DynamicHazard, source);
            var missing = new EntitySpawn { Prefab = "HollowConnect deliberately missing asset", SpawnedType = EntityType.DynamicHazard };
            SpawnDescriptor.Clear();
            GameObject[] SearchBurst() {
                GameObject[] found = null;
                for (var i = 0; i < 12; i++) {
                    missing.Prefab = "HollowConnect deliberately missing asset " + i;
                    if (SpawnDescriptor.Create(missing, entity) != null) throw new Exception("Missing asset resolved to the wrong prefab");
                    var current = (GameObject[])cache.GetValue(null);
                    if (current == null || (found != null && !ReferenceEquals(current, found)))
                        throw new Exception("Missing asset burst repeated its global resource scan in one frame");
                    found = current;
                }
                return found;
            }
            var first = SearchBurst(); yield return null;
            if (ReferenceEquals(first, SearchBurst())) throw new Exception("Resource miss was cached across frames");
            SpawnDescriptor.Clear();
            if (cache.GetValue(null) != null) throw new Exception("Resource candidates survived scope cleanup");
            Mark("PASS missing resource bursts share one scan per frame, retry next frame and clear at scope end");
        } finally { entity?.Destroy(); Object.Destroy(source); net.Disconnect(); SpawnDescriptor.Clear(); }
    }
}
