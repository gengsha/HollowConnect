using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using Hkmp.Game.Client.Entity;
using MonoMod.RuntimeDetour;
using Newtonsoft.Json;
using UnityEngine;

namespace HollowConnect.Smoke;

public sealed partial class CoverageProbe {
    private static bool HitStopCase => Environment.GetEnvironmentVariable("HOLLOWCONNECT_HITSTOP_CHECK") == "1";
    private sealed class StopFrame {
        public long UtcTicks;
        public float WallMs, TimeScale, DeltaMs, HeroX, HeroY;
        public bool Accepting, RecoilFrozen, Recoiling, ControlRelinquished, Invulnerable;
        public float BossX, BossY;
        public StopProjectile[] Projectiles;
    }
    private sealed class StopProjectile { public ulong Id; public float X, Y; }
    private static bool StopBall(Entity e) => e.Type == EntityType.GrimmFireball;
    private GameObject StopObject(Entity e) => manager.IsSceneHost ? e.Object.Host : e.Object.Client;
    private IEnumerator HitStopChecks() {
        var scene = Environment.GetEnvironmentVariable("HOLLOWCONNECT_HITSTOP_SCENE") ?? "GG_Grimm";
        var expectedAuthority = Environment.GetEnvironmentVariable("HOLLOWCONNECT_HITSTOP_AUTHORITY") ?? "host";
        var injecting = false; var ignoredContacts = 0;
        // takeNoDamage prevents health loss but still plays native recoil and
        // hit-stop. Isolate the one intentional contact at the real boundary.
        using var contactGate = new Hook(typeof(HeroController).GetMethod("TakeDamage"),
            (Action<Action<HeroController, GameObject, GlobalEnums.CollisionSide, int, int>, HeroController, GameObject, GlobalEnums.CollisionSide, int, int>)((orig, self, source, side, amount, hazard) => {
                if (injecting) orig(self, source, side, amount, hazard);
                else ignoredContacts++;
            }));
        HollowConnect.CoopMod.Settings.EntryProtection = false;
        BossSequenceController.Reset(); PlayerData.instance.SetInt("bossStatueTargetLevel", 0);
        BossSceneController.SetupEvent = c => c.BossLevel = 0;
        PlayerData.instance.SetString("dreamReturnScene", "Town");
        if (role != expectedAuthority) yield return new WaitForSecondsRealtime(3);
        Enter(scene); yield return Until(() => In(scene) && manager.IsSceneHostDetermined, "hit-stop native arena authority");
        var hero = HeroController.instance;
        hero.takeNoDamage = true; hero.AffectedByGravity(false); hero.RegainControl(); hero.StartAnimationControl();
        hero.transform.position = new Vector3(80, 8, 0); hero.GetComponent<Rigidbody2D>().velocity = Vector2.zero;
        yield return Until(() => Entities.Values.Any(e => e.Type == EntityType.Grimm || e.Type == EntityType.NightmareKingGrimm), "hit-stop native boss");
        if (manager.IsSceneHost != (role == expectedAuthority)) throw new Exception("Unexpected authority: " + role + " expected=" + expectedAuthority + " actual=" + manager.IsSceneHost);
        var boss = Entities.Values.Single(e => e.Type == EntityType.Grimm || e.Type == EntityType.NightmareKingGrimm);
        Flag(role + ".hitstop-room");
        yield return Until(() => Has((host ? "guest" : "host") + ".hitstop-room"), "both hit-stop rooms ready");
        // Keep native entry/bow/AI. Only damage the native health manager at
        // its ordinary thresholds; never set a boss FSM state or projectile position.
        yield return new WaitForSecondsRealtime(8);
        var freezeRequests = 0; var freezeKinds = new List<object>();
        var method = typeof(GameManager).GetMethod("FreezeMoment", new[] { typeof(float), typeof(float), typeof(float), typeof(float) });
        using var freeze = new Hook(method,
            (Func<Func<GameManager, float, float, float, float, IEnumerator>, GameManager, float, float, float, float, IEnumerator>)((orig, gm, down, wait, up, speed) => {
                freezeRequests++; freezeKinds.Add(new { utcTicks = DateTime.UtcNow.Ticks, down, wait, up, speed, stack = new StackTrace().ToString() });
                return orig(gm, down, wait, up, speed);
            }));
        var input = InputHandler.Instance.inputActions;
        var inputHeldUntil = 0f; var inputArmed = false; var inputReading = false; var inputEdges = 0; var attacks = 0;
        var attackTimes = new List<float>(); var edgeTimes = new List<float>();
        using var read = new Hook(typeof(HeroController).GetMethod("LookForQueueInput", BindingFlags.Instance | BindingFlags.NonPublic),
            (Action<Action<HeroController>, HeroController>)((orig, self) => {
                inputReading = true;
                try { orig(self); } finally { inputReading = false; }
            }));
        using var pressed = new Hook(typeof(InControl.OneAxisInputControl).GetProperty("IsPressed").GetGetMethod(),
            (Func<Func<InControl.OneAxisInputControl, bool>, InControl.OneAxisInputControl, bool>)((orig, self) =>
                ReferenceEquals(self, input.attack) ? Time.realtimeSinceStartup < inputHeldUntil : orig(self)));
        using var down = new Hook(typeof(InControl.OneAxisInputControl).GetProperty("WasPressed").GetGetMethod(),
            (Func<Func<InControl.OneAxisInputControl, bool>, InControl.OneAxisInputControl, bool>)((orig, self) => {
                if (!ReferenceEquals(self, input.attack)) return orig(self);
                if (!inputArmed || !inputReading) return false;
                inputArmed = false; inputEdges++; edgeTimes.Add(Time.realtimeSinceStartup); return true;
            }));
        using var attack = new Hook(typeof(HeroController).GetMethod("Attack", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic),
            (Action<Action<HeroController, GlobalEnums.AttackDirection>, HeroController, GlobalEnums.AttackDirection>)((orig, self, direction) => {
                attacks++; attackTimes.Add(Time.realtimeSinceStartup); orig(self, direction);
            }));
        foreach (var group in new[] { "authority", "replica" }) {
            var victim = group == "authority" ? expectedAuthority : expectedAuthority == "host" ? "guest" : "host";
            if (manager.IsSceneHost) boss.Object.Host.GetComponent<HealthManager>().ApplyExtraDamage(scene == "GG_Grimm" ? (group == "authority" ? 201 : 200) : (group == "authority" ? 601 : 600));
            yield return Until(() => Entities.Values.Count(e => StopBall(e) && StopObject(e) != null && StopObject(e).activeInHierarchy) >= 4,
                "native balloon projectiles " + group);
            Flag(role + ".hitstop-ready-" + group);
            yield return Until(() => Has((host ? "guest" : "host") + ".hitstop-ready-" + group), "both balloon replicas ready " + group);
            var samples = new List<StopFrame>(1200);
            var health = PlayerData.instance.health;
            var calls = freezeRequests; var fired = false; var stamp = Stopwatch.GetTimestamp();
            var attacksBefore = attacks; var edgesBefore = inputEdges;
            var firstInput = false; var queuedInput = false; var sawQueue = false;
            var guardedContacts = 0;
            var begin = Time.realtimeSinceStartup;
            while (Time.realtimeSinceStartup - begin < 2.5f) {
                if (!In(scene)) throw new Exception("Hit-stop fixture left arena");
                var elapsed = Time.realtimeSinceStartup - begin;
                if (role != victim) {
                    var cooldown = (float)typeof(HeroController).GetField("attack_cooldown", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(hero);
                    if (!firstInput && elapsed >= .1f || firstInput && !queuedInput && attacks == attacksBefore + 1 && cooldown > 0 && cooldown < .04f) {
                        if (firstInput) queuedInput = true; else firstInput = true;
                        inputArmed = true; inputHeldUntil = Time.realtimeSinceStartup + .1f;
                    }
                    sawQueue |= (bool)typeof(HeroController).GetField("attackQueuing", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(hero);
                }
                if (role == victim && !fired && Time.realtimeSinceStartup - begin >= .35f) {
                    fired = true;
                    var source = Entities.Values.First(e => StopBall(e) && StopObject(e) != null && StopObject(e).activeInHierarchy);
                    // Exercise native TakeDamage/StartRecoil and real animation
                    // packets. This is an injected contact, not collision coverage.
                    hero.takeNoDamage = false;
                    injecting = true;
                    try { hero.TakeDamage(StopObject(source), GlobalEnums.CollisionSide.left, 1, 1); }
                    finally { injecting = false; hero.takeNoDamage = true; }
                    File.WriteAllText(Path.Combine(root, "hitstop-fired-" + group), DateTime.UtcNow.Ticks.ToString());
                }
                if (role == victim && fired && guardedContacts < 2 && elapsed >= (guardedContacts == 0 ? .5f : .9f)) {
                    injecting = true; hero.takeNoDamage = false;
                    try { hero.TakeDamage(null, GlobalEnums.CollisionSide.left, 1, 1); }
                    finally { injecting = false; hero.takeNoDamage = true; }
                    guardedContacts++;
                }
                var now = Stopwatch.GetTimestamp(); var go = StopObject(boss);
                samples.Add(new StopFrame {
                    UtcTicks = DateTime.UtcNow.Ticks, WallMs = (float)((now - stamp) * 1000.0 / Stopwatch.Frequency),
                    TimeScale = Time.timeScale, DeltaMs = Time.unscaledDeltaTime * 1000,
                    HeroX = hero.transform.position.x, HeroY = hero.transform.position.y, Accepting = hero.acceptingInput,
                    RecoilFrozen = hero.cState.recoilFrozen, Recoiling = hero.cState.recoiling, ControlRelinquished = hero.controlReqlinquished,
                    Invulnerable = hero.cState.invulnerable,
                    BossX = go.transform.position.x, BossY = go.transform.position.y,
                    Projectiles = Entities.Values.Where(e => StopBall(e) && StopObject(e) != null && StopObject(e).activeInHierarchy)
                        .Select(e => new StopProjectile { Id = e.Id, X = StopObject(e).transform.position.x, Y = StopObject(e).transform.position.y }).ToArray()
                });
                stamp = now; yield return null;
            }
            var paused = samples.Count(s => s.TimeScale < .01f);
            var localCalls = freezeRequests - calls;
            File.WriteAllText(Path.Combine(root, role + ".hitstop-" + group + ".json"), JsonConvert.SerializeObject(new {
                scene, role, authority = manager.IsSceneHost, victim, injectedNativeContact = true, ignoredContacts, shared = HollowConnect.CoopMod.Settings.ShareProgress,
                linkedHealth = HollowConnect.CoopMod.Settings.LinkedHealth, healthBefore = health, healthAfter = PlayerData.instance.health,
                freezeRequests = localCalls, pausedFrames = paused, pausedWallMs = samples.Where(s => s.TimeScale < .01f).Sum(s => s.WallMs),
                recoveryMs = samples.Where(s => s.RecoilFrozen).Sum(s => s.WallMs), immunityMs = samples.Where(s => s.Invulnerable).Sum(s => s.WallMs),
                attacks = attacks - attacksBefore, edges = inputEdges - edgesBefore, sawQueue, queuedInput, attackTimes, edgeTimes,
                samples, freezeKinds = freezeKinds.Skip(calls).ToArray()
            }));
            if (!Has("hitstop-fired-" + group)) throw new Exception("Victim did not take injected native damage");
            var baseline = Environment.GetEnvironmentVariable("HOLLOWCONNECT_HITSTOP_BASELINE") == "1";
            if (role == victim && (PlayerData.instance.health != health - 1 || (baseline ? paused < 3 || localCalls != 1 : paused != 0 || localCalls != 0)))
                throw new Exception("Native damage/stop missing: health=" + health + "->" + PlayerData.instance.health + " paused=" + paused + " calls=" + localCalls);
            if (role != victim && (PlayerData.instance.health != health || paused != 0 || localCalls != 0))
                throw new Exception("Undamaged peer unexpectedly has local damage/stop: paused=" + paused + " calls=" + localCalls);
            if (!baseline && role == victim && (samples.Where(s => s.RecoilFrozen).Sum(s => s.WallMs) < 250 ||
                samples.Any(s => s.RecoilFrozen && !s.Invulnerable) || hero.cState.recoilFrozen || hero.cState.invulnerable))
                throw new Exception("Native local recovery/immunity timing or release failed");
            if (!baseline && role != victim && (!queuedInput || !sawQueue || attacks - attacksBefore != 2 || inputEdges - edgesBefore != 2))
                throw new Exception("Unaffected native queued attack failed: attacks=" + (attacks - attacksBefore) + " queue=" + sawQueue + " edges=" + (inputEdges - edgesBefore));
            if (!baseline) {
                var still = 0f; var longest = 0f;
                for (var i = 1; i < samples.Count; i++) {
                    var before = samples[i - 1].Projectiles.ToDictionary(p => p.Id);
                    var common = samples[i].Projectiles.Where(p => before.ContainsKey(p.Id)).ToArray();
                    if (common.Length >= 3 && common.All(p => Math.Abs(p.X - before[p.Id].X) < .00001f && Math.Abs(p.Y - before[p.Id].Y) < .00001f)) still += samples[i].WallMs;
                    else still = 0;
                    longest = Math.Max(longest, still);
                }
                if (longest >= 140) throw new Exception("Shared projectiles still freeze: " + longest + "ms");
                Mark("PASS continuous native projectiles longestStillMs=" + longest + " nativeQueuedAttack=" + sawQueue);
            }
            Mark("HITSTOP " + group + " victim=" + victim + " authority=" + manager.IsSceneHost + " localFreezeCalls=" + localCalls + " pausedFrames=" + paused + " renderFrames=" + samples.Count);
            Flag(role + ".hitstop-finished-" + group);
            yield return Until(() => Has((host ? "guest" : "host") + ".hitstop-finished-" + group), "both hit-stop samples completed");
            if (group == "authority") {
                yield return Until(() => !Entities.Values.Any(e => StopBall(e) && StopObject(e) != null && StopObject(e).activeInHierarchy), "native balloon wave clears");
                yield return new WaitForSecondsRealtime(2);
            }
        }
        if (Environment.GetEnvironmentVariable("HOLLOWCONNECT_HITSTOP_BASELINE") == "1") yield break;
        // Other native freezes (parry, scripted slow motion, etc.) keep their
        // original global clock. Exercise the same overload outside recoil.
        var nativeDone = false; var nativePaused = false;
        IEnumerator OtherFreeze() {
            yield return GameManager.instance.StartCoroutine(GameManager.instance.FreezeMoment(.001f, .08f, .02f, .0001f));
            nativeDone = true;
        }
        StartCoroutine(OtherFreeze());
        var by = Time.realtimeSinceStartup + 2;
        while (!nativeDone && Time.realtimeSinceStartup < by) { nativePaused |= Time.timeScale < .01f; yield return null; }
        if (!nativeDone || !nativePaused || Time.timeScale < .99f) throw new Exception("Unrelated native FreezeMoment was changed");
        Mark("PASS unrelated native FreezeMoment retains global pause and restores time scale");
        Flag(role + ".hitstop-disconnect-ready");
        yield return Until(() => Has((host ? "guest" : "host") + ".hitstop-disconnect-ready"), "both ready to disconnect during recovery");
        void Hurt() {
            injecting = true; hero.takeNoDamage = false;
            try { hero.TakeDamage(null, GlobalEnums.CollisionSide.left, 1, 1); }
            finally { injecting = false; hero.takeNoDamage = true; }
        }
        var beforeDisconnect = PlayerData.instance.health;
        Hurt();
        if (!hero.cState.recoilFrozen || !hero.cState.invulnerable || PlayerData.instance.health != beforeDisconnect - 1)
            throw new Exception("Disconnect fixture did not enter real native recovery");
        engine.ClientManager.Disconnect();
        by = Time.realtimeSinceStartup + 2.5f;
        while (hero.cState.invulnerable && Time.realtimeSinceStartup < by) {
            if (Time.timeScale < .99f) throw new Exception("Local recovery changed world time during disconnect");
            yield return null;
        }
        if (hero.cState.invulnerable || hero.cState.recoilFrozen || !hero.acceptingInput || PlayerData.instance.GetBool("disablePause"))
            throw new Exception("Disconnect left native recovery/control stuck");
        Mark("PASS disconnect during local recovery releases native hard stun, immunity and pause lock");
        var offlineHealth = PlayerData.instance.health; var offlineCalls = freezeRequests;
        Hurt();
        var offlinePaused = false;
        by = Time.realtimeSinceStartup + 2.5f;
        while ((hero.cState.recoilFrozen || hero.cState.invulnerable) && Time.realtimeSinceStartup < by) {
            offlinePaused |= Time.timeScale < .01f; yield return null;
        }
        if (!offlinePaused || freezeRequests != offlineCalls + 1 || PlayerData.instance.health != offlineHealth - 1 ||
            hero.cState.recoilFrozen || hero.cState.invulnerable || Time.timeScale < .99f)
            throw new Exception("Offline native damage freeze/recovery fallback failed");
        Mark("PASS offline damage restores original global hit-stop and native recovery");
    }
}
