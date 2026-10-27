using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Hkmp.Game.Client.Entity;
using HollowConnect;
using HollowConnect.Core;
using UnityEngine;
using Object = UnityEngine.Object;

namespace HollowConnect.Smoke;

public sealed partial class RescueProbe {
    private static bool StoryFixtureScene(GameObject go, string scene) => go.scene.name == scene || go.scene.name.StartsWith(scene + "_", StringComparison.Ordinal);
    private IEnumerator StoryTogether(string step) {
        Flag(role + ".story." + step);
        yield return Until(() => Has((host ? "guest" : "host") + ".story." + step), "both story " + step);
    }

    // Only normal input changes the rescuer's position after Down. No warp,
    // transform assignment, ShadeHit injection or replacement rescue API.
    private IEnumerator StoryWalk(float x, string step) {
        var hero = HeroController.instance;
        var start = hero.transform.position;
        using (new MonoMod.RuntimeDetour.Hook(typeof(HeroController).GetMethod("LookForInput", BindingFlags.Instance | BindingFlags.NonPublic),
            new Action<Action<HeroController>, HeroController>((orig, self) => {
                orig(self);
                if (self.acceptingInput) self.move_input = Math.Abs(self.transform.position.x - x) < .12f ? 0 : Math.Sign(x - self.transform.position.x);
            })))
            yield return Until(() => Math.Abs(hero.transform.position.x - x) < .2f, "native walk " + step, 12);
        hero.move_input = 0; // Release the synthetic direction before disposing the walking stage.
        Log("STORY WALK " + step + " from=" + start + " to=" + hero.transform.position);
    }

    // Independent fixture selection: native terrain must support an eight-unit
    // flat corridor. This does not call the product landing or route solver.
    private Vector3 StoryFloor(Vector3 seed, string scene) {
        var hero = HeroController.instance; var bodyBounds = hero.GetComponent<Collider2D>().bounds;
        var standingHeight = bodyBounds.extents.y - (bodyBounds.center - hero.transform.position).y + .1f;
        var candidates = new List<Vector3>();
        for (var x = seed.x - 18; x <= seed.x + 18; x += 1)
            foreach (var hit in Physics2D.RaycastAll(new Vector2(x, seed.y + 18), Vector2.down, 65, 1 << 8)) {
                if (hit.collider.isTrigger || hit.distance == 0 || hit.normal.y < .95f || hit.point.y > seed.y || !StoryFixtureScene(hit.collider.gameObject, scene) ||
                    hit.collider.GetComponentInParent<DamageHero>() != null) continue;
                var flat = true;
                foreach (var dx in new[] { -4f, -2f, 0f, 2f, 4f }) {
                    var support = Physics2D.Raycast(new Vector2(x + dx, hit.point.y + .5f), Vector2.down, 1, 1 << 8);
                    if (support.collider == null || support.collider.isTrigger || support.normal.y < .95f ||
                        Math.Abs(support.point.y - hit.point.y) > .12f ||
                        Physics2D.OverlapBox(new Vector2(x + dx, hit.point.y + 1), new Vector2(.8f, 1.5f), 0, 1 << 8) != null) { flat = false; break; }
                }
                if (flat) candidates.Add(new Vector3(x, hit.point.y + standingHeight, 0));
            }
        Check(candidates.Count > 0, "native terrain has a real walking corridor " + scene);
        return candidates.OrderBy(p => (p - seed).sqrMagnitude).First();
    }

    private IEnumerator StoryRescuePlacementChecks() {
        var selected = Environment.GetEnvironmentVariable("HOLLOWCONNECT_STORY_RESCUE_CASE");
        if (selected == "acid") { yield return StoryAcidRescueChecks(); yield break; }
        var dynamic = selected == "moving" || selected == "vanishing" || selected == "retry" || selected == "rejected" || selected == "timeout" || selected == "revive-loss";
        var scene = dynamic || selected == "blocked" ? "Crossroads_04" : selected;
        var authority = role == (Environment.GetEnvironmentVariable("HOLLOWCONNECT_STORY_RESCUE_AUTHORITY") ?? "host");
        var hero = HeroController.instance; var body = hero.GetComponent<Rigidbody2D>(); var pd = PlayerData.instance;
        var manager = Field<EntityManager>(engine.ClientManager, "_entityManager");
        // Keep live boss contact from adding unrelated deaths/recoil between
        // route trials. The deliberate lethal hit still runs the native path.
        // Native acid coverage is separate and returns above this fixture.
        var injectingLethal = false;
        using var combatIsolation = new MonoMod.RuntimeDetour.Hook(typeof(HeroController).GetMethod("TakeDamage"),
            new Action<Action<HeroController, GameObject, GlobalEnums.CollisionSide, int, int>, HeroController, GameObject, GlobalEnums.CollisionSide, int, int>(
                (orig, self, source, side, damage, hazard) => { if (injectingLethal) orig(self, source, side, damage, hazard); }));
        GameObject reviveLossSupport = null;
        using var lifeTrace = new MonoMod.RuntimeDetour.Hook(typeof(CoopRuntime).GetMethod("Receive", BindingFlags.Instance | BindingFlags.NonPublic),
            new Action<Action<CoopRuntime, Message>, CoopRuntime, Message>((orig, owner, message) => {
                if (message.Kind == Kind.Returning || message.Kind == Kind.Revived || message.Kind == Kind.RescueAnchor || message.Kind == Kind.Down)
                    Log("STORY LIFE " + message.Kind + " id=" + message.Id + " reason=" + message.Name + " serial=" + message.Serial + " revision=" + message.Value);
                if (selected == "revive-loss" && message.Kind == Kind.Revived && message.Id == runtime.LocalId) {
                    reviveLossSupport.SetActive(false); Physics2D.SyncTransforms();
                    Log("STORY INJECTED SUPPORT LOSS BETWEEN SERVER ACCEPTANCE AND LOCAL REVIVE");
                }
                orig(owner, message);
            }));
        var faultCount = 0;
        using var anchorFault = new MonoMod.RuntimeDetour.Hook(typeof(CoopServerAddon).GetMethod("Receive", BindingFlags.Instance | BindingFlags.NonPublic),
            new Action<Action<CoopServerAddon, ushort, Message>, CoopServerAddon, ushort, Message>((orig, addon, id, message) => {
                if (message.Kind == Kind.RescueAnchorRequest &&
                    (selected == "timeout" || (selected == "retry" || selected == "rejected") && faultCount < 2)) {
                    faultCount++; Log("STORY INJECTED ANCHOR FAULT " + selected + " revision=" + message.Aux);
                    if (selected != "rejected") return;
                    message.X = float.NaN; // Real server rejection and reliable negative acknowledgement.
                }
                orig(addon, id, message);
            }));
        BossSequenceController.Reset(); pd.SetInt("bossStatueTargetLevel", 0);
        pd.SetString("dreamReturnScene", "Town"); pd.SetString("dreamReturnGate", "left1");
        hero.takeNoDamage = true;
        if (!authority) yield return Until(() => Has("story.first"), "story first entrant");
        var previousVisit = Hkmp.CoopContext.LocalVisit;
        GameManager.instance.BeginSceneTransition(new GameManager.SceneLoadInfo {
            SceneName = scene, EntryGateName = scene.StartsWith("Dream_") ? "door1" :
                scene.StartsWith("GG_") ? "door_dreamEnter" : scene == "Fungus2_05" ? "right1" : "left1",
            Visualization = GameManager.SceneLoadVisualizations.Default, WaitForSceneTransitionCameraFade = true });
        yield return Until(() => runtime.Playable && GameManager.instance.sceneName == scene &&
            UnityEngine.SceneManagement.SceneManager.GetActiveScene().name == scene && Hkmp.CoopContext.LocalScene == scene &&
            Hkmp.CoopContext.LocalVisit > previousVisit && hero.acceptingInput &&
            !hero.cState.transitioning && !GameManager.instance.IsInSceneTransition && engine.ClientManager.SceneSnapshotReady &&
            manager.IsSceneHostDetermined && manager.IsSceneHost == authority, "native story room entered " + scene);
        hero.takeNoDamage = true;
        var entry = hero.transform.position;
        if (authority) Flag("story.first");
        yield return StoryTogether("entered");
        Log("STORY LOADED SCENES " + string.Join(",", Enumerable.Range(0, UnityEngine.SceneManagement.SceneManager.sceneCount)
            .Select(i => UnityEngine.SceneManagement.SceneManager.GetSceneAt(i).name)));
        if (scene == "Crossroads_10") foreach (var c in Object.FindObjectsOfType<Collider2D>(true).Where(c => c.name.Contains("Battle Scene")))
            Log("STORY BATTLE COLLIDER " + c.name + " scene=" + c.gameObject.scene.name + " enabled=" + c.enabled + " active=" + c.gameObject.activeInHierarchy + " trigger=" + c.isTrigger + " bounds=" + c.bounds);
        var battle = Object.FindObjectsOfType<PlayMakerFSM>(true).FirstOrDefault(f => StoryFixtureScene(f.gameObject, scene) &&
            f.gameObject.activeInHierarchy && f.FsmName == "Battle Control" && f.GetComponent<Collider2D>() != null && f.GetComponent<Collider2D>().enabled);
        var trigger = battle?.GetComponent<Collider2D>() ?? Object.FindObjectsOfType<Collider2D>(true)
            .FirstOrDefault(c => StoryFixtureScene(c.gameObject, scene) && c.enabled && c.gameObject.activeInHierarchy && c.isTrigger && c.name == "Battle Scene");
        if (trigger != null && trigger.enabled) {
            hero.transform.position = trigger.bounds.center; body.velocity = Vector2.zero; hero.AffectedByGravity(false);
            Physics2D.SyncTransforms(); yield return new WaitForSecondsRealtime(1);
            Log("STORY NATIVE BATTLE " + trigger.name + "=" + battle?.ActiveStateName);
        }
        var entities = Field<Dictionary<ulong, Entity>>(manager, "_entities");
        var boss = entities.Values.FirstOrDefault(e => BossDefeatSignals.IsBoss(e.Type) && e.Object.Host != null);
        if (scene == "Crossroads_10") {
            if (authority) {
                foreach (var enemy in entities.Values.Where(e => !BossDefeatSignals.IsBoss(e.Type) && e.Type != EntityType.FalseKnightHead &&
                    e.Object.Host != null && e.Object.Host.activeInHierarchy && e.Object.Host.GetComponent<HealthManager>() != null).ToArray())
                    enemy.Object.Host.GetComponent<HealthManager>().ApplyExtraDamage(1000);
                yield return Until(() => boss.Object.Host.GetComponents<PlayMakerFSM>().Any(f => f.FsmName == "FalseyControl" && f.ActiveStateName == "Idle"),
                    "native False Knight intro after real battle trigger", 20);
                Flag("story.knight-intro");
            } else yield return Until(() => Has("story.knight-intro"), "native False Knight authority intro");
        }
        var seed = trigger != null && trigger.enabled ? trigger.bounds.center : boss != null ? boss.Object.Host.transform.position : hero.transform.position;
        if (scene == "Dream_03_Infected_Knight") {
            var dreamGates = Object.FindObjectsOfType<PlayMakerFSM>(true).Where(f => StoryFixtureScene(f.gameObject, scene) &&
                f.name.StartsWith("Dream Gate", StringComparison.Ordinal) && f.FsmName == "Control").ToArray();
            Check(dreamGates.Length == 2, "Lost Kin native arena gates locate the fight independently of the boss spawn pool");
            seed = (dreamGates[0].transform.position + dreamGates[1].transform.position) / 2;
        }
        var arenaGates = Object.FindObjectsOfType<PlayMakerFSM>(true).Where(f => StoryFixtureScene(f.gameObject, scene) &&
            (f.FsmName == "BG Control" && f.name.StartsWith("Battle Gate", StringComparison.Ordinal) ||
             f.FsmName == "Control" && (f.name == "Dream Gate" || f.name.StartsWith("Dream Gate (", StringComparison.Ordinal))))
            .Select(f => f.transform.position).ToArray();
        var pairs = (from a in arenaGates from b in arenaGates where b.x - a.x >= 12 && b.x - a.x <= 90 && Math.Abs(a.y - b.y) < 2
            select (a + b) / 2).OrderBy(p => Math.Abs(p.y - seed.y) * 2 + Math.Abs(p.x - seed.x)).ToArray();
        if (pairs.Length > 0) { seed = pairs[0] + Vector3.up; Log("STORY PAIRED NATIVE ARENA GATES CENTER " + seed); }
        Log("STORY ARENA SEED " + seed + " boss=" + boss?.Type);
        var floor = StoryFloor(seed, scene);
        Log("STORY GEOMETRY scene=" + scene + " entry=" + entry + " floor=" + floor + " zone=" + GameManager.instance.GetCurrentMapZone());
        foreach (var marker in GameObject.FindGameObjectsWithTag("Shade Marker").Where(g => g.scene.name == scene))
            Log("STORY NATIVE SHADE MARKER " + marker.name + "=" + marker.transform.position);
        foreach (var gate in Object.FindObjectsOfType<PlayMakerFSM>(true).Where(f => StoryFixtureScene(f.gameObject, scene) && f.name.IndexOf("gate", StringComparison.OrdinalIgnoreCase) >= 0).Take(12))
            Log("STORY NATIVE GATE " + gate.name + "/" + gate.FsmName + "=" + gate.ActiveStateName + " pos=" + gate.transform.position);
        Modding.ModHooks.SoulGainHook += _ => 0;
        foreach (var victimRole in selected == "timeout" || selected == "blocked" || selected == "revive-loss" ? new[] { "guest" } : new[] { "guest", "host" }) {
            faultCount = 0;
            var victim = role == victimRole; var step = selected + "." + victimRole;
            Check(!runtime.Down && !runtime.Returning, "no unrelated second death between route trials " + step);
            GameObject support = null;
            // Dynamic cases are labelled injected support faults, not native
            // platform coverage. Keep the natural scene floor below as fallback.
            if (dynamic) {
                support = new GameObject("Story audit injected support " + step) { layer = 8 };
                support.transform.position = floor + Vector3.up * (selected == "revive-loss" ? 40 : 4);
                support.AddComponent<BoxCollider2D>().size = new Vector2(16, 1);
                reviveLossSupport = support;
            }
            var liveBounds = hero.GetComponent<Collider2D>().bounds;
            var standingHeight = liveBounds.extents.y - (liveBounds.center - hero.transform.position).y + .1f;
            var stand = dynamic ? new Vector3(floor.x, support.transform.position.y + .5f + standingHeight, 0) : floor;
            hero.takeNoDamage = true; hero.cState.invulnerable = hero.cState.recoiling = false;
            hero.RegainControl(); hero.StartAnimationControl();
            body.simulated = true; body.bodyType = RigidbodyType2D.Dynamic; body.constraints = RigidbodyConstraints2D.FreezeRotation;
            pd.SetInt("health", 5); pd.SetInt("healthBlue", 0); pd.SetBool("isInvincible", false);
            pd.SetInt("MPCharge", 0); pd.SetInt("MPReserve", 0);
            pd.SetInt("geo", 321); pd.SetInt("geoPool", 67); pd.SetString("shadeScene", "Town");
            hero.transform.position = stand + Vector3.left * 3; body.velocity = Vector2.zero;
            hero.AffectedByGravity(true); Physics2D.SyncTransforms();
            yield return new WaitForFixedUpdate();
            yield return Until(() => hero.cState.onGround && hero.acceptingInput, "fixture supported before death " + step);
            // Leave the old checkpoint behind. The victim's actual corridor
            // supplies safety evidence, independent of that native marker.
            hero.SetHazardRespawn(entry, true);
            if (victim) yield return StoryWalk(stand.x + 3, "victim trail " + step);
            // Crossing a native dream/boss intro can relinquish control after
            // the first grounded frame. Wait for both real presences to be
            // Alive before injecting a death; a Loading actor cannot rescue.
            yield return new WaitForSecondsRealtime(4);
            yield return Until(() => hero.acceptingInput && !hero.controlReqlinquished && !hero.cState.transitioning &&
                runtime.Peers.Values.All(p => p.Life == Life.Alive && p.State != null && p.State.Scene == scene),
                "both native battle controls ready before death " + step, 20);
            yield return StoryTogether(step + ".ready");
            GameObject gate = null;
            if (selected == "blocked") {
                gate = new GameObject("Battle Gate Story audit injected isolation") { layer = 8 };
                gate.transform.position = stand + Vector3.up;
                gate.AddComponent<BoxCollider2D>().size = new Vector2(.8f, 8);
                var control = gate.AddComponent<PlayMakerFSM>(); control.Reset(); control.FsmName = "BG Control"; control.enabled = false;
                Physics2D.SyncTransforms();
                Log("STORY INJECTED CLOSED BATTLE GATE " + gate.transform.position);
            }
            var serial = runtime.DeathSerial;
            var quota = runtime.RescuesUsed;
            if (victim) {
                var origin = hero.transform.position;
                if (!dynamic) hero.transform.position += Vector3.up * 3;
                Physics2D.SyncTransforms();
                var landingArgs = new object[] { hero, (Vector2)hero.transform.position, 20f, true, null, null };
                var found = (bool)typeof(CoopRuntime).GetMethod("TryStoryLanding", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(runtime, landingArgs);
                Log("STORY BEFORE DOWN landing=" + found + " point=" + landingArgs[4] + " support=" + landingArgs[5] + " bounds=" + hero.GetComponent<Collider2D>().bounds);
                if (!found) {
                    var b = hero.GetComponent<Collider2D>().bounds;
                    var o = b.center - hero.transform.position;
                    foreach (var hit in Physics2D.RaycastAll(hero.transform.position, Vector2.down, 20, 1 << 8).Where(h => !h.collider.isTrigger && h.normal.y > .7f).Take(2)) {
                        var point = new Vector2(hit.point.x, hit.point.y + b.extents.y - o.y + .07f);
                        foreach (var c in Physics2D.OverlapBoxAll(point + (Vector2)o, (Vector2)b.size * .94f, 0))
                            Log("STORY LANDING OVERLAP " + c.name + " layer=" + c.gameObject.layer + " trigger=" + c.isTrigger + " hp=" + c.GetComponentInParent<HealthManager>()?.name +
                                " damage=" + c.GetComponentInParent<DamageHero>()?.hazardType + " scene=" + c.gameObject.scene.name + " bounds=" + c.bounds);
                    }
                }
                if (found) {
                    var size = (Vector2)hero.GetComponent<Collider2D>().bounds.size;
                    var offset = (Vector2)(hero.GetComponent<Collider2D>().bounds.center - hero.transform.position);
                    var delta = (Vector2)landingArgs[4] - (Vector2)hero.transform.position;
                    foreach (var hit in Physics2D.BoxCastAll((Vector2)hero.transform.position + offset, size * .9f, 0, delta.normalized, delta.magnitude))
                        Log("STORY SWEEP " + hit.collider.name + " distance=" + hit.distance + " layer=" + hit.collider.gameObject.layer + " trigger=" + hit.collider.isTrigger + " root=" + hit.collider.transform.root.name);
                }
                Physics2D.SyncTransforms(); hero.takeNoDamage = false; pd.SetInt("health", 1);
                injectingLethal = true;
                try { hero.TakeDamage(null, GlobalEnums.CollisionSide.left, 1, 1); }
                finally { injectingLethal = false; }
                yield return Until(() => runtime.Down, "native lethal damage produces down " + step, 10);
                Log("STORY DOWN " + step + " origin=" + origin + " anchor=" + runtime.DeathPosition + " body=" + Field<Vector2>(runtime, "rescueBodySize"));
                Check(runtime.DeathSerial == serial + 1 && !runtime.Spectating, "one rescuable death " + step);
                Check(Math.Abs(runtime.DeathPosition.x - origin.x) < 6 && runtime.DeathPosition.y <= origin.y + .5f,
                    "story anchor keeps actual death-side area or recent safe trail " + step);
                Flag("story.down." + step);
            } else {
                yield return Until(() => runtime.Peers.Values.Any(p => p.Life == Life.Down), "actual teammate down " + step);
            }
            yield return StoryTogether(step + ".down");
            if (victim) {
                Log("STORY GATE ISOLATION " + typeof(CoopRuntime).GetMethod("StoryIsolatedByBattleGate", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(runtime, new object[] { hero }));
                foreach (var peer in runtime.Peers.Values) Log("STORY SURVIVOR " + peer.Life + "=" + peer.State?.X + "," + peer.State?.Y);
            }
            if (selected == "blocked") {
                if (victim) { yield return RuleArrived("sealed rescue corridor"); yield return RuleBenchUp(); }
                else yield return Until(() => runtime.Peers.Values.Single().Life == Life.Alive && runtime.Peers.Values.Single().State.Scene == "Town",
                    "peer returns when all survivors are across a sealed battle gate", 40);
                Check(runtime.DeathSerial == (victim ? serial + 1 : serial) && runtime.RescuesUsed == quota,
                    "sealed corridor cannot rescue through wall or consume rescue quota");
                yield return StoryTogether(step + ".returned"); Object.Destroy(gate); continue;
            }
            if (dynamic && selected != "revive-loss") {
                if (selected == "moving") support.transform.position += Vector3.right * 20;
                else support.SetActive(false);
                Physics2D.SyncTransforms();
                // The rescuer stays at their physical position and falls onto
                // the native floor; no transform correction after Down.
                if (selected == "timeout") {
                    if (victim) {
                        yield return RuleArrived("anchor confirmation timeout");
                        Check(runtime.DeathSerial == serial + 1 && runtime.RescuesUsed == quota,
                            "unconfirmed anchor returns without extra death event or rescue charge");
                        yield return RuleBenchUp();
                    } else yield return Until(() => runtime.Peers.Values.Single().Life == Life.Alive &&
                        runtime.Peers.Values.Single().State.Scene == "Town", "peer observes native return after bounded anchor retries", 45);
                    yield return StoryTogether(step + ".returned");
                    Object.Destroy(support); continue;
                }
                if (victim) {
                    yield return Until(() => Field<uint>(runtime, "rescueAnchorAccepted") > 0,
                        "server confirms replacement in same death " + step, 12);
                    Check(runtime.DeathSerial == serial + 1 && runtime.RescuesUsed == quota, "reanchor preserves death and quota " + step);
                    Log("STORY REANCHOR " + step + "=" + runtime.DeathPosition);
                    var accepted = Field<uint>(runtime, "rescueAnchorAccepted");
                    if (selected == "retry" || selected == "rejected") Check(accepted == 3, "two failed proposals recover on third revision " + step);
                    var confirmed = runtime.DeathPosition;
                    Inject(new Message { Kind = Kind.RescueAnchor, Id = runtime.LocalId, Serial = runtime.DeathSerial, Value = (int)accepted,
                        Scene = scene, X = confirmed.x + 50, Y = confirmed.y });
                    Inject(new Message { Kind = Kind.RescueAnchor, Id = runtime.LocalId, Serial = runtime.DeathSerial, Value = -(int)accepted, Scene = scene });
                    Inject(new Message { Kind = Kind.RescueAnchor, Id = runtime.LocalId, Serial = runtime.DeathSerial - 1, Value = (int)accepted + 1,
                        Scene = scene, X = confirmed.x + 50, Y = confirmed.y });
                    Check(runtime.DeathPosition == confirmed && runtime.Down, "stale duplicate and rejection cannot move confirmed anchor " + step);
                } else {
                    yield return Until(() => runtime.Peers.Values.Single().State.Y < stand.y - 2 ||
                        runtime.Peers.Values.Single().State.X > stand.x + 5, "peer receives replacement anchor " + step, 12);
                }
            }
            yield return StoryTogether(step + ".anchor");
            if (!victim) {
                var peer = runtime.Peers.Single(); var service = Field<ShadeService>(runtime, "shades");
                yield return Until(() => service.Get(peer.Key) != null, "shade exists before real walking rescue " + step);
                var anchor = service.Get(peer.Key).transform.position;
                Check(Vector2.Distance(anchor, new Vector2(peer.Value.State.X, peer.Value.State.Y)) < .1f,
                    "shade and authoritative roster agree " + step);
                var start = hero.transform.position;
                yield return StoryWalk(anchor.x - 1.5f, "rescuer route " + step);
                Check(Math.Abs(hero.transform.position.x - start.x) > .4f, "rescuer really travels to shade " + step);
                hero.FaceRight(); hero.Attack(GlobalEnums.AttackDirection.normal);
                yield return Until(() => runtime.Peers[peer.Key].Life == Life.Alive, "real nail collision rescues teammate " + step, 8);
                Check(pd.GetInt("MPCharge") == RescueFeedback.SoulReward, "one native rescue soul reward " + step);
            } else {
                if (selected == "revive-loss") {
                    yield return RuleArrived("support lost during accepted revive"); yield return RuleBenchUp();
                    Check(runtime.DeathSerial == serial + 1 && runtime.RescuesUsed == quota + 1,
                        "failed accepted revive retains its original death and single accepted rescue charge");
                    yield return StoryTogether(step + ".rescued"); Object.Destroy(support); continue;
                }
                yield return Until(() => !runtime.Down && !runtime.Returning && pd.health == 1 && hero.acceptingInput && hero.cState.onGround,
                    "one-health supported controllable rescue " + step, 25);
                hero.takeNoDamage = true;
                Check(runtime.DeathSerial == serial + 1 && runtime.RescuesUsed == quota + 1, "exactly one rescue quota charge " + step);
                Check(pd.GetInt("geo") == 321 && pd.GetInt("geoPool") == 67 && pd.GetString("shadeScene") == "Town", "personal death resources preserved " + step);
            }
            yield return StoryTogether(step + ".rescued");
            if (support != null) Object.Destroy(support);
            yield return new WaitForSecondsRealtime(2);
        }
    }
}
