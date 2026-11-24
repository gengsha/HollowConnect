using System;
using System.Collections;
using Hkmp.Game.Settings;
using Hkmp.Util;
using UnityEngine;
using Logger = Hkmp.Logging.Logger;

namespace Hkmp.Game.Client.Skin;

/// <summary>
/// Class that manages skins for player objects.
/// </summary>
internal class SkinManager {
    private PlayerSkin _defaultPlayerSkin;
    private byte _localSkinId;
    private bool _settingLocalCustomSkin;
    private ModSettings _settings;
    private LocalAtlasSkin _localAtlases;
    private HeroController _atlasHero;

    // Managed choices always advertise a name. The byte slot is only used by the old /skin command.
    internal event Action SelectionChanged;
    internal byte LocalSkinId => _localSkinId;
    internal string SavedAtlasId => _settings.AtlasSkinId ?? "";
    private string SelectedAtlasId => AtlasSkinPacks.CanSelect(SavedAtlasId) ? SavedAtlasId : "";
    internal string LocalCustomSkinId => _localSkinId != 0 ? "" :
        SavedAtlasId.Length != 0 ? SelectedAtlasId : CustomKnightSkinBridge.GetCurrentSkinId();
    internal string LocalDisplaySkinId => _localSkinId != 0 ? AtlasSkinPacks.LegacyId(_localSkinId) ?? "" : LocalCustomSkinId;

    public void Initialize(ModSettings settings) {
        _settings = settings;
        AtlasSkinPacks.Discover();
    }

    internal void RegisterLocalAtlasHook() => CustomHooks.HeroControllerStartAction += ApplyLocalAtlasSkinOnStart;

    internal bool TrySelectSkin(string id) {
        id ??= "";
        var pluginSkin = CustomKnightSkinBridge.HasSkin(id);
        if (id.Length != 0 && !pluginSkin && !AtlasSkinPacks.CanSelect(id)) return false;

        _settingLocalCustomSkin = true;
        try {
            var currentPluginSkin = CustomKnightSkinBridge.GetCurrentSkinId();
            var desiredPluginSkin = pluginSkin ? id : "";
            if (currentPluginSkin != desiredPluginSkin) {
                // Restore native materials before letting the plugin own them again.
                if (pluginSkin) ApplyLocalAtlasSkin("");
                if (!CustomKnightSkinBridge.SetLocalSkin(desiredPluginSkin)) {
                    ApplySelectedSkin();
                    return false;
                }
            }
            _localSkinId = 0;
            _settings.AtlasSkinId = pluginSkin ? "" : id;
            ApplySelectedSkin();
        } finally {
            _settingLocalCustomSkin = false;
        }
        SelectionChanged?.Invoke();
        return true;
    }

    internal void Rescan() {
        AtlasSkinPacks.Discover();
        ApplySelectedSkin();
        SelectionChanged?.Invoke();
    }

    private void ApplySelectedSkin() {
        if (_localSkinId != 0) {
            ApplyLocalAtlasSkin(AtlasSkinPacks.LegacyId(_localSkinId) ?? "");
        } else if (SavedAtlasId.Length != 0 || CustomKnightSkinBridge.GetCurrentSkinId().Length == 0) {
            ApplyLocalAtlasSkin(SelectedAtlasId);
        }
    }

    private void ApplyLocalAtlasSkinOnStart() {
        CustomKnightSkinBridge.Initialize(OnCustomKnightSkinChanged);
        ApplySelectedSkin();
        if (!string.IsNullOrEmpty(LocalDisplaySkinId)) {
            MonoBehaviourUtil.Instance.StartCoroutine(ApplyLocalAtlasesAfterStart(HeroController.instance));
        }
    }

    private IEnumerator ApplyLocalAtlasesAfterStart(HeroController hero) {
        // The HUD is attached by the camera after the hero has started.
        yield return null;
        if (hero != null && HeroController.instance == hero) ApplySelectedSkin();
    }

    private void ApplyLocalAtlasSkin(string id) {
        var hero = HeroController.instance;
        if (hero == null) return;
        if (_atlasHero != hero) {
            _atlasHero = hero;
            _localAtlases = null;
        }
        if (_localAtlases == null && !AtlasSkinPacks.HasPacks) return;
        (_localAtlases ??= new LocalAtlasSkin()).Apply(hero, id);
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

        // A named packet is authoritative even if the receiver also has its byte slot.
        var id = string.IsNullOrEmpty(customSkinId) ? AtlasSkinPacks.LegacyId(skinId) : customSkinId;
        if (!string.IsNullOrEmpty(id)) {
            ApplyCustomSkin(playerObject, id, skinRoot);
            return;
        }
        CustomKnightSkinBridge.Restore(skinRoot ?? playerObject);
        ApplyNativeBody(playerObject);
    }

    internal void ApplyLegacyCommand(byte skinId) {
        if (skinId == 0 && _localSkinId == 0) return;
        if (skinId != 0 && CustomKnightSkinBridge.GetCurrentSkinId().Length != 0) {
            _settingLocalCustomSkin = true;
            try {
                if (!CustomKnightSkinBridge.SetLocalSkin("")) return;
            } finally {
                _settingLocalCustomSkin = false;
            }
        }
        _localSkinId = skinId;
        // /skin 0 means original appearance. Numeric rooms remain session-scoped.
        if (skinId == 0) _settings.AtlasSkinId = "";
        ApplySelectedSkin();
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

    internal void ResetLocalPlayerSkin() {
        if (_localSkinId == 0) return;
        _localSkinId = 0;
        ApplySelectedSkin();
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

    private void ApplyNativeBody(GameObject playerObject) {
        var spriteAnimator = playerObject.GetComponent<tk2dSpriteAnimator>();
        ApplyTextureToClip(spriteAnimator?.GetClipByName("Idle"),
            CustomKnightSkinBridge.GetDefaultSkinTexture("Knight.png") ?? _defaultPlayerSkin?.KnightTexture);
        ApplyTextureToClip(spriteAnimator?.GetClipByName("Sprint"),
            CustomKnightSkinBridge.GetDefaultSkinTexture("Sprint.png") ?? _defaultPlayerSkin?.SprintTexture);
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

    private void OnCustomKnightSkinChanged(string _) {
        if (_settingLocalCustomSkin) return;
        _localSkinId = 0;
        _settings.AtlasSkinId = "";
        SelectionChanged?.Invoke();
    }
}
