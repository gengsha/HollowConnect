using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using HollowConnect;
using HollowConnect.Core;
using HutongGames.PlayMaker;
using UnityEngine;
using Object = UnityEngine.Object;

namespace HollowConnect.Smoke;

public sealed partial class ProgressProbe {
    private readonly Dictionary<ResourceKind, int> notificationTotals = new Dictionary<ResourceKind, int>();
    private int notificationStarts;
    private string notificationError;
    private readonly HashSet<ResourceKind> notificationCaptures = new HashSet<ResourceKind>();
    private bool NotificationIdle {
        get {
            var notifications = Field<ResourceNotifications>(runtime, "resourceNotifications");
            var popup = Field<GameObject>(notifications, "active");
            return Field<Dictionary<ResourceKind, int>>(notifications, "pending").Count == 0 &&
                (popup == null || !popup.activeSelf);
        }
    }
    private void ObserveResourceNotification(Fsm fsm, FsmState state) {
        if (fsm.GameObject == null || fsm.GameObject.name != ResourceNotifications.PopupName ||
            fsm.Name != "Relic Msg" || state.Name != "Up") return;
        try {
            var popup = fsm.GameObject;
            var sprite = popup.transform.Find("Icon").GetComponent<SpriteRenderer>().sprite;
            var kind = sprite.name == "Heart_Piece_01" ? ResourceKind.MaskShard :
                sprite.name == "Mini_Soul_Orb_Collect" ? ResourceKind.VesselFragment :
                sprite.name == "inv_item__0009_ore" ? ResourceKind.PaleOre :
                sprite.name == "Charm_Notch" ? ResourceKind.CharmNotch : throw new Exception("unexpected notification icon " + sprite.name);
            var key = kind == ResourceKind.MaskShard ? "INV_NAME_HEARTPIECE_1" : kind == ResourceKind.VesselFragment ?
                "INV_NAME_SOULORBS_1" : kind == ResourceKind.PaleOre ? "INV_NAME_ORE" : "INV_NAME_NOTCH";
            var text = popup.transform.Find("Text").GetComponent<TMPro.TextMeshPro>().text;
            var split = text.Split('×');
            var amount = split.Length == 1 ? 1 : int.Parse(split[1]);
            if (amount <= 0 || split[0].TrimEnd() != Language.Language.Get(key, "UI")) throw new Exception("incorrect localized notification text " + text);
            var point = GameCameras.instance.hudCamera.WorldToViewportPoint(popup.transform.position);
            if (point.x < 0 || point.x > .3f || point.y < 0 || point.y > .3f || point.z <= 0)
                throw new Exception("notification outside lower-left HUD " + point);
            if (Object.FindObjectsOfType<PlayMakerFSM>().Count(f => f.name == ResourceNotifications.PopupName && f.FsmName == "Relic Msg") != 1)
                throw new Exception("overlapping resource notifications");
            notificationTotals[kind] = (notificationTotals.TryGetValue(kind, out var old) ? old : 0) + amount;
            notificationStarts++;
            Mark("NATIVE RESOURCE NOTIFICATION " + kind + " amount=" + amount + " text=" + text + " viewport=" + point);
            if (notificationCaptures.Add(kind)) StartCoroutine(CaptureResourceNotification(popup, kind));
        } catch (Exception e) { notificationError = e.ToString(); }
    }
    private IEnumerator ExpectResourceNotifications(Dictionary<ResourceKind, int> expected, string label) {
        yield return Until(() => notificationError != null || (NotificationIdle && expected.All(p =>
            (notificationTotals.TryGetValue(p.Key, out var amount) ? amount : 0) == p.Value)), label, 40);
        ResourceCheck(notificationError == null && notificationTotals.All(p => expected.TryGetValue(p.Key, out var amount) && amount == p.Value),
            label + (notificationError == null ? " with native localized icons and no overlap" : " " + notificationError));
        ResourceCheck(remoteResourceUiStarts == 0 && HeroController.instance.acceptingInput && !HeroController.instance.controlReqlinquished &&
            !PlayerData.instance.GetBool("disablePause"), "notification playback never starts fragment collection UI or takes control");
    }
    private IEnumerator CaptureResourceNotification(GameObject popup, ResourceKind kind) {
        yield return new WaitForSeconds(.6f);
        if (popup == null || !popup.activeInHierarchy) yield break;
        var hud = GameCameras.instance.hudCamera;
        var text = popup.transform.Find("Text").GetComponent<TMPro.TextMeshPro>();
        // Batch mode skips normal rendering callbacks; build the native text mesh before the explicit camera render.
        text.ForceMeshUpdate();
        Mark("RESOURCE NOTIFICATION FONT " + kind + " language=" + Language.Language.CurrentLanguage() + " font=" + text.font?.name +
            " size=" + text.fontSize + " characters=" + text.textInfo.characterCount + " visible=" +
            text.textInfo.characterInfo.Take(text.textInfo.characterCount).Count(c => c.isVisible) + " material=" + text.fontSharedMaterial?.name);
        var iconBounds = popup.transform.Find("Icon").GetComponent<SpriteRenderer>().bounds;
        var min = hud.WorldToViewportPoint(iconBounds.min); var max = hud.WorldToViewportPoint(iconBounds.max);
        if (min.x < 0 || min.y < 0 || max.x > 1 || max.y > 1)
            notificationError = "notification icon outside HUD: " + min + ".." + max;
        Mark("RESOURCE NOTIFICATION GEOMETRY " + kind + " icon=" + min + ".." + max +
            " hudAspect=" + hud.aspect + " screen=" + Screen.width + "x" + Screen.height);
        var width = Mathf.RoundToInt(600 * hud.aspect);
        var target = new RenderTexture(width, 600, 24);
        var pixels = new Texture2D(width, 600, TextureFormat.RGB24, false);
        var prior = RenderTexture.active;
        try {
            foreach (var camera in new[] { GameCameras.instance.mainCamera, GameCameras.instance.hudCamera }) {
                var original = camera.targetTexture;
                var projection = camera.projectionMatrix;
                var rect = camera.rect;
                try {
                    camera.targetTexture = target; camera.rect = new Rect(0, 0, 1, 1);
                    camera.projectionMatrix = projection; camera.Render();
                } finally { camera.targetTexture = original; camera.rect = rect; camera.projectionMatrix = projection; }
            }
            RenderTexture.active = target;
            pixels.ReadPixels(new Rect(0, 0, width, 600), 0, 0); pixels.Apply();
            File.WriteAllBytes(Path.Combine(root, role + "-notification-" + kind + ".png"), pixels.EncodeToPNG());
        } finally { RenderTexture.active = prior; Object.Destroy(pixels); Object.Destroy(target); }
    }
}
