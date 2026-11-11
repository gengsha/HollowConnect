using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using Hkmp.Game.Client.Entity;
using HollowConnect.Core;
using HutongGames.PlayMaker;
using HutongGames.PlayMaker.Actions;
using UnityEngine;
using Object = UnityEngine.Object;

namespace HollowConnect.Smoke;

public sealed partial class ColosseumProbe {
    private ResourceClaims ArenaReceipts {
        get {
            var service = typeof(HollowConnect.CoopRuntime).GetFields(BindingFlags.Instance | BindingFlags.NonPublic)
                .Single(f => f.FieldType.Name == "ResourceShareService").GetValue(runtime);
            return (ResourceClaims)Field<Delegate>(service, "local").DynamicInvoke();
        }
    }

    // Whole native Silver trial, including the real pickup and physical exit.
    // Unlike the older Bronze reward fixture, this never substitutes a scene
    // transition for the Geo Pool's SHINY PICKED UP -> Open Gates continuation.
    private IEnumerator SilverRewardFlow() {
        const string receipt = "ore:colosseumSilver";
        var pd = PlayerData.instance;
        var hero = HeroController.instance;
        var initialOre = pd.GetInt("ore");
        var handoffMode = Environment.GetEnvironmentVariable("HOLLOWCONNECT_COLOSSEUM_REWARD_HANDOFF") ?? "none";
        var handoff = handoffMode == "1";
        var midPickup = handoffMode == "mid-pickup";
        var anyHandoff = handoff || midPickup;
        var survivor = first == "host" ? "guest" : "host";
        var collector = handoff ? survivor : Environment.GetEnvironmentVariable("HOLLOWCONNECT_COLOSSEUM_COLLECTOR") ?? first;
        Require(collector == "host" || collector == "guest", "Silver native collector is an actual room endpoint");
        Require(!midPickup || first == "host" && collector == "guest" && HollowConnect.CoopMod.Settings.ShareProgress,
            "Silver mid-pickup handoff uses host authority, guest collector and shared progress");
        Log("SILVER REWARD MODE handoff=" + handoffMode + " first=" + first + " collector=" + collector);
        Require(!pd.GetBool("colosseumSilverCompleted") && !ArenaReceipts.Contains(receipt),
            "Silver starts with neither a completion marker nor a reward receipt");
        var pool = ArenaEntity.Object.Host.GetComponents<PlayMakerFSM>().Single(f => f.FsmName == "Geo Pool");
        var shiny = LocalArena.transform.Find("Shiny Item");
        Require(shiny != null, "Silver native reward child exists under " + LocalArena.name);
        var control = shiny.GetComponents<PlayMakerFSM>().SingleOrDefault(f => f.FsmName == "Shiny Control");
        var inspect = shiny.GetComponentsInChildren<PlayMakerFSM>(true).SingleOrDefault(f => f.FsmName == "inspect");
        Require(control != null && inspect != null, "Silver native shiny and inspect FSMs exist");
        var awaitPickup = pool.FsmStates.Single(s => s.Name == "Give Shiny?");
        Require(awaitPickup.Transitions.Any(t => t.EventName == "SHINY PICKED UP" && t.ToState == "Open Gates") &&
            pool.FsmStates.Single(s => s.Name == "Open Gates").Actions.OfType<SendEventByName>()
                .Any(a => a.Enabled && a.sendEvent.Value == "GATE OPEN"),
            "Silver original reward controller owns its pickup-to-open-gates continuation");
        flow.Capture("Silver reward preflight", true); flow.CaptureAuthority(ArenaManager);
        Flag(role + ".silver-preflight");
        yield return Until(() => Has((host ? "guest" : "host") + ".silver-preflight"), "both Silver reward schemas preflighted");
        if (ArenaManager.IsSceneHost) {
            ArenaEntity.Object.Host.GetComponents<PlayMakerFSM>().Single(f => f.FsmName == "Battle Control").Fsm.ManualUpdate = false;
            Log("SILVER resumed original held first wave; no reward state or completion flag substituted");
        }
        var deadline = Time.realtimeSinceStartup + 180;
        var previous = "";
        var stateSince = Time.realtimeSinceStartup;
        var mimicBottleVisited = false;
        while (ArenaState("Battle Control") != "End") {
            var state = ArenaState("Battle Control");
            if (state != previous) {
                Log("SILVER ACTUAL WAVE " + state); previous = state; stateSince = Time.realtimeSinceStartup;
            }
            if (Time.realtimeSinceStartup >= deadline || Time.realtimeSinceStartup - stateSince >= 25) {
                SaveWallActors("silver-reward-timeout");
                TraceSilverMimicBottle("timeout");
                throw new TimeoutException("Silver native waves stalled: battle=" + state + " manager=" + ArenaState("Manager"));
            }
            if (state.StartsWith("Wave ", StringComparison.Ordinal) &&
                int.TryParse(new string(state.Substring(5).TakeWhile(char.IsDigit).ToArray()), out var number)) wallWave = number;
            if (state == "Wave 20" && ArenaManager.IsSceneHost && !mimicBottleVisited) {
                mimicBottleVisited = true;
                yield return AttackSilverMimicBottle();
            }
            ApproachCurrentWallWave();
            DamageCurrentWallWave();
            if (state == "Wave 20" && mimicBottleVisited) DamageReleasedSilverMimic();
            yield return new WaitForSecondsRealtime(.15f);
        }
        flow.Capture("Silver actual waves completed", true); flow.CaptureAuthority(ArenaManager);
        yield return Until(() => ArenaState("Geo Pool") is "Give Shiny?" or "Open Gates", "Silver original reward branch reached", 30);
        Log("SILVER REWARD state=" + ArenaState("Geo Pool") + " shiny=" + shiny.gameObject.activeInHierarchy +
            " control=" + control.ActiveStateName + " enabled=" + control.enabled + " inspect=" + inspect.ActiveStateName +
            " pdCompleted=" + pd.GetBool("colosseumSilverCompleted") + " ore=" + pd.GetInt("ore") + " receipt=" + ArenaReceipts.Contains(receipt));
        // The second entrant can still be in the entrance corridor after the
        // shared wave driver finishes. Observe reward/exit from the actual
        // arena floor, where the local entry gate sees this player's entry.
        // Do this before either pickup can emit the original GATE OPEN.
        var rewardGate = Object.FindObjectsOfType<PlayMakerFSM>().Single(f =>
            f.gameObject.scene.name == ActiveScene && f.name == "Colosseum Gate" && f.FsmName == "Rise");
        var rewardGround = hero.FindGroundPoint(new Vector3(108 + (host ? -2 : 2), 12, 0), true);
        Log("SILVER REWARD STAGING before hero=" + hero.transform.position + " gate=" + WallPath(rewardGate.transform) +
            " state=" + rewardGate.ActiveStateName + " collider=" + rewardGate.GetComponent<Collider2D>().enabled);
        hero.transform.position = rewardGround + Vector3.up * .1f;
        hero.move_input = 0; hero.GetComponent<Rigidbody2D>().velocity = Vector2.zero;
        hero.AffectedByGravity(true); Physics2D.SyncTransforms();
        yield return Until(() => hero.cState.onGround && hero.acceptingInput &&
            hero.transform.position.x > rewardGate.transform.position.x + 2, "Silver reward observer stands inside native arena", 5);
        Log("SILVER REWARD STAGING after hero=" + hero.transform.position + " gateState=" + rewardGate.ActiveStateName +
            " collider=" + rewardGate.GetComponent<Collider2D>().enabled + " pool=" + ArenaState("Geo Pool"));
        Flag(role + ".silver-reward-staged");
        yield return Until(() => Has((host ? "guest" : "host") + ".silver-reward-staged"),
            "both Silver reward observers are inside arena before pickup");
        if (anyHandoff) {
            Flag(role + ".silver-reward-ready");
            yield return Until(() => Has((host ? "guest" : "host") + ".silver-reward-ready"), "both peers at actual Silver reward before authority departure");
            if (role == first) {
                if (midPickup) {
                    yield return Until(() => Has(survivor + ".silver-pickup-started"),
                        "guest starts its actual Silver ore pickup before authority leaves", 25);
                    flow.Capture("Silver authority leaves during guest native pickup");
                }
                // Keep the server and unsaved profile alive through an actual
                // network departure. The survivor must finish its own native
                // pickup before this endpoint rejoins for resource catchup.
                engine.ClientManager.Disconnect();
                Flag(role + ".silver-authority-left");
                yield return Until(() => Has(survivor + ".silver-survivor-collected"), "survivor collects original ore after authority handoff", 60);
                yield return Connect();
                yield return Until(() => engine.ClientManager.SceneSnapshotReady && ArenaManager.IsSceneHostDetermined,
                    "departed Silver authority rejoins the live reward room", 15);
                Flag(role + ".silver-authority-rejoined");
            } else if (handoff) {
                yield return Until(() => Has(first + ".silver-authority-left") && ArenaManager.IsSceneHost,
                    "surviving player actually becomes Silver authority", 15);
                // Use the newly active original hierarchy; never continue
                // interacting with the retired visual replica.
                shiny = LocalArena.transform.Find("Shiny Item");
                control = shiny.GetComponents<PlayMakerFSM>().Single(f => f.FsmName == "Shiny Control");
                inspect = shiny.GetComponentsInChildren<PlayMakerFSM>(true).Single(f => f.FsmName == "inspect");
                flow.Capture("Silver authority changed while waiting for native pickup", true);
            }
        }
        if (role == collector || !HollowConnect.CoopMod.Settings.ShareProgress) {
            yield return Until(() => shiny != null && shiny.gameObject.activeInHierarchy && control.enabled &&
                control.ActiveStateName == "Idle", "Silver native ore pickup is available on collector " + role, 10);
            var ground = hero.FindGroundPoint(inspect.transform.position + Vector3.up * 2, true);
            hero.transform.position = ground + Vector3.up * .1f;
            hero.GetComponent<Rigidbody2D>().velocity = Vector2.zero;
            hero.AffectedByGravity(true); Physics2D.SyncTransforms();
            yield return Until(() => inspect.ActiveStateName == "In Range" && hero.cState.onGround &&
                hero.isHeroInPosition && hero.CanInspect(), "Silver actual ore inspect range", 10);
            yield return Press(InputHandler.Instance.inputActions.up);
            yield return Until(() => pd.GetInt("ore") == initialOre + 1, "Silver real ore pickup increments inventory once", 12);
            if (midPickup) {
                Require((!hero.acceptingInput || hero.controlReqlinquished || pd.GetBool("disablePause")) &&
                    control != null && control.ActiveStateName != "Idle",
                    "Silver guest has the real ore while its native pickup still owns control");
                Log("SILVER MID PICKUP before authority departure source=" + WallPath(shiny) +
                    " active=" + shiny.gameObject.activeInHierarchy + " control=" + control.ActiveStateName +
                    " enabled=" + control.enabled + " acceptingInput=" + hero.acceptingInput +
                    " relinquished=" + hero.controlReqlinquished + " disablePause=" + pd.GetBool("disablePause"));
                flow.Capture("Silver guest ore incremented with native pickup still in progress");
                Flag(role + ".silver-pickup-started");
                yield return Until(() => Has(first + ".silver-authority-left") && ArenaManager.IsSceneHost,
                    "Silver guest becomes authority during its actual native pickup", 15);
                // Keep the exact in-progress source reference. Rebinding and
                // collecting another shiny would hide a retired child that
                // never finishes releasing the player's native controls.
                Log("SILVER MID PICKUP after authority departure source=" + (shiny == null ? "destroyed" : WallPath(shiny)) +
                    " active=" + (shiny != null && shiny.gameObject.activeInHierarchy) +
                    " control=" + (control == null ? "destroyed" : control.ActiveStateName) +
                    " enabled=" + (control != null && control.enabled) + " acceptingInput=" + hero.acceptingInput +
                    " relinquished=" + hero.controlReqlinquished + " disablePause=" + pd.GetBool("disablePause"));
                flow.Capture("Silver guest authority changed during native pickup");
                flow.CaptureAuthority(ArenaManager);
            }
            var finish = Time.realtimeSinceStartup + 12;
            while ((!hero.acceptingInput || hero.controlReqlinquished || pd.GetBool("disablePause")) && Time.realtimeSinceStartup < finish) {
                yield return Press(InputHandler.Instance.inputActions.jump);
                yield return new WaitForSecondsRealtime(.2f);
            }
        } else yield return Until(() => pd.GetInt("ore") == initialOre + 1, "Silver shared ore reaches other endpoint", 30);
        Require(pd.GetInt("ore") == initialOre + 1 && ArenaReceipts.Contains(receipt) && pd.GetBool("colosseumSilverCompleted"),
            "Silver ore, native completion and exactly-once receipt agree");
        Require(hero.acceptingInput && !hero.controlReqlinquished && !pd.GetBool("disablePause"),
            "Silver reward returns input and pause before attempting to leave");
        if (anyHandoff && role == survivor) {
            Flag(role + ".silver-survivor-collected");
            yield return Until(() => Has(first + ".silver-authority-rejoined"), "departed authority reconnects for source catchup", 25);
        }
        Flag(role + ".silver-ore");
        yield return Until(() => Has((host ? "guest" : "host") + ".silver-ore"), "both Silver ore awards checked");
        // Colosseum Gate is a local-only barrier, so its own FSM and collider
        // are the evidence. The surrounding Geo Pool remains authoritative.
        var gate = rewardGate;
        Log("SILVER EXIT OBSERVATION hero=" + hero.transform.position + " gate=" + WallPath(gate.transform) +
            " state=" + gate.ActiveStateName + " enabled=" + gate.enabled + " collider=" + gate.GetComponent<Collider2D>().enabled +
            " pool=" + ArenaState("Geo Pool") + " authority=" + ArenaManager.IsSceneHost);
        yield return Until(() => ActiveScene == "Room_Colosseum_Silver" && !GameManager.instance.IsInSceneTransition &&
            ArenaState("Geo Pool") == "Open Gates" && gate != null && !gate.GetComponent<Collider2D>().enabled &&
            (gate.ActiveStateName == "Open" || gate.ActiveStateName == "Init"),
            "Silver native pickup continuation opens each exit gate", 10);
        flow.Capture("Silver actual reward opened exit", true);
        Flag(role + ".silver-gate-ready");
        yield return Until(() => Has((host ? "guest" : "host") + ".silver-gate-ready"),
            "both Silver local gates observed open before either player walks out");
        Log("SILVER PHYSICAL EXIT START hero=" + hero.transform.position + " gate=" + gate.transform.position +
            " state=" + gate.ActiveStateName + " collider=" + gate.GetComponent<Collider2D>().enabled);
        // Walk from the actual reward position through the collision gate and
        // the original left TransitionPoint; do not teleport across the gate.
        var exitStart = hero.transform.position;
        var leftmost = exitStart.x;
        using (new MonoMod.RuntimeDetour.Hook(typeof(HeroController).GetMethod("LookForInput", BindingFlags.Instance | BindingFlags.NonPublic),
            new Action<Action<HeroController>, HeroController>((orig, self) => {
                orig(self); leftmost = Mathf.Min(leftmost, self.transform.position.x);
                if (self.acceptingInput) self.move_input = -1f;
            }))) {
            yield return Until(() => GameManager.instance.IsInSceneTransition, "Silver physical walk reaches original exit transition", 30);
        }
        Log("SILVER PHYSICAL EXIT TRANSITION start=" + exitStart + " leftmost=" + leftmost + " hero=" + hero.transform.position);
        yield return Until(() => In("Room_Colosseum_01"), "Silver real exit returns to registration hall", 15);
        Require(pd.GetInt("ore") == initialOre + 1 && ArenaReceipts.Contains(receipt), "Silver physical exit preserves the single ore award");
        Flag(role + ".silver-exited");
        yield return Until(() => Has((host ? "guest" : "host") + ".silver-exited"), "both Silver players physically left arena");
        Log("FINAL PASS Silver full native waves, real ore pickup, shared receipt, input recovery and physical exit" +
            (midPickup ? "; authority departed during guest native pickup, original source restored control and departed player rejoined" :
                handoff ? "; authority departed at reward, survivor collected and departed player rejoined" : ""));
    }

    private Transform[] SilverMimicBottles() => Resources.FindObjectsOfTypeAll<Transform>().Where(t =>
        t.gameObject.scene.name == ActiveScene && t.name.StartsWith("Grub Mimic Bottle Col", StringComparison.Ordinal) &&
        !EntityReferences.IsReplica(t.gameObject)).ToArray();

    private void DamageReleasedSilverMimic() {
        if (!ArenaManager.IsSceneHost || ArenaState("Battle Control") != "Wave 20") return;
        foreach (var bottle in SilverMimicBottles().Where(t => t.gameObject.activeInHierarchy)) {
            var bottleControl = bottle.GetComponents<PlayMakerFSM>().SingleOrDefault(f => f.FsmName == "Bottle Control");
            var top = bottle.Find("Grub Mimic Top");
            var topControl = top?.GetComponents<PlayMakerFSM>().SingleOrDefault(f => f.FsmName == "Grub Control");
            var mimic = top?.Find("Grub Mimic");
            var hm = mimic?.GetComponent<HealthManager>();
            var control = mimic?.GetComponents<PlayMakerFSM>().SingleOrDefault(f => f.FsmName == "Grub Mimic");
            if (bottleControl == null || !bottleControl.enabled || bottleControl.ActiveStateName != "Destroy Self" ||
                topControl == null || !topControl.enabled || topControl.ActiveStateName != "Transform" ||
                hm == null || !hm.gameObject.activeInHierarchy || hm.gameObject.scene.name != ActiveScene ||
                EntityReferences.IsReplica(hm.gameObject) || hm.isDead || hm.hp <= 0 || hm.IsInvincible ||
                control == null || !control.enabled || !hm.GetComponents<Collider2D>().Any(c => c.enabled && !c.isTrigger)) continue;
            Require(hm.GetComponentsInChildren<PlayMakerFSM>(true).Any(f => f.FsmName == "WAVE END" &&
                f.FsmStates.SelectMany(s => s.Actions).OfType<SendEventByName>().Any(a => a.Enabled && a.sendEvent.Value == "WAVE END")),
                "released Silver mimic retains its native corpse WAVE END callback");
            TraceSilverMimicBottle("before-native-death");
            Log("SILVER MIMIC NATIVE DAMAGE path=" + WallPath(hm.transform) + " hp=" + hm.hp +
                " state=" + control.ActiveStateName + " registered=" + (EntityReferences.Owner(hm.gameObject) != null));
            // The released actor stays below its bottle and has no registered
            // root entity. Use the same native damage/death path as the other
            // waves; its original corpse must emit WAVE END on its own.
            hm.ApplyExtraDamage(hm.hp);
        }
    }

    private void TraceSilverMimicBottle(string phase) {
        var lines = new List<string>();
        foreach (var bottle in SilverMimicBottles()) {
            lines.Add("BOTTLE " + WallPath(bottle) + " self=" + bottle.gameObject.activeSelf +
                " active=" + bottle.gameObject.activeInHierarchy + " pos=" + bottle.position);
            foreach (var collider in bottle.GetComponentsInChildren<Collider2D>(true))
                lines.Add("COLLIDER " + WallPath(collider.transform) + " enabled=" + collider.enabled +
                    " active=" + collider.gameObject.activeInHierarchy + " trigger=" + collider.isTrigger + " bounds=" + collider.bounds);
            foreach (var fsm in bottle.GetComponentsInChildren<PlayMakerFSM>(true)) {
                lines.Add("FSM " + WallPath(fsm.transform) + "/" + fsm.FsmName + " enabled=" + fsm.enabled +
                    " active=" + fsm.gameObject.activeInHierarchy + " state=" + fsm.ActiveStateName);
                foreach (var state in fsm.FsmStates) {
                    lines.Add(" STATE " + state.Name + " -> " + string.Join(",", state.Transitions.Select(t => t.EventName + ":" + t.ToState)));
                    foreach (var action in state.Actions) lines.Add("  ACTION enabled=" + action.Enabled + " " + action.GetType().Name + " " +
                        string.Join(";", action.GetType().GetFields(BindingFlags.Instance | BindingFlags.Public)
                            .Select(f => f.Name + "=" + Describe(f.GetValue(action)))));
                }
            }
        }
        File.WriteAllLines(Path.Combine(root, role + "-silver-mimic-" + phase + ".inspect.txt"), lines);
        Log("SILVER MIMIC TRACE " + phase + " bottles=" + SilverMimicBottles().Length);
    }

    private IEnumerator AttackSilverMimicBottle() {
        // Silver's native Wave 20 releases a bottle, not a live damageable
        // enemy. A health-only wave driver leaves it unopened forever. Enter
        // its actual physical range and use the player's original nail input;
        // the bottle/mimic FSMs own release, transformation and WAVE END.
        TraceSilverMimicBottle("wave20-entered");
        try {
            Transform bottle = null;
            yield return Until(() => (bottle = SilverMimicBottles().FirstOrDefault(t => t.gameObject.activeInHierarchy)) != null,
                "Silver original Wave 20 mimic bottle becomes active", 10);
            // Let the original cage release its physical bottle before the
            // floor-level attack; activation alone can precede that animation.
            yield return new WaitForSecondsRealtime(2);
            TraceSilverMimicBottle("before-attack");
            var hero = HeroController.instance;
            var body = hero.GetComponent<Rigidbody2D>();
            var ground = hero.FindGroundPoint(bottle.position + new Vector3(-1.6f, 3, 0), true);
            hero.transform.position = ground + Vector3.up * .1f;
            body.velocity = Vector2.zero;
            hero.AffectedByGravity(true); hero.FaceRight(); Physics2D.SyncTransforms();
            yield return Until(() => hero.cState.onGround && hero.acceptingInput, "Silver native nail attack beside mimic bottle", 5);
            Log("SILVER MIMIC NATIVE ATTACK bottle=" + WallPath(bottle) + " pos=" + bottle.position + " hero=" + hero.transform.position);
            yield return Press(InputHandler.Instance.inputActions.attack);
            yield return new WaitForSecondsRealtime(.3f);
        } finally { TraceSilverMimicBottle("after-attempt"); }
    }
}
