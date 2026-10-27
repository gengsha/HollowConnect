using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using Hkmp.Game.Client.Entity;
using HutongGames.PlayMaker;
using UnityEngine;
using Object = UnityEngine.Object;

namespace HollowConnect.Smoke;

public sealed partial class RescueProbe {
    private bool SoulMasterCheck => !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("HOLLOWCONNECT_SOUL_MASTER_CHECK"));
    private string SoulMode => Environment.GetEnvironmentVariable("HOLLOWCONNECT_SOUL_MASTER_CHECK");
    private bool SoulVerify => SoulMode == "verify";
    private string SoulArrival => Environment.GetEnvironmentVariable("HOLLOWCONNECT_SOUL_ARRIVAL") ?? "outside";
    private bool SoulLate => SoulArrival is "phase1" or "phase2" or "complete";
    private bool SoulFirst => role == (Environment.GetEnvironmentVariable("HOLLOWCONNECT_SOUL_FIRST") ?? "host");
    private const string SoulScene = "Ruins1_24";
    private bool soulAuthoritySkippedCascade;

    private void SoulDump(string label) {
        var lines = new List<string> { DateTime.UtcNow.ToString("O") + " " + label };
        foreach (var fsm in Object.FindObjectsOfType<PlayMakerFSM>(true).Where(f => f.gameObject.scene.name.StartsWith(SoulScene))) {
            lines.Add("FSM " + EntityIdentity.Path(fsm.transform) + "/" + fsm.FsmName + " state=" + fsm.ActiveStateName +
                " pos=" + fsm.transform.position + " active=" + fsm.gameObject.activeInHierarchy + " scene=" + fsm.gameObject.scene.name);
            lines.Add("VARS " + string.Join(",", fsm.FsmVariables.IntVariables.Select(v => v.Name + "=" + v.Value)) + " / " +
                string.Join(",", fsm.FsmVariables.GameObjectVariables.Select(v => v.Name + "=" + (v.Value == null ? "null" : v.Value.name))));
            foreach (var state in fsm.FsmStates) {
                lines.Add(" STATE " + state.Name + " -> " + string.Join(",", state.Transitions.Select(t => t.EventName + ":" + t.ToState)));
                foreach (var action in state.Actions) lines.Add("  " + action.GetType().Name + " " + string.Join(",", action.GetType()
                    .GetFields(BindingFlags.Instance | BindingFlags.Public).Select(f => f.Name + "=" + f.GetValue(action))));
            }
        }
        File.AppendAllLines(Path.Combine(root, role + ".soul-schema.txt"), lines);
        foreach (var health in Object.FindObjectsOfType<HealthManager>(true).Where(h => h.gameObject.scene.name.StartsWith(SoulScene)))
            Log("SOUL HEALTH " + EntityIdentity.Path(health.transform) + " hp=" + health.hp + " special=" + health.hasSpecialDeath +
                " active=" + health.gameObject.activeInHierarchy + " pos=" + health.transform.position);
        foreach (var gate in Object.FindObjectsOfType<TransitionPoint>(true).Where(g => g.gameObject.scene.name.StartsWith(SoulScene)))
            Log("SOUL EXIT " + gate.name + " at=" + gate.transform.position);
        foreach (var collider in Object.FindObjectsOfType<Collider2D>(true).Where(c => c.gameObject.scene.name.StartsWith(SoulScene) &&
            (c.name.StartsWith("Battle Gate") || c.name == "Start Range")))
            Log("SOUL COLLIDER " + EntityIdentity.Path(collider.transform) + " bounds=" + collider.bounds + " enabled=" + collider.enabled);
    }

    private IEnumerator SoulMasterChecks() {
        var hero = HeroController.instance;
        Check(runtime.Shared == (Environment.GetEnvironmentVariable("HOLLOWCONNECT_SOUL_SHARED") == "1"), "requested permanent progress rule negotiated");
        if (Environment.GetEnvironmentVariable("HOLLOWCONNECT_LOCAL_BARRIER_REPLAY_CHECK") == "1") {
            yield return LocalBarrierEventChecks.Run(Check);
            yield return PickupConditionChecks.Run(Check);
        }
        if (PlayerData.instance.GetBool("atBench")) {
            var bench = Object.FindObjectsOfType<PlayMakerFSM>().Single(f => f.name == "RestBench" && f.FsmName == "Bench Control");
            yield return Until(() => bench.ActiveStateName == "Resting", "initial native bench ready");
            bench.Fsm.Event(bench.Fsm.ActiveState.Actions.OfType<HutongGames.PlayMaker.Actions.ListenForJump>().Single().wasPressed);
            yield return Until(() => !PlayerData.instance.GetBool("atBench") && hero.acceptingInput, "native bench get-up");
        }
        On.HutongGames.PlayMaker.Fsm.hook_EnterState trace = (orig, fsm, state) => {
            if (SoulFirst && fsm.Name == "Pickup" && state.Name == "Instant Activate" &&
                fsm.GameObject != null && fsm.GameObject.scene.name == SoulScene) soulAuthoritySkippedCascade = true;
            if (fsm.GameObject != null && fsm.GameObject.scene.name.StartsWith(SoulScene))
                File.AppendAllText(Path.Combine(root, role + ".soul-states.txt"), DateTime.UtcNow.ToString("O") + " " +
                    EntityIdentity.Path(fsm.GameObject.transform) + "/" + fsm.Name + " => " + state.Name + "\n");
            orig(fsm, state);
        };
        On.HutongGames.PlayMaker.Fsm.EnterState += trace;
        try {
            if (!SoulFirst) yield return Until(() => Has("soul.first-ready"), "first entrant establishes scene authority");
            if (!SoulFirst && SoulLate) {
                var arrival = SoulArrival == "phase1" ? "soul.fighting" : SoulArrival == "phase2" ? "soul.phase2-killed" : "soul.real-spell-collected";
                yield return Until(() => Has(arrival), "late entrant remains in Town until " + SoulArrival, 120);
            }
            GameManager.instance.BeginSceneTransition(new GameManager.SceneLoadInfo {
                SceneName = SoulScene, EntryGateName = "right1", Visualization = GameManager.SceneLoadVisualizations.Default,
                WaitForSceneTransitionCameraFade = true
            });
            yield return Until(() => UnityEngine.SceneManagement.SceneManager.GetSceneByName(SoulScene).isLoaded, "native Soul Master scene loaded");
            SoulDump("scene loaded");
            var manager = Field<EntityManager>(engine.ClientManager, "_entityManager");
            yield return Until(() => GameManager.instance.sceneName == SoulScene && !GameManager.instance.IsInSceneTransition &&
                runtime.Playable && hero.acceptingInput && engine.ClientManager.SceneSnapshotReady && manager.IsSceneHostDetermined,
                "native Soul Master entry ready");
            hero.takeNoDamage = true;
            Log("SOUL HERO ready at=" + hero.transform.position + " authority=" + manager.IsSceneHost);
            SoulDump("entry ready");
            if (SoulFirst) Flag("soul.first-ready");
            Flag(role + ".soul-entry");
            if (!SoulLate) yield return Until(() => Has((host ? "guest" : "host") + ".soul-entry"), "both entrants ready");
            yield return new WaitForSecondsRealtime(2);
            SoulDump("both entries observed");
            if (SoulMode == "inspect") Check(!PlayerData.instance.GetBool("mageLordDefeated"), "read-only inspection did not complete story boss");
            else if (!SoulFirst && SoulLate) yield return SoulLateEntryChecks();
            else yield return SoulCombatChecks(manager);
        } finally { On.HutongGames.PlayMaker.Fsm.EnterState -= trace; }
    }

    private IEnumerator SoulCombatChecks(EntityManager manager) {
        var hero = HeroController.instance;
        var entities = Field<Dictionary<ulong, Entity>>(manager, "_entities");
        yield return Until(() => entities.Values.Any(e => e.Type == EntityType.SoulMaster), "native story Soul Master registered");
        Check(manager.IsSceneHost == SoulFirst, "first entrant owns native boss authority");
        var first = entities.Values.Single(e => e.Type == EntityType.SoulMaster);
        var phase2 = entities.Values.Single(e => e.Type == EntityType.SoulMasterPhase2);
        GameObject Local(Entity e) => manager.IsSceneHost ? e.Object.Host : e.Object.Client;
        PlayMakerFSM Control(Entity e, string name) => Local(e)?.GetComponents<PlayMakerFSM>().Single(f => f.FsmName == name);
        var phase1Control = Control(first, "Mage Lord");
        hero.transform.position = new Vector3(SoulFirst ? 20 : 40, 31, hero.transform.position.z);
        hero.GetComponent<Rigidbody2D>().velocity = Vector2.zero;
        hero.AffectedByGravity(SoulFirst);
        Physics2D.SyncTransforms();
        Flag(role + ".soul-positioned");
        if (!SoulLate) yield return Until(() => Has((host ? "guest" : "host") + ".soul-positioned"), "one hero inside and other outside arena gate");
        if (SoulFirst) {
            yield return Until(() => phase1Control != null && phase1Control.ActiveStateName is not ("Sleep" or "Pause" or "Init" or "Encountered?"),
                "native collision starts Soul Master intro");
            Flag("soul.intro-started");
            yield return Until(() => phase1Control.ActiveStateName == "Idle", "original intro completes and native boss starts fighting");
            Flag("soul.fighting");
        } else yield return Until(() => Has("soul.fighting"), "authority native intro finishes while local hero remains outside");
        yield return new WaitForSecondsRealtime(.5f);
        SoulDump("native fight started; other hero outside");
        Check(!PlayerData.instance.GetBool("mageLordDefeated"), "initial boss intro is not victory");
        if (SoulVerify) {
            if (!SoulFirst) yield return SoulCrossGate("Battle Gate", -1, "late hero walks through upper gate during live phase one");
            Flag(role + ".soul-fight-checked");
            if (!SoulLate) yield return Until(() => Has((host ? "guest" : "host") + ".soul-fight-checked"), "live fight entry checked on both ends");
        }
        if (SoulFirst) {
            var health = first.Object.Host.GetComponent<HealthManager>();
            Log("SOUL DAMAGE phase1 hp=" + health.hp + " state=" + phase1Control.ActiveStateName);
            health.ApplyExtraDamage(health.hp);
            Flag("soul.phase1-killed");
        } else yield return Until(() => Has("soul.phase1-killed"), "authority defeats phase one");
        yield return new WaitForSecondsRealtime(12);
        SoulDump("phase one death and fake spell observation");
        Log("SOUL FLAGS quake=" + PlayerData.instance.GetInt("quakeLevel") + " defeated=" + PlayerData.instance.GetBool("mageLordDefeated") +
            " hero=" + hero.transform.position + " input=" + hero.acceptingInput);
        Flag(role + ".soul-phase1-observed");
        if (!SoulLate) yield return Until(() => Has((host ? "guest" : "host") + ".soul-phase1-observed"), "both phase one observations recorded");
        if (SoulMode == "phase1-inspect") yield break;
        PlayMakerFSM FakeSpell() => Object.FindObjectsOfType<PlayMakerFSM>(true).SingleOrDefault(f => f.FsmName == "Quake Fake" &&
            f.gameObject.scene.name.StartsWith(SoulScene));
        yield return Until(() => FakeSpell() != null && FakeSpell().gameObject.activeInHierarchy, "original fake spell appears after first-phase death");
        if (SoulFirst) {
            hero.transform.position = FakeSpell().transform.position + Vector3.up;
            hero.AffectedByGravity(true); hero.GetComponent<Rigidbody2D>().velocity = Vector2.zero; Physics2D.SyncTransforms();
            yield return Until(() => Control(phase2, "Mage Lord 2") != null && Control(phase2, "Mage Lord 2").ActiveStateName is not ("Init" or "Pause"),
                "native fake spell cutscene starts phase two");
            yield return Until(() => hero.acceptingInput && !hero.controlReqlinquished && hero.transform.position.y < 25,
                "native break-floor cutscene restores first player's input in lower arena");
            yield return new WaitForSecondsRealtime(2);
            var health = phase2.Object.Host.GetComponent<HealthManager>();
            Check(health.hp > 0 && !health.isDead, "phase two is a live boss before terminal damage");
            Log("SOUL DAMAGE phase2 hp=" + health.hp + " state=" + Control(phase2, "Mage Lord 2")?.ActiveStateName);
            health.ApplyExtraDamage(health.hp);
            Flag("soul.phase2-killed");
        } else yield return Until(() => Has("soul.phase2-killed"), "authority completes phase two while local hero remains outside", 65);
        yield return new WaitForSecondsRealtime(12);
        SoulDump("both phases defeated; late local interaction has not run");
        Log("SOUL POST-DEATH flags=" + PlayerData.instance.GetBool("mageLordDefeated") + " quake=" + PlayerData.instance.GetInt("quakeLevel") +
            " input=" + hero.acceptingInput + " pos=" + hero.transform.position);
        Flag(role + ".soul-death-observed");
        if (!SoulLate) yield return Until(() => Has((host ? "guest" : "host") + ".soul-death-observed"), "both final-death observations recorded");
        if (SoulMode == "death-inspect") yield break;
        yield return SoulRewardObservations();
    }

    private IEnumerator SoulLateEntryChecks() {
        yield return SoulCrossGate("Battle Gate", -1, "hero arriving after " + SoulArrival + " enters through upper gate");
        yield return Until(() => Has("soul.phase2-killed"), "late entrant observes final authority defeat", 90);
        yield return Until(() => PlayerData.instance.GetBool("mageLordDefeated"), "late scene snapshot or live death completes local boss flags");
        SoulDump("late entrant final corpse and reward state");
        yield return SoulRewardObservations();
    }

    private IEnumerator SoulRewardObservations() {
        var hero = HeroController.instance;
        var body = hero.GetComponent<Rigidbody2D>();
        var pd = PlayerData.instance;
        var other = host ? "guest" : "host";
        PlayMakerFSM Reward() => Object.FindObjectsOfType<PlayMakerFSM>(true).SingleOrDefault(f => f.FsmName == "Quake" &&
            f.name == "Quake Item" && f.gameObject.scene.name.StartsWith(SoulScene));
        yield return Until(() => Reward() != null && Reward().gameObject.activeInHierarchy && Reward().ActiveStateName == "Idle",
            "native final corpse lands and real spell becomes collectible");
        var reward = Reward();
        if (SoulFirst) {
            yield return SoulCollectSpell(reward);
            Flag("soul.real-spell-collected");
        } else {
            yield return Until(() => Has("soul.real-spell-collected"), "authority completes original real-spell pickup", 65);
            if (Environment.GetEnvironmentVariable("HOLLOWCONNECT_SOUL_SECOND_FAKE") == "1") {
                var fake = Object.FindObjectsOfType<PlayMakerFSM>(true).SingleOrDefault(f => f.FsmName == "Quake Fake" &&
                    f.gameObject.scene.name.StartsWith(SoulScene));
                Check(fake != null && fake.gameObject.activeInHierarchy, "late hero can still encounter native fake spell");
                hero.transform.position = fake.transform.position + Vector3.up;
                hero.GetComponent<Rigidbody2D>().velocity = Vector2.zero; hero.AffectedByGravity(true); Physics2D.SyncTransforms();
                yield return Until(() => !hero.acceptingInput || hero.controlReqlinquished, "late hero's actual fake-spell collision takes control");
                yield return Until(() => hero.acceptingInput && !hero.controlReqlinquished && hero.transform.position.y < 25,
                    "late hero's native fake-spell cinematic restores control after authority victory", 45);
            }
            if (SoulVerify) {
                if (runtime.Shared) yield return Until(() => pd.GetInt("quakeLevel") == 1, "shared spell arrives before this hero's local cutscene");
                else Check(pd.GetInt("quakeLevel") == 0, "sharing off keeps spell acquisition local");
                yield return SoulCollectSpell(reward);
            }
        }
        yield return new WaitForSecondsRealtime(3);
        SoulDump("after original real spell pickup");
        var gate = Object.FindObjectsOfType<Collider2D>(true).Single(c => c.name == "Battle Gate" && c.gameObject.scene.name == SoulScene);
        Log("SOUL RESULT shared=" + runtime.Shared + " first=" + SoulFirst + " defeated=" + pd.GetBool("mageLordDefeated") +
            " quake=" + pd.GetInt("quakeLevel") + " upperGateCollider=" + gate.enabled + " input=" + hero.acceptingInput +
            " pauseDisabled=" + pd.GetBool("disablePause"));
        if (SoulVerify) {
            Check(pd.GetInt("quakeLevel") == 1, "local and shared acquisition grant exactly Desolate Dive level one");
            if (SoulFirst) Check(!soulAuthoritySkippedCascade, "authority retains native corpse and spell-appearance cascade");
            yield return SoulCrossGate("Battle Gate", -1, "upper gate permits actual walking after this player's native pickup");
            yield return SoulCrossGate("Battle Gate (1)", -1, "lower exit gate permits actual walking after this player's native pickup");
        } else if (!SoulFirst) {
            var gateX = gate.transform.position.x;
            hero.transform.position = hero.FindGroundPoint(new Vector3(gateX + 2, gate.transform.position.y + 2, 0), true) + Vector3.up * .1f;
            body.velocity = Vector2.zero; hero.AffectedByGravity(true); Physics2D.SyncTransforms();
            yield return Until(() => hero.cState.onGround, "outside hero grounded for actual native gate crossing");
            var end = Time.realtimeSinceStartup + 3;
            using (new MonoMod.RuntimeDetour.Hook(typeof(HeroController).GetMethod("LookForInput", BindingFlags.Instance | BindingFlags.NonPublic),
                new Action<Action<HeroController>, HeroController>((orig, self) => { orig(self); if (self.acceptingInput) self.move_input = -1f; })))
                while (hero.transform.position.x > gateX - 1 && Time.realtimeSinceStartup < end) yield return null;
            Log("SOUL RESULT nativeWalkCrossed=" + (hero.transform.position.x < gateX - 1) + " pos=" + hero.transform.position +
                " upperGateCollider=" + gate.enabled + " input=" + hero.acceptingInput);
        }
        Flag(role + ".soul-reward-observed");
        yield return Until(() => Has(other + ".soul-reward-observed"), "both reward and gate observations recorded", 100);
    }

    private IEnumerator SoulCollectSpell(PlayMakerFSM reward) {
        var hero = HeroController.instance;
        var pd = PlayerData.instance;
        Check(reward != null && reward.gameObject.activeInHierarchy, "local spell pickup remains available for original cutscene");
        hero.transform.position = reward.transform.position + Vector3.up;
        hero.GetComponent<Rigidbody2D>().velocity = Vector2.zero; hero.AffectedByGravity(true); Physics2D.SyncTransforms();
        yield return Until(() => pd.GetInt("quakeLevel") == 1 && !hero.acceptingInput, "native real spell collision awards Desolate Dive");
        var end = Time.realtimeSinceStartup + 35;
        while ((!hero.acceptingInput || hero.controlReqlinquished || pd.GetBool("disablePause")) && Time.realtimeSinceStartup < end) {
            yield return SoulPress(InputHandler.Instance.inputActions.jump);
            yield return SoulPress(InputHandler.Instance.inputActions.menuSubmit);
            yield return new WaitForSecondsRealtime(.3f);
        }
        Check(hero.acceptingInput && !hero.controlReqlinquished && !pd.GetBool("disablePause"), "original spell animation and item message restore control");
    }

    private IEnumerator SoulCrossGate(string name, float direction, string label) {
        var hero = HeroController.instance;
        var gate = Object.FindObjectsOfType<Collider2D>(true).SingleOrDefault(c => c.name == name && c.gameObject.scene.name == SoulScene);
        // Native Destroy if Quake removes the lower door on entry when this
        // hero already owns the shared spell. Still walk its actual doorway.
        Check(gate == null || !gate.enabled || !gate.gameObject.activeInHierarchy, label + " collider is open or natively removed");
        var at = gate != null ? gate.transform.position : name == "Battle Gate (1)" ? new Vector3(7.8f, 19.1f) : new Vector3(37.3f, 29.9f);
        var x = at.x;
        var raised = name == "Battle Gate (1)";
        hero.transform.position = raised ? new Vector3(x - direction * 2, at.y, hero.transform.position.z) :
            hero.FindGroundPoint(new Vector3(x - direction * 2, at.y + 2, 0), true) + Vector3.up * .1f;
        hero.GetComponent<Rigidbody2D>().velocity = Vector2.zero; hero.AffectedByGravity(!raised); Physics2D.SyncTransforms();
        if (!raised) yield return Until(() => hero.cState.onGround, label + " original floor supports hero");
        using (new MonoMod.RuntimeDetour.Hook(typeof(HeroController).GetMethod("LookForInput", BindingFlags.Instance | BindingFlags.NonPublic),
            new Action<Action<HeroController>, HeroController>((orig, self) => { orig(self); if (self.acceptingInput) self.move_input = direction; })))
            yield return Until(() => (hero.transform.position.x - x) * direction > 1, label, 5);
        hero.AffectedByGravity(true);
    }

    private IEnumerator SoulPress(InControl.PlayerAction action) {
        var frame = Time.frameCount + 1;
        using (new MonoMod.RuntimeDetour.Hook(typeof(InControl.OneAxisInputControl).GetProperty("WasPressed").GetGetMethod(),
            new Func<Func<InControl.OneAxisInputControl, bool>, InControl.OneAxisInputControl, bool>((orig, self) => ReferenceEquals(self, action) ? Time.frameCount == frame : orig(self))))
        using (new MonoMod.RuntimeDetour.Hook(typeof(InControl.OneAxisInputControl).GetProperty("IsPressed").GetGetMethod(),
            new Func<Func<InControl.OneAxisInputControl, bool>, InControl.OneAxisInputControl, bool>((orig, self) => ReferenceEquals(self, action) ? Time.frameCount >= frame : orig(self))))
            yield return new WaitForSecondsRealtime(.15f);
    }
}
