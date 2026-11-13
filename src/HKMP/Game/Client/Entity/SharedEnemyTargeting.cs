using System;
using System.Collections.Generic;
using System.Reflection;
using HutongGames.PlayMaker.Actions;
using Mono.Cecil;
using Mono.Cecil.Cil;
using MonoMod.Cil;
using MonoMod.RuntimeDetour;
using UnityEngine;

namespace Hkmp.Game.Client.Entity;

// Keep the native range, raycast and attack actions. Only the crystal enemies'
// perception may read another living player; replicas and local progression do
// not acquire new triggers or persist remote avatars in native FSM variables.
internal static class SharedEnemyTargeting {
    private static readonly List<ILHook> Hooks = new();
    internal static ActiveRegion NativeRegion;

    internal static void AddActivationRegion(GameObject player) {
        NativeRegion = UnityEngine.Object.FindObjectOfType<ActiveRegion>();
        var source = NativeRegion == null ? null : NativeRegion.GetComponent<BoxCollider2D>();
        if (source == null) return;
        // The game's camera region is 50x35 in Legacy. Reuse its actual size
        // on both engines; the remote player's camera is not simulated here.
        var region = new GameObject("Shared enemy activation region") { layer = source.gameObject.layer };
        region.transform.SetParent(player.transform, false);
        var box = region.AddComponent<BoxCollider2D>();
        var scale = source.transform.lossyScale;
        box.size = Vector2.Scale(source.size, new Vector2(Mathf.Abs(scale.x), Mathf.Abs(scale.y)));
        box.offset = source.offset;
        box.isTrigger = true;
        region.AddComponent<RemoteEnemyActivationRegion>();
    }

    internal static bool Allows(GameObject owner) {
        if (CoopContext.Resetting || CoopContext.LocalWorldOnly || owner == null || EntityReferences.IsReplica(owner)) return false;
        var e = EntityReferences.Owner(owner);
        return e != null && e.IsNativeAuthority && !e.BossTargetScope &&
            e.Type is (EntityType.HuskMiner or EntityType.CrystallisedHusk or EntityType.CrystalHunter or
                EntityType.CrystalCrawler or EntityType.CrystalTurret or EntityType.Shardmite or EntityType.Glimback) &&
            EntityReferences.Entities != null && EntityReferences.Entities.TryGetValue(e.Id, out var live) && live == e;
    }

    internal static void Register() {
        if (Hooks.Count != 0) return;
        try {
            foreach (var method in new[] { "DoTriggerEnter2D", "DoTriggerStay2D", "DoTriggerExit2D" }) {
                var target = typeof(Trigger2dEvent).GetMethod(method, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                    ?? throw new MissingMethodException(typeof(Trigger2dEvent).FullName, method);
                Hooks.Add(new ILHook(target, il => {
                    var cursor = new ILCursor(il);
                    cursor.GotoNext(MoveType.After, i => i.OpCode == OpCodes.Callvirt && i.Operand is MethodReference m &&
                        m.DeclaringType.FullName == typeof(GameObject).FullName && m.Name == "get_tag");
                    cursor.Emit(OpCodes.Ldarg_0);
                    cursor.Emit(OpCodes.Ldarg_1);
                    cursor.EmitDelegate<Func<string, Trigger2dEvent, Collider2D, string>>(PlayerTag);
                }));
            }
        } catch { Unregister(); throw; }
    }

    private static string PlayerTag(string original, Trigger2dEvent action, Collider2D collider) {
        if (action.collideTag.Value != "Player" || collider == null || collider.gameObject.layer != 9 ||
            !Allows(action.Fsm?.GameObject)) return original;
        var id = CoopContext.AttackSourcePlayer?.Invoke(collider.gameObject) ?? -1;
        // Match the player's body, never their spells, attacks or a spectator.
        return id >= 0 && CoopContext.BossTargetObject?.Invoke(id) == collider.gameObject ? "Player" : original;
    }

    internal static void Unregister() {
        foreach (var hook in Hooks) hook.Dispose();
        Hooks.Clear();
        NativeRegion = null;
    }
}

internal sealed class RemoteEnemyActivationRegion : MonoBehaviour {
    private void OnTriggerEnter2D(Collider2D other) {
        if (other == null || !SharedEnemyTargeting.Allows(other.gameObject)) return;
        var id = CoopContext.AttackSourcePlayer?.Invoke(gameObject) ?? -1;
        if (id < 0 || CoopContext.BossTargetObject?.Invoke(id) != transform.parent.gameObject) return;
        var source = SharedEnemyTargeting.NativeRegion;
        if (source == null) SharedEnemyTargeting.NativeRegion = source = UnityEngine.Object.FindObjectOfType<ActiveRegion>();
        var activator = other.GetComponent<FSMActivator>();
        if (source == null || activator == null) return;
        // The native region owns the coroutine, so a departing remote avatar
        // cannot interrupt an enemy halfway through its native activation.
        if (source.staggeredActivation) source.StartCoroutine(activator.ActivateStaggered());
        else activator.Activate();
    }
}
