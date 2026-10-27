using System;
using System.Collections;
using System.IO;
using System.Linq;
using HollowConnect;
using HollowConnect.Core;
using UnityEngine;
using UnityEngine.UI;
using Object = UnityEngine.Object;

namespace HollowConnect.Smoke;

// Test-only image export using the production view and native game resources.
// ProbeMod intercepts all save writes before this unsaved character is started.
public sealed class DamageRankingPreview : MonoBehaviour {
    private string root;
    private IEnumerator Until(Func<bool> ready) {
        var deadline = Time.realtimeSinceStartup + 60;
        while (!ready() && Time.realtimeSinceStartup < deadline) yield return null;
        if (!ready()) throw new TimeoutException("damage preview scene readiness");
    }
    private IEnumerator Start() {
        root = Environment.GetEnvironmentVariable("HOLLOWCONNECT_SMOKE_ROOT");
        File.WriteAllText(Path.Combine(root, "host.log"), "Preview only; all save writes intercepted\n");
        yield return Until(() => GameManager.instance != null && Object.FindObjectOfType<CoopRuntime>() != null &&
            UnityEngine.SceneManagement.SceneManager.GetActiveScene().name == "Menu_Title");
        CoopMod.Settings.ShareProgress = false;
        PlayerData.instance.SetString("respawnScene", "Town");
        PlayerData.instance.SetString("respawnMarkerName", "RestBench");
        PlayerData.instance.SetInt("respawnType", 1);
        GameManager.instance.profileID = 99;
        GameManager.instance.StartCoroutine(GameManager.instance.RunContinueGame());
        yield return Until(() => GameManager.instance.sceneName == "Town" && HeroController.instance != null &&
            HeroController.instance.isHeroInPosition && !GameManager.instance.IsInSceneTransition);
        HeroController.instance.takeNoDamage = true;
        yield return new WaitForSecondsRealtime(5);
        var fallback = Hkmp.Ui.Resources.FontManager.UIFontRegular;
        using var view = new DamageRankingView(transform, fallback);
        view.Show(new DamageRankingSnapshot {
            Board = 1, Revision = 1, Sequence = "Boss Sequence Tier 5",
            Players = new[] {
                new DamageStanding { Id = 1, Name = "星河", Damage = 6318 },
                new DamageStanding { Id = 2, Name = "小骑士", Damage = 4173 },
                new DamageStanding { Id = 3, Name = "旅人", Damage = 2509 }
            }
        }, 2);
        var canvas = GetComponentInChildren<Canvas>();
        var scaler = canvas.GetComponent<CanvasScaler>();
        scaler.uiScaleMode = CanvasScaler.ScaleMode.ConstantPixelSize; scaler.scaleFactor = 1;
        var captureObject = new GameObject("Preview capture camera", typeof(Camera));
        var capture = captureObject.GetComponent<Camera>(); capture.enabled = false;
        capture.clearFlags = CameraClearFlags.Depth; capture.cullingMask = 1 << 31;
        capture.orthographic = true; capture.nearClipPlane = .1f; capture.farClipPlane = 100;
        var target = new RenderTexture(1920, 1080, 24); capture.targetTexture = target;
        foreach (var child in canvas.GetComponentsInChildren<Transform>(true)) child.gameObject.layer = 31;
        canvas.renderMode = RenderMode.ScreenSpaceCamera; canvas.worldCamera = capture; canvas.planeDistance = 1;
        Canvas.ForceUpdateCanvases(); yield return null; Canvas.ForceUpdateCanvases();
        File.AppendAllText(Path.Combine(root, "host.log"), "Fonts: " + string.Join(", ", canvas.GetComponentsInChildren<Text>().Select(t => t.font.name).Distinct()) +
            "\nOrnament: " + canvas.GetComponentsInChildren<Image>().FirstOrDefault(i => i.sprite != null)?.sprite.name + "\n");
        var pixels = new Texture2D(1920, 1080, TextureFormat.RGB24, false);
        var prior = RenderTexture.active;
        foreach (var camera in Object.FindObjectsOfType<Camera>().Where(c => c.enabled && c != capture).OrderBy(c => c.depth).Concat(new[] { capture })) {
            var original = camera.targetTexture; var projection = camera.projectionMatrix; var rect = camera.rect;
            try {
                camera.targetTexture = target; camera.rect = new Rect(0, 0, 1, 1);
                if (camera != capture) camera.projectionMatrix = projection;
                camera.Render();
            } finally { camera.targetTexture = original; camera.rect = rect; camera.projectionMatrix = projection; }
        }
        RenderTexture.active = target; pixels.ReadPixels(new Rect(0, 0, 1920, 1080), 0, 0); pixels.Apply();
        File.WriteAllBytes(Path.Combine(root, "damage-ranking-actual.png"), pixels.EncodeToPNG());
        RenderTexture.active = prior; Object.Destroy(pixels); Object.Destroy(target); Object.Destroy(captureObject);
        File.WriteAllText(Path.Combine(root, "host.done"), "Actual view captured with sample values");
        Application.Quit();
    }
}
