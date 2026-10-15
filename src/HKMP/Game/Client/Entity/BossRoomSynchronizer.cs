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
    private string layout, sentLayout;
    private string roster, sentRoster;
    private bool sent, applying, takeover;
    private float next;
    private uint visit, epoch;
    internal BossRoomSynchronizer(NetClient client, EntityManager entities) {
        this.client = client; this.entities = entities; warriors = new DreamWarriorSynchronizer(client, entities);
    }
    internal void Register() { warriors.Register(); On.BossSceneController.EndBossScene += End; MonoBehaviourUtil.Instance.OnUpdateEvent += Tick; }
    internal void Destroy() { warriors.Destroy(); On.BossSceneController.EndBossScene -= End; MonoBehaviourUtil.Instance.OnUpdateEvent -= Tick; }
    internal void Clear() { warriors.Clear(); pending = latest = layout = sentLayout = roster = sentRoster = null; sent = applying = takeover = false; next = 0; visit = CoopContext.LocalVisit; epoch = CoopContext.Epoch; }
    internal void AuthorityTransferred() { takeover = true; }
    private static bool Owns(BossSceneController controller) {
        var scene = controller.gameObject.scene.name;
        return scene == CoopContext.LocalScene || scene == CoopContext.LocalScene + "_boss" || scene == CoopContext.LocalScene + "_boss_defeated";
    }
    private void End(On.BossSceneController.orig_EndBossScene orig, BossSceneController self) {
        if (!client.IsConnected || !Owns(self)) { orig(self); return; }
        if (applying) { orig(self); return; }
        // Local replica OnDeath/EndGGBossScene must never advance a sequence.
        if (!entities.IsSceneHost || !entities.IsSceneHostDetermined || CoopContext.Resetting) return;
        var context = CoopContext.CaptureBossContext?.Invoke();
        if (!sent && context != null) {
            Logger.Debug("Boss room completion sent: " + CoopContext.LocalScene);
            Send(EntityComponentType.BossSceneComplete, context); sent = true;
        }
        // The host also waits while downed or entering the room. This retains
        // the player's rescue/voluntary-bench choice instead of advancing under it.
        if (!(CoopContext.BossTransitionReady?.Invoke() ?? true)) { pending = context; return; }
        orig(self);
    }
    private void Send(EntityComponentType type, string context) {
        var data = new EntityNetworkData { Type = type }; data.Packet.Write(context);
        client.UpdateManager.AddEntityData(0UL, data);
    }
    internal void Receive(EntityNetworkData data) {
        if (data.Type == EntityComponentType.DreamWarrior) { warriors.Receive(data); return; }
        var context = data.Packet.ReadString();
        if (context.Length > 32768) throw new FormatException("Boss context exceeds limit");
        if (data.Type == EntityComponentType.EnemyRoster) roster = context;
        else if (data.Type == EntityComponentType.SceneLayout) layout = context;
        else if (data.Type == EntityComponentType.BossSceneComplete) { pending = context; Logger.Debug("Boss room completion received: " + CoopContext.LocalScene); }
        else latest = context;
    }
    private void Tick() {
        if (visit != CoopContext.LocalVisit || epoch != CoopContext.Epoch) Clear();
        if (!client.IsConnected || !entities.IsSceneHostDetermined || CoopContext.Resetting) return;
        try {
            warriors.Tick();
            if (entities.IsSceneHost && Time.unscaledTime >= next) {
                next = Time.unscaledTime + 1;
                var currentLayout = BossSceneLayout.Capture();
                if (currentLayout != sentLayout) { sentLayout = currentLayout; Send(EntityComponentType.SceneLayout, currentLayout); }
                var currentRoster = EnemyRoster.Capture();
                if (currentRoster != sentRoster) { sentRoster = currentRoster; Send(EntityComponentType.EnemyRoster, currentRoster); }
                if (!sent && UnityEngine.Object.FindObjectsOfType<BossSceneController>().Any(Owns)) {
                    var context = CoopContext.CaptureBossContext?.Invoke();
                    if (context != null) Send(EntityComponentType.BossSceneContext, context);
                }
            }
            if (!(CoopContext.BossTransitionReady?.Invoke() ?? true)) return;
            if (roster != null && EnemyRoster.Apply(roster)) roster = null;
            if (layout != null && !entities.IsSceneHost) { BossSceneLayout.Apply(layout); layout = null; }
            var controller = UnityEngine.Object.FindObjectsOfType<BossSceneController>().FirstOrDefault(Owns);
            if (controller == null) return;
            if (takeover && entities.IsSceneHost && !sent && pending == null && controller.bosses != null && controller.bosses.Length > 0 && controller.bosses.All(h => h == null || h.isDead)) {
                controller.EndBossScene(); return;
            }
            if (pending != null) {
                if (!(CoopContext.ApplyBossContext?.Invoke(pending) ?? false)) return;
                applying = true;
                try { controller.EndBossScene(); pending = latest = null; sent = true; }
                finally { applying = false; }
            } else if (latest != null && !entities.IsSceneHost) {
                if (CoopContext.ApplyBossContext?.Invoke(latest) ?? false) latest = null;
            }
        } catch (Exception e) { Logger.Error($"Boss room synchronization: {e}"); }
    }
}
