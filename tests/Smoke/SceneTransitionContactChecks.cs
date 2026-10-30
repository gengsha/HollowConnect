using System;
using System.Collections;
using System.IO;
using System.Linq;
using System.Reflection;
using Hkmp;
using Hkmp.Game.Client.Entity;
using UnityEngine;
using Object = UnityEngine.Object;

namespace HollowConnect.Smoke;

public sealed partial class RescueProbe {
    private IEnumerator SceneTransitionContactChecks() {
        if (Environment.GetEnvironmentVariable("HOLLOWCONNECT_TRANSITION_ARRIVAL") == "1") {
            yield return TransitionArrivalChecks();
            yield break;
        }
        if (Environment.GetEnvironmentVariable("HOLLOWCONNECT_HORNET_ENTRY") == "1") {
            yield return HornetNaturalEntryChecks();
            yield break;
        }
        const string room = "Fungus1_04";
        var actor = Environment.GetEnvironmentVariable("HOLLOWCONNECT_TRANSITION_ACTOR") ?? "guest";
        var moving = role == actor;
        var paused = Environment.GetEnvironmentVariable("HOLLOWCONNECT_TRANSITION_PAUSED") == "1";
        var localTransitions = 0;
        var remoteContacts = 0;
        var localContacts = 0;
        using var contact = IgnoreExternalCombatDamage();
        On.TransitionPoint.hook_OnTriggerEnter2D observe = (orig, gate, other) => {
            if (gate.targetScene != room && gate.gameObject.scene.name == room) {
                var own = other != null && other.transform.IsChildOf(HeroController.instance.transform);
                var remote = other != null && LocalWorldIsolation.IsRemote(other.gameObject);
                if (own) localContacts++;
                if (remote) remoteContacts++;
                Log("TRANSITION CONTACT gate=" + gate.name + " -> " + gate.targetScene + "/" + gate.entryPoint +
                    " collider=" + other?.name + " layer=" + other?.gameObject.layer + " own=" + own + " remote=" + remote +
                    " localHero=" + HeroController.instance.transform.position + " paused=" + GameManager.instance.isPaused);
            }
            orig(gate, other);
        };
        On.GameManager.hook_BeginSceneTransitionRoutine transition = (orig, gm, info) => {
            localTransitions++;
            Log("TRANSITION LOAD " + gm.sceneName + " -> " + info.SceneName + "/" + info.EntryGateName +
                " hero=" + HeroController.instance?.transform.position);
            return orig(gm, info);
        };
        On.TransitionPoint.OnTriggerEnter2D += observe;
        try {
            if (!host) yield return Until(() => Has("transition.first"), "authority first native entry");
            yield return RuleEnter(room, "right1");
            var hero = HeroController.instance;
            var exit = Object.FindObjectsOfType<TransitionPoint>().Single(t => t.gameObject.scene.name == room && t.name == "right1");
            var trigger = exit.GetComponent<Collider2D>();
            var destination = exit.targetScene;
            Check(trigger != null && trigger.enabled && !exit.isADoor && !string.IsNullOrEmpty(exit.targetScene),
                "native right exit exists and is active");
            Log("TRANSITION EXIT " + room + "/" + exit.name + " bounds=" + trigger.bounds + " -> " + exit.targetScene + "/" + exit.entryPoint);
            // Stage on native ground near the real entrance, outside the arena.
            // Only subsequent input/physics may cross the actual exit trigger.
            var start = trigger.bounds.center + Vector3.left * (moving ? 5 : 13);
            hero.transform.position = hero.FindGroundPoint(start + Vector3.up * 3, true) + Vector3.up * .1f;
            hero.GetComponent<Rigidbody2D>().velocity = Vector2.zero;
            hero.AffectedByGravity(true);
            Physics2D.SyncTransforms();
            Check(!trigger.bounds.Intersects(hero.GetComponent<Collider2D>().bounds), "local starting body is outside exit trigger");
            if (host) Flag("transition.first");
            Flag(role + ".transition-staged");
            yield return Until(() => Has("host.transition-staged") && Has("guest.transition-staged") &&
                runtime.PlayerObject(runtime.Peers.Keys.Single()) != null, "both staged with a real replicated body");
            yield return new WaitForSecondsRealtime(1);
            var originalScene = UnityEngine.SceneManagement.SceneManager.GetActiveScene();
            var originalPosition = hero.transform.position;
            On.GameManager.BeginSceneTransitionRoutine += transition;
            try {
                if (!moving && paused) {
                    UIManager.instance.TogglePauseGame();
                    yield return Until(() => GameManager.instance.isPaused, "observer native pause menu opened");
                }
                Flag(role + ".transition-ready");
                yield return Until(() => Has("host.transition-ready") && Has("guest.transition-ready"), "both ready for native peer crossing");
                if (moving) {
                    using (new MonoMod.RuntimeDetour.Hook(typeof(HeroController).GetMethod("LookForInput", BindingFlags.Instance | BindingFlags.NonPublic),
                        new Action<Action<HeroController>, HeroController>((orig, self) => { orig(self); if (self.acceptingInput) self.move_input = 1; })))
                        yield return Until(() => GameManager.instance.IsInSceneTransition, "own horizontal input starts native exit", 15);
                    Flag("transition.crossed");
                    yield return Until(() => runtime.Playable && GameManager.instance.sceneName == destination &&
                        !GameManager.instance.IsInSceneTransition && hero.acceptingInput, "own native adjacent arrival", 35);
                    Check(localTransitions == 1 && localContacts > 0, "own body starts exactly one native transition");
                    Flag("transition.arrived");
                } else {
                    yield return Until(() => Has("transition.crossed"), "peer starts natural crossing", 25);
                    yield return new WaitForSecondsRealtime(3);
                    File.WriteAllText(Path.Combine(root, role + ".transition-contact.json"), Newtonsoft.Json.JsonConvert.SerializeObject(new {
                        actor, paused, shared = runtime.Shared, original = originalScene.name,
                        current = UnityEngine.SceneManagement.SceneManager.GetActiveScene().name, localTransitions, remoteContacts,
                        originalPosition = originalPosition.ToString(), position = hero.transform.position.ToString(),
                        nativePaused = GameManager.instance.isPaused
                    }, Newtonsoft.Json.Formatting.Indented));
                    Check(localTransitions == 0 && UnityEngine.SceneManagement.SceneManager.GetActiveScene() == originalScene,
                        "peer crossing cannot transition stationary observer");
                    Check(Vector2.Distance(originalPosition, hero.transform.position) < 1, "observer keeps native position");
                    if (paused) {
                        Check(GameManager.instance.isPaused, "peer exit cannot dismiss local pause menu");
                        UIManager.instance.TogglePauseGame();
                        yield return Until(() => !GameManager.instance.isPaused && hero.acceptingInput, "observer unpauses normally");
                    }
                    Flag("transition.observer-checked");
                }
                yield return Until(() => Has("transition.arrived") && Has("transition.observer-checked"), "both native crossing assertions complete");
            } finally { On.GameManager.BeginSceneTransitionRoutine -= transition; }
        } finally { On.TransitionPoint.OnTriggerEnter2D -= observe; }
    }
}
