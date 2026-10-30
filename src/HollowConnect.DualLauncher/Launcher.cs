using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Web.Script.Serialization;
using Microsoft.Win32;

namespace HollowConnect.DualLauncher;

internal static class GameSettings {
    // Verified with UnityPy PlayerSettings for Windows 1.5.78.11833. Gate the
    // entire asset before touching offsets; never guess for another build.
    private const string SteamHash = "57EBBD860F452878D82000ED7CAA9D40F22436FFCC1E56088EEC28683C486EFA";
    private const string IsolatedHash = "47C9A39F469E16C4FC6264C88BB026F32928E571E3650E5040EAE68B19F29E45";
    internal static string Product(int player) {
        if (player != 1 && player != 2) throw new ArgumentOutOfRangeException(nameof(player));
        return "HollowCoop P" + player;
    }
    internal static string Hash(byte[] data) {
        using (var sha = SHA256.Create()) return BitConverter.ToString(sha.ComputeHash(data)).Replace("-", "");
    }
    internal static byte[] Patch(byte[] original, int player) {
        var name = Encoding.UTF8.GetBytes(Product(player));
        var hash = Hash(original);
        int flag, product;
        if (hash == SteamHash) { flag = 4452; product = 4152; }
        else if (hash == IsolatedHash) { flag = 1828; product = 1528; }
        else throw new InvalidOperationException("暂不支持这份游戏资源。当前仅适配 Windows 1.5.78.11833，未修改任何原游戏文件。\r\n资源 SHA-256：" + hash);
        var result = (byte[])original.Clone();
        if (Encoding.UTF8.GetString(result, product, name.Length) != "Hollow Knight" || result[flag] > 1)
            throw new InvalidDataException("游戏设置校验失败。");
        result[flag] = 0;
        Buffer.BlockCopy(name, 0, result, product, name.Length);
        return result;
    }
    internal static string Boot(string text) => string.Join("\n", text.Split('\n')
        .Where(line => !Regex.IsMatch(line, @"^\s*single-instance\s*(=|$)", RegexOptions.IgnoreCase)));
}

internal sealed class PreparedGame {
    public string Source { get; set; }
    public string Directory { get; set; }
    public string[] Games { get; set; }
    public string[] Saves { get; set; }
    public int LinkedFiles { get; set; }
    public int CopiedFiles { get; set; }
}

internal sealed class SourceFile {
    internal string Path, Relative;
    internal long Length, Modified;
}

internal static class Launcher {
    private const string Data = "hollow_knight_Data";
    private const string Owner = "HollowConnect.DualLauncher.v1";
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CreateHardLink(string destination, string source, IntPtr reserved);
    internal static string Canonical(string path) => Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
    internal static bool Within(string path, string root) => Canonical(path).StartsWith(Canonical(root) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    internal static void NoReparse(string path) {
        var current = Canonical(path);
        while (!string.IsNullOrEmpty(current)) {
            if ((Directory.Exists(current) || File.Exists(current)) && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("请使用真实文件夹，不能使用目录联接或符号链接：" + current);
            current = Path.GetDirectoryName(current);
        }
    }
    internal static string ResolveGame(string path) {
        path = Canonical(path.Trim().Trim('"'));
        if (File.Exists(path) && Path.GetFileName(path).Equals("hollow_knight.exe", StringComparison.OrdinalIgnoreCase)) path = Path.GetDirectoryName(path);
        NoReparse(path);
        foreach (var relative in new[] { "hollow_knight.exe", "UnityPlayer.dll", Data + "/globalgamemanagers", Data + "/boot.config", Data + "/Managed/Assembly-CSharp.dll" })
            if (!File.Exists(Path.Combine(path, relative))) throw new FileNotFoundException("请选择完整的《空洞骑士》游戏目录。缺少：" + relative);
        return path;
    }
    internal static string DefaultWorkspace(string game) => Path.Combine(Path.GetDirectoryName(ResolveGame(game)), "HollowConnect-Dual");
    internal static string SaveDirectory(int player) => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "AppData", "LocalLow", "Team Cherry", GameSettings.Product(player));

    private static IEnumerable<SourceFile> Files(string game) {
        foreach (var path in System.IO.Directory.GetFiles(game)) {
            var name = Path.GetFileName(path);
            if (name.Equals("hollow_knight.exe", StringComparison.OrdinalIgnoreCase) ||
                name.Equals("UnityCrashHandler64.exe", StringComparison.OrdinalIgnoreCase) ||
                name.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)) yield return Describe(path, name);
        }
        var pending = new Stack<string>(); pending.Push(Path.Combine(game, Data));
        while (pending.Count > 0) {
            var directory = pending.Pop(); NoReparse(directory);
            foreach (var child in System.IO.Directory.GetDirectories(directory)) { NoReparse(child); pending.Push(child); }
            foreach (var path in System.IO.Directory.GetFiles(directory)) {
                NoReparse(path);
                var name = Path.GetFileName(path);
                // Test plugins and historical logs must not reach player copies.
                if (name.IndexOf("HollowConnect.Smoke", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    name.IndexOf("HollowConnect.DualLaunchProbe", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    name.EndsWith(".log", StringComparison.OrdinalIgnoreCase) || name.IndexOf(".disabled", StringComparison.OrdinalIgnoreCase) >= 0) continue;
                yield return Describe(path, path.Substring(game.Length + 1));
            }
        }
    }
    private static SourceFile Describe(string path, string relative) {
        NoReparse(path); var info = new FileInfo(path);
        return new SourceFile { Path = path, Relative = relative, Length = info.Length, Modified = info.LastWriteTimeUtc.Ticks };
    }
    private static bool Linkable(SourceFile file) {
        // Only native, read-only scene/resource data. Settings, executables,
        // assemblies, mods and every potentially writable file are real copies.
        var name = Path.GetFileName(file.Path);
        return Path.GetDirectoryName(file.Relative).Equals(Data, StringComparison.OrdinalIgnoreCase) &&
            (name.EndsWith(".resource", StringComparison.OrdinalIgnoreCase) || name.EndsWith(".ress", StringComparison.OrdinalIgnoreCase) ||
             name.EndsWith(".assets", StringComparison.OrdinalIgnoreCase) || Regex.IsMatch(name, @"^level\d+$"));
    }
    internal static PreparedGame Prepare(string input, string workspace, Action<string> report) {
        var game = ResolveGame(input);
        var original = File.ReadAllBytes(Path.Combine(game, Data, "globalgamemanagers"));
        var settings = new[] { GameSettings.Patch(original, 1), GameSettings.Patch(original, 2) };
        workspace = Canonical(workspace); NoReparse(workspace);
        if (game.Equals(workspace, StringComparison.OrdinalIgnoreCase) || Within(workspace, game) || Within(game, workspace))
            throw new IOException("双开副本目录必须与原游戏目录分开，且不能互相包含。");
        var files = Files(game).OrderBy(file => file.Relative, StringComparer.OrdinalIgnoreCase).ToArray();
        // Hash all DLLs and mutable settings; metadata suffices for immutable
        // scene data. This also makes mod updates select a fresh generation.
        var fingerprint = new StringBuilder(game).Append('|').Append(GameSettings.Hash(original));
        foreach (var file in files) {
            fingerprint.Append('|').Append(file.Relative).Append('|').Append(file.Length).Append('|').Append(file.Modified);
            if (file.Relative.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) ||
                file.Relative.EndsWith(".config", StringComparison.OrdinalIgnoreCase) || file.Relative.EndsWith(".info", StringComparison.OrdinalIgnoreCase))
                fingerprint.Append('|').Append(GameSettings.Hash(File.ReadAllBytes(file.Path)));
        }
        var identity = GameSettings.Hash(Encoding.UTF8.GetBytes(fingerprint.ToString())).Substring(0, 24);
        System.IO.Directory.CreateDirectory(workspace);
        var marker = Path.Combine(workspace, ".hollowconnect-dual");
        if (!File.Exists(marker)) {
            if (System.IO.Directory.EnumerateFileSystemEntries(workspace).Any()) throw new IOException("副本目录已有其他文件，请选择一个空文件夹。");
            File.WriteAllText(marker, Owner, Encoding.UTF8);
        } else if (File.ReadAllText(marker) != Owner) throw new IOException("副本目录标记无效，请选择一个空文件夹。");
        using (var gate = new FileStream(Path.Combine(workspace, ".prepare.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None)) {
            var final = Path.Combine(workspace, "build-" + identity); NoReparse(final);
            var ready = Path.Combine(final, "ready.json");
            if (File.Exists(ready)) {
                var cached = new JavaScriptSerializer().Deserialize<PreparedGame>(File.ReadAllText(ready));
                cached.Directory = final; cached.Source = game;
                cached.Games = new[] { Path.Combine(final, "P1"), Path.Combine(final, "P2") };
                cached.Saves = new[] { SaveDirectory(1), SaveDirectory(2) };
                for (var i = 0; i < 2; i++) {
                    NoReparse(cached.Games[i]);
                    var asset = Path.Combine(cached.Games[i], Data, "globalgamemanagers"); NoReparse(asset);
                    if (!File.ReadAllBytes(asset).SequenceEqual(settings[i])) throw new IOException("已有副本的游戏设置发生变化。请选择新的空副本目录。");
                    if (!File.Exists(Path.Combine(cached.Games[i], "hollow_knight.exe"))) throw new IOException("已有副本不完整。请选择新的空副本目录。");
                }
                report("已复用准备好的双开副本。"); return cached;
            }
            var staging = final + ".incomplete-" + Guid.NewGuid().ToString("N");
            var result = new PreparedGame { Source = game, Directory = final, Games = new[] { Path.Combine(final, "P1"), Path.Combine(final, "P2") }, Saves = new[] { SaveDirectory(1), SaveDirectory(2) } };
            for (var i = 0; i < 2; i++) {
                var destination = Path.Combine(staging, "P" + (i + 1)); System.IO.Directory.CreateDirectory(destination);
                report("正在准备玩家 " + (i + 1) + " 的游戏副本…");
                foreach (var file in files) {
                    var target = Path.Combine(destination, file.Relative); System.IO.Directory.CreateDirectory(Path.GetDirectoryName(target));
                    if (file.Relative.Equals(Data + "\\globalgamemanagers", StringComparison.OrdinalIgnoreCase)) File.WriteAllBytes(target, settings[i]);
                    else if (file.Relative.Equals(Data + "\\boot.config", StringComparison.OrdinalIgnoreCase)) File.WriteAllText(target, GameSettings.Boot(File.ReadAllText(file.Path)), new UTF8Encoding(false));
                    else if (file.Relative.Equals(Data + "\\app.info", StringComparison.OrdinalIgnoreCase)) File.WriteAllText(target, "Team Cherry\n" + GameSettings.Product(i + 1), new UTF8Encoding(false));
                    else if (Linkable(file) && CreateHardLink(target, file.Path, IntPtr.Zero)) { result.LinkedFiles++; continue; }
                    else File.Copy(file.Path, target);
                    result.CopiedFiles++;
                }
                // app.info may be absent in an otherwise compatible build.
                File.WriteAllText(Path.Combine(destination, Data, "app.info"), "Team Cherry\n" + GameSettings.Product(i + 1), new UTF8Encoding(false));
            }
            // Refuse a mixed generation if the source changed during copying.
            foreach (var file in files) {
                var info = new FileInfo(file.Path);
                if (info.Length != file.Length || info.LastWriteTimeUtc.Ticks != file.Modified) throw new IOException("准备期间游戏文件发生变化，请关闭更新程序后重试。");
            }
            File.WriteAllText(Path.Combine(staging, "ready.json"), new JavaScriptSerializer().Serialize(result), Encoding.UTF8);
            System.IO.Directory.Move(staging, final);
            report("双开副本已就绪；两个玩家使用独立的新存档目录。"); return result;
        }
    }
    internal static ProcessStartInfo StartInfo(string directory, string log) {
        NoReparse(directory); NoReparse(log);
        var start = new ProcessStartInfo(Path.Combine(directory, "hollow_knight.exe"), "-screen-fullscreen 0 -screen-width 960 -screen-height 600 -logFile \"" + log + "\"") {
            WorkingDirectory = directory, UseShellExecute = false
        };
        start.EnvironmentVariables["SteamAppId"] = "367520";
        start.EnvironmentVariables["SteamGameId"] = "367520";
        // Developer fixture flags must never leak into a player launch.
        foreach (var key in start.EnvironmentVariables.Keys.Cast<string>().Where(key => key.StartsWith("HOLLOWCONNECT_", StringComparison.OrdinalIgnoreCase)).ToArray())
            start.EnvironmentVariables.Remove(key);
        return start;
    }
    internal static void Launch(PreparedGame prepared, Action<string> report) {
        if (!Process.GetProcessesByName("steam").Any()) throw new InvalidOperationException("请先打开 Steam，并登录拥有《空洞骑士》的账号。");
        var games = prepared.Games.Select(Canonical).ToArray();
        foreach (var process in Process.GetProcessesByName("hollow_knight")) {
            using (process) {
                string path;
                try { path = process.MainModule.FileName; } catch { throw new IOException("无法确认已有游戏的路径，请先关闭《空洞骑士》后再双开。"); }
                if (Within(path, Path.GetDirectoryName(prepared.Directory))) throw new IOException("已有双开窗口正在运行，请先退出两个窗口。");
            }
        }
        var logs = Path.Combine(Path.GetDirectoryName(prepared.Directory), "logs", DateTime.Now.ToString("yyyyMMdd-HHmmss-fff"));
        System.IO.Directory.CreateDirectory(logs);
        for (var i = 0; i < 2; i++) {
            var log = Path.Combine(logs, "P" + (i + 1) + ".log");
            using (var process = Process.Start(StartInfo(games[i], log))) {
                report("已启动玩家 " + (i + 1) + "，进程 " + process.Id + "。");
                // Modding API rotates its log during initialization. Wait for
                // this player's loader, not merely for the OS window to exist.
                var deadline = DateTime.UtcNow.AddSeconds(90);
                var loaded = false;
                while (DateTime.UtcNow < deadline && !loaded) {
                    if (process.HasExited) throw new IOException("玩家 " + (i + 1) + " 提前退出，请查看日志：" + log);
                    if (File.Exists(log)) {
                        using (var stream = new FileStream(log, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                        using (var reader = new StreamReader(stream)) {
                            var text = reader.ReadToEnd();
                            loaded = text.Contains("Initializing Mod:") || text.Contains("Initializing mod:") || text.Contains("Modding API") || text.Contains("[API] - ModLoader");
                        }
                    }
                    if (!loaded) Thread.Sleep(250);
                }
                if (!loaded) throw new TimeoutException("玩家 " + (i + 1) + " 未在 90 秒内完成模组启动。请检查是否已安装 HollowConnect。日志：" + log);
            }
        }
        report("两个窗口已启动。P1 选择 Host，P2 连接 127.0.0.1，端口填写 P1 的开房端口。");
    }
    internal static string[] DetectGames() {
        var roots = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        using (var key = Registry.CurrentUser.OpenSubKey(@"Software\Valve\Steam")) {
            var value = key?.GetValue("SteamPath") as string; if (!string.IsNullOrWhiteSpace(value)) roots.Add(value);
        }
        roots.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Steam"));
        var libraries = new HashSet<string>(roots, StringComparer.OrdinalIgnoreCase);
        foreach (var root in roots) {
            var file = Path.Combine(root, "steamapps", "libraryfolders.vdf");
            if (!File.Exists(file)) continue;
            foreach (Match match in Regex.Matches(File.ReadAllText(file), "\"(?:path|[0-9]+)\"\\s*\"([^\"]+)\""))
                if (Path.IsPathRooted(match.Groups[1].Value)) libraries.Add(match.Groups[1].Value.Replace("\\\\", "\\"));
        }
        return libraries.Select(library => Path.Combine(library, "steamapps", "common", "Hollow Knight"))
            .Where(path => File.Exists(Path.Combine(path, "hollow_knight.exe"))).ToArray();
    }
}
