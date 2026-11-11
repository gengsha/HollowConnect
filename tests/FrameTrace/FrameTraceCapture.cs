using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Threading;
using Mono.Cecil;
using Mono.Cecil.Cil;
using MonoMod.Cil;
using MonoMod.RuntimeDetour;
using Newtonsoft.Json;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnitySceneManager = UnityEngine.SceneManagement.SceneManager;

namespace HollowConnect.FrameTrace;

public sealed class FrameTraceCapture : MonoBehaviour {
    private const int Capacity = 65536;
    private const double Duration = 120;
    private const BindingFlags AnyInstance = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
    private const BindingFlags AnyStatic = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
    private static readonly CultureInfo Invariant = CultureInfo.InvariantCulture;
    public static FrameTraceCapture Instance { get; private set; }
    public string State { get; private set; } = "idle";
    public string LastOutputDirectory { get; private set; }
    public string LastError { get; private set; }
    public int FrameCount => count;
    private volatile bool measuring, exporting;
    private int mainThread, count, previousGc, markCount;
    private long previousLate, previousObserverTicks;
    private double warmUntil, started, nextMetadata, noticeUntil;
    private string notice = "", outputRoot;
    private Thread writer;
    private readonly List<IDisposable> hooks = new();
    private readonly List<Metric> metrics = new();
    private readonly List<string> coverage = new();
    private readonly Dictionary<string, ushort> sceneIds = new(StringComparer.Ordinal);
    private readonly List<string> scenes = new();
    private readonly List<(string Name, string Version, string Path)> binaries = new();
    private bool installed, installFailed, fixture, lastConnected;
    private string activeScene;
    private object netClient, updateManager, entityTable;
    private PropertyInfo connectedProperty, sentCountProperty;
    private ICollection actions;
    private int lastSent = -1, lastQueue = -1, lastEntities = -1;
    private PropertyInfo entityCountProperty;
    private string writerError;
    private volatile bool writerDone;
    private object machine;
    private struct Frame {
        internal int UnityFrame, Gc, Queue, Pending, Entities;
        internal ushort Scene;
        internal byte Flags;
        internal float UnityMs;
        internal long Stamp, WallTicks, ObserverTicks;
    }
    private Frame[] frames;
    private sealed class Metric {
        internal readonly string Name;
        internal readonly bool Background;
        internal long Ticks;
        internal int Calls;
        internal long[] Samples;
        internal int[] Counts;
        internal Metric(string name, bool background) { Name = name; Background = background; }
    }
    private void Awake() {
        Instance = this; mainThread = Thread.CurrentThread.ManagedThreadId;
        outputRoot = Path.Combine(Application.persistentDataPath, "HollowConnect-FrameTrace");
        activeScene = UnitySceneManager.GetActiveScene().name ?? "";
        UnitySceneManager.activeSceneChanged += SceneChanged;
    }
    private void SceneChanged(Scene previous, Scene current) { activeScene = current.name ?? ""; }
    private Metric AddMetric(string name, bool background = false) {
        var metric = new Metric(name, background); metrics.Add(metric); return metric;
    }
    private void Record(Metric metric, long ticks) {
        if (!measuring || (!metric.Background && Thread.CurrentThread.ManagedThreadId != mainThread)) return;
        Interlocked.Add(ref metric.Ticks, ticks); Interlocked.Increment(ref metric.Calls);
    }
    private void WatchInstance<T>(MethodInfo method, string label, bool background) {
        var metric = AddMetric(label, background);
        hooks.Add(new Hook(method, (Action<Action<T>, T>)((orig, self) => {
            if (!measuring) { orig(self); return; }
            var stamp = Stopwatch.GetTimestamp();
            try { orig(self); } finally { Record(metric, Stopwatch.GetTimestamp() - stamp); }
        })));
        coverage.Add("timing: " + label);
    }
    private void Watch(Type type, string methodName, bool background = false) {
        var method = type?.GetMethod(methodName, AnyInstance, null, Type.EmptyTypes, null);
        if (method == null || method.ReturnType != typeof(void)) { coverage.Add("unavailable: " + type?.FullName + "." + methodName); return; }
        typeof(FrameTraceCapture).GetMethod(nameof(WatchInstance), AnyInstance).MakeGenericMethod(type)
            .Invoke(this, new object[] { method, type.Name + "." + methodName, background });
    }
    // The observer surrounds the original Monitor.Enter instruction. Its stack
    // operands and lockTaken reference remain untouched, and exceptions still
    // flow through the original lock's try/finally. This records wait only;
    // method timing separately includes work after acquiring the lock.
    private void WatchLocks(MethodInfo method, string label) {
        var wait = AddMetric("lock-wait:" + label);
        var found = 0;
        hooks.Add(new ILHook(method, il => {
            var stamp = new VariableDefinition(il.Import(typeof(long))); il.Body.Variables.Add(stamp);
            var cursor = new ILCursor(il);
            while (cursor.TryGotoNext(MoveType.Before, i => i.OpCode == OpCodes.Call &&
                i.Operand is MethodReference m && m.DeclaringType.FullName == typeof(Monitor).FullName && m.Name == "Enter")) {
                cursor.EmitDelegate<Func<long>>(() => measuring && Thread.CurrentThread.ManagedThreadId == mainThread ? Stopwatch.GetTimestamp() : 0);
                cursor.Emit(OpCodes.Stloc, stamp);
                cursor.Index++; // The original Monitor.Enter consumes its original operands.
                cursor.Emit(OpCodes.Ldloc, stamp);
                cursor.EmitDelegate<Action<long>>(start => { if (start != 0) Record(wait, Stopwatch.GetTimestamp() - start); });
                found++;
            }
        }));
        coverage.Add("locks: " + label + " sites=" + found);
    }
    public void ObserveValidationLock(MethodInfo method) {
        if (State != "idle" || string.IsNullOrEmpty(Environment.GetEnvironmentVariable("HOLLOWCONNECT_SMOKE_ROOT")))
            throw new InvalidOperationException("Validation fixtures require an isolated Smoke process before capture.");
        fixture = true; WatchLocks(method, "validation-fixture");
    }
    private static Assembly AssemblyNamed(string name) => AppDomain.CurrentDomain.GetAssemblies().FirstOrDefault(a => a.GetName().Name == name);
    private static object Field(object value, string name) {
        for (var t = value?.GetType(); t != null; t = t.BaseType) {
            var field = t.GetField(name, AnyInstance | BindingFlags.DeclaredOnly);
            if (field != null) return field.GetValue(value);
        }
        return null;
    }
    private void Install() {
        if (installed) return;
        if (installFailed) throw new InvalidOperationException("Observer installation failed earlier; restart the game before retrying.");
        installFailed = true; // A partial installation must never be installed twice.
        var hkmp = AssemblyNamed("HKMP"); var coop = AssemblyNamed("HollowConnect");
        if (hkmp == null || coop == null) throw new InvalidOperationException("HollowConnect/HKMP is not loaded.");
        foreach (var item in new[] {
            ("Hkmp.Game.Client.Entity.Component.PresentationComponent", "Tick"),
            ("Hkmp.Game.Client.Entity.Component.PresentationComponent", "Build"),
            ("Hkmp.Game.Client.Entity.Component.CombatStateComponent", "Tick"),
            ("Hkmp.Game.Client.Entity.Component.CombatStateComponent", "Build"),
            ("Hkmp.Game.Client.Entity.Entity", "OnUpdate"),
            ("Hkmp.Game.Client.Entity.EntityManager", "RetryPending"),
            ("Hkmp.Game.Client.Entity.BossRoomSynchronizer", "Tick"),
            ("Hkmp.Util.MonoBehaviourUtil", "Update"), ("Hkmp.Util.ThreadUtil", "Update")
        }) Watch(hkmp.GetType(item.Item1), item.Item2);
        foreach (var item in new[] { ("HollowConnect.CoopRuntime", "Update"), ("HollowConnect.CoopServerAddon", "Tick"), ("HollowConnect.MapService", "Update") })
            Watch(coop.GetType(item.Item1), item.Item2);
        var steam = AssemblyNamed("HollowConnect.Steam");
        if (steam != null) Watch(steam.GetType("HollowConnect.Steam.SteamRoomRuntime"), "Update");
        Watch(typeof(HeroController), "Update");
        var clientUpdates = hkmp.GetType("Hkmp.Networking.Client.ClientUpdateManager");
        foreach (var name in new[] { "UpdatePlayerPosition", "UpdatePlayerScale", "UpdatePlayerAnimation", "UpdateEntityPosition", "UpdateEntityScale", "AddEntityData", "AddEntityHostFsmData" }) {
            var method = clientUpdates?.GetMethod(name, AnyInstance);
            if (method != null) WatchLocks(method, "ClientUpdateManager." + name);
        }
        var udp = clientUpdates?.BaseType;
        var atomic = udp?.GetMethod("RunAtomicUpdate", AnyInstance);
        if (atomic != null) WatchLocks(atomic, "UdpUpdateManager.RunAtomicUpdate");
        if (udp != null) Watch(udp, "CreateAndSendUpdatePacket", true);
        actions = hkmp.GetType("Hkmp.Util.ThreadUtil")?.GetField("ActionsToRun", AnyStatic)?.GetValue(null) as ICollection;
        foreach (var name in new[] { "HKMP", "HollowConnect", "HollowConnect.Core", "HollowConnect.Steam", "Assembly-CSharp", "UnityEngine.CoreModule", "HollowConnect.FrameTrace" }) {
            var assembly = AssemblyNamed(name);
            if (assembly != null) binaries.Add((name + ".dll", assembly.GetName().Version.ToString(), assembly.Location));
        }
        installed = true; installFailed = false;
    }
    public void BeginCapture() {
        if (measuring || State == "warming" || exporting) return;
        try {
            Install(); RefreshNetwork();
            frames = new Frame[Capacity];
            foreach (var metric in metrics) { metric.Samples = new long[Capacity]; metric.Counts = new int[Capacity]; }
            count = markCount = 0; scenes.Clear(); sceneIds.Clear(); LastError = null; writerError = null; writerDone = false;
            machine = new {
                schema = 1, observer = "HollowConnect.FrameTrace diagnostic-1.0.0", utc = DateTime.UtcNow, fixture,
                cpu = SystemInfo.processorType, gpu = SystemInfo.graphicsDeviceName, os = SystemInfo.operatingSystem,
                unity = Application.unityVersion, width = Screen.width, height = Screen.height,
                targetFrameRate = Application.targetFrameRate, vSync = QualitySettings.vSyncCount,
                room = ReadRoom(),
                limitations = "Inclusive method costs overlap. Background send costs are not main-thread CPU. Lock measurements include observer/timer overhead. Unity delta and current hook costs have different frame boundaries. No allocation byte counter."
            };
            State = "warming"; warmUntil = Time.realtimeSinceStartup + 2;
            Notice("Frame trace warming up (2s). F2: cancel / F3: mark");
        } catch (Exception e) { Fail(e); }
    }
    public void MarkStutter() { if (measuring) { markCount++; Notice("Stutter marked. F2: save / F3: mark"); } }
    private void Notice(string message) { notice = message; noticeUntil = Time.realtimeSinceStartup + 12; }
    private void RefreshNetwork() {
        var mod = Modding.ModHooks.GetMod("HKMP");
        var engine = Field(mod, "_gameManager");
        var manager = Field(engine, "ClientManager");
        netClient = Field(manager, "_netClient");
        connectedProperty = netClient?.GetType().GetProperty("IsConnected", AnyInstance);
        updateManager = netClient?.GetType().GetProperty("UpdateManager", AnyInstance)?.GetValue(netClient, null);
        sentCountProperty = updateManager?.GetType().GetProperty("PendingSentCount", AnyInstance);
        var entities = Field(manager, "_entityManager"); entityTable = Field(entities, "_entities");
        entityCountProperty = entityTable?.GetType().GetProperty("Count");
    }
    private object ReadRoom() {
        var coop = AssemblyNamed("HollowConnect")?.GetType("HollowConnect.CoopMod");
        var settings = coop?.GetField("Settings", AnyStatic)?.GetValue(null);
        var engine = Field(Modding.ModHooks.GetMod("HKMP"), "_gameManager");
        var server = Field(Field(engine, "ServerManager"), "_netServer");
        return new {
            version = coop?.GetField("Version", AnyStatic)?.GetRawConstantValue(),
            shareProgress = Field(settings, "ShareProgress"),
            isHost = server?.GetType().GetProperty("IsStarted", AnyInstance)?.GetValue(server, null),
            connected = connectedProperty?.GetValue(netClient, null)
        };
    }
    private void LateUpdate() {
        var observerStart = Stopwatch.GetTimestamp();
        try {
            if (writerDone) {
                writerDone = false; exporting = false;
                if (writerError != null) { LastError = writerError; State = "error"; Notice("Frame trace export failed. See ModLog."); UnityEngine.Debug.LogError("Frame trace export: " + writerError); }
                else { State = "saved"; Notice("Saved to HollowConnect-FrameTrace. F2: new capture"); UnityEngine.Debug.Log("Frame trace saved: " + LastOutputDirectory); }
            }
            if (Input.GetKeyDown(KeyCode.F2)) {
                if (measuring || State == "warming") EndCapture("manual"); else BeginCapture();
            }
            if (Input.GetKeyDown(KeyCode.F3)) MarkStutter();
            if (State == "warming" && Time.realtimeSinceStartup >= warmUntil) {
                foreach (var metric in metrics) { Interlocked.Exchange(ref metric.Ticks, 0); Interlocked.Exchange(ref metric.Calls, 0); }
                previousLate = Stopwatch.GetTimestamp(); previousObserverTicks = 0; previousGc = GC.CollectionCount(0);
                started = Time.realtimeSinceStartup; nextMetadata = 0; measuring = true; State = "recording";
                Notice("Recording (up to 120s). F2: save / F3: mark stutter");
                return;
            }
            if (!measuring) return;
            var stamp = Stopwatch.GetTimestamp(); var now = Time.realtimeSinceStartup;
            if (now >= nextMetadata) {
                nextMetadata = now + 1;
                if (netClient == null) RefreshNetwork();
                lastQueue = actions?.Count ?? -1; // Approximate queue length; do not enumerate a live queue.
                lastSent = sentCountProperty == null ? -1 : (int)sentCountProperty.GetValue(updateManager, null);
                lastEntities = entityCountProperty == null ? -1 : (int)entityCountProperty.GetValue(entityTable, null);
                lastConnected = connectedProperty != null && (bool)connectedProperty.GetValue(netClient, null);
            }
            var scene = activeScene;
            if (!sceneIds.TryGetValue(scene, out var sceneId)) { sceneId = (ushort)scenes.Count; sceneIds[scene] = sceneId; scenes.Add(scene); }
            byte flags = 0; var hero = HeroController.SilentInstance;
            if (hero != null) { if (!hero.cState.onGround) flags |= 1; if (hero.cState.dashing) flags |= 2; }
            if (markCount > 0) { flags |= 4; markCount = 0; }
            if (GameManager.instance != null && GameManager.instance.IsInSceneTransition) flags |= 8;
            if (lastConnected) flags |= 16;
            var gc = GC.CollectionCount(0);
            frames[count] = new Frame { UnityFrame = Time.frameCount, Gc = gc - previousGc, Queue = lastQueue, Pending = lastSent, Entities = lastEntities,
                Scene = sceneId, Flags = flags, UnityMs = Time.unscaledDeltaTime * 1000, Stamp = stamp, WallTicks = stamp - previousLate, ObserverTicks = previousObserverTicks };
            foreach (var metric in metrics) { metric.Samples[count] = Interlocked.Exchange(ref metric.Ticks, 0); metric.Counts[count] = Interlocked.Exchange(ref metric.Calls, 0); }
            previousGc = gc; previousLate = stamp; count++;
            if (count == Capacity || now - started >= Duration) EndCapture(count == Capacity ? "capacity" : "duration");
        } catch (Exception e) { Fail(e); }
        finally { previousObserverTicks = Stopwatch.GetTimestamp() - observerStart; }
    }
    public void EndCapture(string reason = "manual") {
        if (!measuring && State != "warming") return;
        measuring = false;
        if (count == 0) { State = "idle"; Notice("Frame trace cancelled."); return; }
        State = "exporting"; exporting = true;
        var snapshotCount = count;
        var folder = "capture-" + DateTime.UtcNow.ToString("yyyyMMdd-HHmmss-fff", Invariant) + "-" + Process.GetCurrentProcess().Id;
        LastOutputDirectory = Path.Combine(outputRoot, folder);
        writer = new Thread(() => {
            try { Export(snapshotCount, reason, LastOutputDirectory); }
            catch (Exception e) { writerError = e.ToString(); }
            finally { writerDone = true; }
        }) { IsBackground = true, Name = "HollowConnect frame trace export" };
        writer.Start(); Notice("Saving frame trace in background...");
    }
    private static double Ms(long ticks) => ticks * 1000.0 / Stopwatch.Frequency;
    private void Export(int length, string reason, string path) {
        Directory.CreateDirectory(path);
        var hashes = new List<object>();
        foreach (var binary in binaries) {
            string hash; using (var stream = File.OpenRead(binary.Path)) using (var sha = SHA256.Create()) hash = BitConverter.ToString(sha.ComputeHash(stream)).Replace("-", "");
            hashes.Add(new { name = binary.Name, version = binary.Version, sha256 = hash });
        }
        var methodNames = metrics.Select(m => new { name = m.Name, background = m.Background }).ToArray();
        File.WriteAllText(Path.Combine(path, "manifest.json"), JsonConvert.SerializeObject(new { machine, reason, frames = length, scenes, methods = methodNames, coverage, binaries = hashes }, Formatting.Indented));
        using (var stream = File.Create(Path.Combine(path, "frames.csv.gz")))
        using (var zip = new GZipStream(stream, CompressionLevel.Fastest))
        using (var output = new StreamWriter(zip)) {
            output.Write("frame,elapsed_ms,unity_previous_ms,late_interval_ms,gc0,scene,flags,main_queue_approx,pending_sent,entities,previous_observer_ms");
            foreach (var metric in metrics) output.Write("," + metric.Name + "_ms," + metric.Name + "_calls");
            output.WriteLine();
            for (var i = 0; i < length; i++) {
                var f = frames[i];
                output.Write(f.UnityFrame.ToString(Invariant) + "," + Ms(f.Stamp - frames[0].Stamp).ToString("F3", Invariant) + "," + f.UnityMs.ToString("F3", Invariant) + "," + Ms(f.WallTicks).ToString("F3", Invariant) + "," + f.Gc + "," + f.Scene + "," + f.Flags + "," + f.Queue + "," + f.Pending + "," + f.Entities + "," + Ms(f.ObserverTicks).ToString("F4", Invariant));
                foreach (var metric in metrics) output.Write("," + Ms(metric.Samples[i]).ToString("F4", Invariant) + "," + metric.Counts[i].ToString(Invariant));
                output.WriteLine();
            }
        }
        var values = frames.Take(length).Select(f => f.UnityMs).OrderBy(v => v).ToArray();
        var summary = new { frames = length, p50Ms = values[(int)((length - 1) * .5)], p99Ms = values[(int)((length - 1) * .99)], maxMs = values.Last(),
            over33ms = values.Count(v => v > 33.333), gc0 = frames.Take(length).Sum(f => f.Gc), markedFrames = frames.Take(length).Count(f => (f.Flags & 4) != 0),
            observerMeanMs = frames.Take(length).Average(f => Ms(f.ObserverTicks)), observerMaxMs = frames.Take(length).Max(f => Ms(f.ObserverTicks)),
            methods = metrics.Select(m => new { name = m.Name, background = m.Background, calls = m.Counts.Take(length).Sum(), totalMs = Ms(m.Samples.Take(length).Sum()), maxFrameMs = Ms(m.Samples.Take(length).Max()) }) };
        File.WriteAllText(Path.Combine(path, "summary.json"), JsonConvert.SerializeObject(summary, Formatting.Indented));
        File.WriteAllText(Path.Combine(path, "complete.txt"), "Complete capture. Do not sum nested method costs. flags: 1=airborne 2=dashing 4=user mark 8=transition 16=connected.\n");
    }
    private void OnGUI() {
        if (State == "idle" || (!measuring && State != "warming" && State != "exporting" && Time.realtimeSinceStartup > noticeUntil)) return;
        GUI.Label(new Rect(12, Screen.height - 42, Screen.width - 24, 32), notice);
    }
    private void Fail(Exception e) { measuring = false; State = "error"; LastError = e.ToString(); Notice("Frame trace failed. See ModLog."); UnityEngine.Debug.LogError("Frame trace: " + e); }
    private void OnApplicationQuit() { if (measuring) EndCapture("quit"); if (writer?.IsAlive == true) writer.Join(5000); }
    private void OnDestroy() {
        measuring = false;
        UnitySceneManager.activeSceneChanged -= SceneChanged;
        foreach (var hook in hooks.AsEnumerable().Reverse()) hook.Dispose();
        if (Instance == this) Instance = null;
    }
}
