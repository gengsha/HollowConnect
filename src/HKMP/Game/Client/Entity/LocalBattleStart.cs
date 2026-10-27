using System.Linq;
using HutongGames.PlayMaker;
using HutongGames.PlayMaker.Actions;
using UnityEngine;

namespace Hkmp.Game.Client.Entity;

// A peer can enter a local arena before the scene authority does. Start the
// native battle simulation there, retaining entrance doors/camera until that
// player's own hero enters. Enemy activation, timers and counters stay native.
internal sealed class LocalBattleStart {
    private readonly PlayMakerFSM battle;
    private readonly Collider2D trigger;
    private readonly FsmEvent start;
    private readonly FsmState entry;
    private FsmStateAction[] presentation;
    private bool pendingPresentation;

    private LocalBattleStart(PlayMakerFSM battle, Collider2D trigger, FsmEvent start, FsmState entry) {
        this.battle = battle; this.trigger = trigger; this.start = start; this.entry = entry;
    }
    internal static LocalBattleStart Find(GameObject boss, EntityType type) {
        if (!BossDefeatSignals.IsBoss(type) || !boss.GetComponents<PlayMakerFSM>()
            .Any(f => f.FsmStates.Any(s => s.Transitions.Any(t => t.EventName == "BATTLE START")))) return null;
        var candidates = UnityEngine.Object.FindObjectsOfType<PlayMakerFSM>(true).Where(f =>
            f.gameObject.scene == boss.scene && f.FsmName == "Battle Control" && LocalBattleCompletion.IsLocal(f.gameObject) &&
            f.GetComponent<Collider2D>() is { isTrigger: true }).ToArray();
        if (candidates.Length != 1) return null;
        var battle = candidates[0];
        foreach (var state in battle.FsmStates) {
            foreach (var action in state.Actions.OfType<Trigger2dEvent>().Where(a => a.Enabled &&
                a.trigger == PlayMakerUnity2d.Trigger2DType.OnTriggerEnter2D && a.sendEvent != null)) {
                var transition = state.Transitions.FirstOrDefault(t => t.EventName == action.sendEvent.Name);
                var entered = battle.FsmStates.FirstOrDefault(s => s.Name == transition?.ToState);
                if (entered == null || !entered.Actions.OfType<SendEventByName>().Any(a => a.Enabled &&
                    a.sendEvent.Value == "BATTLE START" && a.delay.Value < .001f &&
                    a.eventTarget.target == FsmEventTarget.EventTarget.BroadcastAll)) continue;
                // Only support the immediate native entry boundary. Do not
                // defer asynchronous actions or arbitrary progression logic.
                return new LocalBattleStart(battle, battle.GetComponent<Collider2D>(), action.sendEvent, entered);
            }
        }
        return null;
    }
    internal bool Matches(HutongGames.PlayMaker.Fsm sender) => battle != null && ReferenceEquals(battle.Fsm, sender);
    private bool LocalHeroInside() {
        var hero = HeroController.instance;
        return hero != null && hero.gameObject.activeInHierarchy && trigger != null && trigger.enabled &&
            trigger.OverlapPoint(hero.transform.position);
    }
    internal void Tick() {
        if (battle == null || !battle.enabled || !battle.gameObject.activeInHierarchy) return;
        if (pendingPresentation) {
            var counter = battle.FsmVariables.FindFsmInt("Battle Enemies");
            if (counter == null || counter.Value <= 0) { pendingPresentation = false; return; }
            if (!LocalHeroInside()) return;
            pendingPresentation = false;
            foreach (var action in presentation) action.OnEnter();
            return;
        }
        if (battle.Fsm.ActiveState?.Transitions.Any(t => t.EventName == start.Name) != true) return;
        // Native Init resolves camera GameObject variables after entities
        // are constructed; classify their actual targets at entry time.
        presentation = entry.Actions.Where(a => a.Enabled &&
            (a is SendEventByName send && send.sendEvent.Value == "BG CLOSE" && send.delay.Value < .001f ||
             a is ActivateGameObject activate && activate.activate.Value &&
             battle.Fsm.GetOwnerDefaultTarget(activate.gameObject)?.GetComponent<CameraLockArea>() != null)).ToArray();
        var defer = !LocalHeroInside();
        if (defer) foreach (var action in presentation) action.Enabled = false;
        try {
            battle.SendEvent(start.Name);
            pendingPresentation = defer;
        } finally {
            if (defer) foreach (var action in presentation) action.Enabled = true;
        }
    }
}
