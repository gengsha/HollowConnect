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
    private const string LegacyPrefix = "HKMP/Skins/";
    private static readonly Dictionary<byte, string> LegacyIds = new();
    private static readonly HashSet<byte> AmbiguousLegacyIds = new();
    private static readonly Dictionary<string, bool> Readable = new(StringComparer.Ordinal);

    internal static string InstallFolder => Path.Combine(SkinLoader.GetModsFolder(), "HKMP", "SkinPacks");

    internal static void Discover() {
        Packs.Clear();
        LegacyIds.Clear();
        AmbiguousLegacyIds.Clear();
        Readable.Clear();
        // Retry repaired files without freeing any texture that a native renderer still holds.
        foreach (var path in Textures.Where(pair => pair.Value == null).Select(pair => pair.Key).ToArray()) {
            Textures.Remove(path);
        }
        Scan(Path.Combine(SkinLoader.GetModsFolder(), "CustomKnight", "Skins"), legacy: false);
        Scan(InstallFolder, legacy: false);
        Scan(Path.Combine(SkinLoader.GetModsFolder(), "HKMP", "Skins"), legacy: true);
    }

    private static void Scan(string root, bool legacy) {
        try {
            if (!Directory.Exists(root)) return;
            foreach (var directory in Directory.GetDirectories(root).OrderBy(path => path, StringComparer.Ordinal)) {
                if (TryAdd(directory, legacy) || legacy) continue;
                // Some downloads have an extra outer folder around the actual skin.
                foreach (var child in Directory.GetDirectories(directory).OrderBy(path => path, StringComparer.Ordinal)) {
                    TryAdd(child, legacy: false);
                }
            }
        } catch (IOException e) {
            Logger.Warn($"Could not scan skin folder '{root}': {e.Message}");
        } catch (UnauthorizedAccessException e) {
            Logger.Warn($"Could not scan skin folder '{root}': {e.Message}");
        }
    }

    internal static bool HasPacks => Packs.Count != 0;
    internal static bool Contains(string id) => !string.IsNullOrEmpty(id) && Packs.ContainsKey(id);
    internal static bool IsLegacy(string id) => id?.StartsWith(LegacyPrefix, StringComparison.Ordinal) == true;
    internal static string DisplayName(string id) => IsLegacy(id) ? id.Substring(LegacyPrefix.Length) : id;
    internal static string LegacyId(byte id) => LegacyIds.TryGetValue(id, out var name) ? name : null;

    internal static string[] GetIds() => Packs.Keys.OrderBy(DisplayName, StringComparer.OrdinalIgnoreCase)
        .ThenBy(id => id, StringComparer.Ordinal).ToArray();

    private static bool TryAdd(string directory, bool legacy) {
        var name = Path.GetFileName(directory);
        if (string.IsNullOrWhiteSpace(name) || (!legacy && name == "Default")) return false;
        var id = legacy ? LegacyPrefix + name : name;
        if (Encoding.UTF8.GetByteCount(id) > 128) return false;

        var files = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var path in Directory.GetFiles(directory).OrderBy(path => path, StringComparer.Ordinal)) {
            var file = Path.GetFileName(path);
            if (AtlasNames.Contains(file) && !files.ContainsKey(file)) files.Add(file, path);
        }
        if (files.Count == 0) return false;
        if (!Packs.ContainsKey(id)) Packs.Add(id, files);
        if (legacy) AddLegacyId(directory, id);
        return true;
    }

    private static void AddLegacyId(string directory, string id) {
        var path = Path.Combine(directory, "id.txt");
        try {
            if (!File.Exists(path) || new FileInfo(path).Length > 64 ||
                !byte.TryParse(File.ReadAllText(path), out var number) || number == 0 || AmbiguousLegacyIds.Contains(number)) return;
            if (LegacyIds.ContainsKey(number)) {
                LegacyIds.Remove(number);
                AmbiguousLegacyIds.Add(number);
                Logger.Warn($"Legacy skin number {number} is ambiguous. The skins can still be selected by name.");
            } else {
                LegacyIds.Add(number, id);
            }
        } catch (IOException e) {
            Logger.Warn($"Could not read legacy skin ID '{path}': {e.Message}");
        } catch (UnauthorizedAccessException e) {
            Logger.Warn($"Could not read legacy skin ID '{path}': {e.Message}");
        }
    }

    internal static bool CanSelect(string id) {
        if (!Contains(id)) return false;
        if (!Readable.TryGetValue(id, out var readable)) {
            readable = Packs[id].Keys.Any(file => GetTexture(id, file) != null);
            Readable.Add(id, readable);
        }
        return readable;
    }

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
