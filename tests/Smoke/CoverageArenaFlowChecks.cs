using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Hkmp.Game.Client.Entity;
using HutongGames.PlayMaker;
using Modding;
using UnityEngine;
using UnityEngine.SceneManagement;
using Object = UnityEngine.Object;

namespace HollowConnect.Smoke;

// Used by the isolated ColosseumProbe flow-inspect/victory cases, with
// disposable in-memory characters and the existing SaveGame interception.
// This recorder never advances a wave, activates
// an FSM, grants a reward, sends an event or alters PlayerData.
//
// Suggested milestones: before registration, first peer Wave 1, second peer
// snapshot ready (join during Wave 2), one later MUSIC event, WAVES COMPLETED,
// Geo Pool/Give Shiny?, before/after actual shiny interaction, both exit, then
// a second registered bronze completion. Record initial charmSlots and require
// exactly +1 once per player; the repeated completion must add zero. Do not
// treat colosseumBronzeCompleted=true alone as a notch-pickup proof.
internal sealed class ArenaFlowRecorder : IDisposable {
    private const BindingFlags PublicInstance = BindingFlags.Instance | BindingFlags.Public;
    private readonly Action<string> write;
    private readonly List<Component> texts = new();
    private readonly Dictionary<int, string> previousText = new();
    private bool attached;
    internal bool HasMusicTransition { get; private set; }
    internal bool HasMusicPlayback { get; private set; }
    internal bool HasInvalidTitle { get; private set; }
    internal bool HasArenaTitleText { get; private set; }

    internal ArenaFlowRecorder(Action<string> write) {
        this.write = write ?? throw new ArgumentNullException(nameof(write));
    }

    internal void Attach() {
        if (attached) return;
        attached = true;
        On.HutongGames.PlayMaker.Fsm.EnterState += EnterState;
        ModHooks.SetPlayerIntHook += SetInt;
        ModHooks.SetPlayerBoolHook += SetBool;
    }

    public void Dispose() {
        if (!attached) return;
        attached = false;
        On.HutongGames.PlayMaker.Fsm.EnterState -= EnterState;
        ModHooks.SetPlayerIntHook -= SetInt;
        ModHooks.SetPlayerBoolHook -= SetBool;
        texts.Clear(); previousText.Clear();
        HasMusicTransition = false;
        HasMusicPlayback = false;
    }

    private static bool InArena => UnityEngine.SceneManagement.SceneManager.GetActiveScene().name.StartsWith("Room_Colosseum_");
    private void Log(string text) => write("ARENA frame=" + Time.frameCount + " t=" + Time.realtimeSinceStartup.ToString("F3") + " " + text);

    private static string Path(GameObject go) {
        if (go == null) return "null";
        var segments = new Stack<string>();
        for (var t = go.transform; t != null; t = t.parent)
            segments.Push(t.name + "#" + t.GetSiblingIndex());
        return go.scene.name + "/" + string.Join("/", segments);
    }

    private static bool Watched(GameObject go) {
        if (go == null || !go.scene.IsValid() || !go.scene.isLoaded) return false;
        // Include the global Area Title even if it lives in DontDestroyOnLoad.
        // Capture the original and replica paths/instance IDs independently.
        var path = Path(go);
        if (path.Contains("Area Title")) return true;
        if (!path.Contains("Colosseum Manager")) return false;
        return go.name.StartsWith("Colosseum Manager") || go.name == "Music Control" ||
            go.name == "Shiny Item" || path.Contains("/Shiny Item#");
    }

    private static string Describe(object value) {
        if (value == null) return "null";
        if (value is FsmEvent evt) return evt.Name;
        if (value is FunctionCall call) return call.FunctionName + "(" + call.ParameterType + ")";
        if (value is FsmOwnerDefault owner) return owner.OwnerOption + ":" + Describe(owner.GameObject);
        if (value is FsmEventTarget target)
            return target.target + ":" + Describe(target.gameObject) + ":" + target.fsmName;
        if (value is NamedVariable variable) {
            // Read the actual binding as well as the current value. Old static
            // dumps omitted Enabled and variable names, hiding template branches.
            var property = variable.GetType().GetProperties(PublicInstance)
                .FirstOrDefault(p => p.Name == "Value" && p.GetIndexParameters().Length == 0);
            object current;
            try { current = property?.GetValue(variable, null); }
            catch (Exception ex) { current = "<" + ex.GetType().Name + ">"; }
            return variable.GetType().Name + "[name=" + variable.Name + ",variable=" + variable.UseVariable +
                ",none=" + variable.IsNone + "]=" + Describe(current);
        }
        if (value is Object obj) return obj == null ? "destroyed" : obj.GetType().Name + ":" + obj.name +
            "#" + obj.GetInstanceID() + (obj is GameObject go ? ":" + Path(go) : "");
        if (value is Array array) return "[" + string.Join(",", array.Cast<object>().Select(Describe)) + "]";
        return value.ToString().Replace("\r", "\\r").Replace("\n", "\\n");
    }

    private void EnterState(On.HutongGames.PlayMaker.Fsm.orig_EnterState orig, Fsm fsm, FsmState state) {
        var watch = InArena && Watched(fsm.GameObject);
        if (watch && fsm.GameObject.name == "Music Control" && fsm.Name == "Control" &&
            (state.Name.StartsWith("Set Music", StringComparison.Ordinal) || state.Name == "Sting" || state.Name == "New Track")) {
            HasMusicTransition = true;
            Log("MUSIC TRANSITION " + state.Name + " object=" + Path(fsm.GameObject));
        }
        if (watch) {
            Log("STATE BEFORE " + Path(fsm.GameObject) + "/" + fsm.Name + " -> " + state.Name);
            DumpActions(fsm, state);
        }
        orig(fsm, state);
        if (watch) Log("STATE AFTER " + Path(fsm.GameObject) + "/" + fsm.Name + " current=" + fsm.ActiveStateName);
    }

    private void DumpActions(Fsm fsm, FsmState state) {
        for (var i = 0; i < state.Actions.Length; i++) {
            var action = state.Actions[i];
            Log("ACTION " + Path(fsm.GameObject) + "/" + fsm.Name + "/" + state.Name + "[" + i +
                "] enabled=" + action.Enabled + " type=" + action.GetType().FullName + " " +
                string.Join(";", action.GetType().GetFields(PublicInstance).Select(field => field.Name + "=" + Describe(field.GetValue(action)))));
        }
    }

    private int SetInt(string key, int value) {
        if (InArena && (key == "charmSlots" || key == "ore" || key == "heartPieces" || key == "vesselFragments"))
            Log("PD INT " + key + " old=" + PlayerData.instance.GetInt(key) + " proposed=" + value + " stack=" + Environment.StackTrace);
        return value;
    }

    private bool SetBool(string key, bool value) {
        if (InArena && (key.StartsWith("colosseum") || key == "seenColosseumTitle"))
            Log("PD BOOL " + key + " old=" + PlayerData.instance.GetBool(key) + " proposed=" + value + " stack=" + Environment.StackTrace);
        return value;
    }

    // Invoke explicitly at a milestone; this enumerates loaded FSMs once.
    // includeLayouts is useful before entering and at Give Shiny? to preserve
    // disabled template actions, IntSwitch comparison arrays and target bindings.
    internal void Capture(string milestone, bool includeLayouts = false) {
        Log("CAPTURE " + milestone + " scene=" + UnityEngine.SceneManagement.SceneManager.GetActiveScene().name);
        var pd = PlayerData.instance;
        foreach (var key in new[] { "charmSlots", "ore", "heartPieces", "vesselFragments", "MPReserveMax" })
            Log("PD SNAPSHOT " + key + "=" + pd.GetInt(key));
        foreach (var key in new[] { "colosseumBronzeCompleted", "colosseumSilverCompleted", "colosseumGoldCompleted", "seenColosseumTitle" })
            Log("PD SNAPSHOT " + key + "=" + pd.GetBool(key));
        foreach (var fsm in Resources.FindObjectsOfTypeAll<PlayMakerFSM>().Where(f => Watched(f.gameObject))) {
            Log("FSM " + Path(fsm.gameObject) + "#" + fsm.GetInstanceID() + "/" + fsm.FsmName +
                " enabled=" + fsm.enabled + " active=" + fsm.gameObject.activeInHierarchy + " state=" + fsm.ActiveStateName +
                " template=" + Describe(fsm.FsmTemplate));
            foreach (var variables in new Array[] { fsm.FsmVariables.StringVariables, fsm.FsmVariables.IntVariables,
                fsm.FsmVariables.BoolVariables, fsm.FsmVariables.FloatVariables, fsm.FsmVariables.ObjectVariables,
                fsm.FsmVariables.GameObjectVariables })
                foreach (var variable in variables) Log("VAR " + Path(fsm.gameObject) + "/" + fsm.FsmName + " " + Describe(variable));
            if (includeLayouts) foreach (var state in fsm.FsmStates) {
                Log("LAYOUT " + Path(fsm.gameObject) + "/" + fsm.FsmName + "/" + state.Name + " -> " +
                    string.Join(",", state.Transitions.Select(t => t.EventName + ":" + t.ToState)));
                DumpActions(fsm.Fsm, state);
            }
        }
        CaptureAudio();
        BindTextObjects();
        SampleVisibleText();
    }

    // Replica FSMs stay disabled, so their ActiveStateName is not the
    // authoritative continuation state. Record both explicitly at join and
    // completion; the caller already has EntityManager from its engine setup.
    internal void CaptureAuthority(EntityManager manager) {
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
        Log("AUTHORITY determined=" + manager.IsSceneHostDetermined + " local=" + manager.IsSceneHost);
        var entities = (Dictionary<ulong, Entity>)typeof(EntityManager).GetField("_entities", flags).GetValue(manager);
        foreach (var entity in entities.Values.Where(e => e.Type == EntityType.ColosseumManager)) {
            var fsms = (HostClientPair<List<PlayMakerFSM>>)typeof(Entity).GetField("_fsms", flags).GetValue(entity);
            var snapshots = (List<FsmSnapshot>)typeof(Entity).GetField("_fsmSnapshots", flags).GetValue(entity);
            for (var i = 0; i < snapshots.Count && i < fsms.Host.Count; i++) {
                var fsm = fsms.Host[i]; var snapshot = snapshots[i];
                Log("CONTINUATION entity=" + entity.Id + " fsm=" + i + ":" + fsm.FsmName + " state=" + snapshot.CurrentState +
                    " localNative=" + fsm.ActiveStateName);
                for (var k = 0; k < snapshot.Ints.Length && k < fsm.FsmVariables.IntVariables.Length; k++)
                    Log("CONTINUATION INT " + fsm.FsmName + "/" + fsm.FsmVariables.IntVariables[k].Name + "=" + snapshot.Ints[k]);
                for (var k = 0; k < snapshot.Strings.Length && k < fsm.FsmVariables.StringVariables.Length; k++)
                    Log("CONTINUATION STRING " + fsm.FsmName + "/" + fsm.FsmVariables.StringVariables[k].Name + "=" + Describe(snapshot.Strings[k]));
            }
        }
    }

    // Sound remains muted by the isolated runner. isPlaying/timeSamples/clip
    // verify source progression without using audible output as a criterion.
    internal void CaptureAudio() {
        Log("AUDIO LISTENER volume=" + AudioListener.volume + " paused=" + AudioListener.pause);
        foreach (var source in Resources.FindObjectsOfTypeAll<AudioSource>().Where(a =>
            a.gameObject.scene.IsValid() && a.gameObject.scene.isLoaded && Path(a.gameObject).Contains("Colosseum Manager")))
            Log("AUDIO " + Path(source.gameObject) + "#" + source.GetInstanceID() + " enabled=" + source.enabled +
                " active=" + source.gameObject.activeInHierarchy + " clip=" + Describe(source.clip) + " playing=" + source.isPlaying +
                " time=" + source.time + " samples=" + source.timeSamples + " volume=" + source.volume +
                " mute=" + source.mute + " loop=" + source.loop + " pitch=" + source.pitch +
                " mixer=" + Describe(source.outputAudioMixerGroup));
    }

    // Rebind at milestones or after a UI object is created. Avoid a full object
    // scan every frame; the runner may sample this cached list during the short
    // entry/title window to capture an intermittent literal "main".
    internal void BindTextObjects() {
        texts.Clear();
        foreach (var component in Resources.FindObjectsOfTypeAll<Component>()) {
            if (component == null || !component.gameObject.scene.IsValid() || !component.gameObject.scene.isLoaded) continue;
            if (!Watched(component.gameObject)) continue;
            var type = component.GetType();
            if (!(type.FullName?.StartsWith("TMPro.") == true || type.FullName == "UnityEngine.TextMesh" || type.FullName == "UnityEngine.UI.Text")) continue;
            if (type.GetProperty("text", PublicInstance)?.PropertyType == typeof(string)) texts.Add(component);
        }
    }

    internal void SampleVisibleText() {
        foreach (var component in texts) {
            if (component == null) continue;
            var active = component.gameObject.activeInHierarchy && (!(component is Behaviour behaviour) || behaviour.enabled);
            var text = (string)component.GetType().GetProperty("text", PublicInstance).GetValue(component, null);
            if (InArena && active && !string.IsNullOrEmpty(text) && Path(component.gameObject).Contains("/Area Title#")) {
                if (text.Contains("#!#_MAIN#!#") || text.Contains("#!#_SUB#!#") || text.Contains("#!#_SUPER#!#")) HasInvalidTitle = true;
                else HasArenaTitleText = true;
            }
            var observed = active + ":" + text;
            var id = component.GetInstanceID();
            if (previousText.TryGetValue(id, out var previous) && previous == observed) continue;
            previousText[id] = observed;
            Log("TEXT " + Path(component.gameObject) + "#" + id + " type=" + component.GetType().FullName +
                " active=" + active + " position=" + component.transform.position + " value=" + Describe(text));
        }
    }

    // Called from the probe's existing per-frame sampler. Keep this as a
    // single edge-triggered observation so the smoke log stays small while
    // proving that the late replica actually assigned and advanced a track.
    internal void SampleMusicPlayback() {
        if (HasMusicPlayback || !InArena) return;
        foreach (var source in Resources.FindObjectsOfTypeAll<AudioSource>().Where(a =>
            a != null && a.gameObject.scene.IsValid() && a.gameObject.scene.isLoaded &&
            Path(a.gameObject).Contains("Colosseum Manager") && a.gameObject.name == "Music Control")) {
            if (source.clip != null && (source.isPlaying || source.timeSamples > 0)) {
                HasMusicPlayback = true;
                Log("MUSIC PLAYBACK clip=" + Describe(source.clip) + " samples=" + source.timeSamples +
                    " playing=" + source.isPlaying + " path=" + Path(source.gameObject));
                break;
            }
        }
    }
}

