using System;
using System.Collections;
using System.IO;
using System.Linq;
using System.Reflection;
using Hkmp.Game.Client.Entity;
using Hkmp.Game.Client.Save;
using HutongGames.PlayMaker.Actions;
using MonoMod.RuntimeDetour;
using UnityEngine;
using Object = UnityEngine.Object;

namespace HollowConnect.Smoke;

public sealed partial class ProgressProbe {
    private void DefenderBarrierDump(string label) {
        Mark("DEFENDER BARRIER " + label + " hero=" + HeroController.instance.transform.position);
        foreach (var fsm in Object.FindObjectsOfType<PlayMakerFSM>(true).Where(f => f.gameObject.scene.name.StartsWith("Waterways_05") &&
            (f.name.Contains("Wall") || f.name.Contains("Gate") || f.name.Contains("Battle") || f.name.Contains("CamLock")))) {
            Mark("DEFENDER FSM " + fsm.gameObject.scene.name + "/" + EntityIdentity.Path(fsm.transform) + "/" + fsm.FsmName +
                "=" + fsm.ActiveStateName + " active=" + fsm.gameObject.activeInHierarchy + " pos=" + fsm.transform.position +
                " localBarrier=" + EntityRegistry.IsLocalOnlyBarrier(fsm.gameObject));
            Dump(fsm);
        }
        foreach (var collider in Object.FindObjectsOfType<Collider2D>(true).Where(c => c.gameObject.scene.name.StartsWith("Waterways_05") &&
            (c.name.Contains("Wall") || c.name.Contains("Gate") || c.name.Contains("Battle") ||
             c.transform.parent != null && c.transform.parent.name.Contains("Wall"))))
            Mark("DEFENDER COLLIDER " + collider.gameObject.scene.name + "/" + EntityIdentity.Path(collider.transform) + " enabled=" +
                collider.enabled + " active=" + collider.gameObject.activeInHierarchy + " trigger=" + collider.isTrigger + " bounds=" + collider.bounds);
    }

    private IEnumerator DefenderBarrierChecks() {
        var mode = Environment.GetEnvironmentVariable("HOLLOWCONNECT_DEFENDER_BARRIER_CHECK");
        if (mode == "inspect") {
            yield return Room("Waterways_05");
            DefenderBarrierDump("entry");
            ResourceCheck(!PlayerData.instance.GetBool("defeatedDungDefender"), "barrier diagnostic preserves live encounter");
            yield return ResourceBarrier("defender-barrier-inspected");
            yield break;
        }
        var first = role == (Environment.GetEnvironmentVariable("HOLLOWCONNECT_DEFENDER_FIRST") ?? "host");
        var late = mode == "late";
        var manager = Field<EntityManager>(engine.ClientManager, "_entityManager");
        var hero = HeroController.instance;
        On.HutongGames.PlayMaker.Fsm.hook_EnterState trace = (orig, fsm, state) => {
            if (fsm.GameObject != null && fsm.GameObject.scene.name.StartsWith("Waterways_05") &&
                (fsm.GameObject.name.Contains("Gate") || fsm.Name == "Battle Scene" || fsm.Name == "Corpse"))
                File.AppendAllText(Path.Combine(root, role + ".defender-states.txt"), DateTime.UtcNow.ToString("O") + " " +
                    EntityIdentity.Path(fsm.GameObject.transform) + "/" + fsm.Name + " => " + state.Name + "\n");
            orig(fsm, state);
        };
        On.HutongGames.PlayMaker.Fsm.EnterState += trace;
        try {
            if (!first) yield return Until(() => Has(late ? "defender.settled" : "defender.first"), "first entrant prepared before second arrives", 80);
            Enter("Waterways_05");
            yield return Until(() => AliveScene("Waterways_05") && engine.ClientManager.SceneSnapshotReady && manager.IsSceneHostDetermined,
                "native Defender room and snapshot ready");
            Freeze();
            ResourceCheck(manager.IsSceneHost == first, "requested first entrant owns boss authority");
            var battle = Object.FindObjectsOfType<PlayMakerFSM>().Single(f => f.name == "Battle Scene" && f.FsmName == "Battle Scene");
            var gate = Object.FindObjectsOfType<Collider2D>().Single(c => c.name == "Battle Gate (1)" && c.gameObject.scene.name == "Waterways_05");
            var wallSaver = battle.transform.Find("Wall Saver").gameObject;
            var shortcut = Object.FindObjectsOfType<PlayMakerFSM>().Single(f => f.name == "One Way Wall" && f.FsmName == "break_floor");
            var shortcutKey = new PersistentItemKey { Id = "One Way Wall", SceneName = "Waterways_05" };
            ResourceCheck(SaveDataMapping.Instance.PersistentBoolVarProperties[shortcutKey].Sync && !EntityRegistry.IsLocalOnlyBarrier(shortcut.gameObject),
                "breakable shortcut keeps world progress synchronization");
            hero.transform.position = new Vector3(56, 12.5f, hero.transform.position.z);
            hero.GetComponent<Rigidbody2D>().velocity = Vector2.zero;
            Physics2D.SyncTransforms();
            if (first) Flag("defender.first");
            Flag(role + ".defender-entry");
            if (!late) yield return Until(() => Has(ResourceOther + ".defender-entry"), "both endpoints remain outside the native trigger");
            if (first) {
                hero.transform.position = battle.GetComponent<Collider2D>().bounds.center;
                hero.GetComponent<Rigidbody2D>().velocity = Vector2.zero; Physics2D.SyncTransforms();
                yield return Until(() => battle.ActiveStateName == "Dung Start" && wallSaver.activeInHierarchy && gate.enabled,
                    "actual native trigger closes authority gate and activates Wall Saver", 15);
                Flag("defender.fighting");
            } else if (!late) {
                yield return Until(() => Has("defender.fighting"), "authority starts battle while second hero remains outside");
                yield return new WaitForSecondsRealtime(1);
                ResourceCheck(battle.ActiveStateName == "Idle" && !wallSaver.activeInHierarchy && !gate.enabled,
                    "remote battle cannot close second player's local entrance or Wall Saver");
                yield return DefenderWalkGate(gate, 1, "second player walks through live encounter entrance");
                hero.transform.position = battle.GetComponent<Collider2D>().bounds.center;
                hero.GetComponent<Rigidbody2D>().velocity = Vector2.zero; Physics2D.SyncTransforms();
                yield return Until(() => battle.ActiveStateName == "Dung Start" && wallSaver.activeInHierarchy && gate.enabled,
                    "second player's own collision closes only their local battle", 15);
                Flag("defender.second-triggered");
            }
            if (first) {
                if (!late) yield return Until(() => Has("defender.second-triggered"), "second player entered before boss death");
                yield return new WaitForSecondsRealtime(4);
                var boss = Object.FindObjectsOfType<HealthManager>().Single(h => h.name == "Dung Defender");
                ResourceCheck(!PlayerData.instance.GetBool("defeatedDungDefender"), "native battle remains live before terminal damage");
                boss.ApplyExtraDamage(boss.hp + 1);
            }
            yield return Until(() => PlayerData.instance.GetBool("defeatedDungDefender") && PlayerData.instance.GetBool("killedDungDefender"),
                "native Defender death flags converge", 20);
            if (first || !late) {
                yield return Until(() => battle.ActiveStateName == "End" && !wallSaver.activeInHierarchy && !gate.enabled,
                    "native corpse completion clears local battle barriers", 25);
                if (first) Flag("defender.settled");
            } else {
                ResourceCheck(!Object.FindObjectsOfType<HealthManager>().Any(h => h.name.StartsWith("Dung Defender") && h.hp > 0),
                    "late entrant has no living story boss");
                ResourceCheck(!wallSaver.activeInHierarchy && !gate.enabled, "defeated encounter is passable on late entry");
                hero.transform.position = battle.GetComponent<Collider2D>().bounds.center;
                hero.GetComponent<Rigidbody2D>().velocity = Vector2.zero; Physics2D.SyncTransforms();
                yield return new WaitForSecondsRealtime(2);
                ResourceCheck(!wallSaver.activeInHierarchy && !gate.enabled && battle.ActiveStateName == "Activate",
                    "late player's collision cannot restart the defeated battle or reclose barriers");
            }
            yield return Until(() => hero.acceptingInput && !hero.controlReqlinquished, "native completion restores control", 15);
            yield return DefenderWalkGate(gate, -1, "hero walks out through actual completed left battle gate");
            var rightGate = Object.FindObjectsOfType<Collider2D>(true).SingleOrDefault(c => c.name == "Battle Gate" &&
                c.gameObject.scene.name == "Waterways_05");
            yield return DefenderWalkGate(rightGate, 1, "hero walks out through completed right battle gate");
            yield return ResourceBarrier("defender-barriers-complete");
        } finally {
            DefenderBarrierDump("final");
            On.HutongGames.PlayMaker.Fsm.EnterState -= trace;
        }
    }

    private IEnumerator DefenderWalkGate(Collider2D gate, float direction, string label) {
        ResourceCheck(gate == null || !gate.enabled || !gate.gameObject.activeInHierarchy, label + " collider is open or natively removed");
        var hero = HeroController.instance;
        var at = gate != null ? gate.transform.position : new Vector3(92.3f, 8.5f, 0);
        var x = at.x;
        // Place at the doorway height, then use the original movement and
        // collision. This verifies the gate, not the entire platforming route.
        hero.transform.position = new Vector3(x - direction * 2, at.y, hero.transform.position.z);
        hero.GetComponent<Rigidbody2D>().velocity = Vector2.zero; hero.AffectedByGravity(false); Physics2D.SyncTransforms();
        using (new Hook(typeof(HeroController).GetMethod("LookForInput", BindingFlags.Instance | BindingFlags.NonPublic),
            new Action<Action<HeroController>, HeroController>((orig, self) => { orig(self); if (self.acceptingInput) self.move_input = direction; })))
            yield return Until(() => (hero.transform.position.x - x) * direction > 1, label, 5);
    }
}
