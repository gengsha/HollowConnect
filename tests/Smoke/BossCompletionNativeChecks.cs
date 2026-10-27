using System;
using System.Collections;
using System.IO;
using System.Linq;
using System.Reflection;
using Hkmp;
using Hkmp.Game.Client.Entity;
using Hkmp.Game.Client.Entity.Component;
using Hkmp.Networking.Packet.Data;
using MonoMod.RuntimeDetour;
using Newtonsoft.Json;
using UnityEngine;
using Object = UnityEngine.Object;

namespace HollowConnect.Smoke;

public sealed partial class RescueProbe {
    // Retains actual transport, HealthManager death, addon settlement and native
    // EndBossScene. Only the named missing-context/entity-only boundary is held.
    private IEnumerator BossCompletionNativeChecks() {
        using var noContact = IgnoreExternalCombatDamage();
        var entityOnly = Environment.GetEnvironmentVariable("HOLLOWCONNECT_BOSS_COMPLETION_INTENT") == "native-entity";
        BossSequenceController.Reset(); PlayerData.instance.SetInt("bossStatueTargetLevel", 0);
        // Match the established Hall fixtures: Awake consumes SetupEvent and
        // runs native Setup, which subscribes to each boss's HealthManager death.
        // Direct arena entry without it has no native completion subscription.
        BossSceneController.SetupEvent = c => c.BossLevel = 0;
        PlayerData.instance.SetString("dreamReturnScene", "Town"); PlayerData.instance.SetString("bossReturnEntryGate", "left1");
        if (!host) yield return Until(() => Has("cn.first"), "authority enters native Gruz arena first");
        yield return RuleEnter("GG_Gruz_Mother", "door_dreamEnter");
        var manager = Field<EntityManager>(engine.ClientManager, "_entityManager");
        yield return Until(() => manager.IsSceneHostDetermined && manager.IsSceneHost == host &&
            EntityReferences.Entities.Values.Any(e => e.Type == EntityType.GruzMother && e.Object.Host != null),
            "native completion entity and authority preflight");
        var controller = Object.FindObjectsOfType<BossSceneController>(true).Single(c => c.gameObject.scene.name == CoopContext.LocalScene);
        var health = EntityReferences.Entities.Values.Single(e => e.Type == EntityType.GruzMother).Object.Host.GetComponent<HealthManager>();
        Log("PREFLIGHT controller=" + controller.name + " bossesLeft=" + Field<int>(controller, "bossesLeft") +
            " bosses=" + string.Join(",", controller.bosses.Select(h => h == null ? "null" : h.name + ":" + h.hp)));
        Check(controller.bosses.Contains(health) && Field<int>(controller, "bossesLeft") > 0,
            "native Awake/Setup subscribed to the living Gruz controller boss");
        var room = Field<BossRoomSynchronizer>(manager, "_bossRoom");
        var hero = HeroController.instance; hero.takeNoDamage = true; hero.AffectedByGravity(true);
        yield return Until(() => hero.cState.onGround && hero.acceptingInput && !hero.cState.transitioning &&
            hero.transitionState == GlobalEnums.HeroTransitionState.WAITING_TO_TRANSITION && GameManager.instance.HasFinishedEnteringScene,
            "native supported hero and completed intro");
        Check(runtime.Shared && !BossSequenceController.IsInSequence && !Field<bool>(controller, "endedScene"),
            "shared ON ordinary arena with uncompleted native controller");
        if (host) Flag("cn.first");
        Flag(role + ".cn.arena"); yield return Until(() => Has("host.cn.arena") && Has("guest.cn.arena"), "both native completion entries ready");
        yield return Until(() => runtime.Peers.Count == 1 && runtime.Peers.Values.Single().Life == HollowConnect.Core.Life.Alive &&
            runtime.Peers.Values.Single().State?.Scene == CoopContext.LocalScene, "fresh addon living participant in exact native arena");
        yield return new WaitForSecondsRealtime(1);
        var savedCapture = CoopContext.CaptureBossContext; var savedReady = CoopContext.BossTransitionReady;
        var initialContext = JsonConvert.DeserializeObject<HollowConnect.Message>(savedCapture());
        Check(initialContext.BossReturnScene == "Town" && initialContext.BossReturnGate == "left1",
            "native Hall capture contains a complete valid return route");
        var savedEntity = CoopContext.ApplyBossEntityComplete;
        var terminalObserved = false; var captureReleased = false; var readyAt = float.MaxValue;
        var entityObserved = false; var entityHeld = false; var releaseEntityAt = float.MaxValue;
        var droppedRoomCompletions = 0; var nativeEnds = 0; var captureNulls = 0;
        string State() => " down=" + runtime.Down + " ready=" + (savedReady?.Invoke() ?? true) +
            " peer=" + runtime.Peers.Values.Single().Life + " token=" + Field<uint>(runtime, "settlementToken") +
            " until=" + Field<double>(runtime, "settlementUntil") + " pending=" + Field<string>(room, "pending") +
            " entityPending=" + Field<bool>(room, "entityCompletionPending");
        using var receipts = new Hook(typeof(HollowConnect.CoopRuntime).GetMethod("Receive", IntentHidden),
            new Action<Action<HollowConnect.CoopRuntime, HollowConnect.Message>, HollowConnect.CoopRuntime, HollowConnect.Message>((orig, owner, message) => {
                orig(owner, message);
                if (message.Kind == HollowConnect.Kind.BossVictory || message.Kind == HollowConnect.Kind.BossRevived ||
                    message.Kind == HollowConnect.Kind.BossSettlementReady)
                    Log("RECEIPT " + message.Kind + " serial=" + message.Serial + " aux=" + message.Aux + State());
            }));
        var deadField = typeof(BossSceneController).GetField("OnBossesDead", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        Action nativeEnd = () => {
            nativeEnds++;
            Check(!runtime.Down && !runtime.Returning && (savedReady?.Invoke() ?? true),
                "actual native completion follows local recovery and settlement");
            Log("NATIVE END " + role + " count=" + nativeEnds + " entityOnly=" + entityOnly);
        };
        deadField.SetValue(controller, Delegate.Combine((Delegate)deadField.GetValue(controller), nativeEnd));
        using var terminalHook = new Hook(typeof(BossRoomSynchronizer).GetMethod("End", IntentHidden),
            new Action<Action<BossRoomSynchronizer, On.BossSceneController.orig_EndBossScene, BossSceneController>, BossRoomSynchronizer, On.BossSceneController.orig_EndBossScene, BossSceneController>(
                (orig, owner, next, self) => {
                    if (host && !entityOnly && owner == room && self == controller && !terminalObserved) {
                        terminalObserved = true; readyAt = Time.unscaledTime + .5f;
                        Log("INJECTION authority terminal context unavailable; readiness released first");
                    }
                    orig(owner, next, self);
                }));
        using var receiveHook = new Hook(typeof(BossRoomSynchronizer).GetMethod("Receive", BindingFlags.Instance | BindingFlags.NonPublic),
            new Action<Action<BossRoomSynchronizer, EntityNetworkData>, BossRoomSynchronizer, EntityNetworkData>((orig, owner, data) => {
                if (!host && entityOnly && owner == room && data.Type == EntityComponentType.BossSceneComplete) {
                    droppedRoomCompletions++; Log("INJECTION omit room completion; keep real entity completion and addon receipts"); return;
                }
                orig(owner, data);
            }));
        try {
            if (host && !entityOnly) {
                CoopContext.CaptureBossContext = () => {
                    if (!captureReleased) { captureNulls++; return null; }
                    return savedCapture?.Invoke();
                };
                CoopContext.BossTransitionReady = () => Time.unscaledTime >= readyAt && (savedReady?.Invoke() ?? true);
            }
            if (!host && entityOnly) {
                CoopContext.BossTransitionReady = () => Time.unscaledTime >= releaseEntityAt && (savedReady?.Invoke() ?? true);
                CoopContext.ApplyBossEntityComplete = () => {
                    if (!entityObserved) { entityObserved = true; releaseEntityAt = Time.unscaledTime + 2; }
                    savedEntity?.Invoke();
                    if (Time.unscaledTime < releaseEntityAt) {
                        Check(!Field<bool>(controller, "endedScene") && Field<bool>(room, "entityCompletionPending"),
                            "actual entity-only delivery retained while readiness is held");
                        entityHeld = true;
                    }
                };
            }
            if (!host && entityOnly) {
                Die(); yield return Until(() => runtime.Down, "guest becomes native cooperative shadow before boss death");
                Flag("cn.down");
            }
            if (entityOnly) {
                yield return Until(() => Has("cn.down") && (host ? runtime.Peers.Values.Single().Life == HollowConnect.Core.Life.Down : runtime.Down),
                    "both observe downed participant before native victory");
            } else {
                yield return Until(() => runtime.Peers.Values.Single().Life == HollowConnect.Core.Life.Alive && !runtime.Down,
                    "living participants isolate context timing from downed-settlement latency");
            }
            if (host) {
                yield return Until(() => !health.IsInvincible && health.hp > 0, "native boss accepts terminal damage after intro");
                Log("PREFLIGHT terminal" + State());
                health.ApplyExtraDamage(Math.Max(1, health.hp)); Flag("cn.killed");
            }
            yield return Until(() => Has("cn.killed"), "native HealthManager terminal damage applied");
            if (host && !entityOnly) {
                yield return Until(() => terminalObserved, "actual authority End hook observed");
                var traceAt = 0f;
                yield return Until(() => {
                    if (Time.unscaledTime >= traceAt) { traceAt = Time.unscaledTime + 1; Log("WAIT CAPTURE" + State()); }
                    return Time.unscaledTime >= readyAt && (savedReady?.Invoke() ?? true);
                },
                    "actual readiness recovers while capture still unavailable", 6);
                Check(Field<string>(room, "pending") == "" && !Field<bool>(controller, "endedScene") && !Field<bool>(room, "sent"),
                    "real authority retains empty pending after readiness recovers");
                yield return new WaitForSecondsRealtime(1);
                Check(!Field<bool>(controller, "endedScene") && nativeEnds == 0, "native completion stays deferred throughout missing context");
                captureReleased = true;
            }
            yield return Until(() => nativeEnds == 1 && Field<bool>(room, "completionApplied") && !runtime.Down && !runtime.Returning,
                "native completion and recovered participation on both endpoints", 35);
            if (host && !entityOnly) Check(Field<bool>(room, "sent") && captureNulls > 0, "later context enqueued through real transport before native end");
            if (!host && entityOnly) Check(entityObserved && entityHeld && droppedRoomCompletions > 0 &&
                Field<string>(room, "pending") == null && !Field<bool>(room, "entityCompletionPending"),
                "entity-only actual delivery completes after gate recovery without room completion");
            yield return new WaitForSecondsRealtime(1);
            Check(nativeEnds == 1 && !runtime.Down && !runtime.Returning, "native completion remains exactly once with stable recovered player");
            File.WriteAllText(Path.Combine(root, role + ".native-completion.json"), JsonConvert.SerializeObject(new {
                mode = entityOnly ? "entity-only" : "late-context", nativeEnds, terminalObserved, captureNulls,
                entityObserved, entityHeld, droppedRoomCompletions, completed = Field<bool>(room, "completionApplied"),
                ready = savedReady?.Invoke() ?? true, down = runtime.Down, sent = Field<bool>(room, "sent")
            }, Formatting.Indented));
            Flag(role + ".cn.checked");
            yield return Until(() => Has("host.cn.checked") && Has("guest.cn.checked"), "both native completion result files recorded");
        } finally {
            CoopContext.CaptureBossContext = savedCapture; CoopContext.BossTransitionReady = savedReady;
            CoopContext.ApplyBossEntityComplete = savedEntity;
            if (controller != null) deadField.SetValue(controller, Delegate.Remove((Delegate)deadField.GetValue(controller), nativeEnd));
        }
    }
}
