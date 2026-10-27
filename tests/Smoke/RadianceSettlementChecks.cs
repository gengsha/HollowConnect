using System;
using System.Collections;
using System.Linq;
using System.Reflection;
using HollowConnect;
using HollowConnect.Core;
using MonoMod.RuntimeDetour;
using UnityEngine;

namespace HollowConnect.Smoke;

public sealed partial class CoverageProbe {
    private IEnumerator RadianceFinalAttackInput() {
        var action = InputHandler.Instance.inputActions.attack;
        var frame = Time.frameCount + 1;
        using (new Hook(typeof(InControl.OneAxisInputControl).GetProperty("WasPressed").GetGetMethod(),
            new Func<Func<InControl.OneAxisInputControl, bool>, InControl.OneAxisInputControl, bool>((orig, control) =>
                ReferenceEquals(control, action) ? Time.frameCount == frame : orig(control))))
        using (new Hook(typeof(InControl.OneAxisInputControl).GetProperty("IsPressed").GetGetMethod(),
            new Func<Func<InControl.OneAxisInputControl, bool>, InControl.OneAxisInputControl, bool>((orig, control) =>
                ReferenceEquals(control, action) ? Time.frameCount == frame : orig(control))))
            yield return new WaitForSecondsRealtime(.15f);
    }
    private IEnumerator RadianceSettlementFinal(Hkmp.Game.Client.Entity.Entity[] steps) {
        using var personalRecords = new CoverageFinalBossEndingChecks(Mark);
        using var endingCallTrace = new Hook(typeof(HutongGames.PlayMaker.Actions.CallMethodProper).GetMethod("OnEnter"),
            new Action<Action<HutongGames.PlayMaker.Actions.CallMethodProper>, HutongGames.PlayMaker.Actions.CallMethodProper>((orig, action) => {
                var inspect = action.methodName.Value == "ChangeToScene" || action.State.Name == "Ending Scene";
                if (inspect) {
                    foreach (var p in action.parameters) p.UpdateValue();
                    var owner = Hkmp.Game.Client.Entity.EntityReferences.Owner(action.Fsm.GameObject);
                    Mark("FINAL RADIANCE CALL before scene=" + Hkmp.CoopContext.LocalScene + " owner=" + owner?.Type + "/" + owner?.SourceScene +
                        " fsm=" + action.Fsm.Name + "/" + action.State.Name + " target=" + action.Fsm.GetOwnerDefaultTarget(action.gameObject)?.name + " behaviour=" + action.behaviour.Value + " method=" + action.methodName.Value +
                        " parameters=" + string.Join(",", action.parameters.Select(p => p.GetValue() + ":" + p.Type)) +
                        " actions=" + string.Join(",", action.State.Actions.Select(a => a.GetType().Name + ":" + a.Enabled)));
                }
                orig(action);
                if (inspect) Mark("FINAL RADIANCE CALL after scene=" + Hkmp.CoopContext.LocalScene + " owner=" +
                    Hkmp.Game.Client.Entity.EntityReferences.Owner(action.Fsm.GameObject)?.Type + " flag=" + StaticVariableList.GetValue<bool>("ggCinematicEnding"));
            }));
        var mode = Environment.GetEnvironmentVariable("HOLLOWCONNECT_RADIANCE_SETTLEMENT");
        var hero = HeroController.instance;
        var body = hero.GetComponent<Rigidbody2D>();
        var pd = PlayerData.instance;
        var original = Hkmp.CoopContext.LocalScene;
        // The story Radiance's network scene id maps to Unity's internal
        // Dream_Final scene name.
        var originalGameScene = original == "Dream_Final_Boss" ? "Dream_Final" : original;
        var beforeCharges = runtime.RescuesUsed;
        var transitioned = false; var leftDefeated = false;
        On.HutongGames.PlayMaker.Fsm.hook_EnterState stateTrace = (orig, fsm, state) => {
            if (GameManager.instance.sceneName == originalGameScene &&
                (fsm.GameObject.name == "Boss Control" || fsm.GameObject.name == "Absolute Radiance"))
                Mark("FINAL RADIANCE native FSM " + fsm.GameObject.name + "/" + fsm.Name + "=" + state.Name);
            orig(fsm, state);
        };
        On.HutongGames.PlayMaker.Fsm.EnterState += stateTrace;
        On.GameManager.hook_BeginSceneTransition trace = (orig, gm, info) => {
            Mark("FINAL RADIANCE native transition " + gm.sceneName + " -> " + info.SceneName + "/" + info.EntryGateName +
                " down=" + runtime.Down + " spectator=" + runtime.Spectating);
            if (gm.sceneName == originalGameScene) { transitioned = true; leftDefeated |= runtime.Down || runtime.Spectating; }
            orig(gm, info);
        };
        On.GameManager.BeginSceneTransition += trace;
        On.GameManager.hook_ChangeToScene legacyTrace = (orig, gm, scene, gate, delay) => {
            Mark("FINAL RADIANCE native legacy transition " + gm.sceneName + " -> " + scene + "/" + gate +
                " down=" + runtime.Down + " spectator=" + runtime.Spectating);
            if (gm.sceneName == originalGameScene) { transitioned = true; leftDefeated |= runtime.Down || runtime.Spectating; }
            orig(gm, scene, gate, delay);
        };
        On.GameManager.ChangeToScene += legacyTrace;
        try {
            if (mode != "alive") {
                if (!host) {
                    pd.SetInt("health", 0);
                    hero.StartCoroutine((IEnumerator)typeof(HeroController).GetMethod("Die", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(hero, null));
                    yield return Until(() => runtime.Down && !runtime.Spectating, "final Radiance participant enters actual cooperative death");
                    if (mode == "spectator") {
                        var pressed = false;
                        runtime.KeyPressed = key => key == CoopMod.Settings.GiveUpKey && !pressed && (pressed = true);
                        yield return Until(() => runtime.Spectating, "final Radiance configured voluntary spectate key");
                        runtime.KeyPressed = _ => false;
                    }
                } else yield return Until(() => runtime.Peers.Values.Single().Life == (mode == "spectator" ? Life.Spectating : Life.Down),
                    "final Radiance survivor observes defeated participant");
            }
            Flag(role + ".final-radiance-ready");
            yield return Until(() => Has((host ? "guest" : "host") + ".final-radiance-ready"), "both final Radiance participants ready");
            var boss = RadianceBossEntity();
            var control = boss.Object.Host.LocateMyFSM("Control");
            if (!runtime.Down) {
                var top = steps.Select(e => manager.IsSceneHost ? e.Object.Host : e.Object.Client)
                    .SelectMany(go => go.GetComponentsInChildren<Collider2D>(true)).Where(RadiancePlatform).OrderByDescending(c => c.bounds.max.y).First();
                var feet = hero.GetComponent<Collider2D>().bounds;
                hero.transform.position = new Vector3(top.bounds.center.x, top.bounds.max.y + hero.transform.position.y - feet.min.y + .1f, hero.transform.position.z);
                body.velocity = Vector2.zero; hero.AffectedByGravity(true); Physics2D.SyncTransforms();
                yield return Until(() => RadianceSupported(), "living final Radiance participant on highest native support");
            }
            if (original == "Dream_Final_Boss" && manager.IsSceneHost) {
                var nativeBoss = boss.Object.Host;
                var bounds = nativeBoss.GetComponentsInChildren<Collider2D>(true).Where(c => c.enabled && c.gameObject.activeInHierarchy)
                    .Select(c => c.name + ":trigger=" + c.isTrigger + ":" + c.bounds).ToArray();
                Mark("STORY RADIANCE ASCEND HIT PREP state=" + control.ActiveStateName + " hero=" + hero.transform.position +
                    " boss=" + nativeBoss.transform.position + " colliders=" + string.Join(";", bounds));
                if (control.ActiveStateName != "Ascend Cast")
                    yield return RadianceEndingWait(() => control.ActiveStateName == "Ascend Cast", "story Radiance reaches native final nail trigger", 20);
                hero.transform.position = nativeBoss.transform.position + Vector3.down * 1.6f;
                body.velocity = Vector2.zero; hero.AffectedByGravity(false); Physics2D.SyncTransforms();
                var hitDeadline = Time.realtimeSinceStartup + 8;
                var strikes = 0;
                while (control.ActiveStateName == "Ascend Cast" && Time.realtimeSinceStartup < hitDeadline) {
                    // The story Radiance's native final phase listens for a
                    // "Nail Attack" collision in Trigger2dEvent, unlike the
                    // Godhome variant's Final Idle/HealthManager path below.
                    hero.Attack(GlobalEnums.AttackDirection.upward);
                    strikes++;
                    Mark("STORY RADIANCE FINAL UPSTRIKE " + strikes + " hero=" + hero.transform.position +
                        " boss=" + nativeBoss.transform.position + " state=" + control.ActiveStateName);
                    yield return new WaitForSecondsRealtime(.45f);
                }
                if (control.ActiveStateName == "Ascend Cast")
                    throw new Exception("Native story Radiance Ascend Cast did not receive the player's upward nail attack; hero=" +
                        hero.transform.position + " boss=" + nativeBoss.transform.position + " colliders=" + string.Join(";", bounds));
                Mark("PASS story Radiance native upward nail reached " + control.ActiveStateName + " after " + strikes + " strikes");
                Flag("final-radiance.nail-ready");
            }
            if (manager.IsSceneHost && original != "Dream_Final_Boss") {
                var hp = boss.Object.Host.GetComponent<HealthManager>();
                var deadline = Time.realtimeSinceStartup + 40;
                var previous = "";
                while (control.ActiveStateName != "Death Ready" && !(original == "Dream_Final_Boss" && control.ActiveStateName != "Ascend Cast") &&
                       Time.realtimeSinceStartup < deadline) {
                    if (control.ActiveStateName != previous) { previous = control.ActiveStateName; Mark("FINAL RADIANCE phase=" + previous + " hp=" + hp.hp); }
                    if (control.ActiveStateName == "Final Idle" && hp.hp >= 720)
                        hp.Hit(new HitInstance { Source = hero.gameObject, AttackType = AttackTypes.Nail, DamageDealt = 100,
                            Multiplier = 1, IgnoreInvulnerable = true, Direction = 0, MagnitudeMultiplier = 1 });
                    yield return new WaitForSecondsRealtime(.2f);
                }
                if (control.ActiveStateName != "Death Ready") throw new Exception("Native final Radiance phase did not reach final nail trigger");
                Flag("final-radiance.nail-ready");
            }
            yield return Until(() => Has("final-radiance.nail-ready"), "authority reaches real final Radiance nail trigger");
            if (host && original != "Dream_Final_Boss") {
                var visible = manager.IsSceneHost ? boss.Object.Host : boss.Object.Client;
                hero.transform.position = visible.transform.position + Vector3.left * 1.5f;
                body.velocity = Vector2.zero; hero.AffectedByGravity(false); hero.FaceRight(); Physics2D.SyncTransforms();
                hero.Attack(GlobalEnums.AttackDirection.normal);
            }
            yield return Until(() => !runtime.Down && !runtime.Spectating, "final Radiance settlement revives before native ending");
            if (runtime.RescuesUsed != beforeCharges) throw new Exception("Final Radiance settlement consumed a rescue charge");
            var endingDeadline = Time.realtimeSinceStartup + 65;
            while (!transitioned && Time.realtimeSinceStartup < endingDeadline) {
                yield return RadianceFinalAttackInput();
                yield return new WaitForSecondsRealtime(.35f);
            }
            if (!transitioned) {
                Mark("FINAL RADIANCE TIMEOUT sequence=" + BossTravel.CurrentSequenceName + " " + RadianceState());
                foreach (var fsm in UnityEngine.Object.FindObjectsOfType<PlayMakerFSM>(true)
                    .Where(f => f.gameObject.scene.name == original && (f.name == "Boss Control" || f.name.Contains("Radiance"))))
                    Mark("FINAL RADIANCE TIMEOUT FSM " + fsm.name + "/" + fsm.FsmName + "=" + fsm.ActiveStateName + " enabled=" + fsm.enabled);
                throw new Exception("Native final Radiance ending did not consume real attack inputs");
            }
            if (leftDefeated) throw new Exception("Final Radiance transitioned while participant remained defeated");
            yield return RadianceEndingWait(() => Hkmp.CoopContext.LocalScene != original && !GameManager.instance.IsInSceneTransition,
                "native final Radiance destination loaded", 50);
            if (Hkmp.CoopContext.LocalScene is not ("Cinematic_Ending_C" or "Cinematic_Ending_D" or "Cinematic_Ending_E"))
                throw new Exception("Final Radiance participant missed its native cinematic: " + Hkmp.CoopContext.LocalScene);
            Mark("FINAL RADIANCE destination cinematic flag=" + StaticVariableList.GetValue<bool>("ggCinematicEnding"));
            Mark("PASS final Radiance native ending participation scene=" + Hkmp.CoopContext.LocalScene + " mode=" + mode);
            if (Hkmp.CoopContext.LocalScene == "Cinematic_Ending_C" &&
                Environment.GetEnvironmentVariable("HOLLOWCONNECT_RADIANCE_ENDING_RETURN") == "1")
                yield return RadianceStoryEndingReturn();
            Flag(role + ".final-radiance-checked");
            yield return Until(() => Has((host ? "guest" : "host") + ".final-radiance-checked"), "both final Radiance destinations loaded");
        } finally { On.GameManager.BeginSceneTransition -= trace; On.GameManager.ChangeToScene -= legacyTrace;
            On.HutongGames.PlayMaker.Fsm.EnterState -= stateTrace; runtime.KeyPressed = _ => false; }
    }

    private IEnumerator RadianceStoryEndingReturn() {
        var cameras = GameCameras.instance;
        var canvas = cameras.hudCanvas;
        var initialPosition = canvas.transform.localPosition;
        var pd = PlayerData.instance;
        var slot = GameManager.instance.profileID;
        Mark("STORY ENDING HUD BEFORE " + GameManager.instance.sceneName + " camera=" +
            cameras.hudCamera.gameObject.activeInHierarchy + "/" + cameras.hudCamera.enabled + " canvas=" +
            canvas.activeSelf + "/" + canvas.activeInHierarchy + " pos=" + initialPosition);
        Flag(role + ".radiance-ending-c-ready");
        yield return RadianceEndingWait(() => Has((host ? "guest" : "host") + ".radiance-ending-c-ready"),
            "both story Radiance players entered native ending C", 50);

        // Serialize each player's native video/credits exit. This reproduces
        // the real C -> credits -> ending-helper path without concurrent
        // room returns. SkipVideo and LoadNextScene are the game's own input
        // boundaries; no scene or FSM state is forced.
        if (!host)
            yield return RadianceEndingWait(() => Has("host.radiance-ending-hud-returned"),
                "host returned from native story credits first", 100);

        var cinematic = UnityEngine.Object.FindObjectsOfType<CinematicPlayer>().Single();
        var videoDeadline = Time.realtimeSinceStartup + 40;
        while (!Field<bool>(cinematic, "videoTriggered") && Time.realtimeSinceStartup < videoDeadline)
            yield return null;
        if (!Field<bool>(cinematic, "videoTriggered"))
            throw new Exception("Native Radiance ending video did not start before skip");
        Mark("STORY ENDING VIDEO type=" + cinematic.videoType + " skip=" + cinematic.skipMode +
            " locked=" + cinematic.startSkipLocked + " triggered=" + Field<bool>(cinematic, "videoTriggered"));
        cinematic.StartCoroutine(cinematic.SkipVideo());
        yield return RadianceEndingWait(() => GameManager.instance.sceneName == "End_Credits",
            "native story ending video advances to credits", 45);
        var helperDeadline = Time.realtimeSinceStartup + 45;
        CutsceneHelper helper = null;
        while (helper == null && Time.realtimeSinceStartup < helperDeadline) {
            helper = UnityEngine.Object.FindObjectsOfType<CutsceneHelper>().FirstOrDefault();
            yield return null;
        }
        if (helper == null) throw new Exception("Native story credits has no CutsceneHelper");
        Mark("STORY ENDING CREDITS helper=" + helper.name + " next=" + helper.nextSceneType +
            " skip=" + helper.skipMode + " locked=" + helper.startSkipLocked);
        On.CutsceneHelper.hook_DoSceneLoad exitTrace = (orig, self) => {
            Mark("STORY ENDING NATIVE EXIT next=" + self.nextSceneType + " source=" +
                self.gameObject.scene.name + " camera=" + GameCameras.instance.hudCamera.gameObject.activeInHierarchy);
            orig(self);
        };
        On.CutsceneHelper.DoSceneLoad += exitTrace;
        try {
            helper.LoadNextScene();
            yield return RadianceEndingWait(() => GameManager.instance.sceneName == "Town" && runtime.Playable &&
                GameManager.instance.HasFinishedEnteringScene && HeroController.instance.isHeroInPosition,
                "native story credits returns to personal checkpoint", 75);
            var restoreDeadline = Time.realtimeSinceStartup + 10;
            while ((!cameras.hudCamera.gameObject.activeInHierarchy || !cameras.hudCamera.enabled ||
                    !canvas.activeInHierarchy || canvas.GetComponents<PlayMakerFSM>().Any(f => !f.enabled || !f.gameObject.activeInHierarchy) ||
                    Vector3.Distance(canvas.transform.localPosition, initialPosition) >= .05f) &&
                   Time.realtimeSinceStartup < restoreDeadline) yield return null;
            if (!cameras.hudCamera.gameObject.activeInHierarchy || !cameras.hudCamera.enabled ||
                !canvas.activeInHierarchy || canvas.GetComponents<PlayMakerFSM>().Any(f => !f.enabled || !f.gameObject.activeInHierarchy) ||
                Vector3.Distance(canvas.transform.localPosition, initialPosition) >= .05f)
                throw new Exception("Story ending return left the native health/soul HUD camera, canvas or FSM hidden: camera=" +
                    cameras.hudCamera.gameObject.activeInHierarchy + "/" + cameras.hudCamera.enabled + " canvas=" +
                    canvas.activeSelf + "/" + canvas.activeInHierarchy + " fsms=" +
                    string.Join(",", canvas.GetComponents<PlayMakerFSM>().Select(f => f.FsmName + ":" + f.enabled + ":" + f.gameObject.activeInHierarchy + ":" + f.ActiveStateName)) +
                    " pos=" + canvas.transform.localPosition);
            if (!runtime.Connected || PlayerData.instance != pd || GameManager.instance.profileID != slot)
                throw new Exception("Native story ending checkpoint return did not preserve the player's active session");
            Mark("PASS story Radiance native credits return restores health/soul HUD camera=" +
                cameras.hudCamera.gameObject.activeInHierarchy + "/" + cameras.hudCamera.enabled + " canvas=" + canvas.activeInHierarchy +
                " fsms=" + string.Join(",", canvas.GetComponents<PlayMakerFSM>().Select(f => f.FsmName + ":" + f.enabled + ":" + f.ActiveStateName)));
            Flag(role + ".radiance-ending-hud-returned");
        } finally { On.CutsceneHelper.DoSceneLoad -= exitTrace; }
    }

    private IEnumerator RadianceEndingWait(Func<bool> ready, string label, float seconds) {
        var deadline = Time.realtimeSinceStartup + seconds;
        while (!ready() && Time.realtimeSinceStartup < deadline) yield return null;
        if (!ready()) throw new Exception("Story ending: timed out waiting for " + label +
            " in " + GameManager.instance.sceneName);
        Mark("PASS story ending " + label);
    }
}
