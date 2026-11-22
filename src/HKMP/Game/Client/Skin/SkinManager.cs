using System;
using System.Collections;
using System.Collections.Generic;
using Hkmp.Game.Settings;
using Hkmp.Util;
using UnityEngine;
using Logger = Hkmp.Logging.Logger;

namespace Hkmp.Game.Client.Skin;

/// <summary>
/// Class that manages skins for player objects.
/// </summary>
internal class SkinManager {
    private Dictionary<byte, PlayerSkin> _playerSkins;
    private PlayerSkin _defaultPlayerSkin;
    private byte _localSkinId;
    private string _localCustomSkinId = "";
    private bool _settingLocalCustomSkin;
    private ModSettings _settings;
    private LocalAtlasSkin _localAtlases;
    private HeroController _atlasHero;

    /// <summary>
    /// Raised when the local selection changes through Custom Knight or the atlas pack menu.
    /// </summary>
    internal event Action<string> CustomSkinChangedEvent;

    internal byte LocalSkinId => _localSkinId;
    private string SelectedAtlasId => AtlasSkinPacks.Contains(_settings?.AtlasSkinId) ? _settings.AtlasSkinId : "";
    internal string LocalCustomSkinId => CustomKnightSkinBridge.IsAvailable ? _localCustomSkinId : SelectedAtlasId;

    public SkinManager() {
        _playerSkins = new Dictionary<byte, PlayerSkin>();
    }

    /// <summary>
    /// Initialize the skin manager by loading all the built-in HKMP skins.
    /// </summary>
    public void Initialize(ModSettings settings) {
        _settings = settings;
        new SkinLoader().LoadAllSkins(ref _playerSkins);
        AtlasSkinPacks.Discover();
    }

    internal void RegisterLocalAtlasHook() => CustomHooks.HeroControllerStartAction += ApplyLocalAtlasSkinOnStart;

    internal bool TrySelectAtlasSkin(string id) {
        if (CustomKnightSkinBridge.IsAvailable || (!string.IsNullOrEmpty(id) && !AtlasSkinPacks.CanSelect(id))) return false;
        id ??= "";
        if (_settings.AtlasSkinId == id) return true;
        _settings.AtlasSkinId = id;
        ApplyLocalAtlasSkin();
        CustomSkinChangedEvent?.Invoke(id);
        return true;
    }

    private void ApplyLocalAtlasSkinOnStart() {
        if (CustomKnightSkinBridge.IsAvailable) return;
        ApplyLocalAtlasSkin();
        if (!string.IsNullOrEmpty(SelectedAtlasId)) {
            MonoBehaviourUtil.Instance.StartCoroutine(ApplyLocalAtlasesAfterStart(HeroController.instance));
        }
    }

    private IEnumerator ApplyLocalAtlasesAfterStart(HeroController hero) {
        // The HUD is attached by the camera after the hero has started.
        yield return null;
        if (hero != null && HeroController.instance == hero) ApplyLocalAtlasSkin();
    }

    private void ApplyLocalAtlasSkin() {
        var hero = HeroController.instance;
        if (hero == null) return;
        if (_atlasHero != hero) {
            _atlasHero = hero;
            _localAtlases = null;
        }
        if (_localAtlases == null && !AtlasSkinPacks.HasPacks) return;
        (_localAtlases ??= new LocalAtlasSkin()).Apply(hero, SelectedAtlasId);
    }

    /// <summary>
    /// Register hooks for skin-related operations.
    /// </summary>
    public void RegisterHooks() {
        CustomHooks.HeroControllerStartAction += HeroControllerOnStart;
        CustomKnightSkinBridge.Initialize(OnCustomKnightSkinChanged);

        // A connection can be created after the player has already entered the game.
        if (HeroController.instance != null) {
            HeroControllerOnStart();
        }
    }

    /// <summary>
    /// Deregister hooks for skin-related operations.
    /// </summary>
    public void DeregisterHooks() {
        CustomHooks.HeroControllerStartAction -= HeroControllerOnStart;
        CustomKnightSkinBridge.Deregister();
    }

    /// <summary>
    /// Callback method for when the HeroController is started so we can store the default skin and initialize sprites.
    /// </summary>
    private void HeroControllerOnStart() {
        if (_defaultPlayerSkin == null) {
            Logger.Debug("Storing default player skin");
            StoreDefaultPlayerSkin(HeroController.instance);
        }

        if (HeroController.instance == null || HeroController.instance.gameObject == null) {
            return;
        }

        InitializeSpritesOnLocalPlayer(HeroController.instance.gameObject);
        _localCustomSkinId = CustomKnightSkinBridge.GetCurrentSkinId();
    }

    /// <summary>
    /// This method loads the Sprint animation on the local player to ensure that whenever the animation
    /// library is copied for skin purposes, it doesn't lack the instantiations of that animation.
    /// </summary>
    private void InitializeSpritesOnLocalPlayer(GameObject gameObject) {
        var spriteAnimator = gameObject.GetComponent<tk2dSpriteAnimator>();
        if (spriteAnimator == null) {
            Logger.Warn("Tried to initialize sprites on local player, but SpriteAnimator is null");
            return;
        }

        var sprint = spriteAnimator.GetClipByName("Sprint");
        if (sprint?.frames != null && sprint.frames.Length > 0) {
            var firstSpriteFrame = sprint.frames[0];
            spriteAnimator.SetSprite(firstSpriteFrame.spriteCollection, firstSpriteFrame.spriteId);
        }

        var slugIdle = spriteAnimator.GetClipByName("Slug Idle");
        if (slugIdle?.frames != null && slugIdle.frames.Length > 0) {
            var firstSpriteFrame = slugIdle.frames[0];
            spriteAnimator.SetSprite(firstSpriteFrame.spriteCollection, firstSpriteFrame.spriteId);
        }

        Logger.Debug("Initialized sprites on local player");
    }

    /// <summary>
    /// Update a player object with an HKMP skin or with a Custom Knight skin.
    /// </summary>
    internal void UpdatePlayerSkin(
        GameObject playerObject,
        byte skinId,
        string customSkinId = "",
        GameObject skinRoot = null
    ) {
        if (playerObject == null) {
            return;
        }

        customSkinId ??= "";
        if (!string.IsNullOrEmpty(customSkinId)) {
            ApplyCustomSkin(playerObject, customSkinId, skinRoot);
            return;
        }

        // A pooled remote player may previously have used a Custom Knight skin.
        // Restore the original textures before applying the numeric HKMP skin.
        CustomKnightSkinBridge.Restore(skinRoot ?? playerObject);
        ApplyBuiltInSkin(playerObject, skinId);
    }

    /// <summary>
    /// Apply the local player's acknowledged skin state.
    /// </summary>
    internal void UpdateLocalPlayerSkin(byte skinId, string customSkinId = "") {
        _localSkinId = skinId;
        _localCustomSkinId = customSkinId ?? "";

        var heroController = HeroController.instance;
        if (heroController == null || heroController.gameObject == null) {
            Logger.Warn("Tried to update local player skin, but HeroController was not available");
            return;
        }

        var customKnightAvailable = CustomKnightSkinBridge.IsAvailable;
        if (!customKnightAvailable && !string.IsNullOrEmpty(SelectedAtlasId)) {
            ApplyLocalAtlasSkin();
            return;
        }
        var currentCustomSkinId = customKnightAvailable ? CustomKnightSkinBridge.GetCurrentSkinId() : "";
        var customSkinApplied = true;
        if (customKnightAvailable && !string.Equals(currentCustomSkinId, _localCustomSkinId, StringComparison.Ordinal)) {
            _settingLocalCustomSkin = true;
            try {
                customSkinApplied = CustomKnightSkinBridge.SetLocalSkin(_localCustomSkinId);
            } finally {
                _settingLocalCustomSkin = false;
            }
        }

        if (string.IsNullOrEmpty(_localCustomSkinId) || !customKnightAvailable || !customSkinApplied) {
            ApplyBuiltInSkin(heroController.gameObject, skinId);
        }
    }

    /// <summary>
    /// Apply a Custom Knight skin to a remote body and its currently tracked visuals.
    /// </summary>
    internal void ApplyCustomSkin(GameObject playerObject, string customSkinId, GameObject skinRoot = null) {
        if (playerObject == null || string.IsNullOrEmpty(customSkinId)) {
            return;
        }

        var textureRoot = skinRoot ?? playerObject;
        CustomKnightSkinBridge.ApplyTo(textureRoot, customSkinId);

        var knightTexture = CustomKnightSkinBridge.GetSkinTexture(customSkinId, "Knight.png") ??
            CustomKnightSkinBridge.GetDefaultSkinTexture("Knight.png") ?? _defaultPlayerSkin?.KnightTexture;
        var sprintTexture = CustomKnightSkinBridge.GetSkinTexture(customSkinId, "Sprint.png") ??
            CustomKnightSkinBridge.GetDefaultSkinTexture("Sprint.png") ?? _defaultPlayerSkin?.SprintTexture;
        var spriteAnimator = playerObject.GetComponent<tk2dSpriteAnimator>();
        ApplyTextureToClip(spriteAnimator?.GetClipByName("Idle"), knightTexture);
        ApplyTextureToClip(spriteAnimator?.GetClipByName("Sprint"), sprintTexture);
    }

    /// <summary>
    /// Reset the skin of the given player to the default HKMP skin.
    /// </summary>
    internal void ResetPlayerSkin(GameObject playerObject, GameObject skinRoot = null) {
        UpdatePlayerSkin(playerObject, 0, "", skinRoot);
    }

    /// <summary>
    /// Reset the local player's numeric HKMP skin while retaining the Custom Knight selection.
    /// </summary>
    internal void ResetLocalPlayerSkin() {
        UpdateLocalPlayerSkin(0, _localCustomSkinId);
    }

    /// <summary>
    /// Store the default player skin from the given hero controller.
    /// </summary>
    private void StoreDefaultPlayerSkin(HeroController heroController) {
        if (heroController == null || heroController.gameObject == null) {
            return;
        }

        var spriteAnimator = heroController.gameObject.GetComponent<tk2dSpriteAnimator>();
        if (spriteAnimator == null) {
            return;
        }

        var knightTexture = CustomKnightSkinBridge.GetDefaultSkinTexture("Knight.png") ??
                            GetFirstTexture(spriteAnimator.GetClipByName("Idle"));
        var sprintTexture = CustomKnightSkinBridge.GetDefaultSkinTexture("Sprint.png") ??
                            GetFirstTexture(spriteAnimator.GetClipByName("Sprint"));
        if (knightTexture == null || sprintTexture == null) {
            Logger.Warn("Tried to store default player skin, but a player texture was null");
            return;
        }

        _defaultPlayerSkin = new PlayerSkin();
        _defaultPlayerSkin.SetKnightTexture(knightTexture);
        _defaultPlayerSkin.SetSprintTexture(sprintTexture);
    }

    private void ApplyBuiltInSkin(GameObject playerObject, byte skinId) {
        if (_defaultPlayerSkin == null) {
            return;
        }

        var playerSkin = _defaultPlayerSkin;
        if (skinId != 0 && !_playerSkins.TryGetValue(skinId, out playerSkin)) {
            Logger.Warn($"Tried to update skin with ID: {skinId}, but there was no such skin loaded");
            playerSkin = _defaultPlayerSkin;
        }

        var spriteAnimator = playerObject.GetComponent<tk2dSpriteAnimator>();
        if (spriteAnimator == null) {
            Logger.Warn("Tried to update player skin, but SpriteAnimator is null");
            return;
        }

        var knightTexture = playerSkin == _defaultPlayerSkin
            ? CustomKnightSkinBridge.GetDefaultSkinTexture("Knight.png") ?? playerSkin.KnightTexture
            : playerSkin.KnightTexture;
        var sprintTexture = playerSkin == _defaultPlayerSkin
            ? CustomKnightSkinBridge.GetDefaultSkinTexture("Sprint.png") ?? playerSkin.SprintTexture
            : playerSkin.SprintTexture;
        ApplyTextureToClip(spriteAnimator.GetClipByName("Idle"), knightTexture);
        ApplyTextureToClip(spriteAnimator.GetClipByName("Sprint"), sprintTexture);
    }

    private static void ApplyTextureToClip(tk2dSpriteAnimationClip clip, Texture texture) {
        if (clip?.frames == null || texture == null) {
            return;
        }

        foreach (var frame in clip.frames) {
            if (frame?.spriteCollection?.spriteDefinitions == null) {
                continue;
            }

            foreach (var definition in frame.spriteCollection.spriteDefinitions) {
                if (definition?.material != null) {
                    definition.material.mainTexture = texture;
                }
                if (definition?.materialInst != null && definition.materialInst != definition.material) {
                    definition.materialInst.mainTexture = texture;
                }
            }
        }
    }

    private static Texture2D GetFirstTexture(tk2dSpriteAnimationClip clip) {
        if (clip?.frames == null) {
            return null;
        }

        foreach (var frame in clip.frames) {
            var definitions = frame?.spriteCollection?.spriteDefinitions;
            if (definitions == null || definitions.Length == 0) {
                continue;
            }

            return definitions[0]?.material?.mainTexture as Texture2D;
        }

        return null;
    }

    private void OnCustomKnightSkinChanged(string skinId) {
        _localCustomSkinId = skinId ?? "";
        if (_settingLocalCustomSkin) {
            return;
        }

        CustomSkinChangedEvent?.Invoke(_localCustomSkinId);
    }
}
