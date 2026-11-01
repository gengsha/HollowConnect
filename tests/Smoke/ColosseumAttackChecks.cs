using System;
using System.Collections;
using System.IO;
using System.Linq;
using Hkmp.Game.Client.Entity;
using Newtonsoft.Json;
using UnityEngine;
using Object = UnityEngine.Object;

namespace HollowConnect.Smoke;

public sealed partial class ColosseumProbe {
    private IEnumerator ColosseumAttackChecks() {
        var metrics = GetComponent<ColosseumNetworkMetrics>();
        var hero = HeroController.instance;
        HealthManager target = null;
        PlayMakerFSM[] fsms = null;
        Rigidbody2D body = null;
        RigidbodyType2D oldBody = default;
        RigidbodyConstraints2D oldConstraints = default;
        var oldGravity = 0f;
        RigidbodySleepMode2D oldSleep = default;
        var oldHp = 0;
        if (ArenaManager.IsSceneHost) {
            yield return Until(() => Object.FindObjectsOfType<HealthManager>().Any(h => h.name == "Colosseum_Miner(Clone)" && h.gameObject.activeInHierarchy), "native first-wave miner available", 20);
            target = Object.FindObjectsOfType<HealthManager>().First(h => h.name == "Colosseum_Miner(Clone)" && h.gameObject.activeInHierarchy);
            oldHp = target.hp; target.hp = 10000;
            fsms = target.GetComponents<PlayMakerFSM>().Where(f => f.enabled).ToArray();
            foreach (var fsm in fsms) fsm.Fsm.ManualUpdate = true;
            body = target.GetComponent<Rigidbody2D>();
            // A kinematic target cannot contact the remote avatar's kinematic
            // nail-art collider. Freeze a dynamic body instead.
            if (body != null) {
                oldBody = body.bodyType; oldConstraints = body.constraints; oldGravity = body.gravityScale;
                oldSleep = body.sleepMode; body.sleepMode = RigidbodySleepMode2D.NeverSleep;
                body.bodyType = RigidbodyType2D.Dynamic; body.constraints = RigidbodyConstraints2D.FreezeAll;
                body.gravityScale = 0; body.velocity = Vector2.zero;
            }
            target.transform.position = new Vector3(106, 20, 0);
            Flag("audit.target-ready");
        }
        yield return Until(() => Has("audit.target-ready"), "authority collision target ready");
        // Hero.Update also publishes the actual player pose. Disabling it
        // leaves the remote attacker at its previous location.
        var oldEnabled = hero.enabled;
        hero.StopAnimationControl();
        hero.transform.position = new Vector3(ArenaManager.IsSceneHost ? 100 : 104, 20, 0);
        var scale = hero.transform.localScale; scale.x = -Math.Abs(scale.x); hero.transform.localScale = scale;
        hero.GetComponent<Rigidbody2D>().velocity = Vector2.zero;
        PlayerData.instance.nailDamage = 21; PlayerData.instance.fireballLevel = 2;
        Physics2D.SyncTransforms();
        var animator = hero.GetComponent<tk2dSpriteAnimator>();
        var spells = Environment.GetEnvironmentVariable("HOLLOWCONNECT_GOLD_AUDIT_SPELLS") == "1";
        if (spells) metrics.LossEnabled = Environment.GetEnvironmentVariable("HOLLOWCONNECT_GOLD_AUDIT_LOSS") == "1";
        var spellPreflight = Environment.GetEnvironmentVariable("HOLLOWCONNECT_GOLD_AUDIT_SPELL_PREFLIGHT") == "1";
        var clips = spellPreflight ? new[] { "Quake Land 2" } : spells ? new[] { "Fireball2 Cast", "Quake Land", "Quake Land 2" } : new[] { "NA Big Slash", "NA Dash Slash", "Fireball2 Cast" };
        if (spells) {
            PlayerData.instance.quakeLevel = 2;
            PlayerData.instance.SetBool("equippedCharm_19", false);
            File.WriteAllText(Path.Combine(root, role + ".spell-native-control.json"), JsonConvert.SerializeObject(hero.spellControl.FsmStates
                .Where(s => s.Name.Contains("Land") || s.Name.Contains("Damage"))
                .Select(s => new { s.Name, actions = s.Actions.Select(a => new { type = a.GetType().Name, fields = a.GetType().GetFields().ToDictionary(field => field.Name, field => Describe(field.GetValue(a))) }) }), Formatting.Indented));
            foreach (var name in new[] { "Q Slam", "Q Slam 2", "Q Mega", "Q Trail", "Q Trail 2" }) {
                var source = hero.spellControl.gameObject.GetComponentsInChildren<Transform>(true).First(t => t.name == name);
                File.WriteAllText(Path.Combine(root, role + ".spell-prefab-" + name.Replace(' ', '-') + ".json"), JsonConvert.SerializeObject(source.GetComponentsInChildren<Collider2D>(true).Select(c => new {
                    path = WallPath(c.transform), c.enabled, local = c.transform.localPosition.ToString(), position = c.transform.position.ToString(), bounds = c.bounds.ToString(),
                    fsms = c.GetComponents<PlayMakerFSM>().Select(f => new { f.FsmName, f.ActiveStateName, states = f.FsmStates.Select(s => new { s.Name, actions = s.Actions.Select(a => new { type = a.GetType().Name, fields = a.GetType().GetFields().ToDictionary(field => field.Name, field => Describe(field.GetValue(a))) }) }) })
                }), Formatting.Indented));
            }
        }
        File.WriteAllText(Path.Combine(root, role + ".native-attack-clips.json"), JsonConvert.SerializeObject(clips.Select(name => {
            var clip = animator.GetClipByName(name);
            return new { name, wrap = clip.wrapMode.ToString(), clip.fps, duration = clip.Duration,
                frames = clip.frames.Select((frame, index) => new { index, frame.triggerEvent, frame.eventInfo }) };
        }), Formatting.Indented));
        foreach (var fps in spellPreflight ? new[] { 60 } : new[] { 60, 20 }) foreach (var clip in clips) foreach (var repeat in spellPreflight ? new[] { 1 } : new[] { 1, 2 }) {
            Application.targetFrameRate = fps; QualitySettings.vSyncCount = 0;
            var key = clip.Replace(' ', '-') + "-" + fps + "-" + repeat; metrics.CombatPhase = key;
            if (spellPreflight && repeat == 1 && clip.StartsWith("Quake", StringComparison.Ordinal)) {
                if (ArenaManager.IsSceneHost) yield return NativeSpellLandingReference(clip, key);
                yield return Until(() => Has("native-reference-" + key), "native landing reference completed " + key);
                metrics.CombatPhase = key;
            }
            var hitsBefore = metrics.CombatHits; var damageBefore = metrics.CombatDamage;
            hero.StopAnimationControl();
            if (target != null) { target.transform.position = new Vector3(106, 20, 0); if(body != null) body.velocity = Vector2.zero; }
            var beforeHp = target == null ? 0 : target.hp;
            Flag(role + ".clip-ready-" + key);
            yield return Until(() => Has((host ? "guest" : "host") + ".clip-ready-" + key), "both clip observers ready " + clip);
            yield return new WaitForSecondsRealtime(1);
            if (!ArenaManager.IsSceneHost) {
                Log("GOLD AUDIT SINGLE NATIVE CLIP PLAY " + key);
                animator.Stop(); animator.PlayFromFrame(clip, 0);
            }
            yield return new WaitForSecondsRealtime(.05f);
            if (ArenaManager.IsSceneHost) {
                var sources = Object.FindObjectsOfType<Collider2D>().Where(c =>
                    (spells ? LocalWorldIsolation.IsRemote(c.gameObject) : Hkmp.Animation.AttackOwnership.RemoteOwner(c.gameObject) >= 0) &&
                    (spells || c.name.Contains("Slash") || c.name.Contains("Fireball"))).Select(c => new {
                        c.name, id = c.GetInstanceID(), c.enabled, c.isTrigger,
                        layer = c.gameObject.layer, body = c.attachedRigidbody == null ? null : new { c.attachedRigidbody.simulated, c.attachedRigidbody.isKinematic, c.attachedRigidbody.useFullKinematicContacts, asleep = c.attachedRigidbody.IsSleeping() },
                        position = c.transform.position.ToString(), bounds = c.bounds.ToString(),
                        animator = c.GetComponent<tk2dSpriteAnimator>()?.ClipFps,
                        limiter = c.GetComponent<LimitSendEvents>() == null ? null : c.GetComponent<LimitSendEvents>().GetType().GetFields(System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic).ToDictionary(f => f.Name, f => Describe(f.GetValue(c.GetComponent<LimitSendEvents>()))),
                        clipFps = c.GetComponent<tk2dSpriteAnimator>()?.DefaultClip?.fps,
                        distance = target.GetComponents<Collider2D>().Select(t => new { t.name, t.enabled, t.isTrigger, layer = t.gameObject.layer, ignore = Physics2D.GetIgnoreLayerCollision(c.gameObject.layer, t.gameObject.layer), simulated = t.attachedRigidbody?.simulated, body = t.attachedRigidbody?.bodyType.ToString(), bounds = t.bounds.ToString(), distance = c.Distance(t).distance }),
                        fsms = c.GetComponents<PlayMakerFSM>().Select(f => new { f.FsmName, f.ActiveStateName, f.Fsm.ManualUpdate,
                            actions = f.FsmStates.Select(s => new { s.Name, actions = s.Actions.Select(a => new { type = a.GetType().Name, fields = a.GetType().GetFields().ToDictionary(field => field.Name, field => Describe(field.GetValue(a))) }) }) })
                    }).ToArray();
                File.WriteAllText(Path.Combine(root, role + ".colliders-" + key + ".json"), JsonConvert.SerializeObject(sources, Formatting.Indented));
            }
            yield return new WaitForSecondsRealtime(2.95f);
            if (ArenaManager.IsSceneHost) {
                Log("GOLD AUDIT COLLISION RESULT " + clip + " hp=" + target.hp + " target=" + target.GetInstanceID());
                if (spells) {
                    var observed = metrics.ObservedCombatHits.Where(h => h.phase == key && h.remote).ToArray();
                    File.WriteAllText(Path.Combine(root, role + ".spell-hits-" + key + ".json"), JsonConvert.SerializeObject(observed, Formatting.Indented));
                    var referenceKey = clip.Replace(' ', '-') + "-" + fps + "-1";
                    var nativeHits = spellPreflight && clip.StartsWith("Quake", StringComparison.Ordinal)
                        ? JsonConvert.DeserializeObject<ColosseumNetworkMetrics.ObservedCombatHit[]>(File.ReadAllText(Path.Combine(root, role + ".native-reference-" + referenceKey + ".json")))
                        : null;
                    // Values were established by the isolated native landing
                    // preflight. Reusing local attack objects between spells
                    // carries native FSM/collider history into that reference.
                    var expected = nativeHits != null ? nativeHits.Select(h => h.damage).ToArray() :
                        clip == "Quake Land 2" ? new[] { 30, 15 } : new[] { clip == "Quake Land" ? 20 : 30 };
                    Log("SPELL CONTACT " + key + " observed=" + JsonConvert.SerializeObject(observed));
                    Require(observed.Select(h => h.damage).OrderBy(d => d).SequenceEqual(expected.OrderBy(d => d)),
                        "native remote spell phases have expected damage " + key + " expected=" + string.Join(",", expected) + " observed=" + string.Join(",", observed.Select(h => h.damage)));
                    if (nativeHits != null) Require(observed.Select(SpellStage).OrderBy(s => s).SequenceEqual(nativeHits.Select(SpellStage).OrderBy(s => s)),
                        "remote landing matches native sources and phase damage " + key);
                    if (clip.StartsWith("Quake", StringComparison.Ordinal)) Require(observed.Count(h => h.path.Contains("/Q Slam")) == 1 &&
                        observed.Count(h => h.path.Contains("/Q Mega")) == (clip == "Quake Land 2" ? 1 : 0),
                        "one remote contact per distinct native landing stage " + key);
                } else {
                    var expected = clip.StartsWith("NA ", StringComparison.Ordinal) ? 52 : 30;
                    Require(metrics.CombatHits - hitsBefore == 1 && metrics.CombatDamage - damageBefore == expected,
                        "one real remote attack damages native target " + key + " hits=" + (metrics.CombatHits - hitsBefore) + " damage=" + (metrics.CombatDamage - damageBefore));
                }
            }
            Flag(role + ".clip-done-" + key);
            yield return Until(() => Has((host ? "guest" : "host") + ".clip-done-" + key), "both clip observations finished " + clip);
        }
        metrics.CombatPhase = "none";
        hero.enabled = oldEnabled;
        hero.StartAnimationControl();
        hero.transform.position = new Vector3(108, 24, 0);
        if (target != null) {
            target.hp = oldHp;
            foreach (var fsm in fsms) fsm.Fsm.ManualUpdate = false;
            if (body != null) { body.bodyType = oldBody; body.constraints = oldConstraints; body.gravityScale = oldGravity; body.sleepMode = oldSleep; }
        }
        Flag(role + ".combat-done");
        yield return Until(() => Has((host ? "guest" : "host") + ".combat-done"), "both clean combat observations complete");
        Application.targetFrameRate = -1;
    }

    private IEnumerator NativeSpellLandingReference(string clip, string key) {
        var metrics = GetComponent<ColosseumNetworkMetrics>();
        var phase = "native-reference-" + key;
        metrics.CombatPhase = phase;
        var hero = HeroController.instance;
        var priorPosition = hero.transform.position;
        var spells = hero.spellControl.gameObject;
        var slamName = clip == "Quake Land 2" ? "Q Slam 2" : "Q Slam";
        var slam = spells.GetComponentsInChildren<Transform>(true).First(t => t.name == slamName).gameObject;
        var mega = spells.GetComponentsInChildren<Transform>(true).First(t => t.name == "Q Mega").gameObject;
        Require(!slam.activeSelf && !mega.activeSelf, "native landing sources begin dormant " + key);
        var slamMarker = slam.AddComponent<NativeSpellReference>();
        var megaMarker = mega.AddComponent<NativeSpellReference>();
        try {
            // Use the knight's actual native objects, retaining their authored
            // parent hierarchy and FSM references. Only enable each landing
            // phase; no damage/hit callbacks are injected.
            hero.transform.position = new Vector3(104, 20, 0);
            hero.GetComponent<Rigidbody2D>().velocity = Vector2.zero;
            Physics2D.SyncTransforms();
            slam.SetActive(true);
            if (clip == "Quake Land 2") {
                yield return new WaitForSeconds(.4f);
                mega.SetActive(true); mega.GetComponent<tk2dSpriteAnimator>().PlayFromFrame(0);
            }
            yield return new WaitForSecondsRealtime(1.5f);
            var observed = metrics.ObservedCombatHits.Where(h => h.phase == phase && h.reference).ToArray();
            File.WriteAllText(Path.Combine(root, role + "." + phase + ".json"), JsonConvert.SerializeObject(observed, Formatting.Indented));
            Log("NATIVE SPELL REFERENCE " + key + " observed=" + JsonConvert.SerializeObject(observed));
            Require(observed.Length == (clip == "Quake Land 2" ? 2 : 1),
                "unaltered native landing hitboxes contact each phase " + key + " observed=" + string.Join(",", observed.Select(h => h.damage)));
            Require(observed.Count(h => h.path.Contains("/Q Slam")) == 1 &&
                observed.Count(h => h.path.Contains("/Q Mega")) == (clip == "Quake Land 2" ? 1 : 0),
                "native reference hits each distinct landing phase once " + key);
            Flag("native-reference-" + key);
        } finally {
            slam.SetActive(false); mega.SetActive(false);
            Object.Destroy(slamMarker); Object.Destroy(megaMarker);
            hero.transform.position = priorPosition; hero.GetComponent<Rigidbody2D>().velocity = Vector2.zero;
            Physics2D.SyncTransforms(); metrics.CombatPhase = "none";
        }
    }

    private static string SpellStage(ColosseumNetworkMetrics.ObservedCombatHit hit) =>
        (hit.path.Contains("/Q Mega") ? "late:" : "impact:") + hit.damage;
}
