using System;
using System.Collections;
using System.Linq;
using System.Reflection;
using Hkmp;
using Hkmp.Game.Client.Entity;
using HollowConnect;
using HollowConnect.Core;
using MonoMod.RuntimeDetour;
using UnityEngine;

namespace HollowConnect.Smoke;

public sealed partial class CoverageProbe {
    private bool RadianceSpectatorCheck => !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("HOLLOWCONNECT_RADIANCE_SPECTATOR"));
    private bool IsRadianceSpectator => role == Environment.GetEnvironmentVariable("HOLLOWCONNECT_RADIANCE_SPECTATOR");
    private IEnumerator RadiancePrepareSpectatorReturn() {
        // A directly loaded synthetic Godhome fight has no statue/door return
        // context. Visit the real lobby and use its native, renamed return gate.
        PlayerData.instance.SetVariable("bossDoorStateTier1", new BossSequenceDoor.Completion { unlocked = true });
        HeroController.instance.AffectedByGravity(true);
        Enter("GG_Atrium", "door_dreamEnter");
        yield return Until(() => In("GG_Atrium"), "native spectator Godhome return lobby visited");
        yield return null;
        var door = UnityEngine.Object.FindObjectsOfType<BossSequenceDoor>().First(d => d.playerDataString == "bossDoorStateTier1");
        if (door.dreamReturnGate == null) throw new Exception("Native spectator Godhome return gate missing");
        PlayerData.instance.SetString("dreamReturnScene", "GG_Atrium");
        PlayerData.instance.SetString("bossReturnEntryGate", door.dreamReturnGate.name);
        Mark("PASS native spectator Godhome personal return gate prepared");
        Freeze();
    }
    private IEnumerator RadianceStartSpectator(int round) {
        if (IsRadianceSpectator) {
            var hero = HeroController.instance;
            PlayerData.instance.SetInt("health", 0);
            hero.StartCoroutine((IEnumerator)typeof(HeroController).GetMethod("Die", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(hero, null));
            yield return RadianceWait(() => runtime.Down && runtime.Spectating, "actual death enters spectator before ascent");
        } else yield return RadianceWait(() => runtime.Peers.Values.Single().Life == Life.Spectating, "server observes spectator before ascent");
        yield return RadianceTogether(round, "spectator entered");
    }
    private IEnumerator RadianceJumpInput() {
        var action = InputHandler.Instance.inputActions.jump;
        var frame = Time.frameCount + 1;
        using (new Hook(typeof(InControl.OneAxisInputControl).GetProperty("WasPressed").GetGetMethod(),
            new Func<Func<InControl.OneAxisInputControl, bool>, InControl.OneAxisInputControl, bool>((orig, control) =>
                ReferenceEquals(control, action) ? Time.frameCount == frame : orig(control))))
        using (new Hook(typeof(InControl.OneAxisInputControl).GetProperty("IsPressed").GetGetMethod(),
            new Func<Func<InControl.OneAxisInputControl, bool>, InControl.OneAxisInputControl, bool>((orig, control) =>
                ReferenceEquals(control, action) ? Time.frameCount >= frame : orig(control))))
            yield return new WaitForSecondsRealtime(.2f);
    }
    private IEnumerator RadianceSpectatorAscent(int round, Entity[] steps) {
        var camera = GameCameras.instance.cameraController;
        var minimumCameraY = camera.transform.position.y;
        var maximumCameraY = minimumCameraY;
        var firstHeight = float.NaN;
        var samples = 0;
        foreach (var step in steps.OrderBy(e => e.Object.Host.transform.position.y)) {
            var key = "spectator-step-" + step.Id;
            if (!IsRadianceSpectator) {
                // Fixture setup places the living player on each native step.
                // Then real native jump input/physics moves them vertically;
                // neither endpoint's camera or spectator position is set here.
                var go = manager.IsSceneHost ? step.Object.Host : step.Object.Client;
                var support = go.GetComponentsInChildren<Collider2D>(true).Where(RadiancePlatform).OrderByDescending(c => c.bounds.max.y).First();
                var hero = HeroController.instance;
                var feet = hero.GetComponent<Collider2D>().bounds;
                hero.transform.position = new Vector3(support.bounds.center.x, support.bounds.max.y + hero.transform.position.y - feet.min.y + .1f, hero.transform.position.z);
                hero.GetComponent<Rigidbody2D>().velocity = Vector2.zero; hero.AffectedByGravity(true);
                Physics2D.SyncTransforms();
                yield return RadianceWait(() => RadianceSupported() && hero.acceptingInput, "living player supported on ascent step " + step.Id, 6);
                var startY = hero.transform.position.y;
                yield return RadianceJumpInput();
                yield return RadianceWait(() => hero.transform.position.y > startY + 1, "native jump rises above ascent step " + step.Id, 4);
                Flag(key + ".jumping");
                yield return RadianceWait(() => Has(key + ".sampled"), "spectator sampled native jump " + step.Id, 6);
                yield return RadianceWait(() => RadianceSupported(), "native jump lands on ascent platform " + step.Id, 8);
                Freeze();
            } else {
                yield return RadianceWait(() => Has(key + ".jumping"), "living teammate jumps on ascent step " + step.Id, 12);
                var peer = runtime.Peers.Single();
                yield return RadianceWait(() => {
                    var avatar = runtime.PlayerObject(peer.Key);
                    if (avatar == null) return false;
                    var view = camera.cam.WorldToViewportPoint(avatar.transform.position);
                    return view.z > 0 && view.x > .05f && view.x < .95f && view.y > .05f && view.y < .95f;
                }, "spectator camera keeps jumping teammate visible " + step.Id, 5);
                for (var i = 0; i < 12; i++) {
                    var avatar = runtime.PlayerObject(peer.Key);
                    var view = camera.cam.WorldToViewportPoint(avatar.transform.position);
                    if (!runtime.Spectating || !runtime.Down || view.z <= 0 || view.x <= 0 || view.x >= 1 || view.y <= 0 || view.y >= 1)
                        throw new Exception("Spectator lost ascent jumper: " + step.Id + " viewport=" + view + " camera=" + camera.transform.position);
                    minimumCameraY = Mathf.Min(minimumCameraY, camera.transform.position.y);
                    maximumCameraY = Mathf.Max(maximumCameraY, camera.transform.position.y);
                    if (float.IsNaN(firstHeight)) firstHeight = camera.transform.position.y;
                    samples++;
                    yield return null;
                }
                Mark("PASS spectator ascent sample step=" + step.Id + " camera=" + camera.transform.position + " target=" + runtime.PlayerObject(peer.Key).transform.position);
                if (samples == 12 || step == steps.OrderBy(e => e.Object.Host.transform.position.y).Last())
                    yield return RadianceCapture("spectator-ascent-" + step.Id);
                Flag(key + ".sampled");
            }
        }
        if (IsRadianceSpectator) {
            if (maximumCameraY - minimumCameraY < 25 || samples < steps.Length * 12)
                throw new Exception("Spectator camera did not traverse full ascent: range=" + (maximumCameraY - minimumCameraY) + " samples=" + samples);
            Mark("PASS spectator full ascent camera range=" + minimumCameraY + ".." + maximumCameraY + " samples=" + samples + " steps=" + steps.Length);
            typeof(CoopRuntime).GetMethod("RequestPersonalReturn", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(runtime, null);
            var scene = CoopContext.LocalScene;
            yield return Until(() => !runtime.Down && !runtime.Spectating && !runtime.Returning && !runtime.Resetting && runtime.Playable &&
                CoopContext.LocalScene != scene && !GameManager.instance.IsInSceneTransition && GameCameras.instance.cameraController.mode != CameraController.CameraMode.FROZEN,
                "spectator native return releases ascent camera and lock");
            var returnedHero = HeroController.instance;
            if (PlayerData.instance.GetBool("atBench")) {
                yield return Until(() => UnityEngine.Object.FindObjectsOfType<PlayMakerFSM>().Any(f =>
                    f.FsmName == "Bench Control" && f.ActiveStateName == "Resting"), "native returned bench finishes before jump input");
                yield return RadianceJumpInput();
            } else {
                var wake = returnedHero.GetComponents<PlayMakerFSM>().FirstOrDefault(f => f.FsmName == "Dream Return");
                if (wake != null) {
                    yield return Until(() => returnedHero.acceptingInput || wake.ActiveStateName == "Ready", "native dream wake reaches input prompt");
                    if (!returnedHero.acceptingInput) yield return RadianceJumpInput();
                }
            }
            yield return Until(() => returnedHero.acceptingInput && !returnedHero.controlReqlinquished &&
                returnedHero.GetComponent<Rigidbody2D>().simulated && GameCameras.instance.cameraController.mode != CameraController.CameraMode.FROZEN,
                "native return restores playable input physics and camera after spectating");
            Flag("radiance.spectator.returned");
        } else yield return Until(() => Has("radiance.spectator.returned"), "spectator exits without moving surviving climber");
        Flag(role + ".spectator-ascent-checked");
        yield return Until(() => Has((host ? "guest" : "host") + ".spectator-ascent-checked"), "both spectator ascent checks complete");
    }
}
