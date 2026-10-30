using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using HollowConnect;
using HollowConnect.Core;
using Modding;
using UnityEngine;
using Object = UnityEngine.Object;

namespace HollowConnect.Smoke;

// Actual two-client nail collision, accepted rescuer identity, native visuals,
// capped soul, unreliable-roster ordering and replay protection.
public sealed partial class RescueProbe : MonoBehaviour {
    private string root, role;
    private bool host;
    private CoopRuntime runtime;
    private Hkmp.Game.GameManager engine;
    private static T Field<T>(object o, string n) => (T)o.GetType().GetField(n, BindingFlags.Instance | BindingFlags.NonPublic).GetValue(o);
    private void Log(string s) => File.AppendAllText(Path.Combine(root, role + ".log"), DateTime.UtcNow.ToString("O") + " " + s + "\n");
    private bool Has(string n) => File.Exists(Path.Combine(root, n));
    private void Flag(string n) => File.WriteAllText(Path.Combine(root, n), "ok");
    private void Check(bool b, string s) { if (!b) throw new Exception(s); Log("PASS " + s); }
    // takeNoDamage does not suppress native contact recoil. These lifecycle
    // fixtures retain deliberate source-null TakeDamage calls while excluding
    // unrelated enemy contact; real acid collision has its own route fixture.
    private static MonoMod.RuntimeDetour.Hook IgnoreExternalCombatDamage(bool allowHazards = false) =>
        new MonoMod.RuntimeDetour.Hook(typeof(HeroController).GetMethod("TakeDamage"),
            new Action<Action<HeroController, GameObject, GlobalEnums.CollisionSide, int, int>, HeroController, GameObject, GlobalEnums.CollisionSide, int, int>(
                (orig, self, source, side, damage, hazard) => {
                    if (source == null || allowHazards && (hazard == 2 || hazard == 3 || hazard == 5)) orig(self, source, side, damage, hazard);
                }));
    private IEnumerator Until(Func<bool> test, string s, float seconds = 45) {
        var end = Time.realtimeSinceStartup + seconds;
        while (!test() && Time.realtimeSinceStartup < end) yield return null;
        if (!test() && HeroController.instance != null) {
            var hero = HeroController.instance; var body = hero.GetComponent<Rigidbody2D>();
            Log("TIMEOUT HERO " + s + " scene=" + GameManager.instance.sceneName + " pos=" + hero.transform.position +
                " velocity=" + body.velocity + " gravity=" + body.gravityScale + " input=" + hero.acceptingInput +
                " transition=" + hero.transitionState + " down=" + runtime.Down + " returning=" + runtime.Returning +
                " health=" + PlayerData.instance.health + " invulnerable=" + hero.cState.invulnerable + " recoil=" + hero.cState.recoiling +
                " hazard=" + hero.cState.hazardDeath + "/" + hero.cState.hazardRespawning);
            if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable("HOLLOWCONNECT_STORY_RESCUE_CASE")))
                Log("TIMEOUT STORY PHYSICS simulated=" + body.simulated + " type=" + body.bodyType + " constraints=" + body.constraints +
                    " scale=" + Time.timeScale + " move=" + hero.move_input + " state=" + hero.hero_state + " attacking=" + hero.cState.attacking);
            foreach (var fsm in Object.FindObjectsOfType<PlayMakerFSM>().Where(f => f.name == "White Palace Lift"))
                Log("TIMEOUT LIFT " + fsm.name + "/" + fsm.FsmName + "=" + fsm.ActiveStateName + " pos=" + fsm.transform.position);
            if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable("HOLLOWCONNECT_RESCUE_RULE_CHECK")))
                foreach (var fsm in Object.FindObjectsOfType<PlayMakerFSM>().Where(f => f.FsmName == "Bench Control"))
                    Log("TIMEOUT BENCH " + fsm.name + "=" + fsm.ActiveStateName + " bools=" + string.Join(",", fsm.FsmVariables.BoolVariables.Select(v => v.Name + "=" + v.Value)));
        }
        Check(test(), s);
    }
    private void Inject(Message m) {
        m.Session = runtime.Session; m.Round = Hkmp.CoopContext.Epoch;
        typeof(CoopRuntime).GetMethod("Receive", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(runtime, new object[] { m });
    }
    private void Freeze() {
        var h = HeroController.instance; h.RegainControl(); h.StartAnimationControl(); h.takeNoDamage = true;
        h.cState.dead = h.cState.recoiling = h.cState.invulnerable = false; h.cState.recoilingLeft = h.cState.recoilingRight = false;
        HeroBox.inactive = false; h.GetComponent<Rigidbody2D>().gravityScale = 0; h.GetComponent<Rigidbody2D>().velocity = Vector2.zero;
        h.transform.position = UnityEngine.SceneManagement.SceneManager.GetActiveScene().name == "Crossroads_01"
            ? StoryFloor(new Vector3(20, 12, h.transform.position.z), "Crossroads_01") + Vector3.right * (host ? 0 : 2)
            : new Vector3(host ? 20 : 22, 12, h.transform.position.z);
    }
    private void Die() {
        var h = HeroController.instance; PlayerData.instance.SetInt("health", 0);
        h.StartCoroutine((IEnumerator)typeof(HeroController).GetMethod("Die", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(h, null));
    }
    private IEnumerator Start() {
        root = Environment.GetEnvironmentVariable("HOLLOWCONNECT_SMOKE_ROOT"); role = Environment.GetEnvironmentVariable("HOLLOWCONNECT_SMOKE_ROLE"); host = role == "host";
        var stack = new Stack<IEnumerator>(); stack.Push(Run());
        while (stack.Count > 0) {
            object current = null; bool more;
            try { more = stack.Peek().MoveNext(); if (more) current = stack.Peek().Current; }
            catch (Exception e) { Time.timeScale = 1; Log("FAIL " + e); File.WriteAllText(Path.Combine(root, role + ".failed"), e.ToString()); yield break; }
            if (!more) { stack.Pop(); continue; }
            if (current is IEnumerator nested) stack.Push(nested); else yield return current;
        }
    }
    private IEnumerator Run() {
        Log("muted, fresh character, all save overloads intercepted");
        yield return Until(() => GameManager.instance != null && Object.FindObjectOfType<CoopRuntime>() != null &&
            UnityEngine.SceneManagement.SceneManager.GetActiveScene().name == "Menu_Title", "menu ready");
        runtime = Object.FindObjectOfType<CoopRuntime>(); engine = Field<Hkmp.Game.GameManager>(ModHooks.GetMod("HKMP"), "_gameManager");
        Field<Hkmp.Game.Settings.ModSettings>(engine.ClientManager, "_modSettings").AuthKey = Hkmp.Util.AuthUtil.GenerateAuthKey();
        CoopMod.Settings.Rescue = true;
        CoopMod.Settings.RescueLimit = 0;
        CoopMod.Settings.SpectateAfterExhaustion = Environment.GetEnvironmentVariable("HOLLOWCONNECT_SETTLEMENT_SPECTATOR") == "1";
        if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable("HOLLOWCONNECT_PATH_OF_PAIN_CHECK")))
            CoopMod.Settings.SpectateAfterExhaustion = true;
        if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable("HOLLOWCONNECT_RESCUE_RULE_CHECK"))) ConfigureRescueRuleCheck();
        CoopMod.Settings.ShareProgress = VinePlatformCheck || !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("HOLLOWCONNECT_ENCOUNTER_CASE")) || !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("HOLLOWCONNECT_LOCAL_BATTLE_CHECK")) ||
            !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("HOLLOWCONNECT_PATH_OF_PAIN_CHECK"));
        if (SoulMasterCheck) CoopMod.Settings.ShareProgress = Environment.GetEnvironmentVariable("HOLLOWCONNECT_SOUL_SHARED") == "1";
        if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable("HOLLOWCONNECT_STORY_GATE_CHECK")))
            CoopMod.Settings.ShareProgress = Environment.GetEnvironmentVariable("HOLLOWCONNECT_STORY_GATE_SHARED") != "0";
        if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable("HOLLOWCONNECT_BOSS_FLOW_CHECK")))
            CoopMod.Settings.ShareProgress = Environment.GetEnvironmentVariable("HOLLOWCONNECT_BOSS_FLOW_SHARED") == "1";
        if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable("HOLLOWCONNECT_BOSS_SPOT")))
            CoopMod.Settings.ShareProgress = Environment.GetEnvironmentVariable("HOLLOWCONNECT_BOSS_SPOT_SHARED") == "1";
        if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable("HOLLOWCONNECT_EXTRA_ENCOUNTER")))
            CoopMod.Settings.ShareProgress = Environment.GetEnvironmentVariable("HOLLOWCONNECT_EXTRA_SHARED") == "1";
        if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable("HOLLOWCONNECT_MOTHER_SHARED")))
            CoopMod.Settings.ShareProgress = Environment.GetEnvironmentVariable("HOLLOWCONNECT_MOTHER_SHARED") == "1";
        if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable("HOLLOWCONNECT_COLLECTOR_SHARED")))
            CoopMod.Settings.ShareProgress = Environment.GetEnvironmentVariable("HOLLOWCONNECT_COLLECTOR_SHARED") == "1";
        if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable("HOLLOWCONNECT_FK_GATE_CHECK")))
            CoopMod.Settings.ShareProgress = Environment.GetEnvironmentVariable("HOLLOWCONNECT_FK_SHARED") == "1";
        if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable("HOLLOWCONNECT_HORNET_STORY"))) {
            CoopMod.Settings.ShareProgress = Environment.GetEnvironmentVariable("HOLLOWCONNECT_HORNET_SHARED") == "1";
            CoopMod.Settings.BossHealthScaling = false;
        }
        if (Environment.GetEnvironmentVariable("HOLLOWCONNECT_TRANSITION_CONTACT") == "1")
            CoopMod.Settings.ShareProgress = Environment.GetEnvironmentVariable("HOLLOWCONNECT_TRANSITION_SHARED") == "1";
        if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable("HOLLOWCONNECT_SETTLEMENT_DISCONNECT"))) {
            CoopMod.Settings.ShareProgress = Environment.GetEnvironmentVariable("HOLLOWCONNECT_SETTLEMENT_SHARED") == "1";
            CoopMod.Settings.BossDamageRanking = Environment.GetEnvironmentVariable("HOLLOWCONNECT_SETTLEMENT_DISCONNECT") == "damage";
        }
        if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable("HOLLOWCONNECT_BOSS_COMPLETION_INTENT"))) CoopMod.Settings.ShareProgress = true;
        CoopMod.Settings.EntryProtection = false;
        if (Environment.GetEnvironmentVariable("HOLLOWCONNECT_OBSERVATION_PROTECTION") == "1") CoopMod.Settings.EntryProtection = true;
        if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable("HOLLOWCONNECT_BOSS_TARGET_AUTHORITY")))
            CoopMod.Settings.BossTargetingMode = 0;
        if (MossRetargetCheck) {
            CoopMod.Settings.BossTargetingMode = Environment.GetEnvironmentVariable("HOLLOWCONNECT_MOSS_NATIVE") == "1" ? 0 : 1;
            CoopMod.Settings.Rescue = false;
            CoopMod.Settings.SpectateAfterExhaustion = false;
            CoopMod.Settings.BossHealthScaling = false;
            CoopMod.Settings.ShareProgress = false;
        }
        if (Environment.GetEnvironmentVariable("HOLLOWCONNECT_NOSK_STALL_CHECK") == "1") {
            CoopMod.Settings.ShareProgress = true;
            CoopMod.Settings.BossTargetingMode = 1;
        }
        if (PostBossDamageCheck) {
            CoopMod.Settings.BossHealthScaling = true;
            CoopMod.Settings.BossExtraHealthPerPlayer = .6f;
            postBossPlayerDamage = PlayerDamageSignature();
        }
        CoopMod.Settings.BossVictoryRescue = Environment.GetEnvironmentVariable("HOLLOWCONNECT_BOSS_VICTORY_DISABLED") != "1";
        if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable("HOLLOWCONNECT_EXHAUSTED_BOSS_CHECK"))) {
            CoopMod.Settings.RescueLimit = 3;
            CoopMod.Settings.SpectateAfterExhaustion = true;
            CoopMod.Settings.BossTargetingMode = 0;
        }
        if (Environment.GetEnvironmentVariable("HOLLOWCONNECT_OBSERVATION_LINKED") == "1") {
            CoopMod.Settings.ShareProgress = CoopMod.Settings.ShareCharms = CoopMod.Settings.LinkedHealth = true;
        }
        var pd = PlayerData.instance; pd.SetString("respawnScene", "Town"); pd.SetString("respawnMarkerName", "RestBench"); pd.SetInt("respawnType", 1);
        pd.SetBool("visitedDirtmouth", true);
        Log("fixture: Dirtmouth visited; native first-visit title is outside rescue scope");
        GameManager.instance.profileID = 99; GameManager.instance.StartCoroutine(GameManager.instance.RunContinueGame());
        yield return Until(() => HeroController.instance != null && HeroController.instance.isHeroInPosition && !GameManager.instance.IsInSceneTransition, "fresh unsaved Town entered");
        yield return RuleBenchUp();
        if (FlexibleTargetCheck || MenuPointerCheck) ConfigureFlexibleTargetChecks();
        if (MenuPointerCheck) CheckMenuPointerMenus();
        if (host) { engine.ServerManager.Start(29650, false); Flag("server.started"); } else yield return Until(() => Has("server.started"), "server ready");
        yield return new WaitForSecondsRealtime(1);
        typeof(Hkmp.Game.Client.ClientManager).GetField("_autoConnect", BindingFlags.Instance | BindingFlags.NonPublic).SetValue(engine.ClientManager, true);
        typeof(Hkmp.Game.Client.ClientManager).GetMethod("Connect", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(engine.ClientManager, new object[] { "127.0.0.1", 29650, "rescue" + role });
        yield return Until(() => runtime.Connected, "real UDP handshake"); Flag(role + ".connected");
        yield return Until(() => Has((host ? "guest" : "host") + ".connected"), "both connected");
        if (Environment.GetEnvironmentVariable("HOLLOWCONNECT_OBSERVATION_CHECK") == "1") {
            yield return VoluntaryObservationChecks();
            yield return ProbeShutdown.Run(engine, root, role);
            Log("FINAL PASS noncombat observation, pause and native Dirtmouth bench return"); Flag(role + ".done"); yield break;
        }
        if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable("HOLLOWCONNECT_EXTRA_ENCOUNTER"))) {
            yield return ExtraEncounterChecks();
            yield return ProbeShutdown.Run(engine, root, role);
            Log("FINAL PASS extra encounter checks"); Flag(role + ".done"); yield break;
        }
        if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable("HOLLOWCONNECT_BOSS_SPOT"))) {
            yield return BossSpotChecks();
            yield return ProbeShutdown.Run(engine, root, role);
            Log("FINAL PASS boss spot checks"); Flag(role + ".done"); yield break;
        }
        if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable("HOLLOWCONNECT_BOSS_FLOW_CHECK"))) {
            yield return BossFlowChecks();
            yield return ProbeShutdown.Run(engine, root, role);
            Log("FINAL PASS boss flow checks"); Flag(role + ".done"); yield break;
        }
        if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable("HOLLOWCONNECT_STORY_GATE_CHECK"))) {
            yield return StoryBossGateChecks();
            yield return ProbeShutdown.Run(engine, root, role);
            Log("FINAL PASS story boss gate checks"); Flag(role + ".done"); yield break;
        }
        if (Environment.GetEnvironmentVariable("HOLLOWCONNECT_TRANSITION_CONTACT") == "1") {
            yield return SceneTransitionContactChecks();
            yield return ProbeShutdown.Run(engine, root, role);
            Log("FINAL PASS native scene transition contact isolation");
            Flag(role + ".done"); yield break;
        }
        if (MossRetargetCheck) {
            yield return MossRetargetChecks();
            yield return ProbeShutdown.Run(engine, root, role);
            Log("FINAL PASS Moss retarget " + Environment.GetEnvironmentVariable("HOLLOWCONNECT_MOSS_RETARGET"));
            Flag(role + ".done"); yield break;
        }
        if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable("HOLLOWCONNECT_WARP_COLLISION_CHECK"))) {
            yield return WarpCollisionChecks();
            yield return ProbeShutdown.Run(engine, root, role);
            Log("FINAL PASS warp collision " + Environment.GetEnvironmentVariable("HOLLOWCONNECT_WARP_COLLISION_CHECK"));
            Flag(role + ".done"); yield break;
        }
        if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable("HOLLOWCONNECT_HORNET_STORY"))) {
            yield return HornetStoryGateChecks();
            yield return ProbeShutdown.Run(engine, root, role);
            Log("FINAL OBSERVATION COMPLETE Hornet story " + Environment.GetEnvironmentVariable("HOLLOWCONNECT_HORNET_STORY"));
            Flag(role + ".done"); yield break;
        }
        if (Environment.GetEnvironmentVariable("HOLLOWCONNECT_BOSS_COMPLETION_INTENT") == "1") {
            yield return BossCompletionIntentChecks();
            yield return ProbeShutdown.Run(engine, root, role);
            Log("FINAL COMPLETE A3 boundary execution; product assertions recorded separately");
            Flag(role + ".done"); yield break;
        }
        if (Environment.GetEnvironmentVariable("HOLLOWCONNECT_BOSS_COMPLETION_INTENT") == "native-context" ||
            Environment.GetEnvironmentVariable("HOLLOWCONNECT_BOSS_COMPLETION_INTENT") == "native-entity") {
            yield return BossCompletionNativeChecks();
            yield return ProbeShutdown.Run(engine, root, role);
            Log("FINAL PASS native boss completion " + Environment.GetEnvironmentVariable("HOLLOWCONNECT_BOSS_COMPLETION_INTENT"));
            Flag(role + ".done"); yield break;
        }
        if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable("HOLLOWCONNECT_SETTLEMENT_DISCONNECT"))) {
            yield return SettlementDisconnectChecks();
            yield return ProbeShutdown.Run(engine, root, role);
            Log("FINAL PASS targeted settlement disconnect " + Environment.GetEnvironmentVariable("HOLLOWCONNECT_SETTLEMENT_DISCONNECT"));
            Flag(role + ".done"); yield break;
        }
        if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable("HOLLOWCONNECT_FK_GATE_CHECK"))) {
            yield return FalseKnightGateChecks();
            yield return ProbeShutdown.Run(engine, root, role);
            Log(Environment.GetEnvironmentVariable("HOLLOWCONNECT_FK_GATE_CHECK") == "schema"
                ? "FINAL PASS False Knight schema and armour preflight only"
                : "FINAL PASS False Knight native gates and walking"); Flag(role + ".done"); yield break;
        }
        if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable("HOLLOWCONNECT_REPORTED_BOSS_CHECK"))) {
            yield return ReportedBossChecks();
            yield return ProbeShutdown.Run(engine, root, role);
            Log("FINAL PASS reported boss checks"); Flag(role + ".done"); yield break;
        }
        if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable("HOLLOWCONNECT_PATH_OF_PAIN_CHECK"))) {
            yield return PathOfPainChecks();
            yield return ProbeShutdown.Run(engine, root, role);
            Log(Environment.GetEnvironmentVariable("HOLLOWCONNECT_PATH_OF_PAIN_CHECK") == "schema"
                ? "FINAL PASS Path of Pain schema preflight only"
                : "FINAL PASS Path of Pain native completion and abandoned shade return");
            Flag(role + ".done"); yield break;
        }
        if (MenuPointerCheck) {
            yield return MenuPointerLockChecks();
            yield return ProbeShutdown.Run(engine, root, role);
            yield return FlexibleMenuUnlockChecks();
            Log("FINAL PASS scoped mouse navigation, native menu callbacks and room locks");
            Flag(role + ".done"); yield break;
        }
        if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable("HOLLOWCONNECT_COLLECTOR_GATE_CHECK"))) {
            yield return CollectorGateChecks();
            yield return ProbeShutdown.Run(engine, root, role);
            Log("FINAL PASS collector gate checks"); Flag(role + ".done"); yield break;
        }
        if (Environment.GetEnvironmentVariable("HOLLOWCONNECT_NOSK_STALL_CHECK") == "1") {
            yield return NoskStallChecks();
            yield return ProbeShutdown.Run(engine, root, role);
            Log("FINAL PASS nosk targeted checks"); Flag(role + ".done"); yield break;
        }
        if (Environment.GetEnvironmentVariable("HOLLOWCONNECT_RELATED_BOSS_SCENE_CHECK") == "1") {
            yield return RelatedBossSceneChecks();
            yield return ProbeShutdown.Run(engine, root, role);
            Log("FINAL PASS additive boss settlement and warp collision checks");
            Flag(role + ".done"); yield break;
        }
        if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable("HOLLOWCONNECT_EXHAUSTED_BOSS_CHECK"))) {
            yield return ExhaustedBossChecks();
            yield return ProbeShutdown.Run(engine, root, role);
            Log("FINAL PASS exhausted story boss " + Environment.GetEnvironmentVariable("HOLLOWCONNECT_EXHAUSTED_BOSS_CHECK"));
            Flag(role + ".done"); yield break;
        }
        if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable("HOLLOWCONNECT_STORY_RESCUE_CASE"))) {
            yield return StoryRescuePlacementChecks();
            yield return ProbeShutdown.Run(engine, root, role);
            Log("FINAL PASS story rescue placement " + Environment.GetEnvironmentVariable("HOLLOWCONNECT_STORY_RESCUE_CASE"));
            Flag(role + ".done"); yield break;
        }
        if (FlexibleTargetCheck) {
            yield return FlexibleTargetChecks();
            yield return ProbeShutdown.Run(engine, root, role);
            yield return FlexibleMenuUnlockChecks();
            Log("FINAL PASS flexible targets, snapshots, authority handoff and locked room settings");
            Flag(role + ".done"); yield break;
        }
        if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable("HOLLOWCONNECT_BOSS_TARGET_AUTHORITY"))) {
            yield return BossTargetChecks();
            yield return ProbeShutdown.Run(engine, root, role);
            Log("FINAL PASS boss targets living teammate across down, movement and revival");
            Flag(role + ".done"); yield break;
        }
        if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable("HOLLOWCONNECT_RESCUE_RULE_CHECK"))) {
            yield return RescueRuleChecks();
            yield return ProbeShutdown.Run(engine, root, role);
            Log("FINAL PASS rescue rules " + Environment.GetEnvironmentVariable("HOLLOWCONNECT_RESCUE_RULE_CHECK"));
            Flag(role + ".done"); yield break;
        }
        if (SoulMasterCheck) {
            yield return SoulMasterChecks();
            yield return ProbeShutdown.Run(engine, root, role);
            Log("FINAL PASS Soul Master targeted " + Environment.GetEnvironmentVariable("HOLLOWCONNECT_SOUL_MASTER_CHECK"));
            Flag(role + ".done");
            yield break;
        }
        if (VinePlatformCheck) {
            yield return LocalVinePlatformChecks();
            yield return ProbeShutdown.Run(engine, root, role);
            Log("FINAL PASS native vine platforms only respond to each local player's nail"); Flag(role + ".done");
            yield break;
        }
        if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable("HOLLOWCONNECT_ENCOUNTER_CASE"))) {
            yield return EncounterWaveChecks();
            yield return ProbeShutdown.Run(engine, root, role);
            Log("FINAL PASS native encounter " + Environment.GetEnvironmentVariable("HOLLOWCONNECT_ENCOUNTER_CASE"));
            Flag(role + ".done"); yield break;
        }
        if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable("HOLLOWCONNECT_LOCAL_BATTLE_CHECK"))) {
            yield return LocalBattleChecks();
            if (PostBossDamageCheck) yield return PostBossRoomCleanupChecks();
            yield return ProbeShutdown.Run(engine, root, role);
            if (PostBossDamageCheck) {
                Check(runtime.CurrentBossHealthMultiplier() == 1, "disconnect disables boss damage multiplier");
                Check(PlayerDamageSignature() == postBossPlayerDamage, "disconnect leaves player damage properties unchanged");
            }
            Log(Environment.GetEnvironmentVariable("HOLLOWCONNECT_LOCAL_BATTLE_CHECK") == "mother"
                ? "FINAL PASS native Gruz mother and child waves, local gates"
                : "FINAL PASS local arena battle completion, barriers and personal reward"); Flag(role + ".done");
            yield break;
        }
        if (Environment.GetEnvironmentVariable("HOLLOWCONNECT_BOSS_VICTORY_CHECK") == "1") {
            yield return BossVictoryChecks();
            yield return ProbeShutdown.Run(engine, root, role);
            Log("FINAL PASS boss victory shadow recovery and native control"); Flag(role + ".done");
            yield break;
        }
        if (Environment.GetEnvironmentVariable("HOLLOWCONNECT_PLAYER_MOTION_CHECK") == "1") {
            yield return CoveragePlayerMotionChecks.Run(Log);
            Flag(role + ".motion");
            yield return Until(() => Has((host ? "guest" : "host") + ".motion"), "both player motion checks complete");
            yield return ProbeShutdown.Run(engine, root, role);
            Log("FINAL PASS player motion interpolation, bounded prediction and packet ordering"); Flag(role + ".done");
            yield break;
        }
        if (Environment.GetEnvironmentVariable("HOLLOWCONNECT_DEATH_CAMERA_CHECK") == "1") {
            yield return DeathCameraRescueChecks();
            yield return ProbeShutdown.Run(engine, root, role);
            Log("FINAL PASS death camera follows native safe rescue point"); Flag(role + ".done");
            yield break;
        }
        if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable("HOLLOWCONNECT_RESCUE_EDGE_CHECK"))) {
            yield return EdgeRescueChecks();
            yield return ProbeShutdown.Run(engine, root, role);
            Log("FINAL PASS rescue edge audit"); Flag(role + ".done");
            yield break;
        }
        if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable("HOLLOWCONNECT_RESCUE_NATIVE_CHECK"))) {
            yield return NativeRescueChecks();
            yield return ProbeShutdown.Run(engine, root, role);
            Log("FINAL PASS native rescue placement and protection"); Flag(role + ".done");
            yield break;
        }
        if (Environment.GetEnvironmentVariable("HOLLOWCONNECT_HAZARD_RESCUE_CHECK") == "1") {
            yield return HazardRescueChecks();
            yield return ProbeShutdown.Run(engine, root, role);
            Log("FINAL PASS hazard rescue at native safe point"); Flag(role + ".done");
            yield break;
        }
        using var rewardFixtureDamage = IgnoreExternalCombatDamage();
        GameManager.instance.BeginSceneTransition(new GameManager.SceneLoadInfo { SceneName = "Crossroads_01", EntryGateName = "left1",
            Visualization = GameManager.SceneLoadVisualizations.Default, WaitForSceneTransitionCameraFade = true });
        yield return Until(() => runtime.Playable && UnityEngine.SceneManagement.SceneManager.GetActiveScene().name == "Crossroads_01" &&
            HeroController.instance.acceptingInput && !HeroController.instance.cState.transitioning &&
            HeroController.instance.transitionState == GlobalEnums.HeroTransitionState.WAITING_TO_TRANSITION && engine.ClientManager.SceneSnapshotReady, "native entry and snapshot ready");
        Freeze(); Flag(role + ".ready"); yield return Until(() => Has((host ? "guest" : "host") + ".ready") && runtime.Peers.Count == 1, "both ready");
        yield return Ordering();
        Flag(role + ".ordered"); yield return Until(() => Has((host ? "guest" : "host") + ".ordered"), "ordering probes complete");
        // Isolate the rescue's AddMPCharge reward from native enemies that
        // can wander into either swing beside the new grounded anchors.
        ModHooks.SoulGainHook += amount => { Log("native combat soul excluded from reward fixture: " + amount); return 0; };
        using var shadeTrace = new MonoMod.RuntimeDetour.Hook(typeof(ShadeView).GetMethod("Hit"),
            new Action<Action<ShadeView, HitInstance>, ShadeView, HitInstance>((orig, view, hit) => {
                Log("TRACE shade collision serial=" + view.Serial + " source=" + hit.Source?.name + " type=" + hit.AttackType + " damage=" + hit.DamageDealt);
                orig(view, hit);
            }));
        using var requestTrace = new MonoMod.RuntimeDetour.Hook(typeof(CoopRuntime).GetMethod("RequestShadeHit", BindingFlags.Instance | BindingFlags.NonPublic),
            new Func<Func<CoopRuntime, ushort, uint, bool>, CoopRuntime, ushort, uint, bool>((orig, owner, target, serial) => {
                var accepted = orig(owner, target, serial);
                Log("TRACE shade request target=" + target + " serial=" + serial + " sent=" + accepted + " hero=" + HeroController.instance.transform.position);
                return accepted;
            }));
        using var serverTrace = new MonoMod.RuntimeDetour.Hook(typeof(CoopServerAddon).GetMethod("Receive", BindingFlags.Instance | BindingFlags.NonPublic),
            new Action<Action<CoopServerAddon, ushort, Message>, CoopServerAddon, ushort, Message>((orig, addon, id, message) => {
                if (message.Kind == Kind.ShadeHit) {
                    var party = Field<Party>(addon, "party");
                    party.Members.TryGetValue(id, out var rescuer); party.Members.TryGetValue(message.Target, out var target);
                    Log("TRACE server shade request serial=" + message.Serial + " rescuer=" + rescuer?.Life + "/" + rescuer?.X + "/" + rescuer?.Y +
                        " target=" + target?.Life + "/" + target?.X + "/" + target?.Y + " quota=" + target?.RescuesUsed + "/" + party.RescueLimit);
                }
                orig(addon, id, message);
            }));
        for (var i = 0; i < 4; i++) yield return Trial(i);
        yield return ProbeShutdown.Run(engine,root,role);
        Log("FINAL PASS rescue soul and native feedback"); Flag(role + ".done");
    }
    private IEnumerator Ordering() {
        var pd = PlayerData.instance; pd.SetInt("MPCharge", 0); pd.SetInt("MPReserve", 0); pd.SetBool("soulLimited", false);
        // Exercise the production handler after its shade view has already
        // been removed by an Alive roster, using a synthetic peer only.
        const ushort target = 65000;
        Inject(new Message { Kind = Kind.Roster, Id = target, Aux = 3, Serial = 10, Key = "1", Value = (int)Life.Down, Scene = "Crossroads_01", X = 20, Y = 12 });
        yield return null;
        Inject(new Message { Kind = Kind.Roster, Id = target, Aux = 4, Serial = 11, Key = "1", Value = (int)Life.Alive, Scene = "Crossroads_01", X = 20, Y = 12 });
        yield return null;
        var accepted = new Message { Kind = Kind.Revived, Id = target, Target = runtime.LocalId, Aux = 4, Serial = 1, Scene = "Crossroads_01", X = 20, Y = 12 };
        Inject(accepted); Inject(accepted);
        Check(pd.GetInt("MPCharge") == 33 && Object.FindObjectsOfType<RescueBreakEffect>().Length == 1, "Alive roster before reliable rescue still rewards and presents exactly once");
        Time.timeScale = 0; yield return new WaitForSecondsRealtime(.6f);
        var fx = Object.FindObjectOfType<RescueBreakEffect>(); Check(fx != null, "native effect survives hit-stop");
        Check(fx.GetComponent<tk2dSpriteAnimator>().CurrentClip.name == "Death" && fx.transform.Find("Death Glow").gameObject.activeSelf, "native Death and Death Glow advance in unscaled time");
        Check(fx.transform.Find("Particle Wave").GetComponent<ParticleSystem>().particleCount > 0, "native shade particle wave emitted");
        var audio = fx.GetComponents<AudioSource>();
        Check(audio.Length == 2 && audio.Any(a => a.clip.name == "boss_explode_clean") && audio.Any(a => a.clip.name == "hero_void_scream_spell"), "both original own-shade break audio assets attached");
        Check(audio.All(a => a.outputAudioMixerGroup != null), "break audio respects native mixer");
        Check(fx.GetComponentsInChildren<PlayMakerFSM>(true).Length == 0 && fx.GetComponentsInChildren<Collider2D>(true).Length == 0, "visual clone has no native recovery FSM or collision");
        Inject(new Message { Kind = Kind.Down, Id = target, Aux = 5, Serial = 2, Scene = "Crossroads_01", X = 20, Y = 12 }); Inject(accepted);
        Check(runtime.Peers[target].Life == Life.Down && pd.GetInt("MPCharge") == 33, "old rescue cannot reward or revive a later death");
        Inject(new Message { Kind = Kind.Revived, Id = target, Target = runtime.LocalId, Aux = 6, Serial = 2, Scene = "Crossroads_01", X = 20, Y = 12 });
        Check(pd.GetInt("MPCharge") == 66, "a new accepted death earns one new reward");
        // Verify the actual session gate separately, without Inject rewriting it.
        var wrong = new Message { Kind = Kind.Revived, Id = target, Target = runtime.LocalId, Aux = 8, Serial = 3, Scene = "Crossroads_01", Session = "wrong", Round = Hkmp.CoopContext.Epoch };
        typeof(CoopRuntime).GetMethod("Receive", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(runtime, new object[] { wrong });
        Check(pd.GetInt("MPCharge") == 66, "wrong-session event cannot reward");
        wrong.Session = runtime.Session; wrong.Round++;
        typeof(CoopRuntime).GetMethod("Receive", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(runtime, new object[] { wrong });
        Check(pd.GetInt("MPCharge") == 66, "wrong-round event cannot reward");
        runtime.Resetting = true;
        Inject(new Message { Kind = Kind.Revived, Id = target, Target = runtime.LocalId, Aux = 8, Serial = 3, Scene = "Crossroads_01" });
        runtime.Resetting = false;
        Check(pd.GetInt("MPCharge") == 66, "wipe in progress rejects rescue reward");
        Inject(new Message { Kind = Kind.Roster, Id = target, Aux = 7, Serial = 12, Key = "3", Value = (int)Life.Returning, Scene = "Crossroads_01" });
        Inject(accepted);
        Check(pd.GetInt("MPCharge") == 66 && runtime.Peers[target].Life == Life.Returning, "old rescue after voluntary death grants no soul");
        Inject(new Message { Kind = Kind.Left, Id = target });
        yield return new WaitForSecondsRealtime(2.7f); Time.timeScale = 1;
        Check(Object.FindObjectsOfType<RescueBreakEffect>().Length == 0, "all accepted effects clean up while paused");
    }
    private IEnumerator Trial(int i) {
        var prefix = "trial" + i; var rescuer = i % 2 == 0 ? host : !host; var pd = PlayerData.instance;
        Freeze(); pd.SetInt("health", 4); pd.SetInt("healthBlue", 0); pd.SetBool("soulLimited", i == 3);
        pd.SetInt("geo", host ? 123 : 456); pd.SetInt("geoPool", 78); pd.SetString("shadeScene", "Crossroads_10");
        pd.SetInt("MPReserveMax", 33); pd.SetInt("MPCharge", rescuer ? (i == 1 ? 90 : i == 2 ? 99 : 0) : 7); pd.SetInt("MPReserve", i == 2 && rescuer ? 33 : 0);
        Flag(prefix + "." + role + ".ready"); yield return Until(() => Has(prefix + "." + (host ? "guest" : "host") + ".ready"), prefix + " ready");
        uint serial; ushort target; uint version;
        if (!rescuer) {
            Die(); yield return Until(() => runtime.Down, prefix + " victim down");
            serial = runtime.DeathSerial; target = runtime.LocalId; Flag(prefix + ".down");
            yield return Until(() => !runtime.Down && pd.GetInt("health") == 1, prefix + " one-health revival");
            Check(pd.GetInt("MPCharge") == 7 && pd.GetInt("MPReserve") == 0, prefix + " victim receives no rescue soul");
            version = 0;
        } else {
            yield return Until(() => Has(prefix + ".down") && runtime.Peers.Values.Any(p => p.Life == Life.Down), prefix + " teammate down received");
            var peer = runtime.Peers.Single(); target = peer.Key; serial = peer.Value.DownSerial;
            var service = Field<ShadeService>(runtime, "shades"); yield return Until(() => service.Get(target) != null, prefix + " visible rescue shade");
            Check(!runtime.RequestShadeHit(target, serial + 1) && !runtime.RequestShadeHit(runtime.LocalId, serial), prefix + " wrong death and self rescue rejected");
            var before = pd.GetInt("MPCharge"); var beforeReserve = pd.GetInt("MPReserve");
            // Send a deliberately invalid request through the real server too.
            typeof(CoopRuntime).GetMethod("Send", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(runtime,
                new object[] { new Message { Kind = Kind.ShadeHit, Target = target, Serial = serial + 1, Scene = "Crossroads_01" } });
            var pos = service.Get(target).transform.position; var h = HeroController.instance;
            // Rescue anchors now sit by native ground. Swing away in clear
            // air so a nearby native enemy cannot award unrelated nail soul.
            h.transform.position = pos + new Vector3(-2, 6, 0); h.GetComponent<Rigidbody2D>().velocity = Vector2.zero;
            h.FaceLeft(); h.Attack(GlobalEnums.AttackDirection.normal); Physics2D.SyncTransforms();
            yield return new WaitForSecondsRealtime(.75f);
            Log(prefix + " missed swing state=" + runtime.Peers[target].Life + " soul=" + pd.GetInt("MPCharge") + "/" + before +
                " reserve=" + pd.GetInt("MPReserve") + "/" + beforeReserve + " hero=" + h.transform.position + " shade=" + pos);
            Check(runtime.Peers[target].Life == Life.Down && pd.GetInt("MPCharge") == before && pd.GetInt("MPReserve") == beforeReserve, prefix + " rejected request and missed swing grant no reward");
            yield return Until(() => !h.cState.attacking, prefix + " missed nail swing fully finished");
            var heroBox = h.GetComponent<Collider2D>();
            var offset = heroBox.bounds.center - h.transform.position;
            var strike = Vector3.zero; var clearStrike = false;
            foreach (var distance in new[] { 2f, 1.4f, 1f }) {
                foreach (var side in new[] { -1, 1 }) {
                    var candidate = pos + Vector3.right * (side * distance);
                    var blocked = Physics2D.OverlapBoxAll(candidate + offset, heroBox.bounds.size * .9f, 0, 1 << 8).Any(c => !c.isTrigger);
                    Log(prefix + " strike geometry candidate=" + candidate + " blocked=" + blocked);
                    if (blocked) continue;
                    strike = candidate; clearStrike = true; break;
                }
                if (clearStrike) break;
            }
            Check(clearStrike, prefix + " actual nail strike has a clear hero position beside native anchor");
            h.transform.position = strike; h.AffectedByGravity(false); h.GetComponent<Rigidbody2D>().velocity = Vector2.zero;
            if (strike.x < pos.x) h.FaceRight(); else h.FaceLeft();
            h.Attack(GlobalEnums.AttackDirection.normal); Physics2D.SyncTransforms();
            Log(prefix + " strike physics=" + h.GetComponent<Rigidbody2D>().bodyType + " input=" + h.acceptingInput + "/" + h.controlReqlinquished +
                " attack=" + h.cState.attacking + " atBench=" + pd.GetBool("atBench") + " hero=" + h.transform.position + " shade=" + service.Get(target).transform.position);
            yield return Until(() => runtime.Peers[target].Life == Life.Alive, prefix + " actual nail rescue accepted");
            Log(prefix + " accepted rescue soul=" + pd.GetInt("MPCharge") + " reserve=" + pd.GetInt("MPReserve"));
            Check(pd.GetInt("MPCharge") == (i == 1 || i == 2 ? 99 : 33) && pd.GetInt("MPReserve") == (i == 1 ? 24 : i == 2 ? 33 : 0), prefix + " rescuer soul and reserve match native cap");
            version = runtime.Peers[target].LifeVersion;
            var effects = Object.FindObjectsOfType<RescueBreakEffect>().Length;
            var replay = new Message { Kind = Kind.Revived, Id = target, Target = runtime.LocalId, Serial = serial, Aux = version, Scene = "Crossroads_01", X = pos.x, Y = pos.y };
            Inject(replay); Inject(replay);
            Check(Object.FindObjectsOfType<RescueBreakEffect>().Length == effects && pd.GetInt("MPCharge") == (i == 1 || i == 2 ? 99 : 33), prefix + " reliable replay grants no additional soul or effect");
        }
        Check(Object.FindObjectsOfType<RescueBreakEffect>().Length == 1, prefix + " both clients see one native rescue break");
        Check(pd.GetInt("geo") == (host ? 123 : 456) && pd.GetInt("geoPool") == 78 && pd.GetString("shadeScene") == "Crossroads_10" && pd.GetBool("soulLimited") == (i == 3), prefix + " personal geo shade and soul limitation remain intact");
        Flag(prefix + "." + role + ".checked"); yield return Until(() => Has(prefix + "." + (host ? "guest" : "host") + ".checked"), prefix + " both checked");
        yield return new WaitForSecondsRealtime(2.7f);
        Check(Object.FindObjectsOfType<RescueBreakEffect>().Length == 0, prefix + " feedback fully retired");
    }
}
