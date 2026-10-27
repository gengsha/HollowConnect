using System;
using UnityEngine;

namespace Hkmp.Animation;

// Ownership survives detachment of remote spells from the avatar hierarchy.
internal static class AttackOwnership {
    internal static int CreatingPlayer = -1;
    internal static GameObject ExtraSource;
    internal static int RemoteOwner(GameObject source) {
        if (source == null) return -1;
        var marker = source.GetComponentInParent<RemoteVisualOpacity>();
        return marker == null ? -1 : marker.PlayerId;
    }
    internal static IDisposable Creating(ushort id) {
        var previous = CreatingPlayer; CreatingPlayer = id;
        return new Restore(() => CreatingPlayer = previous);
    }
    internal sealed class Restore : IDisposable {
        private Action restore;
        internal Restore(Action action) => restore = action;
        public void Dispose() { var action = restore; restore = null; action?.Invoke(); }
    }
    internal static void TickExtra(On.DamageEffectTicker.orig_Update orig, DamageEffectTicker self) {
        var previous = ExtraSource; ExtraSource = self.gameObject;
        try { orig(self); } finally { ExtraSource = previous; }
    }
    internal static void SendExtra(On.SendExtraDamage.orig_OnEnter orig, SendExtraDamage self) {
        var previous = ExtraSource; ExtraSource = self.Owner;
        try { orig(self); } finally { ExtraSource = previous; }
    }
}
