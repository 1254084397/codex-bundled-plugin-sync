using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Text;
using System.Threading;
using System.Windows.Forms;
using CodexPluginSync.Core;

namespace CodexPluginSync.Gui
{
    /// <summary>
    /// M3 最小可用界面：一件事一按钮，结果写在同一块日志里。
    /// 所有写操作都要二次确认；执行期间按钮禁用，避免并发写入。
    /// 引擎层与 CLI 完全共用（同一个 Core），界面不做任何自己的业务判断。
    /// </summary>
    public sealed class MainForm : Form
    {
        private readonly Label _headline = new Label();
        private readonly Label _detail = new Label();
        private readonly TextBox _log = new TextBox();
        private readonly Button _check = new Button();
        private readonly Button _sync = new Button();
        private readonly Button _rollback = new Button();
        private readonly Button _cleanup = new Button();
        private readonly Button _diag = new Button();
        private readonly Button _openLogs = new Button();
        private readonly Button _openHome = new Button();
        private readonly ProgressBar _progress = new ProgressBar();

        private string _codexHome;
        private bool _busy;

        public MainForm()
        {
            _codexHome = CodexPaths.ResolveCodexHome(Settings.Load(null).CodexHome);

            Text = "Codex 插件同步 " + Cli.CliRunner.ToolVersion;
            MinimumSize = new Size(720, 520);
            Size = new Size(820, 600);
            StartPosition = FormStartPosition.CenterScreen;
            Font = new Font("Microsoft YaHei UI", 9f);

            TableLayoutPanel root = new TableLayoutPanel();
            root.Dock = DockStyle.Fill;
            root.ColumnCount = 1;
            root.RowCount = 5;
            root.Padding = new Padding(12);
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            root.RowStyles.Add(new RowStyle(SizeType.Percent, 100f));
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            Controls.Add(root);

            _headline.Text = "正在读取状态…";
            _headline.Font = new Font(Font.FontFamily, 12f, FontStyle.Bold);
            _headline.AutoSize = true;
            _headline.Margin = new Padding(0, 0, 0, 4);
            root.Controls.Add(_headline, 0, 0);

            _detail.Text = _codexHome;
            _detail.AutoSize = true;
            _detail.ForeColor = SystemColors.GrayText;
            _detail.Margin = new Padding(0, 0, 0, 10);
            root.Controls.Add(_detail, 0, 1);

            FlowLayoutPanel buttons = new FlowLayoutPanel();
            buttons.AutoSize = true;
            buttons.WrapContents = true;
            buttons.Margin = new Padding(0, 0, 0, 8);
            root.Controls.Add(buttons, 0, 2);

            ConfigureButton(_check, "检查", delegate { RunCheck(); });
            ConfigureButton(_sync, "同步", delegate { RunWithConfirm("同步会替换 Codex 的内置插件目录（先自动备份，可回滚）。继续？", RunSync); });
            ConfigureButton(_rollback, "回滚…", delegate { RunRollback(); });
            ConfigureButton(_cleanup, "清理", delegate { RunWithConfirm("清理会删除过期的 stage、旧备份与旧日志（当前操作引用的内容不会删）。继续？", RunCleanup); });
            ConfigureButton(_diag, "导出诊断", delegate { RunDiag(); });
            ConfigureButton(_openLogs, "打开日志", delegate { OpenFolder(CodexPaths.ToolLogDirectory); });
            ConfigureButton(_openHome, "打开 CodexHome", delegate { OpenFolder(_codexHome); });

            buttons.Controls.Add(_check);
            buttons.Controls.Add(_sync);
            buttons.Controls.Add(_rollback);
            buttons.Controls.Add(_cleanup);
            buttons.Controls.Add(_diag);
            buttons.Controls.Add(_openLogs);
            buttons.Controls.Add(_openHome);

            _log.Multiline = true;
            _log.ReadOnly = true;
            _log.ScrollBars = ScrollBars.Vertical;
            _log.Dock = DockStyle.Fill;
            _log.BackColor = SystemColors.Window;
            _log.Font = new Font("Consolas", 9f);
            root.Controls.Add(_log, 0, 3);

            _progress.Style = ProgressBarStyle.Marquee;
            _progress.Visible = false;
            _progress.Dock = DockStyle.Fill;
            _progress.Height = 6;
            root.Controls.Add(_progress, 0, 4);

            Shown += delegate { RefreshStatus(); };
        }

        private void ConfigureButton(Button button, string text, EventHandler handler)
        {
            button.Text = text;
            button.AutoSize = true;
            button.Padding = new Padding(8, 2, 8, 2);
            button.Click += delegate(object sender, EventArgs e)
            {
                if (_busy) { return; }
                handler(sender, e);
            };
        }

        private void AppendLog(string line)
        {
            if (string.IsNullOrEmpty(line)) { return; }
            if (_log.TextLength == 0)
            {
                _log.Text = line;
            }
            else
            {
                _log.AppendText(Environment.NewLine + line);
            }
        }

        /// <summary>所有耗时动作都在后台线程跑，日志回到 UI 线程写。</summary>
        private void RunAsync(string headline, Action<TextWriter> work)
        {
            RunAsync(headline, work, true);
        }

        private void RunAsync(string headline, Action<TextWriter> work, bool refreshAfterwards)
        {
            if (_busy) { return; }
            _busy = true;
            SetButtonsEnabled(false);
            _headline.Text = headline;
            _progress.Visible = true;

            Thread thread = new Thread(delegate()
            {
                StringWriter writer = new StringWriter();
                string error = null;
                try
                {
                    work(writer);
                }
                catch (Exception ex)
                {
                    error = ex.GetType().Name + ": " + ex.Message;
                }

                string text = writer.ToString();
                if (error != null)
                {
                    text += (text.Length > 0 ? Environment.NewLine : string.Empty) + "错误：" + error;
                }

                if (IsHandleCreated && !IsDisposed)
                {
                    try
                    {
                        BeginInvoke((MethodInvoker)delegate
                        {
                            string[] lines = text.Replace("\r\n", "\n").Split('\n');
                            for (int i = 0; i < lines.Length; i++) { AppendLog(lines[i]); }
                            TrimLog();
                            _busy = false;
                            _progress.Visible = false;
                            SetButtonsEnabled(true);
                            // 刷新自己不能再触发刷新，否则会无限循环。
                            if (refreshAfterwards) { RefreshStatus(); }
                        });
                    }
                    catch (Exception)
                    {
                        // 窗口已关闭：忽略
                    }
                }
            });
            thread.IsBackground = true;
            thread.Start();
        }

        private void TrimLog()
        {
            const int maxChars = 200000;
            if (_log.TextLength <= maxChars) { return; }
            _log.Text = "…（已截断较早输出）" + Environment.NewLine +
                _log.Text.Substring(_log.TextLength - maxChars / 2);
            _log.SelectionStart = _log.TextLength;
            _log.ScrollToCaret();
        }

        private void SetButtonsEnabled(bool enabled)
        {
            _check.Enabled = enabled;
            _sync.Enabled = enabled;
            _rollback.Enabled = enabled;
            _cleanup.Enabled = enabled;
            _diag.Enabled = enabled;
            _openLogs.Enabled = enabled;
            _openHome.Enabled = enabled;
            Cursor = enabled ? Cursors.Default : Cursors.WaitCursor;
        }

        private void RunWithConfirm(string question, Action run)
        {
            DialogResult answer = MessageBox.Show(this, question, "确认", MessageBoxButtons.OKCancel, MessageBoxIcon.Warning);
            if (answer == DialogResult.OK)
            {
                run();
            }
        }

        private void RefreshStatus()
        {
            RunAsync("正在读取状态…", delegate(TextWriter writer)
            {
                Settings settings = Settings.Load(null);
                CheckOptions options = new CheckOptions();
                options.CodexHome = CodexPaths.ResolveCodexHome(settings.CodexHome);
                options.MinimumFreeGB = settings.MinimumFreeGB;
                options.ExtraLockingProcesses = settings.ExtraLockingProcesses;

                CheckEngine engine = new CheckEngine(options, null);
                CheckOutcome outcome = engine.Execute();
                Snapshot snapshot = outcome.Snapshot;

                writer.WriteLine("CodexHome：" + snapshot.CodexHome);
                if (snapshot.Desktop != null && snapshot.Desktop.Found)
                {
                    writer.WriteLine("Codex Desktop：" + snapshot.Desktop.Version);
                }
                for (int i = 0; i < snapshot.Components.Count; i++)
                {
                    ComponentInfo component = snapshot.Components[i];
                    if (component.Type != "cache") { continue; }
                    writer.WriteLine("  " + component.Name + "：" + component.Actual +
                        (component.State == ComponentStates.Ok ? "（最新）" : "（" + component.State + "）"));
                }
                if (snapshot.NativeHost != null && snapshot.NativeHost.Chrome != null)
                {
                    writer.WriteLine("Chrome native host：" + snapshot.NativeHost.Chrome.State);
                }
                if (snapshot.Efs != null)
                {
                    writer.WriteLine("EFS：" + snapshot.Efs.StagingParentEncrypted);
                }
                if (snapshot.Journal != null)
                {
                    writer.WriteLine("未收尾操作：" + snapshot.Journal.Phase + "（" + snapshot.Journal.OperationId + "）");
                }
                if (snapshot.Backups.Newest != null)
                {
                    writer.WriteLine("最新备份：" + snapshot.Backups.Newest.BackupId + "（" + snapshot.Backups.Newest.Status + "）");
                }
                if (snapshot.LockingProcesses.Count > 0)
                {
                    StringBuilder sb = new StringBuilder();
                    for (int i = 0; i < snapshot.LockingProcesses.Count; i++)
                    {
                        if (i > 0) { sb.Append("、"); }
                        sb.Append(snapshot.LockingProcesses[i].Name);
                    }
                    writer.WriteLine("⚠ 正在运行、会占用插件目录：" + sb.ToString() + "（同步前请先退出）");
                }
                writer.WriteLine("判定：" + VerdictText(snapshot.Verdict) + "｜退出码 " + outcome.ExitCode);
                if (outcome.ErrorMessage != null) { writer.WriteLine("错误：" + outcome.ErrorMessage); }

                if (IsHandleCreated && !IsDisposed)
                {
                    try
                    {
                        BeginInvoke((MethodInvoker)delegate
                        {
                            _headline.Text = HeadlineText(snapshot.Verdict);
                        });
                    }
                    catch (Exception) { }
                }
            }, false);
        }

        private static string HeadlineText(string verdict)
        {
            if (verdict == Verdicts.Ok) { return "状态：正常，无需更新"; }
            if (verdict == Verdicts.UpdateNeeded) { return "状态：需要同步"; }
            if (verdict == Verdicts.Blocked) { return "状态：被阻塞（有正在运行的程序或未收尾操作）"; }
            return "状态：检查失败（见下方日志）";
        }

        private static string VerdictText(string verdict)
        {
            if (verdict == Verdicts.Ok) { return "正常"; }
            if (verdict == Verdicts.UpdateNeeded) { return "需要同步"; }
            if (verdict == Verdicts.Blocked) { return "被阻塞"; }
            return "错误";
        }

        private void RunCheck()
        {
            RunAsync("正在检查…", delegate(TextWriter writer)
            {
                Settings settings = Settings.Load(null);
                CheckOptions options = new CheckOptions();
                options.CodexHome = CodexPaths.ResolveCodexHome(settings.CodexHome);
                options.MinimumFreeGB = settings.MinimumFreeGB;
                options.ExtraLockingProcesses = settings.ExtraLockingProcesses;
                CheckEngine engine = new CheckEngine(options, null);
                CheckOutcome outcome = engine.Execute();
                writer.WriteLine("判定：" + VerdictText(outcome.Snapshot.Verdict) + "（退出码 " + outcome.ExitCode + "）");
                if (outcome.ErrorMessage != null) { writer.WriteLine("错误：" + outcome.ErrorMessage); }
                for (int i = 0; i < outcome.Warnings.Count; i++) { writer.WriteLine("警告：" + outcome.Warnings[i]); }
            });
        }

        private void RunSync()
        {
            RunAsync("正在同步（先备份，再替换）…", delegate(TextWriter writer)
            {
                SyncOptions options = new SyncOptions();
                options.CodexHome = _codexHome;
                options.Out = writer;
                options.Err = writer;
                SyncRunResult result = SyncEngine.Run(options);
                writer.WriteLine("结果：" + (result.Status ?? "(未知)") + "｜退出码 " + result.ExitCode);
                if (!string.IsNullOrEmpty(result.Message)) { writer.WriteLine(result.Message); }
            });
        }

        private void RunRollback()
        {
            RunAsync("读取备份列表…", delegate(TextWriter writer)
            {
                BackupsInfo info = BackupStore.Enumerate(new CodexPaths(_codexHome).BackupsRoot);

                List<string> candidates = new List<string>();
                string root = new CodexPaths(_codexHome).BackupsRoot;
                if (Directory.Exists(root))
                {
                    string[] directories = Directory.GetDirectories(root);
                    for (int i = 0; i < directories.Length; i++)
                    {
                        string manifestPath = Path.Combine(directories[i], BackupManifestStore.FileName);
                        if (!File.Exists(manifestPath)) { continue; }
                        try
                        {
                            BackupManifestDto dto = JsonRead.FromFile<BackupManifestDto>(manifestPath);
                            if (dto == null || dto.Status != BackupStates.Complete) { continue; }
                            candidates.Add(dto.BackupId + "  (" + dto.CreatedUtc + ")");
                        }
                        catch (Exception) { }
                    }
                }
                if (candidates.Count == 0 && info.Newest == null)
                {
                    writer.WriteLine("没有可回滚的备份。");
                    return;
                }
                SelectedBackup selection = PromptBackup(candidates);
                if (selection == null)
                {
                    writer.WriteLine("已取消回滚。");
                    return;
                }
                writer.WriteLine("使用备份：" + (selection.BackupId ?? "最新 status=complete"));
                RollbackOptions options = new RollbackOptions();
                options.CodexHome = _codexHome;
                options.BackupId = selection.BackupId;
                options.Out = writer;
                options.Err = writer;
                SyncRunResult result = RollbackEngine.Run(options);
                writer.WriteLine("结果：" + (result.Status ?? "(未知)") + "｜退出码 " + result.ExitCode);
            });
        }

        private sealed class SelectedBackup
        {
            public string BackupId;
        }

        private SelectedBackup PromptBackup(List<string> candidates)
        {
            SelectedBackup selection = null;
            using (Form dialog = new Form())
            {
                dialog.Text = "选择要回滚到的备份";
                dialog.StartPosition = FormStartPosition.CenterParent;
                dialog.Width = 460;
                dialog.Height = 260;
                dialog.Font = Font;

                ListBox list = new ListBox();
                list.Dock = DockStyle.Fill;
                list.Items.Add("（最新 status=complete）");
                for (int i = 0; i < candidates.Count; i++) { list.Items.Add(candidates[i]); }
                list.SelectedIndex = 0;

                FlowLayoutPanel panel = new FlowLayoutPanel();
                panel.Dock = DockStyle.Bottom;
                panel.FlowDirection = FlowDirection.RightToLeft;
                panel.Height = 44;

                Button ok = new Button();
                ok.Text = "回滚";
                ok.DialogResult = DialogResult.OK;
                ok.Width = 84;

                Button cancel = new Button();
                cancel.Text = "取消";
                cancel.DialogResult = DialogResult.Cancel;
                cancel.Width = 84;

                panel.Controls.Add(ok);
                panel.Controls.Add(cancel);
                dialog.Controls.Add(list);
                dialog.Controls.Add(panel);
                dialog.AcceptButton = ok;
                dialog.CancelButton = cancel;

                if (dialog.ShowDialog(this) == DialogResult.OK && list.SelectedIndex >= 0)
                {
                    selection = new SelectedBackup();
                    string text = list.SelectedItem as string;
                    if (list.SelectedIndex == 0 || string.IsNullOrEmpty(text))
                    {
                        selection.BackupId = null;
                    }
                    else
                    {
                        int space = text.IndexOf("  (", StringComparison.Ordinal);
                        selection.BackupId = space > 0 ? text.Substring(0, space) : text;
                    }
                }
            }
            return selection;
        }

        private void RunCleanup()
        {
            RunAsync("正在清理…", delegate(TextWriter writer)
            {
                CleanupOptions options = new CleanupOptions();
                options.CodexHome = _codexHome;
                options.Out = writer;
                options.Err = writer;
                SyncRunResult result = CleanupEngine.Run(options);
                writer.WriteLine("结果：" + (result.Status ?? "(未知)") + "｜退出码 " + result.ExitCode);
            });
        }

        private void RunDiag()
        {
            SaveFileDialog dialog = new SaveFileDialog();
            dialog.Title = "保存诊断包";
            dialog.Filter = "Zip 包 (*.zip)|*.zip";
            dialog.FileName = "CodexPluginSync-diag-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + ".zip";
            if (dialog.ShowDialog(this) != DialogResult.OK)
            {
                return;
            }
            string path = dialog.FileName;
            RunAsync("正在导出诊断包…", delegate(TextWriter writer)
            {
                DiagOptions options = new DiagOptions();
                options.CodexHome = _codexHome;
                options.OutPath = path;
                options.Out = writer;
                options.Err = writer;
                SyncRunResult result = DiagExporter.Run(options);
                writer.WriteLine("结果：" + (result.Status ?? "(未知)") + "｜退出码 " + result.ExitCode);
                writer.WriteLine("提示：默认已脱敏；提交 issue 时附带这个 zip 即可。");
            });
        }

        private void OpenFolder(string path)
        {
            try
            {
                if (string.IsNullOrEmpty(path) || !Directory.Exists(path))
                {
                    MessageBox.Show(this, "目录不存在：" + path, "提示", MessageBoxButtons.OK, MessageBoxIcon.Information);
                    return;
                }
                System.Diagnostics.Process.Start("explorer.exe", "\"" + path + "\"");
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, ex.Message, "无法打开目录", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }
    }
}
