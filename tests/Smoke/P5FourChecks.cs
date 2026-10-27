using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using Hkmp;
using Hkmp.Game.Client.Entity;
using HollowConnect;
using HutongGames.PlayMaker.Actions;
using UnityEngine;
using Object = UnityEngine.Object;

namespace HollowConnect.Smoke;

public sealed partial class CoverageProbe {
    private bool P5FourCheck => !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("HOLLOWCONNECT_P5_FOUR"));
    private readonly HashSet<string> p5TitleStates = new();
    private string[] P5FourScenes() {
        var sequence = Resources.LoadAll<BossSequence>("GG").Single(s => System.Text.RegularExpressions.Regex.IsMatch(s.name, @"Tier 5$"));
        var native = Enumerable.Range(0, sequence.Count).Select(sequence.GetSceneAt).ToArray();
        Mark("P5 NATIVE SCENES " + string.Join(",", native));
        return (Environment.GetEnvironmentVariable("HOLLOWCONNECT_P5_FOUR_ONLY") ?? "GG_Ghost_Markoth,GG_Grey_Prince_Zote,GG_Traitor_Lord,GG_Uumuu").Split(',')
            .Select(prefix => native.First(s => s.StartsWith(prefix, StringComparison.Ordinal))).ToArray();
    }
    private void ObserveP5Four() {
        CoopMod.Settings.BossTargetingMode = 1;
        On.HutongGames.PlayMaker.Fsm.EnterState += (orig, fsm, state) => {
            if (fsm.GameObject != null && fsm.GameObject.name == "Grey Prince Title" && fsm.Name == "Control") {
                p5TitleStates.Add(state.Name);
                Mark("P5 TITLE " + state.Name);
            }
            orig(fsm, state);
        };
    }
    private GameObject P5Visible(Entity e) => manager.IsSceneHost ? e.Object.Host : e.Object.Client;
    private void P5Assert(bool ok, string message) {
        if (!ok) throw new Exception(message);
        Mark("PASS " + message);
    }
    private IEnumerator P5FourChecks(int round, string scene) {
        yield return new WaitForSecondsRealtime(5);
        var camera = GameCameras.instance.cameraController;
        var locks = Modding.ReflectionHelper.GetField<CameraController, List<CameraLockArea>>(camera, "lockZoneList");
        Mark("P5 CAMERA " + scene + " " + string.Join(",", locks.Select(a => a == null ? "null" : a.name)));
        if (scene.StartsWith("GG_Ghost_Markoth", StringComparison.Ordinal)) {
            var shields = Object.FindObjectsOfType<PlayMakerFSM>().Where(f => f.FsmName == "Control" && f.name.StartsWith("Markoth Shield", StringComparison.Ordinal)).ToArray();
            P5Assert(shields.Length > 0 && shields.All(f => EntityReferences.Owner(f.gameObject)?.Type == EntityType.MarkothShield), "Markoth has only managed active shields");
            P5Assert(Entities.Values.Where(e => e.Type == EntityType.MarkothShield).Count(e => P5Visible(e) != null && P5Visible(e).activeInHierarchy) == shields.Length, "Markoth active shield count matches synchronized lifetimes");
        } else if (scene == "GG_Grey_Prince_Zote") {
            P5Assert(!Entities.Values.Any(e => e.Object.Host != null && e.Object.Host.name.StartsWith("Grey Prince Title", StringComparison.Ordinal)), "Zote title stays under native presentation control");
            yield return Until(() => p5TitleStates.Contains("End"), "Zote title naturally reaches End after displaying adjectives");
            yield return new WaitForSecondsRealtime(3);
            var title = Object.FindObjectsOfType<PlayMakerFSM>(true).Single(f => f.gameObject.scene.name == scene && f.name == "Grey Prince Title" && f.FsmName == "Control");
            P5Assert(p5TitleStates.Contains("Main Title") && title.GetComponentsInChildren<PlayMakerFSM>(true).Where(f => f.FsmName == "color_fader").All(f => f.ActiveStateName is "Downed" or "Init" || !f.gameObject.activeInHierarchy), "Zote title text finishes fading in the same room");
        } else if (scene == "GG_Traitor_Lord") {
            P5Assert(!locks.Any(a => a != null && a.name.Contains("Intro")), "Traitor Lord native intro camera lock is released");
            P5Assert(Object.FindObjectsOfType<CameraLockArea>(true).Any(a => a.gameObject.scene.name == scene && a.name.Contains("Intro") && !a.gameObject.activeInHierarchy), "Traitor Lord intro controller deactivates itself");
        } else if (scene.StartsWith("GG_Uumuu", StringComparison.Ordinal)) {
            P5Assert(!locks.Any(a => a != null && a.name.Contains("Intro")), "Uumuu native intro camera lock is released");
            yield return P5OomaChecks(round);
        }
        Flag(role + ".p5four." + round);
        yield return Until(() => Has((host ? "guest" : "host") + ".p5four." + round), "both direct P5 checks " + scene);
    }
    private IEnumerator P5OomaChecks(int round) {
        // This fixture measures projectile ownership/motion, not player hazard
        // recovery. Finish any entry hazard natively, then exclude unrelated
        // combat contacts so an unavailable hitter cannot invalidate the aim.
        using var noContact = new MonoMod.RuntimeDetour.Hook(typeof(HeroController).GetMethod("TakeDamage"),
            new Action<Action<HeroController, GameObject, GlobalEnums.CollisionSide, int, int>, HeroController, GameObject, GlobalEnums.CollisionSide, int, int>(
                (orig, self, source, side, damage, hazard) => { }));
        yield return Until(() => !HeroController.instance.cState.hazardDeath && !HeroController.instance.cState.hazardRespawning, "native entry hazard recovery completes before Ooma aim fixture");
        HeroController.instance.transform.position = new Vector3(host ? 48 : 59, 113, 0);
        Freeze();
        yield return Until(() => Entities.Values.Count(e => e.Type == EntityType.Ooma && P5Visible(e) != null && P5Visible(e).activeInHierarchy) >= 2, "Uumuu registered native Ooma wave");
        Flag(role + ".ooma.ready");
        yield return Until(() => Has((host ? "guest" : "host") + ".ooma.ready"), "both endpoints received Ooma wave");
        for (var attack = 0; attack < 2; attack++) {
            var hitter = attack == 0 ? runtime.Peers.Single().Key : runtime.LocalId;
            if (host) {
                var jelly = Entities.Values.Where(e => e.Type == EntityType.Ooma && P5Visible(e) != null && P5Visible(e).activeInHierarchy).OrderBy(e => e.Id).First();
                jelly.Object.Host.transform.position = new Vector3(35, 113, 0);
                var source = attack == 0 ? runtime.PlayerObject(hitter) : HeroController.instance.gameObject;
                P5Assert(source != null, "actual attack source avatar exists");
                jelly.Object.Host.GetComponent<HealthManager>().Hit(new HitInstance { Source = source, AttackType = AttackTypes.Nail,
                    Direction = 180, DamageDealt = 100, Multiplier = 1, IgnoreInvulnerable = true });
                Flag("ooma.hit." + attack);
            } else yield return Until(() => Has("ooma.hit." + attack), "authority received " + (attack == 0 ? "guest" : "host") + " Ooma hit");
            Entity bomb = null;
            yield return Until(() => (bomb = Entities.Values.FirstOrDefault(e => e.Type == EntityType.OomaCore && P5Visible(e) != null && P5Visible(e).activeInHierarchy &&
                BossTargetCommitment.Decode(e.BossTargetReceipt?.Value, runtime.Session, CoopContext.Epoch).Player == (attack == 0 ? (host ? runtime.Peers.Single().Key : runtime.LocalId) : (host ? runtime.LocalId : runtime.Peers.Single().Key)))) != null,
                "native Ooma collision creates synchronized core aimed at hitter " + attack);
            Dump(P5Visible(bomb));
            if (host) {
                var action = bomb.BossTargetControl.States.SelectMany(s => s.Actions).FirstOrDefault(a => a is ChaseObject or ChaseObjectV2);
                P5Assert(action != null && Hkmp.Game.Client.Entity.BossTargeting.Resolve(HeroController.instance.gameObject, action) == (attack == 0 ? runtime.PlayerObject(hitter) : HeroController.instance.gameObject), "Ooma core resolves actual hitter independently of Uumuu target " + attack);
            } else {
                P5Assert(P5Visible(bomb).GetComponentsInChildren<PlayMakerFSM>(true).All(f => !f.enabled), "guest core follows authority instead of running another homing AI " + attack);
            }
            if (host) {
                yield return Until(() => bomb.BossTargetControl.ActiveStateName == "Chase", "core completes native Startle animation " + attack);
                Flag("ooma.chasing." + attack);
            } else yield return Until(() => Has("ooma.chasing." + attack), "authority core starts chasing " + attack);
            var before = P5Visible(bomb).transform.position;
            yield return new WaitForSecondsRealtime(.5f);
            P5Assert(P5Visible(bomb) == null || !P5Visible(bomb).activeInHierarchy || Vector2.Distance(before, P5Visible(bomb).transform.position) > .1f, "core trajectory advances on this endpoint " + attack);
            Flag(role + ".ooma." + attack);
            yield return Until(() => Has((host ? "guest" : "host") + ".ooma." + attack), "both endpoints checked core " + attack);
        }
    }
}
