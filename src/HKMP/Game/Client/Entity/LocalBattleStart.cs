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
    private readonly string wakeState;
    private readonly string activeBattleState;
    private readonly SendEventByName waveStart;
    private bool wakeFinished;
    private FsmStateAction[] presentation;
    private bool pendingPresentation;

    private LocalBattleStart(PlayMakerFSM battle, Collider2D trigger, FsmEvent start, FsmState entry,
        SendEventByName waveStart = null, PlayMakerFSM wake = null, string wakeState = null, string activeBattleState = null) {
        this.battle = battle; this.trigger = trigger; this.start = start; this.entry = entry;
        this.wake = wake; this.waveStart = waveStart;
        this.wakeState = wakeState; this.activeBattleState = activeBattleState;
    }
    private LocalBattleStart(PlayMakerFSM battle, PlayMakerFSM wake, string signal, string wakeState = null) {
        this.battle = battle; this.wake = wake; wakeSignal = signal; this.wakeState = wakeState;
    }
    // The first-wave entity carries the request. The Garden controller is
    // still needed before that enemy activates and after it has been killed.
    internal bool UsesSceneController => waveStart != null;
    internal static bool IsGardenScene(string scene) => scene is "Fungus3_05" or "Fungus3_10";

    private static LocalBattleStart FindGardenStart(GameObject anchor, EntityType type) {
        // One native anchor per room, the only child of its first wave.
        if (!(anchor.scene.name == "Fungus3_05" && type == EntityType.MantisPetraSummon && anchor.name == "Dragonfly Summon" ||
              anchor.scene.name == "Fungus3_10" && type == EntityType.MantisTraitor && anchor.name == "Mantis Heavy")) return null;
        var wave = anchor.transform.parent;
        var controller = wave == null ? null : wave.parent;
        if (wave == null || wave.name != "Wave 1" || controller == null || controller.name != "Battle Scene" ||
            controller.gameObject.scene != anchor.scene || !LocalBattleCompletion.IsLocal(controller.gameObject)) return null;
        var battle = controller.gameObject.LocateMyFSM("Battle Control");
        if (battle == null || battle.GetComponent<Collider2D>() is not { isTrigger: true } trigger) return null;
        var init = battle.FsmStates.FirstOrDefault(s => s.Name == "Init");
        var entry = battle.FsmStates.FirstOrDefault(s => s.Name == "Wave 1");
        var collision = init?.Actions.OfType<Trigger2dEvent>().FirstOrDefault(a => a.Enabled &&
            a.trigger == PlayMakerUnity2d.Trigger2DType.OnTriggerEnter2D && a.sendEvent?.Name == "HIT");
        var summon = entry?.Actions.OfType<SendEventByName>().FirstOrDefault(a => a.Enabled && !a.everyFrame &&
            a.sendEvent.Value == "SUMMON" && a.delay.Value < .001f && a.eventTarget?.target == FsmEventTarget.EventTarget.GameObject &&
            a.eventTarget.sendToChildren.Value);
        return collision != null && summon != null && init.Transitions.Any(t => t.EventName == "HIT" && t.ToState == entry.Name)
            ? new LocalBattleStart(battle, trigger, collision.sendEvent, entry, waveStart: summon) : null;
    }
    internal static LocalBattleStart Find(GameObject boss, EntityType type) {
        if (IsGardenScene(boss.scene.name)) {
            var garden = FindGardenStart(boss, type);
            if (garden != null) return garden;
        }
        if (type == EntityType.MossKnight && boss.scene.name == "Fungus1_32" && boss.name == "Moss Knight B") {
            var control = boss.LocateMyFSM("Moss Knight Control");
            var mossBattle = boss.scene.GetRootGameObjects().FirstOrDefault(o => o.name == "Battle Scene v2")?.LocateMyFSM("Battle Control");
            // Only the arena's first knight needs this broadcast. Its native
            // controller and gates can still wait for the local player's entry.
            if (mossBattle != null && LocalBattleCompletion.IsLocal(mossBattle.gameObject) &&
                control != null && control.FsmStates.Any(s => s.Name == "Sleep" && s.Transitions.Any(t => t.EventName == "BATTLE START")))
                return new LocalBattleStart(mossBattle, control, "BATTLE START", "Sleep");
        }
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
                var (fsmName, dormant, active) = (type, boss.scene.name) switch {
                    (EntityType.SoulWarrior, "Ruins1_31b") => ("Mage Knight", "Sleep", "Battling"),
                    (EntityType.FalseKnight, "Crossroads_10_boss") => ("FalseyControl", "Dormant", "Start"),
                    _ => (null, null, null)
                };
                var wake = fsmName == null ? null : boss.GetComponents<PlayMakerFSM>().SingleOrDefault(f =>
                    f.FsmName == fsmName && f.FsmStates.Any(s => s.Name == dormant &&
                        s.Transitions.Any(t => t.EventName == "BATTLE START")));
                return new LocalBattleStart(battle, battle.GetComponent<Collider2D>(), action.sendEvent, entered,
                    wake: wake, wakeState: dormant, activeBattleState: active);
            }
        }
        return null;
    }
    internal bool Matches(FsmStateAction action) => battle != null && ReferenceEquals(battle.Fsm, action.Fsm) &&
        (waveStart != null ? ReferenceEquals(waveStart, action) : action is SendEventByName send &&
            send.sendEvent.Value == (wakeSignal ?? "BATTLE START") &&
            (wakeSignal == null || wakeState == null || action.State.Name == "Start"));
    private bool LocalHeroInside() {
        var hero = HeroController.instance;
        return hero != null && hero.gameObject.activeInHierarchy && trigger != null && trigger.enabled &&
            trigger.OverlapPoint(hero.transform.position);
    }
    internal void Tick() {
        if (wakeSignal != null) {
            // Accepted requests and takeover receipts may repeat. Only the
            // native dormant transition consumes this event; never rewind AI.
            if (wakeFinished || !wake.enabled || !wake.gameObject.activeInHierarchy ||
                wake.Fsm.ActiveState == null || wake.ActiveStateName == "Init") return;
            if (wakeState != null && (wake.ActiveStateName != wakeState ||
                wake.GetComponent<HealthManager>() is not { isDead: false, hp: > 0 })) return;
            wakeFinished = true;
            if (wake.Fsm.ActiveState.Transitions.Any(t => t.EventName == wakeSignal)) wake.SendEvent(wakeSignal);
            return;
        }
        if (battle == null || !battle.enabled || !battle.gameObject.activeInHierarchy) return;
        // The arena can emit BATTLE START while the boss is still in its
        // entry FSM states. Let the native dormant transition consume it.
        if (!wakeFinished && wake != null && wake.isActiveAndEnabled && wake.ActiveStateName == wakeState &&
            battle.ActiveStateName == activeBattleState && battle.FsmVariables.FindFsmInt("Battle Enemies") is { Value: > 0 } &&
            wake.GetComponent<HealthManager>() is { isDead: false, hp: > 0 }) {
            wakeFinished = true;
            wake.SendEvent("BATTLE START");
        }
        if (pendingPresentation) {
            var counter = battle.FsmVariables.FindFsmInt("Battle Enemies");
            // A Garden wave legitimately reaches zero during a native pause.
            if (waveStart != null ? battle.ActiveStateName is "End Pause" or "End" : counter == null || counter.Value <= 0) {
                pendingPresentation = false;
                return;
            }
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
