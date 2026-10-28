using System;
using System.Collections;
using System.Linq;
using System.Reflection;
using UnityEngine;
using Object = UnityEngine.Object;

namespace HollowConnect.Smoke;

public sealed partial class ProgressProbe {
    private IEnumerator PrepareCompatibilitySkin() {
        var selected = Environment.GetEnvironmentVariable("HOLLOWCONNECT_SKIN_OWNER") ?? "host";
        var skinManager = AppDomain.CurrentDomain.GetAssemblies().Select(a => a.GetType("CustomKnight.SkinManager")).FirstOrDefault(t => t != null);
        if (skinManager == null) {
            if (selected == role || selected == "both") throw new Exception("Custom Knight missing on skin owner");
            yield break;
        }
        On.XB1CinematicVideoPlayer.ctor += (orig, player, config) => {
            orig(player, config);
            var source = Field<object>(player, "videoPlayer");
            Mark("VIDEO constructed source=" + source.GetType().GetProperty("source").GetValue(source) + " url=" + source.GetType().GetProperty("url").GetValue(source));
        };
        skinManager.GetMethod("SetSkinById").Invoke(null, new object[] { selected == role || selected == "both" ? "Lost Kin by teh_ches_noddle" : "Default" });
        yield return new WaitForSecondsRealtime(3);
        Mark("SKIN selected " + skinManager.GetMethod("GetCurrentSkin").Invoke(null, null));
    }

    private IEnumerator SkinCompatibilityChecks() {
        yield return Room("Crossroads_47");
        AnimationBoundaryChecks();
        if (Environment.GetEnvironmentVariable("HOLLOWCONNECT_SKIN_CASE") == "boundaries") yield break;
        var stag = Object.FindObjectsOfType<PlayMakerFSM>().Single(f => f.name == "Stag" && f.FsmName == "Stag Control");
        stag.SendEvent("STAG BELL RUNG");
        yield return Until(() => stag.ActiveStateName == "Idle" || stag.ActiveStateName == "In Range", "native Stag arrives", 15);
        foreach (var fsm in Object.FindObjectsOfType<PlayMakerFSM>(true).Where(f => f.gameObject.scene.name == "Crossroads_47" &&
            (f.name.Contains("Stag") || f.FsmName.Contains("Stag")))) Dump(fsm);
        Mark("SKIN PREFLIGHT stag=" + stag.ActiveStateName + " position=" + stag.transform.position);
        if (Environment.GetEnvironmentVariable("HOLLOWCONNECT_SKIN_CASE") == "inspect") yield break;
        for (var ride = 0; ride < 6; ride++) {
            if (ride != 0) stag = Object.FindObjectsOfType<PlayMakerFSM>().Single(f => f.name == "Stag" && f.FsmName == "Stag Control");
            var hero = HeroController.instance;
            hero.transform.position = stag.transform.position + new Vector3(1.5f, 0, 0);
            hero.AffectedByGravity(true); hero.RegainControl(); hero.StartAnimationControl();
            stag.SetState("Take Control");
            yield return Until(() => stag.ActiveStateName == "Open map", "native Stag destination menu", 15);
            var menu = Object.FindObjectsOfType<PlayMakerFSM>().Single(f => f.name == "Stag Map(Clone)" && f.FsmName == "Control");
            yield return Until(() => menu.ActiveStateName == "Up", "native Stag menu accepts selection", 10);
            var list = Object.FindObjectsOfType<PlayMakerFSM>().Single(f => f.name == "UI List Stag" && f.FsmName == "ui_list");
            list.FsmVariables.FindFsmString("Current Selection").Value = ride % 2 == 0 ? "Dirtmouth" : "Crossroads";
            list.SendEvent("SELECTION MADE");
            yield return Until(() => UnityEngine.SceneManagement.SceneManager.GetActiveScene().name == "Cinematic_Stag_travel", "native cinematic entered ride " + ride, 20);
            var sequence = Object.FindObjectOfType<CinematicSequence>();
            yield return Until(() => sequence != null && sequence.VideoPlayer != null, "native cinematic player created", 10);
            var source = Field<object>(sequence.VideoPlayer, "videoPlayer");
            var sourceType = source.GetType();
            var maxFrame = -1L; var skipped = false;
            var until = Time.realtimeSinceStartup + 35;
            while (UnityEngine.SceneManagement.SceneManager.GetActiveScene().name == "Cinematic_Stag_travel" && Time.realtimeSinceStartup < until) {
                if ((Object)source != null) maxFrame = Math.Max(maxFrame, (long)sourceType.GetProperty("frame").GetValue(source));
                if (ride % 2 == 1 && maxFrame > 2 && !skipped) {
                    var travel = Object.FindObjectOfType<global::StagTravel>();
                    travel.StartCoroutine(travel.Skip()); skipped = true;
                }
                yield return null;
            }
            if (maxFrame < 1) throw new Exception("Video did not decode frames, ride=" + ride + " maxFrame=" + maxFrame);
            Mark("PASS VIDEO ride=" + ride + " decodedFrame=" + maxFrame + " skipped=" + skipped);
            var destination = ride % 2 == 0 ? "Room_Town_Stag_Station" : "Crossroads_47";
            yield return Until(() => AliveScene(destination), "native arrival after cinematic " + ride, 30);
            Freeze();
            yield return ResourceBarrier("skin-ride-" + ride);
            yield return Until(() => VisiblePeer() && engine.ClientManager.SceneSnapshotReady, "peer visible after cinematic " + ride, 15);
            if (!runtime.Connected || runtime.Peers.Count != 1) throw new Exception("Room lost after cinematic");
        }
    }

    private void AnimationBoundaryChecks() {
        var animator = HeroController.instance.GetComponent<tk2dSpriteAnimator>();
        var nativeClip = animator.GetClipByName("Idle");
        var savedEvents = animator.AnimationEventTriggered;
        tk2dSpriteAnimationClip Clip(int length) => new tk2dSpriteAnimationClip {
            name = "Idle", fps = 12, wrapMode = tk2dSpriteAnimationClip.WrapMode.Loop,
            frames = Enumerable.Range(0, length).Select(_ => new tk2dSpriteAnimationFrame {
                spriteCollection = nativeClip.frames[0].spriteCollection, spriteId = nativeClip.frames[0].spriteId
            }).ToArray()
        };
        try {
            animator.AnimationEventTriggered = null;
            var replacement = Clip(6);
            // Native tk2d accepts loop offsets at/past the final frame. A skin
            // or animation addon may resume a shorter replacement this way.
            animator.Play(replacement, .5f, 12);
            ResourceCheck(animator.CurrentFrame == 0, "skin loop resumes at wrapped frame without network hook exception");
            var process = typeof(tk2dSpriteAnimator).GetMethod("ProcessEvents", BindingFlags.NonPublic | BindingFlags.Instance);
            process.Invoke(animator, new object[] { 2, 1, 1 });
            process.Invoke(animator, new object[] { 0, 2, 0 });
            process.Invoke(animator, new object[] { -100, 100, 1 });
            ResourceCheck(true, "network observer rejects non-traversable animation event ranges");
            var oldClip = Clip(3); oldClip.frames[1].triggerEvent = true;
            var newClip = Clip(1); var calls = 0;
            animator.AnimationEventTriggered = (self, clip, frame) => { calls++; self.Play(newClip, 0, 12); };
            animator.Play(oldClip, 0, 12);
            process.Invoke(animator, new object[] { 0, 2, 1 });
            ResourceCheck(calls == 1 && animator.CurrentClip == newClip, "native frame callback may replace clip with a shorter skin animation");
        } finally {
            animator.AnimationEventTriggered = savedEvents;
            animator.Play(nativeClip, 0, nativeClip.fps);
        }
    }
}
