using System;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;

namespace Hkmp.Game.Client.Entity;

// HealthManager keeps one native evasion timer per enemy.  A shared timer is
// correct for one local fighter, but makes a second player's simultaneous hit
// disappear on the scene authority.  Keep the native timer for the current
// call while remembering a short cooldown for each attacking player.
internal sealed class IndependentHitWindow {
    private readonly Dictionary<int, float> _nextAllowed = new();

    internal float Remaining(int player, float now) {
        if (!_nextAllowed.TryGetValue(player, out var next)) return 0;
        if (now < next) return next - now;
        _nextAllowed.Remove(player);
        return 0;
    }

    internal void Commit(int player, float now, float duration) {
        if (duration > 0) _nextAllowed[player] = now + duration;
        else _nextAllowed.Remove(player);
    }

}

internal static class IndependentHitEvasion {
    private static readonly FieldInfo Evasion = typeof(HealthManager).GetField(
        "evasionByHitRemaining", BindingFlags.Instance | BindingFlags.NonPublic);
    private static readonly Dictionary<int, IndependentHitWindow> Windows = new();

    [ThreadStatic] private static Scope _active;
    private static readonly IDisposable Noop = new NoopScope();

    internal static IDisposable Begin(HealthManager target, HitInstance hit) {
        if (target == null || Evasion == null || hit.AttackType == AttackTypes.Generic) return Noop;
        var entity = EntityReferences.Owner(target.gameObject);
        if (entity == null || !entity.IsNativeAuthority || EntityReferences.IsReplica(target.gameObject)) return Noop;
        var player = CoopContext.AttackSourcePlayer?.Invoke(hit.Source) ?? -1;
        // Local spells can detach from the hero. Remote effects carry their
        // owner marker before their first contact; never classify them local.
        if (player < 0 && hit.Source != null && !LocalWorldIsolation.IsRemote(hit.Source) &&
            EntityReferences.Owner(hit.Source) == null && (hit.Source.layer == 17 || hit.Source.GetComponent<DamageEnemies>() != null)) {
            var hero = HeroController.instance;
            if (hero != null) player = CoopContext.AttackSourcePlayer?.Invoke(hero.gameObject) ?? -1;
        }
        if (player < 0) return Noop;

        // Hit calls TakeDamage synchronously.  The nested hook must not create
        // a second cooldown or restore the timer before the outer call ends.
        if (_active != null && _active.Target == target && _active.Player == player) return Noop;

        var window = GetWindow(target);
        var beforeEvasion = Read(target);
        Write(target, window.Remaining(player, Time.time));
        var current = new Scope(target, player, window, beforeEvasion, _active);
        _active = current;
        return current;
    }

    internal static void Forget(HealthManager target) {
        if (!ReferenceEquals(target, null)) Windows.Remove(target.GetInstanceID());
    }

    internal static void Clear() => Windows.Clear();

    private static IndependentHitWindow GetWindow(HealthManager target) {
        var id = target.GetInstanceID();
        if (!Windows.TryGetValue(id, out var window)) Windows[id] = window = new IndependentHitWindow();
        return window;
    }

    private static float Read(HealthManager target) => (float)Evasion.GetValue(target);
    private static void Write(HealthManager target, float value) => Evasion.SetValue(target, value);

    private sealed class Scope : IDisposable {
        internal readonly HealthManager Target;
        internal readonly int Player;
        private readonly IndependentHitWindow _window;
        private readonly float _beforeEvasion;
        private readonly Scope _previous;
        private bool _disposed;

        internal Scope(HealthManager target, int player, IndependentHitWindow window,
            float beforeEvasion, Scope previous) {
            Target = target; Player = player; _window = window;
            _beforeEvasion = beforeEvasion; _previous = previous;
        }

        public void Dispose() {
            if (_disposed || !ReferenceEquals(_active, this)) return;
            _disposed = true; _active = _previous;
            if (Target == null) return;
            var afterEvasion = Read(Target);
            // Record exactly what native NonFatalHit/Invincible produced.
            // Alternate animations can leave zero; direct TakeDamage calls
            // retain their native bypass of Hit's evasion gate.
            _window.Commit(Player, Time.time, afterEvasion);
            Write(Target, _previous?.Target == Target ? _beforeEvasion : Mathf.Max(_beforeEvasion, afterEvasion));
        }
    }

    private sealed class NoopScope : IDisposable {
        public void Dispose() { }
    }
}
