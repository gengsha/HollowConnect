using Hkmp.Api.Client;
using UnityEngine;

namespace Hkmp.Game.Client;

/// <inheritdoc />
internal class ClientPlayerData : IClientPlayer {
    internal readonly PeerScenePresence ScenePresence = new();
    /// <inheritdoc />
    public ushort Id { get; }

    /// <inheritdoc />
    public string Username { get; }

    /// <inheritdoc />
    public bool IsInLocalScene { get; set; }

    /// <inheritdoc />
    public GameObject PlayerContainer { get; set; }

    /// <inheritdoc />
    public GameObject PlayerObject { get; set; }

    /// <inheritdoc />
    public Team Team { get; set; }

    /// <inheritdoc />
    public byte SkinId { get; set; }

    /// <summary>
    /// The Custom Knight skin directory ID of the player, or an empty string for the default skin.
    /// </summary>
    public string CustomSkinId { get; set; } = "";

    public ClientPlayerData(
        ushort id,
        string username
    ) {
        Id = id;
        Username = username;
    }
}
