using System;
using System.Collections.Generic;
using System.Linq;

namespace HollowConnect.Core;

public enum Life { Loading, Alive, Down, Resetting, Returning, Spectating, Observing, ObservationReturning }
public sealed class Member {
    public ushort Id;
    public string Name = "", Scene = "", CheckpointScene = "", CheckpointMarker = "";
    public string ReturnScene = "", ReturnMarker = "";
    public int CheckpointRespawnType, ReturnRespawnType;
    public Life Life;
    public uint Sequence, DownSerial, LifeVersion, RescueAnchorSerial, LastRescueVersion;
    public float X, Y, MapX, MapY;
    public bool HasMap, ResetReady;
    public bool InBossBattle;
    public int RescuesUsed;
    public uint RestSerial;
    public uint ObservationSerial;
    public double Seen;
}

// Pure server state, called only from the server's Unity update queue.
public sealed partial class Party {
    public string Session { get; } = Guid.NewGuid().ToString("N");
    public uint Round { get; private set; }
    public bool Resetting { get; private set; }
    public readonly Dictionary<ushort, Member> Members = new Dictionary<ushort, Member>();
    public readonly DeathStatistics Deaths = new DeathStatistics();
    private readonly Dictionary<string, int> departedRescues = new Dictionary<string, int>(StringComparer.Ordinal);
    public void Join(ushort id, string name) {
        if (!Members.ContainsKey(id)) Members[id] = new Member { Id = id, Name = name, Life = Life.Loading,
            RescuesUsed = departedRescues.TryGetValue(name, out var used) ? used : 0 };
    }
    public void Leave(ushort id) {
        if (Members.TryGetValue(id, out var member)) departedRescues[member.Name] = member.RescuesUsed;
        Members.Remove(id);
    }
    public bool RecordDeaths(ushort id, uint count) {
        return Members.TryGetValue(id, out var member) && Deaths.Merge(member.Name, count);
    }
    public bool Presence(ushort id, uint round, uint sequence, string scene, float x, float y,
        bool ready, bool hasMap, float mx, float my, double now, bool inBossBattle = false) {
        if (round != Round || !Members.TryGetValue(id, out var m) || sequence <= m.Sequence ||
            string.IsNullOrEmpty(scene) || scene.Length > 128 || !Finite(x) || !Finite(y)) return false;
        m.Sequence = sequence; m.Seen = now;
        // Down's reliable coordinates are the death anchor. A heartbeat sent
        // just before death may arrive later, even from the preceding room.
        if (!Defeated(m.Life) && !Observer(m.Life)) { m.Scene = scene; m.X = x; m.Y = y; m.InBossBattle = inBossBattle; }
        if (!Defeated(m.Life) || scene == m.Scene && x == m.X && y == m.Y) {
            // Map projection and teleport readiness are independent. Native
            // shops, benches and cutscenes can temporarily relinquish input
            // while the player's last valid map position remains useful to
            // teammates. IsReady is retained for warp authorization only.
            m.HasMap = hasMap && Finite(mx) && Finite(my);
            m.MapX = mx; m.MapY = my;
        }
        if (m.Life == Life.Returning || m.Life == Life.Spectating || Observer(m.Life)) m.HasMap = false;
        // A delayed Alive heartbeat cannot resurrect a downed player.
        if (!Resetting && !Defeated(m.Life) && m.Life != Life.Returning && !Observer(m.Life))
            m.Life = ready ? Life.Alive : Life.Loading;
        return true;
    }
    public bool Down(ushort id, uint round, uint serial, string scene, float x, float y, double now, bool inBossBattle = false) {
        if (Resetting || round != Round || !Members.TryGetValue(id, out var m) || serial <= m.DownSerial ||
            Defeated(m.Life) || Observer(m.Life) || m.Life == Life.Returning || string.IsNullOrEmpty(scene) || scene.Length > 128 || !Finite(x) || !Finite(y)) return false;
        if (m.Scene != scene) { m.HasMap = false; m.InBossBattle = false; }
        m.Scene = scene; m.X = x; m.Y = y; m.Seen = now;
        m.InBossBattle = inBossBattle || m.InBossBattle;
        m.DownSerial = serial; m.RescueAnchorSerial = m.LastRescueVersion = 0;
        m.Life = !CanBeRescued(m) && SpectateAfterExhaustion && m.InBossBattle ? Life.Spectating : Life.Down;
        m.LifeVersion++; return true;
    }
    public bool CanRescue(ushort rescuer, ushort target, uint round, uint targetSerial, double now) {
        if (Resetting || round != Round || rescuer == target || !Members.TryGetValue(rescuer, out var a) ||
            !Members.TryGetValue(target, out var b) || a.Life != Life.Alive || b.Life != Life.Down ||
            !CanBeRescued(b) || b.DownSerial != targetSerial || a.Scene != b.Scene || now - a.Seen > 2 || now - b.Seen > 2) return false;
        var dx = a.X - b.X; var dy = a.Y - b.Y;
        // The client requires a physical nail collision. This envelope also
        // accommodates Longnail/Mark of Pride and a recent moving heartbeat.
        return dx * dx + dy * dy <= 5f * 5f;
    }
    public bool Revive(ushort rescuer, ushort target, uint round, uint serial, double now) {
        if (!CanRescue(rescuer, target, round, serial, now)) return false;
        Members[target].RescuesUsed++; Members[target].Life = Life.Alive; Members[target].LifeVersion++;
        Members[target].LastRescueVersion = Members[target].LifeVersion; return true;
    }
    public bool ReviveAfterBoss(ushort id, uint round, uint serial, string scene, ushort survivor, double now) {
        if (Resetting || round != Round || serial == 0 || !Members.TryGetValue(id, out var member) ||
            !Defeated(member.Life) || member.DownSerial != serial || member.Scene != scene || survivor == id ||
            !Members.TryGetValue(survivor, out var living) || (living.Life != Life.Alive && living.Life != Life.Loading) ||
            living.Scene != scene || now < living.Seen || now - living.Seen >= 2 || !Finite(living.X) || !Finite(living.Y)) return false;
        // Settlement is independent of the nail-rescue budget. The location
        // belongs to a surviving participant, never to an inaccessible shade.
        member.X = living.X; member.Y = living.Y; member.Seen = now;
        member.Life = Life.Alive; member.LifeVersion++;
        member.LastRescueVersion = member.LifeVersion; return true;
    }
    public bool GiveUp(ushort id, uint round, uint serial) {
        if (Resetting || round != Round || !Members.TryGetValue(id, out var m) ||
            !Defeated(m.Life) || m.DownSerial != serial || Members.Values.All(p => Defeated(p.Life)) ||
            !ValidCheckpoint(m.CheckpointScene, m.CheckpointMarker, m.CheckpointRespawnType)) return false;
        BeginReturn(m);
        return true;
    }
    public bool FinishReturn(ushort id, uint round, uint serial, uint sequence, string scene, float x, float y, double now) {
        if (Resetting || round != Round || !Members.TryGetValue(id, out var m) || m.Life != Life.Returning ||
            m.DownSerial != serial || string.IsNullOrEmpty(scene) || scene.Length > 128 ||
            m.ReturnScene.Length != 0 && scene != m.ReturnScene || !Finite(x) || !Finite(y)) return false;
        m.Life = Life.Alive; m.LifeVersion++; m.Scene = scene; m.X = x; m.Y = y; m.Seen = now; m.InBossBattle = false;
        // Old heartbeats from the abandoned room must not overwrite this arrival.
        m.Sequence = Math.Max(m.Sequence, sequence);
        return true;
    }
    public bool BeginWipe() {
        if (Resetting || !Members.Values.Any(m => Defeated(m.Life)) || Members.Values.Any(m => !Defeated(m.Life) && !Observer(m.Life))) return false;
        Round++; Resetting = true;
        foreach (var m in Members.Values) {
            m.ReturnScene = Observer(m.Life) ? "Town" : m.CheckpointScene;
            m.ReturnMarker = Observer(m.Life) ? "RestBench" : m.CheckpointMarker;
            m.ReturnRespawnType = Observer(m.Life) ? 1 : m.CheckpointRespawnType;
            m.Life = Life.Resetting; m.ResetReady = false; m.HasMap = false;
        }
        return true;
    }
    public bool Ready(ushort id, uint round, string scene) {
        if (!Resetting || round != Round || !Members.TryGetValue(id, out var m) || string.IsNullOrEmpty(scene) || scene.Length > 128 ||
            m.ReturnScene.Length != 0 && m.ReturnScene != scene) return false;
        m.Scene = scene;
        m.InBossBattle = false;
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
