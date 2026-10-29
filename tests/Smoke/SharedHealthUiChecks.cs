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

public sealed partial class ProgressProbe {
    private bool SharedHealthUiCase => Environment.GetEnvironmentVariable("HOLLOWCONNECT_SHARED_HEALTH_UI") == "on";
    private PlayMakerFSM[] WhiteMasks => GameCameras.instance.hudCanvas.GetComponentsInChildren<PlayMakerFSM>(true)
        .Where(f => f.FsmName == "health_display" && f.gameObject.activeInHierarchy).ToArray();
    private bool MasksMatch(int capacity, int health) => WhiteMasks.Length >= capacity && WhiteMasks.All(f => {
        var number = f.FsmVariables.FindFsmInt("Health Number").Value;
        return f.ActiveStateName == (number > capacity ? "Inactive" : number > health ? "Empty" : "Idle") &&
            f.transform.Find("Idle").GetComponent<Renderer>().enabled == (number <= health);
    });
    private IEnumerator SharedHealthUiChecks() {
        var pd = PlayerData.instance;
        if (host) {
            yield return Until(() => Field<bool>(runtime, "linkedHealthStateReceived"), "UI shared host ready");
            Flag("host.linked.join-ready");
        }
        yield return ResourceBarrier("shared-ui-joined");
        var capacity = 9 + CoopMod.Settings.LinkedHealthBonusMasks;
        yield return Until(() => pd.GetInt("maxHealth") == capacity && pd.health == capacity, "UI both inherit host capacity");
        yield return Until(() => MasksMatch(capacity, capacity), "native white masks match full shared capacity " + capacity, 25);
        var masks = WhiteMasks.OrderBy(f => f.FsmVariables.FindFsmInt("Health Number").Value).ToArray();
        var step = masks[1].transform.localPosition - masks[0].transform.localPosition;
        ResourceCheck(masks.All(f => (f.transform.localPosition - masks[0].transform.localPosition - step *
            (f.FsmVariables.FindFsmInt("Health Number").Value - 1)).sqrMagnitude < .001f), "extended masks keep native spacing");
        CaptureSharedUi("full-" + capacity);
        yield return ResourceBarrier("ui-full");
        if (host) {
            var hero = HeroController.instance; hero.takeNoDamage = false; hero.cState.invulnerable = false;
            hero.TakeDamage(null, GlobalEnums.CollisionSide.left, 3, 1);
        }
        yield return Until(() => pd.health == capacity - 3 && MasksMatch(capacity, capacity - 3), "three shared hits show exactly three empty masks");
        yield return ResourceBarrier("ui-damaged");
        // More than a screen of native blue health must survive untouched.
        var parent = masks[0].transform.parent;
        var blue = parent.GetComponents<PlayMakerFSM>().Single(f => f.FsmName == "Blue Health Control");
        yield return Until(() => blue.ActiveStateName == "Idle", "native blue HUD ready");
        for (var i = 0; i < (host ? 3 : 30); i++) {
            blue.SendEvent("ADD BLUE HEALTH"); yield return null;
        }
        yield return new WaitForSecondsRealtime(.6f);
        Mark("BLUE DIAGNOSTIC raw=" + pd.healthBlue + " control=" + blue.ActiveStateName + " children=" + string.Join(";",
            parent.GetComponentsInChildren<PlayMakerFSM>(true).Where(f => f.FsmName != "health_display").Select(f =>
                f.name + "/" + f.FsmName + "=" + f.ActiveStateName + " active=" + f.gameObject.activeInHierarchy)));
        Dump(blue);
        yield return Until(() => parent.GetComponentsInChildren<PlayMakerFSM>().Count(f => f.FsmName == "blue_health_display" &&
            f.name.StartsWith("Blue Health", StringComparison.Ordinal)) == (host ? 3 : 30), "native blue mask count can extend beyond screen");
        yield return new WaitForSecondsRealtime(.5f);
        var blues = parent.GetComponentsInChildren<PlayMakerFSM>().Where(f => f.FsmName == "blue_health_display" &&
            f.name.StartsWith("Blue Health", StringComparison.Ordinal)).ToArray();
        ResourceCheck(pd.healthBlue == (host ? 3 : 30) && blues.All(f => Mathf.Abs(f.transform.localPosition.x -
            (masks[0].transform.localPosition.x + step.x * (capacity + f.FsmVariables.FindFsmInt("Health Number").Value - 1))) < .02f),
            "local blue count and placement follow complete white capacity");
        CaptureSharedUi("hurt-blue-" + capacity);
        yield return ResourceBarrier("ui-blue");
        if (host) HeroController.instance.AddHealth(3);
        yield return Until(() => MasksMatch(capacity, capacity), "shared heal refills all native masks");
        if (CoopMod.Settings.LinkedHealthBonusMasks == 2) {
            if (host) yield return RestSit();
            yield return Until(() => RestCount.Resting == 1, "real bench starts left notification");
            yield return new WaitForSecondsRealtime(.5f);
            var view = Field<object>(runtime, "linkedRestView");
            var label = Field<Text>(view, "label");
            var rootObject = Field<GameObject>(view, "root");
            ResourceCheck(label.font.name == "NotoSerifCJKsc-Regular" && label.rectTransform.anchoredPosition.x == 64 &&
                label.text.Contains("1/2"), "bench notification uses native Chinese font and rests near left edge");
            CaptureSharedUi("bench-shown", rootObject.GetComponent<Canvas>());
            yield return ResourceBarrier("ui-bench-shown");
            if (host) yield return RestStand();
            yield return Until(() => RestCount.Resting == 0, "bench exit starts slide out");
            yield return Until(() => !rootObject.activeSelf, "notification finishes leftward exit");
            ResourceCheck(label.rectTransform.anchoredPosition.x + label.rectTransform.rect.width < 0, "hidden notification is fully beyond left camera edge");
            // A separate visual fixture can reverse targets without the runtime
            // overwriting them from live network state each frame.
            using (var visual = new LinkedRestView(transform)) {
                var visualLabel = Field<Text>(visual, "label");
                var visualRoot = Field<GameObject>(visual, "root");
                visual.Show(new LinkedRestCount(2, 0, 1), "progressguest（未就位）");
                yield return new WaitForSecondsRealtime(.12f);
                var entering = visualLabel.rectTransform.anchoredPosition.x;
                visual.Hide(); yield return null; yield return null;
                ResourceCheck(visualRoot.activeSelf && visualLabel.rectTransform.anchoredPosition.x < entering, "entry reverses left smoothly before hiding");
                var leaving = visualLabel.rectTransform.anchoredPosition.x;
                visual.Show(new LinkedRestCount(2, 0, 1), "progressguest（未就位）");
                yield return null; yield return null;
                ResourceCheck(visualRoot.activeSelf && visualLabel.rectTransform.anchoredPosition.x > leaving, "exit reverses right without a position reset");
                visual.Hide();
            }
        }
        yield return ResourceBarrier("ui-before-exit");
        var localBlue = pd.healthBlue;
        engine.ClientManager.Disconnect();
        yield return Until(() => MasksMatch(pd.GetInt("maxHealth"), pd.health), "disconnect restores personal mask capacity and fill");
        yield return new WaitForSecondsRealtime(.5f);
        ResourceCheck(pd.healthBlue == localBlue && parent.GetComponentsInChildren<PlayMakerFSM>(true).Count(f => f.FsmName == "health_display") == 11,
            "mode exit removes only added white visuals and preserves local blue health");
        CaptureSharedUi("restored-personal");
        yield return ResourceBarrier("shared-ui-observed");
    }

    private void CaptureSharedUi(string name, Canvas overlay = null) {
        var target = new RenderTexture(1920, 1080, 24);
        var pixels = new Texture2D(1920, 1080, TextureFormat.RGB24, false);
        var prior = RenderTexture.active;
        var cameras = Object.FindObjectsOfType<Camera>().Where(c => c.enabled).OrderBy(c => c.depth).ToArray();
        GameObject captureCamera = null;
        Transform[] overlayObjects = null;
        int[] overlayLayers = null;
        try {
            foreach (var camera in cameras) {
                var original = camera.targetTexture; var projection = camera.projectionMatrix; var rect = camera.rect;
                try {
                    camera.targetTexture = target; camera.rect = new Rect(0, 0, 1, 1); camera.projectionMatrix = projection;
                    Canvas.ForceUpdateCanvases(); camera.Render();
                } finally { camera.targetTexture = original; camera.rect = rect; camera.projectionMatrix = projection; }
            }
            if (overlay != null) {
                // Overlay canvases are outside Camera.Render. Give just this
                // canvas a temporary capture camera/layer for visual evidence.
                captureCamera = new GameObject("Rest UI capture camera");
                var camera = captureCamera.AddComponent<Camera>(); camera.enabled = false;
                camera.clearFlags = CameraClearFlags.Depth; camera.cullingMask = 1 << 31;
                camera.targetTexture = target; camera.nearClipPlane = .1f; camera.farClipPlane = 10;
                overlayObjects = overlay.GetComponentsInChildren<Transform>(true);
                overlayLayers = overlayObjects.Select(t => t.gameObject.layer).ToArray();
                foreach (var obj in overlayObjects) obj.gameObject.layer = 31;
                overlay.renderMode = RenderMode.ScreenSpaceCamera; overlay.worldCamera = camera; overlay.planeDistance = 1;
                Canvas.ForceUpdateCanvases(); camera.Render();
            }
            RenderTexture.active = target; pixels.ReadPixels(new Rect(0, 0, 1920, 1080), 0, 0); pixels.Apply();
            File.WriteAllBytes(Path.Combine(root, role + "-" + name + ".png"), pixels.EncodeToPNG());
        } finally {
            if (overlay != null) {
                overlay.renderMode = RenderMode.ScreenSpaceOverlay; overlay.worldCamera = null;
                if (overlayObjects != null) for (var i = 0; i < overlayObjects.Length; i++) overlayObjects[i].gameObject.layer = overlayLayers[i];
            }
            if (captureCamera != null) Object.Destroy(captureCamera);
            RenderTexture.active = prior; Object.Destroy(pixels); Object.Destroy(target);
        }
    }
}
