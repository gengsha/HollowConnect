using System;
using System.Collections.Generic;
using Mono.Cecil.Cil;
using MonoMod.Cil;
using UnityEngine;
using Hkmp.Animation;

namespace Hkmp.Game.Client.Entity;

// Change the accepted HP debit, after native attack modifiers/one-hit armour.
// Native HP thresholds, phase resets, invulnerability and scripted kills keep
// their own values. Replicas still receive the one authoritative absolute HP.
internal static class BossDamageScaling {
#if HOLLOW_UNITY6
    [ThreadStatic] private static HitInstance currentHit;
    [ThreadStatic] private static bool inHit;
#endif
    private static readonly Dictionary<int, Func<int, int>> Targets = new();
    private static readonly Dictionary<int, Action<int, GameObject>> Observers = new();
    internal static void Watch(HealthManager health, Func<int, int> scale, Action<int, GameObject> observe) {
        if (health != null) Targets[health.GetInstanceID()] = scale;
        if (health != null) Observers[health.GetInstanceID()] = observe;
    }
    internal static void Forget(HealthManager health) {
        if (!ReferenceEquals(health, null)) Targets.Remove(health.GetInstanceID());
        if (!ReferenceEquals(health, null)) Observers.Remove(health.GetInstanceID());
    }
    internal static void Clear() { Targets.Clear(); Observers.Clear(); }
    internal static void Register() {
#if HOLLOW_UNITY6
        IL.HealthManager.TakeDamage += PatchHitScope;
        IL.HealthManager.SubtractHealth += PatchHit;
#else
        IL.HealthManager.TakeDamage += PatchHit;
#endif
        IL.ExtraDamageable.ApplyExtraDamageToHealthManager += PatchExtra;
        On.DamageEffectTicker.Update += AttackOwnership.TickExtra;
        On.SendExtraDamage.OnEnter += AttackOwnership.SendExtra;
    }
    internal static void Unregister() {
#if HOLLOW_UNITY6
        IL.HealthManager.TakeDamage -= PatchHitScope;
        IL.HealthManager.SubtractHealth -= PatchHit;
#else
        IL.HealthManager.TakeDamage -= PatchHit;
#endif
        IL.ExtraDamageable.ApplyExtraDamageToHealthManager -= PatchExtra;
        On.DamageEffectTicker.Update -= AttackOwnership.TickExtra;
        On.SendExtraDamage.OnEnter -= AttackOwnership.SendExtra;
        Clear();
    }
    private static int Scale(HealthManager health, int damage) => health != null && Targets.TryGetValue(health.GetInstanceID(), out var scale) ? scale(damage) : damage;
    private static int Observe(HealthManager health, int damage, GameObject source) {
        if (health != null && damage > 0 && health.hp > 0 && Observers.TryGetValue(health.GetInstanceID(), out var observe))
            observe(System.Math.Min(health.hp, damage), source);
        return damage;
    }
    private static int AcceptedDamage(int damage, HealthManager health, HitInstance hit) {
        if (hit.AttackType == AttackTypes.Generic || hit.DamageDealt >= 9999) return damage;
        // Native hit events, soul, impact effects and evasion still follow
        // their normal path. Only the HP debit belongs to authority:
        // subtracting again on a replica after its absolute snapshot arrives
        // makes one local strike appear to damage the enemy twice.
        if (health != null && EntityReferences.IsReplica(health.gameObject)) return 0;
        return Observe(health, Scale(health, damage), hit.Source);
    }
    private static void PatchHit(ILContext il) {
        var c = new ILCursor(il);
#if HOLLOW_UNITY6
        c.GotoNext(MoveType.Before, i => i.MatchLdarg(0), i => i.MatchLdfld<HealthManager>("hp"),
            i => i.MatchLdarg(1), i => i.MatchSub());
        c.Index += 3;
        c.Emit(OpCodes.Ldarg_0);
        c.EmitDelegate<Func<int, HealthManager, int>>((damage, health) =>
            inHit ? AcceptedDamage(damage, health, currentHit) : damage);
#else
        c.GotoNext(MoveType.Before, i => i.MatchLdarg(0), i => i.MatchLdfld<HealthManager>("hp"),
            i => i.MatchLdloc(out _), i => i.MatchSub());
        c.Index += 3;
        c.Emit(OpCodes.Ldarg_0);
        c.Emit(OpCodes.Ldarg_1);
        c.EmitDelegate<Func<int, HealthManager, HitInstance, int>>(AcceptedDamage);
#endif
    }
#if HOLLOW_UNITY6
    private static void PatchHitScope(ILContext il) {
        var cursor = new ILCursor(il);
        cursor.GotoNext(MoveType.Before, instruction => instruction.MatchCallOrCallvirt<HealthManager>("SubtractHealth"));
        cursor.Remove();
        cursor.Emit(OpCodes.Ldarg_1);
        cursor.EmitDelegate<Action<HealthManager, int, HitInstance>>(HitScope);
    }

    private static void HitScope(HealthManager health, int damage, HitInstance hit) {
        var previous = currentHit; var wasInHit = inHit;
        currentHit = hit; inHit = true;
        try { health.SubtractHealth(damage); }
        finally { currentHit = previous; inHit = wasInHit; }
    }
#endif
    private static void PatchExtra(ILContext il) {
        var c = new ILCursor(il);
        c.GotoNext(MoveType.Before, i => i.MatchLdarg(1), i => i.MatchCallvirt<HealthManager>("ApplyExtraDamage"));
        c.Emit(OpCodes.Dup); // Keep the native call's receiver below the delegate's receiver/amount.
        c.Index++;
        c.EmitDelegate<Func<HealthManager, int, int>>((health, damage) =>
            Observe(health, Scale(health, damage), AttackOwnership.ExtraSource));
    }
}
