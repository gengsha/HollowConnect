using System;
using System.Collections;
using System.Linq;
using Hkmp.Game.Client.Entity;
using MonoMod.RuntimeDetour;
using UnityEngine;
using UnityEngine.SceneManagement;
using Object = UnityEngine.Object;

namespace HollowConnect.Smoke;

public sealed partial class CoverageProbe {
    private IEnumerator CliffsInteriorCheck(int round) {
        var hero = HeroController.instance;
        var body = hero.GetComponent<Rigidbody2D>();
        var chest = Object.FindObjectsOfType<PlayMakerFSM>().Single(f => f.name == "Chest" && f.FsmName == "Chest Control");
        var grub = Object.FindObjectsOfType<Transform>().Single(t => t.name == "Grub Bottle");
        yield return Until(() => hero.acceptingInput && !hero.cState.transitioning && engine.ClientManager.SceneSnapshotReady,
            "interior native entry and snapshot ready");
        Mark("INTERIOR LANDMARKS grub=" + grub.position + " chest=" + chest.transform.position + " shared=" + CoopMod.Settings.ShareProgress);
        Dump(chest.gameObject);

        IEnumerator Drop(string label, Vector2 start, float below) {
            Freeze();
            hero.transform.position = new Vector3(start.x, start.y, hero.transform.position.z);
            hero.cState.onGround = false;
            hero.AffectedByGravity(true);
            Physics2D.SyncTransforms();
            var deadline = Time.realtimeSinceStartup + 8;
            var sample = 0f;
            while (hero.transform.position.y >= below && Time.realtimeSinceStartup < deadline) {
                if (Time.realtimeSinceStartup >= sample) {
                    sample = Time.realtimeSinceStartup + .5f;
                    Mark("INTERIOR DROP " + label + " pos=" + hero.transform.position + " velocity=" + body.velocity +
                        " ground=" + hero.cState.onGround + " contacts=" + string.Join(";", Physics2D.OverlapBoxAll(
                            hero.GetComponent<Collider2D>().bounds.center, hero.GetComponent<Collider2D>().bounds.size, 0, 1 << 8)
                            .Select(c => EntityIdentity.Path(c.transform) + "@" + c.gameObject.scene.name + " " + c.bounds)));
                }
                yield return null;
            }
            if (hero.transform.position.y >= below) throw new Exception("Interior passage blocked: " + label + " pos=" + hero.transform.position);
            Mark("PASS interior natural fall " + label + " pos=" + hero.transform.position);
            Freeze();
        }

        // Start above each opening, then let the native hero/physics descend.
        // Do not disable terrain, force the chest state, or move through walls.
        yield return Drop("left of grub", new Vector2(73, 54.5f), 32);
        if (manager.IsSceneHost) {
            hero.transform.position = chest.transform.position + new Vector3(-2, .8f, 0);
            hero.FaceRight();
            Physics2D.SyncTransforms();
            var attack = InputHandler.Instance.inputActions.attack;
            var frame = Time.frameCount + 1;
            using (new Hook(typeof(InControl.OneAxisInputControl).GetProperty("WasPressed").GetGetMethod(),
                new Func<Func<InControl.OneAxisInputControl, bool>, InControl.OneAxisInputControl, bool>((orig, self) =>
                    ReferenceEquals(self, attack) ? Time.frameCount == frame : orig(self))))
            using (new Hook(typeof(InControl.OneAxisInputControl).GetProperty("IsPressed").GetGetMethod(),
                new Func<Func<InControl.OneAxisInputControl, bool>, InControl.OneAxisInputControl, bool>((orig, self) =>
                    ReferenceEquals(self, attack) ? Time.frameCount >= frame : orig(self)))) {
                yield return new WaitForSecondsRealtime(.12f);
            }
        }
        yield return Until(() => !chest.GetComponent<Collider2D>().enabled && chest.transform.Find("Opened").gameObject.activeInHierarchy,
            "native chest opens locally or through shared progress");
        Mark("INTERIOR CHEST state=" + chest.ActiveStateName);
        yield return Drop("inside opened chest", new Vector2(chest.transform.position.x, 32), 14);
        TraceSceneGeometry();
        Flag(role + ".interior." + round);
        if (Environment.GetEnvironmentVariable("HOLLOWCONNECT_GEOMETRY_GUEST_ALONE") != "1")
            yield return Until(() => Has((host ? "guest" : "host") + ".interior." + round), "both interior passages sampled");
    }

    private void EnterGeometryScene(string scene) {
        var current = UnityEngine.SceneManagement.SceneManager.GetActiveScene();
        var door = Object.FindObjectsOfType<TransitionPoint>().FirstOrDefault(t =>
            t.gameObject.scene == current && t.targetScene == scene && !t.isADoor);
        if (door != null) {
            Mark("GEOMETRY NATIVE EXIT " + current.name + "/" + door.name + " -> " + scene + "/" + door.entryPoint);
            typeof(TransitionPoint).GetMethod("OnTriggerEnter2D", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)
                .Invoke(door, new object[] { HeroController.instance.GetComponent<Collider2D>() });
        } else {
            // Bootstrap from Town using an entrance present in the native level.
            Enter(scene, scene == "Cliffs_01" ? "right3" : null);
        }
    }

    // Diagnostic only: distinguish a second loaded level from duplicated or
    // displaced terrain inside the current level. Never unload or move it.
    private void TraceSceneGeometry() {
        for (var i = 0; i < UnityEngine.SceneManagement.SceneManager.sceneCount; i++) {
            var scene = UnityEngine.SceneManagement.SceneManager.GetSceneAt(i);
            Mark("GEOMETRY SCENE " + scene.name + " handle=" + scene.handle + " loaded=" + scene.isLoaded +
                " active=" + (scene == UnityEngine.SceneManagement.SceneManager.GetActiveScene()));
        }
        foreach (var group in Object.FindObjectsOfType<Collider2D>(true).Where(c => c.gameObject.layer == 8)
            .GroupBy(c => c.gameObject.scene.name)) {
            Mark("GEOMETRY TERRAIN " + group.Key + " total=" + group.Count() + " active=" +
                group.Count(c => c.enabled && c.gameObject.activeInHierarchy));
            foreach (var c in group.Where(c => c.enabled && c.gameObject.activeInHierarchy))
                Mark("GEOMETRY COLLIDER " + EntityIdentity.Path(c.transform) + " bounds=" + c.bounds +
                    " trigger=" + c.isTrigger + " owner=" + EntityReferences.Owner(c.gameObject)?.Id);
        }
        foreach (var t in Object.FindObjectsOfType<TransitionPoint>(true))
            Mark("GEOMETRY DOOR " + t.gameObject.scene.name + "/" + t.name + " pos=" + t.transform.position +
                " active=" + t.gameObject.activeInHierarchy + " target=" + t.targetScene + "/" + t.entryPoint);
        foreach (var c in Object.FindObjectsOfType<SceneAdditiveLoadConditional>(true))
            Mark("GEOMETRY LOADER " + c.gameObject.scene.name + "/" + c.name + " main=" + c.sceneNameToLoad + " alt=" + c.altSceneNameToLoad);
    }
}
