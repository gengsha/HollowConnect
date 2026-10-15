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
    private IEnumerator DreamWarriors() {
        if (Environment.GetEnvironmentVariable("HOLLOWCONNECT_DREAM_WARRIORS") != "inspect") {
            var scenes = Environment.GetEnvironmentVariable("HOLLOWCONNECT_DREAM_WARRIORS") == "all"
                ? new[] { "RestingGrounds_02", "Cliffs_02", "Fungus2_32", "Fungus3_40", "Fungus1_35", "Deepnest_40", "Deepnest_East_10" }
                : new[] { "RestingGrounds_02" };
            foreach (var scene in scenes) {
                yield return DreamWarriorBattle(scene);
                if (Has(role + ".failed")) yield break;
            }
            yield break;
        }
        foreach (var scene in new[] { "RestingGrounds_02", "Cliffs_02", "Fungus2_32", "Fungus3_40", "Fungus1_35", "Deepnest_40", "Deepnest_East_10" }) {
            yield return Room(scene);
            if (Has(role + ".failed")) yield break;
            yield return new WaitForSecondsRealtime(2);
            foreach (var f in Object.FindObjectsOfType<PlayMakerFSM>(true).Where(f => f.gameObject.scene.name.StartsWith(scene, StringComparison.Ordinal) &&
                (f.name.IndexOf("Ghost", StringComparison.OrdinalIgnoreCase) >= 0 || f.name.IndexOf("Warrior", StringComparison.OrdinalIgnoreCase) >= 0))) Dump(f);
            Flag(role + ".inspected." + scene);
            yield return Until(() => Has((host ? "guest" : "host") + ".inspected." + scene), "both inspected " + scene);
        }
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
        var npc = Object.FindObjectsOfType<PlayMakerFSM>(true).Single(f => f.name == "Ghost Warrior NPC" && f.FsmName == "Conversation Control");
        var control = npc.GetComponents<PlayMakerFSM>().Single(f => f.FsmName == "npc_control");
        var key = npc.FsmVariables.GetFsmString("PD Int Name").Value;
        var reward = npc.FsmVariables.GetFsmInt("Dream Orbs").Value;
        Mark("WARRIOR NATIVE " + scene + " key=" + key + " reward=" + reward);
        var summoner = role == (Environment.GetEnvironmentVariable("HOLLOWCONNECT_DREAM_SUMMONER") ?? "host");
        var pd = PlayerData.instance;
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
        if (pd.GetInt(key) != 0) throw new Exception("Non-authority announced warrior victory before fight");
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
        yield return Until(() => npc.ActiveStateName == "Start Fight", "non-summoner native ghost presentation joins battle", 10);
        if (Has(role + ".failed")) yield break;
        Flag(role + ".warrior-fighting." + scene);
        yield return Until(() => Has((host ? "guest" : "host") + ".warrior-fighting." + scene), "both clients see fight");
        if (pd.GetInt(key) != 0) throw new Exception("Warrior became defeated before lethal hit");
        if (!manager.IsSceneHost && Field<System.Collections.Generic.Dictionary<ulong, Entity>>(manager, "_entities").Values.Where(Warrior).Any(e => e.Object.Host.activeInHierarchy))
            throw new Exception("Participant started an independent original warrior");
        Mark("PASS challenge preserves zero victory state and single native authority");
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
        yield return Until(() => pd.GetInt(key) == 1, "both players can claim personal warrior reward", 5);
        if (Has(role + ".failed")) yield break;
        yield return Until(() => npc.ActiveStateName == "Idle" && npc.GetComponents<PlayMakerFSM>().Single(f => f.FsmName == "Appear").ActiveStateName == "Reappear",
            "native warrior death animation restores reward ghost", 15);
        yield return new WaitForSecondsRealtime(2);
        var orbs = pd.GetInt("dreamOrbs");
        if (!summoner) {
            yield return Until(() => Has("summoner.reward." + scene), "summoner claims first while staying in room");
            yield return Until(() => pd.GetInt(key) == 1 && pd.GetInt("dreamOrbs") == orbs, "teammate collection preserves personal unclaimed reward");
        }
        HeroController.instance.transform.position = npc.transform.position + new Vector3(1, 0, 0);
        Freeze();
        control.SetState("Take Control");
        yield return Until(() => npc.ActiveStateName == "Defeated", "non-summoner receives defeat dialogue instead of challenge");
        npc.SendEvent("CONVO_FINISH");
        yield return Until(() => pd.GetInt(key) == 2 && pd.GetInt("dreamOrbs") == orbs + reward, "each player receives exactly one native essence reward", 20);
        if (summoner) Flag("summoner.reward." + scene);
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
        if (pd.GetInt(key) != 2 || pd.GetInt("dreamOrbs") != orbs + reward || npc.ActiveStateName == "Ready" || npc.ActiveStateName == "Start Fight")
            throw new Exception("Duplicate or older warrior state replayed claim or challenge");
        Mark("PASS duplicate completion and older challenge cannot replay essence or restart fight");
        Flag(role + ".warrior-complete." + scene);
        yield return Until(() => Has((host ? "guest" : "host") + ".warrior-complete." + scene), "both warrior checks complete");
    }
}
