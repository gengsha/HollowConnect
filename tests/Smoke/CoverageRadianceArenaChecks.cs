using System;
using System.Collections;
using System.IO;
using System.Linq;
using System.Reflection;
using Hkmp.Game.Client.Entity;
using UnityEngine;

namespace HollowConnect.Smoke;

public sealed partial class CoverageProbe {
    private string[] RadianceArenaSnapshot() => Entities.Values.Where(e => e.Type == EntityType.RadiancePlatform)
        .OrderBy(e => e.Id).Select(e => {
            var go = manager.IsSceneHost ? e.Object.Host : e.Object.Client;
            var colliders = go.GetComponentsInChildren<Collider2D>(true)
                .Count(c => c.enabled && !c.isTrigger && c.gameObject.activeInHierarchy);
            var renderers = go.GetComponentsInChildren<Renderer>(true)
                .Count(r => r.enabled && r.gameObject.activeInHierarchy);
            var sprite = go.GetComponent<tk2dBaseSprite>();
            var animator = go.GetComponent<tk2dSpriteAnimator>();
            Mark("RADIANCE PLATFORM VISUAL id=" + e.Id + " group=" + go.transform.parent?.name + " sprite=" + sprite?.spriteId +
                " animation=" + animator?.CurrentClip?.name + "/" + (animator?.CurrentClip == null ? -1 : animator.CurrentFrame) + " playing=" + animator?.Playing +
                " bounds=" + go.GetComponent<Renderer>()?.bounds);
            return e.Id + " colliders=" + colliders + " renderers=" + renderers +
                " pos=" + go.transform.position.ToString("F1") + " scale=" + go.transform.lossyScale.ToString("F1") +
                " alpha=" + (sprite == null ? "none" : sprite.color.a.ToString("F1"));
        }).ToArray();

    private IEnumerator RadianceArenaCompare(int round, string label) {
        var file = Path.Combine(root, role + ".arena." + round + "." + label);
        File.WriteAllLines(file, RadianceArenaSnapshot());
        yield return RadianceTogether(round, "arena snapshot " + label);
        var expected = File.ReadAllLines(Path.Combine(root, (host ? "guest" : "host") + ".arena." + round + "." + label));
        var actual = RadianceArenaSnapshot();
        var difference = expected.Except(actual).Concat(actual.Except(expected)).ToArray();
        Mark("RADIANCE ARENA COMPARE " + label + " platforms=" + actual.Length + " difference=" + string.Join(";", difference));
        if (difference.Length != 0) radianceArenaDiverged = true;
        var cameraFile = Path.Combine(root, role + ".camera." + round + "." + label);
        var cameras = RadianceVisible().transform.parent.GetComponentsInChildren<CameraLockArea>(true)
            .Where(a => !a.name.Contains("Intro") && !a.name.Contains("Death"))
            .Select(a => a.name + "=" + a.gameObject.activeInHierarchy).OrderBy(s => s).ToArray();
        File.WriteAllLines(cameraFile, cameras);
        yield return RadianceTogether(round, "camera snapshot " + label);
        var expectedCameras = File.ReadAllLines(Path.Combine(root, (host ? "guest" : "host") + ".camera." + round + "." + label));
        var cameraDifference = expectedCameras.Except(cameras).Concat(cameras.Except(expectedCameras)).ToArray();
        Mark("RADIANCE CAMERA COMPARE " + label + " difference=" + string.Join(";", cameraDifference));
        if (cameraDifference.Length != 0) radianceArenaDiverged = true;
        if (!manager.IsSceneHost && RadianceVisible().transform.parent.Find("Challenge Prompt Radiant")?.gameObject.activeInHierarchy == true)
            throw new Exception("Radiance late player retains a challenge interaction after battle start");
    }
    private bool radianceArenaDiverged;
    private IEnumerator RadianceStoryChallenge() {
        var arena = RadianceBossEntity().Object.Host.transform.parent;
        var prompt = arena.GetComponentsInChildren<PlayMakerFSM>(true).Single(f => f.name == "Challenge Prompt Radiant" && f.FsmName == "Challenge Start");
        yield return RadianceWait(() => RadiancePlayable() && UnityEngine.Object.FindObjectsOfType<Collider2D>().Any(RadiancePlatform),
            "story native challenge platforms ready", 15);
        var support = UnityEngine.Object.FindObjectsOfType<Collider2D>().Where(RadiancePlatform)
            .OrderBy(c => ((Vector2)c.bounds.center - (Vector2)prompt.transform.position).sqrMagnitude).First();
        var hero = HeroController.instance;
        var feet = hero.GetComponent<Collider2D>().bounds;
        hero.transform.position = new Vector3(support.bounds.center.x,
            support.bounds.max.y + hero.transform.position.y - feet.min.y + .06f, hero.transform.position.z);
        hero.GetComponent<Rigidbody2D>().velocity = Vector2.zero;
        Physics2D.SyncTransforms();
        yield return RadianceWait(() => hero.cState.onGround && prompt.Fsm.ActiveState.Actions.OfType<HutongGames.PlayMaker.Actions.ListenForUp>().Any(),
            "story native challenge prompt in range", 5);
        // Same input boundary as native bench get-up: use the listener's
        // configured event after real trigger contact, never set a boss state.
        prompt.Fsm.Event(prompt.Fsm.ActiveState.Actions.OfType<HutongGames.PlayMaker.Actions.ListenForUp>().Single().wasPressed);
        Mark("RADIANCE STORY native challenge up event");
    }
    private void RadianceArenaDump(string label) {
        var control = RadianceBossEntity().Object.Host.LocateMyFSM("Control");
        foreach (var name in new[] { "Plat Sets", "Plats P2A", "Plats Climb", "Plats Ascend", "Plat Hazard" }) {
            var go = control.FsmVariables.GetFsmGameObject(name)?.Value;
            if (go == null) continue;
            Mark("RADIANCE GROUP " + label + " " + name + " path=" + EntityIdentity.Path(go.transform) +
                " self=" + go.activeSelf + " hierarchy=" + go.activeInHierarchy + " scripts=" +
                string.Join(",", go.GetComponents<Component>().Where(c => c != null).Select(c => c.GetType().Name)));
            if (name != "Plat Sets") continue;
            foreach (var fsm in go.GetComponentsInChildren<PlayMakerFSM>(true))
                if (fsm.FsmName != "radiant_plat" || fsm.name == "Radiant Plat Small (2)" ||
                    fsm.transform.parent?.name == "Ascend Set" && fsm.name == "Radiant Plat Wide (2)") RadianceDumpFsm(fsm);
        }
        var sceneRoot = control.transform.parent;
        foreach (var area in sceneRoot.GetComponentsInChildren<CameraLockArea>(true))
            Mark("RADIANCE CAMERA CONFIG " + label + " " + area.name + " self=" + area.gameObject.activeSelf +
                " hierarchy=" + area.gameObject.activeInHierarchy + " pos=" + area.transform.position);
        foreach (var fsm in sceneRoot.GetComponentsInChildren<PlayMakerFSM>(true))
            if (fsm.name.Contains("Detector") || fsm.name.Contains("CamLock")) {
                Mark("RADIANCE LOCAL " + label + " " + EntityIdentity.Path(fsm.transform) + " active=" + fsm.gameObject.activeSelf +
                    "/" + fsm.gameObject.activeInHierarchy + " state=" + fsm.ActiveStateName);
                RadianceDumpFsm(fsm);
            }
        if (manager.IsSceneHost) RadianceDumpFsm(control);
    }
    private IEnumerator RadianceArenaAssertions(int round) {
        radianceArenaDiverged = false;
        RadianceArenaDump("after late");
        yield return new WaitForSecondsRealtime(2);
        yield return RadianceCapture("phase-two");
        var battlePlatforms = RadianceGroup("P2 SetA").Concat(RadianceGroup("Hazard Plat")).ToArray();
        yield return RadianceRenderSupports(battlePlatforms, RadianceBossEntity().Type == EntityType.Radiance ? 8 : 7, "phase-two");
        yield return RadianceArenaCompare(round, "late");
        if (RadianceSpectatorCheck) yield return RadianceStartSpectator(round);
        var steps = Entities.Values.Where(e => e.Type == EntityType.RadiancePlatform && e.Object.Host.transform.parent?.name == "Ascend Set").ToArray();
        var expectedSteps = RadianceBossEntity().Type == EntityType.Radiance ? 15 : 18;
        if (steps.Length != expectedSteps || steps.Any(e => (manager.IsSceneHost ? e.Object.Host : e.Object.Client).GetComponentsInChildren<Collider2D>().Any(RadiancePlatform)))
            throw new Exception("Radiance ascent fixture requires stairs to start hidden/non-solid");
        if (manager.IsSceneHost) {
            var boss = RadianceVisible();
            var health = boss.GetComponent<HealthManager>();
            var phase = boss.LocateMyFSM("Phase Control");
            var deadline = Time.realtimeSinceStartup + 40;
            while (phase.ActiveStateName != "Set Ascend" && Time.realtimeSinceStartup < deadline) {
                if (health.hp <= 1) throw new Exception("Radiance ascent native Hit failed to advance phase");
                if (phase.ActiveStateName == "Idle 4")
                    health.Hit(new HitInstance { Source = HeroController.instance.gameObject, AttackType = AttackTypes.Nail,
                        DamageDealt = Math.Min(100, health.hp - 1), Multiplier = 1, IgnoreInvulnerable = true, Direction = 0, MagnitudeMultiplier = 1 });
                yield return new WaitForSecondsRealtime(.4f);
            }
            if (phase.ActiveStateName != "Set Ascend") throw new Exception("Radiance native ascend phase timed out");
            Flag("radiance.arena.ascend." + round);
        }
        yield return RadianceWait(() => Has("radiance.arena.ascend." + round), "native damage requests ascent", 45);
        var generated = new System.Collections.Generic.Dictionary<ulong, float>();
        var started = Time.realtimeSinceStartup;
        while (Time.realtimeSinceStartup - started < 12) {
            foreach (var step in steps) {
                var go = manager.IsSceneHost ? step.Object.Host : step.Object.Client;
                if (!generated.ContainsKey(step.Id) && go.GetComponentsInChildren<Collider2D>().Any(RadiancePlatform))
                    generated.Add(step.Id, Time.realtimeSinceStartup - started);
            }
            yield return new WaitForSecondsRealtime(.1f);
        }
        Mark("RADIANCE ASCENT TIMELINE " + string.Join(";", generated.OrderBy(p => p.Value).Select(p => p.Key + "=" + p.Value.ToString("F1"))));
        if (generated.Count != steps.Length)
            throw new Exception("Radiance ascent stairs failed to become solid: " + generated.Count + "/" + steps.Length);
        var timeline = Path.Combine(root, role + ".ascent-times." + round);
        File.WriteAllLines(timeline, generated.Select(p => p.Key + " " + p.Value.ToString(System.Globalization.CultureInfo.InvariantCulture)));
        yield return RadianceTogether(round, "ascent times");
        var peerTimes = File.ReadAllLines(Path.Combine(root, (host ? "guest" : "host") + ".ascent-times." + round))
            .Select(line => line.Split(' ')).ToDictionary(v => ulong.Parse(v[0]), v => float.Parse(v[1], System.Globalization.CultureInfo.InvariantCulture));
        if (generated.Any(p => !peerTimes.TryGetValue(p.Key, out var time) || Math.Abs(time - p.Value) > 2))
            throw new Exception("Radiance staircase activation timing differs from native authority");
        RadianceArenaDump("ascend");
        yield return RadianceArenaCompare(round, "ascend");
        if (radianceArenaDiverged) throw new Exception("Radiance late/ascent platform set differs between peers");
        if (RadianceSpectatorCheck) yield return RadianceSpectatorAscent(round, steps);
        else yield return RadianceRenderSupports(steps, expectedSteps, "ascent");
        Mark("PASS radiance complete late/ascent platform set matches");
        if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable("HOLLOWCONNECT_RADIANCE_SETTLEMENT")))
            yield return RadianceSettlementFinal(steps);
    }

    private static void RadianceConfigureFifthSequence() {
        var sequence = Resources.Load<BossSequence>("GG/Boss Sequence Tier 5");
        BossSequenceController.SetupNewSequence(sequence, BossSequenceController.ChallengeBindings.None, "bossDoorStateTier5");
        typeof(BossSequenceController).GetField("bossIndex", BindingFlags.Static | BindingFlags.NonPublic)
            .SetValue(null, Enumerable.Range(0, sequence.Count).First(i => sequence.GetSceneAt(i) == "GG_Radiance"));
    }
    private IEnumerator RadianceFreshLateCase() {
        var scene = Environment.GetEnvironmentVariable("HOLLOWCONNECT_RADIANCE_FRESH_SCENE") ?? "GG_Radiance";
        bool InArena() => runtime.Playable && UnityEngine.SceneManagement.SceneManager.GetActiveScene().name == scene;
        var nativeEntry = Environment.GetEnvironmentVariable("HOLLOWCONNECT_RADIANCE_FRESH_ENTRY") == "native";
        var authority = host != (Environment.GetEnvironmentVariable("HOLLOWCONNECT_COVERAGE_AUTHORITY") == "guest");
        On.HeroController.hook_TakeDamage isolate = (orig, self, source, side, amount, hazard) => {
            if (self != HeroController.instance || hazard > 1) orig(self, source, side, amount, hazard);
        };
        On.HeroController.TakeDamage += isolate;
        try {
            if (RadianceSpectatorCheck && scene == "GG_Radiance") yield return RadiancePrepareSpectatorReturn();
            if (authority) {
                BossSequenceController.Reset();
                PlayerData.instance.SetInt("bossStatueTargetLevel", 0);
                if (Environment.GetEnvironmentVariable("HOLLOWCONNECT_RADIANCE_FRESH_P5") == "1") {
                    RadianceConfigureFifthSequence();
                }
                HeroController.instance.AffectedByGravity(true);
                Enter(scene, scene == "Dream_Final_Boss" ? "door1" : "door_dreamEnter");
                if (scene == "Dream_Final_Boss") {
                    var entering = Time.realtimeSinceStartup + 50;
                    while (!(InArena() && manager.IsSceneHostDetermined) && Time.realtimeSinceStartup < entering) {
                        var h = HeroController.instance;
                        Mark("STORY ENTRY scene=" + GameManager.instance.sceneName + " active=" + UnityEngine.SceneManagement.SceneManager.GetActiveScene().name + " changing=" + GameManager.instance.IsInSceneTransition +
                            " inPosition=" + h.isHeroInPosition + " state=" + GameManager.instance.gameState + " hero=" + h.transform.position +
                            " transition=" + h.transitionState + " body=" + h.GetComponent<Rigidbody2D>().velocity +
                            "/" + h.GetComponent<Rigidbody2D>().gravityScale + "/" + h.GetComponent<Rigidbody2D>().bodyType);
                        yield return new WaitForSecondsRealtime(2);
                    }
                }
                yield return Until(() => InArena() && manager.IsSceneHostDetermined, "fresh first arena authority");
                Mark("ARENA SCENES active=" + UnityEngine.SceneManagement.SceneManager.GetActiveScene().name + " gm=" + GameManager.instance.sceneName +
                    " boss=" + RadianceVisible().scene.name + " root=" + RadianceVisible().transform.parent?.name);
                if (!manager.IsSceneHost) throw new Exception("Fresh arena first entrant is not authority");
                var hero = HeroController.instance;
                hero.takeNoDamage = true;
                hero.AffectedByGravity(true);
                if (scene == "Dream_Final_Boss") yield return RadianceStoryChallenge();
                yield return RadianceWait(() => RadiancePlayable() && RadianceVisible().activeInHierarchy,
                    "fresh native intro complete", 45);
                if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable("HOLLOWCONNECT_RADIANCE_SETTLEMENT"))) {
                    var ending = RadianceVisible().LocateMyFSM("Control").FsmStates.Single(s => s.Name == "Ending Scene");
                    Mark("FINAL RADIANCE NATIVE ENDING ACTIONS " + string.Join(",", ending.Actions.Select(a => a.GetType().FullName + ":" + a.Enabled)));
                    foreach (var call in ending.Actions.OfType<HutongGames.PlayMaker.Actions.CallMethodProper>()) {
                        foreach (var p in call.parameters) p.UpdateValue();
                        Mark("FINAL RADIANCE NATIVE ENDING PARAMETERS " + string.Join(",", call.parameters.Select(p => p.GetValue() + ":" + p.Type)));
                    }
                    var sequence = Resources.Load<BossSequence>("GG/Boss Sequence Tier 5");
                    Mark("FINAL RADIANCE SEQUENCE custom=" + sequence.customEndScene + " flag=" + sequence.customEndScenePlayerData +
                        " value=" + PlayerData.instance.GetBool(sequence.customEndScenePlayerData));
                }
                var phase = RadianceVisible().LocateMyFSM("Phase Control");
                var health = RadianceVisible().GetComponent<HealthManager>();
                var deadline = Time.realtimeSinceStartup + 80;
                while (phase.ActiveStateName != "Stun 1" && Time.realtimeSinceStartup < deadline) {
                    if (phase.ActiveStateName.StartsWith("Idle ", StringComparison.Ordinal))
                        health.Hit(new HitInstance { Source = hero.gameObject, AttackType = AttackTypes.Nail,
                            DamageDealt = Math.Min(100, health.hp - 1), Multiplier = 1, IgnoreInvulnerable = true, Direction = 0, MagnitudeMultiplier = 1 });
                    yield return new WaitForSecondsRealtime(.4f);
                }
                if (phase.ActiveStateName != "Stun 1") throw new Exception("Fresh arena native first stun timed out");
                PlayerData.instance.SetInt("health", 9);
                hero.takeNoDamage = false;
                hero.cState.invulnerable = false;
                yield return RadianceWait(() => phase.ActiveStateName == "Idle 4" && RadianceSupported() && RadiancePlayable(),
                    "authority reaches native arena two without late player", 40);
                Freeze();
                Flag("radiance.fresh.ready");
            } else {
                if (!In(RadianceSpectatorCheck && scene == "GG_Radiance" ? "GG_Atrium" : "Town"))
                    throw new Exception("Fresh late entrant must wait outside the unvisited arena");
                var wait = Time.realtimeSinceStartup + 140;
                while (!Has("radiance.fresh.ready") && Time.realtimeSinceStartup < wait) yield return null;
                if (!Has("radiance.fresh.ready")) throw new Exception("Fresh arena authority did not reach phase two");
                yield return Until(() => runtime.Peers.Values.Single().State?.IsReady == true &&
                    (bool)typeof(CoopRuntime).GetProperty("CanStartWarp", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(runtime, null), "fresh late requester ready");
                if (nativeEntry) {
                    // This entrant is already running the same Pantheon.
                    // A native gate load does not adopt another player's
                    // runtime context like an accepted F8 request does.
                    if (Environment.GetEnvironmentVariable("HOLLOWCONNECT_RADIANCE_FRESH_P5") == "1")
                        RadianceConfigureFifthSequence();
                    HeroController.instance.AffectedByGravity(true);
                    HeroController.instance.takeNoDamage = false;
                    PlayerData.instance.SetInt("health", 9);
                    Enter(scene, scene == "Dream_Final_Boss" ? "door1" : "door_dreamEnter");
                    Mark("RADIANCE LATE ENTRY native destination gate");
                } else {
                    runtime.Selected = runtime.Peers.Keys.Single();
                    var handled = Field<uint>(runtime, "handledWarp");
                    var pressed = false;
                    runtime.KeyPressed = key => key == CoopMod.Settings.TeleportKey && !pressed && (pressed = true);
                    yield return Until(() => pressed && Field<uint>(runtime, "handledWarp") > handled, "fresh late F8 granted");
                    runtime.KeyPressed = _ => false;
                }
                yield return Until(() => InArena() && !Field<bool>(runtime, "changingScene") &&
                    engine.ClientManager.SceneSnapshotReady, "first ever arena visit applies snapshot");
                yield return RadianceWait(() => RadiancePlayable() && RadianceSupported(), "fresh late lands on platform", nativeEntry ? 25 : 5);
                Freeze();
            }
            yield return RadianceTogether(0, "fresh entered");
            yield return RadianceArenaAssertions(0);
            ProbeNetworkFaults.Verify(root, role);
        } finally {
            On.HeroController.TakeDamage -= isolate;
            runtime.KeyPressed = _ => false;
        }
    }
}
