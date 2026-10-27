using System;
using HollowConnect.Core;

namespace HollowConnect;

public sealed partial class CoopRuntime {
    private void RefreshGrimmFlames() {
        if (!Playable || PlayerData.instance == null) return;
        var pd = PlayerData.instance;
        var level = pd.GetInt("grimmChildLevel");
        var key = GrimmFlames.Key(level);
        if (key == null) return;
        var local = GrimmFlames.Evidence(level, pd.scenesFlameCollected, pd.GetBool("gotBrummsFlame"));
        var share = Connected && ProgressCatalog.CanShare(key, Shared, ShareCharms);
        var receipts = local | (share ? granted.Get(key) : 0);
        var scenes = GrimmFlames.Locations(level);
        for (var i = 0; i < scenes.Count; i++) {
            if ((receipts & (1 << i)) == 0) continue;
            if (!pd.scenesFlameCollected.Contains(scenes[i])) pd.scenesFlameCollected.Add(scenes[i]);
            // Preserve the native one-shot spawner receipt for a later visit.
            if (!(level == 3 && i == 3)) SceneData.instance.SaveMyState(new PersistentBoolData {
                id = "Flamebearer Spawn", sceneName = scenes[i], activated = true
            });
        }
        // The native list is stronger evidence than a lost counter update.
        // Never lower a valid count or carry a previous round into this one.
        var count = Math.Min(pd.GetInt("flamesRequired"), GrimmFlames.Count(receipts));
        if (pd.GetInt("flamesCollected") < count) pd.SetInt("flamesCollected", count);
        if (level == 3 && (receipts & 8) != 0) pd.SetBool("gotBrummsFlame", true);
        if (!share || receipts == 0 || offered.TryGetValue(key, out var old) && (old | receipts) == old) return;
        offered[key] = receipts;
        Send(new Message { Kind = Kind.ProgressOffer, Key = key, Value = receipts });
    }
}
