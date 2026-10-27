using System;
using System.Collections;
using System.IO;
using System.Linq;
using System.Reflection;
using Hkmp.Game.Client.Entity;
using Hkmp.Game.Client.Entity.Component;
using UnityEngine;
using Object = UnityEngine.Object;

namespace HollowConnect.Smoke;

public sealed partial class CoverageProbe {
    private void RadianceVisualDump(string label) {
        var boss = RadianceBossEntity();
        var arena = boss.Object.Host.transform.parent;
        var components = Field<System.Collections.Generic.Dictionary<EntityComponentType, EntityComponent>>(boss, "_components");
        var component = components[EntityComponentType.RadianceArena];
        var received = Field<System.Collections.Generic.Dictionary<string, bool>>(component, "received");
        Mark("RADIANCE ARENA VALUES " + label + " authority=" + manager.IsSceneHost + " intro=" + arena.GetComponent<PlayMakerFSM>()?.ActiveStateName +
            " remoteStarted=" + Field<bool>(component, "receivedStarted") + " received=" +
            (received == null ? "none" : string.Join(";", received.Select(p => p.Key + "=" + p.Value))));
        foreach (Transform group in arena.Find("Plat Sets"))
            Mark("RADIANCE GROUP VALUES " + label + " " + group.name + " active=" + group.gameObject.activeSelf + "/" + group.gameObject.activeInHierarchy);
        foreach (var entity in Entities.Values.Where(e => e.Type == EntityType.RadiancePlatform).OrderBy(e => e.Id)) {
            var go = manager.IsSceneHost ? entity.Object.Host : entity.Object.Client;
            var sprite = go.GetComponent<tk2dBaseSprite>();
            var renderer = go.GetComponent<Renderer>();
            var material = renderer?.sharedMaterial;
            var frame = sprite?.GetCurrentSpriteDef();
            Mark("RADIANCE VISUAL " + label + " id=" + entity.Id + " name=" + go.name + " parent=" + go.transform.parent?.name +
                " active=" + go.activeSelf + "/" + go.activeInHierarchy + " sprite=" + sprite?.spriteId + "/" + frame?.name +
                " renderer=" + renderer?.enabled + " bounds=" + renderer?.bounds + " color=" + sprite?.color +
                " material=" + material?.name + " shader=" + material?.shader?.name +
                " solid=" + go.GetComponentsInChildren<Collider2D>(true).Count(RadiancePlatform) +
                " scripts=" + string.Join(",", go.GetComponents<UnityEngine.Component>().Where(c => c != null).Select(c => c.GetType().Name)) +
                " replicaFsm=" + go.GetComponent<PlayMakerFSM>()?.ActiveStateName + "/" + go.GetComponent<PlayMakerFSM>()?.enabled + "/" + go.GetComponent<PlayMakerFSM>()?.Fsm.Started +
                " hostFsm=" + entity.Object.Host.GetComponent<PlayMakerFSM>()?.ActiveStateName);
        }
    }
    private IEnumerator RadianceCapture(string label) {
        RadianceVisualDump(label);
        if (Environment.GetEnvironmentVariable("HOLLOWCONNECT_RADIANCE_CAPTURE") != "1") yield break;
        yield return null;
        RadianceVisualDump(label + "-capture-frame");
        var path = Path.Combine(root, role + "-" + label + ".png");
        // Batchmode still uses the real D3D camera; explicitly render it because
        // Unity does not dispatch screen-end capture callbacks in batch mode.
        var camera = GameCameras.instance.cameraController.cam;
        var priorTarget = camera.targetTexture;
        var priorActive = RenderTexture.active;
        var target = new RenderTexture(800, 600, 24);
        var pixels = new Texture2D(800, 600, TextureFormat.RGB24, false);
        try {
            camera.targetTexture = target;
            camera.Render();
            RenderTexture.active = target;
            pixels.ReadPixels(new Rect(0, 0, 800, 600), 0, 0);
            pixels.Apply();
            File.WriteAllBytes(path, pixels.EncodeToPNG());
        } finally {
            camera.targetTexture = priorTarget;
            RenderTexture.active = priorActive;
            Object.Destroy(pixels);
            Object.Destroy(target);
        }
        Mark("RADIANCE CAPTURE " + path);
    }
    private Entity[] RadianceGroup(string group) => Entities.Values.Where(e => e.Type == EntityType.RadiancePlatform &&
        e.Object.Host.transform.parent?.name == group).ToArray();

    private void RadianceRequirePlatforms(Entity[] platforms, int expected, string label) {
        var solid = platforms.Count(e => (manager.IsSceneHost ? e.Object.Host : e.Object.Client)
            .GetComponentsInChildren<Collider2D>(true).Any(RadiancePlatform));
        var visible = platforms.Count(e => {
            var go = manager.IsSceneHost ? e.Object.Host : e.Object.Client;
            var sprite = go.GetComponent<tk2dBaseSprite>();
            return sprite != null && sprite.color.a > .9f && go.GetComponentsInChildren<Renderer>(true)
                .Any(r => r.enabled && r.gameObject.activeInHierarchy && r.bounds.size.sqrMagnitude > 1);
        });
        Mark("RADIANCE EXPECTED " + label + " expected=" + expected + " actual=" + platforms.Length + " solid=" + solid + " visible=" + visible);
        if (platforms.Length != expected || solid != expected || visible != expected)
            throw new Exception("Radiance " + label + " requires " + expected + " visible solid platforms after rendering");
    }
    private IEnumerator RadianceRenderSupports(Entity[] platforms, int expected, string label) {
        RadianceRequirePlatforms(platforms, expected, label);
        foreach (var entity in platforms.OrderBy(e => e.Object.Host.transform.position.y)) {
            var go = manager.IsSceneHost ? entity.Object.Host : entity.Object.Client;
            var support = go.GetComponentsInChildren<Collider2D>(true).Where(RadiancePlatform).OrderByDescending(c => c.bounds.max.y).First();
            var hero = HeroController.instance;
            var feet = hero.GetComponent<Collider2D>().bounds;
            hero.transform.position = new Vector3(support.bounds.center.x,
                support.bounds.max.y + hero.transform.position.y - feet.min.y + .1f, hero.transform.position.z);
            hero.GetComponent<Rigidbody2D>().velocity = Vector2.zero;
            hero.AffectedByGravity(true);
            Physics2D.SyncTransforms();
            yield return new WaitForSecondsRealtime(.4f);
            yield return RadianceWait(() => RadianceSupported() && RadiancePlayable(), label + " native support " + entity.Id, 5);
            yield return RadianceCapture(label + "-" + entity.Id);
            RadianceRequirePlatforms(platforms, expected, label);
            if (!RadianceSupported()) throw new Exception("Radiance platform disappeared beneath hero on rendering");
            Freeze();
        }
    }

    private IEnumerator RadianceEntryLayoutCase() {
        const string scene = "Dream_Final_Boss";
        bool InArena() => runtime.Playable && UnityEngine.SceneManagement.SceneManager.GetActiveScene().name == scene;
        var authority = host != (Environment.GetEnvironmentVariable("HOLLOWCONNECT_COVERAGE_AUTHORITY") == "guest");
        if (!authority) yield return Until(() => Has("radiance.entry.first"), "first player waits before challenge");
        HeroController.instance.takeNoDamage = true;
        HeroController.instance.AffectedByGravity(true);
        if (!authority && Environment.GetEnvironmentVariable("HOLLOWCONNECT_RADIANCE_FRESH_ENTRY") == "f8") {
            yield return Until(() => runtime.Peers.Values.Single().State?.IsReady == true &&
                runtime.Peers.Values.Single().State.Scene == scene &&
                (bool)typeof(CoopRuntime).GetProperty("CanStartWarp", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(runtime, null),
                "pre-challenge F8 both published ready in destination");
            runtime.Selected = runtime.Peers.Keys.Single();
            var handled = Field<uint>(runtime, "handledWarp");
            var pressed = false;
            runtime.KeyPressed = key => key == CoopMod.Settings.TeleportKey && !pressed && (pressed = true);
            yield return Until(() => pressed && Field<uint>(runtime, "handledWarp") > handled, "pre-challenge F8 granted");
            runtime.KeyPressed = _ => false;
        } else Enter(scene, "door1");
        yield return Until(() => InArena() && manager.IsSceneHostDetermined && engine.ClientManager.SceneSnapshotReady,
            "entry scene and snapshot ready");
        if (manager.IsSceneHost != authority) throw new Exception("Unexpected entry authority");
        yield return RadianceWait(RadiancePlayable, "entry controllable before challenge", 20);
        Freeze();
        if (authority) Flag("radiance.entry.first");
        yield return RadianceTogether(0, "before challenge");
        yield return new WaitForSecondsRealtime(2);
        yield return RadianceCapture("before-challenge");
        var initial = RadianceGroup("Init");
        yield return RadianceRenderSupports(initial, 3, "before-challenge");
        yield return RadianceTogether(0, "entry layout accepted");
        if (authority) yield return RadianceStoryChallenge();
        yield return RadianceWait(() => RadianceVisible().activeInHierarchy && RadiancePlayable() &&
            RadianceBossEntity().Object.Host.transform.parent.GetComponent<PlayMakerFSM>().ActiveStateName == "Battle Start",
            "shared native challenge starts and each local intro completes", 45);
        Mark("PASS radiance pre-challenge entry geometry");
    }
}
