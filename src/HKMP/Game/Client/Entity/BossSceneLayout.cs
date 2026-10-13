using System;
using System.Linq;
using Newtonsoft.Json;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Hkmp.Game.Client.Entity;

// Reconcile only the native boss/defeated alternatives actually present in
// this room. Do not alter PlayerData, persistence or unrelated additive scenes.
internal static class BossSceneLayout {
    private sealed class Choice { public string Main = "", Alt = "", Loaded = ""; }
    private static bool Local(string scene) => !string.IsNullOrEmpty(scene) && scene.StartsWith(CoopContext.LocalScene + "_", StringComparison.Ordinal) && scene.IndexOf("boss", StringComparison.OrdinalIgnoreCase) >= 0;
    internal static string Capture() => JsonConvert.SerializeObject(UnityEngine.Object.FindObjectsOfType<SceneAdditiveLoadConditional>(true)
        .Where(c => c.gameObject.scene.name == CoopContext.LocalScene && Local(c.sceneNameToLoad) && (string.IsNullOrEmpty(c.altSceneNameToLoad) || Local(c.altSceneNameToLoad)))
        .Select(c => new Choice { Main = c.sceneNameToLoad, Alt = c.altSceneNameToLoad,
            Loaded = UnityEngine.SceneManagement.SceneManager.GetSceneByName(c.sceneNameToLoad).isLoaded ? c.sceneNameToLoad : UnityEngine.SceneManagement.SceneManager.GetSceneByName(c.altSceneNameToLoad).isLoaded ? c.altSceneNameToLoad : "" })
        .Where(c => c.Loaded.Length != 0).OrderBy(c => c.Main, StringComparer.Ordinal).ToArray());
    internal static void Apply(string json) {
        if (json.Length > 8192) throw new FormatException("Boss layout exceeds limit");
        var choices = JsonConvert.DeserializeObject<Choice[]>(json);
        if (choices == null || choices.Length > 16) return;
        var native = UnityEngine.Object.FindObjectsOfType<SceneAdditiveLoadConditional>(true);
        foreach (var c in choices) {
            if (c == null || !Local(c.Main) || (c.Alt.Length != 0 && !Local(c.Alt)) || c.Loaded.Length == 0 || (c.Loaded != c.Main && c.Loaded != c.Alt) ||
                !native.Any(n => n.gameObject.scene.name == CoopContext.LocalScene && n.sceneNameToLoad == c.Main && (n.altSceneNameToLoad ?? "") == c.Alt)) continue;
            if (!Application.CanStreamedLevelBeLoaded(c.Loaded) || UnityEngine.SceneManagement.SceneManager.GetSceneByName(c.Loaded).isLoaded) continue;
            var other = c.Loaded == c.Main ? c.Alt : c.Main;
            var wrong = UnityEngine.SceneManagement.SceneManager.GetSceneByName(other);
            if (wrong.isLoaded) UnityEngine.SceneManagement.SceneManager.UnloadSceneAsync(wrong);
            UnityEngine.SceneManagement.SceneManager.LoadSceneAsync(c.Loaded, LoadSceneMode.Additive);
        }
    }
}
