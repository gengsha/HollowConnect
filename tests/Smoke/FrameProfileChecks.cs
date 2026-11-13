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
    private static bool FrameProfile => Environment.GetEnvironmentVariable("HOLLOWCONNECT_FRAME_PROFILE") == "1";
    private static bool FrameTraceCheck => Environment.GetEnvironmentVariable("HOLLOWCONNECT_FRAME_TRACE_CHECK") == "1";
    private bool frameMeasuring;
    private sealed class FrameCost {
        public string Name;
        public int Calls, SlowCalls;
        public double TotalMs, MaxMs, FrameMs;
        public long Allocated;
    }
    private readonly List<FrameCost> frameCosts = new();
    private void ObserveFrameMethod<T>(MethodInfo method, string name, bool bypass) {
        var cost = new FrameCost { Name = name }; frameCosts.Add(cost);
        performanceHooks.Add(new Hook(method, (Action<Action<T>, T>)((orig, self) => {
            if (bypass) return;
            if (!frameMeasuring) { orig(self); return; }
            var bytes = allocatedBytes?.Invoke() ?? -1;
            var stamp = Stopwatch.GetTimestamp();
            orig(self);
            var ms = (Stopwatch.GetTimestamp() - stamp) * 1000.0 / Stopwatch.Frequency;
            cost.Calls++; cost.TotalMs += ms; cost.FrameMs += ms;
            cost.MaxMs = Math.Max(cost.MaxMs, ms); if (ms >= 4) cost.SlowCalls++;
            if (bytes >= 0) cost.Allocated += allocatedBytes() - bytes;
        })));
    }
    private void InstallFrameHooks() {
        var assembly = typeof(Entity).Assembly;
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        var ablate = Environment.GetEnvironmentVariable("HOLLOWCONNECT_FRAME_ABLATE") == "1";
        foreach (var spec in new[] {
            (Type: "Hkmp.Game.Client.Entity.Component.PresentationComponent", Method: "Tick", Bypass: ablate),
            (Type: "Hkmp.Game.Client.Entity.Component.PresentationComponent", Method: "Build", Bypass: false),
            (Type: "Hkmp.Game.Client.Entity.Component.CombatStateComponent", Method: "Tick", Bypass: false),
            (Type: "Hkmp.Game.Client.Entity.Component.CombatStateComponent", Method: "Build", Bypass: false),
            (Type: "Hkmp.Game.Client.Entity.Entity", Method: "OnUpdate", Bypass: false),
            (Type: "Hkmp.Game.Client.Entity.EntityManager", Method: "RetryPending", Bypass: false),
            (Type: "Hkmp.Game.Client.Save.SaveManager", Method: "OnUpdatePlayerData", Bypass: false),
            (Type: "Hkmp.Game.Client.Save.SaveManager", Method: "OnUpdatePersistents", Bypass: false),
            (Type: "Hkmp.Game.Client.Save.SaveManager", Method: "OnUpdateCompounds", Bypass: false),
            (Type: "Hkmp.Util.MonoBehaviourUtil", Method: "Update", Bypass: false)
        }) {
            var type = assembly.GetType(spec.Type); var method = type?.GetMethod(spec.Method, flags);
            if (method == null) { Mark("PROFILE absent " + spec.Type + "." + spec.Method); continue; }
            typeof(CoverageProbe).GetMethod(nameof(ObserveFrameMethod), flags).MakeGenericMethod(type)
                .Invoke(this, new object[] { method, type.Name + "." + spec.Method, spec.Bypass });
        }
        foreach (var name in new[] { "HollowConnect.CoopServerAddon", "HollowConnect.MapService" }) {
            var type = typeof(HollowConnect.CoopRuntime).Assembly.GetType(name);
            var method = type?.GetMethod(name.EndsWith("MapService") ? "Update" : "Tick", flags);
            if (method != null) typeof(CoverageProbe).GetMethod(nameof(ObserveFrameMethod), flags).MakeGenericMethod(type)
                .Invoke(this, new object[] { method, type.Name + "." + method.Name, false });
        }
        var steam = AppDomain.CurrentDomain.GetAssemblies().FirstOrDefault(a => a.GetName().Name == "HollowConnect.Steam")?.GetType("HollowConnect.Steam.SteamRoomRuntime");
        var steamUpdate = steam?.GetMethod("Update", flags);
        if (steamUpdate != null) typeof(CoverageProbe).GetMethod(nameof(ObserveFrameMethod), flags).MakeGenericMethod(steam)
            .Invoke(this, new object[] { steamUpdate, "SteamRoomRuntime.Update", false });
        Mark("PROFILE sharedProgress=" + HollowConnect.CoopMod.Settings.ShareProgress + " steamRuntimeLoaded=" + (steamUpdate != null));
        Mark("PROFILE current-thread allocation counter=" + (allocatedBytes == null ? "unavailable" : "available") + " presentationTickBypassed=" + ablate);
    }
    private IEnumerator FrameProfileChecks() {
        if (FrameTraceCheck) yield return StartFrameTraceValidation();
        else InstallFrameHooks();
        var targetFps = int.Parse(Environment.GetEnvironmentVariable("HOLLOWCONNECT_FRAME_TARGET_FPS") ?? "300", System.Globalization.CultureInfo.InvariantCulture);
        if (targetFps < 30 || targetFps > 1000) throw new ArgumentOutOfRangeException("HOLLOWCONNECT_FRAME_TARGET_FPS");
        Application.runInBackground = true; Application.targetFrameRate = targetFps; QualitySettings.vSyncCount = 0;
        PlayerData.instance.SetBool("hasDash", true); PlayerData.instance.SetBool("canDash", true);
        var input = InputHandler.Instance.inputActions;
        var motion = "idle"; var jumpFrame = -1; var dashFrame = -1; var releaseFrame = -1; var direction = 1f;
        bool Held(InControl.OneAxisInputControl self) => ReferenceEquals(self, input.jump) ? Time.frameCount >= jumpFrame && Time.frameCount < releaseFrame :
            ReferenceEquals(self, input.dash) ? Time.frameCount == dashFrame : false;
        bool Synthetic(InControl.OneAxisInputControl self) => ReferenceEquals(self, input.jump) || ReferenceEquals(self, input.dash) ||
            ReferenceEquals(self, input.attack) || ReferenceEquals(self, input.left) || ReferenceEquals(self, input.right);
        using var pressed = new Hook(typeof(InControl.OneAxisInputControl).GetProperty("IsPressed").GetGetMethod(),
            (Func<Func<InControl.OneAxisInputControl, bool>, InControl.OneAxisInputControl, bool>)((orig, self) =>
                ReferenceEquals(self, input.left) ? motion == "walk" && direction < 0 : ReferenceEquals(self, input.right) ? motion == "walk" && direction > 0 : Synthetic(self) ? Held(self) : orig(self)));
        using var down = new Hook(typeof(InControl.OneAxisInputControl).GetProperty("WasPressed").GetGetMethod(),
            (Func<Func<InControl.OneAxisInputControl, bool>, InControl.OneAxisInputControl, bool>)((orig, self) =>
                ReferenceEquals(self, input.jump) ? Time.frameCount == jumpFrame : ReferenceEquals(self, input.dash) ? Time.frameCount == dashFrame : Synthetic(self) ? false : orig(self)));
        using var up = new Hook(typeof(InControl.OneAxisInputControl).GetProperty("WasReleased").GetGetMethod(),
            (Func<Func<InControl.OneAxisInputControl, bool>, InControl.OneAxisInputControl, bool>)((orig, self) =>
                ReferenceEquals(self, input.jump) ? Time.frameCount == releaseFrame : Synthetic(self) ? false : orig(self)));
        // The tiny InControl Vector getter can already be inlined by Mono.
        // Supply the raw axis at the native FilterInput boundary instead;
        // native deadzones, movement, collisions and animation still run.
        using var move = new Hook(typeof(HeroController).GetMethod("FilterInput", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic),
            (Action<Action<HeroController>, HeroController>)((orig, self) => {
                self.move_input = motion == "walk" ? direction : 0; self.vertical_input = 0; orig(self);
            }));
        var seconds = float.Parse(Environment.GetEnvironmentVariable("HOLLOWCONNECT_FRAME_SECONDS") ?? "12", System.Globalization.CultureInfo.InvariantCulture);
        var scenes = (Environment.GetEnvironmentVariable("HOLLOWCONNECT_FRAME_SCENES") ?? "Town,GG_Painter").Split(',');
        foreach (var scene in scenes) {
            if (!In(scene)) {
                if (scene.StartsWith("GG_")) {
                    BossSequenceController.Reset(); PlayerData.instance.SetInt("bossStatueTargetLevel", 0);
                    BossSceneController.SetupEvent = c => c.BossLevel = 0;
                    PlayerData.instance.SetString("dreamReturnScene", "Town");
                }
                if (!host) yield return new WaitForSecondsRealtime(1);
                Enter(scene); yield return Until(() => In(scene), "frame fixture scene " + scene);
            }
            var hero = HeroController.instance;
            hero.takeNoDamage = true; hero.AffectedByGravity(true); hero.RegainControl(); hero.StartAnimationControl();
            yield return Until(() => hero.cState.onGround, "native floor " + scene);
            yield return Until(() => manager.IsSceneHostDetermined, "frame authority " + scene);
            if (scene != "Town") yield return Until(() => Entities.Count > 0, "managed native entities " + scene);
            Flag(role + ".frame-room-" + scene);
            yield return Until(() => Has((host ? "guest" : "host") + ".frame-room-" + scene), "both frame room " + scene);
            var anchor = hero.transform.position;
            foreach (var phase in new[] { "idle", "walk", "jumpdash" }) {
                motion = "idle"; jumpFrame = dashFrame = releaseFrame = -1;
                yield return new WaitForSecondsRealtime(2);
                Flag(role + ".frame-ready-" + scene + "-" + phase);
                yield return Until(() => Has((host ? "guest" : "host") + ".frame-ready-" + scene + "-" + phase), "frame phase " + scene + "/" + phase);
                motion = phase;
                foreach (var c in frameCosts) { c.Calls = c.SlowCalls = 0; c.TotalMs = c.MaxMs = c.FrameMs = 0; c.Allocated = 0; }
                // Fixed buffers avoid allocating one record/list per 300 FPS frame.
                var capacity = 30000; var dt = new float[capacity]; var wall = new double[capacity];
                var gc = new int[capacity]; var moduleMs = frameCosts.Select(_ => new double[capacity]).ToArray();
                var n = 0; var jumps = 0; var dashes = 0; var airborne = 0; var moving = 0;
                var minX = hero.transform.position.x; var maxX = minX;
                var collections = GC.CollectionCount(0); var lastGc = collections;
                var heap = GC.GetTotalMemory(false); var begin = Time.realtimeSinceStartup; var nextJump = begin + .3f;
                var dashAt = float.PositiveInfinity; var stamp = Stopwatch.GetTimestamp();
                frameMeasuring = true;
                while (Time.realtimeSinceStartup - begin < seconds && n < capacity) {
                    if (!In(scene)) throw new Exception("Frame fixture left its scene during " + phase + ": expected=" + scene + " actual=" + GameManager.instance.sceneName);
                    var now = Time.realtimeSinceStartup;
                    direction = hero.transform.position.x > anchor.x + 3 ? -1 : hero.transform.position.x < anchor.x - 3 ? 1 : direction;
                    if (phase == "jumpdash" && now >= nextJump && hero.cState.onGround) {
                        jumpFrame = Time.frameCount + 1; releaseFrame = jumpFrame + 30;
                        nextJump = now + 1.2f; dashAt = now + .15f; jumps++;
                    }
                    if (phase == "jumpdash" && now >= dashAt) {
                        dashFrame = Time.frameCount + 1; dashAt = float.PositiveInfinity;
                        // Native Dash reads cState.facingRight, not only the
                        // sprite's localScale. Keep both aligned through the
                        // original facing methods so alternate dashes stay local.
                        if (jumps % 2 == 0) hero.FaceRight(); else hero.FaceLeft();
                    }
                    if (hero.cState.dashing) dashes++;
                    if (!hero.cState.onGround) airborne++;
                    if (Math.Abs(hero.GetComponent<Rigidbody2D>().velocity.x) > 1) moving++;
                    minX = Math.Min(minX, hero.transform.position.x); maxX = Math.Max(maxX, hero.transform.position.x);
                    // Read at the same coroutine boundary after Update in both releases.
                    var current = Stopwatch.GetTimestamp(); wall[n] = (current - stamp) * 1000.0 / Stopwatch.Frequency; stamp = current;
                    dt[n] = Time.unscaledDeltaTime * 1000;
                    var currentGc = GC.CollectionCount(0); gc[n] = currentGc - lastGc; lastGc = currentGc;
                    for (var i = 0; i < frameCosts.Count; i++) { moduleMs[i][n] = frameCosts[i].FrameMs; frameCosts[i].FrameMs = 0; }
                    n++; yield return null;
                }
                frameMeasuring = false; motion = "idle"; jumpFrame = dashFrame = releaseFrame = -1;
                if (phase == "jumpdash" && (jumps < 3 || dashes < 3 || airborne < 3))
                    throw new Exception("Frame fixture did not execute native jump/dash: jumps=" + jumps + " dashFrames=" + dashes + " airborne=" + airborne + " state=" + hero.cState.onGround + "/" + hero.cState.dashing);
                if (phase == "walk" && (moving < 3 || maxX - minX < 1))
                    throw new Exception("Frame fixture did not execute native walk: movingFrames=" + moving + " range=" + (maxX - minX));
                var frames = Enumerable.Range(1, n - 1).Select(i => new { index = i, unityMs = dt[i], wallMs = wall[i], gc = gc[i], costs = moduleMs.Select(v => v[i]).ToArray() }).ToArray();
                var sorted = frames.Select(f => f.unityMs).OrderBy(v => v).ToArray();
                var result = new { scene, phase, targetFps, vSync = QualitySettings.vSyncCount, sceneAuthority = manager.IsSceneHost,
                    entities = Entities.Count, activeEntities = Entities.Values.Count(e => e.Object.Host != null && e.Object.Host.activeInHierarchy),
                    samples = frames.Length, elapsedSeconds = Time.realtimeSinceStartup - begin, jumps, dashFrames = dashes, airborne, movingFrames = moving, xRange = maxX - minX,
                    p50Ms = sorted[(int)((sorted.Length - 1) * .5)], p95Ms = sorted[(int)((sorted.Length - 1) * .95)],
                    p99Ms = sorted[(int)((sorted.Length - 1) * .99)], maxMs = sorted.Last(),
                    over16ms = sorted.Count(v => v > 16.667f), over33ms = sorted.Count(v => v > 33.333f),
                    collections = GC.CollectionCount(0) - collections, heapDeltaBytes = GC.GetTotalMemory(false) - heap,
                    allocationCounterAvailable = allocatedBytes != null, methods = frameCosts, frames };
                File.WriteAllText(Path.Combine(root, role + ".frames-" + scene + "-" + phase + ".json"), JsonConvert.SerializeObject(result));
                Mark("FRAME " + scene + "/" + phase + " p99=" + result.p99Ms + " max=" + result.maxMs + " gc=" + result.collections + " jumps=" + jumps + " dashFrames=" + dashes);
            }
        }
        foreach (var hook in performanceHooks) hook.Dispose(); performanceHooks.Clear();
        if (FrameTraceCheck) yield return FinishFrameTraceValidation();
    }
}
