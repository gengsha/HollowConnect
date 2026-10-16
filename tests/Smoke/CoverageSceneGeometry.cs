using System;
using System.Linq;
using Hkmp.Game.Client.Entity;
using UnityEngine;
using UnityEngine.SceneManagement;
using Object = UnityEngine.Object;

namespace HollowConnect.Smoke;

public sealed partial class CoverageProbe {
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
