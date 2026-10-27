using System;
using System.Collections;
using System.Linq;
using System.Reflection;
using Hkmp;
using Hkmp.Game.Client.Entity;
using HollowConnect;
using HollowConnect.Core;
using HutongGames.PlayMaker.Actions;
using UnityEngine;
using Object = UnityEngine.Object;

namespace HollowConnect.Smoke;

public sealed partial class RescueProbe {
    private string RuleCase => Environment.GetEnvironmentVariable("HOLLOWCONNECT_RESCUE_RULE_CHECK");
    private void ConfigureRescueRuleCheck() {
        CoopMod.Settings.Rescue = RuleCase != "off-spectate" && RuleCase != "native-control";
        CoopMod.Settings.RescueLimit = RuleCase == "traveller" ? 0 : 1;
        CoopMod.Settings.SpectateAfterExhaustion = RuleCase != "traveller" && RuleCase != "no-spectate" && RuleCase != "native-control";
    }
    private IEnumerator RuleEnter(string scene, string gate = "left1") {
        HeroController.instance.takeNoDamage = true;
        GameManager.instance.BeginSceneTransition(new GameManager.SceneLoadInfo { SceneName = scene, EntryGateName = gate,
            Visualization = GameManager.SceneLoadVisualizations.Default, WaitForSceneTransitionCameraFade = true });
        yield return Until(() => runtime.Playable && GameManager.instance.sceneName == scene && CoopContext.LocalScene == scene &&
            UnityEngine.SceneManagement.SceneManager.GetActiveScene().name == scene &&
            !GameManager.instance.IsInSceneTransition && engine.ClientManager.SceneSnapshotReady && HeroController.instance.acceptingInput,
            "native scene entry " + scene);
        HeroController.instance.takeNoDamage = true;
    }
    private IEnumerator RuleBenchUp() {
        if (!PlayerData.instance.GetBool("atBench")) yield break;
        yield return Until(() => !PlayerData.instance.GetBool("atBench") || Object.FindObjectsOfType<PlayMakerFSM>().Any(f =>
            f.FsmName == "Bench Control" && f.ActiveStateName == "Resting"), "native returned bench animation finishes", 20);
        if (!PlayerData.instance.GetBool("atBench")) yield break;
        var bench = Object.FindObjectsOfType<PlayMakerFSM>().First(f => f.FsmName == "Bench Control" && f.ActiveStateName == "Resting");
        yield return RulePress(InputHandler.Instance.inputActions.jump);
        yield return Until(() => !PlayerData.instance.GetBool("atBench") && HeroController.instance.acceptingInput &&
            !HeroController.instance.controlReqlinquished && !HeroController.instance.GetComponent<Rigidbody2D>().isKinematic, "native bench get-up");
    }
    private IEnumerator RulePress(InControl.PlayerAction action) {
        var frame = Time.frameCount + 1;
        using (new MonoMod.RuntimeDetour.Hook(typeof(InControl.OneAxisInputControl).GetProperty("WasPressed").GetGetMethod(),
            new Func<Func<InControl.OneAxisInputControl, bool>, InControl.OneAxisInputControl, bool>((orig, control) => ReferenceEquals(control, action) ? Time.frameCount == frame : orig(control))))
        using (new MonoMod.RuntimeDetour.Hook(typeof(InControl.OneAxisInputControl).GetProperty("IsPressed").GetGetMethod(),
            new Func<Func<InControl.OneAxisInputControl, bool>, InControl.OneAxisInputControl, bool>((orig, control) => ReferenceEquals(control, action) ? Time.frameCount >= frame : orig(control))))
            yield return new WaitForSecondsRealtime(.15f);
    }
    private void RuleReturn() => typeof(CoopRuntime).GetMethod("RequestPersonalReturn", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(runtime, null);
    private IEnumerator RuleArrived(string label) {
        yield return Until(() => !runtime.Down && !runtime.Returning && !runtime.Resetting && runtime.Playable &&
            GameManager.instance.sceneName == "Town" && !GameManager.instance.IsInSceneTransition && engine.ClientManager.SceneSnapshotReady,
            label + " native personal death arrival", 60);
        yield return Until(() => !runtime.Spectating && HeroController.instance.GetComponent<Rigidbody2D>().simulated &&
            GameCameras.instance.cameraController.mode != CameraController.CameraMode.FROZEN, label + " camera physics and spectator lock released", 15);
        Check(!HeroController.instance.GetComponent<Renderer>().forceRenderingOff, label + " returned local body visible");
    }
    private IEnumerator RuleNailRescue(string prefix) {
        if (!host) {
            Die(); yield return Until(() => runtime.Down && !runtime.Spectating, prefix + " real shadow death"); Flag(prefix + ".down");
            yield return Until(() => !runtime.Down && PlayerData.instance.health == 1 && runtime.RescuesUsed == 1, prefix + " accepted revive consumes quota");
            Check(Field<double>(runtime, "rescueCountUntil") > (DateTime.UtcNow - new DateTime(2020, 1, 1)).TotalSeconds,
                "rescue consumption starts temporary remaining-count notice");
        } else {
            yield return Until(() => Has(prefix + ".down") && runtime.Peers.Values.Any(p => p.Life == Life.Down), prefix + " peer shadow visible");
            var target = runtime.Peers.Single();
            var shadeService = Field<ShadeService>(runtime, "shades");
            yield return Until(() => shadeService.Get(target.Key) != null, prefix + " native shade visual ready");
            var pos = shadeService.Get(target.Key).transform.position;
            var hero = HeroController.instance; hero.transform.position = pos + Vector3.left * 2;
            hero.GetComponent<Rigidbody2D>().velocity = Vector2.zero;
            hero.FaceRight(); hero.Attack(GlobalEnums.AttackDirection.normal); Physics2D.SyncTransforms();
            yield return Until(() => runtime.Peers[target.Key].Life == Life.Alive, prefix + " actual nail collision revives peer");
        }
        Flag(prefix + "." + role); yield return Until(() => Has(prefix + "." + (host ? "guest" : "host")), prefix + " both ready");
    }
    private IEnumerator RescueRuleChecks() {
        var cameraSampling = false; var cameraSamples = 0; var cameraError = 0f;
        On.CameraController.LateUpdate += (orig, camera) => {
            orig(camera);
            if (!cameraSampling || !runtime.Spectating || !runtime.Down || runtime.Peers.Count != 1) return;
            var avatar = runtime.PlayerObject(runtime.Peers.Single().Key);
            if (avatar == null) return;
            cameraSamples++;
            cameraError = Mathf.Max(cameraError, Vector2.Distance(camera.transform.position, camera.KeepWithinSceneBounds(avatar.transform.position)));
        };
        using var ruleLifeTrace = new MonoMod.RuntimeDetour.Hook(typeof(CoopRuntime).GetMethod("Receive", BindingFlags.Instance | BindingFlags.NonPublic),
            new Action<Action<CoopRuntime, Message>, CoopRuntime, Message>((orig, owner, message) => {
                if (message.Kind == Kind.Returning || message.Kind == Kind.Down || message.Kind == Kind.BossVictory)
                    Log("RULE LIFE " + message.Kind + " reason=" + message.Name + " scene=" + message.Scene + " serial=" + message.Serial);
                orig(owner, message);
            }));
        On.GameManager.BeginSceneTransition += (orig, gm, info) => {
            Log("RULE NATIVE TRANSITION " + gm.sceneName + " -> " + info.SceneName + "/" + info.EntryGateName);
            orig(gm, info);
        };
        var other = host ? "guest" : "host";
        yield return RuleBenchUp();
        if (!host) yield return Until(() => Has("rule.host-entered"), "host first boss entrant");
        yield return RuleEnter("Crossroads_04");
        var hero = HeroController.instance; var body = hero.GetComponent<Rigidbody2D>();
        yield return Until(() => hero.cState.onGround, "boss room native supported ground");
        hero.SetHazardRespawn(hero.transform.position, true);
        Check(runtime.InBossEncounter(), "native boss is detected for room failure");
        if (host) Flag("rule.host-entered");
        Flag(role + ".rule-ready");
        yield return Until(() => Has(other + ".rule-ready") && runtime.Peers.Count == 1, "both boss participants ready");
        yield return Until(() => runtime.PlayerObject(runtime.Peers.Single().Key) != null &&
            runtime.Peers.Values.Single().Life == Life.Alive, "peer visual handoff fixture ready");
        CheckSpectatorVisualHandoff();
        if (RuleCase == "visibility-only") {
            Flag(role + ".visibility-checked");
            yield return Until(() => Has(other + ".visibility-checked"), "both visual handoff checks complete");
            yield break;
        }
        if (RuleCase != "traveller" && RuleCase != "native-control") {
            // A story shade now stays beside the death instead of a distant
            // marker. Keep this rule fixture inside the room, so positioning
            // the rescuer beside an entry-side shade cannot enter an exit.
            var boss = EntityReferences.Entities.Values.First(e => e.Type == EntityType.GruzMother && e.Object.Host != null);
            var floor = StoryFloor(boss.Object.Host.transform.position, "Crossroads_04");
            hero.transform.position = floor + Vector3.right * (host ? -3 : 3); body.velocity = Vector2.zero;
            hero.AffectedByGravity(true); Physics2D.SyncTransforms();
            yield return new WaitForFixedUpdate();
            yield return Until(() => hero.cState.onGround && hero.acceptingInput, "rule actor on native interior support");
            hero.SetHazardRespawn(hero.transform.position, true);
            Check(Object.FindObjectsOfType<TransitionPoint>().Where(t => t.gameObject.scene.name == "Crossroads_04")
                .All(t => Vector2.Distance(t.transform.position, hero.transform.position) > 5), "rule fixture stays clear of native exits");
            Flag(role + ".rule-arena-ready");
            yield return Until(() => Has(other + ".rule-arena-ready"), "both rule actors inside native room");
        }
        var initialEpoch = CoopContext.Epoch;
        if (RuleCase == "native-control") {
            if (!host) { Die(); yield return RuleArrived("native control"); yield return RuleBenchUp(); Flag("rule.native-control"); }
            else yield return Until(() => Has("rule.native-control"), "native control completed", 100);
            yield break;
        }
        if (RuleCase == "traveller") {
            if (!host) {
                Die(); yield return Until(() => runtime.Down, "guest enters rescue shade"); RuleReturn();
                yield return RuleArrived("guest give up"); yield return RuleBenchUp();
                yield return RuleEnter("Crossroads_01"); Freeze(); Flag("rule.guest-travelling");
                yield return Until(() => Has("rule.host-returned"), "last combatant finished personal failure return");
                Check(GameManager.instance.sceneName == "Crossroads_01" && CoopContext.Epoch == initialEpoch && !runtime.Returning && !runtime.Resetting && !runtime.Down,
                    "travelling player remains alive in original travelling scene and epoch");
            } else {
                yield return Until(() => Has("rule.guest-travelling"), "teammate is travelling outside boss room");
                Die(); yield return RuleArrived("last boss combatant");
                Check(CoopContext.Epoch == initialEpoch, "boss failure does not reset unrelated scene epoch"); Flag("rule.host-returned");
            }
        } else {
            if (RuleCase != "off-spectate" && RuleCase != "voluntary") yield return RuleNailRescue("rule.first-rescue");
            if (!host) {
                Die();
                if (RuleCase == "no-spectate") {
                    yield return RuleArrived("exhausted guest");
                    Check(runtime.RescuesUsed == 1, "native bench respawn does not refill quota"); Flag("rule.guest-exhausted");
                } else {
                    if (RuleCase == "voluntary") {
                        yield return Until(() => runtime.Down && !runtime.Spectating, "voluntary give-up starts as a rescue shade");
                        var serial = Field<uint>(runtime, "downSerial");
                        var pressed = false;
                        runtime.KeyPressed = key => key == CoopMod.Settings.GiveUpKey && !pressed && (pressed = true);
                        yield return Until(() => runtime.Spectating && runtime.Down, "actual configured give-up key switches to spectator");
                        runtime.KeyPressed = _ => false;
                        Check(Field<uint>(runtime, "downSerial") == serial && runtime.RescuesUsed == 0 && GameManager.instance.sceneName == "Crossroads_04",
                            "voluntary spectating keeps same death room and unused quota");
                    }
                    yield return Until(() => runtime.Spectating && runtime.Down, "exhausted or disabled rescue enters spectator");
                    yield return new WaitForSecondsRealtime(.5f);
                    Check(Object.FindObjectsOfType<ShadeView>().Length == 0 && !body.simulated && !hero.acceptingInput,
                        "spectator has no rescue shade and cannot move or attack");
                    Flag("rule.guest-spectating");
                    yield return Until(() => Has("rule.host-moved"), "living teammate moved during spectating");
                    var peer = runtime.Peers.Single();
                    yield return Until(() => Vector2.Distance(hero.transform.position, runtime.PlayerObject(peer.Key).transform.position) < .5f,
                        "spectator follows living teammate position");
                    Flag("rule.spectator-checked");
                    cameraSampling = true;
                    var samples = 0; var sawLoading = false;
                    while (!Has("rule.damage-done")) {
                        yield return null;
                        var target = runtime.PlayerObject(peer.Key);
                        if (target == null) continue;
                        sawLoading |= runtime.Peers[peer.Key].Life == Life.Loading;
                        if (Vector2.Distance(hero.transform.position, target.transform.position) >= .8f)
                            throw new Exception("spectator flashed to death anchor during hurt: " + hero.transform.position + " target=" + target.transform.position);
                        samples++;
                    }
                    Check(samples > 10 && sawLoading, "actual hurt includes Loading frames without returning to death anchor");
                    cameraSampling = false;
                    Check(cameraSamples > 10 && cameraError < .8f, "every hurt camera frame stays on watched teammate; samples=" + cameraSamples + " maxError=" + cameraError);
                    Flag("rule.damage-checked");
                    if (RuleCase == "spectator-wipe") yield return RuleArrived("spectator after room failure");
                    else {
                        yield return Until(() => !runtime.Down && !runtime.Spectating && !runtime.Returning && hero.acceptingInput,
                            "victory restores spectator in original boss room");
                        Check(!hero.GetComponent<Renderer>().forceRenderingOff, "victory restores local spectator body visibility");
                        Check(GameManager.instance.sceneName == "Crossroads_04" && PlayerData.instance.health == 1 &&
                            runtime.RescuesUsed == (RuleCase == "voluntary" || RuleCase == "off-spectate" ? 0 : 1),
                            "settlement revives without spending or refilling budget");
                        Check(Field<double>(runtime, "rescueCountUntil") <= (DateTime.UtcNow - new DateTime(2020, 1, 1)).TotalSeconds,
                            "expired consumption notice stays hidden after free settlement");
                        Flag("rule.settlement-checked");
                        yield return Until(() => Has("rule.settlement-visibility-checked"), "living peer observes restored spectator body");
                        Die();
                        yield return Until(() => runtime.Down || runtime.Returning || GameManager.instance.sceneName != "Crossroads_04", "new death after settlement");
                        if (runtime.Down) RuleReturn();
                        yield return RuleArrived("explicit post-settlement personal return");
                    }
                }
                // Exercise a real bench FSM input, rather than setting atBench.
                yield return RuleBenchUp();
                var bench = Object.FindObjectsOfType<PlayMakerFSM>().First(f => f.name == "RestBench" && f.FsmName == "Bench Control");
                hero.transform.position = bench.transform.position + new Vector3(-1, 1.5f, 0); body.velocity = Vector2.zero; hero.AffectedByGravity(true);
                yield return Until(() => bench.ActiveStateName == "In Range" && hero.cState.onGround, "native bench in range after return");
                yield return RulePress(InputHandler.Instance.inputActions.up);
                yield return Until(() => bench.ActiveStateName == "Resting" && PlayerData.instance.GetBool("atBench") && runtime.RescuesUsed == 0,
                    "actual bench rest restores personal rescue quota");
                Flag("rule.guest-rested");
            } else {
                if (RuleCase == "no-spectate") yield return Until(() => Has("rule.guest-exhausted"), "exhausted teammate returned without spectating");
                else {
                    yield return Until(() => Has("rule.guest-spectating") && runtime.Peers.Values.Single().Life == Life.Spectating, "server confirms peer spectator");
                    yield return Until(() => runtime.PlayerObject(runtime.Peers.Single().Key).transform.parent.GetComponentsInChildren<Renderer>(true)
                        .Where(r => r.name == "Username").All(r => r.forceRenderingOff), "spectator name hidden beside living teammate");
                    hero.transform.position += Vector3.right * 4; body.velocity = Vector2.zero;
                    Flag("rule.host-moved"); yield return Until(() => Has("rule.spectator-checked"), "spectator follow checked");
                    for (var hit = 0; hit < 2; hit++) {
                        hero.takeNoDamage = false; PlayerData.instance.SetInt("health", 5);
                        hero.TakeDamage(null, GlobalEnums.CollisionSide.left, 1, 1);
                        yield return new WaitForSecondsRealtime(2.2f);
                    }
                    Flag("rule.damage-done"); yield return Until(() => Has("rule.damage-checked"), "damage camera sampling complete");
                    Check(!runtime.RequestShadeHit(runtime.Peers.Single().Key, runtime.Peers.Single().Value.DownSerial), "spectator cannot be shade rescued");
                    if (RuleCase == "spectator-wipe") {
                        Die(); yield return RuleArrived("all boss participants defeated");
                    } else {
                        var boss = EntityReferences.Entities.Values.First(e => e.Type == EntityType.GruzMother && e.Object.Host != null);
                        var hp = boss.Object.Host.GetComponent<HealthManager>(); hp.ApplyExtraDamage(Math.Max(1, hp.hp));
                        yield return Until(() => Has("rule.settlement-checked"), "spectator participated in local victory");
                        yield return RuleRemoteVisibility("victory revives spectator");
                        Flag("rule.settlement-visibility-checked");
                    }
                }
                yield return Until(() => Has("rule.guest-rested"), "teammate completed personal real bench rest", 80);
            }
            if (RuleCase == "spectator-wipe") {
                Flag(role + ".visibility-return-ready");
                yield return Until(() => Has(other + ".visibility-return-ready"), "both players returned to Town");
                yield return RuleRemoteVisibility("room failure reuses pooled avatar");
            }
        }
        Flag(role + ".rule-checked"); yield return Until(() => Has(other + ".rule-checked"), "both rule checks complete");
    }
}
