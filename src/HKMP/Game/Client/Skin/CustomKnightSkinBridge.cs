using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;

using Logger = Hkmp.Logging.Logger;

namespace Hkmp.Game.Client.Skin;

/// <summary>
/// Resolves named skin textures for remote players. It can use either the Custom Knight plugin or data-only atlas packs.
/// </summary>
internal static class CustomKnightSkinBridge {
    private sealed class TextureInfo {
        internal string FileName;
        internal Texture DefaultTexture;
        internal Sprite DefaultSprite;
    }

    private static readonly Dictionary<int, TextureInfo> TextureInfos = new();
    private static readonly Dictionary<string, Texture2D> DefaultTextures = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<int, string> MaterialFiles = new();
    private static readonly Dictionary<int, Texture> MaterialDefaults = new();
    private static readonly Dictionary<int, string> SpriteFiles = new();
    private static readonly Dictionary<int, Sprite> SpriteDefaults = new();
    private static readonly Dictionary<string, Texture2D> SkinTextures = new();
    private static readonly Dictionary<string, Sprite> SkinSprites = new();

    private static Type _skinManagerType;
    private static MethodInfo _getCurrentSkin;
    private static MethodInfo _getSkinById;
    private static MethodInfo _setSkinById;
    private static EventInfo _onSetSkin;
    private static Delegate _onSetSkinHandler;
    private static Action<string> _skinChanged;
    private static bool _mapInitialized;
    private static bool _loggedUnavailable;

    internal static bool IsAvailable {
        get {
            EnsureInitialized();
            return _skinManagerType != null;
        }
    }

    internal static void Initialize(Action<string> skinChanged) {
        _skinChanged = skinChanged;
        EnsureInitialized();

        if (_skinManagerType == null || _onSetSkin == null || _onSetSkinHandler != null) {
            return;
        }

        try {
            var handlerMethod = typeof(CustomKnightSkinBridge).GetMethod(
                nameof(OnSetSkin),
                BindingFlags.Static | BindingFlags.NonPublic
            );
            _onSetSkinHandler = Delegate.CreateDelegate(_onSetSkin.EventHandlerType, handlerMethod);
            _onSetSkin.AddEventHandler(null, _onSetSkinHandler);
        } catch (Exception e) {
            Logger.Warn($"Could not subscribe to Custom Knight skin changes: {e.Message}");
            _onSetSkinHandler = null;
        }
    }

    internal static void Deregister() {
        if (_onSetSkin != null && _onSetSkinHandler != null) {
            try {
                _onSetSkin.RemoveEventHandler(null, _onSetSkinHandler);
            } catch (Exception e) {
                Logger.Warn($"Could not unsubscribe from Custom Knight skin changes: {e.Message}");
            }
        }

        _onSetSkinHandler = null;
        _skinChanged = null;
    }

    internal static string GetCurrentSkinId() {
        EnsureInitialized();
        if (_getCurrentSkin == null) {
            return "";
        }

        try {
            var skin = _getCurrentSkin.Invoke(null, null);
            return NormalizeSkinId(GetStringMember(skin, "GetId", true));
        } catch (Exception e) {
            Logger.Warn($"Could not read the current Custom Knight skin: {e.Message}");
            return "";
        }
    }

    internal static bool SetLocalSkin(string skinId) {
        EnsureInitialized();
        if (_setSkinById == null) {
            return false;
        }

        try {
            _setSkinById.Invoke(null, new object[] { string.IsNullOrEmpty(skinId) ? "Default" : skinId });
            return true;
        } catch (Exception e) {
            Logger.Warn($"Could not set the Custom Knight skin '{skinId}': {e.Message}");
            return false;
        }
    }

    internal static Texture2D GetSkinTexture(string skinId, string fileName) {
        EnsureInitialized();
        if (string.IsNullOrEmpty(skinId) || string.IsNullOrEmpty(fileName)) return null;
        return GetPluginTexture(skinId, fileName) ?? AtlasSkinPacks.GetTexture(skinId, fileName);
    }

    private static Texture2D GetPluginTexture(string skinId, string fileName) {
        if (_getSkinById == null) return null;

        var cacheKey = skinId + "\n" + fileName;
        if (SkinTextures.TryGetValue(cacheKey, out var cachedTexture) && cachedTexture != null) {
            return cachedTexture;
        }

        try {
            var skin = _getSkinById.Invoke(null, new object[] { skinId });
            if (skin == null || GetStringMember(skin, "GetId", true) != skinId || !SkinExists(skin, fileName)) {
                return null;
            }

            var getTexture = skin.GetType().GetMethod("GetTexture", BindingFlags.Instance | BindingFlags.Public);
            var texture = getTexture?.Invoke(skin, new object[] { fileName }) as Texture2D;
            if (texture == null) {
                return null;
            }

            SkinTextures[cacheKey] = texture;
            return texture;
        } catch (Exception e) {
            Logger.Warn($"Could not load Custom Knight texture '{fileName}' for skin '{skinId}': {e.Message}");
            return null;
        }
    }

    internal static Texture2D GetDefaultSkinTexture(string fileName) {
        RefreshTextureMap();
        return DefaultTextures.TryGetValue(fileName, out var texture) ? texture : null;
    }

    internal static void ApplyEffect(GameObject root, string skinId) {
        if (string.IsNullOrEmpty(skinId) && !AtlasSkinPacks.HasPacks && !IsAvailable) return;
        RefreshTextureMap();
        foreach (var renderer in root.GetComponentsInChildren<Renderer>(true)) {
            // Some inactive native effects have no renderer material until their
            // first sprite is built. The sprite definition already owns the atlas.
            var sprite = renderer.GetComponent<tk2dBaseSprite>();
            var atlas = sprite?.GetCurrentSpriteDef()?.material?.mainTexture ?? renderer.sharedMaterial?.mainTexture;
            if (!TryGetTextureInfo(atlas, out var info)) continue;

            var replacement = GetSkinTexture(skinId, info.FileName) ?? info.DefaultTexture;
            if (replacement == null) continue;
            var effect = renderer.GetComponent<RemoteEffectSkin>() ?? renderer.gameObject.AddComponent<RemoteEffectSkin>();
            effect.Apply(renderer, replacement);
        }
    }

    internal static void ApplyTo(GameObject root, string skinId) {
        if (root == null || string.IsNullOrEmpty(skinId)) {
            return;
        }

        RefreshTextureMap();

        foreach (var renderer in root.GetComponentsInChildren<Renderer>(true)) {
            if (renderer == null) {
                continue;
            }

            if (renderer is SpriteRenderer spriteRenderer) {
                ApplySprite(spriteRenderer, skinId);
            }

            var materials = renderer.materials;
            var changed = false;
            for (var i = 0; i < materials.Length; i++) {
                changed |= ApplyMaterial(materials[i], skinId);
            }

            if (changed) {
                renderer.materials = materials;
            }
        }
    }

    internal static void Restore(GameObject root) {
        if (root == null || (MaterialDefaults.Count == 0 && SpriteDefaults.Count == 0)) {
            return;
        }

        foreach (var renderer in root.GetComponentsInChildren<Renderer>(true)) {
            if (renderer == null) {
                continue;
            }

            if (renderer is SpriteRenderer spriteRenderer &&
                SpriteDefaults.TryGetValue(spriteRenderer.GetInstanceID(), out var defaultSprite)) {
                spriteRenderer.sprite = defaultSprite;
            }

            var materials = renderer.materials;
            var changed = false;
            for (var i = 0; i < materials.Length; i++) {
                var material = materials[i];
                if (material == null || !MaterialDefaults.TryGetValue(material.GetInstanceID(), out var defaultTexture)) {
                    continue;
                }

                material.mainTexture = defaultTexture;
                changed = true;
            }

            if (changed) {
                renderer.materials = materials;
            }
        }
    }

    private static bool ApplyMaterial(Material material, string skinId) {
        if (material == null) {
            return false;
        }

        var materialId = material.GetInstanceID();
        if (!MaterialFiles.TryGetValue(materialId, out var fileName)) {
            if (!TryGetTextureInfo(material.mainTexture, out var textureInfo)) {
                return false;
            }

            fileName = textureInfo.FileName;
            MaterialFiles[materialId] = fileName;
            MaterialDefaults[materialId] = textureInfo.DefaultTexture;
        }

        var texture = GetSkinTexture(skinId, fileName);
        material.mainTexture = texture != null ? texture : MaterialDefaults[materialId];
        return true;
    }

    private static void ApplySprite(SpriteRenderer spriteRenderer, string skinId) {
        var sprite = spriteRenderer.sprite;
        var spriteId = spriteRenderer.GetInstanceID();
        if (!SpriteFiles.TryGetValue(spriteId, out var fileName)) {
            if (sprite == null || !TryGetTextureInfo(sprite.texture, out var textureInfo)) {
                return;
            }

            fileName = textureInfo.FileName;
            SpriteFiles[spriteId] = fileName;
            SpriteDefaults[spriteId] = textureInfo.DefaultSprite ?? sprite;
        }

        var texture = GetSkinTexture(skinId, fileName);
        if (texture == null) {
            spriteRenderer.sprite = SpriteDefaults[spriteId];
            return;
        }

        var sourceSprite = SpriteDefaults[spriteId];
        var key = skinId + "\n" + fileName + "\n" + sourceSprite.pixelsPerUnit + "\n" + sourceSprite.pivot;
        if (!SkinSprites.TryGetValue(key, out var replacement) || replacement == null) {
            var normalizedPivot = new Vector2(
                sourceSprite.pivot.x / sourceSprite.rect.width,
                sourceSprite.pivot.y / sourceSprite.rect.height
            );
            replacement = Sprite.Create(
                texture,
                new Rect(0, 0, texture.width, texture.height),
                normalizedPivot,
                sourceSprite.pixelsPerUnit
            );
            SkinSprites[key] = replacement;
        }

        spriteRenderer.sprite = replacement;
    }

    internal static void RegisterAtlas(Texture texture, string file, Texture native) {
        if (native is Texture2D atlas && !DefaultTextures.ContainsKey(file)) DefaultTextures.Add(file, atlas);
        AddTextureInfo(texture, file, native, null);
    }

    private static bool TryGetTextureInfo(Texture texture, out TextureInfo info) {
        info = null;
        return texture != null && TextureInfos.TryGetValue(texture.GetInstanceID(), out info);
    }

    private static void RefreshTextureMap() {
        EnsureInitialized();
        if (_mapInitialized || _skinManagerType == null) {
            return;
        }

        try {
            var skinables = GetMember(_skinManagerType, null, "Skinables", true);
            if (!(skinables is IDictionary dictionary)) {
                return;
            }

            foreach (DictionaryEntry entry in dictionary) {
                var skinable = entry.Value;
                var ckTexture = GetMember(skinable?.GetType(), skinable, "ckTex", false);
                var fileName = GetStringMember(ckTexture, "fileName", false);
                var defaultTexture = GetMember(ckTexture?.GetType(), ckTexture, "defaultTex", false) as Texture2D;
                var defaultSprite = GetMember(ckTexture?.GetType(), ckTexture, "defaultSprite", false) as Sprite;
                var currentTexture = GetMember(ckTexture?.GetType(), ckTexture, "currentTexture", false) as Texture;

                if (string.IsNullOrEmpty(fileName)) {
                    continue;
                }

                if (defaultTexture != null) DefaultTextures[fileName] = defaultTexture;
                AddTextureInfo(defaultTexture, fileName, defaultTexture, defaultSprite);
                AddTextureInfo(currentTexture, fileName, defaultTexture, defaultSprite);
            }

            // Custom Knight captures the originals after the hero loads.
            _mapInitialized = DefaultTextures.ContainsKey("Knight.png") && DefaultTextures.ContainsKey("Sprint.png");
        } catch (Exception e) {
            Logger.Warn($"Could not inspect Custom Knight skin resources: {e.Message}");
        }
    }

    private static void AddTextureInfo(
        Texture texture,
        string fileName,
        Texture defaultTexture,
        Sprite defaultSprite
    ) {
        if (texture == null) {
            return;
        }

        var textureId = texture.GetInstanceID();
        if (TextureInfos.TryGetValue(textureId, out var existing)) {
            existing.DefaultTexture ??= defaultTexture;
            existing.DefaultSprite ??= defaultSprite;
            return;
        }

        TextureInfos[textureId] = new TextureInfo {
            FileName = fileName,
            DefaultTexture = defaultTexture,
            DefaultSprite = defaultSprite
        };
    }

    private static bool SkinExists(object skin, string fileName) {
        var exists = skin.GetType().GetMethod("Exists", BindingFlags.Instance | BindingFlags.Public);
        return exists != null && (bool) exists.Invoke(skin, new object[] { fileName });
    }

    private static void OnSetSkin(object sender, EventArgs args) {
        try {
            _mapInitialized = false;
            _skinChanged?.Invoke(GetCurrentSkinId());
        } catch (Exception e) {
            Logger.Warn($"Custom Knight skin change handling failed: {e.Message}");
        }
    }

    private static void EnsureInitialized() {
        if (_skinManagerType != null || (Modding.ModHooks.GetMod("CustomKnight") == null && Modding.ModHooks.GetMod("Custom Knight") == null)) return;

        foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies()) {
            var type = assembly.GetType("CustomKnight.SkinManager", false);
            if (type == null) {
                continue;
            }

            _skinManagerType = type;
            _getCurrentSkin = type.GetMethod("GetCurrentSkin", BindingFlags.Static | BindingFlags.Public);
            _getSkinById = type.GetMethod("GetSkinById", BindingFlags.Static | BindingFlags.Public);
            _setSkinById = type.GetMethod("SetSkinById", BindingFlags.Static | BindingFlags.Public);
            _onSetSkin = type.GetEvent("OnSetSkin", BindingFlags.Static | BindingFlags.Public);
            return;
        }

        if (!_loggedUnavailable) {
            _loggedUnavailable = true;
            Logger.Debug("Custom Knight is not installed, using the built-in HKMP skin system");
        }
    }

    private static object GetMember(Type type, object instance, string name, bool staticMember) {
        if (type == null) {
            return null;
        }

        var flags = BindingFlags.Public | BindingFlags.NonPublic |
                    (staticMember ? BindingFlags.Static : BindingFlags.Instance);
        var property = type.GetProperty(name, flags);
        if (property != null) {
            return property.GetValue(instance, null);
        }

        var field = type.GetField(name, flags);
        return field?.GetValue(instance);
    }

    private static string GetStringMember(object instance, string name, bool method) {
        if (instance == null) {
            return "";
        }

        if (method) {
            var getId = instance.GetType().GetMethod(name, BindingFlags.Instance | BindingFlags.Public);
            return getId?.Invoke(instance, null) as string ?? "";
        }

        return GetMember(instance.GetType(), instance, name, false) as string ?? "";
    }

    private static string NormalizeSkinId(string skinId) {
        return string.Equals(skinId, "Default", StringComparison.Ordinal) ? "" : skinId ?? "";
    }
}

// tk2d replaces the renderer material when it builds an animation frame.
// A renderer override follows the effect without editing the local sprite collection.
internal sealed class RemoteEffectSkin : MonoBehaviour {
    private static readonly int MainTexture = Shader.PropertyToID("_MainTex");
    private Renderer _renderer;
    private MaterialPropertyBlock _original;
    private MaterialPropertyBlock _override;

    internal void Apply(Renderer renderer, Texture texture) {
        _renderer = renderer;
        if (_original == null) {
            _original = new MaterialPropertyBlock();
            renderer.GetPropertyBlock(_original);
        }
        _override ??= new MaterialPropertyBlock();
        renderer.GetPropertyBlock(_override);
        _override.SetTexture(MainTexture, texture);
        renderer.SetPropertyBlock(_override);
    }

    internal void Restore() {
        if (_renderer == null || _original == null) return;
        _renderer.SetPropertyBlock(_original);
        _original = null;
    }

    private void OnDisable() => Restore();
}
