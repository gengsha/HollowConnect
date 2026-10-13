using Hkmp.Networking.Client;
using Hkmp.Networking.Packet.Data;
using UnityEngine;

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
        data.Packet.Write(true); SendData(data);
    }
    public override void InitializeHost() { }
    public override void Update(EntityNetworkData data, bool snapshot) {
        if (!IsControlled || controller == null || !data.Packet.ReadBool()) return;
        controller.EndBossScene(); // Native endedScene guard makes replays idempotent.
    }
    public override void Destroy() => On.BossSceneController.EndBossScene -= End;
}
