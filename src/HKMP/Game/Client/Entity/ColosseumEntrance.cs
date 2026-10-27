using System.Linq;
using HutongGames.PlayMaker;
using UnityEngine;
using UnityEngine.SceneManagement;
using Object = UnityEngine.Object;

namespace Hkmp.Game.Client.Entity;

// Entry belongs to the local knight. Shared moving walls still run normally;
// only their collision with an entrant outside the native trigger is deferred.
internal sealed class ColosseumEntrance : MonoBehaviour {
    private PlayMakerFSM gate;
    private BoxCollider2D entry;
    private Collider2D[] walls, heroColliders;
    private bool completed;

    internal static bool IsGate(GameObject go) => go != null && go.name == "Colosseum Gate" &&
        IsTrial(go.scene.name) && go.GetComponents<PlayMakerFSM>().Any(f => f.FsmName == "Rise");
    private static bool IsTrial(string scene) => scene is "Room_Colosseum_Bronze" or "Room_Colosseum_Silver" or "Room_Colosseum_Gold";

    internal static void Prepare(Scene scene) {
        if (!IsTrial(scene.name)) return;
        var entrance = Object.FindObjectsOfType<PlayMakerFSM>(true).FirstOrDefault(f => f.gameObject.scene == scene && IsGate(f.gameObject));
        if (entrance == null || entrance.GetComponent<ColosseumEntrance>() != null) return;
        var manager = Object.FindObjectsOfType<PlayMakerFSM>(true).FirstOrDefault(f => f.gameObject.scene == scene &&
            f.name == "Colosseum Manager" && f.FsmName == "Manager");
        var trigger = manager == null ? null : manager.transform.Find("Entry Detect")?.GetComponent<BoxCollider2D>();
        if (trigger == null) return;
        var local = entrance.gameObject.AddComponent<ColosseumEntrance>();
        local.gate = entrance;
        local.entry = trigger;
    }

    // Opening is the shared trial's native reward continuation. Remember it
    // even when the entrant has not yet closed their own gate.
    internal static bool AllowsOpen(HutongGames.PlayMaker.Fsm receiver, string signal) {
        if (signal != "GATE OPEN" || !IsGate(receiver.GameObject)) return false;
        var local = receiver.GameObject.GetComponent<ColosseumEntrance>();
        if (local != null) local.completed = true;
        return true;
    }

    private void FixedUpdate() {
        var hero = HeroController.instance;
        if (gate == null || entry == null || hero == null || !hero.isHeroInPosition ||
            global::GameManager.instance.IsInSceneTransition || UnityEngine.SceneManagement.SceneManager.GetActiveScene() != gameObject.scene) return;
        if (walls == null) {
            // Run after scene registration/deferred clone cleanup. Include
            // both sides of the authority pair so a handoff cannot reblock it.
            walls = Object.FindObjectsOfType<Collider2D>(true).Where(c => c.gameObject.scene == gameObject.scene &&
                !c.isTrigger && c.transform.parent != null &&
                c.transform.parent.name.Replace("(Clone)", "").Trim() == "Colosseum Wall L").ToArray();
            heroColliders = hero.GetComponents<Collider2D>().Where(c => !c.isTrigger).ToArray();
        }
        // The original trigger may be under an inactive authority object.
        // Its serialized shape still defines the same local entry boundary.
        var centre = entry.transform.TransformPoint(entry.offset);
        var scale = entry.transform.lossyScale;
        var size = new Vector3(Mathf.Abs(entry.size.x * scale.x), Mathf.Abs(entry.size.y * scale.y), 1);
        var bounds = new Bounds(centre, size);
        var entered = heroColliders.Any(c => c.enabled && bounds.Intersects(c.bounds));
        foreach (var wall in walls) foreach (var body in heroColliders) {
            if (wall != null && body != null && wall.enabled && wall.gameObject.activeInHierarchy && body.enabled)
                Physics2D.IgnoreCollision(body, wall, !entered);
        }
        if (!entered) return;
        if (!completed && gate.ActiveStateName == "Init") gate.SendEvent("GATE CLOSE");
        enabled = false;
    }
}
