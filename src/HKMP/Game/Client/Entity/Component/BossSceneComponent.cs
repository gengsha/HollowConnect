using System;
using Hkmp.Networking.Client;
using Hkmp.Networking.Packet.Data;
using UnityEngine;
using Logger = Hkmp.Logging.Logger;

namespace Hkmp.Game.Client.Entity.Component;

// A special-death boss can finish through a script/SendDeathEvent without
// HealthManager.Die setting isDead. Mirror the authority's actual controller
// completion, rather than inferring completion from zero HP or animation.
internal sealed class BossSceneComponent : EntityComponent {
    private readonly BossSceneController controller;
    private bool sent;
    public BossSceneComponent(NetClient client, ulong id, HostClientPair<GameObject> objects, BossSceneController controller)
        : base(client, id, objects) {
        this.controller = controller;
        On.BossSceneController.EndBossScene += End;
    }
    private void End(On.BossSceneController.orig_EndBossScene orig, BossSceneController self) {
        orig(self);
        if (self != controller || IsControlled || sent || CoopContext.Resetting) return;
        sent = true;
        var data = new EntityNetworkData { Type = EntityComponentType.BossSceneComplete };
        data.Packet.Write(true);
        try { SendData(data); }
        catch (Exception e) {
            Logger.Error($"Boss scene completion send failed scene={controller?.gameObject.scene.name}: {e}");
        }
    }
    public override void InitializeHost() { }
    public override void Update(EntityNetworkData data, bool snapshot) {
        if (!IsControlled || controller == null || !data.Packet.ReadBool()) return;
        try {
            // The room synchronizer owns the completion context and sequence
            // index. Use its guarded fallback when available; the native call
            // remains the last resort when no synchronizer is registered.
            if (CoopContext.ApplyBossEntityComplete != null) CoopContext.ApplyBossEntityComplete();
            else controller.EndBossScene(); // Native endedScene guard makes replays idempotent.
        }
        catch (Exception e) {
            Logger.Error($"Boss scene replica completion failed scene={controller?.gameObject.scene.name}: {e}");
        }
    }
    public override void Destroy() => On.BossSceneController.EndBossScene -= End;
}
