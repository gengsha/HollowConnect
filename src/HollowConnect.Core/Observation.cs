using System.Linq;

namespace HollowConnect.Core;

public sealed partial class Party {
    public static bool Observer(Life life) => life == Life.Observing || life == Life.ObservationReturning;
    public bool Observe(ushort id, ushort target, uint round, uint serial, string scene, double now) {
        if (Resetting || round != Round || serial == 0 || id == target || !Members.TryGetValue(id, out var m)) return false;
        if (m.Life == Life.Observing && m.ObservationSerial == serial && m.Scene == scene) return true;
        if (serial <= m.ObservationSerial || (m.Life != Life.Alive && m.Life != Life.Loading) || m.Scene != scene ||
            now < m.Seen || now - m.Seen >= 2 || !Members.TryGetValue(target, out var fighter) ||
            !ObservationFighter(fighter, scene, now)) return false;
        m.ObservationSerial = serial; m.Life = Life.Observing; m.HasMap = false; m.InBossBattle = false; m.LifeVersion++;
        return true;
    }
    private static bool ObservationFighter(Member m, string scene, double now) =>
        (m.Life == Life.Alive || m.Life == Life.Loading) && m.Scene == scene && now >= m.Seen && now - m.Seen < 2;
    public bool ObservationHasFighter(Member observer, double now) =>
        Members.Values.Any(m => m.Id != observer.Id && ObservationFighter(m, observer.Scene, now));
    public bool ReturnObservation(ushort id, uint round, uint serial) {
        if (Resetting || round != Round || !Members.TryGetValue(id, out var m) || !Observer(m.Life) || m.ObservationSerial != serial) return false;
        if (m.Life == Life.ObservationReturning) return true;
        m.Life = Life.ObservationReturning; m.HasMap = false; m.InBossBattle = false; m.LifeVersion++;
        m.ReturnScene = "Town"; m.ReturnMarker = "RestBench"; m.ReturnRespawnType = 1;
        return true;
    }
    public bool FinishObservation(ushort id, uint round, uint serial, uint sequence, string scene, float x, float y, double now) {
        if (Resetting || round != Round || !Members.TryGetValue(id, out var m) || m.ObservationSerial != serial ||
            m.Life != Life.ObservationReturning || scene != "Town" || !Finite(x) || !Finite(y)) return false;
        m.Life = Life.Alive; m.LifeVersion++; m.Scene = scene; m.X = x; m.Y = y; m.Seen = now;
        m.Sequence = System.Math.Max(m.Sequence, sequence); m.InBossBattle = false;
        m.CheckpointScene = "Town"; m.CheckpointMarker = "RestBench"; m.CheckpointRespawnType = 1;
        return true;
    }
}
