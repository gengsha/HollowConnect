using System;
using System.Collections;
using System.Linq;
using System.Reflection;
using Hkmp.Game.Client.Entity;
using HutongGames.PlayMaker;
using HutongGames.PlayMaker.Actions;
using UnityEngine;
using Object = UnityEngine.Object;

namespace HollowConnect.Smoke;

internal static class FsmTemplateChecks {
    internal static IEnumerator Run(Action<string> mark) {
        foreach (var inheritedPreprocessed in new[] { false, true })
            yield return RunOne(mark, inheritedPreprocessed);
    }

    private static IEnumerator RunOne(Action<string> mark, bool inheritedPreprocessed) {
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
        var template = ScriptableObject.CreateInstance<FsmTemplate>();
        var source = new GameObject("Dormant template fixture"); source.SetActive(false);
        var clone = (GameObject)null;
        try {
            var fsm = source.AddComponent<PlayMakerFSM>(); fsm.Reset();
            var definition = new Fsm();
            var wait = new Wait(); wait.Reset(); wait.time = 1000;
            var state = new FsmState(definition) { Name = "Native wait", Actions = new FsmStateAction[] { wait } };
            var done = new FsmState(definition) { Name = "Native done", Actions = Array.Empty<FsmStateAction>() };
            definition.States = new[] { state, done }; definition.StartState = state.Name;
            foreach (var item in definition.States) {
                item.Transitions = Array.Empty<FsmTransition>();
                foreach (var action in item.Actions) action.Init(item);
                item.SaveActions();
            }
            template.fsm = definition;
            // Unity deserialization initializes the serialized placeholder's
            // state owners before Awake expands its template. Reproduce that
            // exact condition: the old null-reference heuristic misses it.
            typeof(PlayMakerFSM).GetField("fsmTemplate", flags).SetValue(fsm, template);
            fsm.Fsm.InitData();
            fsm.Fsm.Preprocessed = inheritedPreprocessed;
            mark("TEMPLATE before states=" + fsm.FsmStates.Length + " preprocessed=" + fsm.Fsm.Preprocessed +
                " initialized=" + fsm.Fsm.Initialized + " owners=" + fsm.FsmStates.All(s => s.Fsm != null));
            EntityInitializer.CheckPreProcessFsm(fsm);
            if (fsm.FsmStates.Length != 2 || !fsm.Fsm.Preprocessed)
                throw new Exception("Dormant template was not expanded before snapshot schema capture");
            var instance = fsm.Fsm;
            var actionInstance = fsm.FsmStates[0].Actions[0];
            fsm.FsmVariables.IntVariables = new[] { new FsmInt("Live value") { Value = 42 } };
            var changes = 0; fsm.Fsm.StateChanged += _ => changes++;
            // A native expanded instance can have Preprocessed=false too.
            // Preparing it must preserve its live actions and variables.
            typeof(PlayMakerFSM).GetField("fsmTemplate", flags).SetValue(fsm, template);
            fsm.Fsm.Preprocessed = false;
            EntityInitializer.CheckPreProcessFsm(fsm);
            if (!ReferenceEquals(instance, fsm.Fsm) || !ReferenceEquals(actionInstance, fsm.FsmStates[0].Actions[0]))
                throw new Exception("Repeated template preparation replaced a live FSM");
            source.SetActive(true); yield return null;
            if (!ReferenceEquals(instance, fsm.Fsm) || !ReferenceEquals(actionInstance, fsm.FsmStates[0].Actions[0]) ||
                fsm.FsmVariables.IntVariables.Single(v => v.Name == "Live value").Value != 42 || changes == 0)
                throw new Exception("Native Awake replaced the prepared FSM, variables, actions or state subscription");
            mark("PASS dormant template schema and subscriptions survive first native activation");

            // Cloning an already materialized native instance also needs new
            // local action owners without reconstructing the old template.
            source.SetActive(false); clone = Object.Instantiate(source); clone.SetActive(false);
            var replica = clone.GetComponent<PlayMakerFSM>();
            EntityInitializer.InitializeFsm(replica);
            if (replica.FsmStates.Length != 2 || replica.FsmStates.Any(s => s.Fsm != replica.Fsm) ||
                replica.FsmStates.SelectMany(s => s.Actions).Any(a => a.Fsm != replica.Fsm))
                throw new Exception("Materialized template clone retained foreign FSM owners");
            var replicaInstance = replica.Fsm;
            clone.SetActive(true); yield return null;
            if (!ReferenceEquals(replicaInstance, replica.Fsm)) throw new Exception("Replica activation replaced prepared schema");
            mark("PASS template clone retains independent local owners through native activation");
        } finally {
            if (clone != null) Object.Destroy(clone);
            Object.Destroy(source); Object.Destroy(template);
        }
    }
}
