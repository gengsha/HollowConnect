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
    private readonly PlayMakerFSM wake;
    private readonly string wakeSignal;
    private bool wakeFinished;
    private FsmStateAction[] presentation;
    private bool pendingPresentation;

    private LocalBattleStart(PlayMakerFSM battle, Collider2D trigger, FsmEvent start, FsmState entry) {
        this.battle = battle; this.trigger = trigger; this.start = start; this.entry = entry;
    }
    private LocalBattleStart(PlayMakerFSM battle, PlayMakerFSM wake, string signal) {
        this.battle = battle; this.wake = wake; wakeSignal = signal;
    }
    internal static LocalBattleStart Find(GameObject boss, EntityType type) {
        // The Waterways controller lives in the main scene; its boss lives
        // in the additive scene. DUNG START follows a native 1.5s intro wait.
        // Forward only that emitted wake, never the other player's gate/intro.
        if (type == EntityType.DungDefender && boss.scene.name == "Waterways_05_boss" &&
            CoopContext.LocalScene == "Waterways_05") {
            var wake = boss.GetComponents<PlayMakerFSM>().SingleOrDefault(f => f.FsmName == "Dung Defender" &&
                f.FsmStates.Any(s => s.Transitions.Any(t => t.EventName == "DUNG START")));
            var controllers = UnityEngine.Object.FindObjectsOfType<PlayMakerFSM>(true).Where(f =>
                f.gameObject.scene.name == CoopContext.LocalScene && f.FsmName == "Battle Scene" &&
                LocalBattleCompletion.IsLocal(f.gameObject) && f.GetComponent<Collider2D>() is { isTrigger: true } &&
                f.FsmStates.SelectMany(s => s.Actions).OfType<SendEventByName>().Any(a => a.Enabled &&
                    !a.everyFrame && a.sendEvent.Value == "DUNG START" && a.delay.Value < .001f &&
                    a.eventTarget.target == FsmEventTarget.EventTarget.BroadcastAll)).ToArray();
            if (wake != null && controllers.Length == 1) return new LocalBattleStart(controllers[0], wake, "DUNG START");
        }
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
    internal bool Matches(FsmStateAction action) => battle != null && ReferenceEquals(battle.Fsm, action.Fsm) &&
        action is SendEventByName send && send.sendEvent.Value == (wakeSignal ?? "BATTLE START");
    private bool LocalHeroInside() {
        var hero = HeroController.instance;
        return hero != null && hero.gameObject.activeInHierarchy && trigger != null && trigger.enabled &&
            trigger.OverlapPoint(hero.transform.position);
    }
    internal void Tick() {
        if (wake != null) {
            // Accepted requests and takeover receipts may repeat. Only the
            // native dormant transition consumes this event; never rewind AI.
            if (wakeFinished || !wake.enabled || !wake.gameObject.activeInHierarchy ||
                wake.Fsm.ActiveState == null || wake.ActiveStateName == "Init") return;
            wakeFinished = true;
            if (wake.Fsm.ActiveState.Transitions.Any(t => t.EventName == wakeSignal)) wake.SendEvent(wakeSignal);
            return;
        }
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
