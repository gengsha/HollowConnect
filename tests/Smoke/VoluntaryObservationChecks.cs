using System;
using System.Collections;
using System.IO;
using System.Linq;
using System.Reflection;
using HollowConnect;
using HollowConnect.Core;
using UnityEngine;

namespace HollowConnect.Smoke;

public sealed partial class RescueProbe {
    private Vector3 observationFixtureAnchor;
    private IEnumerator ObservationKey(KeyCode key) {
        var previous = runtime.KeyPressed;
        var pressed = false;
        runtime.KeyPressed = value => value == key && !pressed && (pressed = true);
        try { yield return Until(() => pressed, "actual runtime input consumed " + key, 3); }
        finally { runtime.KeyPressed = previous; }
        yield return null;
    }
    private IEnumerator ObservationBegin(string label) {
        yield return Until(() => HeroController.instance.cState.onGround && HeroController.instance.acceptingInput &&
            !HeroController.instance.controlReqlinquished && !HeroController.instance.cState.recoiling &&
            GameCameras.instance.cameraController.mode != CameraController.CameraMode.FROZEN, label + " native input ready");
        observationFixtureAnchor = HeroController.instance.transform.position;
        yield return ObservationKey(CoopMod.Settings.ObserveKey);
        yield return Until(() => runtime.Observing, label + " living observation entered", 3);
    }
    private IEnumerator ObservationStand() {
        var hero = HeroController.instance;
        hero.transform.position = StoryFloor(new Vector3(host ? 20 : 25, 25, 0), "Crossroads_01");
        hero.GetComponent<Rigidbody2D>().velocity = Vector2.zero;
        hero.AffectedByGravity(true); Physics2D.SyncTransforms();
        yield return Until(() => hero.cState.onGround && hero.acceptingInput && !hero.controlReqlinquished &&
            GameCameras.instance.cameraController.mode != CameraController.CameraMode.FROZEN,
            "observation fixture native ground and camera ready");
        hero.SetHazardRespawn(hero.transform.position, true);
    }

    private IEnumerator ObservationBenchArrival(bool connected = true) {
        yield return Until(() => !runtime.Observing && !runtime.Resetting && runtime.Playable &&
            UnityEngine.SceneManagement.SceneManager.GetActiveScene().name == "Town" && PlayerData.instance.GetBool("atBench") &&
            UnityEngine.Object.FindObjectsOfType<PlayMakerFSM>().Any(f => f.name == "RestBench" && f.FsmName == "Bench Control" && f.ActiveStateName == "Resting"),
            "native Town RestBench arrival and authoritative release", 85);
        Check(runtime.Connected == connected && HeroController.instance.GetComponent<Rigidbody2D>().simulated &&
            !HeroBox.inactive && !HeroController.instance.GetComponent<Renderer>().forceRenderingOff &&
            GameCameras.instance.cameraController.mode != CameraController.CameraMode.FROZEN,
            "bench restores real physics collision rendering camera and connection contract");
    }
    private IEnumerator VoluntaryObservationChecks() {
        using var externalDamage = IgnoreExternalCombatDamage();
        var observer = role == (Environment.GetEnvironmentVariable("HOLLOWCONNECT_OBSERVATION_ROLE") ?? "host");
        var scenario = Environment.GetEnvironmentVariable("HOLLOWCONNECT_OBSERVATION_SCENARIO") ?? "f4";
        Message delayedReturn = null;
        var holdReturn = observer && scenario == "early-up";
        using var delayedAck = new MonoMod.RuntimeDetour.Hook(typeof(CoopRuntime).GetMethod("Receive", BindingFlags.Instance | BindingFlags.NonPublic),
            new Action<Action<CoopRuntime, Message>, CoopRuntime, Message>((orig, owner, message) => {
                if (holdReturn && message.Kind == Kind.ObservationReturned) delayedReturn = message;
                else orig(owner, message);
            }));
        var linked = Environment.GetEnvironmentVariable("HOLLOWCONNECT_OBSERVATION_LINKED") == "1";
        var other = host ? "guest" : "host";
        yield return RuleEnter("Crossroads_01"); yield return ObservationStand();
        Flag(role + ".observation-ready");
        yield return Until(() => Has(other + ".observation-ready") && runtime.Peers.Count == 1 &&
            runtime.Peers.Values.Single().State?.Scene == "Crossroads_01" && runtime.PlayerObject(runtime.Peers.Single().Key) is { activeInHierarchy: true }, "both actual peer avatars in observation room");
        var hero = HeroController.instance; var pd = PlayerData.instance; var body = hero.GetComponent<Rigidbody2D>();
        if (linked) {
            yield return Until(() => Field<bool>(runtime, "linkedHealthStateReceived"), "linked pool initialized before non-healing return probe");
            if (!observer) pd.TakeHealth(1);
            yield return Until(() => pd.health == Field<int>(runtime, "linkedHealthMax") - 1, "both peers adopt deliberately damaged team pool");
        }
        var geo = pd.geo; var ownDeaths = CoopMod.Statistics.Get("rescue" + role);
        var rescues = runtime.RescuesUsed; var round = Hkmp.CoopContext.Epoch;
        if (linked) Check(runtime.LinkedHealthRuleEnabled, "linked rule actually negotiated");
        if (!observer) {
            yield return Until(() => Has("observation.active") && runtime.Peers.Values.Single().Life == Life.Observing, "server publishes observation instead of living participant");
            var avatar = runtime.PlayerObject(runtime.Peers.Single().Key);
            yield return Until(() => avatar.GetComponent<Renderer>().forceRenderingOff, "actual remote observing body hidden");
            hero.transform.position += Vector3.right * 10; body.velocity = Vector2.zero; Physics2D.SyncTransforms();
            Flag("observation.target-moved");
            yield return Until(() => Has("observation.trigger"), "observer verified ghost and pause");
            if (scenario == "departure") { yield return RuleEnter("Town"); Flag("observation.target-left"); }
            if (scenario == "wipe") {
                Die();
                yield return Until(() => Hkmp.CoopContext.Epoch > round && !runtime.Resetting && runtime.Playable && pd.GetBool("atBench"), "last fighter death releases wipe including living observer", 85);
                Flag("observation.target-wipe-complete");
            }
            yield return Until(() => Has("observation.finished"), "observer bench checks completed", 100);
            if (scenario != "wipe") Check(!runtime.Down && !runtime.Resetting && !runtime.Returning, "fighter remains living without party reset");
            if (scenario != "disconnect") yield return Until(() => runtime.Peers.Values.Single().Life == Life.Alive && runtime.Peers.Values.Single().State.Scene == "Town", "server reactivates observer only at Town");
            yield break;
        }
        var menu = CoopMod.Instance.GetMenuData(null).Single(e => e.Name == CoopMod.ObservationOptionName);
        Check(!new Settings().VoluntaryObservation && CoopMod.ObservationOptionName.Contains("Beta") && CoopMod.SharedHealthOptionName.Contains("Beta"), "default off and both Beta labels");
        menu.Saver(0); yield return ObservationKey(CoopMod.Settings.ObserveKey);
        Check(!runtime.Observing && hero.acceptingInput, "disabled menu leaves control alone"); menu.Saver(1);
        yield return ObservationBegin("noncombat start");
        yield return Until(() => Field<bool>(runtime, "observationAccepted"), "real server accepts reliable observation");
        Flag("observation.active");
        var anchor = hero.transform.position; var health = pd.health;
        Check(!body.simulated && HeroBox.inactive && hero.takeNoDamage && !hero.acceptingInput &&
            hero.GetComponentsInChildren<Collider2D>(true).All(c => !c.enabled), "local body has no physics input or collision");
        yield return null;
        Check(hero.GetComponent<Renderer>().forceRenderingOff && !runtime.Down && !runtime.Spectating, "living observation is hidden and distinct from death spectating");
        hero.TakeDamage(null, GlobalEnums.CollisionSide.left, 1, 1); yield return new WaitForSecondsRealtime(.2f);
        Check(pd.health == health && runtime.Observing, "actual damage cannot hurt observer or shared pool");
        var enemy = UnityEngine.Object.FindObjectsOfType<HealthManager>().FirstOrDefault(h => !h.isDead && h.hp > 0 && h.gameObject.activeInHierarchy);
        Check(enemy != null, "native enemy available for outgoing attack check");
        var hp = enemy.hp;
        enemy.Hit(new HitInstance { Source = hero.gameObject, DamageDealt = 1, AttackType = AttackTypes.Nail, Multiplier = 1 });
        Check(enemy.hp == hp, "observer outgoing native nail damage blocked");
        var detached = new GameObject("Observation detached companion probe");
        try {
            enemy.Hit(new HitInstance { Source = detached, DamageDealt = 1, AttackType = AttackTypes.Nail, Multiplier = 1 });
            Check(enemy.hp == hp, "detached local companion cannot bypass observation attack guard");
        } finally { UnityEngine.Object.Destroy(detached); }
        yield return Until(() => Has("observation.target-moved"), "actual remote target moved");
        yield return new WaitForSecondsRealtime(.3f);
        var camera = GameCameras.instance.cameraController;
        var expected = camera.KeepWithinSceneBounds(runtime.PlayerObject(runtime.ObservationTarget).transform.position);
        Check(Vector2.Distance(camera.transform.position, expected) < .25f && Vector2.Distance(hero.transform.position, anchor) < .01f, "camera follows remote avatar while body stays fixed");
        yield return ObservationKey(CoopMod.Settings.SelectTeammateKey);
        UIManager.instance.TogglePauseGame();
        yield return Until(() => GameManager.instance.isPaused, "native pause menu opens");
        yield return new WaitForSecondsRealtime(.5f);
        Check(runtime.Observing && !runtime.ObservationReturning && !body.simulated, "pause menu preserves noncombat observation");
        UIManager.instance.TogglePauseGame(); yield return Until(() => !GameManager.instance.isPaused, "native pause resumes observation");
        Check(runtime.Observing && !hero.acceptingInput, "resume cannot restore combat input to observer");
        Flag("observation.trigger");
        if (scenario == "f4" || scenario == "early-up") yield return ObservationKey(CoopMod.Settings.ObserveKey);
        else if (scenario == "disconnect") engine.ClientManager.Disconnect();
        else if (scenario == "native") {
            GameManager.instance.BeginSceneTransition(new GameManager.SceneLoadInfo { SceneName = "Town", EntryGateName = "left1", Visualization = GameManager.SceneLoadVisualizations.Default });
        } else if (scenario != "departure" && scenario != "wipe") throw new Exception("Unknown observation scenario " + scenario);
        if (scenario == "early-up") {
            yield return Until(() => delayedReturn != null && Field<bool>(runtime, "observationBenchArrived") && pd.GetBool("atBench"),
                "server return acknowledgement delayed after proven native bench arrival", 85);
            yield return RuleBenchUp();
            Check(runtime.Observing && !pd.GetBool("atBench"), "native bench can be left before delayed reliable acceptance");
            holdReturn = false; Inject(delayedReturn);
            yield return Until(() => !runtime.Observing && hero.acceptingInput && body.simulated && runtime.Connected,
                "late acknowledgement releases observation after native bench get-up");
        } else yield return ObservationBenchArrival(scenario != "disconnect");
        Check(pd.geo == geo && CoopMod.Statistics.Get("rescue" + role) == ownDeaths,
            "return creates no formal death or geo loss");
        Check(runtime.RescuesUsed == rescues, "normal bench rules preserve expected fresh rescue allowance");
        if (scenario != "wipe") Check(Hkmp.CoopContext.Epoch == round, "personal observation exit leaves party epoch intact");
        else yield return Until(() => Has("observation.target-wipe-complete"), "fighter also completes wipe");
        if (linked && scenario != "wipe") Check(pd.health == health, "native observer respawn cannot refill team pool");
        yield return RuleBenchUp();
        Check(hero.acceptingInput && !hero.controlReqlinquished && body.simulated, "native bench departure restores actual movement");
        Flag("observation.finished");
    }
}

