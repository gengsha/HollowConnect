using System;
using System.Collections;
using System.Linq;
using Hkmp.Game.Client.Entity;
using UnityEngine;
using Object = UnityEngine.Object;

namespace HollowConnect.Smoke;

public sealed partial class CoverageProbe {
    private IEnumerator DreamPlatformAssertions(int round) {
        const string scene = "Dream_Nailcollection";
        var hero = HeroController.instance;
        var platform = Object.FindObjectsOfType<DreamPlatform>().Single(p => p.gameObject.scene.name == scene && p.name == "dream_plat_small");
        bool Visible() => Field<bool>(platform, "visible");
        IEnumerator Check(Func<bool> ready, string label) {
            var end = Time.realtimeSinceStartup + 12;
            while (!ready() && Time.realtimeSinceStartup < end) yield return null;
            if (!ready()) throw new Exception("Local dream platform " + label + " hero=" + hero.transform.position + " visible=" + Visible());
            Mark("PASS local dream platform " + label);
        }
        IEnumerator Together(string label) {
            Flag(role + ".dream." + round + "." + label);
            yield return Check(() => Has((host ? "guest" : "host") + ".dream." + round + "." + label), "both " + label);
        }
        void Move(Vector3 pos) { Freeze(); hero.transform.position = pos; Physics2D.SyncTransforms(); }
        var far = platform.transform.position + Vector3.up * 35;
        var near = platform.innerCollider.GetComponent<Collider2D>().bounds.center + Vector3.up * 4;
        IEnumerator Stand() {
            var floor = platform.GetComponentsInChildren<Collider2D>().First(c => !c.isTrigger && c.enabled);
            hero.transform.position = new Vector3(floor.bounds.center.x, floor.bounds.max.y + 3, hero.transform.position.z);
            hero.cState.onGround = false;
            hero.AffectedByGravity(true);
            hero.GetComponent<Rigidbody2D>().velocity = Vector2.zero;
            Physics2D.SyncTransforms();
            yield return Check(() => hero.cState.onGround && Math.Abs(hero.GetComponent<Collider2D>().bounds.min.y - floor.bounds.max.y) < .4f,
                "stands on native platform collider");
            Freeze();
        }
        yield return Check(() => engine.ClientManager.SceneSnapshotReady && hero.acceptingInput && !hero.cState.transitioning &&
            hero.transitionState == GlobalEnums.HeroTransitionState.WAITING_TO_TRANSITION, "native entry ready");
        if (Entities.Count != 0 || Object.FindObjectsOfType<DreamPlatform>(true).Any(p =>
            p.gameObject.scene.name == scene && (p.name.Contains("(Clone)") || EntityReferences.Owner(p.gameObject) != null)))
            throw new Exception("Personal dream scene was networked or cloned");
        // Also cover late/spawn registration, which must not take over a native platform.
        if (new EntityProcessor { GameObject = platform.gameObject, LateLoad = true, IsSceneHostDetermined = true,
            IsSceneHost = manager.IsSceneHost }.Process().Success) throw new Exception("Late registration networked personal dream platform");
        Mark("PASS local dream scene has no network entities or platform clones");
        Move(far);
        yield return Together("far");
        yield return new WaitForSecondsRealtime(1);
        if (Visible()) throw new Exception("Native dream platform did not hide away from local hero");
        foreach (var approaching in new[] { "host", "guest" }) {
            if (role == approaching) Move(near);
            yield return Together("approach-" + approaching);
            yield return new WaitForSecondsRealtime(1);
            if (Visible() != (role == approaching)) throw new Exception("Peer approach changed local platform visibility");
            Mark("PASS peer approach leaves personal platform visibility independent");
            if (role == approaching) yield return Stand();
            yield return Together("stood-" + approaching);
            if (role == approaching) Move(far);
            yield return Together("leave-" + approaching);
            yield return Check(() => !Visible(), "native leave hides personal platform");
        }
    }
}
