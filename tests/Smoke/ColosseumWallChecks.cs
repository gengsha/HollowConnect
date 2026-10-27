using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Hkmp.Game.Client.Entity;
using HutongGames.PlayMaker;
using Newtonsoft.Json;
using UnityEngine;
using Object = UnityEngine.Object;

namespace HollowConnect.Smoke;

public sealed partial class ColosseumProbe {
    private bool WallCase => testCase == "walls" || testCase == "walls-entry" || testCase == "walls-late" || testCase == "air-rescue";
    private int wallWave = 1;
    private readonly HashSet<int> wallApproached = new();
    private static string WallPath(Transform t) {
        var parts = new Stack<string>();
        for (; t != null; t = t.parent) parts.Push(t.name);
        return string.Join("/", parts);
    }
    private static bool IsSideWall(Component c) => WallPath(c.transform).Contains("Colosseum Wall L") ||
        WallPath(c.transform).Contains("Colosseum Wall R");
    private IEnumerable<Entity> SideWallEntities() => Field<Dictionary<ulong, Entity>>(ArenaManager, "_entities").Values
        .Where(e => e.Type == EntityType.ColosseumWall && e.Object.Host != null &&
            (e.Object.Host.name == "Colosseum Wall L" || e.Object.Host.name == "Colosseum Wall R"));
    private Transform[] LocalSideWalls() {
        var registered = SideWallEntities().Select(e => ArenaManager.IsSceneHost ? e.Object.Host : e.Object.Client)
            .Where(o => o != null).Select(o => o.transform).ToArray();
        return registered.Length != 0 ? registered : LocalArena.GetComponentsInChildren<Transform>(true)
            .Where(t => t.name == "Colosseum Wall L" || t.name == "Colosseum Wall R").ToArray();
    }
    private bool WallRenderer(Renderer r) => r != null && r.enabled && !r.forceRenderingOff && r.gameObject.activeInHierarchy;
    private bool WallCollider(Collider2D c) => c != null && c.enabled && !c.isTrigger && c.gameObject.activeInHierarchy;

    private void SaveWallActors(string state) {
        if (!ArenaManager.IsSceneHost) return;
        var actors = Object.FindObjectsOfType<HealthManager>(true).Where(h => h.gameObject.scene.name == ActiveScene &&
            !EntityReferences.IsReplica(h.gameObject)).Select(h => new {
                path = WallPath(h.transform), active = h.gameObject.activeInHierarchy, h.hp, h.isDead,
                invincible = h.IsInvincible, registered = EntityReferences.Owner(h.gameObject) != null,
                x = h.transform.position.x, y = h.transform.position.y,
                colliders = h.GetComponents<Collider2D>().Select(c => new { c.enabled, c.isTrigger }).ToArray(),
                fsms = h.GetComponents<PlayMakerFSM>().Select(f => new { name = f.FsmName, state = f.ActiveStateName }).ToArray()
            }).ToArray();
        var counters = ArenaEntity.Object.Host.GetComponents<PlayMakerFSM>().Single(f => f.FsmName == "Battle Control")
            .FsmVariables.IntVariables.Select(v => new { v.Name, v.Value }).ToArray();
        var output = new { state, wallWave, counters, actors };
        File.WriteAllText(Path.Combine(root, role + "-wall-actors-" + state.Replace(' ', '-').Replace('/', '-') + ".json"),
            JsonConvert.SerializeObject(output, Formatting.Indented));
        File.WriteAllText(Path.Combine(root, role + "-wall-progress.json"), JsonConvert.SerializeObject(new {
            state, wallWave, utc = DateTime.UtcNow, liveActive = actors.Count(a => a.active && !a.isDead && a.hp > 0)
        }, Formatting.Indented));
    }

    private static int? ActorWave(Transform t) {
        for (; t != null; t = t.parent) {
            if (!t.name.StartsWith("Wave ", StringComparison.Ordinal)) continue;
            var number = new string(t.name.Substring(5).TakeWhile(char.IsDigit).ToArray());
            if (int.TryParse(number, out var value)) return value;
        }
        return null;
    }

    private void DamageCurrentWallWave() {
        if (!ArenaManager.IsSceneHost) return;
        foreach (var entity in Field<Dictionary<ulong, Entity>>(ArenaManager, "_entities").Values.ToArray()) {
            var go = entity.Object.Host;
            var hm = go == null || !go.activeInHierarchy ? null : go.GetComponent<HealthManager>();
            if (hm == null || hm.gameObject.scene.name != ActiveScene || hm.isDead || hm.hp <= 0 || hm.IsInvincible ||
                !hm.GetComponents<Collider2D>().Any(c => c.enabled) || ActorWave(hm.transform) is int future && future > wallWave) continue;
            Log("WALL NATIVE DAMAGE wave=" + wallWave + " path=" + WallPath(hm.transform) + " hp=" + hm.hp +
                " states=" + string.Join(",", hm.GetComponents<PlayMakerFSM>().Select(f => f.FsmName + ":" + f.ActiveStateName)));
            hm.ApplyExtraDamage(hm.hp);
        }
    }

    private void ApproachCurrentWallWave() {
        if (!ArenaManager.IsSceneHost) return;
        // Gold's Wave 23 blobs emerge only when the native hero approaches.
        // Keep that original detection path; never wake them with a fixture
        // event/state or damage through their disabled hiding colliders.
        var waiting = Object.FindObjectsOfType<HealthManager>().FirstOrDefault(h => h.gameObject.scene.name == ActiveScene &&
            !EntityReferences.IsReplica(h.gameObject) && !h.isDead && h.hp > 0 && ActorWave(h.transform) == wallWave &&
            h.GetComponents<PlayMakerFSM>().Any(f => f.FsmName == "Blob" && f.ActiveStateName == "Hide"));
        var hero = HeroController.instance;
        if (waiting == null) {
            // Check Saver waits until the native hero leaves the emergence
            // area. The final hidden blob has no next blob to move us away.
            var spawning = Object.FindObjectsOfType<HealthManager>().Any(h => h.gameObject.scene.name == ActiveScene &&
                !EntityReferences.IsReplica(h.gameObject) && !h.isDead && h.hp > 0 && ActorWave(h.transform) == wallWave &&
                h.GetComponents<PlayMakerFSM>().Any(f => f.FsmName == "Blob" && f.ActiveStateName == "Check Saver"));
            if (!spawning) return;
            hero.transform.position = new Vector3(106, 14, 0);
            hero.GetComponent<Rigidbody2D>().velocity = Vector2.zero; Physics2D.SyncTransforms();
            return;
        }
        hero.transform.position = waiting.transform.position + Vector3.up;
        hero.GetComponent<Rigidbody2D>().velocity = Vector2.zero; Physics2D.SyncTransforms();
        if (wallApproached.Add(waiting.GetInstanceID()))
            Log("WALL NATIVE APPROACH " + WallPath(waiting.transform) + " hero=" + hero.transform.position);
    }

    private void SaveWallObservation(string phase) {
        Physics2D.SyncTransforms();
        var roots = new[] { ArenaEntity.Object.Host, ArenaEntity.Object.Client }
            .Concat(SideWallEntities().SelectMany(e => new[] { e.Object.Host, e.Object.Client }));
        var walls = roots.Where(o => o != null).SelectMany(o => o.GetComponentsInChildren<Transform>(true))
            .Where(t => t.name.Replace("(Clone)", "") == "Colosseum Wall L" || t.name.Replace("(Clone)", "") == "Colosseum Wall R")
            .Distinct().Select(t => new {
                path = WallPath(t), active = t.gameObject.activeInHierarchy, x = t.position.x, y = t.position.y,
                fsm = t.GetComponent<PlayMakerFSM>()?.ActiveStateName,
                distance = t.GetComponent<PlayMakerFSM>()?.FsmVariables.FindFsmFloat("Distance")?.Value,
                renderers = t.GetComponentsInChildren<Renderer>(true).Select(r => new {
                    path = WallPath(r.transform), visible = WallRenderer(r), x = r.bounds.center.x,
                    y = r.bounds.center.y, width = r.bounds.size.x, height = r.bounds.size.y
                }).ToArray(),
                colliders = t.GetComponentsInChildren<Collider2D>(true).Select(c => new {
                    path = WallPath(c.transform), solid = WallCollider(c), x = c.bounds.center.x,
                    y = c.bounds.center.y, width = c.bounds.size.x, height = c.bounds.size.y
                }).ToArray()
            }).ToArray();
        var local = LocalSideWalls();
        var renderCount = local.Sum(t => t.GetComponentsInChildren<Renderer>(true).Count(WallRenderer));
        var solidCount = local.Sum(t => t.GetComponentsInChildren<Collider2D>(true).Count(WallCollider));
        var observation = new {
            role, phase, authority = ArenaManager.IsSceneHost, state = ArenaState("Battle Control"),
            localRoot = LocalArena.name, localRootActive = LocalArena.activeInHierarchy,
            localSideWalls = local.Length, renderCount, solidCount, walls
        };
        File.WriteAllText(Path.Combine(root, role + "-walls-" + phase + ".json"), JsonConvert.SerializeObject(observation, Formatting.Indented));
        Log("WALL OBSERVATION phase=" + phase + " authority=" + ArenaManager.IsSceneHost + " wave=" + observation.state +
            " localRoot=" + LocalArena.name + " sideWalls=" + local.Length + " activeRenderers=" + renderCount + " solidColliders=" + solidCount);
    }

    private void CaptureWalls(string phase) {
        var camera = GameCameras.instance.cameraController.cam;
        var priorTarget = camera.targetTexture; var priorActive = RenderTexture.active;
        var target = new RenderTexture(800, 600, 24);
        var pixels = new Texture2D(800, 600, TextureFormat.RGB24, false);
        try {
            camera.targetTexture = target; camera.Render(); RenderTexture.active = target;
            pixels.ReadPixels(new Rect(0, 0, 800, 600), 0, 0); pixels.Apply();
            File.WriteAllBytes(Path.Combine(root, role + "-walls-" + phase + ".png"), pixels.EncodeToPNG());
        } finally {
            camera.targetTexture = priorTarget; RenderTexture.active = priorActive;
            Object.Destroy(pixels); Object.Destroy(target);
        }
    }

    private IEnumerator ObserveColosseumWalls() {
        Require(trial == "Gold", "wall reproduction uses the user's third trial");
        var peer = host ? "guest" : "host";
        var battle = ArenaEntity.Object.Host.GetComponents<PlayMakerFSM>().Single(f => f.FsmName == "Battle Control");
        var hero = HeroController.instance;
        hero.AffectedByGravity(false); hero.GetComponent<Rigidbody2D>().velocity = Vector2.zero;
        hero.transform.position = new Vector3(108 + (host ? -2 : 2), 14, 0);
        Require(runtime.Peers.Count == 1 && engine.ClientManager.SceneSnapshotReady,
            "wall fixture has both real UDP endpoints and native Gold scene ownership");
        LocalBarrierChecks.Run(Log);
        yield return LocalBarrierEventChecks.Run(Require);
        var originalWalls = ArenaEntity.Object.Host.transform.Find("Walls");
        Require(originalWalls != null && originalWalls.Find("Colosseum Wall L") != null && originalWalls.Find("Colosseum Wall R") != null,
            "native Gold scene contains both original wall controllers");
        SaveWallObservation("initial");
        SaveWallActors("initial");
        if (ArenaManager.IsSceneHost) {
            var blob = ArenaEntity.Object.Host.GetComponentsInChildren<PlayMakerFSM>(true).FirstOrDefault(f => f.FsmName == "Blob");
            if (blob != null) {
                var schema = new List<string>();
                foreach (var state in blob.FsmStates) {
                    schema.Add("STATE " + state.Name + " transitions=" + string.Join(",", state.Transitions.Select(t => t.EventName + ":" + t.ToState)));
                    foreach (var action in state.Actions) schema.Add(" ACTION " + action.GetType().Name + " " +
                        string.Join(";", action.GetType().GetFields(System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public)
                            .Select(f => f.Name + "=" + Describe(f.GetValue(action)))));
                }
                File.WriteAllLines(Path.Combine(root, role + "-native-blob-schema.txt"), schema);
            }
        }
        Flag(role + ".wall-initial");
        if (testCase != "walls-late")
            yield return Until(() => Has(peer + ".wall-initial"), "both initial wall observations recorded");
        if (testCase != "walls-entry") {
        if (ArenaManager.IsSceneHost) battle.Fsm.ManualUpdate = false;
        var deadline = Time.realtimeSinceStartup + 360;
        var prior = "";
        var stateSince = Time.realtimeSinceStartup;
        while (ArenaState("Battle Control") != "Wave 30") {
            var state = ArenaState("Battle Control");
            if (prior != state) {
                prior = state; stateSince = Time.realtimeSinceStartup;
                if (state.StartsWith("Wave ") && int.TryParse(state.Substring(5), out var number)) wallWave = number;
                Log("WALL NATIVE PROGRESS " + state); SaveWallActors(state);
            }
            if (Time.realtimeSinceStartup - stateSince > 25) {
                SaveWallActors("stalled-" + state);
                throw new TimeoutException("Native wall fixture stalled in " + state + "; actor/counter diagnostics saved");
            }
            RequireWallDeadline(deadline, "first original lateral contraction at Wave 30");
            ApproachCurrentWallWave();
            DamageCurrentWallWave();
            yield return new WaitForSecondsRealtime(.15f);
        }
        // Keep Wave 30's original enemies alive. Its MOVE events and native
        // wall tweens run normally, with no injected wall states or events.
        Log("WALL NATIVE PROGRESS Wave 30; stop killing enemies while original delayed MOVE events execute");
        }
        if (testCase == "walls-late" && ArenaManager.IsSceneHost) {
            yield return new WaitForSecondsRealtime(5);
            Flag(role + ".walls-late-ready");
            yield return Until(() => Has(peer + ".wall-initial"), "late entrant snapshot at native Wave30", 60);
        }
        hero.transform.position = new Vector3(106, 10, 0);
        hero.GetComponent<Rigidbody2D>().velocity = Vector2.zero; Physics2D.SyncTransforms();
        yield return new WaitForSecondsRealtime(5);
        var phase = testCase == "walls-entry" ? "entry" : "contracted";
        Require(ArenaState("Battle Control") == (testCase == "walls-entry" ? "Wave 1" : "Wave 30"),
            "wall observation remains at its actual native enemy wave");
        SaveWallObservation(phase); CaptureWalls(phase);
        if (ArenaManager.IsSceneHost) {
            var local = LocalSideWalls();
            Require(local.Length == 2 && local.All(t => t.GetComponentsInChildren<Renderer>(true).Any(WallRenderer)) &&
                local.All(t => t.GetComponentsInChildren<Collider2D>(true).Any(WallCollider)),
                "original authoritative left and right walls are visible and solid at the observed native boundary");
            var targets = local.Select(t => t.GetComponentsInChildren<Collider2D>(true).First(WallCollider).bounds)
                .Select(b => new[] { b.center.x, 14f }).ToArray();
            File.WriteAllText(Path.Combine(root, "wall-ray-targets.json"), JsonConvert.SerializeObject(targets));
        }
        Flag(role + ".wall-contracted");
        yield return Until(() => Has(peer + ".wall-contracted") && Has("wall-ray-targets.json"), "both wall contraction observations recorded");
        var points = JsonConvert.DeserializeObject<float[][]>(File.ReadAllText(Path.Combine(root, "wall-ray-targets.json")));
        var rays = points.Select(p => {
            var from = new Vector2(p[0] - 3, p[1]);
            var hits = Physics2D.RaycastAll(from, Vector2.right, 6).Where(h => h.collider != null && !h.collider.isTrigger && IsSideWall(h.collider)).ToArray();
            return new { x = p[0], y = p[1], solidWallHits = hits.Length, hits = hits.Select(h => WallPath(h.collider.transform)).ToArray() };
        }).ToArray();
        File.WriteAllText(Path.Combine(root, role + "-wall-rays.json"), JsonConvert.SerializeObject(rays, Formatting.Indented));
        foreach (var ray in rays) Log("WALL PHYSICS x=" + ray.x + " y=" + ray.y + " nativeSolidHits=" + ray.solidWallHits);
        Flag(role + ".wall-observed");
        yield return Until(() => Has(peer + ".wall-observed"), "both rendered wall and physics observations complete");
        Log("OBSERVATION COMPLETE third-trial wall reproduction; outcome is determined by comparing both endpoints, not by this completion marker");
    }

    private void RequireWallDeadline(float deadline, string label) {
        if (Time.realtimeSinceStartup < deadline) return;
        SaveWallObservation("timeout");
        throw new TimeoutException("Gold native progression did not reach " + label + "; state=" + ArenaState("Battle Control"));
    }
}
