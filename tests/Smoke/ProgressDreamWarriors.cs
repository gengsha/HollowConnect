using System;
using System.Collections;
using System.Linq;
using Hkmp.Game.Client.Entity;
using HutongGames.PlayMaker;
using Hkmp.Game.Client.Entity.Component;
using Hkmp.Networking.Client;
using Hkmp.Networking.Packet.Data;
using UnityEngine;
using Object = UnityEngine.Object;

namespace HollowConnect.Smoke;

public sealed partial class ProgressProbe {
    private string[] DreamWarriorScenes() {
        var selected = Environment.GetEnvironmentVariable("HOLLOWCONNECT_DREAM_SCENES");
        if (!string.IsNullOrEmpty(selected)) {
            var scenes = selected.Split(',');
            if (scenes.Any(s => !DreamWarriorSynchronizer.Keys.ContainsKey(s))) throw new Exception("Unknown warrior scene");
            return scenes;
        }
        return Environment.GetEnvironmentVariable("HOLLOWCONNECT_DREAM_WARRIORS") == "all" ||
            Environment.GetEnvironmentVariable("HOLLOWCONNECT_DREAM_WARRIORS") == "inspect"
            ? DreamWarriorSynchronizer.Keys.Keys.ToArray() : new[] { "RestingGrounds_02" };
    }
    private IEnumerator WarriorSharedEquipment() {
        var owner = Environment.GetEnvironmentVariable("HOLLOWCONNECT_DREAM_LANTERN_OWNER");
        if (string.IsNullOrEmpty(owner)) yield break;
        yield return Until(() => runtime.Peers.Count == 1 && PlayerData.instance.GetBool("hasLantern"), "shared lantern reaches both before warrior room load");
        Flag(role + ".warrior-equipment");
        yield return Until(() => Has((host ? "guest" : "host") + ".warrior-equipment"), "both new saves have the real shared prerequisite");
    }
    private void TraceWarriorRoom(string scene) {
        Mark("WARRIOR ROOM " + scene + " pd=" + PlayerData.instance.GetInt(DreamWarriorSynchronizer.Keys[scene]) +
            " lantern=" + PlayerData.instance.GetBool("hasLantern") + " dreamNail=" + PlayerData.instance.GetBool("hasDreamNail") +
            " nativeLantern=" + (HollowConnect.CoopMod.Statistics.NativePickups?.ContainsKey("hasLantern") ?? false));
        foreach (var f in Object.FindObjectsOfType<PlayMakerFSM>(true).Where(f => f.gameObject.scene.name.StartsWith(scene, StringComparison.Ordinal) &&
            (f.name.IndexOf("Ghost", StringComparison.OrdinalIgnoreCase) >= 0 || f.name.IndexOf("Warrior", StringComparison.OrdinalIgnoreCase) >= 0))) {
            Mark("WARRIOR FSM " + f.name + "/" + f.FsmName + " active=" + f.gameObject.activeInHierarchy + " enabled=" + f.enabled + " state=" + f.ActiveStateName);
            Dump(f);
        }
        foreach (var c in Object.FindObjectsOfType<SceneAdditiveLoadConditional>(true).Where(c => c.gameObject.scene.name == scene))
            Mark("WARRIOR CONDITIONAL " + c.name + " " + string.Join(";", c.GetType().GetFields(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance).Select(f => f.Name + "=" + Describe(f.GetValue(c)))));
    }
    private IEnumerator DreamWarriors() {
        if (Environment.GetEnvironmentVariable("HOLLOWCONNECT_DREAM_VARIANT_SCHEMA") == "1") {
            yield return DreamVariantSchema();
            yield break;
        }
        yield return WarriorSharedEquipment();
        if (Has(role + ".failed")) yield break;
        if (Environment.GetEnvironmentVariable("HOLLOWCONNECT_DREAM_WARRIORS") != "inspect") {
            var scenes = DreamWarriorScenes();
            foreach (var scene in scenes) {
                yield return DreamWarriorBattle(scene);
                if (Has(role + ".failed")) yield break;
            }
            if (Environment.GetEnvironmentVariable("HOLLOWCONNECT_WARRIOR_EXIT") == "f8") yield return WarriorF8Exit();
            if (Environment.GetEnvironmentVariable("HOLLOWCONNECT_WARRIOR_EXIT") == "native") yield return WarriorNativeExit();
            yield break;
        }
        foreach (var scene in DreamWarriorScenes()) {
            yield return Room(scene);
            if (Has(role + ".failed")) yield break;
            yield return new WaitForSecondsRealtime(2);
            TraceWarriorRoom(scene);
            Flag(role + ".inspected." + scene);
            yield return Until(() => Has((host ? "guest" : "host") + ".inspected." + scene), "both inspected " + scene);
        }
    }

    private IEnumerator WarriorF8Exit() {
        if (host) {
            Enter("Town");
            yield return Until(() => AliveScene("Town"), "F8 target leaves completed warrior room");
            HeroController.instance.AffectedByGravity(true);
            yield return Until(() => HeroController.instance.cState.onGround, "F8 target has native ground support");
            Flag("warrior.f8-target");
        } else {
            yield return Until(() => Has("warrior.f8-target") && runtime.Peers.Values.Any(p => p.State?.Scene == "Town" && p.State.IsReady), "completed warrior guest sees ready Town target");
            runtime.Selected = runtime.Peers.Keys.Single();
            var pressed = false;
            runtime.KeyPressed = key => key == HollowConnect.CoopMod.Settings.TeleportKey && !pressed && (pressed = true);
            yield return Until(() => pressed && AliveScene("Town") && !runtime.Returning && !runtime.Down, "F8 leaves completed warrior additive room");
            runtime.KeyPressed = _ => false;
        }
        yield return Until(() => !UnityEngine.SceneManagement.SceneManager.GetSceneByName("Cliffs_02_boss").isLoaded, "F8 path cleans up Gorb additive scene");
        Flag(role + ".warrior-exit");
        yield return Until(() => Has((host ? "guest" : "host") + ".warrior-exit"), "both completed warrior F8 exit");
    }

    private IEnumerator WarriorNativeExit() {
        var scene = UnityEngine.SceneManagement.SceneManager.GetActiveScene().name;
        var gates = Object.FindObjectsOfType<TransitionPoint>().Where(t => t.gameObject.scene.name == scene && !string.IsNullOrEmpty(t.targetScene)).OrderBy(t => t.name).ToArray();
        Mark("WARRIOR EXIT gates=" + string.Join(",", gates.Select(t => t.name + "->" + t.targetScene + "/" + t.entryPoint)));
        var gate = gates.OrderBy(t => t.targetScene == "Town" ? 0 : 1).First();
        var destination = gate.targetScene;
        var trigger = gate.GetComponent<Collider2D>();
        var hero = HeroController.instance;
        hero.RegainControl(); hero.StartAnimationControl(); hero.AffectedByGravity(false);
        hero.transform.position = trigger.bounds.center;
        hero.GetComponent<Rigidbody2D>().velocity = Vector2.zero;
        Physics2D.SyncTransforms();
        yield return Until(() => AliveScene(destination), "real warrior exit trigger loads native destination");
        yield return Until(() => !UnityEngine.SceneManagement.SceneManager.GetSceneByName(scene + "_boss").isLoaded, "real exit cleans up warrior additive scene");
        Flag(role + ".warrior-exit");
        yield return Until(() => Has((host ? "guest" : "host") + ".warrior-exit"), "both completed warrior native exit");
    }

    private IEnumerator DreamWarriorBattle(string scene) {
        if (GameManager.instance.sceneName != "Town") {
            var previous = Hkmp.CoopContext.LocalScene;
            Enter("Town");
            yield return Until(() => AliveScene("Town") && !UnityEngine.SceneManagement.SceneManager.GetSceneByName(previous + "_boss").isLoaded,
                "previous native warrior additive scene unloaded");
            Freeze();
            Flag(role + ".warrior-town." + scene);
            yield return Until(() => Has((host ? "guest" : "host") + ".warrior-town." + scene), "both returned through neutral scene");
        }
        yield return Room(scene);
        yield return new WaitForSecondsRealtime(2);
        TraceWarriorRoom(scene);
        var npc = Object.FindObjectsOfType<PlayMakerFSM>(true).Single(f => f.name == "Ghost Warrior NPC" && f.FsmName == "Conversation Control");
        var control = npc.GetComponents<PlayMakerFSM>().Single(f => f.FsmName == "npc_control");
        var key = npc.FsmVariables.GetFsmString("PD Int Name").Value;
        var reward = npc.FsmVariables.GetFsmInt("Dream Orbs").Value;
        Mark("WARRIOR NATIVE " + scene + " key=" + key + " reward=" + reward);
        var summoner = role == (Environment.GetEnvironmentVariable("HOLLOWCONNECT_DREAM_SUMMONER") ?? "host");
        var pd = PlayerData.instance;
        var initialState = host ? 0 : WarriorGuestHistory;
        var initialOrbs = pd.GetInt("dreamOrbs");
        var manager = Field<EntityManager>(engine.ClientManager, "_entityManager");
        var net = Field<NetClient>(engine.ClientManager, "_netClient");
        if (!manager.IsSceneHost) {
            var forged = new EntityNetworkData { Type = EntityComponentType.DreamWarrior };
            forged.Packet.Write(key); forged.Packet.Write((byte)2);
            net.UpdateManager.AddEntityData(0UL, forged);
        }
        Flag(role + ".warrior-validation." + scene);
        yield return Until(() => Has((host ? "guest" : "host") + ".warrior-validation." + scene), "both ready for scoped completion validation");
        yield return new WaitForSecondsRealtime(.6f);
        if (pd.GetInt(key) != initialState) throw new Exception("Non-authority changed personal warrior history before fight");
        Mark("PASS non-authority cannot announce warrior defeat");
        HeroController.instance.transform.position = npc.transform.position + new Vector3(1, 0, 0);
        if (summoner) {
            control.SetState("Take Control");
            yield return Until(() => npc.ActiveStateName == "Talk", "native warrior introduction");
            npc.SendEvent("CONVO_FINISH");
            yield return Until(() => npc.ActiveStateName == "Send Text" && Dialogue.ActiveStateName == "Ready for Input", "native warrior challenge prompt");
            ChooseYes();
            yield return Until(() => npc.ActiveStateName == "Start Fight", "one player starts native warrior battle");
            Flag(scene + ".warrior.started");
        } else yield return Until(() => Has(scene + ".warrior.started"), "teammate alone accepted challenge");
        bool Warrior(Entity e) => e.Object.Host != null && e.Object.Host.name.StartsWith("Ghost Warrior ") && e.Object.Host.GetComponent<HealthManager>() != null;
        yield return Until(() => Field<System.Collections.Generic.Dictionary<ulong, Entity>>(manager, "_entities").Values.Any(e =>
            Warrior(e) && (manager.IsSceneHost ? e.Object.Host : e.Object.Client).activeInHierarchy), "network warrior is active on both clients");
        if (initialState < 2)
            yield return Until(() => npc.ActiveStateName == "Start Fight", "non-summoner native ghost presentation joins battle", 10);
        if (Has(role + ".failed")) yield break;
        Flag(role + ".warrior-fighting." + scene);
        yield return Until(() => Has((host ? "guest" : "host") + ".warrior-fighting." + scene), "both clients see fight");
        if (pd.GetInt(key) != initialState) throw new Exception("Battle changed personal warrior history before lethal hit");
        if (!manager.IsSceneHost && Field<System.Collections.Generic.Dictionary<ulong, Entity>>(manager, "_entities").Values.Where(Warrior).Any(e => e.Object.Host.activeInHierarchy))
            throw new Exception("Participant started an independent original warrior");
        Mark("PASS challenge preserves personal victory state and single native authority");
        Mark("WARRIOR BEFORE DEATH npc=" + npc.ActiveStateName + " pd=" + pd.GetInt(key) + " orbs=" + pd.GetInt("dreamOrbs"));
        var synchronizer = Field<object>(Field<object>(manager, "_bossRoom"), "warriors");
        Mark("WARRIOR SYNC scene=" + Hkmp.CoopContext.LocalScene + " host=" + manager.IsSceneHost + " conversation=" + Field<PlayMakerFSM>(synchronizer, "conversation") +
            " desired=" + Field<byte>(synchronizer, "desired") + " offered=" + Field<byte>(synchronizer, "offered") + " applied=" + Field<byte>(synchronizer, "applied") +
            " time=" + Time.unscaledTime + " next=" + Field<float>(synchronizer, "next"));
        if (manager.IsSceneHost) {
            var boss = Field<System.Collections.Generic.Dictionary<ulong, Entity>>(manager, "_entities").Values.Single(Warrior).Object.Host.GetComponent<HealthManager>();
            boss.Hit(new HitInstance { Source = HeroController.instance.gameObject, AttackType = AttackTypes.Nail,
                DamageDealt = 10000, Multiplier = 1, IgnoreInvulnerable = true, Direction = 0, MagnitudeMultiplier = 1 });
        }
        yield return new WaitForSecondsRealtime(2);
        Mark("WARRIOR AFTER DEATH npc=" + npc.ActiveStateName + " pd=" + pd.GetInt(key) + " orbs=" + pd.GetInt("dreamOrbs"));
        yield return Until(() => pd.GetInt(key) == Math.Max(1, initialState), "completion preserves personal claimed or unclaimed history", 5);
        if (Has(role + ".failed")) yield break;
        if (initialState == 2) {
            yield return Until(() => Field<byte>(synchronizer, "applied") == 2, "claimed guest receives encounter completion", 15);
            yield return Until(() => Has("summoner.reward." + scene), "host collects while claimed guest never interacts");
            yield return new WaitForSecondsRealtime(2);
            TraceWarriorRoom(scene);
            Mark("WARRIOR CLAIMED BEFORE INTERACT npc=" + npc.ActiveStateName + " orbs=" + pd.GetInt("dreamOrbs"));
            yield return WarriorClaimedInteraction(scene, npc, control);
            if (Has(role + ".failed")) yield break;
            if (pd.GetInt(key) != 2 || pd.GetInt("dreamOrbs") != initialOrbs)
                throw new Exception("Already claimed guest received essence again");
            Mark("PASS already claimed participant keeps essence and native inspection without placeholder UI");
        } else {
            yield return Until(() => npc.ActiveStateName == "Idle" && npc.GetComponents<PlayMakerFSM>().Single(f => f.FsmName == "Appear").ActiveStateName == "Reappear",
                "native warrior death animation restores reward ghost", 15);
            yield return new WaitForSecondsRealtime(2);
            var orbs = pd.GetInt("dreamOrbs");
            if (!summoner) {
                yield return Until(() => Has("summoner.reward." + scene), "summoner claims first while staying in room");
                yield return Until(() => pd.GetInt(key) == 1 && pd.GetInt("dreamOrbs") == orbs + reward,
                    "teammate essence shares while local native conversation remains unclaimed");
            }
            HeroController.instance.transform.position = npc.transform.position + new Vector3(1, 0, 0);
            Freeze();
            control.SetState("Take Control");
            yield return Until(() => npc.ActiveStateName == "Defeated", "non-summoner receives defeat dialogue instead of challenge");
            npc.SendEvent("CONVO_FINISH");
            yield return Until(() => pd.GetInt(key) == 2 && pd.GetInt("dreamOrbs") == orbs + reward, "each player receives exactly one native essence reward", 20);
            if (summoner) Flag("summoner.reward." + scene);
        }
        Flag(role + ".warrior-reward." + scene);
        yield return Until(() => Has((host ? "guest" : "host") + ".warrior-reward." + scene), "both collected personal essence");
        yield return Until(() => HeroController.instance.acceptingInput && !pd.GetBool("disablePause"), "native reward releases input and pause", 10);
        var receive = synchronizer.GetType().GetMethod("Receive", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
        foreach (var stage in new byte[] { 2, 2, 1 }) {
            var duplicate = new EntityNetworkData { Type = EntityComponentType.DreamWarrior };
            duplicate.Packet.Write(key); duplicate.Packet.Write(stage);
            duplicate.Packet = new Hkmp.Networking.Packet.Packet(duplicate.Packet.ToArray());
            receive.Invoke(synchronizer, new object[] { duplicate });
        }
        yield return new WaitForSecondsRealtime(.6f);
        if (pd.GetInt(key) != 2 || pd.GetInt("dreamOrbs") != initialOrbs + (initialState == 2 ? 0 : reward) || npc.ActiveStateName == "Ready" || npc.ActiveStateName == "Start Fight")
            throw new Exception("Duplicate or older warrior state replayed claim or challenge");
        Mark("PASS duplicate completion and older challenge cannot replay essence or restart fight");
        Flag(role + ".warrior-complete." + scene);
        yield return Until(() => Has((host ? "guest" : "host") + ".warrior-complete." + scene), "both warrior checks complete");
    }
}
