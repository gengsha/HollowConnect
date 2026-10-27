using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using Hkmp.Game.Client.Entity;
using MonoMod.RuntimeDetour;

namespace HollowConnect.Smoke;

// Opt-in diagnosis only; never enabled during performance measurements.
internal static class FsmSchemaTrace {
    private static readonly List<IDisposable> Hooks = new();
    internal static void Install() {
        if (Environment.GetEnvironmentVariable("HOLLOWCONNECT_FSM_SCHEMA_TRACE") != "1") return;
        var root = Environment.GetEnvironmentVariable("HOLLOWCONNECT_SMOKE_ROOT");
        var role = Environment.GetEnvironmentVariable("HOLLOWCONNECT_SMOKE_ROLE");
        void Mark(string text) => File.AppendAllText(Path.Combine(root, role + ".schema.log"), DateTime.UtcNow.ToString("O") + " " + text + "\n");
        string Layout(PlayMakerFSM f) => f.FsmName + " states=" + f.FsmStates.Length + " preprocessed=" + f.Fsm.Preprocessed +
            " initialized=" + f.Fsm.Initialized + " started=" + f.Fsm.Started + " template=" + f.FsmTemplate +
            " state=" + f.ActiveStateName + " actions=" + string.Join(",", f.FsmStates.Select(s => s.Name + ":" + string.Join("/", s.Actions.Select(a => a.GetType().Name))));
        foreach (var method in new[] { "ProcessHostFsm", "ProcessClientFsm" }) {
            var name = method;
            Hooks.Add(new Hook(typeof(Entity).GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic),
                (Action<Action<Entity, PlayMakerFSM>, Entity, PlayMakerFSM>)((orig, entity, fsm) => {
                    var inspect = entity.Type == EntityType.Vengefly;
                    if (inspect) Mark("BEFORE " + name + " id=" + entity.Id + " object=" + fsm.name + " " + Layout(fsm));
                    orig(entity, fsm);
                    if (inspect) Mark("AFTER " + name + " id=" + entity.Id + " object=" + fsm.name + " " + Layout(fsm));
                })));
        }
    }
}
