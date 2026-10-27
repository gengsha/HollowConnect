using UnityEngine;

namespace Hkmp.Game.Client.Entity;

// Remote effects still have native attack colliders. Stop them before native
// damage/events reach a private enemy, breakable, switch or pickup controller.
internal static class LocalWorldInteractions {
    internal static void Register() {
        On.DamageEnemies.DoDamage += Damage;
        On.HealthManager.Hit += Hit;
        On.HealthManager.TakeDamage += TakeDamage;
        On.DamageEffectTicker.OnTriggerEnter2D += ExtraContact;
        On.SendExtraDamage.OnEnter += ExtraAction;
        On.Breakable.Hit += BreakableHit;
        On.BreakablePole.Hit += PoleHit;
        On.BreakableObject.OnTriggerEnter2D += BreakableTrigger;
        On.HutongGames.PlayMaker.Fsm.OnTriggerEnter2D += Enter;
        On.HutongGames.PlayMaker.Fsm.OnTriggerStay2D += Stay;
        On.HutongGames.PlayMaker.Fsm.OnTriggerExit2D += Exit;
    }
    internal static void Unregister() {
        On.DamageEnemies.DoDamage -= Damage;
        On.HealthManager.Hit -= Hit;
        On.HealthManager.TakeDamage -= TakeDamage;
        On.DamageEffectTicker.OnTriggerEnter2D -= ExtraContact;
        On.SendExtraDamage.OnEnter -= ExtraAction;
        On.Breakable.Hit -= BreakableHit;
        On.BreakablePole.Hit -= PoleHit;
        On.BreakableObject.OnTriggerEnter2D -= BreakableTrigger;
        On.HutongGames.PlayMaker.Fsm.OnTriggerEnter2D -= Enter;
        On.HutongGames.PlayMaker.Fsm.OnTriggerStay2D -= Stay;
        On.HutongGames.PlayMaker.Fsm.OnTriggerExit2D -= Exit;
    }
    private static void Damage(On.DamageEnemies.orig_DoDamage orig, DamageEnemies self, GameObject target) {
        if (!LocalWorldIsolation.Blocks(target, self.gameObject)) orig(self, target);
    }
    private static void Hit(On.HealthManager.orig_Hit orig, HealthManager self, HitInstance hit) {
        if (!LocalWorldIsolation.Blocks(self.gameObject, hit.Source)) orig(self, hit);
    }
    private static void TakeDamage(On.HealthManager.orig_TakeDamage orig, HealthManager self, HitInstance hit) {
        if (!LocalWorldIsolation.Blocks(self.gameObject, hit.Source)) orig(self, hit);
    }
    private static void ExtraContact(On.DamageEffectTicker.orig_OnTriggerEnter2D orig, DamageEffectTicker self, Collider2D collider) {
        if (!LocalWorldIsolation.Blocks(collider == null ? null : collider.gameObject, self.gameObject)) orig(self, collider);
    }
    private static void ExtraAction(On.SendExtraDamage.orig_OnEnter orig, SendExtraDamage action) {
        if (LocalWorldIsolation.Blocks(action.Fsm.GetOwnerDefaultTarget(action.target), action.Owner)) action.Finish();
        else orig(action);
    }
    private static void BreakableHit(On.Breakable.orig_Hit orig, Breakable self, HitInstance hit) {
        if (!LocalWorldIsolation.Blocks(self.gameObject, hit.Source)) orig(self, hit);
    }
    private static void PoleHit(On.BreakablePole.orig_Hit orig, BreakablePole self, HitInstance hit) {
        if (!LocalWorldIsolation.Blocks(self.gameObject, hit.Source)) orig(self, hit);
    }
    private static void BreakableTrigger(On.BreakableObject.orig_OnTriggerEnter2D orig, BreakableObject self, Collider2D collider) {
        if (!LocalWorldIsolation.Blocks(self.gameObject, collider == null ? null : collider.gameObject)) orig(self, collider);
    }
    private static void Enter(On.HutongGames.PlayMaker.Fsm.orig_OnTriggerEnter2D orig, HutongGames.PlayMaker.Fsm self, Collider2D collider) {
        if (!LocalWorldIsolation.Blocks(self.GameObject, collider == null ? null : collider.gameObject)) orig(self, collider);
    }
    private static void Stay(On.HutongGames.PlayMaker.Fsm.orig_OnTriggerStay2D orig, HutongGames.PlayMaker.Fsm self, Collider2D collider) {
        if (!LocalWorldIsolation.Blocks(self.GameObject, collider == null ? null : collider.gameObject)) orig(self, collider);
    }
    private static void Exit(On.HutongGames.PlayMaker.Fsm.orig_OnTriggerExit2D orig, HutongGames.PlayMaker.Fsm self, Collider2D collider) {
        if (!LocalWorldIsolation.Blocks(self.GameObject, collider == null ? null : collider.gameObject)) orig(self, collider);
    }
}
