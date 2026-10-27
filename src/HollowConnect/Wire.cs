using System;
using Hkmp.Networking.Packet;
using Hkmp.Networking.Packet.Data;

namespace HollowConnect;

public enum Channel { Presence, Event }
public enum Kind : byte { Hello, Welcome, Presence, Roster, Down, ShadeHit, Revived, Wipe, Ready, Release, ProgressOffer, ProgressGrant, WarpRequest, Warp, Left,
    GiveUpRequest, Returning, BenchReady, Returned, GiveUpDenied, DeathCount, DeathHistory, ResourceOffer, ResourceGrant,
    BossDefeated, BossVictory, BossRecoveryRequest, BossRevived, RescueStatus, BenchRest, RescueAnchorRequest, RescueAnchor, RescueUnavailable,
    SpectateRequest, Spectating, BossSettlementReady, DamageBegin, DamageHit, DamageEnd, DamageSnapshot }

public static class Wire {
    // Reliable server-to-client audit request in the existing progress envelope.
    // This key is outside the grant catalog and cannot be offered as progress.
    public const string ProgressAuditKey = ":join-audit";
    // Both senders accumulate messages into HKMP collections before transmission.
    public static IPacketData CreateCollection(Channel _) => new PacketDataCollection<Message>();
}

public sealed class Message : IPacketData {
    public Kind Kind;
    public ushort Id, Target;
    public uint Round, Serial, Aux;
    public string Session = "", Scene = "", Name = "", Key = "";
    public string BossSequence = "", BossDoor = "";
    public int BossIndex, BossBindings;
    public int BossLevel = -1;
    public string BossPlan = "";
    public string BossStatueKey = "", BossReturnGate = "", BossReturnScene = "";
    public float X, Y, MapX, MapY;
    public int Value;
    public bool HasMap, IsReady;
    public bool IsReliable => Kind != Kind.Presence && Kind != Kind.Roster;
    public bool DropReliableDataIfNewerExists => false;
    public void WriteData(IPacket p) {
        p.Write((byte)Kind); p.Write(Id); p.Write(Target); p.Write(Round); p.Write(Serial); p.Write(Aux);
        p.Write(Session); p.Write(Scene); p.Write(Name); p.Write(Key);
        p.Write(X); p.Write(Y); p.Write(MapX); p.Write(MapY); p.Write(Value); p.Write(HasMap); p.Write(IsReady);
        p.Write(BossSequence); p.Write(BossDoor); p.Write(BossIndex); p.Write(BossBindings);
        p.Write(BossLevel); p.Write(BossPlan);
        p.Write(BossStatueKey); p.Write(BossReturnGate); p.Write(BossReturnScene);
    }
    public void ReadData(IPacket p) {
        Kind = (Kind)p.ReadByte(); Id = p.ReadUShort(); Target = p.ReadUShort(); Round = p.ReadUInt(); Serial = p.ReadUInt(); Aux = p.ReadUInt();
        Session = p.ReadString(); Scene = p.ReadString(); Name = p.ReadString(); Key = p.ReadString();
        X = p.ReadFloat(); Y = p.ReadFloat(); MapX = p.ReadFloat(); MapY = p.ReadFloat(); Value = p.ReadInt(); HasMap = p.ReadBool(); IsReady = p.ReadBool();
        BossSequence = p.ReadString(); BossDoor = p.ReadString(); BossIndex = p.ReadInt(); BossBindings = p.ReadInt();
        BossLevel = p.ReadInt(); BossPlan = p.ReadString();
        BossStatueKey = p.ReadString(); BossReturnGate = p.ReadString(); BossReturnScene = p.ReadString();
        if (!Enum.IsDefined(typeof(Kind), Kind) || Session.Length > 64 || Scene.Length > 128 || Name.Length > 128 || Key.Length > 128 ||
            BossSequence.Length > 128 || BossDoor.Length > 128 || BossIndex < 0 || BossIndex > 256 || BossBindings < 0 || BossBindings > 15 || BossLevel < -1 || BossLevel > 2 || BossPlan.Length > 24576 || BossStatueKey.Length > 128 || BossReturnGate.Length > 128 || BossReturnScene.Length > 128)
            throw new FormatException("Invalid HollowConnect message");
    }
}
