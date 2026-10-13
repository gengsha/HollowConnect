using System.Collections.Generic;
using System.Linq;
using Hkmp.Networking.Client;
using Hkmp.Networking.Packet.Data;
using Hkmp.Util;
using UnityEngine;

namespace Hkmp.Game.Client.Entity.Component; 

/// <inheritdoc />
/// This component manages the activation of the children of an entity.
internal class ChildrenActivationComponent : EntityComponent {
    private readonly Dictionary<string, bool> previous = new();
    public ChildrenActivationComponent(NetClient client, ulong id, HostClientPair<GameObject> objects) : base(client, id, objects) {
        MonoBehaviourUtil.Instance.OnUpdateEvent += OnUpdate;
    }
    private void OnUpdate() {
        if (IsControlled || CoopContext.Resetting || GameObject.Host == null) return;
        var children = GameObject.Host.GetChildren();
        var values = new Dictionary<string, bool>();
        foreach (var child in children) if (child != null) values[EntityIdentity.Path(child.transform, GameObject.Host.transform)] = child.activeSelf;
        if (previous.Count == values.Count && values.All(p => previous.TryGetValue(p.Key, out var v) && v == p.Value)) return;
        if (values.Count > 1024) return;
        var data = new EntityNetworkData { Type = EntityComponentType.ChildrenActivation };
        data.Packet.Write((ushort)values.Count);
        foreach (var pair in values) { data.Packet.Write(pair.Key); data.Packet.Write(pair.Value); }
        previous.Clear(); foreach (var pair in values) previous[pair.Key] = pair.Value;
        SendData(data);
    }
    public override void InitializeHost() { previous.Clear(); }
    public override void Update(EntityNetworkData data, bool snapshot) {
        if (!IsControlled) return;
        var count = data.Packet.ReadUShort();
        if (count > 1024) throw new System.FormatException("Too many activation targets");
        var values = new Dictionary<string, bool>();
        for (var i = 0; i < count; i++) values[data.Packet.ReadString()] = data.Packet.ReadBool();
        foreach (var root in new[] { GameObject.Host, GameObject.Client }) {
            if (root == null) continue;
            foreach (var child in root.GetChildren()) {
                if (child != null && values.TryGetValue(EntityIdentity.Path(child.transform, root.transform), out var active)) child.SetActive(active);
            }
        }
    }
    public override void Destroy() => MonoBehaviourUtil.Instance.OnUpdateEvent -= OnUpdate;
}
