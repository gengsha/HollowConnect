using System;
using System.Collections;
using System.IO;
using System.Linq;
using System.Reflection;
using Hkmp;
using Hkmp.Game.Client.Entity;
using HutongGames.PlayMaker;
using UnityEngine;
using Object = UnityEngine.Object;

namespace HollowConnect.Smoke;

public sealed partial class RescueProbe {
    private IEnumerator HornetNaturalEntryChecks() {
        const string room = "Fungus1_04", approach = "Fungus1_21";
        var hero = HeroController.instance;
        var transitions = 0;
        var introStarted = false;
        var warp = Environment.GetEnvironmentVariable("HOLLOWCONNECT_HORNET_ENTRY_WARP") == "1";
        using var contact = IgnoreExternalCombatDamage();
        On.GameManager.hook_BeginSceneTransitionRoutine trace = (orig, gm, info) => {
            transitions++;
            Log("HORNET ENTRY LOAD " + gm.sceneName + " -> " + info.SceneName + "/" + info.EntryGateName +
                " hero=" + hero.transform.position + " heroState=" + hero.hero_state + " transition=" + hero.transitionState);
            return orig(gm, info);
        };
        On.HutongGames.PlayMaker.Fsm.hook_EnterState stateTrace = (orig, fsm, state) => {
            if (fsm.GameObject != null && fsm.GameObject.name == "Hornet Infected Knight Encounter") {
                Log("HORNET ENTRY INTRO " + fsm.GameObject.scene.name + "/" + fsm.Name + " => " + state.Name + " hero=" + hero.transform.position);
                if (state.Name == "Point") introStarted = true;
            }
            orig(fsm, state);
        };
        On.TransitionPoint.hook_OnTriggerEnter2D gateTrace = (orig, gate, other) => {
            Log("HORNET ENTRY CONTACT " + gate.gameObject.scene.name + "/" + gate.name + " -> " + gate.targetScene + "/" + gate.entryPoint +
                " other=" + other?.name + " layer=" + other?.gameObject.layer + " local=" +
                (other != null && other.transform.IsChildOf(hero.transform)) + " remote=" +
                (other != null && LocalWorldIsolation.IsRemote(other.gameObject)) + " hero=" + hero.transform.position);
            orig(gate, other);
        };
        On.GameManager.BeginSceneTransitionRoutine += trace;
        On.HutongGames.PlayMaker.Fsm.EnterState += stateTrace;
        On.TransitionPoint.OnTriggerEnter2D += gateTrace;
        try {
            foreach (var key in new[] { "hasDash", "canDash", "hornet1Defeated", "killedHornet", "dreamerScene1" }) PlayerData.instance.SetBool(key, false);
            if (host) {
                yield return RuleEnter(room, "right1");
                var encounter = Object.FindObjectsOfType<PlayMakerFSM>().Single(f => f.gameObject.scene.name == room + "_boss" &&
                    f.name == "Hornet Infected Knight Encounter" && f.FsmName == "Encounter");
                var gates = Object.FindObjectsOfType<PlayMakerFSM>().Where(f => f.gameObject.scene.name.StartsWith(room, StringComparison.Ordinal) && f.FsmName == "BG Control").ToArray();
                Check(gates.Length == 2, "two native Hornet barriers across base/additive scenes; actual=" + string.Join(",", gates.Select(f => f.gameObject.scene.name + "/" + f.name)));
                var gate = gates.OrderBy(f => f.transform.position.x).Last();
                hero.transform.position = hero.FindGroundPoint(gate.transform.position + Vector3.right * 3 + Vector3.up * 2, true) + Vector3.up * .1f;
                hero.GetComponent<Rigidbody2D>().velocity = Vector2.zero; hero.AffectedByGravity(true); Physics2D.SyncTransforms();
                Check(hero.transform.position.x > gate.transform.position.x + 1 && !encounter.GetComponent<Collider2D>().OverlapPoint(hero.transform.position),
                    "host stages outside native arena and intro trigger");
                Flag("hornet-entry.authority");
                yield return Until(() => Has("hornet-entry.approach"), "guest in adjacent video approach room");
                using (new MonoMod.RuntimeDetour.Hook(typeof(HeroController).GetMethod("LookForInput", BindingFlags.Instance | BindingFlags.NonPublic),
                    new Action<Action<HeroController>, HeroController>((orig, self) => { orig(self); if (self.acceptingInput) self.move_input = -1; })))
                    yield return Until(() => introStarted, "host naturally starts first Hornet intro", 15);
                Flag("hornet-entry.intro");
                yield return Until(() => Has("hornet-entry.crossed"), "guest naturally crosses connector while host is in intro", 35);
            } else {
                yield return Until(() => Has("hornet-entry.authority"), "host prepared Hornet native arena");
                yield return RuleEnter(approach, "left1");
                var gate = Object.FindObjectsOfType<TransitionPoint>().Single(t => t.gameObject.scene.name == approach && t.name == "left1");
                var trigger = gate.GetComponent<Collider2D>();
                Check(gate.targetScene == room && gate.entryPoint == "right1", "video approach has actual reciprocal Hornet entrance");
                Log("HORNET ENTRY APPROACH " + gate.name + " bounds=" + trigger.bounds + " hero=" + hero.transform.position);
                hero.transform.position = hero.FindGroundPoint(trigger.bounds.center + Vector3.right * 4 + Vector3.up * 2, true) + Vector3.up * .1f;
                hero.GetComponent<Rigidbody2D>().velocity = Vector2.zero; hero.AffectedByGravity(true); Physics2D.SyncTransforms();
                Check(!trigger.bounds.Intersects(hero.GetComponent<Collider2D>().bounds), "guest stages outside actual approach exit");
                Flag("hornet-entry.approach");
                yield return Until(() => Has("hornet-entry.intro"), "authority has started native intro dialogue");
                if (warp) {
                    var pressed = false;
                    var keys = runtime.KeyPressed;
                    try {
                        runtime.KeyPressed = key => key == CoopMod.Settings.TeleportKey && !pressed && (pressed = true);
                        yield return Until(() => GameManager.instance.IsInSceneTransition, "guest F8 starts actual network warp", 15);
                    } finally { runtime.KeyPressed = keys; }
                } else {
                    using (new MonoMod.RuntimeDetour.Hook(typeof(HeroController).GetMethod("LookForInput", BindingFlags.Instance | BindingFlags.NonPublic),
                        new Action<Action<HeroController>, HeroController>((orig, self) => { orig(self); if (self.acceptingInput) self.move_input = -1; })))
                        yield return Until(() => GameManager.instance.IsInSceneTransition, "guest walks through native connector", 15);
                }
                Flag("hornet-entry.crossed");
            }
            var before = transitions;
            var end = Time.realtimeSinceStartup + 18;
            var sample = 0f;
            while (Time.realtimeSinceStartup < end) {
                if (Time.realtimeSinceStartup >= sample) {
                    sample = Time.realtimeSinceStartup + .5f;
                    Log("HORNET ENTRY SAMPLE scene=" + GameManager.instance.sceneName + " active=" + UnityEngine.SceneManagement.SceneManager.GetActiveScene().name +
                        " pos=" + hero.transform.position + " velocity=" + hero.GetComponent<Rigidbody2D>().velocity + " input=" + hero.acceptingInput +
                        " relinquished=" + hero.controlReqlinquished + " inPosition=" + hero.isHeroInPosition + " entering=" + GameManager.instance.IsInSceneTransition +
                        " transition=" + hero.transitionState + " camera=" + GameCameras.instance.cameraController.mode + " loads=" + transitions);
                }
                yield return RulePress(InputHandler.Instance.inputActions.jump);
                yield return RulePress(InputHandler.Instance.inputActions.menuSubmit);
            }
            File.WriteAllText(Path.Combine(root, role + ".hornet-entry-result.json"), Newtonsoft.Json.JsonConvert.SerializeObject(new {
                transitions, before, introStarted, scene = GameManager.instance.sceneName, input = hero.acceptingInput,
                entering = GameManager.instance.IsInSceneTransition, position = hero.transform.position.ToString()
            }, Newtonsoft.Json.Formatting.Indented));
            Check(transitions <= before && GameManager.instance.sceneName == room && !GameManager.instance.IsInSceneTransition && hero.acceptingInput,
                "native Hornet late entry settles without repeated loads");
            Flag(role + ".hornet-entry-checked");
            yield return Until(() => Has("host.hornet-entry-checked") && Has("guest.hornet-entry-checked"), "both native route observations complete");
        } finally {
            On.GameManager.BeginSceneTransitionRoutine -= trace;
            On.HutongGames.PlayMaker.Fsm.EnterState -= stateTrace;
            On.TransitionPoint.OnTriggerEnter2D -= gateTrace;
        }
    }
}
