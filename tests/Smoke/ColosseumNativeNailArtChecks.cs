using System;
using System.Collections;
using System.IO;
using System.Linq;
using Hkmp.Game.Client.Entity;
using MonoMod.RuntimeDetour;
using Newtonsoft.Json;
using UnityEngine;
using Object = UnityEngine.Object;

namespace HollowConnect.Smoke;

public sealed partial class ColosseumProbe {
    // Exercise the original Nail Arts FSM through input, rather than playing
    // its final sprite clip. This catches extra publications during release.
    private IEnumerator ColosseumNativeNailArtChecks() {
        var hero = HeroController.instance;
        var metrics = GetComponent<ColosseumNetworkMetrics>();
        var arts = hero.gameObject.LocateMyFSM("Nail Arts");
        File.WriteAllText(Path.Combine(root, role + ".nail-arts-fsm.json"), JsonConvert.SerializeObject(new {
            states = arts.FsmStates.Select(s => new { s.Name, transitions = s.Transitions.Select(t => new { t.EventName, t.ToState }),
                actions = s.Actions.Select(a => new { type = a.GetType().Name, fields = a.GetType().GetFields().ToDictionary(f => f.Name, f => Describe(f.GetValue(a))) }) }),
            variables = arts.FsmVariables.GetAllNamedVariables().Select(v => new { v.Name, value = v.ToString() })
        }, Formatting.Indented));
        HealthManager target = null;
        if (ArenaManager.IsSceneHost) {
            yield return Until(() => Object.FindObjectsOfType<HealthManager>().Any(h => h.name == "Colosseum_Miner(Clone)" && h.gameObject.activeInHierarchy), "native nail-art target available", 20);
            target = Object.FindObjectsOfType<HealthManager>().First(h => h.name == "Colosseum_Miner(Clone)" && h.gameObject.activeInHierarchy);
            target.hp = 10000;
            foreach (var fsm in target.GetComponents<PlayMakerFSM>()) fsm.Fsm.ManualUpdate = true;
            var body = target.GetComponent<Rigidbody2D>();
            body.bodyType = RigidbodyType2D.Dynamic; body.constraints = RigidbodyConstraints2D.FreezeAll;
            body.sleepMode = RigidbodySleepMode2D.NeverSleep; body.gravityScale = 0; body.velocity = Vector2.zero;
            target.transform.position = new Vector3(106, 35, 0);
            if (Environment.GetEnvironmentVariable("HOLLOWCONNECT_NATIVE_ART_MULTICOLLIDER") == "1") {
                foreach (var x in new[] { -.5f, .5f }) {
                    var child = new GameObject("Native art shared HealthManager collider " + x) { layer = target.gameObject.layer };
                    child.transform.SetParent(target.transform, false);
                    child.transform.localPosition = new Vector3(x, 0, 0);
                    child.AddComponent<BoxCollider2D>().size = new Vector2(2, 2);
                }
                Require(target.GetComponentsInChildren<Collider2D>().Length >= 3, "three native contact objects share one HealthManager");
            }
            File.WriteAllText(Path.Combine(root, "native-arts-target"), EntityReferences.Owner(target.gameObject).Id.ToString());
        }
        yield return Until(() => Has("native-arts-target"), "native art target prepared");
        var targetId = ulong.Parse(File.ReadAllText(Path.Combine(root, "native-arts-target")));
        metrics.CombatTargetId = targetId;
        yield return Until(() => EntityReferences.Entities.TryGetValue(targetId, out var e) &&
            (ArenaManager.IsSceneHost ? e.Object.Host : e.Object.Client) != null, "native art target body registered");
        var visibleTarget = ArenaManager.IsSceneHost ? target : EntityReferences.Entities[targetId].Object.Client.GetComponent<HealthManager>();
        // Both copies need the same native alternate-hit configuration. It
        // omits HealthManager's usual 0.2-second evasion, as on enemies using
        // a dedicated hit animation; duplicate contacts must still be limited.
        if (Environment.GetEnvironmentVariable("HOLLOWCONNECT_NATIVE_ART_ALTERNATE_HIT") == "1") {
            Modding.ReflectionHelper.SetField(visibleTarget, "hasAlternateHitAnimation", true);
            Modding.ReflectionHelper.SetField(visibleTarget, "alternateHitAnimation", visibleTarget.GetComponent<tk2dSpriteAnimator>().CurrentClip.name);
        }
        File.WriteAllText(Path.Combine(root, role + ".native-art-target.json"), JsonConvert.SerializeObject(new {
            components = visibleTarget.GetComponents<UnityEngine.Component>().Select(c => c.GetType().Name),
            alternateHitAnimation = Modding.ReflectionHelper.GetField<HealthManager, bool>(visibleTarget, "hasAlternateHitAnimation"),
            fsms = visibleTarget.GetComponents<PlayMakerFSM>().Select(f => new { f.FsmName, f.ActiveStateName,
                states = f.FsmStates.Select(s => new { s.Name, actions = s.Actions.Select(a => a.GetType().Name) }) })
        }, Formatting.Indented));
        var pd = PlayerData.instance;
        pd.nailDamage = 21;
        foreach (var name in new[] { "canDash", "hasDash", "hasNailArt", "hasCyclone", "hasDashSlash", "hasUpwardSlash", "hasAllNailArts" }) pd.SetBool(name, true);
        hero.RegainControl(); hero.StartAnimationControl();
        hero.AffectedByGravity(false); hero.GetComponent<Rigidbody2D>().velocity = Vector2.zero;
        hero.transform.position = new Vector3(ArenaManager.IsSceneHost ? 100 : 104, 20, 0);
        var scale = hero.transform.localScale; scale.x = -Math.Abs(scale.x); hero.transform.localScale = scale;
        Physics2D.SyncTransforms();
        var input = InputHandler.Instance.inputActions;
        var players = Field<Hkmp.Game.Client.PlayerManager>(engine.ClientManager, "_playerManager");
        var peerId = runtime.Peers.Single().Key;
        var held = false; var pressFrame = -1; var releaseFrame = -1;
        var dashHeld = false; var cyclone = false;
        using var pressed = new Hook(typeof(InControl.OneAxisInputControl).GetProperty("IsPressed").GetGetMethod(),
            new Func<Func<InControl.OneAxisInputControl, bool>, InControl.OneAxisInputControl, bool>((orig, self) =>
                ReferenceEquals(self, input.attack) ? held : ReferenceEquals(self, input.dash) ? dashHeld :
                ReferenceEquals(self, input.up) ? cyclone : orig(self)));
        using var down = new Hook(typeof(InControl.OneAxisInputControl).GetProperty("WasPressed").GetGetMethod(),
            new Func<Func<InControl.OneAxisInputControl, bool>, InControl.OneAxisInputControl, bool>((orig, self) =>
                ReferenceEquals(self, input.attack) ? Time.frameCount == pressFrame : ReferenceEquals(self, input.dash) ? dashHeld : orig(self)));
        using var move = new Hook(typeof(InControl.TwoAxisInputControl).GetProperty("Vector").GetGetMethod(),
            new Func<Func<InControl.TwoAxisInputControl, Vector2>, InControl.TwoAxisInputControl, Vector2>((orig, self) =>
                ReferenceEquals(self, input.moveVector) && cyclone ? Vector2.up : orig(self)));
        using var up = new Hook(typeof(InControl.OneAxisInputControl).GetProperty("WasReleased").GetGetMethod(),
            new Func<Func<InControl.OneAxisInputControl, bool>, InControl.OneAxisInputControl, bool>((orig, self) => ReferenceEquals(self, input.attack) ? Time.frameCount == releaseFrame : orig(self)));
        yield return new WaitForSecondsRealtime(1);
        var full = Environment.GetEnvironmentVariable("HOLLOWCONNECT_NATIVE_ART_FULL") == "1";
        foreach (var fps in full ? new[] { 60, 20 } : new[] { 60 })
        foreach (var art in full ? (Environment.GetEnvironmentVariable("HOLLOWCONNECT_NATIVE_ART_SINGLE_SLASHES") == "1"
                     ? new[] { "great", "dash" } : new[] { "great", "dash", "cyclone" }) : new[] { "great" })
        foreach (var reference in full ? new[] { true, false } : new[] { false }) {
            Application.targetFrameRate = fps; QualitySettings.vSyncCount = 0;
            var actor = ArenaManager.IsSceneHost == reference;
            var key = "native-" + art + "-" + fps + (reference ? "-reference" : "-remote");
            var pose = key + ".pose.json";
            metrics.CombatPhase = "charge-" + key;
            cyclone = false;
            if (target != null) target.transform.position = new Vector3(106, 35, 0);
            hero.transform.position = new Vector3(actor ? 104 : 90, 20, 0);
            hero.GetComponent<Rigidbody2D>().velocity = Vector2.zero;
            hero.ResetAirMoves(); hero.RegainControl(); hero.StartAnimationControl();
            scale = hero.transform.localScale; scale.x = -Math.Abs(scale.x); hero.transform.localScale = scale;
            Flag(role + ".ready-" + key);
            yield return Until(() => Has((host ? "guest" : "host") + ".ready-" + key), "both native art observers ready " + key);
            if (actor) { held = true; pressFrame = Time.frameCount + 1; }
            yield return new WaitForSecondsRealtime(2);
            if (actor) File.WriteAllText(Path.Combine(root, pose), JsonConvert.SerializeObject(new[] { hero.transform.position.x, hero.transform.position.y }));
            yield return Until(() => Has(pose), "native attacker release pose captured " + key);
            if (target != null) {
                var position = JsonConvert.DeserializeObject<float[]>(File.ReadAllText(Path.Combine(root, pose)));
                target.transform.position = new Vector3(position[0] + 2, position[1], 0);
                Physics2D.SyncTransforms(); Flag("aligned-" + key);
            }
            yield return Until(() => Has("aligned-" + key), "authority target aligned " + key);
            var expectedPose = JsonConvert.DeserializeObject<float[]>(File.ReadAllText(Path.Combine(root, pose)));
            var expectedTarget = new Vector3(expectedPose[0] + 2, expectedPose[1], 0);
            yield return Until(() => visibleTarget != null && Vector2.Distance(visibleTarget.transform.position, expectedTarget) < .5f,
                "visible native art target alignment received " + key, 20);
            Flag(role + ".target-ready-" + key);
            yield return Until(() => Has((host ? "guest" : "host") + ".target-ready-" + key), "both visible native art targets ready " + key);
            metrics.CombatPhase = key;
            var soulBefore = metrics.LocalSoulCalls;
            if (actor && art == "dash") {
                dashHeld = true;
                yield return Until(() => hero.cState.dashing, "actual native dash before charged release " + key, 2);
                dashHeld = false;
            }
            if (actor) { cyclone = art == "cyclone"; held = false; releaseFrame = Time.frameCount + 1; }
            var observeUntil = Time.realtimeSinceStartup + 2;
            while (Time.realtimeSinceStartup < observeUntil) {
                // Follow the actual native dash displacement. Aligning only
                // before dash incorrectly leaves the target behind its slash.
                if (target != null) {
                    var attacker = actor ? hero.gameObject : players.GetPlayerObject(peerId);
                    if (attacker == null) throw new Exception("native art attacker body missing " + key);
                    target.transform.position = attacker.transform.position + new Vector3(2, 0, 0);
                    Physics2D.SyncTransforms();
                }
                yield return null;
            }
            cyclone = false;
            Log("NATIVE ART RELEASED " + key + " state=" + arts.ActiveStateName + " clip=" + hero.GetComponent<tk2dSpriteAnimator>().CurrentClip?.name);
            Log("NATIVE ART BLOOD " + key + " bursts=" + (metrics.BloodBursts.TryGetValue(key, out var bursts) ? bursts : 0));
            if (art != "cyclone") {
                var impacts = metrics.ImpactCalls.TryGetValue(key, out var count) ? count : 0;
                Require(impacts <= 1 && bursts <= 2, "one native charged-art impact and blood burst pair " + key +
                    " impacts=" + impacts + " blood=" + bursts);
            }
            if (actor && !ArenaManager.IsSceneHost) {
                var localHits = metrics.ObservedReplicaContacts.Where(h => h.phase == key && !h.remote).ToArray();
                File.WriteAllText(Path.Combine(root, key + ".local-contacts.json"), JsonConvert.SerializeObject(localHits, Formatting.Indented));
                // The authority follows the actual dash body. Its delayed
                // replica can miss a brief local slash entirely, particularly
                // at 20 FPS. Authority must still accept exactly one hit below;
                // local prediction may accept zero or one, never extra hits.
                Require((art == "cyclone" || localHits.Length <= 1) && localHits.All(h => h.damage == 0),
                    "guest's own native charged art leaves HP to authority " + key + " actual=" + string.Join(",", localHits.Select(h => h.damage)));
                Require(localHits.Length == 0 || metrics.LocalSoulCalls > soulBefore,
                    "native own-art soul feedback preserved " + key + " contacts=" + localHits.Length);
                if (art == "cyclone") {
                    var impacts = metrics.ImpactCalls.TryGetValue(key, out var count) ? count : 0;
                    Require(impacts <= 3 && bursts <= 6, "own native cyclone feedback bound " + key + " impacts=" + impacts + " blood=" + bursts);
                }
            }
            if (ArenaManager.IsSceneHost) {
                var observed = metrics.ObservedCombatHits.Where(h => h.phase == key && h.remote != reference).ToArray();
                File.WriteAllText(Path.Combine(root, key + ".hits.json"), JsonConvert.SerializeObject(observed, Formatting.Indented));
                if (art == "cyclone") {
                    // Native spin lasts 0.45 seconds; HealthManager evasion is
                    // 0.2 seconds, advanced on frame boundaries. The same
                    // local input can therefore land two or three hits. An
                    // exact count comparison between separate activations
                    // incorrectly rejects normal native timing variation.
                    Require(observed.Length >= 2 && observed.Length <= 3 && observed.All(h => h.damage == 26),
                        "native cyclone damage and contact bound " + key + " actual=" + string.Join(",", observed.Select(h => h.damage)));
                    Require(observed.Zip(observed.Skip(1), (a, b) => b.time - a.time).All(dt => dt >= .18f),
                        "native cyclone evasion cadence " + key);
                    Require(bursts <= 2 * observed.Length, "native cyclone blood follows accepted contacts " + key);
                    if (!reference) {
                        var native = JsonConvert.DeserializeObject<ColosseumNetworkMetrics.ObservedCombatHit[]>(File.ReadAllText(Path.Combine(root, key.Replace("-remote", "-reference") + ".hits.json")));
                        Require(native.Length >= 2 && native.Length <= 3 && observed.All(h => h.damage == native[0].damage),
                            "remote cyclone matches native damage per contact " + key);
                    }
                } else Require(observed.Length == 1 && observed[0].damage == 52,
                    "native charged art hits once " + key + " actual=" + string.Join(",", observed.Select(h => h.damage)));
            }
            Flag(role + ".done-" + key);
            yield return Until(() => Has((host ? "guest" : "host") + ".done-" + key), "both native art observations complete " + key);
            if (target != null) File.WriteAllText(Path.Combine(root, key + ".authority-hp"), target.hp.ToString());
            yield return Until(() => Has(key + ".authority-hp") && visibleTarget.hp == int.Parse(File.ReadAllText(Path.Combine(root, key + ".authority-hp"))),
                "native art HP converges to authoritative result " + key, 15);
        }
        metrics.CombatPhase = "none";
        Flag(role + ".native-arts-complete");
        yield return Until(() => Has((host ? "guest" : "host") + ".native-arts-complete"), "both native art observations complete");
        Application.targetFrameRate = -1;
    }
}
