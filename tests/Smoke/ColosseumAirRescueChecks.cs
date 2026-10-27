using System;
using System.Collections;
using System.Linq;
using System.Reflection;
using HollowConnect;
using HollowConnect.Core;
using UnityEngine;
using Object = UnityEngine.Object;

namespace HollowConnect.Smoke;

public sealed partial class ColosseumProbe {
    private bool airRescueLive;
    private IEnumerator AirTogether(string step) {
        Flag(role + ".air." + step);
        yield return Until(() => Has((host ? "guest" : "host") + ".air." + step), "both air " + step, 60);
    }
    private IEnumerator ColosseumAirRescue() {
        Require(trial == "Gold", "aerial rescue uses actual third trial");
        var hero = HeroController.instance; var body = hero.GetComponent<Rigidbody2D>(); var pd = PlayerData.instance;
        hero.AffectedByGravity(false); body.velocity = Vector2.zero;
        hero.transform.position = new Vector3(108, 20, 0);
        yield return AirTogether("start");
        var battle = ArenaEntity.Object.Host.GetComponents<PlayMakerFSM>().Single(f => f.FsmName == "Battle Control");
        if (ArenaManager.IsSceneHost) battle.Fsm.ManualUpdate = false;
        var deadline = Time.realtimeSinceStartup + 300; var prior = ""; var since = Time.realtimeSinceStartup;
        while (ArenaState("Battle Control") != "Wave 34") {
            var state = ArenaState("Battle Control");
            if (state != prior) {
                prior = state; since = Time.realtimeSinceStartup;
                if (state.StartsWith("Wave ") && int.TryParse(state.Substring(5), out var n)) wallWave = n;
                Log("AIR NATIVE PROGRESS " + state); SaveWallActors(state);
            }
            if (Time.realtimeSinceStartup > deadline || Time.realtimeSinceStartup - since > 30) {
                SaveWallActors("air-stalled"); throw new TimeoutException("Native aerial fixture stalled at " + state);
            }
            ApproachCurrentWallWave(); DamageCurrentWallWave();
            // This native Wave 33 spawn has no network entity registration.
            // Advance it through its real HealthManager death callback too;
            // never alter the battle counter or force the next wave state.
            if (ArenaManager.IsSceneHost)
                foreach (var mawlek in Object.FindObjectsOfType<HealthManager>().Where(h => h.gameObject.scene.name == ActiveScene &&
                    h.name == "Mawlek Col(Clone)" && h.gameObject.activeInHierarchy && !h.isDead && h.hp > 0 && !h.IsInvincible &&
                    h.GetComponents<Collider2D>().Any(c => c.enabled))) {
                    Log("AIR NATIVE DAMAGE " + mawlek.name + " hp=" + mawlek.hp);
                    mawlek.ApplyExtraDamage(mawlek.hp);
                }
            yield return new WaitForSecondsRealtime(.15f);
        }
        yield return new WaitForSecondsRealtime(3);
        Require(ArenaState("Battle Control") == "Wave 34", "original floorless flying-enemy wave remains live");
        var spikes = LocalArena.GetComponentsInChildren<DamageHero>(true).SelectMany(d => d.GetComponents<Collider2D>())
            .Where(c => c.enabled && c.gameObject.activeInHierarchy && c.name.IndexOf("spike", StringComparison.OrdinalIgnoreCase) >= 0).ToArray();
        Require(spikes.Length > 0, "native expanded floor spikes are enabled on this endpoint");
        foreach (var spike in spikes) Log("AIR NATIVE SPIKE " + WallPath(spike.transform) + " " + spike.bounds);
        var wall = Physics2D.RaycastAll(new Vector2(108, 22), Vector2.left, 50, 1 << 8)
            .FirstOrDefault(h => !h.collider.isTrigger && h.normal.x > .9f && h.collider.GetComponentInParent<DamageHero>() == null);
        Require(wall.collider != null, "native climbable wall bounds available");
        var stand = new Vector3(wall.point.x + .8f, 23, 0);
        Log("AIR WALL " + WallPath(wall.collider.transform) + "=" + wall.point + " setup=" + stand);
        GameObject injectedSupport = null; var loseOnAcceptance = false;
        using var acceptanceLoss = new MonoMod.RuntimeDetour.Hook(typeof(CoopRuntime).GetMethod("Receive", BindingFlags.Instance | BindingFlags.NonPublic),
            new Action<Action<CoopRuntime, Message>, CoopRuntime, Message>((orig, owner, message) => {
                if (loseOnAcceptance && message.Kind == Kind.Revived) {
                    injectedSupport.SetActive(false); Physics2D.SyncTransforms(); loseOnAcceptance = false;
                    Log("AIR INJECTED SUPPORT LOSS AT ACCEPTED REVIVE");
                }
                orig(owner, message);
            }));
        foreach (var scenario in new[] { "guest", "host", "loss", "covered", "accepted-loss" }) {
            airRescueLive = false;
            var victimRole = scenario == "host" ? "host" : "guest";
            var victim = role == victimRole;
            hero.RegainControl(); hero.StartAnimationControl(); hero.ResetAirMoves();
            hero.cState.recoiling = hero.cState.invulnerable = false;
            hero.takeNoDamage = false; body.simulated = true;
            pd.SetInt("health", 5); pd.SetInt("healthBlue", 0); pd.SetBool("isInvincible", false);
            hero.AffectedByGravity(false); body.velocity = Vector2.zero;
            hero.transform.position = stand + Vector3.right * (victim ? 1.5f : 0);
            if (scenario == "loss" || scenario == "accepted-loss" || scenario == "covered") {
                var b = hero.GetComponent<Collider2D>().bounds;
                var h = b.extents.y - (b.center - hero.transform.position).y + .07f;
                injectedSupport = new GameObject("Aerial audit injected temporary support " + scenario) { layer = 8 };
                injectedSupport.transform.position = stand + Vector3.right * 1.5f + Vector3.down * (h + .25f);
                injectedSupport.AddComponent<BoxCollider2D>().size = new Vector2(6, .5f);
                Log("AIR INJECTED SUPPORT " + scenario + "=" + injectedSupport.transform.position);
            }
            Physics2D.SyncTransforms();
            yield return new WaitForSecondsRealtime(1);
            yield return Until(() => runtime.Peers.Values.All(p => p.Life == Life.Alive && p.State?.Scene == ActiveScene), "both aerial presences alive");
            var landing = new object[] { hero, (Vector2)hero.transform.position, 20f, true, null, null };
            var ground = (bool)typeof(CoopRuntime).GetMethod("TryStoryLanding", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(runtime, landing);
            Log("AIR LANDING preflight supported=" + ground + " point=" + landing[4] + " support=" + landing[5]);
            Require(ground == (injectedSupport != null), "native air phase or explicit injected support matches preflight " + scenario);
            yield return AirTogether(scenario + ".ready");
            var serial = runtime.DeathSerial; var quota = runtime.RescuesUsed;
            if (victim) {
                pd.SetInt("health", 1);
                hero.TakeDamage(null, GlobalEnums.CollisionSide.left, 1, 1);
                yield return Until(() => runtime.Down || runtime.Returning, "native aerial lethal hit intercepted", 8);
                yield return new WaitForSecondsRealtime(2);
                Require(runtime.Down && !runtime.Returning && !Field<bool>(runtime, "giveUpRequested"),
                    "no formal death while living teammate can rescue in the air");
                Log("AIR DOWN " + scenario + "=" + runtime.DeathPosition);
            } else yield return Until(() => runtime.Peers.Values.Any(p => p.Life == Life.Down), "aerial teammate down");
            yield return AirTogether(scenario + ".down");
            GameObject coveringHazard = null;
            if (scenario == "loss") {
                injectedSupport.SetActive(false); Physics2D.SyncTransforms();
                Log("AIR INJECTED SUPPORT LOSS DURING DOWN");
            } else if (scenario == "covered") {
                var anchor = victim ? runtime.DeathPosition : new Vector3(runtime.Peers.Single().Value.State.X, runtime.Peers.Single().Value.State.Y, 0);
                coveringHazard = new GameObject("Aerial audit injected persistent spike at old anchor");
                coveringHazard.transform.position = anchor;
                coveringHazard.AddComponent<BoxCollider2D>().size = new Vector2(.8f, 2);
                coveringHazard.GetComponent<Collider2D>().isTrigger = true;
                var damage = coveringHazard.AddComponent<DamageHero>(); damage.hazardType = 1; damage.damageDealt = 1;
                injectedSupport.SetActive(false); Physics2D.SyncTransforms();
                Log("AIR INJECTED PERSISTENT SPIKE COVERS OLD ANCHOR " + anchor);
            } else if (scenario == "accepted-loss") loseOnAcceptance = true;
            if ((scenario == "loss" || scenario == "covered") && victim) {
                yield return Until(() => Field<uint>(runtime, "rescueAnchorAccepted") > 0, "confirmed ground-to-air relocation " + scenario, 8);
                Require(Field<bool>(runtime, "rescueAirAnchor") && runtime.Down && runtime.DeathSerial == serial + 1 && runtime.RescuesUsed == quota,
                    "same death reanchors into safe air without rescue charge " + scenario);
            }
            yield return AirTogether(scenario + ".anchor");
            airRescueLive = true;
            if (!victim) {
                var peer = runtime.Peers.Single(); var start = hero.transform.position;
                hero.AffectedByGravity(true);
                if (scenario == "accepted-loss") {
                    var target = peer.Value.State.X - .8f;
                    using (new MonoMod.RuntimeDetour.Hook(typeof(HeroController).GetMethod("LookForInput", BindingFlags.Instance | BindingFlags.NonPublic),
                        new Action<Action<HeroController>, HeroController>((orig, self) => {
                            orig(self); if (self.acceptingInput) self.move_input = Math.Abs(self.transform.position.x - target) < .1f ? 0 : Math.Sign(target - self.transform.position.x);
                        })))
                        yield return Until(() => Math.Abs(hero.transform.position.x - target) < .15f, "native supported walk before acceptance-only support loss", 4);
                    hero.move_input = 0; hero.FaceRight(); hero.Attack(GlobalEnums.AttackDirection.normal);
                    yield return Until(() => peer.Value.Life == Life.Alive, "real supported nail accepted before injected support loss", 5);
                } else {
                using (new MonoMod.RuntimeDetour.Hook(typeof(InControl.OneAxisInputControl).GetProperty("IsPressed").GetGetMethod(),
                    new Func<Func<InControl.OneAxisInputControl, bool>, InControl.OneAxisInputControl, bool>((orig, self) =>
                        ReferenceEquals(self, InputHandler.Instance.inputActions.left) || orig(self))))
                using (new MonoMod.RuntimeDetour.Hook(typeof(HeroController).GetMethod("LookForInput", BindingFlags.Instance | BindingFlags.NonPublic),
                    new Action<Action<HeroController>, HeroController>((orig, self) => { orig(self); if (self.acceptingInput) self.move_input = -1; }))) {
                    yield return Until(() => hero.cState.wallSliding, "real native wall slide beside aerial shade", 5);
                    yield return Press(InputHandler.Instance.inputActions.jump);
                    var end = Time.realtimeSinceStartup + 5; var nextAttack = 0f;
                    while (peer.Value.Life != Life.Alive && Time.realtimeSinceStartup < end) {
                        var point = new Vector2(peer.Value.State.X, peer.Value.State.Y);
                        if (hero.cState.wallSliding && hero.transform.position.y < point.y - 1) yield return Press(InputHandler.Instance.inputActions.jump);
                        if (Vector2.Distance(hero.transform.position, point) < 3 && Time.realtimeSinceStartup > nextAttack) {
                            if (point.x >= hero.transform.position.x) hero.FaceRight(); else hero.FaceLeft();
                            hero.Attack(GlobalEnums.AttackDirection.normal); nextAttack = Time.realtimeSinceStartup + .45f;
                            Log("AIR NATIVE NAIL pos=" + hero.transform.position + " shade=" + point);
                        }
                        yield return null;
                    }
                    Require(peer.Value.Life == Life.Alive, "actual aerial nail collision rescues teammate");
                }
                }
                Require(Vector2.Distance(start, hero.transform.position) > .3f, "rescuer really moved after Down " + scenario);
                hero.move_input = 0;
            } else {
                yield return Until(() => !runtime.Down && !runtime.Returning && pd.health == 1 && hero.acceptingInput,
                    "aerial revive restores one health and native control", 12);
                Require(body.gravityScale > 0 && body.simulated, "aerial revive restores native gravity and simulation");
                using (new MonoMod.RuntimeDetour.Hook(typeof(InControl.OneAxisInputControl).GetProperty("IsPressed").GetGetMethod(),
                    new Func<Func<InControl.OneAxisInputControl, bool>, InControl.OneAxisInputControl, bool>((orig, self) =>
                        ReferenceEquals(self, InputHandler.Instance.inputActions.left) || orig(self))))
                using (new MonoMod.RuntimeDetour.Hook(typeof(HeroController).GetMethod("LookForInput", BindingFlags.Instance | BindingFlags.NonPublic),
                    new Action<Action<HeroController>, HeroController>((orig, self) => { orig(self); if (self.acceptingInput) self.move_input = -1; }))) {
                    yield return Until(() => hero.cState.wallSliding, "revived player really reaches safe native wall", 4);
                    Require(runtime.DeathSerial == serial + 1 && runtime.RescuesUsed == quota + 1 && !runtime.Down,
                        "one death and one rescue without immediate hazard death");
                }
                hero.move_input = 0;
            }
            // Stop only after each endpoint has proved its native airborne path.
            hero.AffectedByGravity(false); body.velocity = Vector2.zero;
            yield return AirTogether(scenario + ".rescued");
            if (injectedSupport != null) { Object.Destroy(injectedSupport); injectedSupport = null; }
            if (coveringHazard != null) Object.Destroy(coveringHazard);
        }
    }
}
