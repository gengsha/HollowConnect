// Local continuation for the native Hollow Knight corpse after networked
// combat ends. The clone is enabled only after an authoritative detached
// corpse seed has been fully applied.
using System;
using System.Linq;
using Hkmp.Fsm;
using Hkmp.Util;
using HutongGames.PlayMaker;
using HutongGames.PlayMaker.Actions;
using UnityEngine;

namespace Hkmp.Game.Client.Entity;

// One exact native post-battle subtree becomes a personal scene object.
// Control and Phase Control on the live boss always stay network-controlled.
internal sealed class FinalBossCorpseContinuation {
    private readonly GameObject corpse;
    private readonly PlayMakerFSM corpseFsm;
    private readonly PlayMakerFSM[] dreamEntries;
    private readonly (Rigidbody2D Body, RigidbodyType2D Type, float Gravity, bool Simulated, bool Contacts)[] bodies;
    private readonly string path;
    private readonly int sceneHandle;
    private readonly bool? killed;
    private readonly int? kills;
    private readonly string story;
    private bool seed, die, rejected, subscribed;
    internal bool Released { get; private set; }
    internal string Path => path;

    private FinalBossCorpseContinuation(GameObject native, GameObject replica, string partPath,
        PlayMakerFSM phase, PlayMakerFSM fsm, GameObject hostRoot, GameObject clientRoot) {
        corpse = replica; corpseFsm = fsm; path = partPath; sceneHandle = replica.scene.handle;
        // Capture BEFORE the entity constructor freezes all cloned rigidbodies.
        bodies = native.GetComponentsInChildren<Rigidbody2D>(true).Select(source => {
            var relative = EntityIdentity.Path(source.transform, native.transform);
            var target = relative.Length == 0 ? replica : EntityReferences.Walk(replica, relative);
            return (target?.GetComponent<Rigidbody2D>(), source.bodyType, source.gravityScale,
                source.simulated, source.useFullKinematicContacts);
        }).ToArray();
        dreamEntries = hostRoot.GetComponentsInChildren<PlayMakerFSM>(true)
            .Concat(clientRoot.GetComponentsInChildren<PlayMakerFSM>(true))
            .Where(f => f.name == "Dream Enter" && f.FsmName == "Control").ToArray();
        // These verified Die actions are personal defeat metadata, never a
        // copied boss state machine or an ending selection.
        var death = phase.FsmStates.Single(s => s.Name == "Die");
        killed = death.Actions.OfType<SetPlayerDataBool>()
            .Where(a => a.Enabled && a.boolName.Value == "killedHollowKnight").Select(a => (bool?)a.value.Value).SingleOrDefault();
        kills = death.Actions.OfType<SetPlayerDataInt>()
            .Where(a => a.Enabled && a.intName.Value == "killsHollowKnight").Select(a => (int?)a.value.Value).SingleOrDefault();
        story = death.Actions.OfType<HutongGames.PlayMaker.Actions.SendMessage>()
            .Where(a => a.Enabled && a.functionCall.FunctionName == "StoryRecord_defeated")
            .Select(a => a.functionCall.StringParameter.Value).SingleOrDefault();
        subscribed = true; MonoBehaviourUtil.Instance.OnUpdateEvent += TryStart;
    }

    internal static FinalBossCorpseContinuation TryCreate(EntityType type, string scene,
        GameObject hostRoot, GameObject clientRoot) {
        if (type != EntityType.HollowKnight || scene != "Room_Final_Boss_Core") return null;
        var phase = hostRoot.GetComponents<PlayMakerFSM>().SingleOrDefault(f => f.FsmName == "Phase Control");
        var nativeFsm = hostRoot.GetComponentsInChildren<PlayMakerFSM>(true)
            .SingleOrDefault(f => f.name == "Boss Corpse" && f.FsmName == "Corpse");
        if (phase == null || nativeFsm == null || !phase.FsmStates.Any(s => s.Name == "Die")) return null;
        // If Die already detached the native corpse before this entity was
        // registered, the freshly cloned client root has no matching corpse.
        // Keep this boundary explicit rather than manufacturing a second
        // corpse or guessing among unrelated scene roots.
        var partPath = EntityIdentity.Path(nativeFsm.transform, hostRoot.transform);
        var replica = EntityReferences.Walk(clientRoot, partPath);
        var fsm = replica?.GetComponents<PlayMakerFSM>().SingleOrDefault(f => f.FsmName == "Corpse");
        if (fsm == null || partPath.Length == 0 || nativeFsm.GetComponent<Rigidbody2D>() == null) return null;
        return new FinalBossCorpseContinuation(nativeFsm.gameObject, replica, partPath, phase, fsm, hostRoot, clientRoot);
    }

    internal void ObservePhase(string fsm, string state) {
        if (fsm != "Phase Control" || state != "Die") return;
        die = true;
        TryStart();
    }
    // Call only after a fully decoded/applied CombatState root node. The
    // native asset activates and detaches Battle Corpse only in Die; this is
    // durable proof even when a late room snapshot has no fresh Die event.
    internal void ObserveSeed(string nodePath, bool active, string parentKey) {
        if (nodePath == path && active && parentKey.Length == 0) {
            seed = true;
            TryStart();
        }
    }
    internal bool IsPersonalPath(string nodePath) => seed && !rejected &&
        (nodePath == path || nodePath.StartsWith(path + "/", StringComparison.Ordinal));
    internal bool KeepClientPart(GameObject go) => seed && !rejected && corpse != null && go != null &&
        (go == corpse || go.transform.IsChildOf(corpse.transform));

    internal void TryStart() {
        if (Released || rejected) return;
        if (corpse == null || !corpse.scene.IsValid() || !corpse.scene.isLoaded || corpse.scene.handle != sceneHandle) { Cancel(); return; }
        // PlayMaker keeps the last state name on disabled scene-entry FSMs;
        // only an actually running, active entry controller can race the
        // ending continuation.
        if (dreamEntries.Any(f => f != null && f.enabled && f.gameObject.activeInHierarchy &&
            f.ActiveStateName is "Take Control" or "Impact" or "Dream Box Down" or "Change Scene")) {
            rejected = true; Cancel(); return;
        }
        // Never interrupt an already selected dream/other native transition.
        if (global::GameManager.instance == null || global::GameManager.instance.sceneName != "Room_Final_Boss_Core" ||
            global::GameManager.instance.IsInSceneTransition || CoopContext.Resetting ||
            CoopContext.LocalRescueDown?.Invoke() == true || HeroController.instance == null ||
            HeroController.instance.cState.dead || PlayerData.instance.health <= 0) return;
        // An inactive root/Die state alone is insufficient: it has no final
        // local pose. Wait for the authoritative active, detached corpse seed.
        if (!die || !seed || corpse.transform.parent != null || !corpse.activeSelf) return;
        Released = true; Cancel();
        foreach (var motion in corpse.GetComponentsInChildren<PositionInterpolation>(true)) { motion.Flush(); motion.enabled = false; }
        foreach (var body in bodies) {
            if (body.Body == null) continue;
            body.Body.velocity = Vector2.zero; body.Body.angularVelocity = 0;
            body.Body.bodyType = body.Type; body.Body.gravityScale = body.Gravity;
            body.Body.simulated = body.Simulated; body.Body.useFullKinematicContacts = body.Contacts;
        }
        if (killed.HasValue) PlayerData.instance.SetBool("killedHollowKnight", killed.Value);
        if (kills.HasValue) PlayerData.instance.SetInt("killsHollowKnight", kills.Value);
        if (!string.IsNullOrEmpty(story)) global::GameManager.instance.gameObject.SendMessage("StoryRecord_defeated", story, SendMessageOptions.DontRequireReceiver);
        // Full native Init -> Burst -> Fall/LAND -> Set Knight Focus -> local
        // focus input -> Ending? selects local gotShadeCharm. No SetState,
        // ChangeToScene, DREAM FOCUS event, or live boss FSM is started here.
        // Corpse is a child FSM and therefore was not initialized by the
        // entity's root-level client-FSM pass. Rebind its local owner/child
        // references before allowing the native sequence to run.
        EntityInitializer.InitializeFsm(corpseFsm);
        corpseFsm.Fsm.RestartOnEnable = true;
        corpseFsm.enabled = true;
    }
    internal void EntityDestroyed() {
        // A final snapshot may precede host departure while the player is
        // down or entering. Keep only its scene-owned corpse/retry callback.
        // Unity destroys the detached corpse on THIS client's scene unload;
        // the next tick then removes the one pending callback as well.
        if (!seed || rejected || Released) Cancel();
    }
    private void Cancel() {
        if (!subscribed) return;
        subscribed = false; MonoBehaviourUtil.Instance.OnUpdateEvent -= TryStart;
    }
}
