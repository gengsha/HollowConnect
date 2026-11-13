#if HOLLOW_UNITY6
using System;
using System.IO;
using System.Reflection;
using System.Runtime.Serialization.Formatters.Binary;
using System.Text;
using MonoMod.RuntimeDetour;
using MonoMod.Cil;
using Newtonsoft.Json.Linq;

namespace HollowConnect.Smoke;

// Exercise the actual API writer while redirecting both save destinations.
// Never enable the native writer until every redirection is installed.
internal static class Unity6SaveFormat {
    internal static bool Checking { get; private set; }

    internal static void Check(string root, string role, Action<string> mark) {
        var path = Path.Combine(root, role + "-native-save.modded.json");
        byte[] bytes = null;
        var writes = 0; var saved = false;
        On.DesktopPlatform.hook_WriteSaveSlot capture = (orig, platform, slot, data, callback) => {
            if (slot != 99) throw new InvalidOperationException("Unexpected native fixture save slot");
            bytes = (byte[])data.Clone(); writes++; callback?.Invoke(true);
        };
        On.AchievementHandler.hook_FlushRecordsToDisk achievements = (orig, handler) => { };
        using var redirected = new ILHook(typeof(GameManager).GetMethod("SaveGame", new[] { typeof(int), typeof(Action<bool>) }), il => {
            var cursor = new ILCursor(il);
            cursor.GotoNext(MoveType.After, instruction => instruction.MatchCall(typeof(GameManager), "ModdedSavePath"));
            // Change the path at the writer itself so JIT inlining cannot
            // bypass the isolated destination.
            cursor.EmitDelegate<Func<string, string>>(nativePath => path);
        });
        On.DesktopPlatform.WriteSaveSlot += capture;
        On.AchievementHandler.FlushRecordsToDisk += achievements;
        try {
            Checking = true;
            GameManager.instance.SaveGame(99, ok => saved = ok);
            if (!saved || writes != 1 || bytes == null || !File.Exists(path))
                throw new InvalidOperationException("Native API save did not reach both isolated destinations");
            string json; string format;
            if (bytes[0] == (byte)'{') { json = Encoding.UTF8.GetString(bytes); format = "JSON"; }
            else {
                using var stream = new MemoryStream(bytes);
                var encrypted = (string)new BinaryFormatter().Deserialize(stream);
                json = TeamCherry.SharedUtils.Encryption.Decrypt(encrypted);
                format = "native encrypted BinaryFormatter";
            }
            var data = JObject.Parse(json);
            if ((int)data["playerData"]["profileID"] != 99 ||
                (int)data["playerData"]["geo"] != PlayerData.instance.GetInt("geo"))
                throw new InvalidOperationException("Native save format roundtrip changed fixture values");
            File.WriteAllBytes(Path.Combine(root, role + "-native-save.dat"), bytes);
            mark("PASS native API save format roundtrip: " + format + "; both destinations isolated");
        } finally {
            Checking = false;
            On.DesktopPlatform.WriteSaveSlot -= capture;
            On.AchievementHandler.FlushRecordsToDisk -= achievements;
        }
    }
}
#endif
