using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using Hkmp.Game.Client.Entity;
using Hkmp.Networking;
using Hkmp.Networking.Client;
using Hkmp.Animation;
using Hkmp.Networking.Packet;
using Hkmp.Networking.Packet.Update;
using Hkmp.Util;
using MonoMod.RuntimeDetour;
using Newtonsoft.Json;
using Org.BouncyCastle.Tls;
using UnityEngine;

namespace HollowConnect.Smoke;

public sealed class ColosseumNetworkMetrics : MonoBehaviour {
    private readonly object gate = new();
    private readonly List<IDisposable> hooks = new();
    private sealed class Wire {
        public long messages, bytes, fragments, fragmented, maxBytes, maxFragments;
    }
    private readonly Dictionary<string, Wire> wire = new();
    private readonly Dictionary<string, Dictionary<int, long>> sizes = new();
    private readonly System.Random loss = new System.Random(21411);
    private long droppedFrames, intervalQueueUs, intervalCallbackUs, intervalUpdateUs;
    private double intervalFrameMs;
    internal bool LossEnabled;
    internal string CombatPhase = "none";
    internal int CombatHits, CombatDamage;
    internal sealed class ObservedCombatHit {
        public string phase, source, path;
        public int damage, sourceId, owner;
        public bool remote, reference;
    }
    internal readonly List<ObservedCombatHit> ObservedCombatHits = new();
    private Hkmp.Game.GameManager engine;
    private string root, role;
    public string Phase = "entry";
    private long callbacks, maxQueueUs, maxCallbackUs, decoded, lastDecoded, entityPackets, playerPackets;
    private float nextSample;
    private int frames;
    private double maxFrameMs, totalFrameMs;
    private static readonly FieldInfo Queue = typeof(ThreadUtil).GetField("ActionsToRun", BindingFlags.Static | BindingFlags.NonPublic);
    private static object Field(object o, string n) => o?.GetType().GetField(n, BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(o);
    private static long Micros(long ticks) => ticks * 1000000 / Stopwatch.Frequency;

    internal void Initialize(Hkmp.Game.GameManager game, string output, string endpoint) {
        engine = game; root = output; role = endpoint;
        hooks.Add(new Hook(typeof(DtlsTransport).GetMethod("Send", new[] { typeof(byte[]), typeof(int), typeof(int) }),
            new Action<Action<DtlsTransport, byte[], int, int>, DtlsTransport, byte[], int, int>((orig, transport, bytes, offset, length) => {
                if (offset == 0 && DatagramFraming.ReadHeader(bytes, length, out _, out var total, out var index, out var count)) {
                    var direction = ReferenceEquals(transport, engine.NetClient.UpdateManager.DtlsTransport) ? "client-to-server" : "server-to-client";
                    lock (gate) {
                        if (!wire.TryGetValue(direction, out var stats)) wire[direction] = stats = new Wire();
                        stats.bytes += length; stats.fragments++;
                        if (index == 0) {
                            stats.messages++; if (count > 1) stats.fragmented++;
                            stats.maxBytes = Math.Max(stats.maxBytes, total); stats.maxFragments = Math.Max(stats.maxFragments, count);
                            var key = Phase + "/" + direction;
                            if (!sizes.TryGetValue(key, out var histogram)) sizes[key] = histogram = new Dictionary<int, long>();
                            histogram[count] = histogram.TryGetValue(count, out var old) ? old + 1 : 1;
                        }
                        if (LossEnabled && loss.NextDouble() < .01) { droppedFrames++; return; }
                    }
                }
                orig(transport, bytes, offset, length);
            })));
        hooks.Add(new Hook(typeof(ThreadUtil).GetMethod("RunActionOnMainThread"),
            new Action<Action<Action>, Action>((orig, callback) => {
                var enqueued = Stopwatch.GetTimestamp();
                orig(() => {
                    var started = Stopwatch.GetTimestamp();
                    try { callback(); }
                    finally {
                        lock (gate) {
                            callbacks++; maxQueueUs = Math.Max(maxQueueUs, Micros(started - enqueued));
                            maxCallbackUs = Math.Max(maxCallbackUs, Micros(Stopwatch.GetTimestamp() - started));
                            intervalQueueUs = Math.Max(intervalQueueUs, Micros(started - enqueued));
                            intervalCallbackUs = Math.Max(intervalCallbackUs, Micros(Stopwatch.GetTimestamp() - started));
                        }
                    }
                });
            })));
        hooks.Add(new Hook(typeof(MonoBehaviourUtil).GetMethod("Update"),
            new Action<Action<MonoBehaviourUtil>, MonoBehaviourUtil>((orig, util) => {
                var start = Stopwatch.GetTimestamp();
                try { orig(util); } finally { intervalUpdateUs = Math.Max(intervalUpdateUs, Micros(Stopwatch.GetTimestamp() - start)); }
            })));
        hooks.Add(new Hook(typeof(ClientUpdateManager).GetMethod("UpdatePlayerAnimation"),
            new Action<Action<ClientUpdateManager, Hkmp.Animation.AnimationClip, int, bool[]>, ClientUpdateManager, Hkmp.Animation.AnimationClip, int, bool[]>((orig, mgr, clip, frame, info) => {
                if (CombatPhase != "none") Event("outgoing", clip.ToString(), frame);
                orig(mgr, clip, frame, info);
            })));
        hooks.Add(new Hook(typeof(AnimationManager).GetMethod("OnPlayerAnimationUpdate"),
            new Action<Action<AnimationManager, ushort, int, int, bool[]>, AnimationManager, ushort, int, int, bool[]>((orig, mgr, id, clip, frame, info) => {
                if (CombatPhase != "none") Event("incoming", ((Hkmp.Animation.AnimationClip)clip).ToString(), frame);
                orig(mgr, id, clip, frame, info);
            })));
        hooks.Add(new Hook(typeof(PacketManager).GetMethod("HandleClientUpdatePacket"),
            new Action<Action<PacketManager, ClientUpdatePacket>, PacketManager, ClientUpdatePacket>((orig, manager, packet) => {
                lock (gate) {
                    decoded++; lastDecoded = Stopwatch.GetTimestamp();
                    var data = packet.GetPacketData();
                    if (data.ContainsKey(ClientUpdatePacketId.EntityUpdate)) entityPackets++;
                    if (data.ContainsKey(ClientUpdatePacketId.PlayerUpdate)) playerPackets++;
                }
                orig(manager, packet);
            })));
        On.HealthManager.TakeDamage += ObserveHit;
    }

    private void Event(string direction, string clip, int frame) => File.AppendAllText(Path.Combine(root, role + ".attack-events.jsonl"),
        JsonConvert.SerializeObject(new { utc = DateTime.UtcNow, direction, CombatPhase, clip, frame, unityFrame = Time.frameCount }) + "\n");

    private void ObserveHit(On.HealthManager.orig_TakeDamage orig, HealthManager health, HitInstance hit) {
        var before = health.hp;
        orig(health, hit);
        if (health != null && health.name == "Colosseum_Miner(Clone)" && before > health.hp && CombatPhase != "none" && hit.Source != null) {
            ObservedCombatHits.Add(new ObservedCombatHit { phase = CombatPhase, damage = before - health.hp,
                source = hit.Source.name, sourceId = hit.Source.GetInstanceID(), owner = AttackOwnership.RemoteOwner(hit.Source), path = PathOf(hit.Source.transform),
                remote = LocalWorldIsolation.IsRemote(hit.Source), reference = hit.Source.GetComponentInParent<NativeSpellReference>() != null });
        }
        if (health != null && health.name == "Colosseum_Miner(Clone)" && before > health.hp && CombatPhase != "none" && AttackOwnership.RemoteOwner(hit.Source) >= 0) {
            CombatHits++; CombatDamage += before - health.hp;
        }
        if (health != null && health.gameObject.scene.name == "Room_Colosseum_Gold" && (before != health.hp || CombatPhase != "none")) {
            var source = hit.Source;
            File.AppendAllText(Path.Combine(root, role + ".damage.jsonl"), JsonConvert.SerializeObject(new {
                utc = DateTime.UtcNow, frame = Time.frameCount, phase = Phase, target = health.name,
                targetId = health.GetInstanceID(), replica = EntityReferences.IsReplica(health.gameObject),
                before, after = health.hp, source = source == null ? "null" : source.name,
                sourceId = source == null ? 0 : source.GetInstanceID(), attack = hit.AttackType.ToString(), hit.DamageDealt, hit.Multiplier,
                CombatPhase, remoteOwner = AttackOwnership.RemoteOwner(source), sourcePath = source == null ? "" : PathOf(source.transform)
            }) + "\n");
        }
    }
    private static string PathOf(Transform t) => t == null ? "" : PathOf(t.parent) + "/" + t.name;

    private void Update() {
        if (engine == null) return;
        frames++; totalFrameMs += Time.unscaledDeltaTime * 1000; maxFrameMs = Math.Max(maxFrameMs, Time.unscaledDeltaTime * 1000);
        intervalFrameMs = Math.Max(intervalFrameMs, Time.unscaledDeltaTime * 1000);
        if (Time.realtimeSinceStartup < nextSample) return;
        nextSample = Time.realtimeSinceStartup + 1;
        Sample();
    }

    internal void Sample() {
        var entities = EntityReferences.Entities;
        var manager = Field(engine.ClientManager, "_entityManager");
        var congestion = typeof(UdpUpdateManager<ServerUpdatePacket, ServerUpdatePacketId>)
            .GetField("_udpCongestionManager", BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(engine.NetClient.UpdateManager);
        var sent = Field(congestion, "_sentQueue");
        var sentCount = sent == null ? -1 : (int)sent.GetType().GetProperty("Count").GetValue(sent);
        var callbackQueue = (ICollection)Queue.GetValue(null);
        object record;
        lock (gate) {
            record = new {
                utc = DateTime.UtcNow, phase = Phase, connected = engine.NetClient.IsConnected,
                scene = GameManager.instance == null ? "null" : GameManager.instance.sceneName,
                rtt = engine.NetClient.UpdateManager.AverageRtt, sendIntervalMs = engine.NetClient.UpdateManager.CurrentSendRate,
                frames, meanFrameMs = frames == 0 ? 0 : totalFrameMs / frames, maxFrameMs,
                callbacks, maxQueueUs, maxCallbackUs, callbackBacklog = callbackQueue.Count,
                intervalQueueUs, intervalCallbackUs, intervalUpdateUs, intervalFrameMs, droppedFrames, LossEnabled,
                decoded, decodedAgeMs = lastDecoded == 0 ? -1 : Micros(Stopwatch.GetTimestamp() - lastDecoded) / 1000,
                entityPackets, playerPackets, retainedSent = sentCount,
                registered = entities?.Count ?? 0,
                dynamic = entities?.Keys.Count(id => (id >> 63) != 0) ?? 0,
                activeHost = entities?.Values.Count(e => e.Object.Host != null && e.Object.Host.activeInHierarchy) ?? 0,
                activeReplica = entities?.Values.Count(e => e.Object.Client != null && e.Object.Client.activeInHierarchy) ?? 0,
                monoSubscribers = ((Delegate)typeof(MonoBehaviourUtil).GetField("OnUpdateEvent", BindingFlags.Instance | BindingFlags.NonPublic)
                    .GetValue(MonoBehaviourUtil.Instance))?.GetInvocationList().Length ?? 0,
                wire = wire.ToDictionary(p => p.Key, p => new Wire {
                    messages=p.Value.messages, bytes=p.Value.bytes, fragments=p.Value.fragments, fragmented=p.Value.fragmented,
                    maxBytes=p.Value.maxBytes, maxFragments=p.Value.maxFragments })
            };
            intervalQueueUs = intervalCallbackUs = intervalUpdateUs = 0; intervalFrameMs = 0;
            File.WriteAllText(Path.Combine(root, role + ".packet-sizes.json"), JsonConvert.SerializeObject(sizes));
        }
        File.AppendAllText(Path.Combine(root, role + ".metrics.jsonl"), JsonConvert.SerializeObject(record) + "\n");
        File.WriteAllText(Path.Combine(root, role + ".metrics-latest.json"), JsonConvert.SerializeObject(record));
    }

    private void OnDestroy() {
        On.HealthManager.TakeDamage -= ObserveHit;
        foreach (var hook in hooks) hook.Dispose();
    }
}

internal sealed class NativeSpellReference : MonoBehaviour { }
