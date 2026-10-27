using System;
using System.Collections.Generic;
using System.Linq;
using Hkmp.Networking.Client;
using Hkmp.Networking.Packet.Data;
using Hkmp.Util;
using Modding;
using UnityEngine;

namespace Hkmp.Game.Client.Entity.Component;

// Platform entities own their visual/collider snapshots, but their scene
// containers and phase camera configuration live outside the boss hierarchy.
// Persist those native values as well: one-shot APPEAR/activation events cannot
// initialize a player who first enters after the event has already happened.
internal sealed class RadianceArenaComponent : EntityComponent {
    private readonly Transform arena;
    private readonly Dictionary<string, GameObject> targets = new();
    private readonly Dictionary<string, bool> previous = new();
    private Dictionary<string, bool> received;
    private float next;
    private bool cameraPending;
    private readonly PlayMakerFSM intro;
    private bool previousStarted, receivedStarted, introFinished;

    public RadianceArenaComponent(NetClient client, ulong id, HostClientPair<GameObject> objects) : base(client, id, objects) {
        arena = objects.Host == null ? null : objects.Host.transform.parent;
        intro = arena == null ? null : arena.gameObject.LocateMyFSM("Control");
        Bind();
        MonoBehaviourUtil.Instance.OnUpdateEvent += Tick;
    }

    private void Bind() {
        if (arena == null) return;
        void Add(Transform t) {
            if (t != null) targets[EntityIdentity.Path(t, arena)] = t.gameObject;
        }
        var platforms = arena.Find("Plat Sets");
        if (platforms != null) {
            Add(platforms);
            // Container activation affects all independently registered
            // platform replicas after CombatState restores their parents.
            foreach (Transform group in platforms)
                if (group.GetComponent<CameraLockArea>() == null && !group.name.StartsWith("Radiant Plat", StringComparison.Ordinal)) Add(group);
        }
        var cameras = arena.Find("CamLocks");
        if (cameras != null) {
            foreach (Transform group in cameras) {
                // Intro and ending cameras belong to each player's native
                // presentation. Only battle-phase availability is shared.
                if (group.name.Contains("Intro") || group.name.Contains("Death") || group.name.Contains("Challenge")) continue;
                Add(group);
            }
        }
    }

    private void Tick() {
        if (arena == null || CoopContext.Resetting || Time.unscaledTime < next) return;
        next = Time.unscaledTime + .05f;
        if (targets.Count == 0) Bind();
        if (IsControlled) {
            // A normal late entrance has its own intro too. Finish only its
            // local presentation once the authority has started the fight.
            if (receivedStarted && !introFinished && CoopContext.FinishRadianceIntro?.Invoke(arena.gameObject.scene.name) == true) {
                introFinished = true;
                cameraPending = true;
            }
            Apply();
            RefreshCamera();
            return;
        }
        var count = 0;
        var started = intro != null && (intro.ActiveStateName == "Battle Start" || intro.ActiveStateName == "Refight");
        var changed = started != previousStarted;
        foreach (var pair in targets) {
            if (pair.Value == null) continue;
            count++;
            if (!previous.TryGetValue(pair.Key, out var value) || value != pair.Value.activeSelf) changed = true;
        }
        if (count == 0 || count > 64 || !changed && previous.Count == count) return;
        var data = new EntityNetworkData { Type = EntityComponentType.RadianceArena };
        data.Packet.Write(started);
        data.Packet.Write((byte)count);
        previousStarted = started;
        previous.Clear();
        foreach (var pair in targets) {
            if (pair.Value == null) continue;
            var active = pair.Value.activeSelf;
            data.Packet.Write(pair.Key); data.Packet.Write(active);
            previous[pair.Key] = active;
        }
        SendData(data);
    }

    private void Apply() {
        if (received == null) return;
        foreach (var pair in received) {
            if (!targets.TryGetValue(pair.Key, out var go) || go == null || go.activeSelf == pair.Value) continue;
            go.SetActive(pair.Value);
            if (go.transform.parent?.name == "CamLocks") cameraPending = true;
        }
    }

    private void RefreshCamera() {
        if (!cameraPending) return;
        var hero = HeroController.instance;
        var camera = GameCameras.instance?.cameraController;
        if (hero == null || camera == null || !hero.acceptingInput || hero.controlReqlinquished ||
            hero.cState.hazardDeath || hero.cState.hazardRespawning || camera.mode == CameraController.CameraMode.FROZEN) return;
        // A disabled lock can remain in CameraController's list after a warp.
        // Reconcile once at this hero's position. Never copy the authority's
        // position/current lock, run a boss FSM, or start repeated camera tweens.
        var locks = ReflectionHelper.GetField<CameraController, List<CameraLockArea>>(camera, "lockZoneList");
        if (locks != null)
            foreach (var area in locks.ToArray()) if (area != null) camera.ReleaseLock(area);
        var areas = arena.GetComponentsInChildren<CameraLockArea>(false).Where(a => a.isActiveAndEnabled &&
            a.GetComponents<Collider2D>().Any(c => c.enabled && c.OverlapPoint(hero.transform.position)))
            .OrderBy(a => a.maxPriority).ToArray();
        foreach (var area in areas) camera.LockToArea(area);
        camera.SetMode(areas.Length == 0 ? CameraController.CameraMode.FOLLOWING : CameraController.CameraMode.LOCKED);
        cameraPending = false;
    }

    public override void InitializeHost() { previous.Clear(); received = null; cameraPending = false; receivedStarted = introFinished = previousStarted = false; next = 0; }
    public override void Update(EntityNetworkData data, bool snapshot) {
        if (!IsControlled) return;
        receivedStarted = data.Packet.ReadBool();
        var count = data.Packet.ReadByte();
        if (count > 64) throw new FormatException("Too many Radiance arena targets");
        var values = new Dictionary<string, bool>();
        for (var i = 0; i < count; i++) {
            var key = data.Packet.ReadString();
            if (key.Length > 1024 || values.ContainsKey(key)) throw new FormatException("Invalid Radiance arena target");
            values.Add(key, data.Packet.ReadBool());
        }
        received = values;
        Apply();
    }
    public override void Destroy() => MonoBehaviourUtil.Instance.OnUpdateEvent -= Tick;
}
