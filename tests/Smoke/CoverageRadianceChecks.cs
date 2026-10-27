using System;
using System.Collections;
using System.Linq;
using System.Reflection;
using Hkmp.Game.Client.Entity;
using HollowConnect;
using UnityEngine;
using Object = UnityEngine.Object;

namespace HollowConnect.Smoke;

public sealed partial class CoverageProbe {
    private GameObject RadianceVisible() {
        var entity = RadianceBossEntity();
        return manager.IsSceneHost ? entity.Object.Host : entity.Object.Client;
    }
    private Entity RadianceBossEntity() => Entities.Values.Single(e => e.Type is EntityType.AbsoluteRadiance or EntityType.Radiance);
    private string RadianceState() {
        var hero = HeroController.instance;
        var camera = GameCameras.instance?.cameraController;
        var boss = RadianceVisible();
        var view = camera == null ? Vector3.zero : camera.cam.WorldToViewportPoint(hero.transform.position);
        return "hero=" + hero.transform.position + " ground=" + hero.cState.onGround +
            " input=" + hero.acceptingInput + "/" + hero.controlReqlinquished + " health=" + PlayerData.instance.health +
            " hazard=" + hero.cState.hazardDeath + "/" + hero.cState.hazardRespawning +
            " camera=" + camera?.mode + " view=" + view + " lock=" + (camera == null ? "null" : Field<CameraLockArea>(camera, "currentLockArea")?.name) +
            " limits=" + camera?.yLockMin + "/" + camera?.yLockMax + " boss=" + boss.activeInHierarchy + "/" +
            string.Join(",", boss.GetComponents<PlayMakerFSM>().Select(f => f.FsmName + "=" + f.ActiveStateName)) +
            " intro=" + boss.transform.parent?.GetComponent<PlayMakerFSM>()?.ActiveStateName +
            " respawn=" + PlayerData.instance.GetVector3("hazardRespawnLocation");
    }
    private IEnumerator RadianceWait(Func<bool> ready, string label, float seconds = 15) {
        var deadline = Time.realtimeSinceStartup + seconds;
        var nextSample = 0f;
        while (!ready() && Time.realtimeSinceStartup < deadline) {
            if (Time.realtimeSinceStartup >= nextSample) {
                nextSample = Time.realtimeSinceStartup + 2;
                Mark("RADIANCE WAIT " + label + " " + RadianceState());
            }
            yield return null;
        }
        if (!ready()) throw new Exception("Radiance " + label + " " + RadianceState());
        Mark("PASS radiance " + label + " " + RadianceState());
    }
    private IEnumerator RadianceTogether(int round, string label) {
        var suffix = ".radiance." + round + "." + label;
        Flag(role + suffix);
        yield return RadianceWait(() => Has((host ? "guest" : "host") + suffix), "both " + label, 50);
    }
    private bool RadiancePlayable() {
        var hero = HeroController.instance;
        var camera = GameCameras.instance?.cameraController;
        if (camera == null) return false;
        var view = camera.cam.WorldToViewportPoint(hero.transform.position);
        // LOCKED is the normal mode inside a Radiance arena. Require the
        // actual hero to be visible and controllable, not an arbitrary mode.
        return hero.acceptingInput && !hero.controlReqlinquished && !hero.cState.hazardDeath &&
            !hero.cState.hazardRespawning && !runtime.Down && camera.mode != CameraController.CameraMode.FROZEN &&
            view.z > 0 && view.x > 0 && view.x < 1 && view.y > 0 && view.y < 1;
    }
    private IEnumerator RadianceNativeEntry(int round, string label) {
        var hero = HeroController.instance;
        hero.takeNoDamage = true;
        hero.AffectedByGravity(true);
        var controller = Object.FindObjectsOfType<PlayMakerFSM>().Single(f => f.name == "Boss Control" && f.FsmName == "Control");
        yield return RadianceWait(() => controller.ActiveStateName == "Battle Start" && RadianceVisible().activeInHierarchy &&
            RadiancePlayable(), "native intro completes " + label, 45);
        if (!manager.IsSceneHost && label == "warp") RadianceDumpFsm(controller);
        Freeze();
        yield return RadianceTogether(round, label + " entered");
    }
    private static bool RadiancePlatform(Collider2D collider) {
        if (collider == null || !collider.enabled || !collider.gameObject.activeInHierarchy || collider.isTrigger ||
            !(collider.gameObject.scene.name is "GG_Radiance" or "Dream_Final_Boss")) return false;
        for (var current = collider.transform; current != null; current = current.parent)
            if (current.name.IndexOf("Radiant Plat", StringComparison.OrdinalIgnoreCase) >= 0) return true;
        return false;
    }
    private static bool RadianceSupported() {
        var hero = HeroController.instance;
        var feet = hero.GetComponent<Collider2D>().bounds;
        return hero.cState.onGround && Object.FindObjectsOfType<Collider2D>().Any(c => RadiancePlatform(c) &&
            feet.max.x > c.bounds.min.x && feet.min.x < c.bounds.max.x && Math.Abs(feet.min.y - c.bounds.max.y) < .4f);
    }
    private void RadianceDumpFsm(PlayMakerFSM fsm) {
        foreach (var state in fsm.FsmStates) {
            Mark("RADIANCE SCHEMA " + fsm.name + "/" + fsm.FsmName + "/" + state.Name + " -> " +
                string.Join(",", state.Transitions.Select(t => t.EventName + ":" + t.ToState)));
            foreach (var action in state.Actions)
                Mark("RADIANCE ACTION " + state.Name + "/" + action.GetType().Name + " " +
                    string.Join(",", action.GetType().GetFields(BindingFlags.Instance | BindingFlags.Public)
                        .Where(f => f.FieldType.Namespace == "HutongGames.PlayMaker" || f.FieldType.IsPrimitive)
                        .Select(f => f.Name + "=" + RadianceActionValue(fsm, f.GetValue(action)))));
        }
    }
    private static string RadianceActionValue(PlayMakerFSM fsm, object value) {
        if (value is HutongGames.PlayMaker.FsmOwnerDefault owner)
            return fsm.Fsm.GetOwnerDefaultTarget(owner)?.name ?? "null";
        if (value is HutongGames.PlayMaker.FsmEvent evt) return evt.Name;
        if (value is HutongGames.PlayMaker.FunctionCall || value is HutongGames.PlayMaker.FsmEventTarget)
            return string.Join(",", value.GetType().GetFields(BindingFlags.Public | BindingFlags.Instance)
                .Select(f => f.Name + ":" + RadianceActionValue(fsm, f.GetValue(value))));
        return value?.ToString() ?? "null";
    }
    private IEnumerator RadiancePhaseAssertions(int round) {
        yield return RadianceNativeEntry(round, "phase");
        var boss = Entities.Values.Single(e => e.Type == EntityType.AbsoluteRadiance);
        var control = boss.Object.Host.LocateMyFSM("Control");
        var phase = boss.Object.Host.LocateMyFSM("Phase Control");
        if (manager.IsSceneHost) {
            RadianceDumpFsm(phase);
            foreach (var state in new[] { "Stun1 Out", "Climb Plats1", "Abyss Antic", "Abyss Start", "Arena 2 Start" }) {
                var value = control.FsmStates.FirstOrDefault(s => s.Name == state);
                if (value != null) Mark("RADIANCE PHASE ACTIONS " + state + " " + string.Join(";", value.Actions.Select(a =>
                    a.GetType().Name + ":" + string.Join(",", a.GetType().GetFields(BindingFlags.Instance | BindingFlags.Public)
                        .Where(f => f.FieldType.Namespace == "HutongGames.PlayMaker" || f.FieldType.IsPrimitive)
                        .Select(f => f.Name + "=" + f.GetValue(a))))));
            }
            var health = boss.Object.Host.GetComponent<HealthManager>();
            var deadline = Time.realtimeSinceStartup + 75;
            while (Time.realtimeSinceStartup < deadline && phase.ActiveStateName != "Stun 1") {
                if (health.hp <= 1) throw new Exception("Radiance native damage did not advance phase " + RadianceState());
                // Extra damage lowers HP without emitting the nail-hit event
                // that the native Phase Control FSM consumes.
                if (phase.ActiveStateName.StartsWith("Idle ", StringComparison.Ordinal))
                    health.Hit(new HitInstance { Source = HeroController.instance.gameObject,
                        AttackType = AttackTypes.Nail, DamageDealt = Math.Min(100, health.hp - 1),
                        Multiplier = 1, IgnoreInvulnerable = true, Direction = 0, MagnitudeMultiplier = 1 });
                Mark("RADIANCE NATIVE DAMAGE hp=" + health.hp + " phase=" + phase.ActiveStateName + " control=" + control.ActiveStateName);
                yield return new WaitForSecondsRealtime(.4f);
            }
            if (phase.ActiveStateName != "Stun 1") throw new Exception("Radiance native stun not reached");
            Flag("radiance.phase.stun." + round);
        }
        yield return RadianceWait(() => Has("radiance.phase.stun." + round), "native damage reaches first stun", 80);
        yield return RadianceWait(() => Object.FindObjectsOfType<Collider2D>().Any(RadiancePlatform), "native phase platforms become solid", 30);
        Mark("RADIANCE PLATFORM SET " + string.Join(";", Object.FindObjectsOfType<Collider2D>().Where(RadiancePlatform)
            .Select(c => EntityIdentity.Path(c.transform) + "=" + c.bounds)));
        Mark("RADIANCE PHASE CHECKPOINT " + RadianceState());
        yield return RadianceTogether(round, "phase platforms");
        var marker = control.FsmVariables.GetFsmGameObject("P2A Hazard").Value;
        yield return RadianceWait(() => Vector3.Distance(PlayerData.instance.GetVector3("hazardRespawnLocation"),
            marker.transform.position) < .2f, "phase checkpoint matches native P2A marker");
        yield return RadianceNativeFall(round);
        yield return RadianceLateWarp(round);
        if (Environment.GetEnvironmentVariable("HOLLOWCONNECT_RADIANCE_ARENA_ASSERTIONS") == "1")
            yield return RadianceArenaAssertions(round);
    }
    private IEnumerator RadianceLateWarp(int round) {
        On.HeroController.hook_TakeDamage isolatePresentation = (orig, self, source, side, amount, hazard) => {
            if (self != HeroController.instance || hazard > 1) orig(self, source, side, amount, hazard);
        };
        On.HeroController.TakeDamage += isolatePresentation;
        try { yield return RadianceLateWarpCore(round); }
        finally { On.HeroController.TakeDamage -= isolatePresentation; }
    }
    private IEnumerator RadianceLateWarpCore(int round) {
        var authority = manager.IsSceneHost;
        if (!authority) {
            // This fixture abandons a directly loaded sequence to arrange an
            // outside requester. Town cannot advertise a usable presence while
            // it still carries that synthetic sequence's Radiance index.
            BossTravel.ResetForEmergency();
            Enter("Town");
            yield return Until(() => In("Town") && HeroController.instance.acceptingInput && engine.ClientManager.SceneSnapshotReady &&
                !BossSequenceController.IsInSequence && !string.IsNullOrEmpty(BossTravel.CaptureContext()), "late entrant waits outside arena with valid native context");
            Freeze();
            Flag("radiance.late.outside." + round);
            yield return Until(() => Has("radiance.late.outside-ready." + round), "authority observes ready outside requester");
            yield return Until(() => runtime.Peers.Values.Single().State?.Scene == "GG_Radiance" &&
                runtime.Peers.Values.Single().State.IsReady, "late target ready");
            yield return Until(() => (bool)typeof(CoopRuntime).GetProperty("CanStartWarp", BindingFlags.Instance | BindingFlags.NonPublic)
                .GetValue(runtime, null), "late requester native transition completes before F8");
            yield return new WaitForSecondsRealtime(.3f);
            runtime.Selected = runtime.Peers.Keys.Single();
            var handled = Field<uint>(runtime, "handledWarp");
            var pressed = false;
            var canWarp = typeof(CoopRuntime).GetProperty("CanStartWarp", BindingFlags.Instance | BindingFlags.NonPublic);
            runtime.KeyPressed = key => key == CoopMod.Settings.TeleportKey && !pressed &&
                (bool)canWarp.GetValue(runtime, null) && (pressed = true);
            yield return Until(() => pressed && Field<uint>(runtime, "handledWarp") > handled, "late cross-room F8 accepted via UDP");
            runtime.KeyPressed = _ => false;
            yield return Until(() => In("GG_Radiance") && !Field<bool>(runtime, "changingScene") &&
                engine.ClientManager.SceneSnapshotReady, "late cross-room F8 snapshot applied");
            RadianceDumpFsm(Object.FindObjectsOfType<PlayMakerFSM>().Single(f => f.name == "Boss Control" && f.FsmName == "Control"));
            HeroController.instance.AffectedByGravity(true);
            yield return RadianceWait(RadiancePlayable, "late cross-room camera settles once", 3);
            if (Field<string>(runtime, "status") != "已到达队友身边") throw new Exception("Late F8 has no successful landing");
            yield return RadianceWait(RadianceSupported, "late F8 stands on active native platform", 3);
            var presentation = Object.FindObjectsOfType<PlayMakerFSM>().Single(f => f.name == "Boss Control" && f.FsmName == "Control");
            if (presentation.ActiveStateName != "Battle Start") throw new Exception("Radiance late presentation still in " + presentation.ActiveStateName);
            Mark("PASS radiance late arrival skips title presentation");
            var bad = false;
            var end = Time.realtimeSinceStartup + 28;
            On.HeroController.hook_TakeDamage isolatePresentation = (orig, self, source, side, amount, hazard) => {
                if (self != HeroController.instance || hazard > 1) orig(self, source, side, amount, hazard);
            };
            On.HeroController.TakeDamage += isolatePresentation;
            try {
                while (Time.realtimeSinceStartup < end) {
                    var hero = HeroController.instance;
                    if (!hero.cState.hazardDeath && !hero.cState.hazardRespawning && !RadiancePlayable()) bad = true;
                    if (presentation.ActiveStateName != "Battle Start") bad = true;
                    Mark("RADIANCE LATE ARRIVAL " + RadianceState());
                    yield return new WaitForSecondsRealtime(1);
                }
                yield return RadianceWait(RadiancePlayable, "late presentation remains playable after observation", 5);
            } finally {
                On.HeroController.TakeDamage -= isolatePresentation;
            }
            if (bad) throw new Exception("Radiance late arrival replays control/camera lock " + RadianceState());
            Mark("PASS radiance late F8 stays visible and controllable through intro window");
            Freeze();
            Flag("radiance.late.complete." + round);
        } else {
            var boss = RadianceVisible();
            var health = boss.GetComponent<HealthManager>().hp;
            yield return Until(() => Has("radiance.late.outside." + round), "replica outside for late F8");
            yield return Until(() => runtime.Peers.Values.Single().Life == HollowConnect.Core.Life.Alive &&
                runtime.Peers.Values.Single().State?.Scene == "Town" && runtime.Peers.Values.Single().State.IsReady,
                "outside requester is alive and ready through UDP presence");
            Flag("radiance.late.outside-ready." + round);
            yield return Until(() => Has("radiance.late.complete." + round), "late F8 returns without replaying intro");
            if (boss.GetComponent<HealthManager>().hp != health || boss.LocateMyFSM("Phase Control").ActiveStateName != "Idle 4")
                throw new Exception("Late arrival reset authoritative boss phase/health");
            Mark("PASS radiance late presentation leaves authority phase/health intact");
        }
        yield return RadianceTogether(round, "late warp complete");
    }
    private IEnumerator RadianceNativeFall(int round) {
        var hero = HeroController.instance;
        var hazardHits = 0;
        var sawHazardDeath = false;
        On.HeroController.hook_TakeDamage observer = (orig, self, source, side, amount, hazard) => {
            // Isolate the terrain recovery from projectile damage during this
            // assertion. Terrain still uses the real collision/native routine.
            if (self == hero && hazard <= 1) return;
            var wasHazardDeath = self.cState.hazardDeath;
            orig(self, source, side, amount, hazard);
            if (self == hero && !wasHazardDeath && self.cState.hazardDeath) {
                hazardHits++;
                sawHazardDeath = true;
                Mark("RADIANCE NATIVE HAZARD source=" + source?.name + " kind=" + hazard + " " + RadianceState());
            }
        };
        On.HeroController.TakeDamage += observer;
        try {
            PlayerData.instance.SetInt("health", 9);
            PlayerData.instance.SetInt("healthBlue", 0);
            PlayerData.instance.SetBool("isInvincible", false);
            hero.cState.invulnerable = false;
            hero.takeNoDamage = false;
            hero.AffectedByGravity(true);
            yield return RadianceWait(() => sawHazardDeath, "rising native abyss catches grounded hero", 35);
            yield return RadianceWait(() => RadiancePlayable() && RadianceSupported() &&
                hero.transform.position.y > 30, "native hazard recovery lands on visible solid platform", 20);
            var health = PlayerData.instance.health;
            if (health >= 9 || health <= 0 || hazardHits != 1) throw new Exception("Radiance hazard damage/respawn count " + RadianceState());
            yield return new WaitForSecondsRealtime(5);
            if (!RadiancePlayable() || !RadianceSupported() || PlayerData.instance.health != health || hazardHits != 1)
                throw new Exception("Radiance repeated abyss damage or lost control after recovery " + RadianceState());
            Mark("PASS radiance native abyss recovery stays stable without healing " + RadianceState());
        } finally {
            On.HeroController.TakeDamage -= observer;
            Freeze();
        }
        yield return RadianceTogether(round, "native fall complete");
    }
    private IEnumerator RadianceWarpAssertions(int round) {
        yield return RadianceNativeEntry(round, "warp");
        var hero = HeroController.instance;
        var sendPresence = typeof(CoopRuntime).GetMethod("SendPresence", BindingFlags.Instance | BindingFlags.NonPublic);
        foreach (var actor in new[] { "host", "guest" }) {
            yield return RadianceTogether(round, "warp " + actor + " ready");
            sendPresence.Invoke(runtime, null);
            yield return new WaitForSecondsRealtime(.3f);
            if (role == actor) {
                runtime.Selected = runtime.Peers.Keys.Single();
                var handled = Field<uint>(runtime, "handledWarp");
                var pressed = false;
                runtime.KeyPressed = key => key == CoopMod.Settings.TeleportKey && !pressed && (pressed = true);
                yield return RadianceWait(() => pressed && Field<uint>(runtime, "handledWarp") > handled, "F8 accepted via UDP " + actor);
                runtime.KeyPressed = _ => false;
                yield return RadianceWait(() => !Field<bool>(runtime, "changingScene"), "warp completes " + actor);
                var status = Field<string>(runtime, "status");
                if (status != "已到达队友身边") throw new Exception("Radiance initial floor warp must succeed: " + status);
                Mark("PASS radiance F8 lands on initial native arena floor " + actor);
                yield return RadianceWait(RadiancePlayable, "warp keeps control and hero visible " + actor);
            }
            yield return RadianceTogether(round, "warp " + actor + " complete");
        }
    }
}
