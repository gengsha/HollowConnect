using System.Collections.Generic;
using Hkmp.Networking.Packet.Data;

namespace Hkmp.Game.Client;

// A reliable enter/snapshot can arrive after live movement. Keep the latest
// visual state per peer visit until its avatar exists; never replay old attacks.
internal sealed class PendingPeerState {
    private readonly Dictionary<ushort, PlayerUpdate> states = new();
    internal void Add(PlayerUpdate update) {
        if (!states.TryGetValue(update.Id, out var state) || state.PlayerVisit < update.PlayerVisit) {
            if (states.Count >= 256 && !states.ContainsKey(update.Id)) return;
            state = new PlayerUpdate { Id = update.Id, PlayerVisit = update.PlayerVisit };
            states[update.Id] = state;
        }
        if (state.PlayerVisit != update.PlayerVisit) return;
        if (update.UpdateTypes.Contains(PlayerUpdateType.Position)) {
            state.UpdateTypes.Add(PlayerUpdateType.Position); state.Position = update.Position;
        }
        if (update.UpdateTypes.Contains(PlayerUpdateType.Scale)) {
            state.UpdateTypes.Add(PlayerUpdateType.Scale); state.Scale = update.Scale;
        }
        if (update.AnimationInfos.Count != 0) {
            var animation = update.AnimationInfos[update.AnimationInfos.Count - 1];
            state.UpdateTypes.Add(PlayerUpdateType.Animation); state.AnimationInfos.Clear();
            state.AnimationInfos.Add(new AnimationInfo { ClipId = animation.ClipId, Frame = animation.Frame });
        }
    }
    internal PlayerUpdate Take(ushort id, uint visit) {
        if (!states.TryGetValue(id, out var state) || state.PlayerVisit != visit) return null;
        states.Remove(id); return state;
    }
    internal void Drop(ushort id, uint throughVisit = uint.MaxValue) {
        if (states.TryGetValue(id, out var state) && state.PlayerVisit <= throughVisit) states.Remove(id);
    }
    internal void Clear() => states.Clear();
}
