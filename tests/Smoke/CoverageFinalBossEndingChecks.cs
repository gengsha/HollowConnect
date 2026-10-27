// Called only by the disposable FinalBossEndingProbe after staggered entry.
using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using Hkmp.Game.Client.Entity;
using MonoMod.RuntimeDetour;
using UnityEngine;

namespace HollowConnect.Smoke;

internal sealed class CoverageFinalBossEndingChecks : IDisposable {
    private readonly Action<string> log;
    private readonly List<string> states = new();
    private readonly HashSet<string> queued = new();
    private readonly Hook queue, award, flush;
    private GameObject corpse;
    private PlayMakerFSM fsm, spell;
    private Entity entity;
    private string previous;
    private bool replica;
    internal CoverageFinalBossEndingChecks(Action<string> logger) {
        log = logger;
        On.HutongGames.PlayMaker.Fsm.EnterState += Enter;
        // Observe the native cinematic award while keeping test platform
        // achievements and records untouched. ProbeMod already blocks every
        // SaveGame overload and loads only a fresh in-memory slot 99.
        queue = new Hook(typeof(GameManager).GetMethod("QueueAchievement"),
            new Action<Action<GameManager, string>, GameManager, string>((orig, gm, key) => {
                queued.Add(key); log("NATIVE ENDING QUEUE " + key);
            }));
        award = new Hook(typeof(AchievementHandler).GetMethod("AwardAchievementToPlayer"),
            new Action<Action<AchievementHandler, string>, AchievementHandler, string>((orig, handler, key) =>
                log("TEST EXCLUDED platform achievement award " + key)));
        flush = new Hook(typeof(AchievementHandler).GetMethod("FlushRecordsToDisk"),
            new Action<Action<AchievementHandler>, AchievementHandler>((orig, handler) =>
                log("TEST EXCLUDED platform record save")));
    }
    private void Require(bool condition, string label) {
        if (!condition) throw new Exception("Final ending: " + label);
        log("PASS final ending " + label);
    }
    private void Enter(On.HutongGames.PlayMaker.Fsm.orig_EnterState orig, HutongGames.PlayMaker.Fsm observed, HutongGames.PlayMaker.FsmState state) {
        if (fsm != null && ReferenceEquals(observed, fsm.Fsm)) {
            states.Add(state.Name);
            log("NATIVE CORPSE ENTER " + state.Name);
        }
        if (spell != null && ReferenceEquals(observed, spell.Fsm)) log("NATIVE SPELL ENTER " + state.Name);
        orig(observed, state);
    }
    private void Observe() {
        if (fsm == null || previous == fsm.ActiveStateName) return;
        previous = fsm.ActiveStateName; states.Add(previous);
        log("NATIVE CORPSE state=" + previous + " position=" + corpse.transform.position +
            " body=" + corpse.GetComponent<Rigidbody2D>().bodyType +
            " gravity=" + corpse.GetComponent<Rigidbody2D>().gravityScale);
    }
    internal void Capture(Entity boss, bool localAuthority) {
        entity = boss; replica = !localAuthority;
        Require(boss.SourceScene == "Room_Final_Boss_Core" && boss.Type == EntityType.HollowKnight,
            "fixture uses the exact native final boss");
        var root = localAuthority ? boss.Object.Host : boss.Object.Client;
        fsm = root.GetComponentsInChildren<PlayMakerFSM>(true).Single(f => f.name == "Boss Corpse" && f.FsmName == "Corpse");
        corpse = fsm.gameObject;
        spell = HeroController.instance.GetComponents<PlayMakerFSM>().Single(f => f.FsmName == "Spell Control");
        if (replica) Require(!fsm.enabled && corpse.GetComponent<Rigidbody2D>().isKinematic,
            "late player's corpse begins as a dormant frozen replica");
        // Set local gotShadeCharm before the battle only as fixture data. One
        // process should use false and the other true, so local A/B choice is
        // distinguishable from copying the authority's ending.
    }
    internal IEnumerator Ready() {
        var end = Time.realtimeSinceStartup + 25;
        while (Time.realtimeSinceStartup < end && !spell.FsmVariables.FindFsmBool("Dream Focus").Value) {
            Observe(); yield return null;
        }
        Observe();
        Require(spell.FsmVariables.FindFsmBool("Dream Focus").Value && states.Contains("Fall"),
            "native gravity and LAND advance to the local Dream Focus prompt");
        Require(corpse.transform.parent == null && corpse.GetComponent<Rigidbody2D>().bodyType == RigidbodyType2D.Dynamic &&
            Math.Abs(corpse.GetComponent<Rigidbody2D>().gravityScale - 1) < .001f,
            "personal corpse retains native dynamic physics in this scene");
        Require(PlayerData.instance.GetBool("killedHollowKnight") && PlayerData.instance.GetInt("killsHollowKnight") == 0,
            "local native defeat and journal fields are recorded without shared progress");
        if (replica) {
            Require(entity.FinalCorpse.Released && !entity.Object.Host.activeSelf && !entity.Object.Client.activeSelf &&
                entity.Object.Client.GetComponents<PlayMakerFSM>().All(f => !f.enabled),
                "only the corpse continues, with both live boss roots inactive and replica AI disabled");
            Require(corpse.scene.GetRootGameObjects().Where(go => go.name == "Boss Control")
                .SelectMany(go => go.GetComponents<PlayMakerFSM>()).Where(f => f.FsmName == "Battle Start")
                .All(f => !f.enabled), "completed replica cannot replay the local battle entrance near its corpse");
        }
    }
    internal void CheckAfterAuthorityLeft() {
        Observe();
        Require(GameManager.instance.sceneName == "Room_Final_Boss_Core" && corpse != null && corpse.activeInHierarchy && fsm.enabled,
            "slower player keeps a native corpse after the authority enters its own ending");
        if (entity.Object.Host != null) Require(!entity.Object.Host.activeSelf, "authority handoff does not reactivate the native boss");
    }
    internal IEnumerator FinishByNativeFocus() {
        var hero = HeroController.instance;
        var ground = hero.FindGroundPoint(corpse.transform.position + Vector3.right * 3, true);
        hero.transform.position = ground + Vector3.up * .1f;
        hero.GetComponent<Rigidbody2D>().velocity = Vector2.zero; Physics2D.SyncTransforms();
        // Native CanFocus requires a grounded, controllable hero. Teleporting
        // above the floor and pressing on the very next frame can consume
        // the one WasPressed edge before the first ground collision.
        var readyBy = Time.realtimeSinceStartup + 5;
        while ((!hero.CanFocus() || fsm.ActiveStateName != "Idle") && Time.realtimeSinceStartup < readyBy) { Observe(); yield return null; }
        log("FOCUS INPUT READY hero=" + hero.transform.position + " corpse=" + corpse.transform.position +
            " ground=" + hero.cState.onGround + " input=" + hero.acceptingInput + " canFocus=" + hero.CanFocus() +
            " spell=" + spell.ActiveStateName + " corpseState=" + fsm.ActiveStateName);
        Require(hero.CanFocus() && fsm.ActiveStateName == "Idle", "native grounded focus eligibility and corpse range before the input edge");
        // Press only the input boundary: the real Spell Control/Corpse FSMs
        // must consume focus, emit their events, and select their own ending.
        var action = InputHandler.Instance.inputActions.cast;
        var readingSpellInput = false;
        var edgeRead = false;
        // Deliver the edge on the actual native listener's next read. In
        // batch mode a one-render-frame pulse can fall between FSM updates.
        using (new Hook(typeof(HutongGames.PlayMaker.Actions.ListenForCast).GetMethod("CheckForInput",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic),
            new Action<Action<HutongGames.PlayMaker.Actions.ListenForCast>, HutongGames.PlayMaker.Actions.ListenForCast>((orig, self) => {
                readingSpellInput = ReferenceEquals(self.Fsm, spell.Fsm) && !edgeRead;
                try { orig(self); } finally { readingSpellInput = false; }
            })))
        using (new Hook(typeof(InControl.OneAxisInputControl).GetProperty("WasPressed").GetGetMethod(),
            new Func<Func<InControl.OneAxisInputControl, bool>, InControl.OneAxisInputControl, bool>((orig, self) => {
                if (!ReferenceEquals(self, action)) return orig(self);
                if (!readingSpellInput || edgeRead) return false;
                edgeRead = true; log("NATIVE SPELL consumed cast WasPressed at frame=" + Time.frameCount + " state=" + spell.ActiveStateName); return true;
            })))
        using (new Hook(typeof(InControl.OneAxisInputControl).GetProperty("IsPressed").GetGetMethod(),
            new Func<Func<InControl.OneAxisInputControl, bool>, InControl.OneAxisInputControl, bool>((orig, self) =>
                ReferenceEquals(self, action) ? edgeRead : orig(self)))) {
            var end = Time.realtimeSinceStartup + 35;
            var inputBy = Time.realtimeSinceStartup + 3;
            while (GameManager.instance.sceneName == "Room_Final_Boss_Core" && Time.realtimeSinceStartup < end) {
                if (!edgeRead && Time.realtimeSinceStartup > inputBy) throw new Exception("Native Spell Control did not read the cast input edge; spell=" + spell.ActiveStateName);
                Observe(); yield return null;
            }
        }
        var expected = PlayerData.instance.GetBool("gotShadeCharm") ? "B" : "A";
        Require(GameManager.instance.sceneName == "Cinematic_Ending_" + expected &&
            states.Contains("Sucking") && states.Contains("Ramp Up"),
            "real local focus naturally enters the ending selected by local gotShadeCharm");
        var deadline = Time.realtimeSinceStartup + 5;
        while (!queued.Contains("ENDING_" + expected) && Time.realtimeSinceStartup < deadline) yield return null;
        Require(queued.Contains("ENDING_" + expected), "native cinematic requests its matching personal ending achievement");
        Require(corpse == null, "local scene unload cleans the independent corpse");
    }
    public void Dispose() { On.HutongGames.PlayMaker.Fsm.EnterState -= Enter; flush.Dispose(); award.Dispose(); queue.Dispose(); }
}
