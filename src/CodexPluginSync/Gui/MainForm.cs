using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Text;
using System.Threading;
using System.Windows.Forms;
using CodexPluginSync.Core;

namespace CodexPluginSync.Gui
{
    /// <summary>
    /// §M3 用户主界面：
    /// 1. 顶部状态区：工具版本、Codex Desktop 版本、CodexHome、当前状态判定、GitHub 最新版本状态；
    /// 2. 组件状态区：表格化清晰罗列各组件（市场规范、插件缓存、Native Host）的目标与本地状态；
    /// 3. 主操作区：检查、同步、回滚、清理、诊断导出等，支持阶段取消（CommitStarted 后禁用）；
    /// 4. 底部日志与进度区：实时展示执行阶段、文件进度、备份 ID、详细文本日志。
    /// </summary>
    public sealed class MainForm : Form
    {
        // 1. 顶部状态区控件
        private readonly Label _lblToolVersion = new Label();
        private readonly Label _lblCodexVersion = new Label();
        private readonly Label _lblCodexHome = new Label();
        private readonly Label _lblStatusVerdict = new Label();
        private readonly LinkLabel _lnkUpdateStatus = new LinkLabel();

        // 2. 组件状态区控件
        private readonly ListView _listComponents = new ListView();

        // 3. 主操作区控件
        private readonly Button _btnCheck = new Button();
        private readonly Button _btnSync = new Button();
        private readonly Button _btnRollback = new Button();
        private readonly Button _btnCleanup = new Button();
        private readonly Button _btnDiag = new Button();
        private readonly Button _btnOpenLogs = new Button();
        private readonly Button _btnOpenHome = new Button();
        private readonly Button _btnCancel = new Button();
        private readonly Button _btnCheckUpdates = new Button();
        private readonly Button _btnDownloadUpdate = new Button();

        // 5. 托盘与后台常驻监控控件 (M4)
        private NotifyIcon _notifyIcon;
        private ContextMenuStrip _trayMenu;
        private System.Windows.Forms.Timer _timerBgCheck;
        private bool _reallyExit = false;
        private bool _hasShownTrayBalloon = false;
        private bool _isFirstBgTick = true;
        private static readonly object _operationLock = new object();
        private static volatile bool _isOperationRunning = false;

        // 4. 底部日志与进度区控件
        private readonly Label _lblBottomStatus = new Label();
        private readonly ProgressBar _progressBar = new ProgressBar();
        private readonly TextBox _txtLog = new TextBox();

        private string _codexHome;
        private string _latestReleaseUrl = UpdateChecker.RepositoryUrl + "/releases";
        private bool _busy;
        private SyncOptions _activeSyncOptions;

        public MainForm()
        {
            _codexHome = CodexPaths.ResolveCodexHome(Settings.Load(null).CodexHome);

            Text = "Codex 插件同步 " + Cli.CliRunner.ToolVersion;
            MinimumSize = new Size(780, 580);
            Size = new Size(880, 680);
            StartPosition = FormStartPosition.CenterScreen;
            Font = new Font("Microsoft YaHei UI", 9f, FontStyle.Regular);

            TableLayoutPanel root = new TableLayoutPanel();
            root.Dock = DockStyle.Fill;
            root.ColumnCount = 1;
            root.RowCount = 5;
            root.Padding = new Padding(12);
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));             // 顶部状态区
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 150f));        // 组件状态表格
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));             // 主操作按钮区
            root.RowStyles.Add(new RowStyle(SizeType.Percent, 100f));        // 日志文本区
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));             // 底部进度与状态条
            Controls.Add(root);

            // ==================== 1. 顶部状态区 ====================
            GroupBox grpTopStatus = new GroupBox();
            grpTopStatus.Text = "核心环境与状态";
            grpTopStatus.Dock = DockStyle.Fill;
            grpTopStatus.AutoSize = true;
            grpTopStatus.Padding = new Padding(10, 8, 10, 10);
            grpTopStatus.Margin = new Padding(0, 0, 0, 8);

            TableLayoutPanel topGrid = new TableLayoutPanel();
            topGrid.Dock = DockStyle.Fill;
            topGrid.ColumnCount = 2;
            topGrid.RowCount = 3;
            topGrid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 55f));
            topGrid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 45f));
            topGrid.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            topGrid.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            topGrid.RowStyles.Add(new RowStyle(SizeType.AutoSize));

            _lblStatusVerdict.Text = "系统状态：正在检测…";
            _lblStatusVerdict.Font = new Font(Font.FontFamily, 10f, FontStyle.Bold);
            _lblStatusVerdict.AutoSize = true;
            _lblStatusVerdict.Margin = new Padding(0, 2, 0, 4);

            _lblToolVersion.Text = "当前工具版本：" + Cli.CliRunner.ToolVersion;
            _lblToolVersion.AutoSize = true;
            _lblToolVersion.ForeColor = SystemColors.ControlText;

            _lblCodexVersion.Text = "Codex Desktop：正在读取…";
            _lblCodexVersion.AutoSize = true;
            _lblCodexVersion.ForeColor = SystemColors.ControlText;

            _lblCodexHome.Text = "CodexHome：" + _codexHome;
            _lblCodexHome.AutoSize = true;
            _lblCodexHome.ForeColor = SystemColors.GrayText;

            _lnkUpdateStatus.Text = "正在检查 GitHub Releases 版本…";
            _lnkUpdateStatus.AutoSize = true;
            _lnkUpdateStatus.LinkColor = SystemColors.HotTrack;
            _lnkUpdateStatus.LinkClicked += delegate
            {
                try
                {
                    Process.Start(new ProcessStartInfo(!string.IsNullOrEmpty(_latestReleaseUrl) ? _latestReleaseUrl : UpdateChecker.RepositoryUrl + "/releases")
                    {
                        UseShellExecute = true
                    });
                }
                catch (Exception) { }
            };

            topGrid.Controls.Add(_lblStatusVerdict, 0, 0);
            topGrid.Controls.Add(_lnkUpdateStatus, 1, 0);
            topGrid.Controls.Add(_lblCodexVersion, 0, 1);
            topGrid.Controls.Add(_lblToolVersion, 1, 1);
            topGrid.Controls.Add(_lblCodexHome, 0, 2);
            topGrid.SetColumnSpan(_lblCodexHome, 2);

            grpTopStatus.Controls.Add(topGrid);
            root.Controls.Add(grpTopStatus, 0, 0);

            // ==================== 2. 组件状态区 ====================
            _listComponents.Dock = DockStyle.Fill;
            _listComponents.View = View.Details;
            _listComponents.FullRowSelect = true;
            _listComponents.GridLines = true;
            _listComponents.HideSelection = false;
            _listComponents.MultiSelect = false;
            _listComponents.HeaderStyle = ColumnHeaderStyle.Nonclickable;
            _listComponents.Columns.Add("组件模块", 150);
            _listComponents.Columns.Add("目标版本", 130);
            _listComponents.Columns.Add("本地状态", 130);
            _listComponents.Columns.Add("判定", 90);
            _listComponents.Columns.Add("详细说明", 300);
            _listComponents.Margin = new Padding(0, 0, 0, 8);
            root.Controls.Add(_listComponents, 0, 1);

            // ==================== 3. 主操作区 ====================
            FlowLayoutPanel actionPanel = new FlowLayoutPanel();
            actionPanel.Dock = DockStyle.Fill;
            actionPanel.AutoSize = true;
            actionPanel.WrapContents = true;
            actionPanel.Margin = new Padding(0, 0, 0, 8);

            ConfigureButton(_btnCheck, "检查状态", delegate { RunCheck(); });
            ConfigureButton(_btnSync, "立即同步", delegate { RunWithConfirm("同步会先自动创建备份，并将内置插件原子写入本地目录。\n继续执行？", RunSync); });
            ConfigureButton(_btnRollback, "回滚备份…", delegate { RunRollback(); });
            ConfigureButton(_btnCleanup, "清理旧备份", delegate { RunWithConfirm("清理会删除过期的 stage 暂存、过期备份与旧日志（活跃操作引用的内容会被保留）。\n继续执行？", RunCleanup); });
            ConfigureButton(_btnDiag, "导出诊断包", delegate { RunDiag(); });
            ConfigureButton(_btnOpenLogs, "打开日志", delegate { OpenFolder(CodexPaths.ToolLogDirectory); });
            ConfigureButton(_btnOpenHome, "打开 CodexHome", delegate { OpenFolder(_codexHome); });
            ConfigureButton(_btnCheckUpdates, "检查更新", delegate { RefreshUpdateStatus(true); });
            ConfigureButton(_btnDownloadUpdate, "下载新版本", delegate
            {
                try
                {
                    Process.Start(new ProcessStartInfo(!string.IsNullOrEmpty(_latestReleaseUrl) ? _latestReleaseUrl : UpdateChecker.RepositoryUrl + "/releases/latest")
                    {
                        UseShellExecute = true
                    });
                }
                catch (Exception ex)
                {
                    MessageBox.Show(this, "无法打开下载页面：" + ex.Message, "提示", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                }
            });
            _btnDownloadUpdate.Visible = false;
            _btnDownloadUpdate.ForeColor = Color.DarkGreen;
            _btnDownloadUpdate.Font = new Font(Font, FontStyle.Bold);

            _btnCancel.Text = "取消同步";
            _btnCancel.AutoSize = true;
            _btnCancel.Padding = new Padding(8, 2, 8, 2);
            _btnCancel.Visible = false;
            _btnCancel.ForeColor = Color.DarkRed;
            _btnCancel.Click += delegate
            {
                if (_activeSyncOptions != null)
                {
                    _activeSyncOptions.CancelRequested = true;
                    _btnCancel.Text = "正在取消…";
                    _lblBottomStatus.Text = "当前阶段：正在请求取消…";
                    _btnCancel.Enabled = false;
                }
            };

            actionPanel.Controls.Add(_btnCheck);
            actionPanel.Controls.Add(_btnSync);
            actionPanel.Controls.Add(_btnRollback);
            actionPanel.Controls.Add(_btnCleanup);
            actionPanel.Controls.Add(_btnDiag);
            actionPanel.Controls.Add(_btnOpenLogs);
            actionPanel.Controls.Add(_btnOpenHome);
            actionPanel.Controls.Add(_btnCheckUpdates);
            actionPanel.Controls.Add(_btnDownloadUpdate);
            actionPanel.Controls.Add(_btnCancel);

            root.Controls.Add(actionPanel, 0, 2);

            // ==================== 4. 底部日志与状态区 ====================
            _txtLog.Multiline = true;
            _txtLog.ReadOnly = true;
            _txtLog.ScrollBars = ScrollBars.Vertical;
            _txtLog.Dock = DockStyle.Fill;
            _txtLog.BackColor = Color.FromArgb(250, 250, 250);
            _txtLog.Font = new Font("Consolas", 9f);
            _txtLog.Margin = new Padding(0, 0, 0, 6);
            root.Controls.Add(_txtLog, 0, 3);

            TableLayoutPanel bottomBar = new TableLayoutPanel();
            bottomBar.Dock = DockStyle.Fill;
            bottomBar.ColumnCount = 2;
            bottomBar.RowCount = 1;
            bottomBar.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
            bottomBar.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 180f));

            _lblBottomStatus.Text = "当前阶段：就绪";
            _lblBottomStatus.AutoSize = true;
            _lblBottomStatus.Anchor = AnchorStyles.Left;
            _lblBottomStatus.ForeColor = SystemColors.ControlDarkDark;

            _progressBar.Style = ProgressBarStyle.Marquee;
            _progressBar.Visible = false;
            _progressBar.Dock = DockStyle.Fill;
            _progressBar.Height = 16;
            _progressBar.Anchor = AnchorStyles.Right;

            bottomBar.Controls.Add(_lblBottomStatus, 0, 0);
            bottomBar.Controls.Add(_progressBar, 1, 0);
            root.Controls.Add(bottomBar, 0, 4);

            InitializeTray();

            Shown += delegate
            {
                RefreshStatus();
                RefreshUpdateStatus(false);
            };
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
            if (_txtLog.TextLength == 0)
            {
                _txtLog.Text = line;
            }
            else
            {
                _txtLog.AppendText(Environment.NewLine + line);
            }
        }

        private void RunAsync(string headline, Action<TextWriter> work, bool refreshAfterwards = true)
        {
            if (_busy) { return; }
            _busy = true;
            SetButtonsEnabled(false);
            _lblBottomStatus.Text = "当前阶段：" + headline;
            _progressBar.Visible = true;

            Thread thread = new Thread(delegate()
            {
                StringWriter writer = new StringWriter();
                string error = null;
                bool lockTaken = false;
                try
                {
                    // 确保任何正在进行的后台静默检查完全退出后再开始操作，最长等待 5 秒
                    Monitor.TryEnter(_operationLock, 5000, ref lockTaken);
                    _isOperationRunning = true;
                    work(writer);
                }
                catch (Exception ex)
                {
                    error = ex.GetType().Name + ": " + ex.Message;
                }
                finally
                {
                    _isOperationRunning = false;
                    if (lockTaken)
                    {
                        Monitor.Exit(_operationLock);
                    }
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
                            _progressBar.Visible = false;
                            _btnCancel.Visible = false;
                            SetButtonsEnabled(true);
                            if (refreshAfterwards) { RefreshStatus(); }
                        });
                    }
                    catch (Exception) { }
                }
            });
            thread.IsBackground = true;
            thread.Start();
        }

        private void TrimLog()
        {
            const int maxChars = 200000;
            if (_txtLog.TextLength <= maxChars) { return; }
            _txtLog.Text = "…（已截断较早输出）" + Environment.NewLine +
                _txtLog.Text.Substring(_txtLog.TextLength - maxChars / 2);
            _txtLog.SelectionStart = _txtLog.TextLength;
            _txtLog.ScrollToCaret();
        }

        private void SetButtonsEnabled(bool enabled)
        {
            _btnCheck.Enabled = enabled;
            _btnSync.Enabled = enabled;
            _btnRollback.Enabled = enabled;
            _btnCleanup.Enabled = enabled;
            _btnDiag.Enabled = enabled;
            _btnOpenLogs.Enabled = enabled;
            _btnOpenHome.Enabled = enabled;
            _btnCheckUpdates.Enabled = enabled;
            _btnDownloadUpdate.Enabled = enabled;
            Cursor = enabled ? Cursors.Default : Cursors.WaitCursor;
        }

        private void RefreshUpdateStatus(bool fromUserClick = false)
        {
            _lnkUpdateStatus.Text = "正在检查 GitHub Releases 版本…";
            _lnkUpdateStatus.LinkColor = SystemColors.GrayText;
            if (fromUserClick)
            {
                AppendLog("正在检查 GitHub Releases 最新版本…");
                _lblBottomStatus.Text = "当前阶段：正在检查 GitHub Releases 版本…";
            }
            Thread thread = new Thread(delegate()
            {
                UpdateInfo update = UpdateChecker.Check(Cli.CliRunner.ToolVersion);
                if (IsHandleCreated && !IsDisposed)
                {
                    try
                    {
                        BeginInvoke((MethodInvoker)delegate
                        {
                            if (!string.IsNullOrEmpty(update.ReleaseUrl))
                            {
                                _latestReleaseUrl = update.ReleaseUrl;
                            }
                            if (!update.Succeeded)
                            {
                                _lnkUpdateStatus.Text = (update.Message ?? "暂无已发布的 Release 版本") + " · 点击访问 Releases";
                                _lnkUpdateStatus.LinkColor = SystemColors.HotTrack;
                                _lnkUpdateStatus.Font = new Font(Font, FontStyle.Regular);
                                _btnDownloadUpdate.Visible = false;
                                if (fromUserClick)
                                {
                                    AppendLog("【版本检测】暂时无法连接至 GitHub Releases（" + (update.Message ?? "网络或服务异常") + "），已保持本地离线运行。");
                                    _lblBottomStatus.Text = "当前阶段：就绪（离线模式）";
                                }
                            }
                            else if (update.IsNewer)
                            {
                                _lnkUpdateStatus.Text = "🎉 发现新版本 " + update.LatestVersion + "（当前 " + update.CurrentVersion + "）· 点击前往 GitHub Releases 查看更新";
                                _lnkUpdateStatus.LinkColor = Color.DarkOrange;
                                _lnkUpdateStatus.Font = new Font(Font, FontStyle.Bold);
                                _btnDownloadUpdate.Text = "下载新版本 (" + update.LatestVersion + ")";
                                _btnDownloadUpdate.Visible = true;
                                if (fromUserClick)
                                {
                                    AppendLog("【版本检测】🎉 发现新版本 v" + update.LatestVersion + "（当前 v" + update.CurrentVersion + "）。发布地址：" + update.ReleaseUrl);
                                    _lblBottomStatus.Text = "当前阶段：发现新版本 " + update.LatestVersion;
                                }
                            }
                            else
                            {
                                _lnkUpdateStatus.Text = "当前版本 " + update.CurrentVersion + " 已是最新 · 点击访问 Releases";
                                _lnkUpdateStatus.LinkColor = Color.DarkGreen;
                                _lnkUpdateStatus.Font = new Font(Font, FontStyle.Regular);
                                _btnDownloadUpdate.Visible = false;
                                if (fromUserClick)
                                {
                                    AppendLog("【版本检测】当前版本 v" + update.CurrentVersion + " 已是最新发布版本。");
                                    _lblBottomStatus.Text = "当前阶段：版本检查完成（当前已是最新）";
                                }
                            }
                        });
                    }
                    catch (Exception) { }
                }
            });
            thread.IsBackground = true;
            thread.Start();
        }

        private void RunWithConfirm(string question, Action run)
        {
            DialogResult answer = MessageBox.Show(this, question, "操作确认", MessageBoxButtons.OKCancel, MessageBoxIcon.Information);
            if (answer == DialogResult.OK)
            {
                run();
            }
        }

        private void RefreshStatus()
        {
            RunAsync("读取状态", delegate(TextWriter writer)
            {
                Settings settings = Settings.Load(null);
                CheckOptions options = new CheckOptions();
                options.CodexHome = CodexPaths.ResolveCodexHome(settings.CodexHome);
                options.MinimumFreeGB = settings.MinimumFreeGB;
                options.ExtraLockingProcesses = settings.ExtraLockingProcesses;

                CheckEngine engine = new CheckEngine(options, null);
                CheckOutcome outcome = engine.Execute();
                Snapshot snapshot = outcome.Snapshot;

                if (IsHandleCreated && !IsDisposed)
                {
                    try
                    {
                        BeginInvoke((MethodInvoker)delegate
                        {
                            UpdateUIFromSnapshot(snapshot, outcome);
                        });
                    }
                    catch (Exception) { }
                }
            }, false);
        }

        private void UpdateUIFromSnapshot(Snapshot snapshot, CheckOutcome outcome)
        {
            // 1. 顶部状态
            _lblCodexHome.Text = "CodexHome：" + snapshot.CodexHome;
            if (snapshot.Desktop != null && snapshot.Desktop.Found)
            {
                _lblCodexVersion.Text = "Codex Desktop：" + snapshot.Desktop.Version;
                _lblCodexVersion.ForeColor = Color.DarkGreen;
            }
            else
            {
                _lblCodexVersion.Text = "Codex Desktop：未检测到已安装的 Codex Desktop";
                _lblCodexVersion.ForeColor = Color.Crimson;
            }

            _lblStatusVerdict.Text = "系统状态：" + HeadlineText(snapshot.Verdict);
            if (snapshot.Verdict == Verdicts.Ok)
            {
                _lblStatusVerdict.ForeColor = Color.DarkGreen;
            }
            else if (snapshot.Verdict == Verdicts.UpdateNeeded)
            {
                _lblStatusVerdict.ForeColor = Color.DarkOrange;
            }
            else
            {
                _lblStatusVerdict.ForeColor = Color.Crimson;
            }

            // 2. 组件列表
            _listComponents.BeginUpdate();
            _listComponents.Items.Clear();

            // 市场清单
            if (snapshot.Marketplace != null)
            {
                ListViewItem item = new ListViewItem("Marketplace (市场包)");
                item.SubItems.Add("内置包规格");
                item.SubItems.Add(snapshot.Marketplace.Exists ? "已部署" : "未部署");
                if (snapshot.Marketplace.NeedsUpdate == true)
                {
                    item.SubItems.Add("需要同步");
                    item.SubItems.Add("openai-bundled 市场清单落后或缺失");
                    item.ForeColor = Color.DarkOrange;
                }
                else if (snapshot.Marketplace.Exists)
                {
                    item.SubItems.Add("最新");
                    item.SubItems.Add("openai-bundled 市场清单一致");
                    item.ForeColor = Color.DarkGreen;
                }
                else
                {
                    item.SubItems.Add("缺失");
                    item.SubItems.Add("尚未部署市场清单");
                    item.ForeColor = Color.Crimson;
                }
                _listComponents.Items.Add(item);
            }

            // 受管缓存组件
            if (snapshot.Components != null)
            {
                for (int i = 0; i < snapshot.Components.Count; i++)
                {
                    ComponentInfo c = snapshot.Components[i];
                    ListViewItem item = new ListViewItem(c.Name + " (缓存)");
                    item.SubItems.Add(!string.IsNullOrEmpty(c.Expected) ? c.Expected : "-");
                    item.SubItems.Add(!string.IsNullOrEmpty(c.Actual) ? c.Actual : "缺失");
                    if (c.State == ComponentStates.Ok)
                    {
                        item.SubItems.Add("最新");
                        item.SubItems.Add("版本与内置包完全匹配");
                        item.ForeColor = Color.DarkGreen;
                    }
                    else if (c.State == ComponentStates.Missing)
                    {
                        item.SubItems.Add("缺失");
                        item.SubItems.Add("本地缓存目录不存在");
                        item.ForeColor = Color.Crimson;
                    }
                    else
                    {
                        item.SubItems.Add("落后");
                        item.SubItems.Add("本地版本为 " + c.Actual + "，需同步至 " + c.Expected);
                        item.ForeColor = Color.DarkOrange;
                    }
                    _listComponents.Items.Add(item);
                }
            }

            // Chrome Native Host
            if (snapshot.NativeHost != null && snapshot.NativeHost.Chrome != null)
            {
                string state = snapshot.NativeHost.Chrome.State;
                bool ok = string.Equals(state, "ok", StringComparison.OrdinalIgnoreCase);
                ListViewItem item = new ListViewItem("Chrome Native Host");
                item.SubItems.Add("latest junction");
                item.SubItems.Add(ok ? "已连接" : state);
                item.SubItems.Add(ok ? "正常" : "待处理");
                item.SubItems.Add(snapshot.NativeHost.Chrome.Detail ?? (ok ? "Native host 符号链接有效" : "需要重新建立链接"));
                item.ForeColor = ok ? Color.DarkGreen : Color.Crimson;
                _listComponents.Items.Add(item);
            }

            _listComponents.EndUpdate();

            // 4. 底部状态
            StringBuilder sb = new StringBuilder();
            sb.Append("当前状态：").Append(VerdictText(snapshot.Verdict));
            if (snapshot.Backups != null)
            {
                sb.Append(" | 可用备份数：").Append(snapshot.Backups.Count);
                if (snapshot.Backups.Newest != null)
                {
                    sb.Append("（最新：").Append(snapshot.Backups.Newest.BackupId).Append("）");
                }
            }
            if (snapshot.LockingProcesses.Count > 0)
            {
                sb.Append(" | ⚠ 发现占用进程：");
                for (int i = 0; i < snapshot.LockingProcesses.Count; i++)
                {
                    if (i > 0) sb.Append("、");
                    sb.Append(snapshot.LockingProcesses[i].Name);
                }
                sb.Append("（同步前请先关闭）");
            }
            _lblBottomStatus.Text = sb.ToString();
        }

        private static string HeadlineText(string verdict)
        {
            if (verdict == Verdicts.Ok) { return "正常（所有组件已是最新）"; }
            if (verdict == Verdicts.UpdateNeeded) { return "需要同步（发现内置组件更新）"; }
            if (verdict == Verdicts.Blocked) { return "被阻塞（检测到进程占用或未完成的事务）"; }
            return "检查失败（无法获取完整状态）";
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
            RunAsync("正在检查组件状态…", delegate(TextWriter writer)
            {
                Settings settings = Settings.Load(null);
                CheckOptions options = new CheckOptions();
                options.CodexHome = CodexPaths.ResolveCodexHome(settings.CodexHome);
                options.MinimumFreeGB = settings.MinimumFreeGB;
                options.ExtraLockingProcesses = settings.ExtraLockingProcesses;
                CheckEngine engine = new CheckEngine(options, null);
                CheckOutcome outcome = engine.Execute();
                writer.WriteLine("检查结果判定：" + VerdictText(outcome.Snapshot.Verdict) + "（退出码 " + outcome.ExitCode + "）");
                if (outcome.ErrorMessage != null) { writer.WriteLine("错误说明：" + outcome.ErrorMessage); }
                for (int i = 0; i < outcome.Warnings.Count; i++) { writer.WriteLine("警告项：" + outcome.Warnings[i]); }
            });
        }

        private void RunSync()
        {
            _activeSyncOptions = new SyncOptions();
            _activeSyncOptions.CodexHome = _codexHome;

            _btnCancel.Text = "取消同步";
            _btnCancel.Visible = true;
            _btnCancel.Enabled = true;

            _activeSyncOptions.OnPhaseChanged = delegate(string phase, string detail)
            {
                if (IsHandleCreated && !IsDisposed)
                {
                    try
                    {
                        BeginInvoke((MethodInvoker)delegate
                        {
                            _lblBottomStatus.Text = "当前阶段：" + detail;
                            if (phase == JournalPhases.CommitStarted)
                            {
                                _btnCancel.Enabled = false; // 进入原子提交，禁止取消
                            }
                        });
                    }
                    catch (Exception) { }
                }
            };

            RunAsync("正在同步组件（先自动备份，再原子切换）…", delegate(TextWriter writer)
            {
                _activeSyncOptions.Out = writer;
                _activeSyncOptions.Err = writer;
                SyncRunResult result = SyncEngine.Run(_activeSyncOptions);
                writer.WriteLine("同步结果：" + (result.Status ?? "(未知)") + "｜退出码 " + result.ExitCode);
                if (!string.IsNullOrEmpty(result.Message)) { writer.WriteLine(result.Message); }
                if (result.ExitCode == ExitCodesLocator.Ok && result.Status != "no-op")
                {
                    writer.WriteLine("提示：同步已成功完成，建议重启 Codex Desktop 应用程序以加载最新插件。");
                }
            });
        }

        private void RunRollback()
        {
            RunAsync("读取历史备份列表…", delegate(TextWriter writer)
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
                    writer.WriteLine("当前没有可用的历史备份。");
                    return;
                }

                SelectedBackup selection = null;
                if (IsHandleCreated && !IsDisposed)
                {
                    Invoke((MethodInvoker)delegate
                    {
                        selection = PromptBackup(candidates);
                    });
                }

                if (selection == null)
                {
                    writer.WriteLine("已取消回滚。");
                    return;
                }
                writer.WriteLine("正在回滚至备份：" + (selection.BackupId ?? "最新 status=complete"));
                RollbackOptions options = new RollbackOptions();
                options.CodexHome = _codexHome;
                options.BackupId = selection.BackupId;
                options.Out = writer;
                options.Err = writer;
                SyncRunResult result = RollbackEngine.Run(options);
                writer.WriteLine("回滚结果：" + (result.Status ?? "(未知)") + "｜退出码 " + result.ExitCode);
                if (result.ExitCode == ExitCodesLocator.Ok)
                {
                    writer.WriteLine("提示：回滚已成功完成，请重启 Codex Desktop 应用程序。");
                }
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
                dialog.Text = "选择回滚快照备份";
                dialog.StartPosition = FormStartPosition.CenterParent;
                dialog.Width = 480;
                dialog.Height = 280;
                dialog.Font = Font;
                dialog.FormBorderStyle = FormBorderStyle.FixedDialog;
                dialog.MaximizeBox = false;
                dialog.MinimizeBox = false;

                ListBox list = new ListBox();
                list.Dock = DockStyle.Fill;
                list.Items.Add("（默认：最新 complete 备份）");
                for (int i = 0; i < candidates.Count; i++) { list.Items.Add(candidates[i]); }
                list.SelectedIndex = 0;

                FlowLayoutPanel panel = new FlowLayoutPanel();
                panel.Dock = DockStyle.Bottom;
                panel.FlowDirection = FlowDirection.RightToLeft;
                panel.Height = 44;
                panel.Padding = new Padding(0, 6, 8, 0);

                Button ok = new Button();
                ok.Text = "确认回滚";
                ok.DialogResult = DialogResult.OK;
                ok.Width = 90;

                Button cancel = new Button();
                cancel.Text = "取消";
                cancel.DialogResult = DialogResult.Cancel;
                cancel.Width = 80;

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
            RunAsync("正在清理旧备份与暂存目录…", delegate(TextWriter writer)
            {
                CleanupOptions options = new CleanupOptions();
                options.CodexHome = _codexHome;
                options.Out = writer;
                options.Err = writer;
                SyncRunResult result = CleanupEngine.Run(options);
                writer.WriteLine("清理结果：" + (result.Status ?? "(未知)") + "｜退出码 " + result.ExitCode);
            });
        }

        private void RunDiag()
        {
            SaveFileDialog dialog = new SaveFileDialog();
            dialog.Title = "保存诊断信息包";
            dialog.Filter = "Zip 压缩包 (*.zip)|*.zip";
            dialog.FileName = "CodexPluginSync-diag-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + ".zip";
            if (dialog.ShowDialog(this) != DialogResult.OK)
            {
                return;
            }
            string path = dialog.FileName;
            RunAsync("正在导出诊断数据包…", delegate(TextWriter writer)
            {
                DiagOptions options = new DiagOptions();
                options.CodexHome = _codexHome;
                options.OutPath = path;
                options.Out = writer;
                options.Err = writer;
                SyncRunResult result = DiagExporter.Run(options);
                writer.WriteLine("导出结果：" + (result.Status ?? "(未知)") + "｜退出码 " + result.ExitCode);
                writer.WriteLine("安全提示：诊断包已完成脱敏处理，提交 Issue 时附带此文件即可。");
            });
        }

        private void OpenFolder(string path)
        {
            try
            {
                if (string.IsNullOrEmpty(path) || !Directory.Exists(path))
                {
                    MessageBox.Show(this, "指定的目录尚不存在：" + path, "提示", MessageBoxButtons.OK, MessageBoxIcon.Information);
                    return;
                }
                Process.Start(new ProcessStartInfo("explorer.exe", "\"" + path + "\"") { UseShellExecute = true });
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, ex.Message, "无法打开目录", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }

        // ==================== 5. 托盘常驻与后台静默检查 (M4) ====================
        private void InitializeTray()
        {
            _notifyIcon = new NotifyIcon();
            try
            {
                _notifyIcon.Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath) ?? SystemIcons.Application;
            }
            catch (Exception)
            {
                _notifyIcon.Icon = SystemIcons.Application;
            }
            this.Icon = _notifyIcon.Icon;
            _notifyIcon.Text = "Codex 插件同步 " + Cli.CliRunner.ToolVersion;

            _trayMenu = new ContextMenuStrip();
            _trayMenu.Font = new Font("Microsoft YaHei UI", 9f);
            _trayMenu.Items.Add("打开主窗口", null, delegate { RestoreFromTray(); });
            _trayMenu.Items.Add("立即检查", null, delegate { RestoreFromTray(); RunCheck(); });
            _trayMenu.Items.Add("立即同步", null, delegate { RestoreFromTray(); RunWithConfirm("同步会先自动创建备份，并将内置插件原子写入本地目录。\n继续执行？", RunSync); });
            _trayMenu.Items.Add(new ToolStripSeparator());
            _trayMenu.Items.Add("访问 GitHub 主页", null, delegate
            {
                try
                {
                    Process.Start(new ProcessStartInfo(UpdateChecker.RepositoryUrl) { UseShellExecute = true });
                }
                catch (Exception) { }
            });
            _trayMenu.Items.Add("退出", null, delegate
            {
                _reallyExit = true;
                Close();
            });

            _notifyIcon.ContextMenuStrip = _trayMenu;
            _notifyIcon.Visible = true;

            _notifyIcon.DoubleClick += delegate { RestoreFromTray(); };
            _notifyIcon.BalloonTipClicked += delegate
            {
                RestoreFromTray();
                RefreshStatus();
            };

            Resize += delegate
            {
                if (WindowState == FormWindowState.Minimized)
                {
                    HideToTray();
                }
            };

            StartBackgroundTimer();
        }

        private void HideToTray()
        {
            Hide();
            ShowInTaskbar = false;
            if (!_hasShownTrayBalloon)
            {
                _hasShownTrayBalloon = true;
                try
                {
                    _notifyIcon.ShowBalloonTip(3000, "Codex 插件同步",
                        "程序已最小化到系统托盘，将在后台定期检查插件更新。\n右键托盘图标可退出程序。", ToolTipIcon.Info);
                }
                catch (Exception) { }
            }
        }

        private void RestoreFromTray()
        {
            Show();
            WindowState = FormWindowState.Normal;
            ShowInTaskbar = true;
            BringToFront();
            Activate();
        }

        private void StartBackgroundTimer()
        {
            Settings s = Settings.Load(null);
            if (s.AutoCheckHours <= 0) { return; }

            _timerBgCheck = new System.Windows.Forms.Timer();
            _timerBgCheck.Interval = 2 * 60 * 1000; // 启动后延迟 2 分钟首检 (§12 M4)
            _timerBgCheck.Tick += delegate
            {
                if (_isFirstBgTick)
                {
                    _isFirstBgTick = false;
                    Settings currentSettings = Settings.Load(null);
                    if (currentSettings.AutoCheckHours <= 0)
                    {
                        if (_timerBgCheck != null) { _timerBgCheck.Stop(); }
                        return;
                    }
                    int safeHours = Math.Min(72, Math.Max(1, currentSettings.AutoCheckHours));
                    _timerBgCheck.Interval = safeHours * 3600 * 1000;
                }

                RunSilentBackgroundCheck();
            };
            _timerBgCheck.Start();
        }

        private void RunSilentBackgroundCheck()
        {
            if (_busy || _isOperationRunning) { return; }

            Thread thread = new Thread(delegate()
            {
                bool lockTaken = false;
                try
                {
                    // 尝试非阻塞获取操作锁，如果用户正在执行操作或界面忙碌，立刻放弃，绝不争抢
                    Monitor.TryEnter(_operationLock, 0, ref lockTaken);
                    if (!lockTaken || _busy) { return; }
                    _isOperationRunning = true;

                    Settings settings = Settings.Load(null);
                    CheckOptions options = new CheckOptions();
                    options.CodexHome = CodexPaths.ResolveCodexHome(settings.CodexHome);
                    options.MinimumFreeGB = settings.MinimumFreeGB;
                    options.ExtraLockingProcesses = settings.ExtraLockingProcesses;

                    CheckEngine engine = new CheckEngine(options, null);
                    CheckOutcome outcome = engine.Execute();
                    Snapshot snapshot = outcome.Snapshot;

                    // 检查完成后，若在执行期间用户已启动前台操作，则丢弃本次静默检查结果
                    if (_busy) { return; }

                    if (IsHandleCreated && !IsDisposed)
                    {
                        BeginInvoke((MethodInvoker)delegate
                        {
                            if (_busy) { return; }
                            if (Visible && WindowState != FormWindowState.Minimized)
                            {
                                UpdateUIFromSnapshot(snapshot, outcome);
                            }
                            else
                            {
                                if (_notifyIcon != null && snapshot != null && snapshot.Verdict == Verdicts.UpdateNeeded)
                                {
                                    try
                                    {
                                        _notifyIcon.ShowBalloonTip(6000, "Codex 插件更新提示",
                                            "检测到 Codex Desktop 内置插件有新版本可用，点击打开主界面进行同步。",
                                            ToolTipIcon.Info);
                                    }
                                    catch (Exception) { }
                                }
                            }
                        });
                    }
                }
                catch (Exception) { }
                finally
                {
                    _isOperationRunning = false;
                    if (lockTaken)
                    {
                        Monitor.Exit(_operationLock);
                    }
                }
            });
            thread.IsBackground = true;
            thread.Start();
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            if (e.CloseReason == CloseReason.UserClosing && !_reallyExit)
            {
                e.Cancel = true;
                HideToTray();
                return;
            }

            if (_timerBgCheck != null)
            {
                _timerBgCheck.Stop();
                _timerBgCheck.Dispose();
                _timerBgCheck = null;
            }

            if (_notifyIcon != null)
            {
                _notifyIcon.Visible = false;
                _notifyIcon.Dispose();
                _notifyIcon = null;
            }

            if (_trayMenu != null)
            {
                _trayMenu.Dispose();
                _trayMenu = null;
            }

            base.OnFormClosing(e);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                if (_timerBgCheck != null)
                {
                    _timerBgCheck.Stop();
                    _timerBgCheck.Dispose();
                    _timerBgCheck = null;
                }
                if (_notifyIcon != null)
                {
                    _notifyIcon.Visible = false;
                    _notifyIcon.Dispose();
                    _notifyIcon = null;
                }
                if (_trayMenu != null)
                {
                    _trayMenu.Dispose();
                    _trayMenu = null;
                }
            }
            base.Dispose(disposing);
        }
    }
}
