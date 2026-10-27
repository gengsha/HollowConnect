using System;
using System.Collections;
using System.Linq;
using System.Reflection;
using Hkmp.Game.Client.Entity;
using HollowConnect;
using HollowConnect.Core;
using UnityEngine;
using UnityEngine.UI;
using Object = UnityEngine.Object;

namespace HollowConnect.Smoke;

public sealed partial class RescueProbe {
    private void CheckDamageRankingShortcut() {
        var enabled = runtime.damageRankingEnabled; var board = runtime.damageRanking;
        var hidden = runtime.damageRankingHidden; var keys = runtime.KeyPressed;
        var tick = typeof(CoopRuntime).GetMethod("TickDamageRanking", BindingFlags.Instance | BindingFlags.NonPublic);
        try {
            runtime.damageRankingEnabled = true; runtime.damageRankingHidden = false;
            runtime.damageRanking = new DamageRankingSnapshot { Board = 1, Players = new[] {
                new DamageStanding { Id = runtime.LocalId, Name = "shortcut fixture", Damage = 17 }
            }};
            runtime.KeyPressed = _ => false; tick.Invoke(runtime, null);
            var canvas = Object.FindObjectsOfType<Canvas>().Single(c => c.name == "HollowConnect Damage Ranking");
            Check(canvas.GetComponentsInChildren<Text>().Any(t => t.name == "Shortcut" && t.text == "F1"), "damage board displays F1 shortcut");
            runtime.KeyPressed = key => key == KeyCode.F1; tick.Invoke(runtime, null);
            Check(runtime.damageRankingHidden && !canvas.gameObject.activeSelf, "F1 alone hides damage board");
            tick.Invoke(runtime, null);
            Check(!runtime.damageRankingHidden && canvas.gameObject.activeSelf && runtime.damageRanking.Total == 17,
                "F1 alone restores board without changing statistics");
        } finally {
            runtime.KeyPressed = keys; runtime.damageRankingEnabled = enabled;
            runtime.damageRanking = board; runtime.damageRankingHidden = hidden; tick.Invoke(runtime, null);
        }
    }

    private IEnumerator NativeTerrainRescueRiskChecks() {
        const string scene = "Mines_17";
        if (!host) yield return Until(() => Has("risk.first"), "authority enters native hazard room");
        yield return RuleEnter(scene, "left1");
        if (host) Flag("risk.first");
        yield return StoryTogether("risk.entered");
        yield return Until(() => runtime.Peers.Count == 1 && runtime.Peers.Values.All(p => p.Life == HollowConnect.Core.Life.Alive &&
            p.State?.Scene == scene && CoopRuntime.Now - p.Seen < 2), "native hazard room has fresh living witness before geometry checks");
        var hero = HeroController.instance; var entry = hero.transform.position;
        var bounds = hero.GetComponent<Collider2D>().bounds;
        var size = (Vector2)bounds.size; var offset = (Vector2)(bounds.center - hero.transform.position);
        hero.AffectedByGravity(false); hero.GetComponent<Rigidbody2D>().velocity = Vector2.zero;
        var spikes = Object.FindObjectsOfType<DamageHero>().Where(d => d.enabled && d.hazardType == 1 &&
            d.gameObject.scene.name == scene && d.name.StartsWith("Spikes", StringComparison.Ordinal))
            .SelectMany(d => d.GetComponents<Collider2D>()).Where(c => c.enabled).ToArray();
        Check(spikes.Length > 0, "native type-1 terrain spikes present");
        var landing = typeof(CoopRuntime).GetMethod("TryStoryLanding", BindingFlags.Instance | BindingFlags.NonPublic);
        var damage = typeof(CoopRuntime).GetMethod("StoryDamage", BindingFlags.Static | BindingFlags.NonPublic);
        var occupied = typeof(CoopRuntime).GetMethod("StoryOccupied", BindingFlags.Instance | BindingFlags.NonPublic);
        bool TryLanding(Vector2 seed, out Vector2 result) {
            hero.transform.position = seed; Physics2D.SyncTransforms();
            var args = new object[] { hero, seed, 3f, false, null, null };
            var found = (bool)landing.Invoke(runtime, args); result = (Vector2)args[4]; return found;
        }
        bool Unsafe(Vector2 point) => Physics2D.OverlapBoxAll(point + offset, size * .94f, 0).Any(c => spikes.Contains(c));
        bool Occupied(Vector2 point) {
            hero.transform.position = point; Physics2D.SyncTransforms();
            return (bool)occupied.Invoke(runtime, new object[] { hero, point });
        }
        Vector2? unsafeSeed = null; Vector2 oldLanding = default;
        Vector2? clearedHazard = null;
        // Reproduce the previous broad type-1 exemption against actual native
        // terrain, without enabling a hazard or changing its FSM/geometry.
        using (new MonoMod.RuntimeDetour.Hook(damage,
            new Func<Func<Collider2D, bool>, Collider2D, bool>((orig, c) =>
                c.GetComponentInParent<DamageHero>() is { hazardType: 1 } ? false : orig(c)))) {
            foreach (var spike in spikes) {
                Log("NATIVE SPIKES " + spike.name + " owner=" + EntityReferences.Owner(spike.gameObject)?.Type + " bounds=" + spike.bounds);
                var b = spike.bounds;
                // Clearance matters even on a currently unsupported approach:
                // the same body-volume query validates trail and peer landings.
                for (var x = b.min.x; x <= b.max.x && !clearedHazard.HasValue; x += .25f)
                    for (var y = b.min.y - size.y; y <= b.max.y + size.y && !clearedHazard.HasValue; y += .25f) {
                        var point = new Vector2(x, y);
                        if (Unsafe(point) && !Occupied(point)) clearedHazard = point;
                    }
                for (var x = b.min.x; x <= b.max.x && !unsafeSeed.HasValue; x += .5f)
                    for (var y = b.min.y; y <= b.max.y + 2 && !unsafeSeed.HasValue; y += .5f) {
                        var seed = new Vector2(x, y);
                        if (TryLanding(seed, out var candidate) && Unsafe(candidate)) { unsafeSeed = seed; oldLanding = candidate; }
                    }
                if (unsafeSeed.HasValue) break;
            }
        }
        Check(clearedHazard.HasValue, "previous broad policy clears a body volume intersecting native persistent spikes");
        Check(Occupied(clearedHazard.Value), "corrected rescue clearance rejects that native persistent-spike volume");
        Log("NATIVE HAZARD CLEARANCE BASELINE " + clearedHazard.Value);
        if (unsafeSeed.HasValue) {
            Log("NATIVE UNSAFE BASELINE seed=" + unsafeSeed.Value + " landing=" + oldLanding);
            var accepted = TryLanding(unsafeSeed.Value, out var safe);
            Check(!accepted || !Unsafe(safe), "corrected policy rejects or relocates native persistent-spike landing");
        } else Log("NATIVE SPIKE BOUNDARY: no supported unsafe landing in this room's sampled geometry; clearance regression reproduced directly");
        hero.transform.position = entry; Physics2D.SyncTransforms();
        CheckAerialRecoveryGeometry(entry + Vector3.up * 100);
        yield return StoryTogether("risk.checked");
    }

    private void CheckAerialRecoveryGeometry(Vector3 point) {
        var hero = HeroController.instance; var pd = PlayerData.instance;
        var hadClaw = pd.GetBool("hasWalljump");
        var air = typeof(CoopRuntime).GetMethod("StoryAirSpace", BindingFlags.Instance | BindingFlags.NonPublic);
        bool Safe() { Physics2D.SyncTransforms(); return (bool)air.Invoke(runtime, new object[] { hero, (Vector2)point }); }
        GameObject Solid(string name, Vector3 at, Vector2 size) {
            var go = new GameObject(name) { layer = 8 }; go.transform.position = at;
            go.AddComponent<BoxCollider2D>().size = size; return go;
        }
        var wall = Solid("Aerial audit injected recovery wall", point + Vector3.right * 2, new Vector2(.5f, 16));
        var floor = Solid("Aerial audit injected recovery floor", point + Vector3.down * 8, new Vector2(10, .5f));
        var bounds = hero.GetComponent<Collider2D>().bounds;
        var height = bounds.extents.y - (bounds.center - hero.transform.position).y + .07f;
        var narrow = Solid("Aerial audit injected narrow perch", point + Vector3.down * (height + .25f), new Vector2(.6f, .5f));
        var hazard = new GameObject("Aerial audit injected persistent hazard");
        hazard.transform.position = point + Vector3.down * 2;
        hazard.AddComponent<BoxCollider2D>().size = new Vector2(1, 1);
        hazard.GetComponent<Collider2D>().isTrigger = true;
        var damage = hazard.AddComponent<DamageHero>(); damage.damageDealt = 1;
        hazard.SetActive(false); wall.SetActive(false); floor.SetActive(false); narrow.SetActive(false);
        try {
            pd.SetBool("hasWalljump", true);
            Check(!Safe(), "unsupported empty space alone is not a safe aerial revival");
            wall.SetActive(true);
            Check(Safe(), "clear aerial nail space and reachable climbable wall permit recovery");
            var nonSlider = wall.AddComponent<NonSlider>();
            Check(!Safe(), "native NonSlider wall cannot justify aerial revival");
            Object.DestroyImmediate(nonSlider);
            wall.layer = 0;
            Check(!Safe(), "solid walls outside native terrain layer cannot justify wall-slide recovery");
            wall.layer = 8;
            pd.SetBool("hasWalljump", false);
            Check(!Safe(), "a wall without the actual claw ability cannot justify aerial revival");
            pd.SetBool("hasWalljump", true);
            foreach (var type in new[] { 1, 2, 3, 5 }) {
                damage.hazardType = type; hazard.SetActive(true);
                Check(!Safe(), "persistent hazard blocks the aerial recovery body and fall clearance type=" + type);
                hazard.SetActive(false);
            }
            wall.SetActive(false); floor.SetActive(true);
            Check(Safe(), "safe nearby floor also supplies an aerial recovery route");
            floor.SetActive(false);
            Check(!Safe(), "vanished support cannot leave an unsupported aerial revival behind");
            // Geometry-only fault injection: give the solver an observed peer
            // beside a tiny perch. No presence packet or rescue is generated.
            var peer = runtime.Peers.Values.Single(); var state = peer.State;
            var px = state.X; var py = state.Y; var ps = state.Scene; var seen = peer.Seen;
            var oldDeathScene = Field<string>(runtime, "deathScene");
            try {
                state.X = point.x - 2; state.Y = point.y; state.Scene = GameManager.instance.sceneName; peer.Seen = CoopRuntime.Now;
                typeof(CoopRuntime).GetField("deathScene", BindingFlags.Instance | BindingFlags.NonPublic).SetValue(runtime, state.Scene);
                narrow.SetActive(true); Physics2D.SyncTransforms();
                var groundStrike = (bool)typeof(CoopRuntime).GetMethod("StoryStrikeSpace", BindingFlags.Instance | BindingFlags.NonPublic)
                    .Invoke(runtime, new object[] { hero, (Vector2)point });
                Check(!groundStrike, "tiny native-sized perch has no adjacent grounded striking stance");
                var args = new object[] { hero, (Vector2)point, .25f, true, null, null };
                var accepted = (bool)typeof(CoopRuntime).GetMethod("TryStoryLanding", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(runtime, args);
                Log("NARROW GEOMETRY witness=" + peer.Life + "/" + state.Scene + " age=" + (CoopRuntime.Now - peer.Seen) +
                    " accepted=" + accepted + " point=" + args[4] + " support=" + args[5]);
                Check(accepted, "supported narrow perch accepts a clear witnessed aerial nail approach");
                wall.SetActive(true); wall.transform.position = point + Vector3.left;
                wall.GetComponent<BoxCollider2D>().size = new Vector2(.3f, 16); Physics2D.SyncTransforms();
                args = new object[] { hero, (Vector2)point, .25f, true, null, null };
                Check(!(bool)typeof(CoopRuntime).GetMethod("TryStoryLanding", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(runtime, args),
                    "a sealed solid wall blocks that aerial approach to the narrow perch");
            } finally {
                state.X = px; state.Y = py; state.Scene = ps; peer.Seen = seen;
                typeof(CoopRuntime).GetField("deathScene", BindingFlags.Instance | BindingFlags.NonPublic).SetValue(runtime, oldDeathScene);
            }
        } finally {
            pd.SetBool("hasWalljump", hadClaw);
            Object.Destroy(wall); Object.Destroy(floor); Object.Destroy(hazard); Object.Destroy(narrow);
        }
    }
}
