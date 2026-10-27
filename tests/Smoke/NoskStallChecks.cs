using System;
using System.Collections;
using System.IO;
using System.Linq;
using System.Reflection;
using Hkmp.Game.Client.Entity;
using HutongGames.PlayMaker;
using HutongGames.PlayMaker.Actions;
using MonoMod.RuntimeDetour;
using UnityEngine;
using Object = UnityEngine.Object;

namespace HollowConnect.Smoke;

public sealed partial class RescueProbe {
    private void NoskDump(PlayMakerFSM fsm) {
        var path = Path.Combine(root, role + ".nosk-fsm.txt");
        void Write(string s) => File.AppendAllText(path, s + "\n");
        string Value(object o) => o is Object u ? u == null ? "null" : u.GetType().Name + ":" + u.name : o?.ToString() ?? "null";
        Write("FSM " + fsm.name + "/" + fsm.FsmName + "=" + fsm.ActiveStateName);
        foreach (var state in fsm.FsmStates) {
            Write("STATE " + state.Name + " transitions=" + string.Join(",", state.Transitions.Select(t => t.EventName + ":" + t.ToState)));
            foreach (var a in state.Actions) Write("ACTION " + a.GetType().Name + " " + string.Join(";", a.GetType().GetFields(BindingFlags.Instance | BindingFlags.Public).Select(f => f.Name + "=" + Value(f.GetValue(a)))));
        }
        foreach (var v in fsm.FsmVariables.FloatVariables) Write("FLOAT " + v.Name + "=" + v.Value);
        foreach (var v in fsm.FsmVariables.GameObjectVariables) Write("OBJECT " + v.Name + "=" + Value(v.Value));
    }
    private IEnumerator NativeLoopRebindChecks() {
        var source = new GameObject("Native loop rebind fixture"); source.SetActive(false);
        try {
            source.transform.position = new Vector3(-500, -500, 0);
            var body = source.AddComponent<Rigidbody2D>(); body.gravityScale = 0;
            var fsm = source.AddComponent<PlayMakerFSM>(); fsm.Reset();
            var velocity = new SetVelocity2d(); velocity.Reset();
            velocity.gameObject = new FsmOwnerDefault(); velocity.vector = new FsmVector2 { UseVariable = true };
            velocity.x = 3; velocity.y = 0; velocity.everyFrame = true;
            var position = new TranslateV2(); position.Reset(); position.gameObject = new FsmOwnerDefault();
            position.vector = new FsmVector3 { UseVariable = true }; position.z = 3;
            position.yMin = new FsmFloat { UseVariable = true }; position.yMax = new FsmFloat { UseVariable = true };
            position.everyFrame = true; position.lateUpdate = true;
            var state = new FsmState(fsm.Fsm) { Name = "Move", Actions = new FsmStateAction[] { velocity, position }, Transitions = Array.Empty<FsmTransition>() };
            fsm.Fsm.States = new[] { state }; fsm.Fsm.StartState = state.Name;
            foreach (var action in state.Actions) action.Init(state);
            state.SaveActions(); source.SetActive(true);
            yield return null;
            Check(fsm.Fsm.HandleFixedUpdate && fsm.Fsm.HandleLateUpdate, "native actions request physics and late update loops");
            var liveAction = state.Actions[0];
            fsm.Fsm.Preprocessed = false;
            EntityInitializer.CheckPreProcessFsm(fsm);
            Check(fsm.Fsm.HandleFixedUpdate && fsm.Fsm.HandleLateUpdate && ReferenceEquals(liveAction, fsm.FsmStates[0].Actions[0]),
                "rebind preserves both native loops and live actions");
            var startX = source.transform.position.x; var startZ = source.transform.position.z; body.velocity = Vector2.zero;
            yield return new WaitForSecondsRealtime(.15f);
            Check(body.velocity.x > 2 && source.transform.position.x > startX && source.transform.position.z > startZ + .1f,
                "native physics velocity and late translation keep updating after rebind");
        } finally { Object.Destroy(source); }
    }
    private IEnumerator NoskStallChecks() {
        using var noContact = IgnoreExternalCombatDamage();
        yield return FsmTemplateChecks.Run(Log);
        yield return NativeLoopRebindChecks();
        using var preprocessTrace = new Hook(typeof(EntityInitializer).GetMethod("CheckPreProcessFsm"),
            new Action<Action<PlayMakerFSM>, PlayMakerFSM>((orig, f) => {
                var inspect = f.name == "Mimic Spider" && f.FsmName == "Climb";
                var before = f.Fsm.HandleFixedUpdate;
                orig(f);
                if (inspect) Log("NOSK PREPROCESS fixed " + before + " -> " + f.Fsm.HandleFixedUpdate + " initialized=" + f.Fsm.Initialized);
            }));
        var velocityReads = 0;
        using var velocityTrace = new Hook(typeof(SetVelocity2d).GetMethod("DoSetVelocity", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic),
            new Action<Action<SetVelocity2d>, SetVelocity2d>((orig, a) => {
                orig(a);
                if (a.Owner == null || a.Owner.name != "Mimic Spider" || EntityReferences.Owner(a.Owner)?.IsNativeAuthority != true ||
                    a.Fsm.Name != "Climb" || velocityReads >= 8) return;
                velocityReads++;
                Log("NOSK VELOCITY " + a.Fsm.Name + "/" + a.State.Name + " vectorNone=" + a.vector.IsNone + " xNone=" + a.x.IsNone + " yNone=" + a.y.IsNone +
                    " requestedY=" + a.y.Value + " actual=" + a.Owner.GetComponent<Rigidbody2D>().velocity + " fixed=" + a.Fsm.HandleFixedUpdate);
            }));
        var other = host ? "guest" : "host";
        if (!host) yield return Until(() => Has("nosk.first"), "nosk authority first");
        yield return RuleEnter("Deepnest_32");
        if (host) Flag("nosk.first");
        yield return Until(() => EntityReferences.Entities.Values.Any(e => e.Type == EntityType.Nosk), "native story Nosk registered");
        var entity = EntityReferences.Entities.Values.Single(e => e.Type == EntityType.Nosk);
        var climb = entity.Object.Host.GetComponents<PlayMakerFSM>().Single(f => f.FsmName == "Climb");
        Check(climb.Fsm.HandleFixedUpdate, "native climb retains physics update handler after network preparation");
        foreach (var f in entity.Object.Host.GetComponents<PlayMakerFSM>()) Log("NOSK FIXED " + f.FsmName + "=" + f.Fsm.HandleFixedUpdate);
        foreach (var proxy in entity.Object.Host.GetComponents<PlayMakerFixedUpdate>()) {
            var targets = (System.Collections.Generic.List<PlayMakerFSM>)typeof(PlayMakerProxyBase).GetField("TargetFSMs", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic).GetValue(proxy);
            Log("NOSK FIXED ORDER " + string.Join(",", targets.Select(f => f.FsmName)));
        }
        foreach (var f in entity.Object.Host.GetComponentsInChildren<PlayMakerFSM>(true)) NoskDump(f);
        Log("NOSK PRE authority=" + entity.IsNativeAuthority + " hero=" + HeroController.instance.transform.position + " boss=" + entity.Object.Host.transform.position);
        var hero = HeroController.instance;
        hero.transform.position = StoryFloor(entity.Object.Host.transform.position, "Deepnest_32") + new Vector3(-4, 0, 0);
        hero.GetComponent<Rigidbody2D>().velocity = Vector2.zero; hero.AffectedByGravity(true); Physics2D.SyncTransforms();
        Flag(role + ".nosk-ready"); yield return Until(() => Has(other + ".nosk-ready"), "both nosk entrants ready");
        var crossedStep = false;
        var replicaMoved = false;
        var replicaStart = entity.Object.Client.transform.position;
        var lastPosition = entity.Object.Host.transform.position;
        var lastMoved = Time.realtimeSinceStartup;
        var nextSample = Time.realtimeSinceStartup;
        var end = Time.realtimeSinceStartup + 38;
        while (Time.realtimeSinceStartup < end) {
            if (!host && entity.Object.Client.activeInHierarchy && Vector2.Distance(entity.Object.Client.transform.position, replicaStart) > 8)
                replicaMoved = true;
            if (host) {
                var pos = entity.Object.Host.transform.position;
                if (Vector2.Distance(pos, lastPosition) > .2f) { lastPosition = pos; lastMoved = Time.realtimeSinceStartup; }
                var main = entity.Object.Host.GetComponents<PlayMakerFSM>().Single(f => f.FsmName == "Mimic Spider");
                if (main.ActiveStateName == "Charge R" && pos.x > 90 && pos.x < 111 && pos.y > 8) crossedStep = true;
                if (Time.realtimeSinceStartup >= nextSample)
                    Check(!main.ActiveStateName.StartsWith("Charge ", StringComparison.Ordinal) || Time.realtimeSinceStartup - lastMoved < 5,
                        "native charge does not stall against platform edge");
            }
            if (Time.realtimeSinceStartup >= nextSample) {
                Log("NOSK SAMPLE authority=" + entity.IsNativeAuthority + " boss=" + entity.Object.Host.transform.position + " vel=" + entity.Object.Host.GetComponent<Rigidbody2D>().velocity + " hero=" + hero.transform.position +
                    " states=" + string.Join(",", entity.Object.Host.GetComponents<PlayMakerFSM>().Select(f => f.FsmName + ":" + f.ActiveStateName)));
                nextSample = Time.realtimeSinceStartup + 1;
            }
            // The charge can cross the raised step between one-second log
            // samples. Observe every frame without changing native movement.
            yield return null;
        }
        if (host) Check(crossedStep, "native charge climbed left step onto raised platform");
        else Check(replicaMoved && !entity.Object.Host.activeInHierarchy, "guest displays moving authoritative replica while local AI stays inactive");
        Flag(role + ".nosk-complete"); yield return Until(() => Has(other + ".nosk-complete"), "both nosk checks finished");
    }
}
