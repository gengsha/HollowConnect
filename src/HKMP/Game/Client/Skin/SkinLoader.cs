using System.IO;
using UnityEngine;

namespace Hkmp.Game.Client.Skin;

internal static class SkinLoader {
    internal static string GetModsFolder() => Path.GetFullPath(SystemInfo.operatingSystemFamily == OperatingSystemFamily.MacOSX
        ? $"{Application.dataPath}/Resources/Data/Managed/Mods"
        : $"{Application.dataPath}/Managed/Mods");
}
