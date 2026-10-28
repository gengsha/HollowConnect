using System;
using System.Collections;
using System.Linq;
using System.Reflection;
using HollowConnect;
using HollowConnect.Core;
using HutongGames.PlayMaker;
using MonoMod.RuntimeDetour;
using UnityEngine;
using Object = UnityEngine.Object;

namespace HollowConnect.Smoke;

public sealed partial class ProgressProbe {
    private LinkedRestCount RestCount => Field<LinkedRestCount>(runtime, "linkedRestCount");
    private LinkedRestKind OwnRest => (LinkedRestKind)typeof(CoopRuntime).GetMethod("LocalLinkedRest", ResourcePrivate).Invoke(runtime, null);
    private IEnumerator RestPress(InControl.PlayerAction action) {
        var frame = Time.frameCount + 1;
        using (new Hook(typeof(InControl.OneAxisInputControl).GetProperty("WasPressed").GetGetMethod(),
            new Func<Func<InControl.OneAxisInputControl, bool>, InControl.OneAxisInputControl, bool>((orig, self) => ReferenceEquals(self, action) ? Time.frameCount == frame : orig(self))))
        using (new Hook(typeof(InControl.OneAxisInputControl).GetProperty("IsPressed").GetGetMethod(),
            new Func<Func<InControl.OneAxisInputControl, bool>, InControl.OneAxisInputControl, bool>((orig, self) => ReferenceEquals(self, action) ? Time.frameCount >= frame : orig(self))))
            yield return new WaitForSecondsRealtime(.15f);
    }
    private IEnumerator RestEnter(string scene) {
        EnterRestScene(scene); yield return Until(() => AliveScene(scene), "rest native entrance " + scene);
        Freeze(); yield return new WaitForSecondsRealtime(.5f);
    }
    private void EnterRestScene(string scene) => GameManager.instance.BeginSceneTransition(new GameManager.SceneLoadInfo {
        SceneName = scene, EntryGateName = scene == "Ruins_Bathhouse" || scene == "GG_Spa" ? "right1" : "left1",
        Visualization = GameManager.SceneLoadVisualizations.Default, WaitForSceneTransitionCameraFade = true
    });
    private IEnumerator RestSit() {
        var hero = HeroController.instance;
        var bench = Object.FindObjectsOfType<PlayMakerFSM>().Single(f => f.FsmName == "Bench Control" && f.name == "RestBench");
        Dump(bench);
        hero.transform.position = bench.transform.position + new Vector3(-1, 1.5f, 0);
        hero.GetComponent<Rigidbody2D>().velocity = Vector2.zero; hero.AffectedByGravity(true); Physics2D.SyncTransforms();
        yield return Until(() => bench.ActiveStateName == "In Range" && hero.cState.onGround, "bench native range/ground " + bench.gameObject.scene.name);
        yield return RestPress(InputHandler.Instance.inputActions.up);
        yield return Until(() => PlayerData.instance.GetBool("atBench") && bench.ActiveStateName == "Resting", "bench native resting");
        ResourceCheck(OwnRest == LinkedRestKind.Bench, "healing bench is counted");
    }
    private IEnumerator RestStand() {
        yield return RestPress(InputHandler.Instance.inputActions.jump);
        yield return Until(() => !PlayerData.instance.GetBool("atBench") && HeroController.instance.acceptingInput, "native stand up");
        Freeze();
    }
    private IEnumerator RestSpring() {
        var spa = Object.FindObjectsOfType<PlayMakerFSM>().Single(f => f.FsmName == "Spa Region");
        Dump(spa);
        var collider = spa.GetComponent<Collider2D>();
        ResourceCheck(collider != null, "native spring has its trigger");
        Mark("SPA TRIGGER " + collider.bounds + " pos=" + spa.transform.position);
        var hero = HeroController.instance; hero.transform.position = collider.bounds.center;
        hero.GetComponent<Rigidbody2D>().velocity = Vector2.zero; hero.AffectedByGravity(false); Physics2D.SyncTransforms();
        yield return Until(() => OwnRest == LinkedRestKind.Spring, "native spring enter recognised");
    }
    private IEnumerator LinkedRestChecks() {
        var pd = PlayerData.instance; var hero = HeroController.instance;
        if (host) Flag("host.linked.join-ready");
        yield return Until(() => Field<bool>(runtime, "linkedHealthStateReceived") && pd.GetInt("health") == 7, "rest initial bar");
        yield return ResourceBarrier("rest-joined");
        foreach (var f in Object.FindObjectsOfType<PlayMakerFSM>(true).Where(f => f.FsmStates.Any(s => s.Actions.Any(a =>
            (a.GetType().GetField("boolName")?.GetValue(a) as FsmString)?.Value == "equippedCharm_29")))) Dump(f);
        ResourceCheck(pd.equippedCharms.SequenceEqual(new[] { 1 }) && pd.charmSlotsFilled == pd.charmCost_1 && !pd.overcharmed,
            "pre-equipped forbidden charms suspended without leftover overcharm");
        ResourceCheck(pd.healthBlue == 3, "automatic suppression preserves cocoon masks above original charm blue");
        pd.SetInt("healthBlue", 0);
        if (host) pd.TakeHealth(1);
        yield return Until(() => pd.GetInt("health") == 6, "rest damage setup");
        yield return ResourceBarrier("rest-blue-setup");
        pd.SetInt("healthBlue", host ? 1 : 7);
        if (host) pd.TakeHealth(2);
        yield return Until(() => pd.GetInt("health") == 5, "one blue against double damage spills one ordinary");
        ResourceCheck(pd.GetInt("healthBlue") == (host ? 0 : 7), "other player's blue cannot absorb the hit");
        yield return ResourceBarrier("rest-blue-two");
        if (host) {
            pd.AddHealth(2); yield return Until(() => pd.GetInt("health") == 7, "refill before quadruple damage");
            pd.SetInt("healthBlue", 1); pd.SetBool("overcharmed", true);
            hero.takeNoDamage = false; hero.cState.invulnerable = false;
            hero.TakeDamage(null, GlobalEnums.CollisionSide.left, 2, 1);
            pd.SetBool("overcharmed", false);
        }
        yield return Until(() => pd.GetInt("health") == 4, "native double damage plus overcharm spills three from one blue", 10);
        ResourceCheck(pd.GetInt("healthBlue") == (host ? 0 : 7), "quadruple hit only consumes victim's blue");
        yield return ResourceBarrier("rest-blue-four");
        if (host) { yield return new WaitForSecondsRealtime(1.5f); Freeze(); }
        // Equipment API and raw-field injection independently exercise both
        // defences. CharmUpdate must never heal the team while suppressing.
        pd.SetInt("healthBlue", 3);
        foreach (var id in new[] { 8, 9, 23, 27, 29 }) {
            pd.SetBool("equippedCharm_" + id, true); pd.EquipCharm(id);
            ResourceCheck(!pd.GetBool("equippedCharm_" + id) && !pd.equippedCharms.Contains(id), "blocked charm cannot equip " + id);
            pd.SetBoolInternal("equippedCharm_" + id, true); pd.equippedCharms.Add(id);
        }
        hero.CharmUpdate();
        ResourceCheck(new[] { 8, 9, 23, 27, 29 }.All(id => !pd.GetBoolInternal("equippedCharm_" + id) && !pd.equippedCharms.Contains(id)) &&
            pd.healthBlue == 3 && pd.joniHealthBlue == 0 && pd.charmSlotsFilled == pd.charmCost_1, "raw injected charm effects removed while world blue survives");
        pd.SetInt("healthBlue", 0);
        // Deliberately force the native terminal effect to simulate a stale
        // cached recovery or another mod. It must still be unable to heal.
        var hive = Object.FindObjectsOfType<PlayMakerFSM>(true).Single(f => f.FsmName == "Hive Health Regen");
        hive.SetState("Recover");
        ResourceCheck(pd.GetInt("health") == 4, "forced native Hiveblood recovery has no healing effect");
        yield return new WaitForSecondsRealtime(11);
        ResourceCheck(pd.GetInt("health") == 4, "forbidden charm refresh cannot refill team");
        yield return ResourceBarrier("rest-charms");
        var beforeRestRevision = Field<uint>(runtime, "linkedHealthRevision");
        // Players occupy different scenes, first with only a single bench.
        yield return RestEnter(host ? "Town" : "Ruins_Bathhouse");
        if (host) yield return RestSit();
        yield return Until(() => RestCount.Resting == 1 && RestCount.Total == 2, "one of two resting progress");
        yield return Until(() => Field<string>(runtime, "linkedRestStatus").Contains("progressguest") &&
            Field<string>(runtime, "linkedRestStatus").Contains("未就位"), "rest progress names the missing player");
        yield return ResourceBarrier("rest-missing-names");
        if (!host) {
            using (new Hook(typeof(CoopRuntime).GetMethod("Send", ResourcePrivate),
                new Action<Action<CoopRuntime, Message>, CoopRuntime, Message>((orig, self, message) => {
                    if (message.Kind == Kind.Presence) message.Aux |= 8; orig(self, message);
                }))) {
                Flag("guest.rest.loading-report");
                yield return Until(() => Has("host.rest.loading-seen"), "host receives loading progress");
            }
        } else {
            yield return Until(() => Has("guest.rest.loading-report") && Field<string>(runtime, "linkedRestStatus").Contains("progressguest（加载中）"),
                "missing names distinguish loading from an idle player");
            var view = Field<object>(runtime, "linkedRestView");
            ResourceCheck(Field<UnityEngine.UI.Text>(view, "label").text.Contains("progressguest（加载中）"), "native font rest overlay renders missing loading name");
            Flag("host.rest.loading-seen");
        }
        yield return Until(() => Field<string>(runtime, "linkedRestStatus").Contains("未就位"), "fresh heartbeat clears loading label");
        ResourceCheck(Field<uint>(runtime, "linkedHealthRevision") > beforeRestRevision, "rest-only change advances snapshot version");
        var old = new Message { Kind = Kind.LinkedHealthState, Round = Hkmp.CoopContext.Epoch,
            LinkedHealth = pd.GetInt("health"), LinkedMaxHealth = pd.GetInt("maxHealth"),
            LinkedRevision = Field<uint>(runtime, "linkedHealthRevision") - 1, Value = 2, X = 0, Y = 0 };
        typeof(CoopRuntime).GetMethod("ReceiveLinkedHealthState", ResourcePrivate).Invoke(runtime, new object[] { old });
        ResourceCheck(RestCount.Resting == 1, "older retransmitted snapshot cannot erase current resting progress");
        yield return new WaitForSecondsRealtime(2);
        ResourceCheck(pd.GetInt("health") == 4, "one native bench does not heal the room");
        yield return ResourceBarrier("rest-waiting");
        if (!host) yield return RestSpring();
        yield return Until(() => RestCount.Ready && RestCount.Benches == 1 && RestCount.Springs == 1 && pd.GetInt("health") == 7,
            "distant mixed bench and spring refill once");
        ResourceCheck(RestCount.Label == "休息", "mixed native progress label");
        yield return ResourceBarrier("rest-mixed");
        if (host) yield return RestStand();
        yield return Until(() => RestCount.Resting == 1, "standing up breaks rest immediately");
        if (host) pd.TakeHealth(2);
        yield return Until(() => pd.GetInt("health") == 5, "hit after standing up reaches team");
        yield return new WaitForSecondsRealtime(2);
        ResourceCheck(pd.GetInt("health") == 5, "one remaining spring cannot heal");
        yield return ResourceBarrier("rest-left");
        // A second healing chair in the bathhouse counts independently of Town.
        if (!host) {
            hero.transform.position += new Vector3(15, 10, 0); Physics2D.SyncTransforms();
            yield return Until(() => OwnRest == LinkedRestKind.None, "native spring exit recognised");
            yield return RestSit();
        }
        yield return ResourceBarrier("rest-second-bench");
        if (host) yield return RestSit();
        yield return Until(() => RestCount.Benches == 2 && pd.GetInt("health") == 7, "different native chairs refill together");
        yield return ResourceBarrier("rest-two-benches");
        yield return RestStand();
        yield return RestEnter(host ? "Ruins_Bathhouse" : "GG_Spa");
        if (host) pd.TakeHealth(3);
        yield return Until(() => pd.GetInt("health") == 4, "all-spring damage setup");
        yield return RestSpring();
        yield return Until(() => RestCount.Ready && RestCount.Springs == 2, "different springs count together");
        yield return Until(() => pd.GetInt("health") >= 5, "all springs heal team");
        ResourceCheck(pd.GetInt("health") <= 6, "spring healing is paced rather than duplicated");
        yield return ResourceBarrier("rest-springs");
        yield return Until(() => pd.GetInt("health") == 7, "spring heals to team maximum");
        hero.transform.position += new Vector3(15, 10, 0); Physics2D.SyncTransforms();
        yield return Until(() => OwnRest == LinkedRestKind.None && RestCount.Resting == 0, "both spring exits clear progress");
        yield return ResourceBarrier("rest-end");
        var save = new SaveGameData(pd, SceneData.instance);
        typeof(CoopRuntime).GetMethod("SaveLinkedHealth", ResourcePrivate).Invoke(runtime, new object[] { save });
        ResourceCheck(save.playerData.equippedCharms.SequenceEqual(new[] { 1, 8, 29 }) && save.playerData.equippedCharm_8 &&
            save.playerData.equippedCharm_29 && !pd.equippedCharm_8 && !pd.equippedCharm_29 && pd.equippedCharms.SequenceEqual(new[] { 1 }),
            "save snapshot preserves suspended loadout while live effects remain disabled");
        yield return ResourceBarrier("rest-save");
        yield return RestEnter("Deepnest_Spider_Town");
        var trap = Object.FindObjectsOfType<PlayMakerFSM>().Single(f => f.FsmName == "Bench Control Spider");
        var normal = Object.FindObjectsOfType<PlayMakerFSM>(true).Single(f => f.FsmName == "Bench Control" && f.name == "RestBench Return");
        var canHeal = typeof(CoopRuntime).GetMethod("HealingBench", BindingFlags.Static | BindingFlags.NonPublic);
        ResourceCheck(!(bool)canHeal.Invoke(null, new object[] { trap }) && (bool)canHeal.Invoke(null, new object[] { normal }),
            "Beast's Den trap excluded while its real return bench stays eligible");
        yield return ResourceBarrier("rest-trap");
        if (host) {
            pd.UnequipCharm(8); pd.SetBool("equippedCharm_29", false);
            var removed = new SaveGameData(pd, SceneData.instance);
            typeof(CoopRuntime).GetMethod("SaveLinkedHealth", ResourcePrivate).Invoke(runtime, new object[] { removed });
            ResourceCheck(removed.playerData.equippedCharms.SequenceEqual(new[] { 1 }) && !removed.playerData.equippedCharm_8 &&
                !removed.playerData.equippedCharm_29 && removed.playerData.charmSlotsFilled == pd.charmCost_1 && !removed.playerData.overcharmed,
                "explicit unequip via list API and false flag survives save with correct notches");
            pd.SetBool("equippedCharm_8", true); pd.EquipCharm(8);
            ResourceCheck(!pd.GetBool("equippedCharm_8") && !pd.equippedCharms.Contains(8), "removed forbidden charm still cannot be re-equipped");
        }
        yield return ResourceBarrier("rest-unequip");
        if (!host) {
            engine.ClientManager.Disconnect();
            ResourceCheck(!runtime.LinkedHealthRuleEnabled && pd.equippedCharms.SequenceEqual(new[] { 1, 8, 29 }) && pd.equippedCharm_8 &&
                pd.equippedCharm_29 && pd.GetInt("maxHealth") == 5, "disconnect restores original loadout and removes mode hooks");
            hero.CharmUpdate();
            ResourceCheck(pd.healthBlue == 2 && pd.health == 5, "native charm healing works again after mode exit");
            Flag("guest.rest-disconnected");
        } else {
            yield return Until(() => Has("guest.rest-disconnected"), "guest verified mode exit");
            engine.ClientManager.Disconnect();
            ResourceCheck(!runtime.LinkedHealthRuleEnabled && pd.equippedCharms.SequenceEqual(new[] { 1 }) &&
                !pd.equippedCharm_8 && !pd.equippedCharm_29 && pd.charmSlotsFilled == pd.charmCost_1,
                "disconnect never restores explicitly unequipped forbidden charms");
        }
    }
}
