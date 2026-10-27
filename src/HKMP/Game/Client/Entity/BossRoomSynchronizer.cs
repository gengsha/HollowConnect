using System;
using System.Linq;
using Hkmp.Game.Client.Entity.Component;
using Hkmp.Networking.Client;
using Hkmp.Networking.Packet.Data;
using Hkmp.Util;
using UnityEngine;
using UnityEngine.SceneManagement;
using Logger = Hkmp.Logging.Logger;

namespace Hkmp.Game.Client.Entity;

// Room scope deliberately includes controllers with no bosses[] (scripted
// completion) and controllers in the active room's additive boss scene.
internal sealed class BossRoomSynchronizer {
    private readonly NetClient client;
    private readonly EntityManager entities;
    private readonly DreamWarriorSynchronizer warriors;
    private string pending, latest;
    private string layout, liveLayout, appliedLayout, sentLayout;
    private string roster, sentRoster, sentScope;
    private bool sent, applying, takeover, completionApplied, completionLayoutRequested, defeatReported;
    private bool entityCompletionPending, entitySettlementAwaited;
    private float next;
    // F07: a native terminal can be observed before the context is capturable.
    // Keep the intent alive, retry capture, and only then commit the local end.
    private float contextDeadline, nextContextAttempt;
    private bool contextExpiredLogged;
    private uint visit, epoch;
    internal BossRoomSynchronizer(NetClient client, EntityManager entities) {
        this.client = client; this.entities = entities; warriors = new DreamWarriorSynchronizer(client, entities);
    }
    internal void Register() { warriors.Register(); CoopContext.ApplyBossEntityComplete = ApplyEntityCompletion; On.BossSceneController.EndBossScene += End; MonoBehaviourUtil.Instance.OnUpdateEvent += Tick; }
    internal void Destroy() { if (CoopContext.ApplyBossEntityComplete == ApplyEntityCompletion) CoopContext.ApplyBossEntityComplete = null; CoopContext.RestoreBossContext?.Invoke(); warriors.Destroy(); On.BossSceneController.EndBossScene -= End; MonoBehaviourUtil.Instance.OnUpdateEvent -= Tick; }
    internal void Clear(bool sceneChanged = false) {
        // A loaded destination has already accepted native progression and its
        // SetupEvent. Rolling back here overwrites the new room with the old index.
        if (sceneChanged) CoopContext.CommitBossContext?.Invoke();
        else CoopContext.RestoreBossContext?.Invoke();
        warriors.Clear(); pending = latest = layout = liveLayout = appliedLayout = sentLayout = roster = sentRoster = sentScope = null;
        sent = applying = takeover = completionApplied = completionLayoutRequested = defeatReported = false;
        entityCompletionPending = entitySettlementAwaited = false;
        next = 0; contextDeadline = nextContextAttempt = 0; contextExpiredLogged = false;
        visit = CoopContext.LocalVisit; epoch = CoopContext.Epoch;
    }
    internal void AuthorityTransferred() { takeover = true; }
    // Entity-level completion is the fallback for special-death bosses whose
    // authority sent only the entity bool. It must never advance a sequence:
    // the room-level context owns BossIndex and is applied through pending.
    internal void ApplyEntityCompletion() {
        if (visit != CoopContext.LocalVisit || epoch != CoopContext.Epoch) Clear();
        if (!client.IsConnected || !entities.IsSceneHostDetermined || entities.IsSceneHost || completionApplied || CoopContext.Resetting || pending != null) return;
        if (BossSequenceController.IsInSequence) return;
        entityCompletionPending = true;
        TryEntityCompletion();
    }
    private void TryEntityCompletion() {
        if (!entityCompletionPending) return;
        if (entities.IsSceneHost || completionApplied || CoopContext.Resetting || pending != null || BossSequenceController.IsInSequence) {
            entityCompletionPending = entitySettlementAwaited = false;
            return;
        }
        // A reliable entity message may arrive only once. Retain it while the
        // player is down/loading or the addon is waiting for victory receipts.
        if (!entitySettlementAwaited) {
            if (!TryAwaitSettlement()) return;
            entitySettlementAwaited = true;
        }
        if (!TransitionReady()) return;
        var controller = UnityEngine.Object.FindObjectsOfType<BossSceneController>(true).FirstOrDefault(Owns);
        if (controller == null || entities.IsPrivateBossScene(controller.gameObject.scene.name)) return;
        applying = true;
        try {
            controller.EndBossScene(); completionApplied = true;
            entityCompletionPending = entitySettlementAwaited = false;
        }
        catch (Exception e) { Logger.Error($"Boss entity completion failed scene={CoopContext.LocalScene} visit={CoopContext.LocalVisit}: {e}"); }
        finally { applying = false; }
    }
    private static bool Owns(BossSceneController controller) {
        var scene = controller.gameObject.scene.name;
        return scene == CoopContext.LocalScene || scene == CoopContext.LocalScene + "_boss" || scene == CoopContext.LocalScene + "_boss_defeated";
    }
    private void End(On.BossSceneController.orig_EndBossScene orig, BossSceneController self) {
        if (!client.IsConnected || !Owns(self)) { orig(self); return; }
        if (entities.IsPrivateBossScene(self.gameObject.scene.name)) { orig(self); return; }
        if (applying) { orig(self); return; }
        // Local replica OnDeath/EndGGBossScene must never advance a sequence.
        if (!entities.IsSceneHost || !entities.IsSceneHostDetermined || CoopContext.Resetting || completionApplied) return;
        TryBossDefeated();
        var context = TryCaptureContext();
        if (string.IsNullOrEmpty(context) && !string.IsNullOrEmpty(pending)) context = pending;
        if (!sent && !string.IsNullOrEmpty(context)) {
            Logger.Debug("Boss room completion sent: " + CoopContext.LocalScene);
            if (TrySend(EntityComponentType.BossSceneComplete, context)) sent = true;
        }
        // The host also waits while downed or entering the room. This retains
        // the player's rescue/voluntary-bench choice instead of advancing under it.
        if (!TransitionReady() || pending != null || (!sent && !string.IsNullOrEmpty(context))) {
            // Keep the terminal intent even when the context is not available
            // yet. An empty pending string means "native end observed, context
            // pending": Tick retries capture, then commits a logged local end
            // if the context never becomes available.
            if (pending == null) {
                pending = context ?? "";
                contextDeadline = Time.unscaledTime + 8;
                nextContextAttempt = 0;
                contextExpiredLogged = false;
            } else if (!string.IsNullOrEmpty(context)) pending = context;
            return;
        }
        if (string.IsNullOrEmpty(context) && !sent)
            Logger.Error($"Boss room completion has no context scene={CoopContext.LocalScene} visit={CoopContext.LocalVisit} epoch={CoopContext.Epoch}");
        try { orig(self); }
        catch (Exception e) {
            Logger.Error($"Boss room native end failed scene={CoopContext.LocalScene} visit={CoopContext.LocalVisit} epoch={CoopContext.Epoch}: {e}");
            throw;
        }
        completionApplied = true;
        try { CoopContext.CommitBossContext?.Invoke(); }
        catch (Exception e) { Logger.Error($"Boss room context commit failed scene={CoopContext.LocalScene}: {e}"); }
        pending = latest = layout = liveLayout = appliedLayout = null;
        contextDeadline = nextContextAttempt = 0; contextExpiredLogged = false;
    }
    private void TryBossDefeated() {
        try { CoopContext.BossDefeated?.Invoke(0); }
        catch (Exception e) {
            Logger.Error($"Boss defeated report failed scene={CoopContext.LocalScene} visit={CoopContext.LocalVisit}: {e}");
        }
    }
    private bool TransitionReady() {
        try { return CoopContext.BossTransitionReady?.Invoke() ?? true; }
        catch (Exception e) {
            Logger.Error($"Boss room transition check failed scene={CoopContext.LocalScene} visit={CoopContext.LocalVisit} epoch={CoopContext.Epoch}: {e}");
            return false;
        }
    }
    private bool TryAwaitSettlement() {
        try { CoopContext.AwaitBossSettlement?.Invoke(); return true; }
        catch (Exception e) {
            Logger.Error($"Boss room settlement await failed scene={CoopContext.LocalScene}: {e}");
            return false;
        }
    }
    private string TryCaptureContext() {
        try { return CoopContext.CaptureBossContext?.Invoke(); }
        catch (Exception e) {
            Logger.Error($"Boss context capture failed scene={CoopContext.LocalScene} visit={CoopContext.LocalVisit}: {e}");
            return null;
        }
    }
    private bool TrySend(EntityComponentType type, string context) {
        try { Send(type, context); return true; }
        catch (Exception e) {
            Logger.Error($"Boss room send failed type={type} scene={CoopContext.LocalScene} visit={CoopContext.LocalVisit}: {e}");
            return false;
        }
    }
    private void Send(EntityComponentType type, string context) {
        var data = new EntityNetworkData { Type = type }; data.Packet.Write(context);
        client.UpdateManager.AddEntityData(0UL, data);
    }
    internal void Receive(EntityNetworkData data) {
        if (data.Type == EntityComponentType.BossCombatScope) {
            if (CoopContext.LocalWorldOnly) entities.AcceptBossScope(data.Packet.ReadString());
            return;
        }
        if (data.Type == EntityComponentType.DreamWarrior) { warriors.Receive(data); return; }
        if (CoopContext.LocalWorldOnly && data.Type != EntityComponentType.BossSceneComplete) return;
        var context = data.Packet.ReadString();
        if (context.Length > 32768) throw new FormatException("Boss context exceeds limit");
        if (data.Type == EntityComponentType.EnemyRoster) roster = context;
        else if (data.Type == EntityComponentType.SceneLayout) {
            layout = context;
            if (BossSceneLayout.HasLoadedBossScene(context) && liveLayout != context) {
                liveLayout = context;
                if (pending != null) completionLayoutRequested = false;
            }
        }
        else if (data.Type == EntityComponentType.BossSceneComplete) {
            if (!completionApplied) {
                // Completion and addon victory receipts can travel in separate
                // packets. A living guest must retain the room as an anchor
                // while its downed authority is still requesting settlement.
                TryAwaitSettlement();
                entityCompletionPending = entitySettlementAwaited = false;
                pending = context; latest = null;
                contextDeadline = Time.unscaledTime + 8;
                nextContextAttempt = 0;
                contextExpiredLogged = false;
            }
            Logger.Debug("Boss room completion received: " + CoopContext.LocalScene);
        }
        // EndBossScene completes asynchronously. Its native callback owns the
        // index until scene exit; a late heartbeat must not reopen an overlay.
        else if (!completionApplied && !sent && pending == null) latest = context;
    }
    private void Tick() {
        if (visit != CoopContext.LocalVisit || epoch != CoopContext.Epoch) Clear();
        if (CoopContext.Resetting) { entityCompletionPending = entitySettlementAwaited = false; return; }
        if (!client.IsConnected || !entities.IsSceneHostDetermined) return;
        try {
            warriors.Tick();
            if (entities.IsSceneHost && !defeatReported) {
                // Keep this path frame-rate independent from the one-second
                // room-layout heartbeat.  Native death effects can disable or
                // destroy their HealthManager between two heartbeats.
                foreach (var entity in (EntityReferences.Entities?.Values ?? Enumerable.Empty<Entity>()).ToArray()) {
                    if (!Owns(entity.SourceScene) || !BossDefeatSignals.IsFinalHealthDeath(entity.Type) || entity.Object.Host == null) continue;
                    var health = entity.Object.Host.GetComponent<HealthManager>();
                    if (health != null && health.isDead && health.hp <= 0 && !health.hasSpecialDeath && !health.deathReset) {
                        if (BossDefeatSignals.HealthDeath(entity.Type, entity.Id)) {
                            defeatReported = true;
                            break;
                        }
                    }
                }
            }
            if (entities.IsSceneHost && Time.unscaledTime >= next) {
                next = Time.unscaledTime + 1;
                if (CoopContext.LocalWorldOnly) {
                    var hero = HeroController.instance;
                    if (hero != null && hero.isHeroInPosition && !global::GameManager.instance.IsInSceneTransition) {
                        var scope = entities.CaptureBossScope();
                        if (scope != sentScope) { sentScope = scope; Send(EntityComponentType.BossCombatScope, scope); }
                    }
                } else {
                var currentLayout = BossSceneLayout.Capture();
                if (currentLayout != sentLayout) { sentLayout = currentLayout; Send(EntityComponentType.SceneLayout, currentLayout); }
                var currentRoster = EnemyRoster.Capture();
                if (currentRoster != sentRoster) { sentRoster = currentRoster; Send(EntityComponentType.EnemyRoster, currentRoster); }
                if (!sent && !completionApplied && pending == null && UnityEngine.Object.FindObjectsOfType<BossSceneController>().Any(Owns)) {
                    var context = TryCaptureContext();
                    if (context != null) Send(EntityComponentType.BossSceneContext, context);
                }
                }
                if (!defeatReported) {
                    var liveController = UnityEngine.Object.FindObjectsOfType<BossSceneController>(true).FirstOrDefault(Owns);
                    if (liveController?.bosses != null && liveController.bosses.Length > 0 && liveController.bosses.All(h => h == null || h.isDead)) {
                        defeatReported = true;
                        TryBossDefeated();
                    }
                }
            }
            // Retry a missing context while the authority is waiting for its
            // downed/loading transition to finish. Do not turn a null context
            // into "no completion task".
            if (entities.IsSceneHost && pending != null && !sent) {
                if (!contextExpiredLogged && Time.unscaledTime >= nextContextAttempt) {
                    nextContextAttempt = Time.unscaledTime + .5f;
                    if (pending.Length == 0) pending = TryCaptureContext() ?? "";
                    if (pending.Length != 0 && TrySend(EntityComponentType.BossSceneComplete, pending)) sent = true;
                }
                // Readiness alone cannot retire an unfinished delivery task.
                // A local fallback is allowed only after an explicit deadline
                // failure, and must not be marked as a successful send.
                if (!sent && Time.unscaledTime < contextDeadline) return;
                if (!sent && !contextExpiredLogged) {
                    var failure = pending.Length == 0 ? "context unavailable" : "context could not be sent";
                    Logger.Error($"Boss room completion {failure} scene={CoopContext.LocalScene} visit={CoopContext.LocalVisit} epoch={CoopContext.Epoch}; ending locally without a remote context");
                    contextExpiredLogged = true;
                }
            }
            if (entityCompletionPending) {
                TryEntityCompletion();
                if (entityCompletionPending || completionApplied) return;
            }
            if (!TransitionReady()) return;
            if (roster != null && EnemyRoster.Apply(roster)) roster = null;
            // A defeated layout unloads the additive boss scene. Consume a
            // queued completion first, otherwise a player who warped in while
            // the host finished can unload the only controller before its
            // native corpse/exit sequence runs.
            var controller = UnityEngine.Object.FindObjectsOfType<BossSceneController>(true).FirstOrDefault(Owns);
            if (takeover && entities.IsSceneHost && !completionApplied && !sent && pending == null && controller != null && controller.bosses != null && controller.bosses.Length > 0 && controller.bosses.All(h => h == null || h.isDead)) {
                controller.EndBossScene(); return;
            }
            if (pending != null) {
                if (controller == null || entities.IsPrivateBossScene(controller.gameObject.scene.name)) {
                    if (CoopContext.LocalWorldOnly) { pending = null; return; }
                    // A late entrant can receive the host's empty post-death
                    // layout before its reliable completion context. Restore
                    // the last live variant long enough for Unity to create a
                    // native BossSceneController, then consume completion on a
                    // later tick. Issue the asynchronous load only once.
                    var bootstrap = BossSceneLayout.HasLoadedBossScene(layout) ? layout : liveLayout;
                    if (!entities.IsSceneHost && !completionLayoutRequested && bootstrap != null) {
                        if (bootstrap != appliedLayout) {
                            BossSceneLayout.Apply(bootstrap);
                            appliedLayout = bootstrap;
                        }
                        completionLayoutRequested = true;
                    }
                    return;
                }
                if (pending.Length != 0 && !CoopContext.LocalWorldOnly &&
                    !(CoopContext.ApplyBossContext?.Invoke(pending) ?? false)) {
                    if (Time.unscaledTime > contextDeadline && !contextExpiredLogged) {
                        Logger.Error($"Boss room completion context could not be applied scene={CoopContext.LocalScene} visit={CoopContext.LocalVisit} pendingBytes={pending.Length}");
                        contextExpiredLogged = true;
                    }
                    return;
                }
                applying = true;
                try {
                    controller.EndBossScene();
                    completionApplied = true;
                    if (!CoopContext.LocalWorldOnly) CoopContext.CommitBossContext?.Invoke();
                    // The layout captured before completion describes the live
                    // boss scene. Do not reapply it after EndBossScene has
                    // started the native corpse/exit sequence.
                    layout = null;
                    liveLayout = null;
                    appliedLayout = null;
                    completionLayoutRequested = false;
                    pending = latest = null;
                    contextDeadline = nextContextAttempt = 0;
                    contextExpiredLogged = false;
                }
                catch { CoopContext.RestoreBossContext?.Invoke(); throw; }
                finally { applying = false; }
            } else if (latest != null && !completionApplied && !sent && !entities.IsSceneHost) {
                if (CoopContext.ApplyBossContext?.Invoke(latest) ?? false) latest = null;
            }
            if (layout != null && !entities.IsSceneHost && pending == null && !completionApplied) {
                // An empty layout often follows a boss death. Keep an existing
                // native controller alive until its reliable completion event
                // can run the corpse/exit sequence.
                if (BossSceneLayout.HasLoadedBossScene(layout) || controller == null) {
                    BossSceneLayout.Apply(layout); appliedLayout = layout; layout = null;
                }
            }
        } catch (Exception e) { Logger.Error($"Boss room synchronization: {e}"); }
    }

    private static bool Owns(string scene) => scene == CoopContext.LocalScene ||
        scene == CoopContext.LocalScene + "_boss" || scene == CoopContext.LocalScene + "_boss_defeated";
}
