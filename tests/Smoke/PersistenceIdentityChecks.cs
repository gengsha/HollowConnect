using System;
using Hkmp.Game.Client.Entity;
using Hkmp.Game.Client.Entity.Component;
using Hkmp.Networking.Packet.Data;
using UnityEngine;
using Object = UnityEngine.Object;

namespace HollowConnect.Smoke;

internal static class PersistenceIdentityChecks {
    internal static void Run(Action<string> log) {
        var source = new GameObject("native name(Clone)");
        source.SetActive(false);
        GameObject replica = null;
        try {
            var root = source.AddComponent<PersistentBoolItem>();
            root.persistentBoolData = new PersistentBoolData { activated = false };
            root.semiPersistent = true;
            for (var i = 0; i < 2; i++) {
                var child = new GameObject("same child");
                child.transform.SetParent(source.transform);
                var item = child.AddComponent<PersistentBoolItem>();
                item.persistentBoolData = new PersistentBoolData { id = "explicit-" + i, sceneName = "native-scene", activated = i == 1 };
                var number = child.AddComponent<PersistentIntItem>();
                number.persistentIntData = new PersistentIntData { id = "phase-" + i, sceneName = "additive-scene", value = i + 2 };
            }
            replica = Object.Instantiate(source);
            EntityPersistence.BindReplica(source, replica);
            var copy = replica.GetComponent<PersistentBoolItem>();
            Require(copy.persistentBoolData.id == source.name && copy.persistentBoolData.semiPersistent &&
                copy.persistentBoolData.sceneName == GameManager.GetBaseSceneName(source.scene.name), "implicit native name including legitimate Clone suffix");
            Require(!ReferenceEquals(root.persistentBoolData, copy.persistentBoolData), "independent native and replica data");
            for (var i = 0; i < 2; i++) {
                var child = replica.transform.GetChild(i);
                var data = child.GetComponent<PersistentBoolItem>().persistentBoolData;
                var number = child.GetComponent<PersistentIntItem>().persistentIntData;
                Require(data.id == "explicit-" + i && data.sceneName == "native-scene" && data.activated == (i == 1) &&
                    number.id == "phase-" + i && number.sceneName == "additive-scene" && number.value == i + 2,
                    "duplicate child names preserve distinct explicit bool/int identities and values");
            }
            copy.persistentBoolData.activated = true;
            Require(!root.persistentBoolData.activated, "replica mutation leaves source data untouched");
            log("PASS native persistence identities: explicit/implicit, children, additive scenes, independent state, semi-persistence");
        } finally {
            if (replica != null) Object.DestroyImmediate(replica);
            Object.DestroyImmediate(source);
        }
        TerminalBoundaries(log);
        DormantTerminal(log);
    }
    private static void DormantTerminal(Action<string> log) {
        var owner = new GameObject("never-awakened native");
        owner.SetActive(false);
        PersistentBoolItem item = null;
        try {
            item = owner.AddComponent<PersistentBoolItem>();
            item.semiPersistent = true;
            item.persistentBoolData = new PersistentBoolData();
            var health = owner.AddComponent<HealthManager>();
            health.hp = 0; health.isDead = true;
            EntityPersistence.SaveTerminalDeath(health);
            var saved = SceneData.instance.FindMyState(new PersistentBoolData {
                id = owner.name, sceneName = GameManager.GetBaseSceneName(owner.scene.name)
            });
            Require(saved?.activated == true && saved.semiPersistent && !owner.activeSelf,
                "terminal late snapshot persists never-awakened native without activating its FSMs");
            log("PASS native dormant terminal persistence before Awake without waking the boss");
        } finally {
            if (item != null) SceneData.instance.persistentBoolItems.Remove(item.persistentBoolData);
            Object.DestroyImmediate(owner);
        }
    }
    private static void TerminalBoundaries(Action<string> log) {
        var owner = new GameObject("Persistence terminal fixture");
        owner.SetActive(false);
        HealthManagerComponent component = null;
        PersistentBoolItem item = null;
        try {
            item = owner.AddComponent<PersistentBoolItem>();
            item.enabled = false;
            item.semiPersistent = true;
            item.persistentBoolData = new PersistentBoolData { id = "terminal-fixture", sceneName = "fixture-only", semiPersistent = true };
            var health = owner.AddComponent<HealthManager>();
            health.enabled = false;
            owner.SetActive(true); // Native Awake installs HealthManager's save callbacks.
            health.hp = 100;
            component = new HealthManagerComponent(null, 0x7fff000000000001,
                new HostClientPair<GameObject> { Host = owner }, new HostClientPair<HealthManager> { Host = health }, EntityType.GruzMother);
            void Receive(EntityComponentType type, uint revision, int hp = 0, byte phase = 0) {
                var data = new EntityNetworkData { Type = type };
                data.Packet.Write(revision);
                if (type == EntityComponentType.Death) {
                    data.Packet.Write(false); data.Packet.Write((byte)AttackTypes.Generic); data.Packet.Write(true);
                } else {
                    data.Packet.Write(hp); data.Packet.Write(false); data.Packet.Write(-1);
                    data.Packet.Write(phase); data.Packet.Write(""); data.Packet.Write("");
                }
                data.Packet = new Hkmp.Networking.Packet.Packet(data.Packet.ToArray());
                component.Update(data, false);
            }
            Receive(EntityComponentType.Health, 1, 0, 3);
            Require(SceneData.instance.FindMyState(item.persistentBoolData) == null && !health.isDead,
                "zero HP special/reset phase has no persistent death");
            Receive(EntityComponentType.Death, 2);
            Require(SceneData.instance.FindMyState(item.persistentBoolData)?.activated == true,
                "terminal receiver without replica/effects commits native death");
            Receive(EntityComponentType.Death, 2);
            Receive(EntityComponentType.Health, 3, 100);
            Require(health.isDead && health.hp == 0, "duplicate death and ordinary HP cannot revive terminal state");
            Receive(EntityComponentType.HealthReset, 4, 100);
            Require(!health.isDead && health.hp == 100, "explicit newer positive reset restores life");
            item.SaveState();
            Require(SceneData.instance.FindMyState(item.persistentBoolData)?.activated == false,
                "native save callback follows restored life");
            Receive(EntityComponentType.Death, 5);
            // Inspect the native reset in an isolated SceneData, so no live
            // arena or unrelated semi-persistent objects are modified.
            var original = SceneData.instance;
            try {
                var isolated = (SceneData)Activator.CreateInstance(typeof(SceneData), true);
                SceneData.instance = isolated;
                isolated.SaveMyState(item.persistentBoolData);
                isolated.ResetSemiPersistentItems();
                Require(!isolated.FindMyState(item.persistentBoolData).activated, "native semi-persistent reset clears the death");
            } finally { SceneData.instance = original; }
            log("PASS persistence boundaries: nonterminal zero HP, terminal without effects, duplicate/stale HP, explicit life reset, native semi-persistent reset");
        } finally {
            component?.Destroy();
            if (item != null) SceneData.instance.persistentBoolItems.Remove(item.persistentBoolData);
            LocalBattleCompletion.ResetDeath(0x7fff000000000001);
            Object.DestroyImmediate(owner);
        }
    }
    private static void Require(bool value, string label) { if (!value) throw new Exception("Persistence fixture: " + label); }
}
