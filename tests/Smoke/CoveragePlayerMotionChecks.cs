using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Hkmp.Game;
using Hkmp.Game.Client;
using Hkmp.Game.Client.Entity.Component;
using Hkmp.Game.Settings;
using Hkmp.Networking;
using Hkmp.Networking.Packet;
using Hkmp.Networking.Packet.Data;
using Hkmp.Networking.Packet.Update;
using UnityEngine;
using Object = UnityEngine.Object;
using NetVector2 = Hkmp.Math.Vector2;

namespace HollowConnect.Smoke;

// Run after a native hero and in-game fonts exist.
// This exercises the real PlayerManager pool and original-datagram filter, but
// uses its own avatars: no live peer, hero, save, or network session is changed.
internal static class CoveragePlayerMotionChecks {
    private static T Field<T>(object instance, string name) => (T)instance.GetType()
        .GetField(name, BindingFlags.Instance | BindingFlags.NonPublic).GetValue(instance);

    internal static IEnumerator Run(Action<string> log) {
        void Require(bool value, string label) {
            if (!value) throw new Exception("Player motion: " + label);
            log("PASS player motion " + label);
        }
        PacketOrderingAssertions(Require);
        Require(HeroController.instance != null && Time.timeScale > 0, "native hero is ready");
        var players = new Dictionary<ushort, ClientPlayerData>();
        var manager = new PlayerManager(new ServerSettings { IsPvpEnabled = false }, players);
        var skin = Field<object>(manager, "_skinManager");
        skin.GetType().GetMethod("StoreDefaultPlayerSkin", BindingFlags.Instance | BindingFlags.NonPublic)
            .Invoke(skin, new object[] { HeroController.instance });
        typeof(PlayerManager).GetMethod("TryCreatePlayerPool", BindingFlags.Instance | BindingFlags.NonPublic)
            .Invoke(manager, null);
        var prefab = Field<GameObject>(manager, "_playerContainerPrefab");
        var pool = Field<Queue<GameObject>>(manager, "_inactivePlayers");
        var created = pool.ToArray();
        var parent = new GameObject("HollowConnect player motion world fixture");
        // Keep a single item in this private pool so recycle really reuses the
        // same instance, instead of passing on a different unoccupied avatar.
        pool.Clear(); pool.Enqueue(created[0]);
        var origin = new Vector3(5000, 5000, 0);
        var oldFrameRate = Application.targetFrameRate;
        var oldVsync = QualitySettings.vSyncCount;
        QualitySettings.vSyncCount = 0; Application.targetFrameRate = 144;
        ClientPlayerData Spawn(ushort id, Vector3 position) {
            var data = new ClientPlayerData(id, "motion-fixture") { IsInLocalScene = true };
            players[id] = data;
            manager.SpawnPlayer(data, data.Username, new NetVector2(position.x, position.y), true, Team.None, 0);
            return data;
        }
        void Update(ushort id, Vector3 position) => manager.UpdatePosition(id, new NetVector2(position.x, position.y));
        bool Near(Vector3 actual, Vector3 expected) => Vector3.Distance(actual, expected) < .025f;
        try {
            var data = Spawn(65000, origin);
            var container = data.PlayerContainer;
            Require(Near(container.transform.position, origin), "spawn is immediate at the authoritative world position");
            parent.transform.position = new Vector3(7, 11, 0);
            parent.transform.rotation = Quaternion.Euler(0, 0, 13);
            parent.transform.localScale = new Vector3(1.2f, .8f, 1);
            container.transform.SetParent(parent.transform, true);
            // Constructing a full native 64-avatar pool takes longer than a
            // packet gap. Let that startup frame finish before measuring cadence.
            yield return new WaitForSecondsRealtime(.35f);
            foreach (var interval in new[] { .017f, .05f, .1f }) {
                container.GetComponent<Hkmp.Fsm.PositionInterpolation>().SetNewPosition(origin, true);
                var start = Time.time;
                var next = start + interval;
                var index = 0; var samples = 0; var frames = 0; var moving = 0;
                var before = container.transform.position; var renderedDelta = Time.deltaTime;
                var maxStep = 0f; var maxSpeed = 0f;
                var jitter = new[] { .75f, 1.25f, 1f, .9f, 1.1f };
                var latest = origin;
                while (Time.time - start < .85f) {
                    if (Time.time >= next) {
                        latest = origin + Vector3.right * (6 * (Time.time - start));
                        var prePacket = container.transform.position;
                        Update(data.Id, latest); samples++;
                        if (!Near(prePacket, container.transform.position)) {
                            var interpolation = container.GetComponent<Hkmp.Fsm.PositionInterpolation>();
                            log("OBSERVE unexpected packet transform before=" + prePacket + " after=" + container.transform.position +
                                " active=" + container.activeInHierarchy + " smooth=" + interpolation.Smooth + " world=" + interpolation.WorldSpace +
                                " predict=" + interpolation.PredictPlayer + " elapsed=" + (Time.time - start));
                            Require(false, "cadence " + interval + " packet directly moved the visual transform");
                        }
                        next = Time.time + interval * jitter[index++ % jitter.Length];
                    }
                    yield return null;
                    var current = container.transform.position;
                    var step = current.x - before.x;
                    // A null coroutine resumes before LateUpdate, so this
                    // observed transform belongs to the preceding frame.
                    var dt = renderedDelta;
                    if (Time.time - start > .16f) {
                        frames++; if (step > .001f) moving++;
                        maxStep = Mathf.Max(maxStep, Mathf.Abs(step));
                        if (dt > 0) maxSpeed = Mathf.Max(maxSpeed, Mathf.Abs(step) / dt);
                        if (step < -.015f || Math.Abs(current.y - origin.y) >= .025f)
                            Require(false, "cadence " + interval + " failed forward world-coordinate movement");
                    }
                    before = current; renderedDelta = Time.deltaTime;
                }
                log("OBSERVE player cadence=" + interval + " samples=" + samples + " frames=" + frames +
                    " movingFrames=" + moving + " maxStep=" + maxStep + " maxSpeed=" + maxSpeed);
                Require(samples >= 5 && frames >= 12 && moving >= frames * .8f && maxSpeed < 20f,
                    "jittered " + interval + " packets produce continuous intermediate rendered movement");
                yield return new WaitForSecondsRealtime(.32f);
                Require(Near(container.transform.position, latest), "cadence " + interval + " stops and converges to the last real packet");
                yield return new WaitForSecondsRealtime(.1f);
                Require(Near(container.transform.position, latest), "cadence " + interval + " remains stationary after convergence");
            }
            container.GetComponent<Hkmp.Fsm.PositionInterpolation>().SetNewPosition(origin, true);
            Update(data.Id, origin);
            yield return new WaitForSecondsRealtime(.05f);
            var target = origin + Vector3.right * .5f;
            Update(data.Id, target);
            Require(Vector3.Distance(container.transform.position, target) > .2f,
                "ordinary packet does not bypass interpolation with an immediate world write");
            var intermediateFrames = 0;
            var changes = 0;
            var worldYStayedCorrect = true;
            var previous = container.transform.position;
            var deadline = Time.realtimeSinceStartup + .14f;
            while (Time.realtimeSinceStartup < deadline) {
                yield return null;
                var current = container.transform.position;
                if (current.x > origin.x + .05f && current.x < target.x - .05f) intermediateFrames++;
                if (Math.Abs(current.x - previous.x) > .002f) changes++;
                worldYStayedCorrect &= Math.Abs(current.y - origin.y) < .025f;
                previous = current;
            }
            Require(worldYStayedCorrect, "world coordinates survive parent translation, rotation and scale");
            Require(intermediateFrames >= 2 && changes >= 2,
                "actual Unity frames interpolate sparse packets at multiple intermediate positions");
            yield return new WaitForSecondsRealtime(.2f);
            Require(Near(container.transform.position, target), "stationary player settles without continuing past the last sample");
            var teleport = origin + new Vector3(24, 8, 0);
            Update(data.Id, teleport);
            Require(Near(container.transform.position, teleport), "F8-sized displacement snaps immediately");
            yield return new WaitForSecondsRealtime(.12f);
            Require(Near(container.transform.position, teleport), "teleport discards the previous movement trail");
            yield return new WaitForSecondsRealtime(.28f);
            var resumed = teleport + Vector3.right;
            Update(data.Id, resumed);
            Require(Near(container.transform.position, resumed), "a long receive gap discards stale interpolation history");
            // Leave a movement sample pending when the avatar is recycled.
            Update(data.Id, resumed + Vector3.right);
            manager.RecyclePlayer(data.Id); players.Remove(data.Id);
            Require(!container.activeSelf, "leaving the room disables the old avatar");
            var replacement = Spawn(65001, origin + new Vector3(-20, 5, 0));
            Require(replacement.PlayerContainer == container, "the same pooled instance is reused for another player");
            var spawn = replacement.PlayerContainer.transform.position;
            yield return new WaitForSecondsRealtime(.14f);
            Require(Near(spawn, origin + new Vector3(-20, 5, 0)) && Near(container.transform.position, spawn),
                "a recycled avatar starts at the new spawn without replaying the previous peer's history");
        } finally {
            Application.targetFrameRate = oldFrameRate; QualitySettings.vSyncCount = oldVsync;
            manager.RecycleAllPlayers();
            foreach (var gameObject in created) if (gameObject != null) Object.Destroy(gameObject);
            if (prefab != null) Object.Destroy(prefab);
            Object.Destroy(parent);
        }
    }

    // The production filter already receives the unwrapped original UDP
    // revision, so adding player fields there need not change the wire format.
    // AnimationInfo is currently unreliable; retaining it here does not claim
    // to introduce retransmission. Reliable side events must also survive.
    private static void PacketOrderingAssertions(Action<bool, string> require) {
        foreach (var newestSequence in new ushort[] { 102, 0 }) {
            var origins = new SequenceWindow();
            var snapshots = new EntitySnapshotHistory();
            ClientUpdatePacket Packet(ushort sequence, ushort id, float x, bool scale = false,
                bool includeScale = false, bool map = false, uint visit = 5) {
                var packet = new ClientUpdatePacket { Sequence = sequence };
                var player = new PlayerUpdate { Id = id, SceneName = "Fixture", Visit = 3, Epoch = 2,
                    PlayerVisit = visit, Position = new NetVector2(x, 0), Scale = scale, MapPosition = new NetVector2(x, 1) };
                player.UpdateTypes.UnionWith(new[] { PlayerUpdateType.Position, PlayerUpdateType.Animation });
                if (includeScale) player.UpdateTypes.Add(PlayerUpdateType.Scale);
                if (map) player.UpdateTypes.Add(PlayerUpdateType.MapPosition);
                player.AnimationInfos.Add(new AnimationInfo { ClipId = 7, Frame = (byte)x });
                var players = new PacketDataCollection<PlayerUpdate>(); players.DataInstances.Add(player);
                packet.SetSendingPacketData(ClientUpdatePacketId.PlayerUpdate, players);
                var action = new EntityNetworkData(); action.Packet.Write((byte)x);
                var entity = new ReliableEntityUpdate { Id = 17 };
                entity.UpdateTypes.Add(EntityUpdateType.Data); entity.GenericData.Add(action);
                var entities = new PacketDataCollection<ReliableEntityUpdate>(); entities.DataInstances.Add(entity);
                packet.SetSendingPacketData(ClientUpdatePacketId.ReliableEntityUpdate, entities);
                return packet;
            }
            Dictionary<ClientUpdatePacketId, IPacketData> Deliver(ClientUpdatePacket outgoing) {
                var bytes = new Packet(); outgoing.CreatePacket(bytes);
                var incoming = new ClientUpdatePacket();
                require(incoming.ReadPacket(PacketManager.ReadDatagram(bytes.ToArray())), "original player datagram decodes");
                incoming.FilterReceivedData(origins, snapshots);
                return incoming.GetPacketData();
            }
            PlayerUpdate Player(Dictionary<ClientUpdatePacketId, IPacketData> packet) =>
                ((RawPacketDataCollection)packet[ClientUpdatePacketId.PlayerUpdate]).DataInstances.Cast<PlayerUpdate>().Single();
            var latest = Player(Deliver(Packet(newestSequence, 1, 3, true, true, true)));
            require(latest.UpdateTypes.Contains(PlayerUpdateType.Position), "newest player position is accepted");
            var olderPacket = Deliver(Packet(unchecked((ushort)(newestSequence - 1)), 1, 2, false, true, true));
            var older = Player(olderPacket);
            require(!older.UpdateTypes.Contains(PlayerUpdateType.Position) && !older.UpdateTypes.Contains(PlayerUpdateType.Scale) &&
                !older.UpdateTypes.Contains(PlayerUpdateType.MapPosition), "out-of-order player position, facing and map position cannot roll back newer fields");
            require(older.UpdateTypes.Contains(PlayerUpdateType.Animation) && older.AnimationInfos.Single().Frame == 2,
                "rejecting stale movement preserves its independent animation data");
            var action = ((RawPacketDataCollection)olderPacket[ClientUpdatePacketId.ReliableEntityUpdate])
                .DataInstances.Cast<ReliableEntityUpdate>().Single();
            require(action.GenericData.Single().Packet.ReadByte() == 2, "rejecting stale movement preserves a reliable side event");
            var unrelated = Player(Deliver(Packet(unchecked((ushort)(newestSequence - 2)), 2, 1, false, true)));
            require(unrelated.UpdateTypes.Contains(PlayerUpdateType.Position) && unrelated.UpdateTypes.Contains(PlayerUpdateType.Scale),
                "a newer peer update does not suppress another player's first sparse fields");
            var newVisit = Player(Deliver(Packet(unchecked((ushort)(newestSequence + 1)), 1, 4, visit: 6)));
            require(newVisit.UpdateTypes.Contains(PlayerUpdateType.Position), "a new source room visit has its own movement history");
            var sparse = Player(Deliver(Packet(unchecked((ushort)(newestSequence - 4)), 1, 0,
                includeScale: true, map: true, visit: 6)));
            require(!sparse.UpdateTypes.Contains(PlayerUpdateType.Position) && sparse.UpdateTypes.Contains(PlayerUpdateType.Scale) &&
                sparse.UpdateTypes.Contains(PlayerUpdateType.MapPosition), "rejecting stale movement keeps sparse fields absent from the newer update");
            // A carrier must still deliver the reliable event from an old
            // origin; unreliable player motion should never be copied into it.
            var lost = Packet(unchecked((ushort)(newestSequence - 3)), 1, 0);
            var carrier = new ClientUpdatePacket { Sequence = unchecked((ushort)(newestSequence + 2)) };
            carrier.SetLostReliableData(lost);
            var resent = Deliver(carrier);
            require(!resent.ContainsKey(ClientUpdatePacketId.PlayerUpdate) &&
                ((RawPacketDataCollection)resent[ClientUpdatePacketId.ReliableEntityUpdate]).DataInstances.Count == 1,
                "a reliable resend keeps its side event without replaying unreliable player movement");
        }
    }
}
