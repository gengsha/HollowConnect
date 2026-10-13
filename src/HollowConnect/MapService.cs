using System;
using System.Collections.Generic;
using System.Linq;
using Hkmp;
using Hkmp.Game.Client;
using HollowConnect.Core;
using Modding;
using UnityEngine;
using Object = UnityEngine.Object;

namespace HollowConnect;

internal sealed class MapService : IDisposable {
    private readonly CoopRuntime runtime;
    private readonly Dictionary<ushort, GameObject> icons = new Dictionary<ushort, GameObject>();
    private GameMap currentMap;
    private bool open;
    private Font mapFont;
    public MapService(CoopRuntime runtime) { this.runtime = runtime; }
    private static GameMap GameMap => GameManager.instance != null && GameManager.instance.gameMap != null
        ? GameManager.instance.gameMap.GetComponent<GameMap>() : null;
    public void Register() {
        On.GameMap.PositionCompass += OnPositionCompass;
        On.GameMap.CloseQuickMap += OnClose;
        Hkmp.Util.MonoBehaviourUtil.Instance.OnUpdateEvent += Update;
        mapFont = Font.CreateDynamicFontFromOSFont(new[] { "Microsoft YaHei", "Arial" }, 36);
    }
    private void OnPositionCompass(On.GameMap.orig_PositionCompass orig, GameMap self, bool shade) {
        orig(self, shade);
        if (shade || ReflectionHelper.GetField<GameMap, bool>(self, "posGate")) return;
        open = true;
    }
    private void OnClose(On.GameMap.orig_CloseQuickMap orig, GameMap self) { orig(self); open = false; }

    public bool TryLocation(out Vector3 location) {
        location = Vector3.zero;
        var map = GameMap; var gm = GameManager.instance; var hero = HeroController.instance;
        if (map == null || gm == null || hero == null || gm.IsInSceneTransition || !hero.isHeroInPosition) return false;
        var zone = map.inRoom ? map.doorMapZone : gm.GetCurrentMapZone();
        var scene = map.inRoom ? map.doorScene : gm.sceneName;
        var area = MapManager.GetAreaObjectByName(map, zone);
        if (area == null || string.IsNullOrEmpty(scene)) return false;
        Transform room = null;
        for (var i = 0; i < area.transform.childCount; i++) {
            var child = area.transform.GetChild(i);
            if (child.name == scene) { room = child; break; }
        }
        if (room == null) return false;
        var renderer = room.GetComponent<SpriteRenderer>();
        if (renderer == null || renderer.sprite == null) return false;
        var center = room.localPosition + area.transform.localPosition;
        var size = renderer.sprite.bounds.size;
        var width = map.inRoom ? map.doorSceneWidth : ReflectionHelper.GetField<GameMap, float>(map, "sceneWidth");
        var height = map.inRoom ? map.doorSceneHeight : ReflectionHelper.GetField<GameMap, float>(map, "sceneHeight");
        var ox = map.inRoom ? map.doorOriginOffsetX : ReflectionHelper.GetField<GameMap, float>(map, "originOffsetX");
        var oy = map.inRoom ? map.doorOriginOffsetY : ReflectionHelper.GetField<GameMap, float>(map, "originOffsetY");
        var x = map.inRoom ? map.doorX : hero.transform.position.x;
        var y = map.inRoom ? map.doorY : hero.transform.position.y;
        if (!Geometry.TryProject(center.x, center.y, size.x, size.y, width, height, ox, oy, x, y, out var mx, out var my)) return false;
        location = new Vector3(mx, my, -1); return true;
    }

    private static Color PeerColor(ushort id) => Color.HSVToRGB((id * .618034f + .1f) % 1, .65f, 1);
    private void Update() {
        if (!runtime.Connected) return;
        var map = GameMap;
        if (currentMap != map) { Clear(); currentMap = map; }
        if (map == null) return;
        foreach (var pair in runtime.Peers) {
            var p = pair.Value; var state = p.State;
            var valid = state != null && state.HasMap && state.IsReady && CoopRuntime.Now - p.Seen < 2;
            if (!icons.TryGetValue(pair.Key, out var icon) || icon == null) {
                if (!valid || map.compassIcon == null) continue;
                icon = Object.Instantiate(map.compassIcon, map.transform);
                icon.name = "HollowConnect teammate " + pair.Key;
                foreach (var fsm in icon.GetComponentsInChildren<PlayMakerFSM>(true)) { fsm.enabled = false; Object.Destroy(fsm); }
                foreach (var r in icon.GetComponentsInChildren<SpriteRenderer>(true)) r.color = PeerColor(pair.Key);
                foreach (var r in icon.GetComponentsInChildren<tk2dSprite>(true)) r.color = PeerColor(pair.Key);
                icons[pair.Key] = icon;
            }
            icon.SetActive(open && valid);
            if (valid) icon.transform.localPosition = new Vector3(state.MapX, state.MapY, map.compassIcon.transform.localPosition.z - .01f);
        }
        foreach (var id in icons.Keys.Where(id => !runtime.Peers.ContainsKey(id)).ToArray()) Remove(id);
    }

    public void DrawOverlay(GUIStyle baseStyle) {
        if (!runtime.Playable) return;
        var camera = GameCameras.instance != null ? GameCameras.instance.mainCamera : Camera.main;
        if (camera == null) return;
        var style = new GUIStyle(baseStyle) { fontSize = 15, wordWrap = false };
        var row = 125f;
        foreach (var pair in runtime.Peers.OrderBy(p => p.Key)) {
            var p = pair.Value; var s = p.State;
            if (s == null) continue;
            var fresh = s.IsReady && CoopRuntime.Now - p.Seen < 2;
            var selected = pair.Key == runtime.Selected ? "◆ " : "";
            var label = selected + s.Name + (s.Value == (int)Life.Down ? " [暗影]" : s.Value == (int)Life.Returning ? " [回椅中]" : "");
            style.normal.textColor = PeerColor(pair.Key);
            if (open) {
                if (fresh && s.HasMap && icons.TryGetValue(pair.Key, out var icon) && icon != null) {
                    var point = camera.WorldToScreenPoint(icon.transform.position);
                    GUI.Label(new Rect(point.x - 90, Screen.height - point.y - 40, 180, 30), label, style);
                } else {
                    GUI.Label(new Rect(20, row, 350, 30), label + (fresh ? " · " + s.Scene + "（无地图坐标）" : " · 定位更新中"), style);
                    row += 28;
                }
                continue;
            }
            if (!CoopMod.Settings.OffscreenNames) continue;
            if (!fresh || s.Scene != CoopContext.LocalScene) {
                GUI.Label(new Rect(20, row, 350, 30), label + " · " + (fresh ? s.Scene : "定位更新中"), style); row += 28; continue;
            }
            var viewport = camera.WorldToViewportPoint(new Vector3(s.X, s.Y, HeroController.instance.transform.position.z));
            if (viewport.z > 0 && viewport.x >= 0 && viewport.x <= 1 && viewport.y >= 0 && viewport.y <= 1) continue;
            var dx = (viewport.x - .5f) * Screen.width;
            var dy = (.5f - viewport.y) * Screen.height;
            Geometry.Edge(dx, dy, Math.Max(30, Screen.width / 2f - 110), Math.Max(30, Screen.height / 2f - 55), out var ex, out var ey);
            var arrow = Math.Abs(dx) > Math.Abs(dy) ? (dx > 0 ? "▶" : "◀") : (dy > 0 ? "▼" : "▲");
            GUI.Label(new Rect(Screen.width / 2f + ex - 100, Screen.height / 2f + ey - 15, 200, 30), arrow + " " + label, style);
        }
    }
    public void Remove(ushort id) { if (icons.TryGetValue(id, out var icon) && icon != null) Object.Destroy(icon); icons.Remove(id); }
    public void Clear() { foreach (var id in icons.Keys.ToArray()) Remove(id); open = false; }
    public void Dispose() {
        Clear(); On.GameMap.PositionCompass -= OnPositionCompass; On.GameMap.CloseQuickMap -= OnClose;
        Hkmp.Util.MonoBehaviourUtil.Instance.OnUpdateEvent -= Update;
        if (mapFont != null) Object.Destroy(mapFont);
    }
}
