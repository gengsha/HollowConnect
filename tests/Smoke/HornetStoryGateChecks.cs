using System;
using System.Collections;
using System.IO;
using System.Linq;
using System.Reflection;
using Hkmp.Game.Client.Entity;
using HutongGames.PlayMaker;
using UnityEngine;
using Object = UnityEngine.Object;

namespace HollowConnect.Smoke;

public sealed partial class RescueProbe {
    private const string HornetStoryScene = "Fungus1_04";
    private string[] HornetRoles => (Environment.GetEnvironmentVariable("HOLLOWCONNECT_SMOKE_ROLES") ?? "host,guest,third").Split(',');
    private PlayMakerFSM[] HornetFsms() => Object.FindObjectsOfType<PlayMakerFSM>(true).Where(f =>
        f.gameObject.scene.name.StartsWith(HornetStoryScene, StringComparison.Ordinal)).ToArray();
    private bool HornetInteresting(GameObject go) => go != null && go.scene.name.StartsWith(HornetStoryScene, StringComparison.Ordinal) &&
        (go.name.IndexOf("Hornet", StringComparison.OrdinalIgnoreCase) >= 0 || go.name.Contains("Cloak") || go.name.Contains("Shiny") ||
         go.name.Contains("Dream") || go.name.Contains("Battle Gate"));
    private void HornetDump(string stage) {
        var hero = HeroController.instance; var pd = PlayerData.instance;
        Log("HORNET SNAP " + stage + " hero=" + hero.transform.position + " input=" + hero.acceptingInput + " control=" + hero.controlReqlinquished +
            " dash=" + pd.GetBool("hasDash") + " defeated=" + pd.GetBool("hornet1Defeated") + " killed=" + pd.GetBool("killedHornet") +
            " dreamer=" + pd.GetBool("dreamerScene1") + " down=" + runtime.Down);
        foreach (var f in HornetFsms().Where(f => HornetInteresting(f.gameObject))) {
            Log("HORNET FSM " + stage + " " + f.gameObject.scene.name + "/" + EntityIdentity.Path(f.transform) + "/" + f.FsmName +
                "=" + f.ActiveStateName + " active=" + f.gameObject.activeInHierarchy + " enabled=" + f.enabled + " pos=" + f.transform.position);
            foreach (var c in f.GetComponents<Collider2D>()) Log("HORNET COLLIDER " + f.name + " enabled=" + c.enabled + " trigger=" + c.isTrigger + " bounds=" + c.bounds);
        }
        File.WriteAllText(Path.Combine(root, role + ".hornet-" + stage + ".json"), Newtonsoft.Json.JsonConvert.SerializeObject(new {
            stage, scene = GameManager.instance.sceneName, x = hero.transform.position.x, y = hero.transform.position.y,
            input = hero.acceptingInput, relinquished = hero.controlReqlinquished, dash = pd.GetBool("hasDash"),
            defeated = pd.GetBool("hornet1Defeated"), dreamer = pd.GetBool("dreamerScene1"),
            fsms = HornetFsms().Where(f => HornetInteresting(f.gameObject)).Select(f => new {
                name = EntityIdentity.Path(f.transform), fsm = f.FsmName, state = f.ActiveStateName, active = f.gameObject.activeInHierarchy,
                x = f.transform.position.x, y = f.transform.position.y,
                colliders = f.GetComponentsInChildren<Collider2D>(true).Select(c => new { c.name, c.enabled, c.isTrigger, active = c.gameObject.activeInHierarchy })
            }).ToArray()
        }, Newtonsoft.Json.Formatting.Indented));
    }
    private IEnumerator HornetWait(Func<bool> ready, float seconds) {
        var end = Time.realtimeSinceStartup + seconds;
        while (!ready() && Time.realtimeSinceStartup < end) yield return null;
    }
    private IEnumerator HornetWalk(float x, float seconds = 15) {
        var hero = HeroController.instance;
        using (new MonoMod.RuntimeDetour.Hook(typeof(HeroController).GetMethod("LookForInput", BindingFlags.Instance | BindingFlags.NonPublic),
            new Action<Action<HeroController>, HeroController>((orig, self) => { orig(self); if (self.acceptingInput) self.move_input = Mathf.Abs(x - self.transform.position.x) < .3f ? 0 : Mathf.Sign(x - self.transform.position.x); }))) {
            var end = Time.realtimeSinceStartup + seconds;
            var lastX = hero.transform.position.x; var lastMoved = Time.realtimeSinceStartup;
            while (Mathf.Abs(hero.transform.position.x - x) >= .5f && GameManager.instance.sceneName == HornetStoryScene && Time.realtimeSinceStartup < end) {
                if (!hero.acceptingInput || hero.controlReqlinquished) {
                    yield return RulePress(InputHandler.Instance.inputActions.jump);
                    yield return RulePress(InputHandler.Instance.inputActions.menuSubmit);
                }
                if (Mathf.Abs(hero.transform.position.x - lastX) > .2f) { lastX = hero.transform.position.x; lastMoved = Time.realtimeSinceStartup; }
                else if (hero.acceptingInput && Time.realtimeSinceStartup - lastMoved > 1.2f) {
                    var direction = Mathf.Sign(x - hero.transform.position.x);
                    Log("HORNET WALK STALLED at=" + hero.transform.position + " ground=" + hero.cState.onGround + " rays=" + string.Join(",", Physics2D.RaycastAll(hero.transform.position, Vector2.right * direction, 3)
                        .Where(h => h.collider != null && !h.collider.isTrigger && !h.collider.transform.IsChildOf(hero.transform)).Select(h => h.collider.name + "/" + h.distance)));
                    if (hero.cState.onGround) yield return RulePress(InputHandler.Instance.inputActions.jump);
                    lastMoved = Time.realtimeSinceStartup;
                }
                yield return new WaitForSecondsRealtime(.1f);
            }
        }
        Log("HORNET WALK target=" + x + " actual=" + hero.transform.position + " input=" + hero.acceptingInput);
    }
    private IEnumerator HornetStoryGateChecks() {
        using var contact = IgnoreExternalCombatDamage();
        var roles = HornetRoles; bool All(string key) => roles.All(r => Has(r + key));
        var schema = Environment.GetEnvironmentVariable("HOLLOWCONNECT_HORNET_STORY") == "schema";
        var nativeFightStarted = false; var nativeStoryEnded = false;
        var pd = PlayerData.instance; var hero = HeroController.instance;
        foreach (var key in new[] { "hasDash", "canDash", "hornet1Defeated", "killedHornet", "dreamerScene1" }) pd.SetBool(key, false);
        yield return Until(() => All(".connected") && runtime.Peers.Count == roles.Length - 1, "three real connected participants");
        Check(runtime.Shared == (Environment.GetEnvironmentVariable("HOLLOWCONNECT_HORNET_SHARED") == "1"), "requested shared-progress option");
        On.HutongGames.PlayMaker.Fsm.hook_EnterState trace = (orig, fsm, state) => {
            if (fsm.GameObject != null && fsm.GameObject.scene.name == HornetStoryScene + "_boss" &&
                fsm.GameObject.name == "Hornet Infected Knight Encounter" && state.Name == "Start Fight") nativeFightStarted = true;
            if (fsm.GameObject != null && fsm.GameObject.scene.name == HornetStoryScene + "_boss" &&
                fsm.GameObject.name == "Dreamer Scene 1" && state.Name == "End") nativeStoryEnded = true;
            if (HornetInteresting(fsm.GameObject)) File.AppendAllText(Path.Combine(root, role + ".hornet-states.txt"),
                DateTime.UtcNow.ToString("O") + " " + fsm.GameObject.scene.name + "/" + EntityIdentity.Path(fsm.GameObject.transform) + "/" + fsm.Name + " => " + state.Name + "\n");
            orig(fsm, state);
        };
        On.HutongGames.PlayMaker.Fsm.EnterState += trace;
        try {
            if (!host) yield return Until(() => Has("hornet.first-entered"), "host first entrant");
            yield return RuleEnter(HornetStoryScene, "right1");
            var manager = Field<EntityManager>(engine.ClientManager, "_entityManager");
            yield return Until(() => manager.IsSceneHostDetermined && EntityReferences.Entities.Values.Any(e => e.Type == EntityType.Hornet), "native Hornet additive boss registered");
            Check(manager.IsSceneHost == host, "host owns native first-entry authority");
            if (host) Flag("hornet.first-entered");
            Flag(role + ".hornet-entered"); yield return Until(() => All(".hornet-entered"), "all three inside same story scene");
            yield return new WaitForSecondsRealtime(1);
            var gates = HornetFsms().Where(f => f.FsmName == "BG Control").ToArray();
            Check(gates.Length == 2 && HornetFsms().Any(f => f.name == "Cloak Corpse") && HornetFsms().Any(f => f.name == "Dream Scene Activate"), "two native gates, cloak and story trigger present");
            HornetDump("entry");
            if (schema) {
                foreach (var f in HornetFsms().Where(f => HornetInteresting(f.gameObject))) {
                    foreach (var s in f.FsmStates) File.AppendAllText(Path.Combine(root, role + ".hornet-schema.txt"), f.name + "/" + f.FsmName + "/" + s.Name +
                        " => " + string.Join(",", s.Transitions.Select(t => t.EventName + ":" + t.ToState)) + " actions=" + string.Join(",", s.Actions.Select(a => a.GetType().Name)) + "\n");
                }
                yield break;
            }
            var boss = EntityReferences.Entities.Values.Single(e => e.Type == EntityType.Hornet);
            var hp = boss.Object.Host.GetComponent<HealthManager>();
            var encounter = HornetFsms().Single(f => f.name == "Hornet Infected Knight Encounter" && f.FsmName == "Encounter");
            var fightX = encounter.transform.position.x;
            var storyX = HornetFsms().Single(f => f.name == "Dream Scene Activate" && f.FsmName == "Control").GetComponent<Collider2D>().bounds.center.x;
            // Stage on native ground outside the arena's right gate. The long
            // approach corridor is outside this gate/terminal comparison.
            var rightGate = gates.OrderBy(g => g.transform.position.x).Last();
            hero.transform.position = hero.FindGroundPoint(rightGate.transform.position + Vector3.right * 3 + Vector3.up * 2, true) + Vector3.up * .1f;
            hero.GetComponent<Rigidbody2D>().velocity = Vector2.zero; hero.AffectedByGravity(true); Physics2D.SyncTransforms();
            Check(hero.transform.position.x > rightGate.transform.position.x + 1 && hero.transform.position.x > encounter.GetComponent<Collider2D>().bounds.max.x,
                "fixture stages outside right gate and real intro trigger");
            Log("HORNET ENTRY approach encounter=" + encounter.transform.position + " gate positions=" + string.Join(",", gates.Select(g => g.name + ":" + g.transform.position)));
            yield return HornetWalk(fightX, 55);
            Check(Mathf.Abs(hero.transform.position.x - fightX) < 2 || encounter.ActiveStateName != "Idle", "fixture reached native encounter trigger before any damage");
            yield return HornetWait(() => gates.All(g => g.ActiveStateName is "Close 2" or "Double Close"), 30);
            HornetDump("intro");
            Flag(role + ".hornet-staged"); yield return Until(() => All(".hornet-staged"), "all native intros observed");
            if (host) {
                var control = boss.Object.Host.GetComponents<PlayMakerFSM>().Single(f => f.FsmName == "Control");
                yield return Until(() => hp != null && hp.gameObject.activeInHierarchy && !hp.IsInvincible && hero.acceptingInput &&
                    control.ActiveStateName is not ("Init" or "Inert" or "Wake" or "Refight Ready" or "Refight Wake") &&
                    nativeFightStarted, "native Hornet intro completes and live fight becomes vulnerable", 40);
                Log("HORNET DAMAGE native ApplyExtraDamage amount=" + hp.hp);
                hp.ApplyExtraDamage(hp.hp);
                Flag("hornet.damage-applied");
            }
            yield return Until(() => Has("hornet.damage-applied"), "identical terminal damage applied by authority");
            yield return new WaitForSecondsRealtime(12);
            HornetDump("after-defeat");
            Flag(role + ".hornet-defeat-observed"); yield return Until(() => All(".hornet-defeat-observed"), "all before-pickup observations captured");
            // Each participant attempts its own native interaction in fixed order.
            foreach (var actor in roles) {
                if (role == actor) {
                    var shiny = HornetFsms().FirstOrDefault(f => f.FsmName == "Shiny Control" && f.transform.parent != null && f.transform.parent.name == "Cloak Corpse");
                    if (shiny != null && shiny.gameObject.activeInHierarchy) {
                        yield return HornetWalk(shiny.transform.position.x);
                        var end = Time.realtimeSinceStartup + 35;
                        while ((!pd.GetBool("hasDash") || !hero.acceptingInput || hero.controlReqlinquished) && Time.realtimeSinceStartup < end) {
                            yield return RulePress(InputHandler.Instance.inputActions.up);
                            yield return RulePress(InputHandler.Instance.inputActions.jump);
                            yield return RulePress(InputHandler.Instance.inputActions.menuSubmit);
                            yield return new WaitForSecondsRealtime(.3f);
                        }
                    } else Log("HORNET PICKUP unavailable localDash=" + pd.GetBool("hasDash") + " shiny=" + (shiny == null ? "null" : shiny.ActiveStateName));
                    HornetDump("pickup"); Flag(role + ".hornet-pickup-attempted");
                }
                yield return Until(() => Has(actor + ".hornet-pickup-attempted"), "fixed-order native pickup " + actor, 70);
            }
            var story = HornetFsms().Single(f => f.name == "Dream Scene Activate" && f.FsmName == "Control");
            yield return HornetWalk(storyX, 25);
            var dreamer = HornetFsms().Single(f => f.name == "Dreamer Scene 1" && f.FsmName == "Control");
            var dreamerTrigger = dreamer.GetComponent<Collider2D>();
            if (dreamerTrigger.enabled) yield return HornetWalk(dreamerTrigger.bounds.center.x, 15);
            var storyEnd = Time.realtimeSinceStartup + 55;
            while ((!nativeStoryEnded || !hero.acceptingInput || hero.controlReqlinquished || pd.GetBool("disablePause")) && Time.realtimeSinceStartup < storyEnd) {
                yield return RulePress(InputHandler.Instance.inputActions.jump);
                yield return RulePress(InputHandler.Instance.inputActions.menuSubmit);
                yield return new WaitForSecondsRealtime(.3f);
            }
            yield return new WaitForSecondsRealtime(3); HornetDump("after-story");
            Log("HORNET NATIVE STORY END observed=" + nativeStoryEnded + " actual=" + dreamer.ActiveStateName);
            var results = new System.Collections.Generic.List<object>();
            var leftExitTraversed = false;
            foreach (var gate in gates.OrderBy(g => g.transform.position.x)) {
                var x = gate.transform.position.x; var startX = hero.transform.position.x;
                var direction = startX < x ? 1f : -1f;
                yield return HornetWalk(x + direction * 1.5f, 12);
                var crossed = (hero.transform.position.x - x) * direction > .8f;
                if (gate == gates.OrderBy(g => g.transform.position.x).First()) leftExitTraversed = crossed;
                var blocking = gate.GetComponentsInChildren<Collider2D>(true).Any(c => c.enabled && !c.isTrigger && c.gameObject.activeInHierarchy);
                results.Add(new { gate = gate.name, state = gate.ActiveStateName, blocking, crossed, x, actualX = hero.transform.position.x });
                Log("HORNET GATE RESULT " + gate.name + " state=" + gate.ActiveStateName + " blocking=" + blocking + " crossed=" + crossed);
            }
            File.WriteAllText(Path.Combine(root, role + ".hornet-result.json"), Newtonsoft.Json.JsonConvert.SerializeObject(new {
                role, shared = runtime.Shared, defeated = pd.GetBool("hornet1Defeated"), dash = pd.GetBool("hasDash"), dreamer = pd.GetBool("dreamerScene1"),
                input = hero.acceptingInput, control = hero.controlReqlinquished, down = runtime.Down, gates = results
                , nativeStoryEnded, leftExitTraversed
            }, Newtonsoft.Json.Formatting.Indented));
            Flag(role + ".hornet-observed"); yield return Until(() => All(".hornet-observed"), "all three completed gate observations", 70);
        } finally { On.HutongGames.PlayMaker.Fsm.EnterState -= trace; }
    }
}
