using UnityEngine;

namespace Hkmp.Game.Client.Entity;

// The first Dreamer encounter is personal traversal. Leave its native
// platforms and story controllers intact, even in shared-progress rooms.
internal static class LocalStoryScene {
    internal static bool Contains(string scene) => scene == "Dream_Nailcollection";

    private static bool HasTraversal(string scene) => scene is
        "Dream_01_False_Knight" or "Dream_02_Mage_Lord" or "Dream_03_Infected_Knight";

    // Only proximity-driven entry platforms are personal traversal. Bosses,
    // arena platforms and platforms shown by phase controllers keep networking.
    internal static bool IsTraversalPlatform(GameObject target) {
        if (target == null || !HasTraversal(target.scene.name)) return false;
        var platform = target.GetComponent<DreamPlatform>();
        return platform != null && !platform.showOnEnable &&
            platform.innerCollider != null && platform.outerCollider != null;
    }

    internal static void Register() {
        On.TriggerEnterEvent.OnTriggerEnter2D += Enter;
        On.TriggerEnterEvent.OnTriggerExit2D += Exit;
        On.TriggerEnterEvent.OnTriggerStay2D += Stay;
        On.HutongGames.PlayMaker.Actions.Trigger2dEvent.DoTriggerEnter2D += FsmEnter;
        On.HutongGames.PlayMaker.Actions.Trigger2dEvent.DoTriggerExit2D += FsmExit;
        On.HutongGames.PlayMaker.Actions.Trigger2dEvent.DoTriggerStay2D += FsmStay;
    }

    internal static void Unregister() {
        On.TriggerEnterEvent.OnTriggerEnter2D -= Enter;
        On.TriggerEnterEvent.OnTriggerExit2D -= Exit;
        On.TriggerEnterEvent.OnTriggerStay2D -= Stay;
        On.HutongGames.PlayMaker.Actions.Trigger2dEvent.DoTriggerEnter2D -= FsmEnter;
        On.HutongGames.PlayMaker.Actions.Trigger2dEvent.DoTriggerExit2D -= FsmExit;
        On.HutongGames.PlayMaker.Actions.Trigger2dEvent.DoTriggerStay2D -= FsmStay;
    }

    private static bool Allows(TriggerEnterEvent trigger, Collider2D collider) {
        var scene = trigger.gameObject.scene.name;
        if (!Contains(scene)) {
            if (!HasTraversal(scene)) return true;
            var platform = trigger.GetComponentInParent<DreamPlatform>();
            if (platform == null || !IsTraversalPlatform(platform.gameObject)) return true;
        }
        var hero = HeroController.instance;
        return hero != null && collider != null && collider.transform.IsChildOf(hero.transform);
    }

    private static void Enter(On.TriggerEnterEvent.orig_OnTriggerEnter2D orig, TriggerEnterEvent self, Collider2D collider) {
        if (Allows(self, collider)) orig(self, collider);
    }
    private static void Exit(On.TriggerEnterEvent.orig_OnTriggerExit2D orig, TriggerEnterEvent self, Collider2D collider) {
        if (Allows(self, collider)) orig(self, collider);
    }
    private static void Stay(On.TriggerEnterEvent.orig_OnTriggerStay2D orig, TriggerEnterEvent self, Collider2D collider) {
        if (Allows(self, collider)) orig(self, collider);
    }

    // These encounters already run native FSMs. A replicated attack still
    // has a real collider and must not advance another player's story object.
    // Permanent defeat grants continue through the normal progress ledger.
    private static bool BlocksPeerContact(HutongGames.PlayMaker.Fsm receiver, Collider2D collider) {
        var target = receiver.GameObject;
        return target != null && (target.scene.name is
            "Dream_Guardian_Lurien" or "Dream_Guardian_Hegemol" or "Dream_Guardian_Monomon" ||
            target.scene.name == "White_Palace_09" && target.name == "White King Corpse") &&
            LocalWorldIsolation.IsRemote(collider == null ? null : collider.gameObject);
    }
    private static void FsmEnter(On.HutongGames.PlayMaker.Actions.Trigger2dEvent.orig_DoTriggerEnter2D orig, HutongGames.PlayMaker.Actions.Trigger2dEvent self, Collider2D collider) {
        if (!BlocksPeerContact(self.Fsm, collider)) orig(self, collider);
    }
    private static void FsmExit(On.HutongGames.PlayMaker.Actions.Trigger2dEvent.orig_DoTriggerExit2D orig, HutongGames.PlayMaker.Actions.Trigger2dEvent self, Collider2D collider) {
        if (!BlocksPeerContact(self.Fsm, collider)) orig(self, collider);
    }
    private static void FsmStay(On.HutongGames.PlayMaker.Actions.Trigger2dEvent.orig_DoTriggerStay2D orig, HutongGames.PlayMaker.Actions.Trigger2dEvent self, Collider2D collider) {
        if (!BlocksPeerContact(self.Fsm, collider)) orig(self, collider);
    }
}

