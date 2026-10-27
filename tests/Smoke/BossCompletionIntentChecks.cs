using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using Hkmp;
using Hkmp.Game.Client.Entity;
using Hkmp.Game.Client.Entity.Component;
using Hkmp.Networking.Client;
using Hkmp.Networking.Packet.Data;
using MonoMod.RuntimeDetour;
using Newtonsoft.Json;
using UnityEngine;
using Object = UnityEngine.Object;

namespace HollowConnect.Smoke;

public sealed partial class RescueProbe {
    private const BindingFlags IntentHidden = BindingFlags.Instance | BindingFlags.NonPublic;
    private static void IntentSet(object owner, string name, object value) => owner.GetType().GetField(name, IntentHidden).SetValue(owner, value);
    private static void IntentCall(object owner, string name, params object[] args) {
        try { owner.GetType().GetMethod(name, IntentHidden).Invoke(owner, args); }
        catch (TargetInvocationException e) { throw e.InnerException; }
    }

    // Executes the delivered production state machine on real Unity clients.
    // A separate, unregistered synchronizer isolates each case. Outgoing packets
    // and native End calls are intercepted, so these are boundary checks, not
    // evidence that an actual fight/sequence/transport completion passed.
    private IEnumerator BossCompletionIntentChecks() {
        using var noContact = IgnoreExternalCombatDamage();
        BossSequenceController.Reset(); PlayerData.instance.SetInt("bossStatueTargetLevel", 0);
        PlayerData.instance.SetString("dreamReturnScene", "Town"); PlayerData.instance.SetString("dreamReturnGate", "left1");
        if (!host) yield return Until(() => Has("a3.first"), "authority enters native arena first");
        yield return RuleEnter("GG_Gruz_Mother", "door_dreamEnter");
        var manager = Field<EntityManager>(engine.ClientManager, "_entityManager");
        yield return Until(() => manager.IsSceneHostDetermined && manager.IsSceneHost == host &&
            Object.FindObjectsOfType<BossSceneController>(true).Any(c => c.gameObject.scene.name == CoopContext.LocalScene),
            "A3 native controller and real authority preflight");
        var controller = Object.FindObjectsOfType<BossSceneController>(true).First(c => c.gameObject.scene.name == CoopContext.LocalScene);
        HeroController.instance.takeNoDamage = true;
        if (host) Flag("a3.first");
        Flag(role + ".a3.arena");
        yield return Until(() => Has("host.a3.arena") && Has("guest.a3.arena"), "both clients own native controller");
        Check(runtime.Shared, "shared progress ON negotiated for A3 probe");
        var client = Field<NetClient>(manager, "_netClient");
        var savedCapture = CoopContext.CaptureBossContext; var savedReady = CoopContext.BossTransitionReady;
        var savedApply = CoopContext.ApplyBossContext; var savedCommit = CoopContext.CommitBossContext;
        var savedRestore = CoopContext.RestoreBossContext; var savedDefeat = CoopContext.BossDefeated;
        var savedAwait = CoopContext.AwaitBossSettlement;
        var savedLocalWorld = CoopContext.LocalWorldOnly;
        var savedVisit = CoopContext.LocalVisit; var savedResetting = CoopContext.Resetting;
        var nativeEnded = Field<bool>(controller, "endedScene");
        var savedIndex = BossSequenceController.BossIndex;
        var rows = new List<object>();
        BossRoomSynchronizer probe = null;
        var sent = new List<string>(); var errors = new List<string>();
        string[] expected = Array.Empty<string>();
        var nativeCalls = 0; var commits = 0; var awaits = 0;
        bool ready = false, sequence = false, sendThrows = false;
        string capture = null;
        using var sendHook = new Hook(typeof(BossRoomSynchronizer).GetMethod("Send", IntentHidden),
            new Action<Action<BossRoomSynchronizer, EntityComponentType, string>, BossRoomSynchronizer, EntityComponentType, string>((orig, owner, type, value) => {
                if (owner != probe) { orig(owner, type, value); return; }
                if (sendThrows) throw new InvalidOperationException("a3-injected-send");
                if (type == EntityComponentType.BossSceneComplete) sent.Add(value);
            }));
        using var endHook = new Hook(typeof(BossSceneController).GetMethod("EndBossScene"),
            new Action<Action<BossSceneController>, BossSceneController>((orig, self) => {
                if (probe != null && self == controller) { nativeCalls++; return; }
                orig(self);
            }));
        using var sequenceHook = new Hook(typeof(BossSequenceController).GetProperty("IsInSequence").GetGetMethod(),
            new Func<Func<bool>, bool>(orig => probe != null ? sequence : orig()));
        using var logHook = new Hook(typeof(Hkmp.Logging.Logger).GetMethod("Error"), new Action<Action<string>, string>((orig, message) => {
            if (probe != null && expected.Any(p => message.StartsWith(p, StringComparison.Ordinal))) { errors.Add(message); return; }
            orig(message); // Unexpected errors retain the normal strict failure gate.
        }));
        void Fresh() {
            probe = new BossRoomSynchronizer(client, manager);
            IntentSet(probe, "visit", CoopContext.LocalVisit); IntentSet(probe, "epoch", CoopContext.Epoch);
            IntentSet(probe, "next", float.MaxValue); IntentSet(probe, "defeatReported", true);
            nativeCalls = commits = awaits = 0; sent.Clear(); errors.Clear();
            ready = sequence = sendThrows = false; capture = null; expected = Array.Empty<string>();
            CoopContext.LocalWorldOnly = false;
            CoopContext.CaptureBossContext = () => capture;
            CoopContext.BossTransitionReady = () => ready;
            CoopContext.ApplyBossContext = _ => true;
            CoopContext.CommitBossContext = () => commits++;
            CoopContext.RestoreBossContext = () => { };
            CoopContext.BossDefeated = _ => { };
            CoopContext.AwaitBossSettlement = () => awaits++;
        }
        void End() => IntentCall(probe, "End", new On.BossSceneController.orig_EndBossScene(_ => nativeCalls++), controller);
        void Tick() => IntentCall(probe, "Tick");
        void Retry() { IntentSet(probe, "nextContextAttempt", 0f); Tick(); }
        void Receive() {
            var data = new EntityNetworkData { Type = EntityComponentType.BossSceneComplete };
            data.Packet.Write("boundary-context");
            data.Packet = new Hkmp.Networking.Packet.Packet(data.Packet.ToArray());
            probe.Receive(data);
        }
        void Row(string id, bool ok, string detail) {
            rows.Add(new { id, passed = ok, detail, nativeCalls, commits, awaits, sends = sent.ToArray(),
                pending = Field<string>(probe, "pending"), markedSent = Field<bool>(probe, "sent"),
                entityPending = Field<bool>(probe, "entityCompletionPending"),
                completionApplied = Field<bool>(probe, "completionApplied"), diagnostics = errors.ToArray() });
            Log("OBS " + id + " met=" + ok + " " + detail);
        }
        try {
            if (host) {
                Fresh(); End(); capture = "boundary-context"; Retry();
                var waiting = nativeCalls == 0 && sent.Count == 1;
                ready = true; Tick(); Tick();
                Row("H1-null-then-context-before-ready", waiting && nativeCalls == 1 && commits == 1 && sent.Count == 1,
                    "missing capture retained; context sent during wait; one end after ready");

                Fresh(); ready = true; expected = new[] { "Boss room completion has no context scene=" }; End();
                Row("H2-immediate-null-policy", nativeCalls == 1 && errors.Count == 1 && sent.Count == 0,
                    "chosen immediate-null policy emits a diagnostic and ends locally");

                Fresh(); End(); expected = new[] { "Boss room completion context unavailable scene=" };
                IntentSet(probe, "contextDeadline", Time.unscaledTime - 1); Retry(); Retry();
                ready = true; Tick();
                Row("H3-always-null-deadline", nativeCalls == 1 && errors.Count == 1 && sent.Count == 0,
                    "expired deadline injected; one explicit diagnostic; local end only after ready");

                Fresh(); End(); ready = true; Retry();
                var endedBeforeContext = nativeCalls; capture = "boundary-later-context"; Retry();
                Row("H4-ready-before-context", endedBeforeContext == 0 && sent.Count == 1 && nativeCalls == 1 && errors.Count == 0,
                    "already deferred intent waits for later context and sends before one end; endBeforeContext=" + endedBeforeContext);

                Fresh(); capture = "boundary-context"; End(); Tick(); Tick();
                waiting = nativeCalls == 0 && sent.Count == 1;
                ready = true; Tick(); Tick();
                Row("H5-ready-delay", waiting && nativeCalls == 1 && commits == 1,
                    "valid context sent while unready; native invocation once after readiness");

                Fresh(); capture = "boundary-context"; ready = true;
                expected = new[] { "Boss room native end failed scene=" };
                var injected = new InvalidOperationException("a3-injected-native-end"); var propagated = false;
                try { IntentCall(probe, "End", new On.BossSceneController.orig_EndBossScene(_ => throw injected), controller); }
                catch (InvalidOperationException e) { propagated = ReferenceEquals(e, injected); }
                Row("H6-orig-exception", propagated && errors.Count == 1 &&
                    errors[0].Contains("scene=" + CoopContext.LocalScene) && errors[0].Contains("visit=" + CoopContext.LocalVisit) &&
                    errors[0].Contains("epoch=" + CoopContext.Epoch) && !Field<bool>(probe, "completionApplied"),
                    "original exception rethrown with scene/visit/epoch diagnostic");

                Fresh(); capture = "boundary-context"; sendThrows = true;
                expected = new[] { "Boss room send failed type=" }; End(); sendThrows = false; Retry(); ready = true; Tick();
                Row("H7-transient-send", errors.Count == 1 && sent.Count == 1 && nativeCalls == 1,
                    "send exception isolated; retry succeeds while readiness remains false");

                Fresh(); End(); var deadline = Time.unscaledTime + 3;
                IntentSet(probe, "contextDeadline", deadline); End(); ready = true; End();
                waiting = Field<float>(probe, "contextDeadline") == deadline && nativeCalls == 0;
                capture = "boundary-context"; Retry();
                Row("H8-repeated-end", waiting && nativeCalls == 1 && sent.Count == 1,
                    "duplicate End preserves first deadline and cannot bypass queued context wait");

                Fresh(); End(); ready = true; Retry(); waiting = nativeCalls == 0;
                expected = new[] { "Boss room completion context unavailable scene=" };
                IntentSet(probe, "contextDeadline", Time.unscaledTime - 1); Retry(); Tick();
                Row("H9-ready-before-null-timeout", waiting && nativeCalls == 1 && errors.Count == 1 && !Field<bool>(probe, "sent"),
                    "ready before deadline still waits; terminal timeout is explicit and not marked sent");

                Fresh(); capture = "boundary-context"; ready = true; sendThrows = true;
                expected = new[] { "Boss room send failed type=" }; End(); waiting = nativeCalls == 0;
                sendThrows = false; Retry(); Tick();
                Row("H10-send-fails-while-ready", waiting && nativeCalls == 1 && sent.Count == 1 && errors.Count == 1,
                    "readiness cannot discard a failed enqueue; later send succeeds before end");

                Fresh(); capture = "boundary-context"; ready = true; sendThrows = true;
                expected = new[] { "Boss room send failed type=", "Boss room completion context could not be sent scene=" };
                End(); IntentSet(probe, "contextDeadline", Time.unscaledTime - 1); Retry();
                var diagnosticCount = errors.Count; Tick(); End();
                Row("H11-send-terminal-failure", nativeCalls == 1 && sent.Count == 0 && errors.Count == diagnosticCount &&
                    errors.Count(e => e.StartsWith("Boss room completion context could not be sent scene=")) == 1 && !Field<bool>(probe, "sent"),
                    "permanent enqueue failure retires explicitly at deadline; no fake sent flag or repeated completion");
            } else {
                Fresh(); ready = true; probe.ApplyEntityCompletion(); probe.ApplyEntityCompletion();
                Row("G1-entity-ordinary", nativeCalls == 1 && Field<bool>(probe, "completionApplied"),
                    "ordinary replica fallback calls native boundary once");

                Fresh(); ready = false; probe.ApplyEntityCompletion();
                var queued = nativeCalls == 0 && awaits == 1 && Field<bool>(probe, "entityCompletionPending");
                probe.ApplyEntityCompletion(); ready = true; Tick(); Tick();
                Row("G2-entity-unready", queued && awaits == 1 && nativeCalls == 1 && !Field<bool>(probe, "entityCompletionPending"),
                    "single entity intent waits for readiness; duplicate does not re-await; Tick completes once");

                Fresh(); ready = true; sequence = true; probe.ApplyEntityCompletion();
                Row("G3-entity-sequence-flag", nativeCalls == 0 && BossSequenceController.BossIndex == savedIndex,
                    "injected sequence flag defers entity fallback and preserves index; not a native Pantheon run");

                Fresh(); ready = true; Receive(); probe.ApplyEntityCompletion();
                Row("G4-entity-with-room-pending", nativeCalls == 0 && awaits == 1 && Field<string>(probe, "pending") != null,
                    "room pending blocks entity fallback and requests settlement wait");

                Fresh(); ready = true; Receive(); CoopContext.ApplyBossContext = _ => false;
                expected = new[] { "Boss room completion context could not be applied scene=" };
                IntentSet(probe, "contextDeadline", Time.unscaledTime - 1); Tick(); Tick();
                var waiting = nativeCalls == 0 && errors.Count == 1;
                CoopContext.ApplyBossContext = _ => true; Tick(); Tick();
                Row("G5-apply-fails-then-recovers", waiting && nativeCalls == 1 && commits == 1,
                    "apply failure diagnostic once after injected deadline; retry remains possible");

                Fresh(); probe.ApplyEntityCompletion(); probe.Clear(); ready = true; Tick();
                Row("G6-entity-clear", nativeCalls == 0 && !Field<bool>(probe, "entityCompletionPending"),
                    "Clear retires queued entity intent before readiness recovers");

                Fresh(); probe.ApplyEntityCompletion(); Receive(); ready = true; Tick(); Tick();
                Row("G7-room-takes-priority", nativeCalls == 1 && commits == 1 && !Field<bool>(probe, "entityCompletionPending"),
                    "later room completion supersedes entity intent without duplicate native invocation");

                Fresh(); probe.ApplyEntityCompletion(); sequence = true; ready = true; Tick(); sequence = false; Tick();
                Row("G8-sequence-cancels-entity", nativeCalls == 0 && !Field<bool>(probe, "entityCompletionPending"),
                    "entering a sequence cancels queued entity fallback; no replay after sequence flag clears");

                Fresh(); probe.ApplyEntityCompletion(); CoopContext.LocalVisit++;
                try { ready = true; Tick(); }
                finally { CoopContext.LocalVisit = savedVisit; }
                Row("G9-new-visit-cancels-entity", nativeCalls == 0 && !Field<bool>(probe, "entityCompletionPending"),
                    "visit change invalidates queued entity fallback before native completion");

                Fresh(); probe.ApplyEntityCompletion(); CoopContext.Resetting = true;
                Tick(); CoopContext.Resetting = false; ready = true; Tick();
                Row("G10-reset-cancels-entity", nativeCalls == 0 && !Field<bool>(probe, "entityCompletionPending"),
                    "reset cancels entity fallback instead of replaying it on readiness recovery");

                Fresh(); expected = new[] { "Boss room transition check failed scene=" };
                CoopContext.BossTransitionReady = () => throw new InvalidOperationException("a3-injected-readiness");
                probe.ApplyEntityCompletion(); queued = nativeCalls == 0 && Field<bool>(probe, "entityCompletionPending");
                CoopContext.BossTransitionReady = () => true; Tick();
                Row("G11-readiness-exception", queued && nativeCalls == 1 && errors.Count == 1 &&
                    errors[0].Contains("epoch=" + CoopContext.Epoch),
                    "throwing readiness callback is diagnosed; queued entity intent survives and recovers");
            }
        } finally {
            probe = null;
            CoopContext.CaptureBossContext = savedCapture; CoopContext.BossTransitionReady = savedReady;
            CoopContext.ApplyBossContext = savedApply; CoopContext.CommitBossContext = savedCommit;
            CoopContext.RestoreBossContext = savedRestore; CoopContext.BossDefeated = savedDefeat;
            CoopContext.AwaitBossSettlement = savedAwait; CoopContext.LocalWorldOnly = savedLocalWorld;
            CoopContext.LocalVisit = savedVisit; CoopContext.Resetting = savedResetting;
            File.WriteAllText(Path.Combine(root, role + ".boundary.json"), JsonConvert.SerializeObject(rows, Formatting.Indented));
        }
        Check(Field<bool>(controller, "endedScene") == nativeEnded && BossSequenceController.BossIndex == savedIndex,
            "probe restored callbacks and left native completion/index unchanged");
        Flag(role + ".a3.boundaries");
        yield return Until(() => Has("host.a3.boundaries") && Has("guest.a3.boundaries"), "both boundary result files recorded");
    }
}
