using System;
using System.Collections;
using System.Reflection;
using System.Linq;
using HollowConnect;
using MonoMod.RuntimeDetour;
using UnityEngine;

namespace HollowConnect.Smoke;

public sealed partial class ProgressProbe {
    private IEnumerator LinkedHealthChecks() {
        if (LinkedSpecial != null) { if (host) Flag("host.linked.join-ready"); yield return LinkedIsolationChecks(); yield break; }
        if (Environment.GetEnvironmentVariable("HOLLOWCONNECT_LINKED_REST") == "inspect") {
            if (host) Flag("host.linked.join-ready");
            yield return ResourceBarrier("inspect-join");
            foreach (var scene in new[] { "Deepnest_Spider_Town", "GG_Spa", "Ruins_Bathhouse" }) {
                EnterRestScene(scene); yield return Until(() => UnityEngine.SceneManagement.SceneManager.GetActiveScene().name == scene &&
                    UnityEngine.Object.FindObjectsOfType<PlayMakerFSM>(true).Any(f => f.gameObject.scene.name == scene), "inspect loaded " + scene);
                yield return new WaitForSecondsRealtime(1);
                Mark("NATIVE GATES " + string.Join(",", UnityEngine.Object.FindObjectsOfType<TransitionPoint>(true).Select(g => g.name)));
                foreach (var f in UnityEngine.Object.FindObjectsOfType<PlayMakerFSM>(true).Where(f =>
                    f.gameObject.scene.name == scene || f.transform.IsChildOf(HeroController.instance.transform))) Dump(f);
                yield return ResourceBarrier("inspect-" + scene);
                if (!AliveScene(scene)) yield break;
            }
            yield break;
        }
        if (Environment.GetEnvironmentVariable("HOLLOWCONNECT_LINKED_REST") == "on") { yield return LinkedRestChecks(); yield break; }
        var pd = PlayerData.instance; var hero = HeroController.instance;
        var extra = CoopMod.Settings.LinkedHealthBonusMasks;
        var enabled = Environment.GetEnvironmentVariable("HOLLOWCONNECT_LINKED_CASE") == "on";
        if (!enabled) {
            if (host) Flag("host.linked.join-ready");
            yield return ResourceBarrier("gate");
            ResourceCheck(!runtime.LinkedHealthRuleEnabled && runtime.RescueEnabled && runtime.SpectateAfterExhaustion,
                "disabled/prerequisite gate leaves existing rescue and spectating options intact");
            if (host) pd.TakeHealth(1);
            yield return new WaitForSecondsRealtime(1);
            ResourceCheck(pd.GetInt("health") == (host ? 4 : 5) && pd.GetInt("maxHealthBase") == 5 && pd.GetInt("maxHealth") == 5,
                "disabled mode keeps health and permanent mask data personal");
            pd.SetBool("equippedCharm_8", true); pd.EquipCharm(8); hero.CharmUpdate();
            ResourceCheck(pd.GetBool("equippedCharm_8") && pd.equippedCharms.Contains(8) && pd.healthBlue == 2 && pd.health == 5,
                "disabled or missing prerequisites retain native charm effects and healing");
            yield return ResourceBarrier("gate-end"); yield break;
        }
        if (host) {
            yield return Until(() => Field<bool>(runtime, "linkedHealthInitSent") && Field<bool>(runtime, "linkedHealthStateReceived") &&
                pd.GetInt("maxHealth") == 5 + extra && pd.GetInt("health") == 5 + extra, "linked initial bonus is filled");
            pd.TakeHealth(2);
            yield return Until(() => Field<int>(runtime, "linkedHealthCurrent") == 3 + extra && pd.GetInt("health") == 3 + extra,
                "host native damage committed before late join");
            yield return new WaitForSecondsRealtime(.4f);
            Flag("host.linked.join-ready");
        }
        yield return Until(() => pd.GetInt("maxHealth") == 5 + extra && pd.GetInt("health") == 3 + extra, "late join adopts current bar without heal");
        ResourceCheck(!runtime.RescueEnabled && !runtime.SpectateAfterExhaustion, "linked mode disables rescue and spectating");
        yield return ResourceBarrier("join");
        yield return LinkedChargeSurvives();
        pd.SetInt("healthBlue", host ? 2 : 6);
        if (host) pd.TakeHealth(3);
        yield return Until(() => pd.GetInt("health") == 2 + extra, "only spilled ordinary damage reaches the team");
        ResourceCheck(pd.GetInt("healthBlue") == (host ? 0 : 6), "blue masks remain local on spill");
        yield return ResourceBarrier("spill");
        if (!host) pd.TakeHealth(2);
        yield return new WaitForSecondsRealtime(.4f);
        ResourceCheck(pd.GetInt("health") == 2 + extra && pd.GetInt("healthBlue") == (host ? 0 : 4), "blue-only damage never changes ordinary team health");
        yield return ResourceBarrier("blue");
        if (host) {
            hero.AffectedByGravity(true);
            yield return Until(() => hero.CanFocus(), "native focus eligibility");
            pd.SetBool("equippedCharm_34", true); pd.SetInt("MPCharge", 33);
            var focus = InputHandler.Instance.inputActions.cast; var frame = Time.frameCount + 1;
            using (new Hook(typeof(InControl.OneAxisInputControl).GetProperty("WasPressed").GetGetMethod(),
                new Func<Func<InControl.OneAxisInputControl, bool>, InControl.OneAxisInputControl, bool>((orig, self) => ReferenceEquals(self, focus) ? Time.frameCount == frame : orig(self))))
            using (new Hook(typeof(InControl.OneAxisInputControl).GetProperty("IsPressed").GetGetMethod(),
                new Func<Func<InControl.OneAxisInputControl, bool>, InControl.OneAxisInputControl, bool>((orig, self) => ReferenceEquals(self, focus) ? Time.frameCount >= frame : orig(self))))
                yield return Until(() => pd.GetInt("health") == 4 + extra, "real Deep Focus input heals two team masks", 8);
            ResourceCheck(pd.GetInt("MPCharge") == 0, "Deep Focus spends 33 local soul");
            yield return Until(() => hero.CanFocus(), "Deep Focus releases native control");
            pd.SetBool("equippedCharm_34", false); Freeze();
        }
        yield return Until(() => pd.GetInt("health") == 4 + extra, "native two-mask heal is shared once");
        yield return ResourceBarrier("heal");
        pd.SetInt("healthBlue", 0); pd.TakeHealth(1);
        yield return Until(() => pd.GetInt("health") == 2 + extra, "simultaneous two-client hits are both retained");
        yield return ResourceBarrier("hits");
        if (host) for (var i = 1; i <= 3; i++) ResourceSend("mask:slyShellFrag" + i);
        yield return Until(() => pd.GetInt("heartPieces") == 3, "three unique shards await native rollover");
        yield return Room("Crossroads_13");
        if (host) {
            var pickup = UnityEngine.Object.FindObjectsOfType<PlayMakerFSM>(true).Single(f =>
                f.gameObject.scene.name == "Crossroads_13" && f.name == "Heart Piece" && f.FsmName == "Heart Container Control");
            Dump(pickup);
            yield return Until(() => pickup.gameObject.activeInHierarchy && pickup.ActiveStateName == "Idle", "native fourth world shard is ready");
            hero.transform.position = pickup.transform.position; Physics2D.SyncTransforms();
        }
        string maskState = null;
        yield return Until(() => {
            var state = "base=" + pd.maxHealthBase + " rawMax=" + pd.maxHealth + " pieces=" + pd.heartPieces + " health=" + pd.health +
                " shared=" + Field<int>(runtime, "linkedHealthCurrent");
            if (maskState != state) { Mark("LINKED SHARD " + state); maskState = state; }
            return pd.GetInt("maxHealthBase") == 6 && pd.GetInt("heartPieces") == 0 && pd.GetInt("maxHealth") == 6 + extra && pd.GetInt("health") == 3 + extra;
        },
            "four unique receipts add one current and maximum mask while personal progression rolls over");
        yield return ResourceBarrier("pieces");
        yield return ResourceIdle("linked native fourth shard");
        if (host) ResourceSend("mask:slyShellFrag1");
        yield return new WaitForSecondsRealtime(.5f);
        ResourceCheck(pd.GetInt("maxHealthBase") == 6 && pd.GetInt("maxHealth") == 6 + extra && pd.GetInt("health") == 3 + extra,
            "duplicate mask source cannot raise or refill the shared bar");
        var save = new SaveGameData(pd, SceneData.instance);
        typeof(CoopRuntime).GetMethod("SaveLinkedHealth", ResourcePrivate).Invoke(runtime, new object[] { save });
        ResourceCheck(!ReferenceEquals(save.playerData, pd) && save.playerData.maxHealthBase == 6 && save.playerData.maxHealth == 6 && save.playerData.health == 6,
            "serialized snapshot retains real mask upgrade health and excludes temporary masks without changing live data");
        yield return ResourceBarrier("saved");
        // A genuine native lethal hit exercises authority zero, both personal
        // death routes, and the existing party retry barrier.
        var trace = StartCoroutine(TraceLinkedDeath());
        if (host) pd.TakeHealth(99);
        yield return Until(() => runtime.Resetting, "shared zero starts the whole-party checkpoint barrier", 25);
        StopCoroutine(trace);
        yield return Until(() => !runtime.Resetting && !runtime.Down && !runtime.Returning && runtime.Playable &&
            pd.GetInt("health") == 6 + extra, "both native deaths complete and team bar refills once", 70);
        ResourceCheck(!runtime.Spectating, "shared death never creates a spectator");
        yield return ResourceBarrier("death", 80);
        yield return LinkedLeaveRetryBench();
        yield return ResourceBarrier("retry-standing");
        // A second attempt must start with fresh report sequences, and a
        // reconnect must inherit damage made while that member was absent.
        if (host) pd.TakeHealth(1);
        yield return Until(() => pd.GetInt("health") == 5 + extra, "new attempt still shares ordinary damage");
        yield return ResourceBarrier("attempt-two");
        if (!host) {
            engine.ClientManager.Disconnect();
            ResourceCheck(!runtime.LinkedHealthRuleEnabled && pd.GetInt("maxHealth") == 6 && pd.GetInt("health") <= 6,
                "disconnect removes shared hooks and temporary capacity");
            Flag("guest.linked.offline");
            yield return Until(() => Has("host.linked.offline-hit"), "host committed damage while guest offline");
            typeof(Hkmp.Game.Client.ClientManager).GetField("_autoConnect", ResourcePrivate).SetValue(engine.ClientManager, true);
            typeof(Hkmp.Game.Client.ClientManager).GetMethod("Connect", ResourcePrivate).Invoke(engine.ClientManager,
                new object[] { "127.0.0.1", 29650, "progressguest" });
        } else {
            yield return Until(() => Has("guest.linked.offline"), "guest disconnected before next shared hit");
            pd.TakeHealth(1); yield return new WaitForSecondsRealtime(.5f); Flag("host.linked.offline-hit");
        }
        yield return Until(() => runtime.LinkedHealthRuleEnabled && pd.GetInt("health") == 4 + extra && pd.GetInt("maxHealth") == 6 + extra,
            "reconnected member adopts current room health");
        yield return ResourceBarrier("reconnect");
        if (!host) pd.TakeHealth(1);
        yield return Until(() => pd.GetInt("health") == 3 + extra, "reconnected member reports with a fresh identity");
        yield return ResourceBarrier("reconnected-hit");
        if (!host) pd.TakeHealth(99);
        yield return Until(() => runtime.Resetting, "second shared zero starts a new checkpoint barrier");
        yield return Until(() => !runtime.Resetting && runtime.Playable && pd.GetInt("health") == 6 + extra, "second shared respawn completes", 70);
        yield return ResourceBarrier("second-death", 80);
        if (extra == 2) {
            var origin = UnityEngine.SceneManagement.SceneManager.GetActiveScene().handle;
            if (!host) {
                // Hold only our Down report so disconnect occurs after native
                // death capture, before the server can accept a whole wipe.
                using (new Hook(typeof(CoopRuntime).GetMethod("Send", ResourcePrivate),
                    new Action<Action<CoopRuntime, Message>, CoopRuntime, Message>((orig, self, message) => {
                        if (message.Kind != Kind.Down) orig(self, message);
                    }))) {
                    pd.TakeHealth(99);
                    yield return Until(() => runtime.Down, "shared death captured before disconnect");
                    engine.ClientManager.Disconnect();
                }
                yield return Until(() => !runtime.Connected && UnityEngine.SceneManagement.SceneManager.GetActiveScene().handle != origin &&
                    runtime.Playable && !HeroController.instance.cState.dead && pd.GetInt("health") == 6,
                    "disconnect preserves captured native death and removes temporary masks", 70);
            } else {
                yield return Until(() => runtime.Resetting, "survivor barrier proceeds after downed member leaves");
                yield return Until(() => !runtime.Resetting && runtime.Playable && pd.GetInt("health") == 8,
                    "remaining room finishes its checkpoint barrier", 70);
            }
            yield return ResourceBarrier("down-disconnect", 80);
        }
    }
    private IEnumerator LinkedChargeSurvives() {
        var pd = PlayerData.instance; var hero = HeroController.instance;
        if (!host) {
            pd.SetBool("hasNailArt", true); pd.SetBool("hasCyclone", true); hero.AffectedByGravity(true);
            yield return Until(() => hero.cState.onGround && hero.acceptingInput, "native charge ground/input");
            var attack = InputHandler.Instance.inputActions.attack; var frame = Time.frameCount + 1;
            using (new Hook(typeof(InControl.OneAxisInputControl).GetProperty("WasPressed").GetGetMethod(),
                new Func<Func<InControl.OneAxisInputControl, bool>, InControl.OneAxisInputControl, bool>((orig, self) => ReferenceEquals(self, attack) ? Time.frameCount == frame : orig(self))))
            using (new Hook(typeof(InControl.OneAxisInputControl).GetProperty("IsPressed").GetGetMethod(),
                new Func<Func<InControl.OneAxisInputControl, bool>, InControl.OneAxisInputControl, bool>((orig, self) => ReferenceEquals(self, attack) ? Time.frameCount >= frame : orig(self)))) {
                yield return Until(() => hero.cState.nailCharging, "real held attack starts nail charge");
                Flag("guest.linked.charging");
                yield return Until(() => Has("host.linked.charge-hit"), "host damages shared bar while guest charges");
                yield return new WaitForSecondsRealtime(.6f);
                ResourceCheck(hero.cState.nailCharging && !hero.cState.recoiling, "teammate damage refreshes HUD without cancelling native nail charge");
                Flag("guest.linked.charge-held");
            }
            yield return new WaitForSecondsRealtime(1); Freeze();
        } else {
            yield return Until(() => Has("guest.linked.charging"), "guest native charge ready");
            pd.TakeHealth(1); Flag("host.linked.charge-hit");
            yield return Until(() => Has("guest.linked.charge-held"), "guest retained charge after shared damage");
            pd.AddHealth(1);
        }
        yield return ResourceBarrier("charge");
        yield return Until(() => pd.GetInt("health") == 3 + CoopMod.Settings.LinkedHealthBonusMasks, "charge check restores setup health");
    }
    private IEnumerator LinkedLeaveRetryBench() {
        var bench = UnityEngine.Object.FindObjectsOfType<PlayMakerFSM>().Single(f => f.name == "RestBench" && f.FsmName == "Bench Control");
        yield return Until(() => bench.ActiveStateName == "Resting", "retry reaches native resting state");
        yield return RestStand();
    }
    private IEnumerator TraceLinkedDeath() {
        var previous = "";
        while (true) {
            var state = "rule=" + runtime.LinkedHealthRuleEnabled + " down=" + runtime.Down + " reset=" + runtime.Resetting +
                " return=" + runtime.Returning + " health=" + PlayerData.instance?.health + " round=" + Hkmp.CoopContext.Epoch +
                " serverHP=" + Field<int>(runtime, "linkedHealthCurrent") + " playable=" + runtime.Playable;
            if (previous != state) { Mark("LINKED DEATH " + state); previous = state; }
            yield return null;
        }
    }
}
