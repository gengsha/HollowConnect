using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace HollowConnect.Installer;

internal static class Program {
    [STAThread]
    private static int Main(string[] args) {
        // The same execution path is available for unattended installations.
        if (args.Length == 1 && args[0] == "--detect") {
            var result = ScriptRunner.RunAsync("GamePath.ps1", " -List").GetAwaiter().GetResult();
            WriteOutput(result.Output);
            return result.ExitCode;
        }
        if (args.Length == 2 && (args[0] == "--install" || args[0] == "--uninstall")) {
            var result = ScriptRunner.RunAsync(args[0] == "--install" ? "Install.ps1" : "Uninstall.ps1",
                " -GamePath " + ScriptRunner.Quote(args[1])).GetAwaiter().GetResult();
            WriteOutput(result.Output);
            return result.ExitCode;
        }
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        Application.Run(new InstallerForm());
        return 0;
    }
    private static void WriteOutput(string text) {
        // A Windows GUI executable has no console code page. Write directly to
        // the redirected pipe instead of calling Console.set_OutputEncoding.
        using (var writer = new StreamWriter(Console.OpenStandardOutput(), new UTF8Encoding(false))) writer.Write(text);
    }
}

internal sealed class ScriptResult {
    internal int ExitCode;
    internal string Output;
}

internal static class ScriptRunner {
    internal static string Quote(string text) => "'" + text.Replace("'", "''") + "'";
    internal static async Task<ScriptResult> RunAsync(string script, string arguments) {
        var scriptPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, script);
        if (!File.Exists(scriptPath)) throw new FileNotFoundException("请先完整解压安装包，保留安装器旁的所有文件。", scriptPath);
        // EncodedCommand keeps apostrophes, spaces, Chinese and shell characters
        // in paths literal. No cmd.exe or interpolated shell command is used.
        var command = "$ProgressPreference = 'SilentlyContinue'; [Console]::OutputEncoding = [Text.UTF8Encoding]::new($false); " +
            "$ErrorActionPreference = 'Stop'; try { & " + Quote(scriptPath) + arguments +
            "; exit 0 } catch { [Console]::Error.WriteLine($_.ToString()); exit 1 }";
        var powershell = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), @"WindowsPowerShell\v1.0\powershell.exe");
        var start = new ProcessStartInfo(powershell, "-NoLogo -NoProfile -NonInteractive -OutputFormat Text -ExecutionPolicy Bypass -EncodedCommand " +
            Convert.ToBase64String(Encoding.Unicode.GetBytes(command))) {
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8,
            WorkingDirectory = AppDomain.CurrentDomain.BaseDirectory
        };
        // Launching this EXE from PowerShell 7 can otherwise make Windows
        // PowerShell import incompatible Core modules (even Get-FileHash fails).
        // Only the child process uses this path; the user's environment is intact.
        start.EnvironmentVariables["PSModulePath"] = Path.Combine(Path.GetDirectoryName(powershell), "Modules") +
            Path.PathSeparator + Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), @"WindowsPowerShell\Modules");
        using (var process = Process.Start(start)) {
            var output = process.StandardOutput.ReadToEndAsync();
            var error = process.StandardError.ReadToEndAsync();
            await Task.WhenAll(output, error);
            await Task.Run(() => process.WaitForExit());
            return new ScriptResult { ExitCode = process.ExitCode, Output = output.Result + error.Result };
        }
    }
}

internal sealed class InstallerForm : Form {
    private readonly ComboBox paths = new ComboBox { Dock = DockStyle.Fill, DropDownStyle = ComboBoxStyle.DropDown };
    private readonly Button browse = new Button { Text = "选择游戏…", AutoSize = true };
    private readonly Button scan = new Button { Text = "重新查找", AutoSize = true };
    private readonly Button install = new Button { Text = "安装 / 更新", AutoSize = true, Enabled = false };
    private readonly Button restore = new Button { Text = "卸载并恢复", AutoSize = true, Enabled = false };
    private readonly Label status = new Label { Text = "正在查找 Steam 游戏库…", AutoSize = true, Dock = DockStyle.Fill };
    private readonly TextBox log = new TextBox { Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical, Dock = DockStyle.Fill };
    private bool busy;

    internal InstallerForm() {
        Text = "HollowConnect 安装器";
        Font = new Font("Microsoft YaHei UI", 10f);
        ClientSize = new Size(740, 440); MinimumSize = new Size(650, 420);
        StartPosition = FormStartPosition.CenterScreen;
        AutoScaleMode = AutoScaleMode.Dpi;
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(22), ColumnCount = 1, RowCount = 6 };
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.Controls.Add(new Label { Text = "安装 HollowConnect 协作联机模组", AutoSize = true,
            Font = new Font(Font.FontFamily, 16, FontStyle.Bold), Margin = new Padding(0, 0, 0, 14) }, 0, 0);
        layout.Controls.Add(new Label { Text = "自动识别各盘的 Steam 游戏库。也可选择 hollow_knight.exe，或粘贴游戏目录。",
            AutoSize = true, Margin = new Padding(0, 0, 0, 12) }, 0, 1);
        var location = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 3, AutoSize = true };
        location.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        location.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize)); location.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        location.Controls.Add(paths, 0, 0); location.Controls.Add(browse, 1, 0); location.Controls.Add(scan, 2, 0);
        layout.Controls.Add(location, 0, 2);
        var actions = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill, Margin = new Padding(0, 12, 0, 10) };
        actions.Controls.Add(install); actions.Controls.Add(restore);
        actions.Controls.Add(new Label { Text = "安装前请关闭游戏。自动备份；卸载保留当前存档。", AutoSize = true, Padding = new Padding(8) });
        layout.Controls.Add(actions, 0, 3);
        status.Margin = new Padding(0, 0, 0, 10); layout.Controls.Add(status, 0, 4); layout.Controls.Add(log, 0, 5);
        Controls.Add(layout);
        paths.TextChanged += (_, _) => RefreshActions();
        browse.Click += (_, _) => Browse(); scan.Click += async (_, _) => await DetectAsync();
        install.Click += async (_, _) => await ApplyAsync(false);
        restore.Click += async (_, _) => await ApplyAsync(true);
        Shown += async (_, _) => await DetectAsync();
        FormClosing += (_, e) => { if (busy) { e.Cancel = true; status.Text = "正在完成操作，请稍候。"; } };
    }

    private void RefreshActions() { install.Enabled = restore.Enabled = !busy && !string.IsNullOrWhiteSpace(paths.Text); }
    private void SetBusy(bool value) { busy = value; browse.Enabled = scan.Enabled = paths.Enabled = !value; RefreshActions(); }
    private void Browse() {
        using (var picker = new OpenFileDialog { Title = "选择《空洞骑士》的 hollow_knight.exe", Filter = "Hollow Knight|hollow_knight.exe", CheckFileExists = true }) {
            if (Directory.Exists(paths.Text)) picker.InitialDirectory = paths.Text;
            if (picker.ShowDialog(this) == DialogResult.OK) { paths.Text = Path.GetDirectoryName(picker.FileName); status.Text = "已选择游戏，点击“安装 / 更新”即可。"; }
        }
    }
    private async Task DetectAsync() {
        SetBusy(true); status.Text = "正在查找 Steam 游戏库…";
        try {
            var result = await ScriptRunner.RunAsync("GamePath.ps1", " -List");
            if (result.ExitCode != 0) throw new InvalidOperationException(result.Output);
            var found = result.Output.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries).Where(Directory.Exists).ToArray();
            paths.Items.Clear(); paths.Items.AddRange(found);
            if (found.Length == 1) { paths.Text = found[0]; status.Text = "已找到游戏，点击“安装 / 更新”即可。"; }
            else if (found.Length > 1) { paths.Text = ""; status.Text = "找到多个游戏副本，请从列表选择要安装的目录。"; }
            else { status.Text = "未自动找到游戏，请点击“选择游戏…”找到 hollow_knight.exe。"; }
        } catch (Exception e) { status.Text = "自动查找失败，可手动选择游戏。"; log.Text = e.Message; }
        finally { SetBusy(false); }
    }
    private async Task ApplyAsync(bool uninstall) {
        var game = paths.Text.Trim().Trim('"');
        if (File.Exists(game) && Path.GetFileName(game).Equals("hollow_knight.exe", StringComparison.OrdinalIgnoreCase)) game = Path.GetDirectoryName(game);
        if (!File.Exists(Path.Combine(game, "hollow_knight.exe")) || !File.Exists(Path.Combine(game, @"hollow_knight_Data\Managed\Assembly-CSharp.dll"))) {
            status.Text = "所选目录不是《空洞骑士》游戏目录，请重新选择。"; return;
        }
        if (uninstall && MessageBox.Show(this, "恢复安装前的模组和游戏文件？当前存档将保留。", "卸载 HollowConnect", MessageBoxButtons.OKCancel, MessageBoxIcon.Question) != DialogResult.OK) return;
        SetBusy(true); status.Text = uninstall ? "正在恢复安装前文件…" : "正在校验、备份和安装…"; log.Clear();
        try {
            var result = await ScriptRunner.RunAsync(uninstall ? "Uninstall.ps1" : "Install.ps1", " -GamePath " + ScriptRunner.Quote(game));
            log.Text = result.Output;
            var logDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "HollowConnect", "Installer");
            try {
                Directory.CreateDirectory(logDirectory);
                var logPath = Path.Combine(logDirectory, DateTime.Now.ToString("yyyyMMdd-HHmmss-fff") + ".log");
                File.WriteAllText(logPath, "Game: " + game + Environment.NewLine + result.Output, Encoding.UTF8);
                log.AppendText(Environment.NewLine + "日志：" + logPath);
            } catch (IOException) { } catch (UnauthorizedAccessException) { }
            if (result.ExitCode == 0) {
                status.Text = uninstall ? "卸载完成，当前存档已保留。" : "安装完成！可以启动游戏；所有队友请使用同一模组版本。";
            } else {
                status.Text = "操作失败，请查看下方日志。";
                if (result.Output.IndexOf("denied", StringComparison.OrdinalIgnoreCase) >= 0 || result.Output.Contains("拒绝访问"))
                    status.Text += " 写入权限不足时，请右键安装器选择“以管理员身份运行”。";
            }
        } catch (Exception e) { status.Text = "操作未完成，请查看下方日志。"; log.Text = e.Message; }
        finally { SetBusy(false); }
    }
}
