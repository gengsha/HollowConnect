using System;
using System.Collections;
using System.Linq;
using Hkmp.Networking.Client;
using Modding;
using Mono.Cecil;
using Mono.Cecil.Cil;
using MonoMod.Cil;
using MonoMod.RuntimeDetour;

namespace Hkmp.Game.Client;

// Damage hit-stop must not stop the scene authority's AI/projectiles, or the
// unaffected player's interpolation/input clock. Keep the native recoil and
// immunity coroutine, replacing only StartRecoil's global freeze while online.
internal sealed class DamageRecoil : IDisposable {
    private readonly ILHook hook;

    internal DamageRecoil(NetClient client) {
        var target = MonoMod.Utils.Extensions.GetStateMachineTarget(
            ReflectionHelper.GetMethodInfo(typeof(HeroController), "StartRecoil"));
        var owner = target.DeclaringType.GetFields(System.Reflection.BindingFlags.Instance |
            System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic)
            .Single(f => f.FieldType == typeof(HeroController));
        hook = new ILHook(target, il => {
            bool Freeze(Instruction i) => i.Operand is MethodReference m && m.DeclaringType.FullName == typeof(global::GameManager).FullName &&
                m.Name == "FreezeMoment" && m.Parameters.Count == 4 && m.Parameters.All(p => p.ParameterType.FullName == "System.Single");
#if HOLLOW_UNITY6
            bool Immune(Instruction i) => i.MatchCallOrCallvirt(typeof(HeroController), "StartInvulnerable");
#else
            bool Immune(Instruction i) => i.MatchCallOrCallvirt(typeof(HeroController), "Invulnerable");
#endif
            // Check the complete native shape before changing any instructions.
            if (il.Instrs.Count(Freeze) != 1 || il.Instrs.Count(Immune) != 2)
                throw new InvalidOperationException($"Unsupported HeroController.StartRecoil damage coroutine: freeze={il.Instrs.Count(Freeze)}, immunity={il.Instrs.Count(Immune)}");
            var c = new ILCursor(il);
#if HOLLOW_UNITY6
            var startInvulnerable = ReflectionHelper.GetMethodInfo(typeof(HeroController), "StartInvulnerable");
            while (c.TryGotoNext(MoveType.Before, Immune)) {
                c.Remove();
                c.EmitDelegate<Action<HeroController, float>>((hero, duration) => {
                    startInvulnerable.Invoke(hero, new object[] { duration });
                    if (!client.IsConnected) return;
                    // Unity 6 tracks this delay separately from immunity. Keep
                    // the native pulse and immunity after our local hit-stop.
                    var delay = hero.DAMAGE_FREEZE_DOWN + hero.DAMAGE_FREEZE_WAIT + hero.DAMAGE_FREEZE_UP;
                    var current = ReflectionHelper.GetField<HeroController, float>(hero, "invulnerableFreezeDuration");
                    ReflectionHelper.SetField(hero, "invulnerableFreezeDuration", System.Math.Max(current, delay));
                });
            }
#else
            while (c.TryGotoNext(MoveType.After, Immune)) {
                c.Emit(OpCodes.Ldarg_0);
                c.EmitDelegate<Func<IEnumerator, object, IEnumerator>>((native, iterator) => {
                    if (!client.IsConnected) return native;
                    var hero = (HeroController)owner.GetValue(iterator);
                    // Native immunity normally waits through the global pause.
                    // Retain that real recovery allowance instead of consuming
                    // its duration while the character is still recoilFrozen.
                    return DelayImmunity(native, hero.DAMAGE_FREEZE_WAIT +
                        (hero.DAMAGE_FREEZE_DOWN + hero.DAMAGE_FREEZE_UP) * .5f);
                });
            }
#endif
            c.Index = 0;
            c.GotoNext(MoveType.Before, Freeze);
            c.Remove();
            c.EmitDelegate<Func<global::GameManager, float, float, float, float, IEnumerator>>((gm, down, wait, up, speed) =>
                client.IsConnected ? LocalFreeze(down + wait + up) : gm.FreezeMoment(down, wait, up, speed));
        });
    }

    private static IEnumerator LocalFreeze(float duration) {
        // Scaled time retains native pause/slow-motion semantics from other
        // game events. This routine never changes the world's time scale.
        yield return new UnityEngine.WaitForSeconds(duration);
    }

    private static IEnumerator DelayImmunity(IEnumerator native, float duration) {
        try {
            // Run the native first step now: invulnerable must already be true
            // during recovery. Leave pulse start/end and charm duration native.
            if (!native.MoveNext()) yield break;
            yield return new UnityEngine.WaitForSeconds(duration);
            do { yield return native.Current; } while (native.MoveNext());
        } finally { (native as IDisposable)?.Dispose(); }
    }

    public void Dispose() => hook.Dispose();
}
