using System;
using System.Linq;

namespace HollowConnect.Core;

public sealed partial class Party {
    public bool RescueEnabled { get; set; } = true;
    // Zero keeps the existing unlimited rescue policy.
    public int RescueLimit { get; set; }
    public bool SpectateAfterExhaustion { get; set; }
    private static bool Defeated(Life life) => life == Life.Down || life == Life.Spectating;
    public bool CanBeRescued(Member member) => RescueEnabled && (RescueLimit <= 0 || member.RescuesUsed < RescueLimit);

    private static void BeginReturn(Member member) {
        member.ReturnScene = member.CheckpointScene; member.ReturnMarker = member.CheckpointMarker;
        member.ReturnRespawnType = member.CheckpointRespawnType;
        member.Life = Life.Returning; member.LifeVersion++; member.HasMap = false;
    }
    public bool ReturnWithoutRescue(ushort id) {
        if (Resetting || !Members.TryGetValue(id, out var m) || m.Life != Life.Down || CanBeRescued(m) ||
            !ValidCheckpoint(m.CheckpointScene, m.CheckpointMarker, m.CheckpointRespawnType)) return false;
        BeginReturn(m); return true;
    }
    // Only defeated occupants leave. Travelling/returning players retain their
    // own native transition, scene visit and epoch. The engine evicts this
    // room's entity cache when its last occupant leaves normally.
    public Member[] FailBossRooms(Func<ushort, string, bool> occupiesScene = null) {
        if (Resetting) return new Member[0];
        var failed = Members.Values.Where(m => Defeated(m.Life) && m.InBossBattle)
            .Select(m => m.Scene).Distinct().Where(scene => !Members.Values.Any(m =>
                (m.Life == Life.Alive || m.Life == Life.Loading) &&
                (occupiesScene != null ? occupiesScene(m.Id, scene) : m.Scene == scene))).ToArray();
        var returning = Members.Values.Where(m => failed.Contains(m.Scene) && Defeated(m.Life) &&
            ValidCheckpoint(m.CheckpointScene, m.CheckpointMarker, m.CheckpointRespawnType)).ToArray();
        foreach (var member in returning) BeginReturn(member);
        return returning;
    }
    public bool StartSpectating(ushort id, uint round, uint serial, string scene) {
        if (!SpectateAfterExhaustion || Resetting || round != Round || !Members.TryGetValue(id, out var m) ||
            m.Life != Life.Down || m.DownSerial != serial || m.Scene != scene ||
            !Members.Values.Any(p => p.Id != id && p.Scene == scene && (p.Life == Life.Alive || p.Life == Life.Loading))) return false;
        m.Life = Life.Spectating; m.LifeVersion++; m.HasMap = false;
        return true;
    }
    // F6 must still leave a shade when the last spectator target has left.
    // Decide on the server so a teammate moving rooms during the request
    // cannot strand the caller in an endless spectate-request/denial loop.
    public bool GiveUpOrSpectate(ushort id, uint round, uint serial, string scene) {
        if (Resetting || round != Round || !Members.TryGetValue(id, out var member) ||
            member.Life != Life.Down || member.DownSerial != serial || member.Scene != scene) return false;
        return StartSpectating(id, round, serial, scene) || GiveUp(id, round, serial);
    }
    public bool Rest(ushort id, uint round, uint serial, string scene) {
        if (Resetting || round != Round || !Members.TryGetValue(id, out var m) || serial <= m.RestSerial ||
            (m.Life != Life.Alive && m.Life != Life.Loading) || m.Scene != scene) return false;
        m.RestSerial = serial; m.RescuesUsed = 0; return true;
    }
}
