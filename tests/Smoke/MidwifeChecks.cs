using System;
using System.Collections;
using System.Linq;
using HutongGames.PlayMaker;
using UnityEngine;
using Object = UnityEngine.Object;

namespace HollowConnect.Smoke;

public sealed partial class ProgressProbe {
    private bool MidwifeCase => Environment.GetEnvironmentVariable("HOLLOWCONNECT_MIDWIFE_CASE") != null;

    private IEnumerator MidwifeChecks() {
        yield return Room("Deepnest_41");
        var midwife = GameObject.Find("Happy Spider NPC");
        ResourceCheck(midwife != null, "native Midwife present in Deepnest_41");
        foreach (var f in Object.FindObjectsOfType<PlayMakerFSM>(true).Where(f =>
            f.transform.IsChildOf(midwife.transform) || f.name == "Text")) {
            Mark("MIDWIFE SCHEMA " + f.name + "/" + f.FsmName + " state=" + f.ActiveStateName +
                " position=" + f.transform.position + " active=" + f.gameObject.activeInHierarchy);
            Dump(f);
        }
        foreach (var c in midwife.GetComponentsInChildren<Collider2D>(true))
            Mark("MIDWIFE COLLIDER " + c.name + "=" + c.bounds + " enabled=" + c.enabled);
        Flag(role + ".midwife.inspect");
        yield return Until(() => Has((host ? "guest" : "host") + ".midwife.inspect"), "both inspected native Midwife");
        if (Environment.GetEnvironmentVariable("HOLLOWCONNECT_MIDWIFE_CASE") == "inspect") yield break;
        var talker = role == (Environment.GetEnvironmentVariable("HOLLOWCONNECT_MIDWIFE_TALKER") ?? "host");
        var hero = HeroController.instance;
        for (var round = 0; round < 3; round++) {
            if (round > 0) {
                // Midwife attacks/retreats after native dialogue. Reload the
                // room normally; retain the naturally earned conversation flags.
                yield return Room("Town");
                yield return Room("Deepnest_41");
                midwife = GameObject.Find("Happy Spider NPC");
                ResourceCheck(midwife != null, "Midwife returned after native room reentry");
            }
            var npc = midwife.transform.Find("NPC").gameObject;
            var control = npc.GetComponents<PlayMakerFSM>().Single(f => f.FsmName == "npc_control");
            var convo = npc.GetComponents<PlayMakerFSM>().Single(f => f.FsmName == "Conversation Control");
            var behavior = midwife.GetComponents<PlayMakerFSM>().Single(f => f.FsmName == "Control");
            var anchor = midwife.transform.position;
            var remoteHits = 0;
            var retreated = false;
            On.HutongGames.PlayMaker.Fsm.hook_EnterState trace = (orig, fsm, state) => {
                if (fsm.GameObject == midwife && fsm.Name == "Control" && state.Name == "Retreated") retreated = true;
                if (fsm.GameObject != null && (fsm.GameObject.transform.IsChildOf(midwife.transform) || fsm.GameObject.name == "Text"))
                    Mark("MIDWIFE STATE " + fsm.GameObject.name + "/" + fsm.Name + " -> " + state.Name);
                orig(fsm, state);
            };
            On.HutongGames.PlayMaker.Fsm.hook_ProcessEvent contact = (orig, fsm, ev, data) => {
                if (fsm.GameObject == midwife && fsm.Name == "tink_effect" && ev?.Name == "TAKE DAMAGE") {
                    var source = data?.SentByGameObject ?? data?.SentByFsm?.GameObject;
                    var remote = source != null && source.GetComponentsInParent<Component>(true).Any(c => c.GetType().Name == "RemoteVisualOpacity");
                    if (remote) remoteHits++;
                    Mark("MIDWIFE CONTACT source=" + source?.name + " remote=" + remote + " talking=" + control.FsmVariables.FindFsmBool("Talking")?.Value);
                }
                orig(fsm, ev, data);
            };
            On.HutongGames.PlayMaker.Fsm.EnterState += trace;
            On.HutongGames.PlayMaker.Fsm.ProcessEvent += contact;
            try {
                hero.transform.position = anchor + new Vector3(-2, 1, 0);
                hero.AffectedByGravity(true); Physics2D.SyncTransforms();
                yield return Until(() => hero.cState.onGround && behavior.ActiveStateName == "Idle",
                    "native Midwife floor/appearance; behavior=" + behavior.ActiveStateName, 8);
                if (talker) {
                    yield return Until(() => control.ActiveStateName.StartsWith("In Range"), "native Midwife interaction range", 8);
                    yield return ResourcePress(InputHandler.Instance.inputActions.up);
                    var expected = round == 0 ? "Meet" : round == 1 ? "Convo1" : "Repeat";
                    yield return Until(() => convo.ActiveStateName == expected, "native Midwife conversation " + expected, 8);
                    ResourceCheck(!hero.acceptingInput && PlayerData.instance.GetBool("disablePause") &&
                        control.FsmVariables.FindFsmBool("Talking").Value, "native Midwife conversation owns input");
                    Flag("midwife.talking." + round);
                    yield return Until(() => Has("midwife.attacked." + round), "peer sent actual Midwife nail attack", 8);
                    yield return new WaitForSecondsRealtime(.5f);
                    Mark("MIDWIFE AFTER ATTACK npc=" + npc.activeInHierarchy + " control=" + control.ActiveStateName +
                        " convo=" + convo.ActiveStateName + " behavior=" + behavior.ActiveStateName + " input=" + hero.acceptingInput);
                    ResourceCheck(remoteHits > 0, "real UDP remote slash contacted Midwife during dialogue");
                    ResourceCheck(npc.activeInHierarchy && convo.ActiveStateName == expected && behavior.ActiveStateName == "Idle" &&
                        !hero.acceptingInput && PlayerData.instance.GetBool("disablePause"), "remote nail preserves active Midwife dialogue and native input lock");
                    ResourceCheck(Object.FindObjectsOfType<DialogueBox>(true).Single(d => d.name == "Text").currentConversation ==
                        (round == 0 ? "SPIDER_MEET" : round == 1 ? "SPIDER_GREET" : "SPIDER_REPEAT"), "native Midwife text conversation remains selected");
                    var end = Time.realtimeSinceStartup + 15;
                    while (!hero.acceptingInput && Time.realtimeSinceStartup < end) {
                        yield return ResourcePress(InputHandler.Instance.inputActions.jump);
                        yield return new WaitForSecondsRealtime(.25f);
                    }
                    ResourceCheck(hero.acceptingInput && !PlayerData.instance.GetBool("disablePause") &&
                        !control.FsmVariables.FindFsmBool("Talking").Value, "native Midwife dialogue closes and restores control/pause");
                    yield return Until(() => behavior.ActiveStateName == "Attack Idle" || behavior.ActiveStateName == "Antic" ||
                        behavior.ActiveStateName == "Attack", "native Midwife post-conversation attack", 8);
                    hero.transform.position = anchor + new Vector3(-1.8f, 0, 0);
                    hero.FaceRight(); hero.Attack(GlobalEnums.AttackDirection.normal); Physics2D.SyncTransforms();
                    yield return Until(() => retreated, "post-dialogue nail hit retains native Midwife retreat", 8);
                    ResourceCheck(hero.acceptingInput && !PlayerData.instance.GetBool("disablePause"), "post-dialogue hit leaves control intact");
                    Flag("midwife.closed." + round);
                } else {
                    yield return Until(() => Has("midwife.talking." + round), "peer opened native Midwife dialogue", 20);
                    hero.transform.position = anchor + new Vector3(-1.8f, 0, 0);
                    hero.FaceRight(); hero.Attack(GlobalEnums.AttackDirection.normal); Physics2D.SyncTransforms();
                    yield return Until(() => retreated, "attacker's idle Midwife retains native hit/retreat", 8);
                    Flag("midwife.attacked." + round);
                    yield return Until(() => Has("midwife.closed." + round), "peer closes Midwife dialogue and checks native hit", 30);
                }
            } finally {
                On.HutongGames.PlayMaker.Fsm.EnterState -= trace;
                On.HutongGames.PlayMaker.Fsm.ProcessEvent -= contact;
            }
        }
        Flag(role + ".midwife.finished");
        yield return Until(() => Has((host ? "guest" : "host") + ".midwife.finished"), "both Midwife assertions complete");
    }
}
