using System;
using System.Collections;
using System.Linq;
using System.Reflection;
using Hkmp.Game.Client.Entity;
using Hkmp.Game.Client.Entity.Component;
using Hkmp.Networking.Client;
using Hkmp.Networking.Packet;
using UnityEngine;
using Object = UnityEngine.Object;

namespace HollowConnect.Smoke;

public sealed partial class CoverageProbe {
    private IEnumerator EnvironmentStateChecks() {
        var source = new GameObject("HollowConnect environment state fixture"); source.SetActive(false);
        var decoration = new GameObject("local corpse particles"); decoration.transform.SetParent(source.transform, false);
        decoration.AddComponent<ParticleSystem>();
        var visual = new GameObject("local visual flare"); visual.transform.SetParent(source.transform, false);
        visual.AddComponent<MeshFilter>(); visual.AddComponent<MeshRenderer>();
        var attack = new GameObject("particle attack"); attack.transform.SetParent(source.transform, false);
        attack.AddComponent<ParticleSystem>(); attack.AddComponent<BoxCollider2D>(); attack.AddComponent<DamageHero>();
        var net = new NetClient(new PacketManager()); Entity entity = null;
        try {
            entity = new Entity(net, 0xffffffffffffffa8UL, EntityType.DynamicHazard, source);
            EntityReferences.Register(entity);
            var components = Field<System.Collections.Generic.Dictionary<EntityComponentType, EntityComponent>>(entity, "_components");
            var combat = (CombatStateComponent)components[EntityComponentType.CombatState];
            var build = typeof(CombatStateComponent).GetMethod("Build", BindingFlags.Instance | BindingFlags.NonPublic);
            var nodeField = typeof(CombatStateComponent).GetField("nodes", BindingFlags.Instance | BindingFlags.NonPublic);
            object[] Nodes() => ((Array)nodeField.GetValue(combat)).Cast<object>().ToArray();
            string PathOf(object node) => (string)node.GetType().GetField("Path", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(node);
            object PartOf(object node) => node.GetType().GetField("Host", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(node);
            build.Invoke(combat, null);
            var nodes = Nodes();
            if (nodes.Any(n => PathOf(n).Contains("local%20corpse")) || nodes.Any(n => PathOf(n).Contains("local%20visual")) ||
                !nodes.Any(n => PathOf(n).Contains("particle%20attack")))
                throw new Exception("Decorative leaves were synchronized or particle damage was excluded");
            var attackNode = nodes.Single(n => PathOf(n).Contains("particle%20attack"));
            var part = PartOf(attackNode);
            build.Invoke(combat, null);
            if (!ReferenceEquals(part, PartOf(Nodes().Single(n => PathOf(n).Contains("particle%20attack")))))
                throw new Exception("Unchanged component binding was rebuilt");
            attack.AddComponent<CircleCollider2D>(); build.Invoke(combat, null);
            var colliders = (Collider2D[])part.GetType().GetField("Colliders", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(part);
            if (colliders.Length != 2) throw new Exception("Cached binding missed a newly added hitbox");
            Mark("PASS cosmetic leaves excluded, damaging particles retained, bindings reused and new hitboxes refreshed");
            yield return null;
        } finally { entity?.Destroy(); Object.Destroy(source); net.Disconnect(); }
    }
}
