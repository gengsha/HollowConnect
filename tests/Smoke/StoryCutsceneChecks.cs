using System;
using System.Collections;
using System.Linq;
using System.Reflection;
using Hkmp.Game.Client.Entity;
using HutongGames.PlayMaker;
using HutongGames.PlayMaker.Actions;
using UnityEngine;
using Object = UnityEngine.Object;
using Scenes = UnityEngine.SceneManagement.SceneManager;

namespace HollowConnect.Smoke;

public sealed partial class CoverageProbe {
    private bool StoryCutsceneCase => !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("HOLLOWCONNECT_STORY_SCENE"));
    private void StorySchema(string scene) {
        foreach (var gate in Object.FindObjectsOfType<TransitionPoint>(true).Where(g => g.gameObject.scene.name == scene))
            Mark("STORY GATE " + gate.name + " pos=" + gate.transform.position);
        foreach (var p in Object.FindObjectsOfType<DreamPlatform>(true).Where(p => p.gameObject.scene.name == scene))
            Mark("STORY PLATFORM " + p.name + " pos=" + p.transform.position + " showOnEnable=" + p.showOnEnable +
                " visible=" + Field<bool>(p, "visible") + " owner=" + EntityReferences.Owner(p.gameObject)?.Type);
        foreach (var f in Object.FindObjectsOfType<PlayMakerFSM>(true).Where(f => f.gameObject.scene.name == scene)) {
            Mark("STORY FSM " + f.name + "/" + f.FsmName + " active=" + f.gameObject.activeInHierarchy + " enabled=" + f.enabled +
                " state=" + f.ActiveStateName + " pos=" + f.transform.position + " owner=" + EntityReferences.Owner(f.gameObject)?.Type);
            foreach (var state in f.FsmStates) {
                Mark("STORY STATE " + f.name + "/" + f.FsmName + "/" + state.Name + " actions=" +
                    string.Join(",", state.Actions.Select(a => a.GetType().Name)) + " next=" +
                    string.Join(",", state.Transitions.Select(t => t.EventName + "->" + t.ToState)));
                foreach (var action in state.Actions) {
                    if (!(action is SetPlayerDataBool || action is SetPlayerDataInt || action is GetPlayerDataBool ||
                        action is GetPlayerDataInt || action is BeginSceneTransition || action is Trigger2dEvent ||
                        action is CallMethodProper || action.GetType().Name.StartsWith("ListenFor", StringComparison.Ordinal))) continue;
                    var values = action.GetType().GetFields(BindingFlags.Instance | BindingFlags.Public).Select(v => {
                        var value = v.GetValue(action);
                        return v.Name + "=" + (value is FsmString s ? s.Value : value is FsmInt i ? i.Value.ToString() :
                            value is FsmBool b ? b.Value.ToString() : value is FsmEvent e ? e.Name : value?.ToString());
                    });
                    Mark("STORY ACTION " + f.name + "/" + state.Name + "/" + action.GetType().Name + " " + string.Join(";", values));
                }
            }
        }
    }
    private IEnumerator StoryCutsceneChecks() {
        var scene = Environment.GetEnvironmentVariable("HOLLOWCONNECT_STORY_SCENE");
        var pd = PlayerData.instance;
        pd.SetBool("hasDreamNail", true);
        pd.SetBool("hasDash", true); pd.SetBool("canDash", true);
        pd.SetBool("hasWalljump", true); pd.SetBool("canWallJump", true);
        pd.SetBool("visitedDirtmouth", true);
        pd.SetString("dreamReturnScene", "Town"); pd.SetString("dreamReturnGate", "left1");
        if (scene == "Dream_Abyss") {
            pd.SetInt("royalCharmState", 3); pd.SetBool("gotCharm_36", true);
            pd.SetBool("equippedCharm_36", true); pd.EquipCharm(36);
        }
        if (!host) yield return Until(() => Has("host.story-entered"), "first player enters story scene");
        void Loaded(UnityEngine.SceneManagement.Scene loaded, UnityEngine.SceneManagement.LoadSceneMode mode) {
            if (loaded.name != scene) return;
            foreach (var gate in Object.FindObjectsOfType<TransitionPoint>(true).Where(g => g.gameObject.scene.name == scene))
                Mark("STORY LOADED GATE " + gate.name + " pos=" + gate.transform.position);
        }
        Scenes.sceneLoaded += Loaded;
        var entryGate = Environment.GetEnvironmentVariable("HOLLOWCONNECT_STORY_GATE");
        Enter(scene, string.IsNullOrEmpty(entryGate) ?
            (scene.StartsWith("Dream_", StringComparison.Ordinal) ? "door1" : "left1") : entryGate);
        if (Environment.GetEnvironmentVariable("HOLLOWCONNECT_STORY_STAGE") == "gates") {
            yield return Until(() => Scenes.GetSceneByName(scene).isLoaded, "scene asset for entrance diagnosis");
            Scenes.sceneLoaded -= Loaded;
            StorySchema(scene);
            Flag(role + ".story-entered");
            yield return Until(() => Has((host ? "guest" : "host") + ".story-entered"), "both loaded entrance diagnosis");
            yield break;
        }
        yield return Until(() => Scenes.GetSceneByName(scene).isLoaded && Hkmp.CoopContext.LocalScene == scene &&
            engine.ClientManager.SceneSnapshotReady, "story scene asset and snapshot " + scene);
        Scenes.sceneLoaded -= Loaded;
        Flag(role + ".story-entered");
        yield return Until(() => Has((host ? "guest" : "host") + ".story-entered"), "both entered story scene");
        yield return new WaitForSecondsRealtime(2);
        StorySchema(scene);
        Mark("STORY HERO input=" + HeroController.instance.acceptingInput + " transitioning=" + HeroController.instance.cState.transitioning +
            " positioned=" + HeroController.instance.isHeroInPosition + " pos=" + HeroController.instance.transform.position + " entities=" + Entities.Count);
        Flag(role + ".story-schema");
        yield return Until(() => Has((host ? "guest" : "host") + ".story-schema"), "both inspected story scene");
        if (Environment.GetEnvironmentVariable("HOLLOWCONNECT_STORY_STAGE") == "remote-attack" ||
            Environment.GetEnvironmentVariable("HOLLOWCONNECT_STORY_STAGE") == "progress")
            yield return StoryGuardianAttackChecks(scene);
        if (Environment.GetEnvironmentVariable("HOLLOWCONNECT_STORY_STAGE") == "progress") {
            if (scene == "White_Palace_09") yield return StoryPalaceBreakChecks(scene);
            else yield return StoryGuardianProgressChecks(scene);
        }
        if (Environment.GetEnvironmentVariable("HOLLOWCONNECT_STORY_STAGE") == "peer-trigger")
            yield return StoryPeerTriggerChecks(scene);
    }

    private IEnumerator StoryPeerTriggerChecks(string scene) {
        var target = Object.FindObjectsOfType<PlayMakerFSM>().Single(f => f.gameObject.scene.name == scene &&
            f.name == "End Cutscene" && f.FsmName == "Control");
        if (target.ActiveStateName != "Idle") throw new Exception("Native end trigger not ready: " + target.ActiveStateName);
        var trigger = target.GetComponent<Collider2D>();
        if (trigger == null || !trigger.enabled || !trigger.isTrigger)
            throw new Exception("Native end cutscene collider missing or disabled");
        var hero = HeroController.instance;
        var touched = false;
        On.HutongGames.PlayMaker.Fsm.hook_EnterState observer = (orig, fsm, state) => {
            if (ReferenceEquals(fsm, target.Fsm) && state.Name == "Start") touched = true;
            orig(fsm, state);
        };
        On.HutongGames.PlayMaker.Fsm.EnterState += observer;
        try {
            Mark("STORY TRIGGER bounds=" + trigger.bounds + " hero=" + hero.transform.position + " entry=" +
                Object.FindObjectsOfType<PlayMakerFSM>().Single(f => f.gameObject.scene.name == scene && f.name == "Entry Cutscene").ActiveStateName);
            Freeze();
            if (host) {
                hero.transform.position = trigger.bounds.center;
                Physics2D.SyncTransforms();
                yield return Until(() => touched, "own native end cutscene collider enters Start");
                yield return new WaitForSecondsRealtime(2);
                Flag("host.story-triggered");
            } else {
                yield return Until(() => Has("host.story-triggered"), "peer enters real native cutscene collider");
                var remote = runtime.PlayerObject(runtime.Peers.Keys.Single());
                if (remote == null || !trigger.bounds.Intersects(remote.GetComponent<Collider2D>().bounds))
                    throw new Exception("Peer contact fixture did not observe remote collider inside native trigger");
                Mark("PASS real replicated body overlaps native cutscene trigger");
                if (touched || target.ActiveStateName != "Idle")
                    throw new Exception("Reproduced peer starts personal cutscene: " + target.ActiveStateName + " touched=" + touched);
                Mark("PASS peer native contact leaves personal cutscene idle");
                Flag("guest.story-private-trigger");
            }
            yield return Until(() => Has("guest.story-private-trigger"), "both verified native trigger isolation");
        } finally { On.HutongGames.PlayMaker.Fsm.EnterState -= observer; }
    }

    private IEnumerator StoryGuardianAttackChecks(string scene) {
        var palace = scene == "White_Palace_09";
        var npc = Object.FindObjectsOfType<PlayMakerFSM>().Single(f => f.gameObject.scene.name == scene &&
            f.name == (palace ? "White King Corpse" : "Dreamer NPC") && f.FsmName == "Control");
        var hero = HeroController.instance;
        var idle = palace ? "Idle" : "Detecting";
        yield return Until(() => hero.acceptingInput && !hero.cState.transitioning && npc.ActiveStateName == idle, "native personal story attack ready");
        var touched = false;
        On.HutongGames.PlayMaker.Fsm.hook_EnterState observer = (orig, fsm, state) => {
            if (ReferenceEquals(fsm, npc.Fsm) && state.Name == (palace ? "Hits" : "Get Damager Parameters")) touched = true;
            orig(fsm, state);
        };
        On.HutongGames.PlayMaker.Fsm.EnterState += observer;
        try {
            Freeze();
            if (host) {
                hero.transform.position = npc.transform.position + new Vector3(-2, -.5f, 0);
                hero.FaceRight(); Physics2D.SyncTransforms();
                yield return new WaitForSecondsRealtime(.5f);
                hero.Attack(GlobalEnums.AttackDirection.normal);
                yield return Until(() => touched, "own native nail collision hits dreamer");
                yield return new WaitForSecondsRealtime(2);
                Flag("host.story-attacked");
            } else {
                yield return Until(() => Has("host.story-attacked"), "peer completes real native nail attack");
                if (touched || npc.ActiveStateName != idle)
                    throw new Exception("Reproduced remote nail advanced personal dreamer: state=" + npc.ActiveStateName +
                        " touched=" + touched + " localHero=" + hero.transform.position + " dreamer=" + npc.transform.position);
                Mark("PASS remote real nail leaves personal dreamer untouched");
                Flag("guest.story-private");
            }
            yield return Until(() => Has("guest.story-private"), "remote attack isolation verified");
        } finally { On.HutongGames.PlayMaker.Fsm.EnterState -= observer; }
    }

    private IEnumerator StoryGuardianProgressChecks(string scene, bool personalRepeat = false) {
        var key = scene == "Dream_Guardian_Lurien" ? "lurienDefeated" : scene == "Dream_Guardian_Hegemol" ? "hegemolDefeated" : "monomonDefeated";
        var pd = PlayerData.instance;
        var npc = Object.FindObjectsOfType<PlayMakerFSM>().Single(f => f.gameObject.scene.name == scene && f.name == "Dreamer NPC" && f.FsmName == "Control");
        var hero = HeroController.instance;
        if (host || personalRepeat) {
            var woundBy = Time.realtimeSinceStartup + 25;
            while (npc.ActiveStateName != "Wound Idle" && Time.realtimeSinceStartup < woundBy) {
                if (npc.ActiveStateName == "Detecting") {
                    hero.transform.position = npc.transform.position + new Vector3(-2, -.5f, 0);
                    hero.FaceRight(); Physics2D.SyncTransforms();
                    hero.Attack(GlobalEnums.AttackDirection.normal);
                }
                yield return new WaitForSecondsRealtime(.8f);
                Mark("STORY WOUND state=" + npc.ActiveStateName);
            }
            if (npc.ActiveStateName != "Wound Idle") throw new Exception("Native attacks did not wound dreamer: " + npc.ActiveStateName);
            Mark("PASS own native attacks wound dreamer");
            hero.transform.position = hero.FindGroundPoint(npc.transform.position + Vector3.left * 2, true) + Vector3.up * .1f;
            hero.GetComponent<Rigidbody2D>().velocity = Vector2.zero; hero.AffectedByGravity(true); Physics2D.SyncTransforms();
            var spell = hero.gameObject.GetComponents<PlayMakerFSM>().Single(f => f.FsmName == "Spell Control");
            yield return Until(() => hero.CanFocus() && spell.ActiveStateName == "Inactive" ||
                hero.CanFocus() && spell.ActiveStateName == "Idle", "native ground and focus ready");
            var action = InputHandler.Instance.inputActions.cast;
            var reading = false; var edge = false;
            var nativeCompleted = false;
            On.HutongGames.PlayMaker.Fsm.hook_EnterState observer = (orig, fsm, state) => {
                if (ReferenceEquals(fsm, npc.Fsm) && state.Name == "PlayerData") nativeCompleted = true;
                orig(fsm, state);
            };
            On.HutongGames.PlayMaker.Fsm.EnterState += observer;
            try {
            using (new MonoMod.RuntimeDetour.Hook(typeof(ListenForCast).GetMethod("CheckForInput", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic),
                new Action<Action<ListenForCast>, ListenForCast>((orig, self) => {
                    reading = ReferenceEquals(self.Fsm, spell.Fsm) && !edge;
                    try { orig(self); } finally { reading = false; }
                })))
            using (new MonoMod.RuntimeDetour.Hook(typeof(InControl.OneAxisInputControl).GetProperty("WasPressed").GetGetMethod(),
                new Func<Func<InControl.OneAxisInputControl, bool>, InControl.OneAxisInputControl, bool>((orig, self) => {
                    if (!ReferenceEquals(self, action)) return orig(self);
                    if (!reading || edge) return false;
                    edge = true; return true;
                })))
            using (new MonoMod.RuntimeDetour.Hook(typeof(InControl.OneAxisInputControl).GetProperty("IsPressed").GetGetMethod(),
                new Func<Func<InControl.OneAxisInputControl, bool>, InControl.OneAxisInputControl, bool>((orig, self) =>
                    ReferenceEquals(self, action) ? edge : orig(self)))) {
                yield return Until(() => nativeCompleted && pd.GetBool(key), "real native focus records dreamer permanent progress");
            }
            } finally { On.HutongGames.PlayMaker.Fsm.EnterState -= observer; }
            Mark("STORY PROGRESS source=" + key + " guardians=" + pd.GetInt("guardiansDefeated") + " npc=" + npc.ActiveStateName);
            if (pd.GetInt("guardiansDefeated") != 1) throw new Exception("Native/shared dreamer was counted more than once");
            if (personalRepeat) { Flag("guest.story-personal-repeat"); yield break; }
            Flag("host.story-progress");
        } else {
            yield return Until(() => pd.GetBool(key), "native dreamer completion shared over UDP");
            yield return new WaitForSecondsRealtime(.75f);
            if (pd.GetInt("guardiansDefeated") != 1) throw new Exception("Shared dreamer defeat flag/count diverged");
            if (!runtime.Shared || npc.ActiveStateName != "Detecting" || !hero.acceptingInput)
                throw new Exception("Shared progress changed personal dreamer flow or control");
            Mark("STORY PROGRESS received=" + key + " guardians=" + pd.GetInt("guardiansDefeated"));
            Mark("PASS permanent dreamer completion shared while personal dreamer and input remain independent");
            Flag("guest.story-progress");
        }
        yield return Until(() => Has("host.story-progress") && Has("guest.story-progress"), "both verified source and shared permanent completion");
        if (scene == "Dream_Guardian_Lurien") {
            if (!host) yield return StoryGuardianProgressChecks(scene, true);
            yield return Until(() => Has("guest.story-personal-repeat"), "slower player naturally absorbs already-shared dreamer without counting twice");
        }
    }

    private IEnumerator StoryPalaceBreakChecks(string scene) {
        var corpse = Object.FindObjectsOfType<PlayMakerFSM>().Single(f => f.gameObject.scene.name == scene && f.name == "White King Corpse");
        var item = Object.FindObjectsOfType<PlayMakerFSM>(true).Single(f => f.gameObject.scene.name == scene &&
            f.name == "Shiny Item RoyalCharm" && f.FsmName == "Shiny Control");
        if (host) {
            var end = Time.realtimeSinceStartup + 15;
            while (!item.gameObject.activeInHierarchy && Time.realtimeSinceStartup < end) {
                if (corpse.ActiveStateName == "Idle") {
                    var hero = HeroController.instance;
                    hero.transform.position = corpse.transform.position + new Vector3(-2, -.5f, 0);
                    hero.FaceRight(); Physics2D.SyncTransforms(); hero.Attack(GlobalEnums.AttackDirection.normal);
                }
                yield return new WaitForSecondsRealtime(.8f);
            }
            if (!item.gameObject.activeInHierarchy) throw new Exception("Own native palace hits did not release royal charm: " + corpse.ActiveStateName);
            Mark("PASS own native palace corpse breaks and releases royal charm");
            Flag("host.story-palace-broken");
        } else {
            yield return Until(() => Has("host.story-palace-broken"), "peer completes native palace corpse break");
            if (corpse.ActiveStateName != "Idle" || item.gameObject.activeInHierarchy)
                throw new Exception("Peer palace break advanced personal corpse or item");
            Mark("PASS peer palace break leaves personal corpse and royal charm independent");
            Flag("guest.story-palace-private");
        }
        yield return Until(() => Has("guest.story-palace-private"), "both verified native palace corpse and reward isolation");
    }
}
