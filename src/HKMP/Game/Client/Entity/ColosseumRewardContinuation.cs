using System.Linq;
using HutongGames.PlayMaker;
using HutongGames.PlayMaker.Actions;
using UnityEngine;

namespace Hkmp.Game.Client.Entity;

// The trial's waves and Geo Pool remain authoritative. Its native reward
// pickup is personal, like an ordinary world pickup: every endpoint must be
// able to finish its own collection animation and release its own controls.
internal sealed class ColosseumRewardContinuation {
    private readonly Entity entity;
    private readonly GameObject host, client;
    private readonly Transform clientShiny;
    private readonly PlayMakerFSM pool;
    private readonly string completion;
    private string receivedPoolState;
    private bool continued;

    private ColosseumRewardContinuation(Entity entity, GameObject host, GameObject client,
        Transform clientShiny, PlayMakerFSM pool, string completion) {
        this.entity = entity; this.host = host; this.client = client;
        this.clientShiny = clientShiny; this.pool = pool; this.completion = completion;
    }

    internal static ColosseumRewardContinuation TryCreate(Entity entity, GameObject host, GameObject client) {
        if (entity.Type != EntityType.ColosseumManager ||
            entity.SourceScene is not ("Room_Colosseum_Bronze" or "Room_Colosseum_Silver")) return null;
        var completion = entity.SourceScene == "Room_Colosseum_Bronze" ? "colosseumBronzeCompleted" : "colosseumSilverCompleted";
        var pool = host.GetComponents<PlayMakerFSM>().FirstOrDefault(f => f.FsmName == "Geo Pool");
        var nativeShiny = host.transform.Find("Shiny Item");
        var clientShiny = client.transform.Find("Shiny Item");
        var source = nativeShiny?.GetComponents<PlayMakerFSM>().FirstOrDefault(f => f.FsmName == "Shiny Control");
        var waiting = pool?.FsmStates.FirstOrDefault(s => s.Name == "Give Shiny?");
        if (pool == null || clientShiny == null || source == null || pool.FsmVariables.FindFsmString("Completion PD Bool")?.Value != completion ||
            source.FsmVariables.FindFsmString("PD Bool Name")?.Value != completion || waiting == null ||
            !waiting.Transitions.Any(t => t.EventName == "SHINY PICKED UP" && t.ToState == "Open Gates") ||
            !waiting.Actions.OfType<FindChild>().Any(a => a.Enabled && a.childName.Value == "Shiny Item") ||
            !waiting.Actions.OfType<PlayerDataBoolTest>().Any(a => a.Enabled && a.boolName.Value == completion && a.isTrue?.Name == "NO")) return null;
        return new ColosseumRewardContinuation(entity, host, client, clientShiny, pool, completion);
    }

    internal bool IsClientReward(GameObject target) => target != null && clientShiny != null &&
        target.transform.IsChildOf(clientShiny);

    internal void ObserveState(string fsm, string state) {
        if (fsm == "Geo Pool") receivedPoolState = state;
    }

    private bool InRoom => !CoopContext.Resetting && host != null && pool != null && PlayerData.instance != null &&
        global::GameManager.instance != null && !global::GameManager.instance.IsInSceneTransition &&
        UnityEngine.SceneManagement.SceneManager.GetActiveScene() == host.scene;

    internal void Tick() {
        if (!InRoom) return;
        if (!entity.IsNativeAuthority) {
            // A late scene snapshot contains the waiting state, but the
            // original one-shot ActivateGameObject action may predate it.
            // Resume only that already-authorized native reward child.
            if (receivedPoolState == "Give Shiny?" && client != null && client.activeInHierarchy &&
                clientShiny != null && !clientShiny.gameObject.activeSelf && !PlayerData.instance.GetBool(completion))
                clientShiny.gameObject.SetActive(true);
            return;
        }
        if (pool.ActiveStateName != "Give Shiny?") { continued = false; return; }
        var hero = HeroController.instance;
        if (continued || !PlayerData.instance.GetBool(completion) || hero == null || !hero.isHeroInPosition ||
            !hero.acceptingInput || hero.controlReqlinquished || hero.cState.transitioning || hero.cState.dead ||
            PlayerData.instance.GetBool("disablePause") || global::GameManager.instance.isPaused || Time.timeScale <= 0) return;
        // A real local pickup or an already-applied shared resource receipt
        // has completed. Recheck the native bool branch once; NO owns its
        // Geo Given Pause -> Open Gates path. Do not grant or synthesize an
        // item, completion flag, gate event, or reward animation here.
        continued = true;
        pool.SetState("Give Shiny?");
    }

    internal void ResumeAuthority() {
        if (!InRoom || !entity.IsNativeAuthority || pool.ActiveStateName != "Give Shiny?") return;
        // Generic takeover intentionally replays only continuous actions.
        // This exact waiting state also needs its one-shot native child
        // activation: otherwise the new authority retains an inactive shiny
        // and can wait forever for a pickup that nobody can perform.
        continued = false;
        pool.SetState("Give Shiny?");
    }
}
