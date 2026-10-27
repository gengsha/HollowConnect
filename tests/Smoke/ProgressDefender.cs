using System;
using System.Collections;
using System.Linq;
using System.Reflection;
using Hkmp.Game.Client.Entity;
using Hkmp.Game.Client.Entity.Component;
using Hkmp.Networking.Packet.Data;
using HollowConnect;
using MonoMod.RuntimeDetour;
using UnityEngine;
using Object = UnityEngine.Object;

namespace HollowConnect.Smoke;

public sealed partial class ProgressProbe {
    private void CheckBossCompletionLayoutOrdering() {
        if (host) return;
        var manager = Field<Hkmp.Game.Client.Entity.EntityManager>(engine.ClientManager, "_entityManager");
        var scene = Hkmp.CoopContext.LocalScene;
        var controller = Object.FindObjectsOfType<BossSceneController>(true).FirstOrDefault(c =>
            c.gameObject.scene.name == scene || c.gameObject.scene.name == scene + "_boss" || c.gameObject.scene.name == scene + "_boss_defeated");
        if (controller == null) {
            Mark("SKIP boss completion layout ordering: White Defender controller is created only after native fight activation");
            return;
        }

        var room = Field<object>(manager, "_bossRoom");
        var type = room.GetType();
        var receive = type.GetMethod("Receive", BindingFlags.Instance | BindingFlags.NonPublic);
        var tick = type.GetMethod("Tick", BindingFlags.Instance | BindingFlags.NonPublic);
        var layoutField = type.GetField("layout", BindingFlags.Instance | BindingFlags.NonPublic);
        var pendingField = type.GetField("pending", BindingFlags.Instance | BindingFlags.NonPublic);
        var oldLayout = layoutField.GetValue(room); var oldPending = pendingField.GetValue(room);
        var oldApply = Hkmp.CoopContext.ApplyBossContext;
        void Deliver(EntityComponentType kind, string value) {
            var data = new EntityNetworkData { Type = kind };
            data.Packet.Write(value); receive.Invoke(room, new object[] { data });
        }
        try {
            Hkmp.CoopContext.ApplyBossContext = _ => false;
            Deliver(EntityComponentType.SceneLayout, "[]");
            tick.Invoke(room, null);
            ResourceCheck(controller != null, "empty post-death layout waits while the native controller is present");
            Deliver(EntityComponentType.BossSceneComplete, "{}");
            tick.Invoke(room, null);
            ResourceCheck(controller != null, "pending completion preserves the controller until context is ready");
        } finally {
            Hkmp.CoopContext.ApplyBossContext = oldApply;
            layoutField.SetValue(room, oldLayout); pendingField.SetValue(room, oldPending);
        }
    }

    private IEnumerator Defender() {
        if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable("HOLLOWCONNECT_DEFENDER_BARRIER_CHECK"))) {
            yield return DefenderBarrierChecks();
            yield break;
        }
        var pd = PlayerData.instance;
        pd.SetBool("hasDreamNail", true); pd.SetInt("quakeLevel", 2);
        if (Environment.GetEnvironmentVariable("HOLLOWCONNECT_DEFENDER_ROUTE_INSPECT") == "1") {
            pd.SetBool("defeatedDungDefender", true); pd.SetBool("killedDungDefender", true);
            pd.SetBool("maskBrokenLurien", true); pd.SetBool("maskBrokenMonomon", true); pd.SetBool("maskBrokenHegemol", true);
            pd.SetBool("dungDefenderSleeping", true);
            yield return Room("Waterways_05");
            foreach (var fsm in HeroController.instance.GetComponents<PlayMakerFSM>().Where(f => f.FsmName == "Spell Control")) Dump(fsm);
            foreach (var fsm in Object.FindObjectsOfType<PlayMakerFSM>(true).Where(f => f.gameObject.scene.name == "Waterways_05" && f.name.Contains("Quake"))) Dump(fsm);
            foreach (var collider in Object.FindObjectsOfType<BoxCollider2D>().Where(c => c.name.Contains("Quake"))) Mark("FLOOR BOUNDS " + collider.name + " " + collider.bounds);
            yield return Room("Waterways_15");
            foreach (var fsm in Object.FindObjectsOfType<PlayMakerFSM>(true).Where(f => f.gameObject.scene.name == "Waterways_15" && (f.name.Contains("Dream") || f.name.Contains("Dung")))) {
                Mark("ROUTE FSM " + fsm.name + "/" + fsm.FsmName + " pos=" + fsm.transform.position + " active=" + fsm.gameObject.activeInHierarchy + " state=" + fsm.ActiveStateName);
                Dump(fsm);
            }
            yield break;
        }
        yield return Room("Waterways_05");
        foreach (var fsm in Object.FindObjectsOfType<PlayMakerFSM>(true).Where(f => f.gameObject.scene.name.StartsWith("Waterways_05") &&
            (f.name.IndexOf("Dung", StringComparison.OrdinalIgnoreCase) >= 0 || f.name.IndexOf("Dream", StringComparison.OrdinalIgnoreCase) >= 0 ||
             f.name.IndexOf("Quake", StringComparison.OrdinalIgnoreCase) >= 0 || f.FsmName.Contains("Battle")))) Dump(fsm);
        foreach (var hm in Object.FindObjectsOfType<HealthManager>()) Mark("DEFENDER native health " + hm.name + " scene=" + hm.gameObject.scene.name + " hp=" + hm.hp + " pos=" + hm.transform.position);
        if (host) {
            var manager = Field<Hkmp.Game.Client.Entity.EntityManager>(engine.ClientManager, "_entityManager");
            ResourceCheck(manager.IsSceneHost, "host enters original Dung Defender encounter first");
            var boss = Object.FindObjectsOfType<HealthManager>().First(h => h.name == "Dung Defender");
            HeroController.instance.transform.position = boss.transform.position + Vector3.right * 3;
            yield return new WaitForSecondsRealtime(1);
            boss.ApplyExtraDamage(boss.hp + 1);
        }
        yield return Until(() => pd.GetBool("defeatedDungDefender") && pd.GetBool("killedDungDefender"), "native Dung Defender death progression reaches both saves", 20);
        yield return Until(() => !host || CoopMod.Statistics.SharedProgress.ContainsKey("defeatedDungDefender"),
            "native Dung Defender progression committed to the host authority ledger", 5);
        ResourceCheck(CoopMod.Statistics.SharedProgress.ContainsKey("defeatedDungDefender") || !host,
            "host records the native Dung Defender story progression");
        yield return ResourceBarrier("dung-defeated");
        yield return Room("Town");
        yield return Room("Waterways_05");
        ResourceCheck(!Object.FindObjectsOfType<HealthManager>().Any(h => h.name == "Dung Defender" && h.hp > 0),
            "guest re-entry does not replay the completed story boss");
        foreach (var fsm in Object.FindObjectsOfType<PlayMakerFSM>(true).Where(f => f.gameObject.scene.name.StartsWith("Waterways_05") &&
            (f.name.IndexOf("Dung", StringComparison.OrdinalIgnoreCase) >= 0 || f.name.IndexOf("Dream", StringComparison.OrdinalIgnoreCase) >= 0 ||
             f.name.IndexOf("Quake", StringComparison.OrdinalIgnoreCase) >= 0))) Dump(fsm);
        // Explicit native story prerequisites in the disposable character.
        pd.SetBool("maskBrokenLurien", true); pd.SetBool("maskBrokenMonomon", true); pd.SetBool("maskBrokenHegemol", true);
        pd.SetBool("dungDefenderSleeping", true);
        var hero = HeroController.instance;
        var floor = Object.FindObjectsOfType<PlayMakerFSM>().Single(f => f.name == "Quake Floor" && f.FsmName == "quake_floor");
        var floorBounds = floor.GetComponent<BoxCollider2D>().bounds;
        var body = hero.GetComponent<Rigidbody2D>();
        body.bodyType = RigidbodyType2D.Dynamic; body.simulated = true;
        hero.AffectedByGravity(true); hero.RegainControl(); hero.StartAnimationControl();
        hero.transform.position = new Vector3(floorBounds.center.x, floorBounds.max.y + 2, hero.transform.position.z);
        body.velocity = Vector2.zero;
        var spell = hero.GetComponents<PlayMakerFSM>().Single(f => f.FsmName == "Spell Control");
        spell.SetState("Quake Antic");
        yield return Until(() => AliveScene("Waterways_15"), "native quake breaks floor and enters hidden Waterways_15", 25);
        ResourceCheck(pd.GetBool("defeatedDungDefender"), "native hidden-room route preserves the completed boss");
        yield return ResourceBarrier("native-hidden-room");
        var dream = Object.FindObjectsOfType<PlayMakerFSM>().Single(f => f.name == "Dream Enter" && f.FsmName == "Control");
        yield return Until(() => dream.ActiveStateName == "Idle", "native hidden-room dream target ready", 8);
        hero.transform.position = new Vector3(dream.transform.position.x - 1, 4.2f, hero.transform.position.z);
        hero.FaceRight(); hero.AffectedByGravity(true); body.velocity = Vector2.zero;
        var dreamNail = hero.GetComponents<PlayMakerFSM>().Single(f => f.FsmName == "Dream Nail");
        yield return Until(() => hero.CanDreamNail() && dreamNail.ActiveStateName == "Inactive", "native dream nail input ready at sleeping Defender", 8);
        yield return ResourceBarrier("native-dream-input-ready");
        if (!host) yield return Until(() => Has("host.native-dream-entered"), "host enters native dream before guest swings", 20);
        var dreamInput = InputHandler.Instance.inputActions.dreamNail;
        var inputFrame = Time.frameCount + 1;
        var inputDeadline = Time.realtimeSinceStartup + 1;
        using (new Hook(typeof(InControl.OneAxisInputControl).GetProperty("WasPressed").GetGetMethod(),
            // PlayMaker can poll this on FixedUpdate. Keep the injected press
            // pending until its native FSM acknowledges it, rather than losing
            // a one-render-frame pulse between two physics ticks.
            new Func<Func<InControl.OneAxisInputControl, bool>, InControl.OneAxisInputControl, bool>((orig, self) => ReferenceEquals(self, dreamInput) ?
                Time.frameCount >= inputFrame && Time.realtimeSinceStartup < inputDeadline && dreamNail.ActiveStateName == "Inactive" : orig(self))))
        using (new Hook(typeof(InControl.OneAxisInputControl).GetProperty("IsPressed").GetGetMethod(),
            new Func<Func<InControl.OneAxisInputControl, bool>, InControl.OneAxisInputControl, bool>((orig, self) => ReferenceEquals(self, dreamInput) ? Time.frameCount >= inputFrame : orig(self)))) {
            yield return Until(() => GameManager.instance.sceneName == "Dream_04_White_Defender", "native dream nail hit changes scene through Dream Enter FSM", 15);
        }
        Flag(role + ".native-dream-entered");
        yield return Until(() => AliveScene("Dream_04_White_Defender"), "native dream entry completes without direct test scene transition", 20);
        yield return Until(() => HeroController.instance.acceptingInput && !HeroController.instance.controlReqlinquished &&
            !pd.GetBool("disablePause"), "White Defender native intro finishes and releases control", 15);
        ResourceCheck(HeroController.instance.acceptingInput && !HeroController.instance.controlReqlinquished,
            "White Defender native story dream can complete entry with both clients");
        yield return UnityLifetimeChecks.SampleRuntime(runtime, Mark);
        yield return ResourceBarrier("dream-entry");
        CheckBossCompletionLayoutOrdering();
        // Fault injection models only the stale entry/control state. It does
        // not load, overwrite or claim to repair the user's old save file.
        engine.ClientManager.Disconnect();
        yield return Until(() => !runtime.Connected, "disconnect before single-player stale dream repair", 6);
        var recoveryTime = typeof(CoopRuntime).GetField("dreamRecoveryAfter", ResourcePrivate);
        var recoveryDone = typeof(CoopRuntime).GetField("dreamRecoveryDone", ResourcePrivate);
        recoveryDone.SetValue(runtime, false); recoveryTime.SetValue(runtime, Time.realtimeSinceStartup - 10);
        HeroController.instance.RelinquishControl(); HeroBox.inactive = true;
        HeroController.instance.GetComponent<Rigidbody2D>().simulated = false;
        GameCameras.instance.cameraController.SetMode(CameraController.CameraMode.FROZEN);
        yield return Until(() => HeroController.instance.acceptingInput && !HeroController.instance.controlReqlinquished &&
            !HeroBox.inactive && HeroController.instance.GetComponent<Rigidbody2D>().simulated &&
            GameCameras.instance.cameraController.mode != CameraController.CameraMode.FROZEN,
            "single-player recovery releases injected dream input/body/camera locks", 8);
        ResourceCheck(pd.GetBool("defeatedDungDefender") && pd.GetBool("killedDungDefender"), "dream recovery preserves defeated boss progress");
    }
}
