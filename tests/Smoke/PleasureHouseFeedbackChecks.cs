using System;
using System.Collections;
using System.Linq;
using HutongGames.PlayMaker.Actions;
using UnityEngine;
using Object = UnityEngine.Object;

namespace HollowConnect.Smoke;

public sealed partial class ProgressProbe {
    private IEnumerator PleasureHouseFeedbackChecks(HealthManager boss) {
        var pd = PlayerData.instance;
        pd.SetInt("simpleKeys", 2);
        if (!host) {
            yield return Until(() => HeroController.instance.acceptingInput && !HeroController.instance.controlReqlinquished,
                "native Lost Kin intro releases control before key user leaves", 20);
            Enter("Ruins2_04");
            yield return Until(() => AliveScene("Ruins2_04") && engine.ClientManager.SceneSnapshotReady, "key user leaves teammate inside Lost Kin dream", 35);
            Freeze();
            var fsms = Object.FindObjectsOfType<PlayMakerFSM>(true).Where(f => f.gameObject.scene.name == "Ruins2_04").ToArray();
            var door = fsms.Single(f => f.name == "Bathhouse Door");
            var conversation = fsms.Single(f => f.name == "Inspect" && f.FsmName == "Conversation Control" &&
                f.FsmStates.SelectMany(s => s.Actions).OfType<SetPlayerDataBool>().Any(a => a.boolName.Value == "bathHouseOpened"));
            Mark("PLEASURE PREFLIGHT door=" + door.ActiveStateName + " flags=" + pd.GetBool("bathHouseOpened") + " keys=" + pd.GetInt("simpleKeys"));
            Dump(door);
            foreach (var fsm in conversation.GetComponents<PlayMakerFSM>()) Dump(fsm);
            var contact = conversation.GetComponents<PlayMakerFSM>().Single(f => f.FsmStates.SelectMany(s => s.Actions).OfType<ListenForUp>().Any());
            var range = conversation.GetComponent<Collider2D>();
            if (range == null) throw new Exception("Pleasure House native inspection collider absent");
            Mark("PLEASURE RANGE " + range.bounds + " hero=" + HeroController.instance.GetComponent<Collider2D>().bounds + " state=" + contact.ActiveStateName);
            HeroController.instance.transform.position = range.bounds.center;
            Physics2D.SyncTransforms();
            yield return new WaitForFixedUpdate();
            yield return Until(() => contact.Fsm.ActiveState.Actions.OfType<ListenForUp>().Any(), "native Pleasure House inspection in range", 10);
            contact.Fsm.Event(contact.Fsm.ActiveState.Actions.OfType<ListenForUp>().Single().wasPressed);
            yield return Until(() => conversation.ActiveStateName == "Send Text" && Dialogue.ActiveStateName == "Ready for Input", "native Pleasure House key confirmation", 15);
            ChooseYes();
            yield return Until(() => pd.GetBool("bathHouseOpened") && pd.GetInt("simpleKeys") == 1 && HeroController.instance.acceptingInput,
                "native Pleasure House unlock spends exactly one local key", 15);
            Mark("PLEASURE OPEN localFlag=" + pd.GetBool("bathHouseOpened") + " localKeys=" + pd.GetInt("simpleKeys"));
            Flag("pleasure.opened");
            yield return Until(() => Has("host.pleasure.checked"), "dream teammate checks shared Pleasure House unlock", 100);
        } else {
            yield return Until(() => Has("pleasure.opened"), "teammate unlocks Pleasure House while this player remains in dream", 70);
            yield return new WaitForSecondsRealtime(2);
            Mark("PLEASURE DREAM RECEIPT flag=" + pd.GetBool("bathHouseOpened") + " keys=" + pd.GetInt("simpleKeys"));
            boss.ApplyExtraDamage(boss.hp);
            yield return Until(() => GameManager.instance.sceneName == "Abyss_19", "native Lost Kin completion returns remaining player", 70);
            var wake = HeroController.instance.GetComponents<PlayMakerFSM>().Single(f => f.FsmName == "Dream Return");
            yield return Until(() => wake.ActiveStateName == "Ready", "remaining dream player wakes normally", 20);
            wake.Fsm.Event(wake.Fsm.ActiveState.Actions.OfType<ListenForJump>().Single().wasPressed);
            yield return Until(() => AliveScene("Abyss_19"), "remaining dream player's input restored");
            if (!pd.GetBool("infectedKnightDreamDefeated")) throw new Exception("Remaining Lost Kin participant lost completion");
            Enter("Ruins2_04");
            yield return Until(() => AliveScene("Ruins2_04") && engine.ClientManager.SceneSnapshotReady, "dream player visits Pleasure House after completion", 35);
            Freeze(); yield return new WaitForSecondsRealtime(2);
            var door = Object.FindObjectsOfType<PlayMakerFSM>(true).Single(f => f.name == "Bathhouse Door" && f.gameObject.scene.name == "Ruins2_04");
            var entrance = door.FsmVariables.GetFsmGameObject("Door Object").Value;
            Mark("PLEASURE AFTER DREAM flag=" + pd.GetBool("bathHouseOpened") + " keys=" + pd.GetInt("simpleKeys") +
                " doorState=" + door.ActiveStateName + " entrance=" + entrance?.name + " active=" + entrance?.activeInHierarchy);
            if (!pd.GetBool("bathHouseOpened") || door.ActiveStateName != "Opened" || entrance == null || !entrance.GetComponent<Collider2D>().enabled)
                throw new Exception("Pleasure House remains locked after teammate spent a key in another scene");
            if (pd.GetInt("simpleKeys") != 2) throw new Exception("Remote Pleasure House unlock consumed dream teammate's personal key");
            Flag("host.pleasure.checked");
        }
    }
}
