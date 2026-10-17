using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using Hkmp.Game.Client.Entity;
using HollowConnect;
using Modding;
using UnityEngine;
using Object = UnityEngine.Object;

namespace HollowConnect.Smoke;

// Actual networked Unity clients, fresh profile 99, SaveGame blocked by ProbeMod.
public sealed class FinalBossEndingProbe : MonoBehaviour {
    private string role, root;
    private bool host, decline;
    private Hkmp.Game.GameManager engine;
    private CoopRuntime runtime;
    private CoverageFinalBossEndingChecks checks;
    private static string Scene => UnityEngine.SceneManagement.SceneManager.GetActiveScene().name;
    private static T Field<T>(object obj, string key) => (T)obj.GetType().GetField(key, BindingFlags.Instance | BindingFlags.NonPublic).GetValue(obj);
    private void Log(string value) => File.AppendAllText(Path.Combine(root, role + ".log"), DateTime.UtcNow.ToString("O") + " " + value + "\n");
    private bool Has(string name) => File.Exists(Path.Combine(root, name));
    private void Flag(string name) => File.WriteAllText(Path.Combine(root, name), "ok");
    private void Require(bool ok, string name) { if (!ok) throw new Exception(name); Log("PASS " + name); }
    private IEnumerator Until(Func<bool> test, string name, float seconds = 40) {
        var end = Time.realtimeSinceStartup + seconds;
        while (!test() && Time.realtimeSinceStartup < end) yield return null;
        if (!test()) {
            Log("TIMEOUT " + name + " scene=" + Scene + " transition=" + GameManager.instance.IsInSceneTransition);
            foreach (var fsm in Object.FindObjectsOfType<PlayMakerFSM>())
                if (fsm.name.Contains("Hollow") || fsm.name.Contains("Boss") || fsm.name == "RestBench")
                    Log("TIMEOUT FSM " + fsm.name + "/" + fsm.FsmName + "=" + fsm.ActiveStateName);
        }
        Require(test(), name);
    }
    private IEnumerator Start() {
        role = Environment.GetEnvironmentVariable("HOLLOWCONNECT_SMOKE_ROLE"); root = Environment.GetEnvironmentVariable("HOLLOWCONNECT_SMOKE_ROOT"); host = role == "host";
        var stack = new Stack<IEnumerator>(); stack.Push(Run());
        while (stack.Count > 0) {
            object current = null; bool more;
            try { more = stack.Peek().MoveNext(); if (more) current = stack.Peek().Current; }
            catch (Exception e) { Log("FAIL " + e); File.WriteAllText(Path.Combine(root, role + ".failed"), e.ToString()); checks?.Dispose(); yield break; }
            if (!more) { stack.Pop(); continue; }
            if (current is IEnumerator nested) stack.Push(nested); else yield return current;
        }
        checks?.Dispose();
    }
    private IEnumerator Press(InControl.PlayerAction action) {
        var frame = Time.frameCount + 1;
        using (new MonoMod.RuntimeDetour.Hook(typeof(InControl.OneAxisInputControl).GetProperty("WasPressed").GetGetMethod(),
            new Func<Func<InControl.OneAxisInputControl, bool>, InControl.OneAxisInputControl, bool>((orig, self) => ReferenceEquals(self, action) ? Time.frameCount == frame : orig(self))))
        using (new MonoMod.RuntimeDetour.Hook(typeof(InControl.OneAxisInputControl).GetProperty("IsPressed").GetGetMethod(),
            new Func<Func<InControl.OneAxisInputControl, bool>, InControl.OneAxisInputControl, bool>((orig, self) => ReferenceEquals(self, action) ? Time.frameCount >= frame && Time.frameCount < frame + 4 : orig(self))))
            for (var i = 0; i < 6; i++) yield return null;
    }
    private EntityManager Manager => Field<EntityManager>(engine.ClientManager, "_entityManager");
    private Entity Boss => Field<Dictionary<ulong, Entity>>(Manager, "_entities").Values.FirstOrDefault(e => e.Type == EntityType.HollowKnight);
    private IEnumerator Run() {
        Log("final boss native music/corpse/focus/A-B probe; muted, disposable unsaved characters");
        checks = new CoverageFinalBossEndingChecks(Log);
        On.AudioManager.ApplyMusicCue += (orig, audio, cue, delay, transition, apply) => {
            Log("MUSIC cue=" + (cue == null ? "null" : cue.name) + " scene=" + Scene);
            if (Scene == "Room_Final_Boss_Core" && cue != null && cue.name.IndexOf("HKDecline", StringComparison.OrdinalIgnoreCase) >= 0) {
                decline = true; Flag(role + ".decline");
                Log("MUSIC FIRST DECLINE health=" + PlayerData.instance.health + " boss=" + Boss?.Object.Host.GetComponent<HealthManager>()?.hp);
            }
            orig(audio, cue, delay, transition, apply);
        };
        On.HutongGames.PlayMaker.Fsm.EnterState += (orig, fsm, state) => {
            if (Scene == "Room_Final_Boss_Core" && (fsm.GameObject.name.Contains("Hollow") || fsm.GameObject.name.Contains("Boss Corpse") || fsm.GameObject.name == "Boss Control"))
                Log("NATIVE FSM " + fsm.GameObject.name + "/" + fsm.Name + "=" + state.Name);
            orig(fsm, state);
        };
        yield return Until(() => GameManager.instance != null && Object.FindObjectOfType<CoopRuntime>() != null && Scene == "Menu_Title", "menu ready");
        runtime = Object.FindObjectOfType<CoopRuntime>(); engine = Field<Hkmp.Game.GameManager>(ModHooks.GetMod("HKMP"), "_gameManager");
        Field<Hkmp.Game.Settings.ModSettings>(engine.ClientManager, "_modSettings").AuthKey = Hkmp.Util.AuthUtil.GenerateAuthKey();
        CoopMod.Settings.ShareProgress = false; CoopMod.Settings.Rescue = true; CoopMod.Settings.EntryProtection = false;
        var pd = PlayerData.instance;
        pd.SetString("respawnScene", "Town"); pd.SetString("respawnMarkerName", "RestBench"); pd.SetInt("respawnType", 1);
        pd.SetBool("gotShadeCharm", !host); pd.SetBool("hornetBlackFountain", false); pd.SetBool("killedHollowKnight", false);
        pd.SetBool("unchainedHollowKnight", true); // Native revisit entrance; avoid the unrelated first chain-cutting intro.
        GameManager.instance.profileID = 99; GameManager.instance.StartCoroutine(GameManager.instance.RunContinueGame());
        yield return Until(() => Scene == "Town" && HeroController.instance != null && HeroController.instance.isHeroInPosition && !GameManager.instance.IsInSceneTransition, "fresh Town loaded");
        if (pd.GetBool("atBench")) {
            var bench = Object.FindObjectsOfType<PlayMakerFSM>().Single(f => f.name == "RestBench" && f.FsmName == "Bench Control");
            yield return Until(() => bench.ActiveStateName == "Resting", "native initial bench resting");
            yield return Press(InputHandler.Instance.inputActions.jump);
        }
        yield return Until(() => HeroController.instance.acceptingInput, "initial native get up");
        HeroController.instance.takeNoDamage = true;
        if (host) { engine.ServerManager.Start(29650, false); Flag("server.started"); }
        else yield return Until(() => Has("server.started"), "server started");
        yield return new WaitForSecondsRealtime(1);
        typeof(Hkmp.Game.Client.ClientManager).GetField("_autoConnect", BindingFlags.Instance | BindingFlags.NonPublic).SetValue(engine.ClientManager, true);
        typeof(Hkmp.Game.Client.ClientManager).GetMethod("Connect", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(engine.ClientManager, new object[] { "127.0.0.1", 29650, "final" + role });
        yield return Until(() => runtime.Connected, "real UDP connection"); Flag(role + ".connected");
        yield return Until(() => Has((host ? "guest" : "host") + ".connected"), "both connected");
        if (!host) { yield return Until(() => Has("host.boss-entered"), "authority enters final boss first"); yield return new WaitForSecondsRealtime(3); }
        GameManager.instance.BeginSceneTransition(new GameManager.SceneLoadInfo { SceneName = "Room_Final_Boss_Core", EntryGateName = "left1",
            Visualization = GameManager.SceneLoadVisualizations.Default, WaitForSceneTransitionCameraFade = true });
        yield return Until(() => Scene == "Room_Final_Boss_Core" && HeroController.instance.isHeroInPosition && !GameManager.instance.IsInSceneTransition && engine.ClientManager.SceneSnapshotReady && Manager.IsSceneHostDetermined && Boss != null, "native final boss entrance and room snapshot");
        Require(Manager.IsSceneHost == host, "staggered room authority matches entry order");
        var boss = Boss;
        checks.Capture(boss, host); Flag(role + ".boss-entered");
        yield return Until(() => Has((host ? "guest" : "host") + ".boss-entered"), "both final boss entities captured");
        var hero = HeroController.instance; hero.takeNoDamage = true;
        hero.transform.position = new Vector3(40, 11, hero.transform.position.z); hero.GetComponent<Rigidbody2D>().velocity = Vector2.zero;
        var continuationOnly = Environment.GetEnvironmentVariable("HOLLOWCONNECT_FINAL_CASE") == "continuation";
        if (continuationOnly) Log("FIXTURE explicit native corpse continuation boundary only; battle/music/death acceptance belongs to preserved r3 logs");
        if (host) {
            var hp = boss.Object.Host.GetComponent<HealthManager>();
            var control = boss.Object.Host.GetComponents<PlayMakerFSM>().Single(f => f.FsmName == "Control");
            yield return Until(() => control.enabled && !string.IsNullOrEmpty(control.ActiveStateName), "native revisit starts authoritative Control", 15);
            if (continuationOnly) boss.Object.Host.GetComponents<PlayMakerFSM>().Single(f => f.FsmName == "Phase Control").SetState("Die");
            else {
                var end = Time.realtimeSinceStartup + 55;
                while (!decline && Time.realtimeSinceStartup < end) {
                    Log("NATIVE DAMAGE before=" + hp.hp + " state=" + control.ActiveStateName);
                    if (hp.hp > 330) Hit(hp, 80);
                    yield return new WaitForSecondsRealtime(.8f);
                }
            }
        }
        if (!continuationOnly) {
            yield return Until(() => decline && Has((host ? "guest" : "host") + ".decline"), "both hear first native self-stab music before death", 55);
            Require(!pd.GetBool("killedHollowKnight"), "first decline music arrives during battle before native death");
            yield return new WaitForSecondsRealtime(.5f);
            var playing = Resources.FindObjectsOfTypeAll<AudioSource>().Where(a => a.isPlaying && a.clip != null).Select(a => a.clip.name + "@" + a.time.ToString("F2")).ToArray();
            Require(playing.Length > 0, "native audio sources continue playing after decline cue"); Log("MUSIC PLAYBACK " + string.Join(",", playing));
            if (host) {
                var hp = boss.Object.Host.GetComponent<HealthManager>(); var end = Time.realtimeSinceStartup + 45;
                while (boss.Object.Host.activeSelf && Time.realtimeSinceStartup < end) { Hit(hp, 80); yield return new WaitForSecondsRealtime(.8f); }
            }
        }
        yield return checks.Ready(); Flag(role + ".corpse-ready");
        yield return Until(() => Has((host ? "guest" : "host") + ".corpse-ready"), "both native corpses ready");
        if (!host) { yield return Until(() => Has("host.ending"), "authority finishes its own ending"); yield return new WaitForSecondsRealtime(3); checks.CheckAfterAuthorityLeft(); }
        yield return checks.FinishByNativeFocus(); Flag(role + ".ending");
        yield return Until(() => Has((host ? "guest" : "host") + ".ending"), "both personal ending branches complete");
        yield return ProbeShutdown.Run(engine, root, role);
        Log("FINAL PASS " + (continuationOnly ? "explicit corpse continuation and real Focus endings A/B" : "native self-stab music, independent corpse and real Focus endings A/B")); Flag(role + ".done");
    }
    private static void Hit(HealthManager target, int amount) => target.Hit(new HitInstance { Source = HeroController.instance.gameObject,
        AttackType = AttackTypes.Nail, DamageDealt = amount, Multiplier = 1, IgnoreInvulnerable = true, Direction = 0, MagnitudeMultiplier = 1 });
}
