using System.Linq;
using Hkmp;
using HollowConnect.Core;
using Newtonsoft.Json;
using UnityEngine;

namespace HollowConnect;

public sealed partial class CoopRuntime {
    private float nextRestProgress;
    private void RefreshPantheonRestProgress() {
        // Bench/conversation control locks must not defer the shared exit until
        // after the advancing teammate has already left. No hero state or
        // bindings change when this same-run rest context is adopted.
        if (Down || !Playable || HeroController.instance.cState.dead || HeroController.instance.cState.hazardDeath ||
            HeroController.instance.cState.hazardRespawning || CoopContext.LocalScene != "GG_Spa" || !BossSequenceController.IsInSequence ||
            BossSceneController.Instance?.customExitPoint == null || Time.unscaledTime < nextRestProgress) return;
        nextRestProgress = Time.unscaledTime + .25f;
        var local = new Message { Scene = CoopContext.LocalScene };
        if (!BossTravel.Capture(local)) return;
        var peer = Peers.Values.Where(p => p.Life == Life.Alive && p.State != null && p.State.IsReady && Now - p.Seen < 2)
            .Select(p => p.State).Where(m => m.Scene == local.Scene && m.BossSequence == local.BossSequence &&
                m.BossDoor == local.BossDoor && m.BossBindings == local.BossBindings && m.BossIndex > local.BossIndex)
            .OrderByDescending(m => m.BossIndex).FirstOrDefault();
        if (peer == null || !BossTravel.ApplyContext(JsonConvert.SerializeObject(peer))) return;
        // This is accepted native rest progression, so disconnect must keep it.
        BossTravel.CommitTransientContext();
        Debug.Log("HollowConnect rest progress advanced: " + local.BossIndex + " -> " + peer.BossIndex);
    }
}
