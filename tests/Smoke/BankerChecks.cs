using System;
using System.Collections;
using System.Linq;
using HutongGames.PlayMaker;
using UnityEngine;
using Object = UnityEngine.Object;

namespace HollowConnect.Smoke;

public sealed partial class ProgressProbe {
    private IEnumerator BankerChecks() {
        yield return Room("Ruins_Bathhouse");
        var banker = GameObject.Find("Banker Spa NPC");
        ResourceCheck(banker != null, "native banker present after theft");
        var npc = banker.transform.Find("NPC").gameObject;
        var control = npc.GetComponents<PlayMakerFSM>().Single(f => f.FsmName == "npc_control");
        var convo = npc.GetComponents<PlayMakerFSM>().Single(f => f.FsmName == "Conversation Control");
        foreach (var f in Object.FindObjectsOfType<PlayMakerFSM>(true).Where(f =>
            f.transform.IsChildOf(banker.transform) || f.name == "Text" || f.name == "Text Dream")) {
            Mark("BANKER SCHEMA " + f.name + "/" + f.FsmName + " state=" + f.ActiveStateName +
                " position=" + f.transform.position + " active=" + f.gameObject.activeInHierarchy +
                " components=" + string.Join(",", f.GetComponents<Component>().Select(c => c.GetType().Name)));
            Dump(f);
        }
        Flag(role + ".banker.inspect");
        yield return Until(() => Has((host ? "guest" : "host") + ".banker.inspect"), "both inspected native banker");
        if (Environment.GetEnvironmentVariable("HOLLOWCONNECT_BANKER_CASE") == "inspect") yield break;
        var talker = role == (Environment.GetEnvironmentVariable("HOLLOWCONNECT_BANKER_TALKER") ?? "host");
        var hero = HeroController.instance;
        var anchor = banker.transform.position;
        var remoteHits = 0;
        var gaveGeo = false;
        foreach (var c in banker.GetComponentsInChildren<Collider2D>(true)) Mark("BANKER COLLIDER " + c.name + "=" + c.bounds + " enabled=" + c.enabled);
        On.HutongGames.PlayMaker.Fsm.hook_EnterState trace = (orig, fsm, state) => {
            if (fsm.GameObject == banker && fsm.Name == "Hit Around" && state.Name == "Give Geo") gaveGeo = true;
            if (fsm.GameObject != null && (fsm.GameObject.transform.IsChildOf(banker.transform) || fsm.GameObject.name == "Text"))
                Mark("BANKER STATE " + fsm.GameObject.name + "/" + fsm.Name + " -> " + state.Name);
            orig(fsm, state);
        };
        On.HutongGames.PlayMaker.Fsm.hook_ProcessEvent contact = (orig, fsm, ev, data) => {
            if (fsm.GameObject == banker && fsm.Name == "tink_effect" && ev?.Name == "TAKE DAMAGE") {
                var source = data?.SentByGameObject ?? data?.SentByFsm?.GameObject;
                var remote = source != null && source.GetComponentsInParent<Component>(true).Any(c => c.GetType().Name == "RemoteVisualOpacity");
                if (remote) remoteHits++;
                Mark("BANKER CONTACT source=" + source?.name + " remote=" + remote + " talking=" + control.FsmVariables.FindFsmBool("Talking")?.Value);
            }
            orig(fsm, ev, data);
        };
        On.HutongGames.PlayMaker.Fsm.EnterState += trace;
        On.HutongGames.PlayMaker.Fsm.ProcessEvent += contact;
        try {
            hero.transform.position = banker.transform.position + new Vector3(talker ? 2.5f : -1.8f, 1, 0);
            hero.AffectedByGravity(true); Physics2D.SyncTransforms();
            yield return Until(() => hero.cState.onGround, "native floor at banker", 8);
            Mark("BANKER HERO " + hero.transform.position + " control=" + control.ActiveStateName);
            for (var round = 0; round < 2; round++) {
            if (talker) {
                yield return Until(() => control.ActiveStateName.StartsWith("In Range"), "native banker interaction range", 8);
                yield return ResourcePress(InputHandler.Instance.inputActions.up);
                var expected = round == 0 ? "Meet" : "Repeat";
                yield return Until(() => convo.ActiveStateName == expected, "native banker conversation " + expected, 8);
                ResourceCheck(!hero.acceptingInput && PlayerData.instance.GetBool("disablePause"), "native conversation owns input");
                var beforeHits = remoteHits;
                Flag("banker.talking." + round);
                yield return Until(() => Has("banker.attacked." + round), "peer sent actual nail attack", 8);
                yield return new WaitForSecondsRealtime(.6f);
                Mark("BANKER AFTER ATTACK npc=" + npc.activeInHierarchy + " control=" + control.ActiveStateName + " convo=" + convo.ActiveStateName);
                ResourceCheck(remoteHits > beforeHits, "real UDP remote slash contacted banker during dialogue");
                ResourceCheck(npc.activeInHierarchy && convo.ActiveStateName == expected && !gaveGeo, "remote nail leaves native dialogue requester active without withdrawal");
                var end = Time.realtimeSinceStartup + 15;
                while (!hero.acceptingInput && Time.realtimeSinceStartup < end) {
                    yield return ResourcePress(InputHandler.Instance.inputActions.jump);
                    yield return new WaitForSecondsRealtime(.25f);
                }
                ResourceCheck(hero.acceptingInput && !PlayerData.instance.GetBool("disablePause") &&
                    !control.FsmVariables.FindFsmBool("Talking").Value && convo.ActiveStateName == "Idle",
                    "native dialogue closes and restores control/pause");
                Flag("banker.closed." + round);
                yield return new WaitForSecondsRealtime(.8f);
            } else {
                yield return Until(() => Has("banker.talking." + round), "peer opened native banker dialogue", 20);
                hero.transform.position = anchor + new Vector3(-1.8f, 0, 0);
                hero.FaceRight(); hero.Attack(GlobalEnums.AttackDirection.normal); Physics2D.SyncTransforms();
                yield return new WaitForSecondsRealtime(.8f);
                if (round == 0) ResourceCheck(gaveGeo, "attacker's idle banker retains native hit and geo withdrawal");
                Flag("banker.attacked." + round);
                yield return Until(() => Has("banker.closed." + round), "peer closes banker dialogue", 20);
            }
            }
            if (talker) {
                var balance = PlayerData.instance.GetInt("bankerBalance");
                ResourceCheck(balance > 0, "banker still has money for native post-dialogue hit");
                hero.transform.position = banker.transform.position + new Vector3(-1.8f, 0, 0);
                hero.FaceRight(); hero.Attack(GlobalEnums.AttackDirection.normal); Physics2D.SyncTransforms();
                yield return Until(() => gaveGeo && PlayerData.instance.GetInt("bankerBalance") < balance,
                    "local post-dialogue hit withdraws geo through native FSM", 8);
                ResourceCheck(Object.FindObjectsOfType<GeoControl>().Length > 0 && hero.acceptingInput &&
                    !PlayerData.instance.GetBool("disablePause"), "post-dialogue hit spawns real geo with hero control intact");
                Flag("banker.post-hit");
            } else yield return Until(() => Has("banker.post-hit"), "peer verifies normal post-dialogue hit", 12);
            Flag(role + ".banker.finished");
            yield return Until(() => Has((host ? "guest" : "host") + ".banker.finished"), "both banker assertions complete");
        } finally {
            On.HutongGames.PlayMaker.Fsm.EnterState -= trace;
            On.HutongGames.PlayMaker.Fsm.ProcessEvent -= contact;
        }
    }
}
