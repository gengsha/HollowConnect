using System;

namespace HollowConnect.Core;

public sealed partial class Party {
    // Terrain can disappear between the nail's acceptance and the victim's
    // reliable revive event. Return that same episode without fabricating a
    // second death. The exact accepted life version is the one-use proof.
    public bool ReturnUnsafeRescue(ushort id, uint round, uint deathSerial, uint acceptedVersion, string scene) {
        if (Resetting || round != Round || deathSerial == 0 || acceptedVersion == 0 ||
            !Members.TryGetValue(id, out var m) || (m.Life != Life.Alive && m.Life != Life.Loading) ||
            m.DownSerial != deathSerial || m.LifeVersion != acceptedVersion || m.LastRescueVersion != acceptedVersion ||
            string.IsNullOrEmpty(scene) || scene != m.Scene || scene.StartsWith("GG_", StringComparison.Ordinal) ||
            !ValidCheckpoint(m.CheckpointScene, m.CheckpointMarker, m.CheckpointRespawnType)) return false;
        m.LastRescueVersion = 0;
        BeginReturn(m); return true;
    }

    // Only the downed player's client may propose its own replacement. A new
    // coordinate is part of the same death, never a revive or another Down.
    public bool MoveRescueAnchor(ushort id, uint round, uint deathSerial, uint revision,
        string scene, float x, float y, double now) {
        if (Resetting || round != Round || revision == 0 || revision > int.MaxValue ||
            !Members.TryGetValue(id, out var m) || m.Life != Life.Down || !CanBeRescued(m) ||
            m.DownSerial != deathSerial || scene != m.Scene || string.IsNullOrEmpty(scene) ||
            scene.StartsWith("GG_", StringComparison.Ordinal) || revision <= m.RescueAnchorSerial ||
            !Finite(x) || !Finite(y) || Math.Abs(x) > 100000 || Math.Abs(y) > 100000 || now - m.Seen > 2) return false;
        m.X = x; m.Y = y; m.RescueAnchorSerial = revision; m.Seen = now;
        m.LifeVersion++;
        return true;
    }
}
