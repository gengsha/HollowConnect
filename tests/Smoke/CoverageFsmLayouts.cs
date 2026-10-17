using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Hkmp.Game.Client.Entity;
using Hkmp.Networking.Packet.Data;
using HutongGames.PlayMaker;
using MonoMod.RuntimeDetour;

namespace HollowConnect.Smoke;

public sealed partial class CoverageProbe {
    private string Layout(PlayMakerFSM f) => f == null ? "null" : f.FsmName + " state=" + f.ActiveStateName +
        " states=" + f.FsmStates.Length + " template=" + typeof(PlayMakerFSM).GetField("fsmTemplate", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)?.GetValue(f) +
        " strings=[" + string.Join(",", f.FsmVariables.StringVariables.Select(v => v.Name + "=" + v.Value)) + "]" +
        " floats=[" + string.Join(",", f.FsmVariables.FloatVariables.Select(v => v.Name)) + "]" +
        " ints=[" + string.Join(",", f.FsmVariables.IntVariables.Select(v => v.Name)) + "]";

    private void ObserveFsmLayouts() {
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
        performanceHooks.Add(new Hook(typeof(PlayMakerFSM).GetMethod("Awake", flags),
            (Action<Action<PlayMakerFSM>, PlayMakerFSM>)((orig, f) => {
                var observe = f.gameObject.scene.name == "Mines_11";
                if (observe) Mark("LAYOUT Awake before " + f.name + " " + Layout(f));
                orig(f);
                if (observe) Mark("LAYOUT Awake after " + f.name + " " + Layout(f));
            })));
        foreach (var method in new[] { "ProcessHostFsm", "ProcessClientFsm" }) {
            var name = method;
            performanceHooks.Add(new Hook(typeof(Entity).GetMethod(name, flags),
                (Action<Action<Entity, PlayMakerFSM>, Entity, PlayMakerFSM>)((orig, e, f) => {
                    Mark("LAYOUT before " + name + " id=" + e.Id + " type=" + e.Type + " object=" + f.name + " " + Layout(f));
                    orig(e, f);
                    Mark("LAYOUT after " + name + " id=" + e.Id + " " + Layout(f));
                })));
        }
        var seen = new HashSet<(ulong, byte)>();
        performanceHooks.Add(new Hook(typeof(Entity).GetMethod("UpdateHostFsmData"),
            (Action<Action<Entity, Dictionary<byte, EntityHostFsmData>>, Entity, Dictionary<byte, EntityHostFsmData>>)((orig, e, data) => {
                var fsms = Field<HostClientPair<List<PlayMakerFSM>>>(e, "_fsms");
                var snapshots = Field<List<FsmSnapshot>>(e, "_fsmSnapshots");
                foreach (var pair in data.Where(p => p.Value.Types.Contains(EntityHostFsmData.Type.State))) {
                    if (pair.Key >= fsms.Host.Count || fsms.Host[pair.Key] == null) continue;
                    var f = fsms.Host[pair.Key];
                    if (pair.Value.CurrentState != byte.MaxValue && pair.Value.CurrentState >= f.FsmStates.Length && seen.Add((e.Id, pair.Key)))
                        Mark("LAYOUT invalid state id=" + e.Id + " type=" + e.Type + " index=" + pair.Key +
                            " received=" + pair.Value.CurrentState + " host=" + Layout(f) + " client=" + Layout(fsms.Client[pair.Key]));
                }
                foreach (var pair in data.Where(p => p.Value.Types.Contains(EntityHostFsmData.Type.Strings))) {
                    if (!seen.Add((e.Id, pair.Key))) continue;
                    Mark("LAYOUT receive id=" + e.Id + " type=" + e.Type + " index=" + pair.Key +
                        " values=" + string.Join(",", pair.Value.Strings.Select(v => v.Key + "=" + v.Value)) +
                        " host=" + Layout(fsms.Host[pair.Key]) + " client=" + Layout(fsms.Client[pair.Key]) +
                        " snapshot=" + snapshots[pair.Key].Strings.Length);
                }
                orig(e, data);
            })));
    }
}
