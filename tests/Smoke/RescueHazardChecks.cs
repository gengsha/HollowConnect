using System;
using System.Collections;
using System.Linq;
using System.Reflection;
using HollowConnect;
using HollowConnect.Core;
using UnityEngine;
using Object = UnityEngine.Object;

namespace HollowConnect.Smoke;

public sealed partial class RescueProbe {
    private IEnumerator HazardRescueChecks() {
        var hero = HeroController.instance;
        var pd = PlayerData.instance;
        var body = hero.GetComponent<Rigidbody2D>();
        var other = host ? "guest" : "host";
        IEnumerator Together(string step) {
            Flag(role + ".hazard." + step);
            yield return Until(() => Has(other + ".hazard." + step), "both " + step);
        }
        if (pd.GetBool("atBench")) {
            var bench = Object.FindObjectsOfType<PlayMakerFSM>().Single(f => f.name == "RestBench" && f.FsmName == "Bench Control");
            yield return Until(() => bench.ActiveStateName == "Resting", "native starting bench ready");
            bench.Fsm.Event(bench.Fsm.ActiveState.Actions.OfType<HutongGames.PlayMaker.Actions.ListenForJump>().Single().wasPressed);
            yield return Until(() => !pd.GetBool("atBench") && hero.acceptingInput && !body.isKinematic, "native starting bench get-up");
        }
        foreach (var scene in new[] { "Fungus1_02", "GG_Vengefly" }) {
            GameManager.instance.BeginSceneTransition(new GameManager.SceneLoadInfo {
                SceneName = scene, EntryGateName = scene.StartsWith("GG_") ? "door_dreamEnter" : "left1",
                Visualization = GameManager.SceneLoadVisualizations.Default, WaitForSceneTransitionCameraFade = true
            });
            yield return Until(() => runtime.Playable && GameManager.instance.sceneName == scene && hero.acceptingInput &&
                !hero.cState.transitioning && engine.ClientManager.SceneSnapshotReady, "native hazard room ready " + scene);
            var hazardType = scene == "Fungus1_02" ? 3 : 2;
            Collider2D Hazard() => Object.FindObjectsOfType<DamageHero>().Where(d => d.gameObject.scene.name == scene &&
                d.hazardType == hazardType && d.enabled).SelectMany(d => d.GetComponents<Collider2D>())
                .Where(c => c.enabled && c.gameObject.activeInHierarchy).OrderBy(c => c.bounds.SqrDistance(hero.transform.position)).First();
            hero.takeNoDamage = true;
            var safe = hero.FindGroundPoint(pd.GetVector3("hazardRespawnLocation"), true);
            hero.transform.position = safe + Vector3.up * .1f;
            hero.AffectedByGravity(true); Physics2D.SyncTransforms();
            yield return Until(() => hero.cState.onGround, "native safe point grounded " + scene);
            safe = hero.transform.position;
            hero.SetHazardRespawn(safe, true);
            foreach (var victimRole in new[] { "guest", "host" }) {
                var step = scene + "." + victimRole;
                var victim = role == victimRole;
                pd.SetInt("health", 5); pd.SetInt("healthBlue", 0); pd.SetBool("isInvincible", false);
                pd.SetInt("geo", 321); pd.SetInt("geoPool", 67); pd.SetString("shadeScene", "Crossroads_10");
                pd.SetFloat("shadePositionX", 9); pd.SetFloat("shadePositionY", 13);
                hero.takeNoDamage = true; hero.cState.invulnerable = false;
                hero.transform.position = safe; body.velocity = Vector2.zero;
                hero.AffectedByGravity(true); Physics2D.SyncTransforms();
                yield return Together(step + ".ready");
                if (victim) {
                    // Ownership setup can retire a previous native/replica
                    // collider. Reacquire the active source for each collision.
                    var source = Hazard();
                    var hazardBounds = source.bounds;
                    Log("HAZARD SOURCE " + source.name + " type=" + hazardType + " bounds=" + hazardBounds + " safe=" + safe);
                    pd.SetInt("health", 1); hero.takeNoDamage = false;
                    // Keep the real native collider, HeroBox and TakeDamage path.
                    hero.transform.position = new Vector3(hazardBounds.center.x, hazardBounds.center.y, hero.transform.position.z);
                    Physics2D.SyncTransforms();
                    yield return Until(() => runtime.Down, "actual lethal hazard collision " + step, 10);
                    Log("HAZARD DOWN " + step + " anchor=" + runtime.DeathPosition + " source=" + hazardBounds);
                    Check(Vector2.Distance(runtime.DeathPosition, safe) < .2f, "rescue anchor uses native safe point " + step);
                    yield return Until(() => !runtime.Down && pd.health == 1, "one-health hazard rescue " + step);
                    yield return new WaitForSecondsRealtime(2);
                    Check(!runtime.Down && pd.health == 1 && hero.cState.onGround && hero.acceptingInput &&
                        !hero.cState.hazardDeath && !hero.cState.hazardRespawning, "rescued player stays alive and controllable " + step);
                    Check(Vector2.Distance(hero.transform.position, safe) < .3f, "rescue settles on original safe support " + step);
                    Check(pd.GetInt("geo") == 321 && pd.GetInt("geoPool") == 67 && pd.GetString("shadeScene") == "Crossroads_10" &&
                        pd.GetFloat("shadePositionX") == 9 && pd.GetFloat("shadePositionY") == 13, "rescue preserves personal shade and geo " + step);
                } else {
                    yield return Until(() => runtime.Peers.Values.Any(p => p.Life == Life.Down), "hazard Down received " + step);
                    var peer = runtime.Peers.Single();
                    var service = Field<ShadeService>(runtime, "shades");
                    yield return Until(() => service.Get(peer.Key) != null, "hazard rescue shade visible " + step);
                    var shade = service.Get(peer.Key).transform.position;
                    Log("HAZARD PEER " + step + " shade=" + shade);
                    Check(Vector2.Distance(shade, safe) < .2f, "remote shade uses native safe point " + step);
                    // The entry checkpoint is beside the left exit. Stand
                    // inside the room so the rescue swing cannot leave it.
                    hero.transform.position = shade + Vector3.right * 2;
                    hero.FaceLeft(); hero.Attack(GlobalEnums.AttackDirection.normal); Physics2D.SyncTransforms();
                    yield return Until(() => runtime.Peers[peer.Key].Life == Life.Alive, "actual nail rescue accepted " + step);
                }
                yield return Together(step + ".complete");
                Check(GameManager.instance.sceneName == scene, "rescue remains in the same room " + step);
            }
        }
    }
}
