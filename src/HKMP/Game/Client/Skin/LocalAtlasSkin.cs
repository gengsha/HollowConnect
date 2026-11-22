using System.Collections.Generic;
using UnityEngine;

namespace Hkmp.Game.Client.Skin;

// Keep native material references so a partial pack or a switch to default restores every atlas.
internal sealed class LocalAtlasSkin {
    private readonly List<(Material Material, Texture Native, string File)> _materials = new();
    private readonly HashSet<Material> _seen = new();

    internal void Apply(HeroController hero, string id) {
        var animator = hero.GetComponent<tk2dSpriteAnimator>();
        TrackClip(animator?.GetClipByName("Idle"), "Knight.png");
        TrackClip(animator?.GetClipByName("Sprint"), "Sprint.png");
        var spells = hero.transform.Find("Spells");
        TrackSprite(spells?.Find("Scr Heads"), "Wraiths.png");
        TrackSprite(spells?.Find("Scr Base"), "Wraiths.png");
        TrackSprite(spells?.Find("Scr Heads 2"), "VoidSpells.png");
        TrackSprite(spells?.Find("Scr Base 2"), "VoidSpells.png");
        if (GameCameras.instance != null && GameCameras.instance.hudCanvas != null) {
            foreach (var sprite in GameCameras.instance.hudCanvas.GetComponentsInChildren<tk2dSprite>(true)) {
                if (sprite.name == "Health 1") {
                    TrackDefinition(sprite.GetCurrentSpriteDef(), "Hud.png");
                    break;
                }
            }
        }
        foreach (var (material, native, file) in _materials) {
            if (material == null) continue;
            var replacement = string.IsNullOrEmpty(id) ? null : AtlasSkinPacks.GetTexture(id, file);
            var texture = replacement ?? native;
            if (material.mainTexture == texture) continue;
            material.mainTexture = texture;
            if (replacement != null) CustomKnightSkinBridge.RegisterAtlas(replacement, file, native);
        }
    }

    private void TrackClip(tk2dSpriteAnimationClip clip, string file) {
        if (clip?.frames == null) return;
        var collections = new HashSet<tk2dSpriteCollectionData>();
        foreach (var frame in clip.frames) {
            var collection = frame?.spriteCollection;
            if (collection?.spriteDefinitions == null || !collections.Add(collection)) continue;
            foreach (var definition in collection.spriteDefinitions) TrackDefinition(definition, file);
        }
    }

    private void TrackSprite(Transform target, string file) {
        if (target == null) return;
        TrackDefinition(target.GetComponent<tk2dBaseSprite>()?.GetCurrentSpriteDef(), file);
    }

    private void TrackDefinition(tk2dSpriteDefinition definition, string file) {
        TrackMaterial(definition?.material, file);
        TrackMaterial(definition?.materialInst, file);
    }

    private void TrackMaterial(Material material, string file) {
        if (material == null || material.mainTexture == null || !_seen.Add(material)) return;
        var native = CustomKnightSkinBridge.GetDefaultSkinTexture(file) ?? material.mainTexture;
        _materials.Add((material, native, file));
        CustomKnightSkinBridge.RegisterAtlas(native, file, native);
    }
}
