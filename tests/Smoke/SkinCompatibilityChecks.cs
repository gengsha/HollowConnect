using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Hkmp.Game.Client;
using Hkmp.Game.Client.Skin;
using UnityEngine;
using UnityEngine.UI;
using Object = UnityEngine.Object;

namespace HollowConnect.Smoke;

public sealed partial class ProgressProbe {
    private static Type InstalledCustomKnight() => AppDomain.CurrentDomain.GetAssemblies()
        .Select(a => a.GetType("CustomKnight.SkinManager")).FirstOrDefault(t => t != null);

    private static string CompatibilitySkinFor(string playerRole) {
        var owner = Environment.GetEnvironmentVariable("HOLLOWCONNECT_SKIN_OWNER") ?? "host";
        if (owner != "both" && owner != playerRole) return "";
        return (playerRole == "guest" ? Environment.GetEnvironmentVariable("HOLLOWCONNECT_CUSTOM_SKIN_GUEST_ID") : null) ??
            Environment.GetEnvironmentVariable("HOLLOWCONNECT_CUSTOM_SKIN_ID");
    }

    private bool AtlasSkinCase => Environment.GetEnvironmentVariable("HOLLOWCONNECT_SKIN_ATLAS") == "1";

    private void SelectCompatibilitySkin(string id) {
        if (AtlasSkinCase) {
            ResourceCheck(engine.ClientManager.TrySelectAtlasSkin(id), "compatible skin selection: " + id);
        } else {
            InstalledCustomKnight().GetMethod("SetSkinById").Invoke(null, new object[] { string.IsNullOrEmpty(id) ? "Default" : id });
        }
    }

    private IEnumerator PrepareCompatibilitySkin() {
        var selectedSkin = CompatibilitySkinFor(role) ??
            (host ? "Lost Kin by teh_ches_noddle" : "");
        if (AtlasSkinCase) {
            CheckAtlasMenuSelection(selectedSkin ?? "");
            if (AtlasSkinPacks.Contains("损坏测试")) {
                ResourceCheck(!engine.ClientManager.TrySelectAtlasSkin("损坏测试"), "corrupt PNG does not replace the selected skin");
                ResourceCheck(Field<Hkmp.Game.Settings.ModSettings>(engine.ClientManager, "_modSettings").AtlasSkinId == selectedSkin,
                    "failed selection retains the previous skin ID");
            }
            yield return null;
            var expected = AtlasSkinPacks.GetTexture(selectedSkin ?? "", "Knight.png");
            if (expected != null) ResourceCheck(BodyAtlas(HeroController.instance.gameObject, "Idle") == expected, "local Idle atlas from " + selectedSkin);
            if (selectedSkin == "死亡细胞") {
                ResourceCheck(expected != null && expected.width == 4096 && expected.height == 4096, "original Dead Cells 4096×4096 PNG decoded");
                var sprint = AtlasSkinPacks.GetTexture(selectedSkin, "Sprint.png");
                ResourceCheck(sprint != null && BodyAtlas(HeroController.instance.gameObject, "Sprint") == sprint, "local Sprint atlas from Dead Cells");
                var hud = GameCameras.instance.hudCanvas.GetComponentsInChildren<tk2dSprite>(true).Single(sprite => sprite.name == "Health 1");
                ResourceCheck(hud.GetCurrentSpriteDef().material.mainTexture == AtlasSkinPacks.GetTexture(selectedSkin, "Hud.png"), "local native masks use the Dead Cells HUD atlas");
            }
            yield break;
        }
        var skinManager = InstalledCustomKnight();
        if (skinManager == null) {
            if (!string.IsNullOrEmpty(selectedSkin)) throw new Exception("Custom Knight missing on skin owner");
            yield break;
        }
#if HOLLOW_UNITY6
        On.TeamCherry.Cinematics.EmbeddedCinematicVideoPlayer.ctor += (orig, player, config) => {
#else
        On.XB1CinematicVideoPlayer.ctor += (orig, player, config) => {
#endif
            orig(player, config);
            var source = Field<object>(player, "videoPlayer");
            Mark("VIDEO constructed source=" + source.GetType().GetProperty("source").GetValue(source) + " url=" + source.GetType().GetProperty("url").GetValue(source));
        };
        skinManager.GetMethod("SetSkinById").Invoke(null, new object[] { string.IsNullOrEmpty(selectedSkin) ? "Default" : selectedSkin });
        yield return new WaitForSecondsRealtime(3);
        Mark("SKIN selected " + CustomKnightSkinBridge.GetCurrentSkinId());
        if (!string.IsNullOrEmpty(CompatibilitySkinFor(role)))
            ResourceCheck(CustomKnightSkinBridge.GetCurrentSkinId() == selectedSkin, "Custom Knight selects local skin " + selectedSkin);
    }

    private void CheckAtlasMenuSelection(string id) {
        global::Language.Language.SwitchLanguage(global::Language.LanguageCode.ZH);
        var menu = Field<Hkmp.Menu.ModMenu>(Modding.ModHooks.GetMod("HKMP"), "_modMenu");
        menu.CreateMenu(null);
        var settings = Field<MenuScreen>(menu, "_clientSettingsMenu");
        var option = settings.GetComponentsInChildren<MenuOptionHorizontal>(true).Single(o => o.name == "AtlasSkinOption");
        ResourceCheck(option.GetComponentsInChildren<UnityEngine.UI.Text>(true).Any(t => t.text == "兼容皮肤（本机）"), "Chinese compatible skin menu");
        var index = Array.IndexOf(option.optionList, id);
        if (id != "") ResourceCheck(index > 0, "skin pack discovered by its folder name " + id);
        option.SetOptionTo(id == "" ? 0 : index);
        option.menuSetting.customApplySetting(option.menuSetting, option.selectedOptionIndex);
        ResourceCheck(Field<Hkmp.Game.Settings.ModSettings>(engine.ClientManager, "_modSettings").AtlasSkinId == id, "menu selection saved by folder name");
    }

    private ClientPlayerData SkinPeer() => Field<Dictionary<ushort, ClientPlayerData>>(engine.ClientManager, "_playerData").Values
            .Single(p => p.IsInLocalScene && p.PlayerObject != null);

    private void CheckRemoteCustomSkin(string configuredSkin) {
        var remote = SkinPeer();
        if (remote.CustomSkinId != configuredSkin) {
            throw new Exception("Remote Custom Knight skin ID mismatch: expected " + configuredSkin + ", got " + remote.CustomSkinId);
        }

        var idleTexture = BodyAtlas(remote.PlayerObject, "Idle");
        var sprintTexture = BodyAtlas(remote.PlayerObject, "Sprint");
        var localHero = HeroController.instance.gameObject;
        var expectedIdle = CustomKnightSkinBridge.GetSkinTexture(configuredSkin, "Knight.png") ??
            CustomKnightSkinBridge.GetDefaultSkinTexture("Knight.png") ??
            (InstalledCustomKnight() == null ? BodyAtlas(localHero, "Idle") : null);
        var expectedSprint = CustomKnightSkinBridge.GetSkinTexture(configuredSkin, "Sprint.png") ??
            CustomKnightSkinBridge.GetDefaultSkinTexture("Sprint.png") ??
            (InstalledCustomKnight() == null ? BodyAtlas(localHero, "Sprint") : null);
        if (expectedIdle == null || idleTexture != expectedIdle || sprintTexture != expectedSprint) {
            string DescribeTexture(Texture texture) => texture == null ? "missing" : texture.name + "#" + texture.GetInstanceID();
            throw new Exception($"Incorrect remote textures for {configuredSkin}: Idle={DescribeTexture(idleTexture)} expected={DescribeTexture(expectedIdle)}, Sprint={DescribeTexture(sprintTexture)} expected={DescribeTexture(expectedSprint)}");
        }
        Mark("PASS remote Custom Knight body skin=" + configuredSkin + " idle=" + idleTexture.name + " sprint=" + sprintTexture.name);
    }

    private static Texture BodyAtlas(GameObject player, string clip) {
        var frame = player.GetComponent<tk2dSpriteAnimator>().GetClipByName(clip).frames[0];
        var definition = frame.spriteCollection.spriteDefinitions[frame.spriteId];
        return (definition.materialInst ?? definition.material)?.mainTexture;
    }

    private void CheckRemoteShriek(string skinId) {
        var expected = CustomKnightSkinBridge.GetSkinTexture(skinId, "VoidSpells.png");
        ResourceCheck(expected != null, "sample includes the Abyss Shriek atlas");
        var spells = HeroController.instance.spellControl.transform;
        var local = spells.GetComponentsInChildren<tk2dSprite>(true).SingleOrDefault(s => s.name == "Scr Heads 2");
        ResourceCheck(local != null && local.GetCurrentSpriteDef()?.material != null,
            "native Abyss Shriek sprite on " + spells.name);
        var localTexture = local.GetCurrentSpriteDef().material.mainTexture;
        var peer = SkinPeer();
        var animations = Field<Hkmp.Animation.AnimationManager>(engine.ClientManager, "_animationManager");
        animations.OnPlayerAnimationUpdate(peer.Id, (int)Hkmp.Animation.AnimationClip.Scream2, 0, null);
        var effect = peer.PlayerObject.GetComponentsInChildren<Hkmp.Animation.RemoteVisualOpacity>(true)
            .Single(v => v.name == "Scr Heads 2(Clone)");
        var renderer = effect.GetComponent<Renderer>();
        var properties = new MaterialPropertyBlock();
        renderer.GetPropertyBlock(properties);
        var rendered = properties.GetTexture("_MainTex") ?? renderer.sharedMaterial?.mainTexture;
        Mark("SKIN SHRIEK actual=" + rendered?.GetInstanceID() + " expected=" + expected.GetInstanceID() +
            " local=" + localTexture?.GetInstanceID());
        ResourceCheck(effect.PlayerId == peer.Id && rendered == expected,
            "remote Abyss Shriek uses its owner's skin atlas");
        ResourceCheck(local.GetCurrentSpriteDef().material.mainTexture == localTexture, "remote spell preserves local spell atlas");
    }

    private IEnumerator SkinCompatibilityChecks() {
        yield return Room("Crossroads_47");
        var remoteSkin = CompatibilitySkinFor(host ? "guest" : "host");
        if (!string.IsNullOrEmpty(remoteSkin)) CheckRemoteCustomSkin(remoteSkin);
        if (AtlasSkinCase) {
            var expected = AtlasSkinPacks.GetTexture(CompatibilitySkinFor(role), "Knight.png");
            var hero = HeroController.instance;
            ResourceCheck(expected != null && BodyAtlas(hero.gameObject, "Idle") == expected, "local body keeps its selected atlas after a room transition");
            var sprite = hero.GetComponent<tk2dSprite>();
            ResourceCheck(sprite.GetComponent<Renderer>().sharedMaterial.mainTexture == expected, "rendered local sprite uses the selected atlas after a room transition");
        }
        if (Environment.GetEnvironmentVariable("HOLLOWCONNECT_SKIN_SHRIEK") == "1" && (!AtlasSkinCase || !host)) CheckRemoteShriek(remoteSkin);
        if (AtlasSkinCase && host && Environment.GetEnvironmentVariable("HOLLOWCONNECT_RENDERED_TEST") == "1") {
            yield return new WaitForSecondsRealtime(.25f);
            ScreenCapture.CaptureScreenshot(System.IO.Path.Combine(root, "死亡细胞-双人实机.png"));
        }
        AnimationBoundaryChecks();
        if (Environment.GetEnvironmentVariable("HOLLOWCONNECT_SKIN_LIVE_SWITCH") == "1") {
            yield return ResourceBarrier("skin-before-switch");
            if (host) SelectCompatibilitySkin("");
            else {
                yield return Until(() => SkinPeer().CustomSkinId == "", "live Custom Knight change reaches existing remote avatar");
                CheckRemoteCustomSkin("");
            }
            yield return ResourceBarrier("skin-default");
            if (host) SelectCompatibilitySkin(CompatibilitySkinFor("host"));
            else {
                yield return Until(() => SkinPeer().CustomSkinId == remoteSkin, "live Custom Knight change restores selected skin");
                CheckRemoteCustomSkin(remoteSkin);
                var localId = AtlasSkinCase ? Field<Hkmp.Game.Settings.ModSettings>(engine.ClientManager, "_modSettings").AtlasSkinId : CustomKnightSkinBridge.GetCurrentSkinId();
                ResourceCheck(localId == CompatibilitySkinFor(role),
                    "remote skin changes preserve local selection");
            }
            yield return ResourceBarrier("skin-restored");
        }
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
