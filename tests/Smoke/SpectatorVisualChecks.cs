using System;
using System.Collections;
using System.Linq;
using System.Reflection;
using HollowConnect;
using HollowConnect.Core;
using UnityEngine;
using Object = UnityEngine.Object;

namespace HollowConnect.Smoke;

public sealed partial class RescueProbe {
    private void CheckSpectatorVisualHandoff() {
        var peer = runtime.Peers.Single();
        var avatar = runtime.PlayerObject(peer.Key);
        var container = avatar.transform.parent.gameObject;
        var body = avatar.GetComponent<Renderer>();
        var name = container.GetComponentsInChildren<Renderer>(true).Single(r => r.name == "Username");
        var tick = typeof(CoopRuntime).GetMethod("TickRescueOptions", BindingFlags.Instance | BindingFlags.NonPublic);
        var originalLife = peer.Value.Life;
        var originalValue = peer.Value.State.Value;
        var bodyHidden = body.forceRenderingOff;
        var nameHidden = name.forceRenderingOff;
        var errors = new System.Collections.Generic.List<string>();
        // Synchronous fixture: unreliable roster can announce Spectating while
        // the old Down shade still owns the same real remote renderer.
        foreach (var shadeFirst in new[] { true, false }) {
            var shadeObject = new GameObject("spectator visibility handoff fixture");
            var shade = shadeObject.AddComponent<ShadeView>();
            try {
                Check(!body.forceRenderingOff && !name.forceRenderingOff, "live peer body and name start visible");
                shade.HideAvatar(avatar);
                peer.Value.Life = Life.Spectating; peer.Value.State.Value = (int)Life.Spectating;
                tick.Invoke(runtime, null);
                if (!name.forceRenderingOff) errors.Add("spectator name remains visible");
                if (shadeFirst) {
                    shade.Retire();
                    if (!body.forceRenderingOff) errors.Add("retiring shade reveals active spectator");
                }
                peer.Value.Life = originalLife; peer.Value.State.Value = originalValue;
                tick.Invoke(runtime, null);
                if (!shadeFirst) {
                    if (!body.forceRenderingOff) errors.Add("ending spectate reveals active shade");
                    shade.Retire();
                }
                if (body.forceRenderingOff || name.forceRenderingOff) errors.Add("body or name remains hidden after both owners release");
            } finally {
                shade.Retire(); Object.Destroy(shadeObject);
                peer.Value.Life = originalLife; peer.Value.State.Value = originalValue;
                tick.Invoke(runtime, null);
                body.forceRenderingOff = bodyHidden; name.forceRenderingOff = nameHidden;
            }
        }
        Check(errors.Count == 0, "shade/spectator overlap hides names and restores original visibility in either release order: " + string.Join("; ", errors));
    }

    private IEnumerator RuleRemoteVisibility(string label) {
        // Native benches and dream wakes advertise Loading until input returns.
        // Both are living presentation states and must retain a visible avatar.
        yield return Until(() => runtime.Peers.Count == 1 &&
            (runtime.Peers.Values.Single().Life == Life.Alive || runtime.Peers.Values.Single().Life == Life.Loading) &&
            runtime.PlayerObject(runtime.Peers.Single().Key) is { activeInHierarchy: true }, label + " remote living body exists");
        var avatar = runtime.PlayerObject(runtime.Peers.Single().Key);
        yield return Until(() => !avatar.GetComponent<Renderer>().forceRenderingOff &&
            avatar.GetComponent<Renderer>().enabled && avatar.transform.parent.GetComponentsInChildren<Renderer>(true)
                .Where(r => r.name == "Username").All(r => !r.forceRenderingOff), label + " remote body and name restored");
    }
}
