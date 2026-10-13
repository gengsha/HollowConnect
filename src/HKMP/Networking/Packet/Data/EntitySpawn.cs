using Hkmp.Game.Client.Entity;

namespace Hkmp.Networking.Packet.Data; 

/// <summary>
/// Packet data for an entity spawn. Either from entering a scene or from spawning while in a scene.
/// </summary>
internal class EntitySpawn : IPacketData {
    /// <inheritdoc />
    public bool IsReliable => true;

    /// <inheritdoc />
    public bool DropReliableDataIfNewerExists => false;

    /// <summary>
    /// The ID of the spawned entity.
    /// </summary>
    public ulong Id { get; set; }
    public string SceneName { get; set; } = "";
    public uint Visit { get; set; }
    public uint Epoch { get; set; }
    public ulong SourceId { get; set; }
    public string Fsm { get; set; } = "";
    public string FsmPath { get; set; } = "";
    public string State { get; set; } = "";
    public string Prefab { get; set; } = "";
    public int ActionIndex { get; set; } = -1;
    public float X { get; set; }
    public float Y { get; set; }
    public float Z { get; set; }
    public bool Active { get; set; } = true;
    
    /// <summary>
    /// The type of the entity that spawned the new entity.
    /// </summary>
    public EntityType SpawningType { get; set; }
    
    /// <summary>
    /// The type of the entity that was spawned.
    /// </summary>
    public EntityType SpawnedType { get; set; }

    /// <inheritdoc />
    public void WriteData(IPacket packet) {
        packet.Write(Id);
        packet.Write(SceneName); packet.Write(Visit); packet.Write(Epoch);
        packet.Write((ushort) SpawningType);
        packet.Write((ushort) SpawnedType);
        packet.Write(SourceId); packet.Write(Fsm); packet.Write(FsmPath); packet.Write(State); packet.Write(Prefab); packet.Write(ActionIndex);
        packet.Write(X); packet.Write(Y); packet.Write(Z);packet.Write(Active);
    }

    /// <inheritdoc />
    public void ReadData(IPacket packet) {
        Id = packet.ReadULong();
        SceneName = packet.ReadString(); Visit = packet.ReadUInt(); Epoch = packet.ReadUInt();
        SpawningType = (EntityType) packet.ReadUShort();
        SpawnedType = (EntityType) packet.ReadUShort();
        SourceId = packet.ReadULong(); Fsm = packet.ReadString(); FsmPath=packet.ReadString(); State = packet.ReadString(); Prefab = packet.ReadString(); ActionIndex = packet.ReadInt();
        X = packet.ReadFloat(); Y = packet.ReadFloat(); Z = packet.ReadFloat();Active=packet.ReadBool();
        if (Fsm.Length > 256 || FsmPath.Length>2048 || State.Length > 256 || Prefab.Length > 256 || ActionIndex < -1 || ActionIndex > 4096 ||
            float.IsNaN(X) || float.IsInfinity(X) || float.IsNaN(Y) || float.IsInfinity(Y) || float.IsNaN(Z) || float.IsInfinity(Z))
            throw new System.FormatException("Invalid entity spawn descriptor");
    }
}
