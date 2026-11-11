using System;
using System.Collections;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Threading;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace HollowConnect.Smoke;

public sealed partial class CoverageProbe {
    private Type traceType;
    private object trace;
    private static readonly object traceFixtureGate = new();
    private static int traceFixtureCalls;
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void TraceLockFixture(bool fail) {
        lock (traceFixtureGate) {
            traceFixtureCalls++;
            if (fail) throw new InvalidOperationException("Expected fixture throw");
        }
    }
    private string TraceState => (string)traceType.GetProperty("State").GetValue(trace, null);
    private void TraceCall(string method, params object[] args) => traceType.GetMethod(method).Invoke(trace, args);
    private IEnumerator StartFrameTraceValidation() {
        traceType = AppDomain.CurrentDomain.GetAssemblies().Single(a => a.GetName().Name == "HollowConnect.FrameTrace")
            .GetType("HollowConnect.FrameTrace.FrameTraceCapture", true);
        trace = traceType.GetProperty("Instance").GetValue(null, null);
        if (trace == null) throw new Exception("Standalone frame trace did not initialize");
        TraceCall("ObserveValidationLock", typeof(CoverageProbe).GetMethod(nameof(TraceLockFixture), BindingFlags.NonPublic | BindingFlags.Static));
        // Cancel warmup, then start again: neither path should duplicate hooks.
        TraceCall("BeginCapture");
        if (TraceState != "warming") throw new Exception("Frame trace warmup failed: " + TraceState);
        TraceCall("EndCapture", "validation-cancel");
        if (TraceState != "idle") throw new Exception("Warmup cancellation failed");
        TraceCall("BeginCapture");
        yield return Until(() => TraceState == "recording" || TraceState == "error", "standalone trace recording");
        if (TraceState != "recording") throw new Exception("Frame trace failed: " + traceType.GetProperty("LastError").GetValue(trace, null));
        using (var held = new ManualResetEvent(false)) {
            var worker = new Thread(() => { lock (traceFixtureGate) { held.Set(); Thread.Sleep(60); } });
            worker.Start();
            if (!held.WaitOne(2000)) throw new Exception("Lock fixture worker did not enter");
            try { TraceLockFixture(true); throw new Exception("Fixture did not preserve original throw"); }
            catch (InvalidOperationException e) { if (e.Message != "Expected fixture throw") throw; }
            if (!worker.Join(2000)) throw new Exception("Lock fixture worker did not stop");
        }
        // Another thread must reacquire after the original finally, and the
        // main thread must still call the original body exactly once per call.
        var reacquired = false;
        var next = new Thread(() => { if (Monitor.TryEnter(traceFixtureGate, 1000)) { try { reacquired = true; } finally { Monitor.Exit(traceFixtureGate); } } });
        next.Start();
        if (!next.Join(2000) || !reacquired) throw new Exception("Instrumented lock leaked after an exception");
        TraceLockFixture(false);
        if (traceFixtureCalls != 2) throw new Exception("Instrumented original call count=" + traceFixtureCalls);
        TraceCall("MarkStutter");
        Mark("TRACE fixture preserved throw/finally/original calls; injected 60ms test lock contention");
    }
    private IEnumerator FinishFrameTraceValidation() {
        TraceCall("MarkStutter"); yield return null;
        TraceCall("EndCapture", "smoke-validation");
        yield return Until(() => TraceState == "saved" || TraceState == "error", "standalone trace export");
        if (TraceState != "saved") throw new Exception("Standalone trace export failed: " + traceType.GetProperty("LastError").GetValue(trace, null));
        var output = (string)traceType.GetProperty("LastOutputDirectory").GetValue(trace, null);
        if (!File.Exists(Path.Combine(output, "complete.txt"))) throw new Exception("Trace completion marker missing");
        var manifest = JObject.Parse(File.ReadAllText(Path.Combine(output, "manifest.json")));
        var summary = JObject.Parse(File.ReadAllText(Path.Combine(output, "summary.json")));
        var methods = (JArray)summary["methods"];
        var fixture = methods.Single(m => (string)m["name"] == "lock-wait:validation-fixture");
        if ((double)fixture["maxFrameMs"] < 20 || (int)fixture["calls"] != 2) throw new Exception("Trace did not measure contended lock: " + fixture);
        foreach (var name in new[] { "HeroController.Update", "Entity.OnUpdate", "MonoBehaviourUtil.Update", "ClientUpdateManager.CreateAndSendUpdatePacket" }) {
            // The closed base type is named UdpUpdateManager`2 at runtime.
            var metric = methods.FirstOrDefault(m => ((string)m["name"]).EndsWith(name.Substring(name.IndexOf('.')), StringComparison.Ordinal) &&
                (name.EndsWith("CreateAndSendUpdatePacket") || ((string)m["name"]).StartsWith(name.Split('.')[0] + ".", StringComparison.Ordinal)));
            if (metric == null || (int)metric["calls"] == 0) throw new Exception("Missing real trace method samples: " + name);
        }
        if ((int)summary["frames"] < 100 || (int)summary["markedFrames"] < 1 || (bool?)manifest["machine"]?["room"]?["shareProgress"] != true ||
            (bool?)manifest["machine"]?["room"]?["isHost"] != host) throw new Exception("Trace metadata/frame/marker validation failed");
        foreach (var binary in (JArray)manifest["binaries"]) {
            var assembly = AppDomain.CurrentDomain.GetAssemblies().Single(a => a.GetName().Name + ".dll" == (string)binary["name"]);
            string hash; using (var input = File.OpenRead(assembly.Location)) using (var sha = SHA256.Create()) hash = BitConverter.ToString(sha.ComputeHash(input)).Replace("-", "");
            if (hash != (string)binary["sha256"]) throw new Exception("Trace binary fingerprint mismatch " + binary["name"]);
        }
        var connected = false; var entities = false; var validQueues = false; var dash = false; var airborne = false;
        using (var file = File.OpenRead(Path.Combine(output, "frames.csv.gz")))
        using (var zip = new GZipStream(file, CompressionMode.Decompress))
        using (var reader = new StreamReader(zip)) {
            var header = reader.ReadLine().Split(',');
            int Index(string key) => Array.IndexOf(header, key);
            string line;
            while ((line = reader.ReadLine()) != null) {
                var row = line.Split(','); var flags = int.Parse(row[Index("flags")]);
                connected |= (flags & 16) != 0; dash |= (flags & 2) != 0; airborne |= (flags & 1) != 0;
                entities |= int.Parse(row[Index("entities")]) > 0;
                validQueues |= int.Parse(row[Index("main_queue_approx")]) >= 0 && int.Parse(row[Index("pending_sent")]) >= 0;
            }
        }
        if (!connected || !entities || !validQueues || !dash || !airborne) throw new Exception("Missing real trace telemetry connected/entities/queues/dash/air=" + connected + "/" + entities + "/" + validQueues + "/" + dash + "/" + airborne);
        var destination = Path.Combine(root, role + ".frame-trace"); Directory.CreateDirectory(destination);
        foreach (var file in Directory.GetFiles(output)) File.Copy(file, Path.Combine(destination, Path.GetFileName(file)));
        Mark("TRACE validated " + summary["frames"] + " frames; lock wait=" + fixture["maxFrameMs"] + "ms; observer mean=" + summary["observerMeanMs"] + "ms; output=" + destination);
    }
}
