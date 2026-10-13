namespace Hkmp.Networking.Packet.Data; 

/// <summary>
/// Packet data for the client-bound player leave scene data.
/// </summary>
internal class ClientPlayerLeaveScene : GenericClientData {
    public uint Visit { get; set; }
    public uint Epoch { get; set; }
    public uint PlayerVisit { get; set; }
    /// <summary>
    /// The name of the scene that the player left.
    /// </summary>
    public string SceneName { get; set; }
    
    /// <summary>
    /// Construct the client player leave scene data.
    /// </summary>
    public ClientPlayerLeaveScene() {
        IsReliable = true;
        DropReliableDataIfNewerExists = false;
    }

    /// <inheritdoc />
    public override void WriteData(IPacket packet) {
        packet.Write(Id);
        packet.Write(SceneName);
        packet.Write(Visit); packet.Write(Epoch); packet.Write(PlayerVisit);
    }

    /// <inheritdoc />
    public override void ReadData(IPacket packet) {
        Id = packet.ReadUShort();
        SceneName = packet.ReadString();
        Visit = packet.ReadUInt(); Epoch = packet.ReadUInt(); PlayerVisit = packet.ReadUInt();
    }
}

/// <summary>
/// Packet data for the server-bound player left scene data.
/// </summary>
internal class ServerPlayerLeaveScene : IPacketData {
    public uint Visit { get; set; }
    public uint Epoch { get; set; }
    /// <inheritdoc />
    public bool IsReliable => true;

    /// <inheritdoc />
    public bool DropReliableDataIfNewerExists => false;
    
    /// <summary>
    /// The name of the scene that the player left.
    /// </summary>
    public string SceneName { get; set; }
    
    /// <inheritdoc />
    public void WriteData(IPacket packet) {
        packet.Write(Visit); packet.Write(Epoch);
        packet.Write(SceneName);
    }

    /// <inheritdoc />
    public void ReadData(IPacket packet) {
        Visit = packet.ReadUInt(); Epoch = packet.ReadUInt();
        SceneName = packet.ReadString();
    }
}
