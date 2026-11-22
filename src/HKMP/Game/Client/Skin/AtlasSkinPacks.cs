using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using UnityEngine;
using Logger = Hkmp.Logging.Logger;
using Object = UnityEngine.Object;

namespace Hkmp.Game.Client.Skin;

// A data-only reader for the atlases we can apply with the native game sprites.
internal static class AtlasSkinPacks {
    private const int MaxAtlasSize = 8192;
    private const int MaxAtlasPixels = 4096 * 4096;
    private const int MaxAtlasBytes = 64 * 1024 * 1024;
    private static readonly HashSet<string> AtlasNames = new(StringComparer.OrdinalIgnoreCase) {
        "Knight.png", "Sprint.png", "Wraiths.png", "VoidSpells.png", "Hud.png"
    };
    private static readonly Dictionary<string, Dictionary<string, string>> Packs = new(StringComparer.Ordinal);
    private static readonly Dictionary<string, Texture2D> Textures = new(StringComparer.Ordinal);

    internal static void Discover() {
        Packs.Clear();
        var mods = SkinLoader.GetModsFolder();
        foreach (var root in new[] { Path.Combine(mods, "HKMP", "SkinPacks"), Path.Combine(mods, "CustomKnight", "Skins") }) {
            try {
                if (!Directory.Exists(root)) continue;
                foreach (var directory in Directory.GetDirectories(root).OrderBy(path => path, StringComparer.Ordinal)) {
                    if (TryAdd(directory)) continue;
                    // Some downloads have an extra outer folder around the actual skin.
                    foreach (var child in Directory.GetDirectories(directory).OrderBy(path => path, StringComparer.Ordinal)) {
                        TryAdd(child);
                    }
                }
            } catch (IOException e) {
                Logger.Warn($"Could not scan atlas skin folder '{root}': {e.Message}");
            } catch (UnauthorizedAccessException e) {
                Logger.Warn($"Could not scan atlas skin folder '{root}': {e.Message}");
            }
        }
    }

    internal static bool HasPacks => Packs.Count != 0;

    internal static bool Contains(string id) => !string.IsNullOrEmpty(id) && Packs.ContainsKey(id);

    internal static string[] GetIds() => Packs.Keys.OrderBy(id => id, StringComparer.OrdinalIgnoreCase).ToArray();

    private static bool TryAdd(string directory) {
        var id = Path.GetFileName(directory);
        if (string.IsNullOrWhiteSpace(id) || id == "Default" || Encoding.UTF8.GetByteCount(id) > 128) return false;
        var files = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var path in Directory.GetFiles(directory).OrderBy(path => path, StringComparer.Ordinal)) {
            var name = Path.GetFileName(path);
            if (AtlasNames.Contains(name) && !files.ContainsKey(name)) files.Add(name, path);
        }
        if (files.Count == 0) return false;
        if (!Packs.ContainsKey(id)) Packs.Add(id, files);
        return true;
    }

    internal static bool CanSelect(string id) => Packs.TryGetValue(id, out var files) &&
        files.Keys.Any(file => GetTexture(id, file) != null);

    internal static Texture2D GetTexture(string id, string file) {
        // Resolve only by discovered IDs. Never construct a path from a peer's skin ID.
        if (!Packs.TryGetValue(id, out var files) || !files.TryGetValue(file, out var path)) return null;
        if (Textures.TryGetValue(path, out var cached)) return cached;
        Texture2D texture = null;
        try {
            using var stream = File.OpenRead(path);
            if (stream.Length is < 33 or > MaxAtlasBytes) throw new InvalidDataException("PNG file size is invalid");
            using var reader = new BinaryReader(stream);
            var bytes = reader.ReadBytes((int)stream.Length);
            if (bytes.Length < 33) throw new InvalidDataException("PNG header is incomplete");
            var width = ReadDimension(bytes, 16);
            var height = ReadDimension(bytes, 20);
            var maxDimension = System.Math.Min(SystemInfo.maxTextureSize, MaxAtlasSize);
            if (bytes[0] != 137 || bytes[1] != 80 || bytes[2] != 78 || bytes[3] != 71 ||
                bytes[4] != 13 || bytes[5] != 10 || bytes[6] != 26 || bytes[7] != 10 ||
                bytes[12] != 'I' || bytes[13] != 'H' || bytes[14] != 'D' || bytes[15] != 'R' ||
                width < 1 || width > maxDimension || height < 1 || height > maxDimension ||
                (long)width * height > MaxAtlasPixels) {
                throw new InvalidDataException("PNG header or atlas dimensions are invalid");
            }
            texture = new Texture2D(2, 2, TextureFormat.RGBA32, false) { name = id + "/" + file };
            if (!texture.LoadImage(bytes, true)) throw new InvalidDataException("PNG decoding failed");
            Textures[path] = texture;
            return texture;
        } catch (Exception e) {
            if (texture != null) Object.Destroy(texture);
            Textures[path] = null;
            Logger.Warn($"Could not load atlas '{file}' for '{id}': {e.Message}");
            return null;
        }
    }

    private static int ReadDimension(byte[] bytes, int offset) =>
        (bytes[offset] << 24) | (bytes[offset + 1] << 16) | (bytes[offset + 2] << 8) | bytes[offset + 3];
}
