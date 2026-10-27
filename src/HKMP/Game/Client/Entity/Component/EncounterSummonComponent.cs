using System.Linq;
using Hkmp.Networking.Client;
using Hkmp.Networking.Packet.Data;
using Hkmp.Util;
using HutongGames.PlayMaker;
using UnityEngine;

namespace Hkmp.Game.Client.Entity.Component;

// Local Battle Control owns the wave threshold/timer and local gates. Its
// SUMMON must reach the actual scene-authority summoner, including when that
// player's hero never touched the arena trigger. All summon actions/animation
// and the spawned enemy still run under the ordinary entity synchronizer.
internal sealed class EncounterSummonComponent : EntityComponent {
    private bool requested, accepted, applying;
    private float nextRequest;
    private readonly LocalBattleStart battle;
    internal EncounterSummonComponent(NetClient client, ulong id, HostClientPair<GameObject> objects, LocalBattleStart battle = null)
        : base(client, id, objects) {
        this.battle = battle;
        MonoBehaviourUtil.Instance.OnUpdateEvent += Tick;
    }
    internal void RequestBattle(HutongGames.PlayMaker.Fsm sender) {
        if (battle?.Matches(sender) == true && !CoopContext.Resetting) RequestNativeWave();
    }
    private void Send(byte action) {
        var data = new EntityNetworkData { Type = EntityComponentType.EncounterSummon };
        data.Packet.Write(action); SendData(data);
    }
    internal bool NativeEvent(HutongGames.PlayMaker.Fsm fsm, string signal) {
        if (signal != "SUMMON" || fsm.Name != "summon" || applying || CoopContext.Resetting) return false;
        RequestNativeWave();
        // The replica must not run native SetPosition/SetParent on its dormant
        // host enemy. Its summon animation and enemy activation are replicated.
        return IsControlled;
    }
    internal void RequestNativeWave() {
        requested = true;
        if (!accepted) { Send(EncounterSummonState.Request); nextRequest = Time.unscaledTime + .5f; }
    }
    public override void InitializeHost() {
        Send(EncounterSummonState.Register);
        nextRequest = 0;
    }
    public override void Update(EntityNetworkData data, bool snapshot) {
        var state = data.Packet.ReadByte();
        if (state == EncounterSummonState.Accepted) accepted = true;
        Tick();
    }
    private void Tick() {
        if (CoopContext.Resetting || GameObject.Host == null) return;
        if (requested && !accepted && Time.unscaledTime >= nextRequest) {
            nextRequest = Time.unscaledTime + .5f; Send(EncounterSummonState.Request);
        }
        if (IsControlled || !accepted || !GameObject.Host.activeInHierarchy) return;
        if (battle != null) {
            applying = true;
            try { battle.Tick(); } finally { applying = false; }
            return;
        }
        var fsm = GameObject.Host.GetComponents<PlayMakerFSM>().FirstOrDefault(f => f.FsmName == "summon");
        // A takeover snapshot beyond the native entry transition already ran
        // this request. Repeated receipts never rewind or re-summon an enemy.
        if (fsm == null || !fsm.enabled || fsm.Fsm.ActiveState?.Transitions.Any(t => t.EventName == "SUMMON") != true) return;
        applying = true;
        try { fsm.SendEvent("SUMMON"); } finally { applying = false; }
    }
    public override void Destroy() => MonoBehaviourUtil.Instance.OnUpdateEvent -= Tick;
}
