using System;
using System.Collections;
using System.IO;
using System.Linq;
using System.Reflection;
using HutongGames.PlayMaker;
using UnityEngine;
using Object = UnityEngine.Object;

namespace HollowConnect.Smoke;

// Read native assets in a fresh unsaved character; never run shade recovery FSMs.
public sealed class ShadeInspectProbe : MonoBehaviour {
    private string root, role;
    private void Log(string s) => File.AppendAllText(Path.Combine(root, role + ".inspect.txt"), s + "\n");
    private string Describe(object o) {
        if (o == null) return "null";
        if (o is FsmGameObject go) return Describe(go.Value);
        if (o is FsmObject fo) return Describe(fo.Value);
        if (o is UnityEngine.Object u) return u.GetType().Name + ":" + u.name;
        if (o is Array a) return "[" + string.Join(",", a.Cast<object>().Select(Describe)) + "]";
        return o.ToString();
    }
    private void Dump(GameObject go) {
        Log("OBJECT " + go.name + " active=" + go.activeSelf);
        foreach (var c in go.GetComponents<Component>()) {
            if (c == null) continue;
            Log("COMPONENT " + c.GetType().FullName);
            foreach (var f in c.GetType().GetFields(BindingFlags.Instance | BindingFlags.Public)) Log("  " + f.Name + "=" + Describe(f.GetValue(c)));
        }
        foreach (var fsm in go.GetComponentsInChildren<PlayMakerFSM>(true)) {
            fsm.Fsm.InitData();
            Log("FSM " + fsm.name + "/" + fsm.FsmName);
            foreach (var state in fsm.FsmStates) {
                Log("STATE " + state.Name + " transitions=" + string.Join(",", state.Transitions.Select(t => t.EventName + ":" + t.ToState)));
                foreach (var a in state.Actions) {
                    Log("ACTION " + a.GetType().FullName);
                    foreach (var f in a.GetType().GetFields(BindingFlags.Instance | BindingFlags.Public)) Log("  " + f.Name + "=" + Describe(f.GetValue(a)));
                }
            }
            foreach (var v in fsm.FsmVariables.GameObjectVariables) Log("VAR " + v.Name + "=" + Describe(v.Value));
            foreach (var v in fsm.FsmVariables.ObjectVariables) Log("VAR " + v.Name + "=" + Describe(v.Value));
        }
        foreach (var t in go.GetComponentsInChildren<Transform>(true)) {
            var a = t.GetComponent<tk2dSpriteAnimator>(); var p = t.GetComponent<ParticleSystem>(); var sound = t.GetComponent<AudioSource>();
            Log("CHILD " + t.name + " active=" + t.gameObject.activeSelf + " pos=" + t.localPosition + " scale=" + t.localScale + " scripts=" + string.Join(",", t.GetComponents<MonoBehaviour>().Select(c => c.GetType().Name)));
            if (a != null) Log("CLIPS " + t.name + " " + string.Join(",", a.Library.clips.Select(c => c.name + ":" + c.fps + ":" + c.frames.Length)) + " default=" + a.DefaultClipId);
            if (p != null) Log("PARTICLE " + t.name + " count=" + p.main.maxParticles + " lifetime=" + p.main.startLifetime.constantMax + " duration=" + p.main.duration + " loop=" + p.main.loop);
            if (sound != null) Log("AUDIO " + t.name + " " + Describe(sound.clip) + " volume=" + sound.volume);
        }
    }
    private IEnumerator Start() {
        root = Environment.GetEnvironmentVariable("HOLLOWCONNECT_SMOKE_ROOT"); role = Environment.GetEnvironmentVariable("HOLLOWCONNECT_SMOKE_ROLE");
        while (GameManager.instance == null || UnityEngine.SceneManagement.SceneManager.GetActiveScene().name != "Menu_Title") yield return null;
        var pd = PlayerData.instance; pd.SetString("respawnScene", "Town"); pd.SetString("respawnMarkerName", "RestBench"); pd.SetInt("respawnType", 1);
        GameManager.instance.profileID = 99; GameManager.instance.StartCoroutine(GameManager.instance.RunContinueGame());
        while (HeroController.instance == null || !HeroController.instance.isHeroInPosition || GameManager.instance.IsInSceneTransition) yield return null;
        yield return new WaitForSecondsRealtime(1);
        foreach (var go in Resources.FindObjectsOfTypeAll<GameObject>().Where(g => g.name == "Hollow Shade" || g.name.Contains("Shade Death"))) Dump(go);
        foreach (var fsm in HeroController.instance.GetComponentsInChildren<PlayMakerFSM>(true).Where(f => f.FsmName == "Spell Control")) {
            foreach (var s in fsm.FsmStates.Where(s => s.Name.Contains("Focus")))
                Log("FOCUS " + s.Name + " " + string.Join(";", s.Actions.Select(a => a.GetType().Name + ":" + string.Join(",", a.GetType().GetFields().Select(f => f.Name + "=" + Describe(f.GetValue(a)))))));
        }
        Log("FOCUS COST " + pd.GetInt("focusMP_amount")); File.WriteAllText(Path.Combine(root, role + ".done"), "inspected");
    }
}
