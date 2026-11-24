using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.IO;
using Hkmp.Menu;
using System.Reflection;
using Hkmp.Game.Client;
using Hkmp.Game.Client.Skin;
using UnityEngine;
using UnityEngine.EventSystems;
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
    private bool ManagerCase => Environment.GetEnvironmentVariable("HOLLOWCONNECT_SKIN_MANAGER") == "1";
    private SkinLibraryMenu _skinMenu;
    private MenuScreen _skinSettingsMenu;

    private void SelectCompatibilitySkin(string id) {
        if (AtlasSkinCase) {
            SelectFromSkinMenu(id);
        } else {
            InstalledCustomKnight().GetMethod("SetSkinById").Invoke(null, new object[] { string.IsNullOrEmpty(id) ? "Default" : id });
        }
    }

    private IEnumerator PrepareCompatibilitySkin() {
        var selectedSkin = CompatibilitySkinFor(role) ??
            (host ? "Lost Kin by teh_ches_noddle" : "");
        if (AtlasSkinCase) {
            if (ManagerCase) {
                Field<Hkmp.Game.Settings.ModSettings>(engine.ClientManager, "_modSettings").AtlasSkinId = null;
                engine.ClientManager.RescanSkins();
                ResourceCheck(engine.ClientManager.LocalDisplaySkinId == "", "no automatic dress-up before choosing a skin");
            }
            OpenSkinFixture();
            SelectFromSkinMenu(selectedSkin ?? "");
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

    private static string SkinButtonLabel(MenuButton button) => button.GetComponentInChildren<Text>(true).text;

    private void OpenSkinFixture() {
        global::Language.Language.SwitchLanguage(global::Language.LanguageCode.ZH);
        var menu = Field<ModMenu>(Modding.ModHooks.GetMod("HKMP"), "_modMenu");
        menu.CreateMenu(null);
        var settings = Field<MenuScreen>(menu, "_clientSettingsMenu");
        _skinSettingsMenu = settings;
        _skinMenu = Field<SkinLibraryMenu>(menu, "_skinLibrary");
        var entry = settings.GetComponentsInChildren<MenuButton>(true).Single(button => button.name == "SkinManager");
        ResourceCheck(SkinButtonLabel(entry) == "皮肤管理器", "localized skin manager entry");
        foreach (var name in new[] { "SkinOption", "TeamOption" }) {
            var locked = settings.GetComponentsInChildren<MenuOptionHorizontal>(true).Single(option => option.name == name);
            ResourceCheck(locked.optionList.Length == 1 && locked.optionText.text.Contains("已锁定"),
                $"{name} is read-only (options={locked.optionList.Length}, text={locked.optionText.text})");
        }
        // Build the real page without entering a pause menu during the room fixture.
        typeof(SkinLibraryMenu).GetMethod("CreateLibrary", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(_skinMenu, null);
        PressSkinButton(_skinMenu, "_library", b => b.name == "RescanSkins");
        if (ManagerCase) {
            ResourceCheck(SkinChoiceIds.SequenceEqual(new[] { "" }.Concat(AtlasSkinPacks.GetIds())) &&
                SkinOption.selectedOptionIndex == 0 && SkinOption.optionText.text.Contains("原版外观"),
                "carousel starts on original, followed by scanned names" +
                $" (ids={string.Join("|", SkinChoiceIds)}, index={SkinOption.selectedOptionIndex}, value={SkinOption.optionText.text})");
        }
    }

    private static void PressSkinButton(SkinLibraryMenu menu, string screen, Func<MenuButton, bool> find) {
        var button = Field<MenuScreen>(menu, screen).GetComponentsInChildren<MenuButton>(true).Single(find);
        button.submitAction(button);
    }

    private MenuOptionHorizontal SkinOption => Field<MenuOptionHorizontal>(_skinMenu, "_skinOption");
    private string[] SkinChoiceIds => Field<string[]>(_skinMenu, "_ids");

    private void SelectFromSkinMenu(string id) {
        // Offline fixture dispatches the same one-step setting changes as native controls.
        var option = SkinOption;
        var choices = SkinChoiceIds;
        ResourceCheck(Array.IndexOf(choices, id) >= 0, "skin appears in selector " + id);
        for (var count = 0; count < choices.Length && engine.ClientManager.LocalDisplaySkinId != id; count++) {
            var next = (option.selectedOptionIndex + 1) % choices.Length;
            option.SetOptionTo(next);
            option.menuSetting.customApplySetting(option.menuSetting, next);
        }
        var name = id.Length == 0 ? "原版外观" : AtlasSkinPacks.DisplayName(id);
        ResourceCheck(engine.ClientManager.LocalDisplaySkinId == id && option.optionText.text.Contains(name),
            "skin manager applies and names local selection " + id);
        ResourceCheck(Field<Hkmp.Game.Settings.ModSettings>(engine.ClientManager, "_modSettings").AtlasSkinId == id ||
            id.Length == 0 && engine.ClientManager.SavedAtlasId.Length == 0, "menu stores the stable folder identifier");
    }

    private MenuOptionHorizontal TeammateSkinToggle => Field<MenuOptionHorizontal>(_skinMenu, "_showTeammateSkins");

    private IEnumerator SkinManagerRoomChecks(string remoteSkin) {
        var toggle = TeammateSkinToggle;
        ResourceCheck(toggle.selectedOptionIndex == 1 && engine.ClientManager.ShowTeammateSkins,
            "teammate skins are visible by default");
        if (!host) {
            var localSkin = engine.ClientManager.LocalDisplaySkinId;
            toggle.menuSetting.customApplySetting(toggle.menuSetting, 0);
            CheckRemoteBodyDefault();
            ResourceCheck(!engine.ClientManager.ShowTeammateSkins && engine.ClientManager.RoomAllowsSkins &&
                SkinPeer().CustomSkinId == remoteSkin && engine.ClientManager.LocalDisplaySkinId == localSkin,
                "local display switch retains the equipped skin, received ID, and room rule");
            if (Environment.GetEnvironmentVariable("HOLLOWCONNECT_SKIN_SHRIEK") == "1")
                CheckRemoteShriek(remoteSkin, showCustom: false);
        }
        yield return ResourceBarrier("teammate-skins-hidden-on-guest");
        if (host) CheckRemoteCustomSkin(remoteSkin);
        yield return ResourceBarrier("teammate-skins-still-visible-on-host");
        if (!host) {
            toggle.menuSetting.customApplySetting(toggle.menuSetting, 1);
            CheckRemoteCustomSkin(remoteSkin);
            if (Environment.GetEnvironmentVariable("HOLLOWCONNECT_SKIN_SHRIEK") == "1") CheckRemoteShriek(remoteSkin);
        }
        yield return ResourceBarrier("skin-manager-room-ready");
        if (host) ChangeSharing(false);
        yield return Until(() => !engine.ClientManager.RoomAllowsSkins, "room skin sharing turned off");
        CheckRemoteBodyDefault();
        if (host) SelectFromSkinMenu("");
        yield return ResourceBarrier("skin-manager-room-disabled");
        if (host) ChangeSharing(true);
        yield return Until(() => engine.ClientManager.RoomAllowsSkins, "room skin sharing turned back on");
        yield return Until(() => SkinPeer().CustomSkinId == (host ? remoteSkin : ""), "latest local choice shared again");
        CheckRemoteCustomSkin(host ? remoteSkin : "");
        yield return ResourceBarrier("skin-manager-room-enabled");
        if (host) SelectFromSkinMenu(CompatibilitySkinFor("host"));
        yield return Until(() => SkinPeer().CustomSkinId == remoteSkin, "named selection restored");
        yield return ResourceBarrier("skin-manager-before-missing");
        if (!host) {
            var source = Path.Combine(AtlasSkinPacks.InstallFolder, remoteSkin);
            var inactive = Path.Combine(Path.GetDirectoryName(AtlasSkinPacks.InstallFolder), "probe-skin-manager-removed", remoteSkin);
            Directory.CreateDirectory(Path.GetDirectoryName(inactive));
            Directory.Move(source, inactive);
            try {
                PressSkinButton(_skinMenu, "_library", b => b.name == "RescanSkins");
                CheckRemoteBodyDefault();
                ResourceCheck(!AtlasSkinPacks.Contains(remoteSkin) && SkinPeer().CustomSkinId == remoteSkin,
                    "missing peer pack falls back while retaining the advertised ID");
            } finally {
                Directory.Move(inactive, source);
            }
            PressSkinButton(_skinMenu, "_library", b => b.name == "RescanSkins");
            CheckRemoteCustomSkin(remoteSkin);
        }
        yield return ResourceBarrier("skin-manager-rescan-complete");
        if (host && Environment.GetEnvironmentVariable("HOLLOWCONNECT_RENDERED_TEST") == "1") {
            yield return CheckNativeSkinNavigation();
        }
        yield return ResourceBarrier("skin-manager-menu-closed", 25);
    }

    private IEnumerator CheckNativeSkinNavigation() {
        var ui = UIManager.instance;
        ui.TogglePauseGame();
        yield return Until(() => GameManager.instance.isPaused, "native pause menu opens", 8);
        yield return new WaitForSecondsRealtime(.55f);
        ui.UIGoToDynamicMenu(_skinSettingsMenu);
        yield return Until(() => ui.currentDynamicMenu == _skinSettingsMenu, "native client settings open", 8);
        var events = UnityEngine.EventSystems.EventSystem.current;
        var submit = new UnityEngine.EventSystems.BaseEventData(events);
        var manager = _skinSettingsMenu.GetComponentsInChildren<MenuButton>(true).Single(b => b.name == "SkinManager");
        manager.OnSubmit(submit);
        var library = Field<MenuScreen>(_skinMenu, "_library");
        yield return Until(() => ui.currentDynamicMenu == library && library.gameObject.activeInHierarchy, "native skin manager opens from client settings", 8);
        var option = SkinOption;
        yield return Until(() => events.currentSelectedGameObject == option.gameObject, "native carousel takes focus", 8);
        var toggle = TeammateSkinToggle;
        ResourceCheck(library.defaultHighlight == option && option.navigation.selectOnDown == toggle &&
            toggle.navigation.selectOnUp == option, "skin carousel and teammate switch participate in native menu navigation");
        var help = library.GetComponentsInChildren<Text>(true).Single(t => t.name == "TeammateSkinsHelp");
        ResourceCheck(help.text.Contains("本机装有队友正在用的皮肤包") && help.text.Contains("缺少皮肤包时显示原版") &&
            Field<Text>(_skinMenu, "_notice").text.Length == 0, "menu shows the teammate pack requirement without routine status text");
        var originalChoice = engine.ClientManager.LocalDisplaySkinId;
        ResourceCheck(SkinChoiceIds[option.selectedOptionIndex] == originalChoice, "opening the menu restores the saved selection");
        yield return new WaitForSecondsRealtime(.25f);
        yield return CaptureSkinMenu("皮肤管理器.png");

        void Move(MoveDirection direction) => option.OnMove(new AxisEventData(events) { moveDir = direction });
        SelectFromSkinMenu("");
        RescueProbe.ClickMenuHalf(option, left: true);
        var last = SkinChoiceIds.Length - 1;
        ResourceCheck(option.selectedOptionIndex == last && engine.ClientManager.LocalDisplaySkinId == SkinChoiceIds[last],
            "left arrow click wraps from original to the last installed skin");
        RescueProbe.ClickMenuHalf(option, left: false);
        ResourceCheck(option.selectedOptionIndex == 0 && engine.ClientManager.LocalDisplaySkinId == "",
            "right arrow click wraps from the last skin to original");
        if (SkinChoiceIds[1] == "损坏测试") {
            Move(MoveDirection.Right);
            ResourceCheck(option.selectedOptionIndex == 2 && engine.ClientManager.LocalDisplaySkinId == SkinChoiceIds[2] &&
                Field<Text>(_skinMenu, "_notice").text.Contains("损坏测试"), "right movement skips unreadable pack and applies the next one");
            Move(MoveDirection.Left);
            ResourceCheck(option.selectedOptionIndex == 0 && engine.ClientManager.LocalDisplaySkinId == "" &&
                Field<Text>(_skinMenu, "_notice").text.Contains("损坏测试"), "left movement skips unreadable pack and wraps back to original");
        }
        SelectFromSkinMenu(originalChoice);
        PressSkinButton(_skinMenu, "_library", b => b.name == "RescanSkins");
        ResourceCheck(SkinChoiceIds[option.selectedOptionIndex] == originalChoice && engine.ClientManager.LocalDisplaySkinId == originalChoice,
            "rescan keeps the equipped skin selected by its identifier");
        toggle.Select();
        RescueProbe.ClickMenuHalf(toggle, left: true);
        ResourceCheck(library == ui.currentDynamicMenu && toggle.selectedOptionIndex == 0 &&
            toggle.optionText.text.Contains("关闭"), "left click switches teammate display off without opening another page");
        CheckRemoteBodyDefault();
        yield return new WaitForSecondsRealtime(.25f);
        yield return CaptureSkinMenu("队友皮肤关闭.png");
        toggle.OnMove(new AxisEventData(events) { moveDir = MoveDirection.Right });
        ResourceCheck(toggle.selectedOptionIndex == 1 && toggle.optionText.text.Contains("开启"), "right movement switches teammate display on");
        CheckRemoteCustomSkin(CompatibilitySkinFor("guest"));
        Field<MenuButton>(_skinMenu, "_back").OnSubmit(submit);
        yield return Until(() => ui.currentDynamicMenu == _skinSettingsMenu, "skin manager returns to client settings", 8);
        var team = engine.ClientManager.Team;
        foreach (var row in _skinSettingsMenu.GetComponentsInChildren<MenuOptionHorizontal>(true)
            .Where(option => option.name is "SkinOption" or "TeamOption")) {
            row.Select();
            RescueProbe.ClickMenuHalf(row, left: true);
            row.OnMove(new AxisEventData(events) { moveDir = MoveDirection.Right });
            ResourceCheck(row.optionList.Length == 1 && row.selectedOptionIndex == 0 && row.optionText.text.Contains("已锁定"),
                row.name + " stays locked after mouse and direction input in a room");
        }
        ResourceCheck(engine.ClientManager.Team == team && engine.ClientManager.LocalDisplaySkinId == originalChoice,
            "locked legacy controls preserve team and appearance");
        yield return new WaitForSecondsRealtime(.25f);
        yield return CaptureSkinMenu("HKMP客户端设置.png");
        ui.UIGoToPauseMenu();
        yield return new WaitForSecondsRealtime(.55f);
        ui.TogglePauseGame();
        yield return Until(() => !GameManager.instance.isPaused, "native pause menu resumes play", 8);
    }

    private IEnumerator CaptureSkinMenu(string filename) {
        yield return new WaitForEndOfFrame();
        var pixels = ScreenCapture.CaptureScreenshotAsTexture();
        try {
            File.WriteAllBytes(Path.Combine(root, filename), pixels.EncodeToPNG());
        } finally {
            Object.Destroy(pixels);
        }
    }

    private void CheckRemoteBodyDefault() {
        var remote = SkinPeer().PlayerObject;
        ResourceCheck(BodyAtlas(remote, "Idle") == CustomKnightSkinBridge.GetDefaultSkinTexture("Knight.png") &&
            BodyAtlas(remote, "Sprint") == CustomKnightSkinBridge.GetDefaultSkinTexture("Sprint.png"), "remote renders native Knight and Sprint atlases");
    }

    private void ChangeSharing(bool enable) {
        var settings = engine.ServerManager.InternalServerSettings.GetCopy();
        settings.AllowSkins = enable;
        engine.ServerManager.ApplyServerSettings(settings);
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

    private void CheckRemoteShriek(string skinId, bool showCustom = true) {
        var expected = showCustom ? CustomKnightSkinBridge.GetSkinTexture(skinId, "VoidSpells.png") :
            CustomKnightSkinBridge.GetDefaultSkinTexture("VoidSpells.png");
        ResourceCheck(expected != null, "Abyss Shriek atlas for current display mode");
        var spells = HeroController.instance.spellControl.transform;
        var local = spells.GetComponentsInChildren<tk2dSprite>(true).SingleOrDefault(s => s.name == "Scr Heads 2");
        ResourceCheck(local != null && local.GetCurrentSpriteDef()?.material != null,
            "native Abyss Shriek sprite on " + spells.name);
        var localTexture = local.GetCurrentSpriteDef().material.mainTexture;
        var peer = SkinPeer();
        Hkmp.Animation.RemoteVisualOpacity[] Effects() => peer.PlayerObject
            .GetComponentsInChildren<Hkmp.Animation.RemoteVisualOpacity>(true)
            .Where(visual => visual.name == "Scr Heads 2(Clone)").ToArray();
        var previousEffects = Effects();
        var animations = Field<Hkmp.Animation.AnimationManager>(engine.ClientManager, "_animationManager");
        animations.OnPlayerAnimationUpdate(peer.Id, (int)Hkmp.Animation.AnimationClip.Scream2, 0, null);
        var effects = Effects();
        ResourceCheck(effects.Except(previousEffects).Count() == 1, "the remote cast creates one Abyss Shriek effect");
        // Earlier casts can still be on screen. Check them as well as the new cast.
        foreach (var effect in effects) {
            var renderer = effect.GetComponent<Renderer>();
            var properties = new MaterialPropertyBlock();
            renderer.GetPropertyBlock(properties);
            var rendered = properties.GetTexture("_MainTex") ?? renderer.sharedMaterial?.mainTexture;
            Mark("SKIN SHRIEK actual=" + rendered?.GetInstanceID() + " expected=" + expected.GetInstanceID() +
                " local=" + localTexture?.GetInstanceID());
            ResourceCheck(effect.PlayerId == peer.Id && rendered == expected,
                showCustom ? "remote Abyss Shriek uses its owner's skin atlas" : "remote Abyss Shriek uses its original atlas with the switch off");
        }
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
        if (ManagerCase) yield return SkinManagerRoomChecks(remoteSkin);
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
