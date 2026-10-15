using Hkmp.Networking.Client;
using Hkmp.Networking.Packet.Data;
using UnityEngine;
using Logger = Hkmp.Logging.Logger;

namespace Hkmp.Game.Client.Entity.Component;

// Local native triggers request membership; the server publishes the total.
internal class DreamPlatformComponent : EntityComponent {
    internal const byte Exited = 0, Entered = 1, Register = 2, State = 3;
    private readonly HostClientPair<DreamPlatform> _platform;
    private ushort _numInRange;
    private bool _isInRange;
    private bool _hasServerState;

    public DreamPlatformComponent(NetClient client, ulong id, HostClientPair<GameObject> objects) : base(client, id, objects) {
        _platform = new HostClientPair<DreamPlatform> {
            Host = objects.Host.GetComponent<DreamPlatform>(), Client = objects.Client.GetComponent<DreamPlatform>()
        };
        if (_platform.Host.showOnEnable) return;
        _platform.Host.outerCollider.OnTriggerExited += OnExited;
        _platform.Client.outerCollider.OnTriggerExited += OnExited;
        _platform.Host.innerCollider.OnTriggerEntered += OnEntered;
        _platform.Client.innerCollider.OnTriggerEntered += OnEntered;
        On.DreamPlatform.Start += OnStart;
    }
    private void OnStart(On.DreamPlatform.orig_Start orig, DreamPlatform self) {
        if (self != _platform.Host && self != _platform.Client) orig(self);
    }
    private bool IsLocalTrigger(Collider2D collider, GameObject sender, bool inner) {
        var platform = IsControlled ? _platform.Client : _platform.Host;
        var hero = HeroController.instance;
        return platform != null && hero != null && collider != null && collider.transform.IsChildOf(hero.transform) &&
            sender == (inner ? platform.innerCollider.gameObject : platform.outerCollider.gameObject);
    }
    private void OnEntered(Collider2D collider, GameObject sender) {
        if (_isInRange || !IsLocalTrigger(collider, sender, true)) return;
        _isInRange = true;
        SendPresence(Entered);
    }
    private void OnExited(Collider2D collider, GameObject sender) {
        if (!_isInRange || !IsLocalTrigger(collider, sender, false)) return;
        _isInRange = false;
        SendPresence(Exited);
    }
    private void SendPresence(byte action) {
        var data = new EntityNetworkData { Type = EntityComponentType.DreamPlatform };
        data.Packet.Write((ushort)(_isInRange ? 1 : 0));
        data.Packet.Write(action);
        SendData(data);
    }
    public override void InitializeHost() {
        if (_platform.Host.showOnEnable) return;
        ApplyState();
        SendPresence(Register);
    }
    public override void Update(EntityNetworkData data, bool snapshot) {
        var count = data.Packet.ReadUShort();
        var action = data.Packet.ReadByte();
        if (action != State) { Logger.Error("Unknown dream platform state action: " + action); return; }
        var firstState = !_hasServerState;
        _hasServerState = true;
        _numInRange = count;
        ApplyState();
        // Triggers may fire before the room snapshot establishes registration.
        if ((snapshot || firstState) && _isInRange) SendPresence(Entered);
    }
    private void ApplyState() {
        var platform = IsControlled ? _platform.Client : _platform.Host;
        if (_numInRange != 0) platform.Show(); else platform.Hide();
    }
    public override void Destroy() {
        foreach (var platform in new[] { _platform.Host, _platform.Client }) {
            if (platform == null || platform.showOnEnable) continue;
            platform.outerCollider.OnTriggerExited -= OnExited;
            platform.innerCollider.OnTriggerEntered -= OnEntered;
        }
        On.DreamPlatform.Start -= OnStart;
    }
}
