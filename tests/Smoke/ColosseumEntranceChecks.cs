using System;
using System.Collections;
using System.IO;
using System.Linq;
using System.Reflection;
using Hkmp.Game.Client.Entity;
using HutongGames.PlayMaker;
using UnityEngine;
using Object = UnityEngine.Object;

namespace HollowConnect.Smoke;

public sealed partial class ColosseumProbe {
    private void DumpEntrance() {
        var lines = new System.Collections.Generic.List<string>();
        foreach (var fsm in Object.FindObjectsOfType<PlayMakerFSM>(true).Where(f => f.gameObject.scene.name == ActiveScene &&
            (f.name.IndexOf("gate", StringComparison.OrdinalIgnoreCase) >= 0 || f.name.IndexOf("entry", StringComparison.OrdinalIgnoreCase) >= 0 ||
             f.name.StartsWith("Colosseum Manager")))) {
            lines.Add("FSM " + WallPath(fsm.transform) + "/" + fsm.FsmName + "=" + fsm.ActiveStateName + " enabled=" + fsm.enabled +
                " active=" + fsm.gameObject.activeInHierarchy + " pos=" + fsm.transform.position + " owner=" + EntityReferences.Owner(fsm.gameObject)?.Type);
            foreach (var state in fsm.FsmStates) {
                lines.Add("STATE " + state.Name + " -> " + string.Join(",", state.Transitions.Select(t => t.EventName + ":" + t.ToState)));
                foreach (var action in state.Actions) lines.Add("ACTION " + action.GetType().Name + " " + string.Join(";", action.GetType()
                    .GetFields(BindingFlags.Instance | BindingFlags.Public).Select(f => f.Name + "=" + Describe(f.GetValue(action)))));
            }
        }
        foreach (var c in Object.FindObjectsOfType<Collider2D>(true).Where(c => c.gameObject.scene.name == ActiveScene &&
            (c.name.IndexOf("gate", StringComparison.OrdinalIgnoreCase) >= 0 || c.name.IndexOf("entry", StringComparison.OrdinalIgnoreCase) >= 0 ||
             c.bounds.center.x < 95 && c.bounds.center.y < 30)))
            lines.Add("COLLIDER " + WallPath(c.transform) + " active=" + c.gameObject.activeInHierarchy + " enabled=" + c.enabled + " trigger=" + c.isTrigger + " bounds=" + c.bounds);
        lines.Add("HERO " + HeroController.instance.transform.position);
        File.WriteAllLines(Path.Combine(root, role + "-entrance.inspect.txt"), lines);
        Log("ENTRANCE native schema captured " + trial + " authority=" + ArenaManager.IsSceneHost);
    }
    private IEnumerator ColosseumEntranceChecks() {
        DumpEntrance();
        if (testCase is "entrance-late" or "entrance-live") {
            if (testCase == "entrance-live") {
                if (role == first) yield return Until(() => Has((host ? "guest" : "host") + ".arena-ready"), "second entrant is in corridor before first entry");
                else yield return Until(() => Has(first + ".native-wave"), "first entrant starts waves while second waits outside");
            }
            var hero = HeroController.instance;
            var gate = Object.FindObjectsOfType<PlayMakerFSM>().Single(f => f.name == "Colosseum Gate" && f.FsmName == "Rise");
            var collider = gate.GetComponent<Collider2D>();
            LocalBarrierChecks.Run(Log);
            Log("ENTRANCE BEFORE WALK hero=" + hero.transform.position + " gate=" + gate.ActiveStateName + " solid=" + collider.enabled);
            Require(hero.transform.position.x < gate.transform.position.x - 2, "entrant starts outside the physical arena gate");
            using (new MonoMod.RuntimeDetour.Hook(typeof(HeroController).GetMethod("LookForInput", BindingFlags.Instance | BindingFlags.NonPublic),
                new Action<Action<HeroController>, HeroController>((orig, self) => { orig(self); if (self.acceptingInput) self.move_input = 1f; }))) {
                var deadline = Time.realtimeSinceStartup + 18;
                while (hero.transform.position.x < 110 && Time.realtimeSinceStartup < deadline) yield return null;
            }
            hero.move_input = 0;
            Log("ENTRANCE AFTER WALK hero=" + hero.transform.position + " gate=" + gate.ActiveStateName + " solid=" + collider.enabled +
                " manager=" + ArenaState("Manager") + " battle=" + ArenaState("Battle Control"));
            if (hero.transform.position.x < 110) {
                foreach (var hit in Physics2D.RaycastAll(hero.transform.position, Vector2.right, 3))
                    Log("ENTRANCE BLOCKER " + WallPath(hit.collider.transform) + " layer=" + hit.collider.gameObject.layer + " distance=" + hit.distance);
            }
            Require(hero.transform.position.x >= 110, "entrant physically crosses the native arena gate and centre trigger");
            yield return Until(() => gate.ActiveStateName == "Close" && collider.enabled, "local entry gate closes after its own player enters", 6);
            if (role == first) {
                yield return Until(() => ArenaState("Battle Control").StartsWith("Wave "), "physical entry starts native combat waves", 22);
                Flag(role + ".native-wave");
            }
            Flag(role + ".physically-entered");
            yield return Until(() => Has((host ? "guest" : "host") + ".physically-entered"), "both entrants physically crossed the arena entrance");
            var localWalls = LocalSideWalls();
            var body = hero.GetComponent<Collider2D>();
            Require(localWalls.Length == 2 && localWalls.All(w => w.GetComponentsInChildren<Collider2D>(true)
                    .Any(c => WallCollider(c) && !Physics2D.GetIgnoreCollision(body, c))),
                "both shared walls retain solid collision with the local knight after entry");
            SaveWallObservation("physical-entry");
            var deadlineNext = Time.realtimeSinceStartup + (trial == "Gold" ? 240 : 30);
            var targetWave = trial == "Gold" ? "Wave 30" : "Wave 3";
            while (ArenaState("Battle Control") != targetWave && Time.realtimeSinceStartup < deadlineNext) {
                var state = ArenaState("Battle Control");
                if (state.StartsWith("Wave ") && int.TryParse(state.Substring(5), out var wave)) wallWave = wave;
                ApproachCurrentWallWave();
                DamageCurrentWallWave();
                yield return new WaitForSecondsRealtime(.15f);
            }
            Require(ArenaState("Battle Control") == targetWave, "original enemies and cages advance native waves to " + targetWave);
            if (trial == "Gold") {
                yield return new WaitForSecondsRealtime(5);
                SaveWallObservation("physical-entry-contracted");
                Require(LocalSideWalls().All(w => w.GetComponentsInChildren<Collider2D>(true)
                        .Any(c => WallCollider(c) && !Physics2D.GetIgnoreCollision(body, c))),
                    "Gold contracted walls still collide after physical late entry");
            }
            Flag(role + ".entry-waves-observed");
            yield return Until(() => Has((host ? "guest" : "host") + ".entry-waves-observed"), "both endpoints observe native wave continuation", 30);
            if (role == first) {
                Log("FIXTURE completion-event only: execute original Geo Pool/Open Gates action; no full trial or reward claim");
                var pool = ArenaEntity.Object.Host.GetComponents<PlayMakerFSM>().Single(f => f.FsmName == "Geo Pool");
                pool.FsmStates.Single(s => s.Name == "Open Gates").Actions.OfType<HutongGames.PlayMaker.Actions.SendEventByName>()
                    .Single(a => a.sendEvent.Value == "GATE OPEN").OnEnter();
            }
            yield return Until(() => gate.ActiveStateName == "Open" && !collider.enabled, "native completion action opens each local entrance gate", 8);
        } else Require(testCase == "entrance-schema", "entrance fixture schema preflight selected");
        Flag(role + ".entrance-checked");
        yield return Until(() => Has((host ? "guest" : "host") + ".entrance-checked"), "both native entrance schemas captured");
    }
}
