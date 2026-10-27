using System;
using System.Linq;
using Hkmp.Util;
using Modding;
using Newtonsoft.Json;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Hkmp.Game.Client.Entity;

// Reconcile only the native boss/defeated alternatives actually present in
// this room. Do not alter PlayerData, persistence or unrelated additive scenes.
internal static class BossSceneLayout {
    private sealed class Choice { public string Main = "", Alt = "", Loaded = ""; }
    private static readonly System.Collections.Generic.HashSet<string> Loading = new System.Collections.Generic.HashSet<string>(StringComparer.Ordinal);
    private static uint Revision;
    private static bool Local(string scene) => !string.IsNullOrEmpty(scene) && scene.StartsWith(CoopContext.LocalScene + "_", StringComparison.Ordinal) && scene.IndexOf("boss", StringComparison.OrdinalIgnoreCase) >= 0;
    internal static bool HasLoadedBossScene(string json) {
        if (string.IsNullOrEmpty(json) || json.Length > 8192) return false;
        try {
            var choices = JsonConvert.DeserializeObject<Choice[]>(json);
            return choices != null && choices.Any(c => c != null && Local(c.Loaded));
        } catch { return false; }
    }
    internal static string Capture() => JsonConvert.SerializeObject(UnityEngine.Object.FindObjectsOfType<SceneAdditiveLoadConditional>(true)
        .Where(c => c.gameObject.scene.name == CoopContext.LocalScene && Local(c.sceneNameToLoad) && (string.IsNullOrEmpty(c.altSceneNameToLoad) || Local(c.altSceneNameToLoad)))
        .Select(c => new Choice { Main = c.sceneNameToLoad, Alt = c.altSceneNameToLoad ?? "",
            Loaded = UnityEngine.SceneManagement.SceneManager.GetSceneByName(c.sceneNameToLoad).isLoaded ? c.sceneNameToLoad : UnityEngine.SceneManagement.SceneManager.GetSceneByName(c.altSceneNameToLoad ?? "").isLoaded ? c.altSceneNameToLoad ?? "" : "" })
        .Where(c => c.Loaded.Length != 0).OrderBy(c => c.Main, StringComparer.Ordinal).ToArray());
    internal static void Apply(string json) {
        if (json.Length > 8192) throw new FormatException("Boss layout exceeds limit");
        var choices = JsonConvert.DeserializeObject<Choice[]>(json);
        if (choices == null || choices.Length > 16) return;
        var revision = unchecked(++Revision);
        var native = UnityEngine.Object.FindObjectsOfType<SceneAdditiveLoadConditional>(true);
        var valid = choices.Where(c => c != null && c.Main != null && c.Alt != null && c.Loaded != null && Local(c.Main) &&
            (c.Alt.Length == 0 || Local(c.Alt)) && c.Loaded.Length != 0 && (c.Loaded == c.Main || c.Loaded == c.Alt) &&
            native.Any(n => n.gameObject.scene.name == CoopContext.LocalScene && n.sceneNameToLoad == c.Main && (n.altSceneNameToLoad ?? "") == c.Alt)).ToArray();
        foreach (var c in valid) {
            var other = c.Loaded == c.Main ? c.Alt : c.Main;
            if (!Application.CanStreamedLevelBeLoaded(c.Loaded)) continue;
            var wanted = UnityEngine.SceneManagement.SceneManager.GetSceneByName(c.Loaded);
            var owner = native.First(n => n.gameObject.scene.name == CoopContext.LocalScene && n.sceneNameToLoad == c.Main &&
                (n.altSceneNameToLoad ?? "") == c.Alt);
            if (wanted.isLoaded) {
                BindNativeUnload(owner, c.Loaded);
                var stale = UnityEngine.SceneManagement.SceneManager.GetSceneByName(other);
                if (stale.IsValid() && stale.isLoaded) UnityEngine.SceneManagement.SceneManager.UnloadSceneAsync(stale);
                continue;
            }
            var baseScene = CoopContext.LocalScene;
            var visit = CoopContext.LocalVisit;
            var key = baseScene + "|" + visit + "|" + c.Main + "|" + c.Alt + "|" + c.Loaded;
            if (!Loading.Add(key)) continue;
            MonoBehaviour runner = MonoBehaviourUtil.Instance != null ? (MonoBehaviour)MonoBehaviourUtil.Instance : global::GameManager.instance;
            if (runner == null) { Loading.Remove(key); continue; }
            runner.StartCoroutine(LoadChoice(owner, c.Loaded, other, baseScene, visit, revision, key));
        }
        // An empty authoritative layout means the host has already removed
        // the live boss scene. Unload any stale local variant as well; merely
        // loading the desired scene would otherwise leave a guest fighting a
        // boss that the host has completed.
        var desiredPairs = new System.Collections.Generic.HashSet<string>(valid.Select(c => c.Main + "|" + c.Alt), StringComparer.Ordinal);
        foreach (var conditional in native.Where(n => n.gameObject.scene.name == CoopContext.LocalScene && Local(n.sceneNameToLoad) &&
            (string.IsNullOrEmpty(n.altSceneNameToLoad) || Local(n.altSceneNameToLoad)))) {
            var main = conditional.sceneNameToLoad;
            var alt = conditional.altSceneNameToLoad ?? "";
            if (desiredPairs.Contains(main + "|" + alt)) continue;
            foreach (var scene in new[] { main, alt }.Where(s => s.Length != 0).Distinct(StringComparer.Ordinal)) {
                var loaded = UnityEngine.SceneManagement.SceneManager.GetSceneByName(scene);
                if (loaded.IsValid() && loaded.isLoaded) UnityEngine.SceneManagement.SceneManager.UnloadSceneAsync(loaded);
            }
        }
    }

    // SceneAdditiveLoadConditional.LoadRoutine also notifies GameManager after
    // the additive operation completes. Boss controllers and dream-entry FSMs
    // use that callback to release their transition state; a direct guest load
    // must preserve the same native lifecycle.
    private static void BindNativeUnload(SceneAdditiveLoadConditional owner, string scene) {
        // Native OnDisable only unloads its selected variant when sceneLoaded
        // is true. A network load must belong to that same native owner.
        ReflectionHelper.SetField(owner, "loadAlt", scene == owner.altSceneNameToLoad);
        ReflectionHelper.SetField(owner, "sceneLoaded", true);
    }

    private static System.Collections.IEnumerator LoadChoice(SceneAdditiveLoadConditional owner, string scene, string other, string baseScene, uint visit, uint revision, string key) {
        try {
            if (revision != Revision) yield break;
            var wrong = UnityEngine.SceneManagement.SceneManager.GetSceneByName(other);
            if (wrong.IsValid() && wrong.isLoaded) {
                var unload = UnityEngine.SceneManagement.SceneManager.UnloadSceneAsync(wrong);
                if (unload != null) while (!unload.isDone) yield return null;
            }
            if (revision != Revision || owner == null || CoopContext.LocalScene != baseScene || CoopContext.LocalVisit != visit) yield break;
            if (UnityEngine.SceneManagement.SceneManager.GetSceneByName(scene).isLoaded) {
                BindNativeUnload(owner, scene);
                yield break;
            }
            var load = UnityEngine.SceneManagement.SceneManager.LoadSceneAsync(scene, LoadSceneMode.Additive);
            if (load == null) yield break;
            while (!load.isDone) yield return null;
            if (owner == null || !owner.gameObject.scene.isLoaded || CoopContext.LocalScene != baseScene || CoopContext.LocalVisit != visit) {
                // A base-room exit can finish while Unity is loading the
                // additive scene. Its destroyed owner cannot clean it up.
                var stale = UnityEngine.SceneManagement.SceneManager.GetSceneByName(scene);
                if (CoopContext.LocalScene != baseScene && stale.IsValid() && stale.isLoaded)
                    UnityEngine.SceneManagement.SceneManager.UnloadSceneAsync(stale);
                yield break;
            }
            BindNativeUnload(owner, scene);
            if (global::GameManager.instance != null && CoopContext.LocalScene == baseScene && CoopContext.LocalVisit == visit &&
                UnityEngine.SceneManagement.SceneManager.GetActiveScene().name == baseScene)
                global::GameManager.instance.LoadedBoss();
        } finally {
            Loading.Remove(key);
        }
    }
}
