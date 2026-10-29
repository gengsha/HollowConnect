using UnityEngine;
using System.Reflection;

namespace Hkmp.Game.Client.Entity;

// Network replicas represent the native object in this player's world. Unity's
// generated clone name and destination scene are not native save identities.
internal static class EntityPersistence {
    private static readonly FieldInfo GetSaveState = typeof(PersistentBoolItem).GetField("OnGetSaveState", BindingFlags.Instance | BindingFlags.NonPublic);
    internal static void BindReplica(GameObject source, GameObject replica) {
        BindItems(source, replica);
        // Called immediately after Instantiate, before registered children are
        // removed. Sibling indices also distinguish identically named children.
        for (var i = 0; i < source.transform.childCount; i++)
            BindReplica(source.transform.GetChild(i).gameObject, replica.transform.GetChild(i).gameObject);
    }

    internal static void BindItems(GameObject source, GameObject replica) {
        var sourceBools = source.GetComponents<PersistentBoolItem>();
        var replicaBools = replica.GetComponents<PersistentBoolItem>();
        for (var i = 0; i < sourceBools.Length && i < replicaBools.Length; i++) {
            var item = sourceBools[i];
            var data = item.persistentBoolData;
            replicaBools[i].persistentBoolData = new PersistentBoolData {
                id = string.IsNullOrEmpty(data.id) ? item.name : data.id,
                sceneName = string.IsNullOrEmpty(data.sceneName) ? global::GameManager.GetBaseSceneName(source.scene.name) : data.sceneName,
                activated = data.activated,
                semiPersistent = item.semiPersistent
            };
        }
        var sourceInts = source.GetComponents<PersistentIntItem>();
        var replicaInts = replica.GetComponents<PersistentIntItem>();
        for (var i = 0; i < sourceInts.Length && i < replicaInts.Length; i++) {
            var item = sourceInts[i];
            var data = item.persistentIntData;
            replicaInts[i].persistentIntData = new PersistentIntData {
                id = string.IsNullOrEmpty(data.id) ? item.name : data.id,
                sceneName = string.IsNullOrEmpty(data.sceneName) ? global::GameManager.GetBaseSceneName(source.scene.name) : data.sceneName,
                value = data.value,
                semiPersistent = item.semiPersistent
            };
        }
    }

    // Only a confirmed terminal Death calls this. Save through the native item
    // and its HealthManager callback, including semi-persistent/reset semantics.
    // Replica death effects can be removed when its corpse is networked itself.
    internal static void SaveTerminalDeath(HealthManager native) {
        if (native == null || !native.isDead) return;
        var item = native.GetComponent<PersistentBoolItem>();
        if (item == null) return;
        if (GetSaveState.GetValue(item) != null) {
            item.SaveState();
            return;
        }
        // A terminal late snapshot can reach an inactive native object before
        // HealthManager.Awake has registered its save callback. Apply that
        // callback's native semantics without waking the original boss/FSMs.
        if (global::GameManager.instance.GetCurrentMapZone() == "COLOSSEUM") return;
        var data = item.persistentBoolData;
        if (string.IsNullOrEmpty(data.id)) data.id = item.name;
        if (string.IsNullOrEmpty(data.sceneName)) data.sceneName = global::GameManager.GetBaseSceneName(item.gameObject.scene.name);
        data.semiPersistent = item.semiPersistent;
        data.activated = native.isDead;
        SceneData.instance.SaveMyState(data);
    }
}
