using System;
using System.Collections;
using System.Linq;
using Hkmp.Game.Client.Entity;
using UnityEngine;
using Object = UnityEngine.Object;

namespace HollowConnect.Smoke;

public sealed partial class CoverageProbe {
    private IEnumerator LocalFlipAssertions(int round) {
        var hero = HeroController.instance;
        yield return Until(() => hero.acceptingInput && !hero.cState.transitioning &&
            hero.transitionState == GlobalEnums.HeroTransitionState.WAITING_TO_TRANSITION && engine.ClientManager.SceneSnapshotReady, "native platform room entry ready");
        var platforms = Object.FindObjectsOfType<FlipPlatform>(true).Where(p => p.gameObject.scene.name == "Mines_35").OrderBy(p => p.name, StringComparer.Ordinal).ToArray();
        if (platforms.Length != 21 || Entities.Values.Any(e => e.Type == EntityType.CrystalPeakPlatform) ||
            platforms.Any(p => EntityReferences.Owner(p.gameObject) != null)) throw new Exception("Crystal platforms still registered or duplicated: " + platforms.Length);
        Mark("PASS all 21 crystal platforms are native local objects");
        var target = platforms.First(p => p.gameObject.activeInHierarchy && p.GetComponent<Collider2D>() != null);
        var floor = target.GetComponent<Collider2D>();
        var top = target.topSpikes;
        var bottom = target.bottomSpikes;
        var far = target.transform.position + Vector3.up * 25;
        void Move(Vector3 position) { Freeze(); hero.transform.position = position; Physics2D.SyncTransforms(); }
        var flips = 0;
        On.FlipPlatform.hook_Flip flip = (orig, self) => { if (self == target) flips++; return orig(self); };
        On.FlipPlatform.Flip += flip;
        IEnumerator Together(string label) {
            var key = ".local-platform." + round + "." + label;
            Flag(role + key); yield return Until(() => Has((host ? "guest" : "host") + key), "both local platform " + label);
        }
        try {
            Move(far);
            yield return Together("ready");
            foreach (var actor in new[] { "host", "guest" }) {
                var before = flips;
                if (role == actor) {
                    Move(new Vector3(floor.bounds.center.x, floor.bounds.max.y + 3, hero.transform.position.z));
                    hero.cState.onGround = false; hero.AffectedByGravity(true);
                    yield return Until(() => hero.cState.onGround && flips > before, "actual landing triggers local flip " + actor);
                    Move(far);
                    yield return Until(() => top.activeSelf && !bottom.activeSelf, "native local platform turns spikes up " + actor);
                }
                yield return Together(actor + "-flipped");
                if (role != actor && (flips != before || top.activeSelf || !bottom.activeSelf)) throw new Exception("Remote player flipped this local platform");
                Mark("PASS platform flip stays local for " + actor);
                yield return Until(() => !top.activeSelf && bottom.activeSelf && Field<Coroutine>(target, "flipRoutine") == null, "native platform returns upright " + actor);
                yield return Together(actor + "-recovered");
            }
        } finally { On.FlipPlatform.Flip -= flip; }
    }
}
