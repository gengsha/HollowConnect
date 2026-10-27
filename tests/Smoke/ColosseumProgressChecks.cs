using System;
using System.Collections;
using System.Linq;
using HutongGames.PlayMaker.Actions;
using UnityEngine;
using Object = UnityEngine.Object;

namespace HollowConnect.Smoke;

public sealed partial class ProgressProbe {
    private IEnumerator ColosseumProgressChecks() {
        var pd = PlayerData.instance;
        yield return Until(() => LocalResourceClaims.Contains("ore:colosseumSilver") && pd.GetBool("colosseumSilverCompleted") &&
            pd.GetBool("colosseumSilverOpened"), "join shares completed Silver trial with its registration", 18);
        if (Has(role + ".failed")) throw new Exception("Shared Silver trial remains locked");
        ResourceCheck(pd.GetInt("ore") == (host ? 0 : 1) && pd.GetInt("charmSlots") == 4 && pd.GetInt("geo") == 500,
            "trial catchup awards ore once without extra notch or registration fee");
        // Existing broken-save fixture: the grant is already acknowledged;
        // repeating only the numerical reward cannot repair this state.
        pd.SetBool("colosseumSilverOpened", false);
        pd.SetInt("ore", 0);
        typeof(HollowConnect.CoopRuntime).GetField("progressAuditRequested", ResourcePrivate).SetValue(runtime, true);
        yield return Until(() => pd.GetBool("colosseumSilverOpened") && AuditIdle && !Field<bool>(runtime, "progressAuditRequested"),
            "repeat audit repairs old completed-but-unregistered trial", 8);
        ResourceSend("ore:colosseumSilver");
        yield return new WaitForSecondsRealtime(1);
        ResourceCheck(pd.GetInt("ore") == 0 && pd.GetInt("charmSlots") == 4 && pd.GetInt("geo") == 500,
            "repeat synchronization never refunds spent ore or duplicates rewards");
        ResourceCheck(!pd.GetBool("colosseumGoldOpened") && !pd.GetBool("colosseumGoldCompleted"),
            "Gold remains available for its normal paid registration without fabricated completion");
        yield return Room("Room_Colosseum_01");
        var board = Object.FindObjectsOfType<PlayMakerFSM>().Single(f => f.name == "Silver Trial Board" && f.FsmName == "Conversation Control");
        foreach (var f in board.GetComponents<PlayMakerFSM>()) Dump(f);
        var control = board.GetComponents<PlayMakerFSM>().Single(f => f.FsmName == "npc_control");
        var range = board.GetComponent<Collider2D>();
        var unlock = board.GetComponents<PlayMakerFSM>().Single(f => f.FsmName == "Unlock Anim");
        pd.SetBool("colosseumSilverOpened", false);
        yield return Until(() => pd.GetBool("colosseumSilverOpened") && unlock.ActiveStateName == "Burst",
            "repair in already loaded lobby runs native chain-cut refresh", 8);
        var expectedSprite = unlock.FsmStates.Single(s => s.Name == "Burst").Actions.OfType<SetSpriteRendererSprite>().Single().sprite.Value;
        ResourceCheck(board.transform.Find("Sprite").GetComponent<SpriteRenderer>().sprite == expectedSprite,
            "Silver board shows native unlocked sprite after catchup");
        var hero = HeroController.instance;
        // Spawn above the native floor (y=5); entering below its surface
        // falls through the hollow terrain edge instead of onto the floor.
        hero.transform.position = board.transform.position + new Vector3(0, 1, 0);
        hero.AffectedByGravity(true); Physics2D.SyncTransforms();
        yield return new WaitForSecondsRealtime(.5f);
        yield return Until(() => hero.cState.onGround, "native ground at Silver trial board", 8);
        Mark("COLOSSEUM RANGE hero=" + hero.transform.position + " bounds=" + range.bounds + " enabled=" + range.enabled +
            " control=" + control.ActiveStateName + " layer=" + range.gameObject.layer + " simulated=" + hero.GetComponent<Rigidbody2D>().simulated +
            " bodies=" + string.Join(";", hero.GetComponents<Collider2D>().Select(c => c.bounds + "/enabled=" + c.enabled + "/layer=" + c.gameObject.layer + "/ignored=" + Physics2D.GetIgnoreCollision(c, range))));
        yield return Until(() => control.ActiveStateName.StartsWith("In Range"), "native Silver board interaction range", 8);
        Mark("COLOSSEUM AFTER RANGE hero=" + hero.transform.position + " state=" + control.ActiveStateName);
        ResourceCheck(control.ActiveStateName.StartsWith("In Range"), "Silver board range reached: " + control.ActiveStateName);
        yield return ResourcePress(InputHandler.Instance.inputActions.up);
        yield return Until(() => board.ActiveStateName == "Box Up YN" || board.ActiveStateName == "Send Text" || board.ActiveStateName == "Box Up 2" ||
            board.ActiveStateName == "Bug Talk" || board.ActiveStateName == "Bug Talk 2", "native Silver board responds to interaction", 8);
        Mark("COLOSSEUM BOARD state=" + board.ActiveStateName);
        ResourceCheck(board.ActiveStateName == "Box Up YN" || board.ActiveStateName == "Send Text", "native Silver board offers repeat trial rather than locked or unpaid message");
        yield return Until(() => Dialogue.ActiveStateName == "Ready for Input", "native repeat-trial prompt ready", 8);
        var list = Dialogue.FsmVariables.GetFsmGameObject("UI List").Value.GetComponents<PlayMakerFSM>().Single(f => f.FsmName == "ui_list");
        list.FsmVariables.GetFsmInt("Current Item").Value = 2;
        Dialogue.SendEvent("UI SELECTION MADE");
        yield return ResourceIdle("declined repeat trial", 10);
        yield return ResourceBarrier("colosseum-progress-complete");
    }
}
