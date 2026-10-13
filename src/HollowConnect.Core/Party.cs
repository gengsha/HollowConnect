using System;
using System.Collections.Generic;
using System.Linq;

namespace HollowConnect.Core;

public enum Life { Loading, Alive, Down, Resetting, Returning }
public sealed class Member {
    public ushort Id;
    public string Name = "", Scene = "", CheckpointScene = "", CheckpointMarker = "";
    public string ReturnScene = "", ReturnMarker = "";
    public int CheckpointRespawnType, ReturnRespawnType;
    public Life Life;
    public uint Sequence, DownSerial, LifeVersion;
    public float X, Y, MapX, MapY;
    public bool HasMap, ResetReady;
    public double Seen;
}

// Pure server state, called only from the server's Unity update queue.
public sealed class Party {
    public string Session { get; } = Guid.NewGuid().ToString("N");
    public uint Round { get; private set; }
    public bool Resetting { get; private set; }
    public readonly Dictionary<ushort, Member> Members = new Dictionary<ushort, Member>();
    public void Join(ushort id, string name) {
        if (!Members.ContainsKey(id)) Members[id] = new Member { Id = id, Name = name, Life = Life.Loading };
    }
    public void Leave(ushort id) => Members.Remove(id);
    public bool Presence(ushort id, uint round, uint sequence, string scene, float x, float y,
        bool ready, bool hasMap, float mx, float my, double now) {
        if (round != Round || !Members.TryGetValue(id, out var m) || sequence <= m.Sequence ||
            string.IsNullOrEmpty(scene) || scene.Length > 128 || !Finite(x) || !Finite(y)) return false;
        m.Sequence = sequence; m.Seen = now;
        // Down's reliable coordinates are the death anchor. A heartbeat sent
        // just before death may arrive later, even from the preceding room.
        if (m.Life != Life.Down) { m.Scene = scene; m.X = x; m.Y = y; }
        if (m.Life != Life.Down || scene == m.Scene && x == m.X && y == m.Y) {
            m.HasMap = ready && hasMap && Finite(mx) && Finite(my);
            m.MapX = mx; m.MapY = my;
        }
        if (m.Life == Life.Returning) m.HasMap = false;
        // A delayed Alive heartbeat cannot resurrect a downed player.
        if (!Resetting && m.Life != Life.Down && m.Life != Life.Returning) m.Life = ready ? Life.Alive : Life.Loading;
        return true;
    }
    public bool Down(ushort id, uint round, uint serial, string scene, float x, float y, double now) {
        if (Resetting || round != Round || !Members.TryGetValue(id, out var m) || serial <= m.DownSerial ||
            m.Life == Life.Down || m.Life == Life.Returning || string.IsNullOrEmpty(scene) || scene.Length > 128 || !Finite(x) || !Finite(y)) return false;
        if (m.Scene != scene) m.HasMap = false;
        m.Scene = scene; m.X = x; m.Y = y; m.Seen = now;
        m.DownSerial = serial; m.Life = Life.Down; m.LifeVersion++; return true;
    }
    public bool CanRescue(ushort rescuer, ushort target, uint round, uint targetSerial, double now) {
        if (Resetting || round != Round || rescuer == target || !Members.TryGetValue(rescuer, out var a) ||
            !Members.TryGetValue(target, out var b) || a.Life != Life.Alive || b.Life != Life.Down ||
            b.DownSerial != targetSerial || a.Scene != b.Scene || now - a.Seen > 2 || now - b.Seen > 2) return false;
        var dx = a.X - b.X; var dy = a.Y - b.Y;
        // The client requires a physical nail collision. This envelope also
        // accommodates Longnail/Mark of Pride and a recent moving heartbeat.
        return dx * dx + dy * dy <= 5f * 5f;
    }
    public bool Revive(ushort rescuer, ushort target, uint round, uint serial, double now) {
        if (!CanRescue(rescuer, target, round, serial, now)) return false;
        Members[target].Life = Life.Alive; Members[target].LifeVersion++; return true;
    }
    public bool GiveUp(ushort id, uint round, uint serial) {
        if (Resetting || round != Round || !Members.TryGetValue(id, out var m) ||
            m.Life != Life.Down || m.DownSerial != serial || Members.Values.All(p => p.Life == Life.Down) ||
            !ValidCheckpoint(m.CheckpointScene, m.CheckpointMarker, m.CheckpointRespawnType)) return false;
        m.ReturnScene = m.CheckpointScene; m.ReturnMarker = m.CheckpointMarker;
        m.ReturnRespawnType = m.CheckpointRespawnType;
        m.Life = Life.Returning; m.LifeVersion++; m.HasMap = false;
        return true;
    }
    public bool FinishReturn(ushort id, uint round, uint serial, uint sequence, string scene, float x, float y, double now) {
        if (Resetting || round != Round || !Members.TryGetValue(id, out var m) || m.Life != Life.Returning ||
            m.DownSerial != serial || string.IsNullOrEmpty(scene) || scene.Length > 128 ||
            m.ReturnScene.Length != 0 && scene != m.ReturnScene || !Finite(x) || !Finite(y)) return false;
        m.Life = Life.Alive; m.LifeVersion++; m.Scene = scene; m.X = x; m.Y = y; m.Seen = now;
        // Old heartbeats from the abandoned room must not overwrite this arrival.
        m.Sequence = Math.Max(m.Sequence, sequence);
        return true;
    }
    public bool BeginWipe() {
        if (Resetting || Members.Count == 0 || Members.Values.Any(m => m.Life != Life.Down)) return false;
        Round++; Resetting = true;
        foreach (var m in Members.Values) {
            m.ReturnScene = m.CheckpointScene; m.ReturnMarker = m.CheckpointMarker;
            m.ReturnRespawnType = m.CheckpointRespawnType;
            m.Life = Life.Resetting; m.ResetReady = false; m.HasMap = false;
        }
        return true;
    }
    public bool Ready(ushort id, uint round, string scene) {
        if (!Resetting || round != Round || !Members.TryGetValue(id, out var m) || string.IsNullOrEmpty(scene) || scene.Length > 128 ||
            m.ReturnScene.Length != 0 && m.ReturnScene != scene) return false;
        m.Scene = scene;
        m.ResetReady = true; return true;
    }
    public bool TryRelease() {
        if (!Resetting || Members.Count == 0 || Members.Values.Any(m => !m.ResetReady)) return false;
        Resetting = false;
        foreach (var m in Members.Values) m.Life = Life.Alive;
        return true;
    }
    public static bool ValidCheckpoint(string scene, string marker, int type) =>
        scene != null && marker != null && scene.Length <= 128 && marker.Length <= 128 && type >= 0 && type <= 2 &&
        (scene.Length != 0 || type == 2);
    public static bool Finite(float f) => !float.IsNaN(f) && !float.IsInfinity(f) && Math.Abs(f) < 1000000;
}
