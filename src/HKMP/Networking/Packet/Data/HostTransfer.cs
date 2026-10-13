namespace Hkmp.Networking.Packet.Data;

/// <summary>
/// Packet data for a host transfer.
/// </summary>
internal class HostTransfer : IPacketData {
    public ClientPlayerAlreadyInScene Snapshot { get; set; } = new ClientPlayerAlreadyInScene();
    /// <inheritdoc />
    public bool IsReliable => true;

    /// <inheritdoc />
    public bool DropReliableDataIfNewerExists => true;
    
    /// <summary>
    /// The name of the scene in which the player becomes the scene host.
    /// </summary>
    public string SceneName { get; set; }

    /// <inheritdoc />
    public void WriteData(IPacket packet) {
        packet.Write(SceneName);
        Snapshot.WriteData(packet);
    }

    /// <inheritdoc />
    public void ReadData(IPacket packet) {
        SceneName = packet.ReadString();
        Snapshot.ReadData(packet);
    }
}
