using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;

namespace CodexPluginSync.Core
{
    /// <summary>
    /// §7.5 脱敏：用户名 / 计算机名 / 用户目录链 / SID / 临时目录一律替换成占位符。
    /// 保留盘符（否则连"装在哪块盘"都无法复现）。环境变量只导出白名单。
    /// </summary>
    public sealed class Redactor
    {
        private static readonly Regex SidPattern = new Regex(@"S-1-5-\d+(-\d+)+", RegexOptions.CultureInvariant);

        private readonly List<string> _needles = new List<string>();
        private readonly List<string> _replacement = new List<string>();

        public readonly bool Enabled;

        public Redactor(bool enabled, string codexHome)
        {
            Enabled = enabled;
            if (!enabled) { return; }

            string profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            string userName = ReadEnvironment("USERNAME");
            string hostName = ReadEnvironment("COMPUTERNAME");
            string temp = Path.GetTempPath();

            Add(profile, DrivePrefix(profile) + "<profile>");
            Add(codexHome, DrivePrefix(codexHome) + "<codexhome>");
            Add(Environment.ExpandEnvironmentVariables("%TEMP%"), DrivePrefix(temp) + "<temp>");
            Add(temp, DrivePrefix(temp) + "<temp>");
            Add(userName, "<user>");
            Add(hostName, "<host>");
        }

        public string Redact(string text)
        {
            if (!Enabled || string.IsNullOrEmpty(text)) { return text; }
            string result = text;
            for (int i = 0; i < _needles.Count; i++)
            {
                if (_needles[i] == null || _needles[i].Length == 0) { continue; }
                result = ReplaceOrdinalIgnoreCase(result, _needles[i], _replacement[i]);
            }
            result = SidPattern.Replace(result, "<sid>");
            return result;
        }

        private void Add(string needle, string replacement)
        {
            if (string.IsNullOrEmpty(needle) || !Enabled) { return; }
            _needles.Add(needle);
            _replacement.Add(replacement);
        }

        private static string ReplaceOrdinalIgnoreCase(string text, string needle, string replacement)
        {
            int index = text.IndexOf(needle, StringComparison.OrdinalIgnoreCase);
            if (index < 0) { return text; }
            StringBuilder sb = new StringBuilder(text.Length);
            int cursor = 0;
            while (index >= 0)
            {
                sb.Append(text, cursor, index - cursor);
                sb.Append(replacement);
                cursor = index + needle.Length;
                index = text.IndexOf(needle, cursor, StringComparison.OrdinalIgnoreCase);
            }
            sb.Append(text, cursor, text.Length - cursor);
            return sb.ToString();
        }

        private static string DrivePrefix(string path)
        {
            if (string.IsNullOrEmpty(path) || path.Length < 2 || path[1] != ':') { return string.Empty; }
            return path.Substring(0, 2);
        }

        private static string ReadEnvironment(string name)
        {
            try
            {
                return Environment.GetEnvironmentVariable(name);
            }
            catch (Exception)
            {
                return null;
            }
        }
    }

    public sealed class DiagOptions
    {
        public string CodexHome;
        public string OutPath;
        public bool NoRedact;
        /// <summary>selftest 专用：跳过进程/磁盘等真机探测依赖。</summary>
        public bool SkipRuntimeProbe;
        /// <summary>selftest 专用：替换工具日志目录。</summary>
        public string ToolLogDirectoryOverride;
        public TextWriter Out;
        public TextWriter Err;
    }

    /// <summary>
    /// §7.5 诊断导出：一个 zip，里面只放状态与日志元数据。
    /// 硬保证：不含 token / API key / cookie / 插件文件内容 / AppX 文件 / 对话数据。
    /// 默认脱敏；--no-redact 必须在 manifest.json 里如实标记 redacted=false。
    /// </summary>
    public static class DiagExporter
    {
        private static readonly string[] EnvironmentWhitelist = new string[]
        {
            "OS", "PROCESSOR_ARCHITECTURE", "PROCESSOR_IDENTIFIER", "DOTNET_ROOT", "DOTNET_CLI_TELEMETRY_OPTOUT"
        };

        public static SyncRunResult Run(DiagOptions options)
        {
            SyncRunResult result = new SyncRunResult();
            TextWriter output = options.Out ?? Console.Out;
            TextWriter error = options.Err ?? Console.Error;

            try
            {
                Settings settings = Settings.Load(null);
                string codexHome = CodexPaths.ResolveCodexHome(options.CodexHome ?? settings.CodexHome);
                CodexPaths paths = new CodexPaths(codexHome);
                string toolLogDirectory = options.ToolLogDirectoryOverride ?? CodexPaths.ToolLogDirectory;
                Redactor redactor = new Redactor(!options.NoRedact, codexHome);

                string stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
                string outPath = options.OutPath;
                if (string.IsNullOrEmpty(outPath))
                {
                    outPath = Path.Combine(Directory.GetCurrentDirectory(), "CodexPluginSync-diag-" + stamp + ".zip");
                }
                string parent = Path.GetDirectoryName(Path.GetFullPath(outPath));
                if (!string.IsNullOrEmpty(parent) && !Directory.Exists(parent))
                {
                    return Fail(result, error, "输出目录不存在：" + parent);
                }
                if (File.Exists(outPath))
                {
                    return Fail(result, error, "输出文件已存在，拒绝覆盖：" + outPath);
                }

                List<string> files = new List<string>();
                int logCount = 0;

                using (FileStream zipStream = new FileStream(outPath, FileMode.CreateNew, FileAccess.Write))
                using (ZipArchive archive = new ZipArchive(zipStream, ZipArchiveMode.Create))
                {
                    string state = BuildState(codexHome, options, settings);
                    AddEntry(archive, "state.json", redactor.Redact(state));
                    files.Add("state.json\tStateSnapshot（只读检查结果）");

                    JournalDto journal = JournalStore.Read(paths);
                    if (journal != null)
                    {
                        string journalJson = AtomicText<JournalDto>(journal);
                        AddEntry(archive, "journal.json", redactor.Redact(journalJson));
                        files.Add("journal.json\t当前未收尾的 journal");
                    }

                    AddEntry(archive, "backups.json", redactor.Redact(BuildBackupsJson(paths)));
                    files.Add("backups.json\t备份清单（只含元数据，不含文件）");

                    AddEntry(archive, "settings.json", redactor.Redact(settings.ToJson()));
                    files.Add("settings.json\t当前配置");

                    AddEntry(archive, "native-host.txt", redactor.Redact(BuildNativeHostText(paths)));
                    files.Add("native-host.txt\tChrome native-messaging 注册表键、值与 manifest 内容");

                    AddEntry(archive, "environment.txt", redactor.Redact(BuildEnvironmentText()));
                    files.Add("environment.txt\tOS/.NET/管理员/区域与环境变量白名单");

                    logCount = AddLogs(archive, paths.LogDirectory, toolLogDirectory, redactor, files);

                    StringBuilder manifest = new StringBuilder();
                    manifest.Append("{");
                    manifest.Append("\"schemaVersion\":1,");
                    manifest.Append("\"tool\":\"" + CodexPaths.ToolName + "\",");
                    manifest.Append("\"exportedUtc\":\"" + DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture) + "\",");
                    manifest.Append("\"redacted\":" + (options.NoRedact ? "false" : "true") + ",");
                    manifest.Append("\"codexHome\":\"" + JsonEscape(redactor.Redact(codexHome)) + "\",");
                    manifest.Append("\"logCount\":" + logCount.ToString(CultureInfo.InvariantCulture) + ",");
                    manifest.Append("\"files\":[");
                    for (int i = 0; i < files.Count; i++)
                    {
                        if (i > 0) { manifest.Append(","); }
                        string[] parts = files[i].Split('\t');
                        manifest.Append("{\"name\":\"" + JsonEscape(parts[0]) + "\",\"purpose\":\"" +
                            JsonEscape(parts.Length > 1 ? parts[1] : string.Empty) + "\"}");
                    }
                    manifest.Append("]}");
                    AddEntry(archive, "manifest.json", manifest.ToString());
                    AddEntry(archive, "README.txt", ReadmeText(options.NoRedact));
                }

                result.Status = "exported";
                result.Message = "诊断包已生成：" + outPath;
                result.ExitCode = ExitCodesLocator.Ok;
                output.WriteLine(result.Message);
                output.WriteLine("脱敏：" + (options.NoRedact ? "否（--no-redact，已在 manifest.json 标记）" : "是"));
                output.WriteLine("内容：state.json / backups.json / settings.json / native-host.txt / environment.txt / 最近 " +
                    logCount.ToString(CultureInfo.InvariantCulture) + " 个日志 / manifest.json / README.txt");
                output.WriteLine("硬保证：不含 token、API key、cookie、插件文件内容、AppX 文件或对话数据。");
                return result;
            }
            catch (Exception ex)
            {
                result.Status = SyncTransaction.StatusAborted;
                result.ExitCode = ExitCodesLocator.Error;
                result.Message = ex.Message;
                error.WriteLine("导出失败：" + ex.Message);
                return result;
            }
        }

        private static SyncRunResult Fail(SyncRunResult result, TextWriter error, string message)
        {
            result.Status = SyncTransaction.StatusAborted;
            result.ExitCode = ExitCodesLocator.Error;
            result.Message = message;
            error.WriteLine(message);
            return result;
        }

        private static string BuildState(string codexHome, DiagOptions options, Settings settings)
        {
            if (options.SkipRuntimeProbe)
            {
                JsonWriter stub = new JsonWriter();
                stub.BeginObject();
                stub.Prop("schemaVersion", 1);
                stub.Prop("note", "selftest: runtime probe skipped");
                stub.Prop("codexHome", codexHome);
                stub.EndObject();
                return stub.ToString();
            }

            CheckOptions checkOptions = new CheckOptions();
            checkOptions.CodexHome = codexHome;
            checkOptions.MinimumFreeGB = settings.MinimumFreeGB;
            checkOptions.ExtraLockingProcesses = settings.ExtraLockingProcesses;
            CheckEngine engine = new CheckEngine(checkOptions, null);
            CheckOutcome outcome = engine.Execute();
            return SnapshotWriter.Write(outcome.Snapshot);
        }

        private static string BuildBackupsJson(CodexPaths paths)
        {
            JsonWriter w = new JsonWriter();
            w.BeginObject();
            w.Name("backups").BeginArray();

            if (Directory.Exists(paths.BackupsRoot))
            {
                string[] directories = Directory.GetDirectories(paths.BackupsRoot);
                for (int i = 0; i < directories.Length; i++)
                {
                    string directory = directories[i];
                    string manifestPath = Path.Combine(directory, BackupManifestStore.FileName);
                    w.BeginObject();
                    w.Prop("backupId", Path.GetFileName(directory));
                    if (File.Exists(manifestPath))
                    {
                        try
                        {
                            BackupManifestDto dto = JsonRead.FromFile<BackupManifestDto>(manifestPath);
                            w.Prop("kind", dto == null ? null : dto.Kind);
                            w.Prop("status", dto == null ? null : dto.Status);
                            w.Prop("createdUtc", dto == null ? null : dto.CreatedUtc);
                            w.Prop("componentCount", dto == null || dto.Components == null ? 0 : dto.Components.Count);
                        }
                        catch (Exception)
                        {
                            w.Prop("status", "unreadable");
                        }
                    }
                    else
                    {
                        w.Prop("status", "legacy");
                    }
                    w.EndObject();
                }
            }
            w.EndArray();
            w.EndObject();
            return w.ToString();
        }

        private static string BuildNativeHostText(CodexPaths paths)
        {
            StringBuilder sb = new StringBuilder();
            string registryPath = NativeHostWriter.ChromeRegistryPath;
            sb.AppendLine("registry: HKCU\\" + registryPath);

            string current = null;
            try
            {
                current = NativeHostWriter.ReadRegistryDefault();
            }
            catch (Exception ex)
            {
                sb.AppendLine("default(value): <unreadable: " + ex.GetType().Name + ">");
            }
            if (current == null)
            {
                sb.AppendLine("default(value): <absent>");
                return sb.ToString();
            }

            sb.AppendLine("default(value): " + current);
            string cacheLeaf = paths.CacheParent + Path.DirectorySeparatorChar + "chrome" +
                Path.DirectorySeparatorChar + "<version>";
            sb.AppendLine("expected(exe): " + cacheLeaf + @"\extension-host\windows\x64\extension-host.exe");
            if (!File.Exists(current))
            {
                sb.AppendLine("manifest: <missing>");
                return sb.ToString();
            }

            sb.AppendLine("manifest: " + current);
            sb.AppendLine("--- manifest content ---");
            try
            {
                sb.AppendLine(File.ReadAllText(current));
            }
            catch (Exception ex)
            {
                sb.AppendLine("<unreadable: " + ex.GetType().Name + ">");
            }
            return sb.ToString();
        }

        private static string BuildEnvironmentText()
        {
            StringBuilder sb = new StringBuilder();
            sb.AppendLine("osVersion: " + Environment.OSVersion);
            sb.AppendLine("64bitOs: " + Environment.Is64BitOperatingSystem.ToString(CultureInfo.InvariantCulture));
            sb.AppendLine("processorCount: " + Environment.ProcessorCount.ToString(CultureInfo.InvariantCulture));
            sb.AppendLine("clrVersion: " + Environment.Version);
            sb.AppendLine("culture: " + CultureInfo.CurrentCulture.Name);
            sb.AppendLine("uiCulture: " + CultureInfo.CurrentUICulture.Name);
            sb.AppendLine("isAdmin: " + IsAdministrator().ToString(CultureInfo.InvariantCulture));
            sb.AppendLine();
            sb.AppendLine("--- environment (whitelist) ---");
            for (int i = 0; i < EnvironmentWhitelist.Length; i++)
            {
                string value = Environment.GetEnvironmentVariable(EnvironmentWhitelist[i]);
                if (value == null) { continue; }
                sb.AppendLine(EnvironmentWhitelist[i] + "=" + value);
            }
            return sb.ToString();
        }

        private static bool IsAdministrator()
        {
            try
            {
                System.Security.Principal.WindowsIdentity identity =
                    System.Security.Principal.WindowsIdentity.GetCurrent();
                System.Security.Principal.WindowsPrincipal principal =
                    new System.Security.Principal.WindowsPrincipal(identity);
                return principal.IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);
            }
            catch (Exception)
            {
                return false;
            }
        }

        /// <summary>最近 5 个日志（两个日志目录合并后按修改时间倒序）。</summary>
        private static int AddLogs(ZipArchive archive, string logDirectory, string toolLogDirectory,
            Redactor redactor, List<string> files)
        {
            List<string> collected = new List<string>();
            CollectLogFiles(collected, logDirectory);
            CollectLogFiles(collected, toolLogDirectory);

            collected.Sort(delegate(string left, string right)
            {
                return File.GetLastWriteTimeUtc(right).CompareTo(File.GetLastWriteTimeUtc(left));
            });

            int written = 0;
            for (int i = 0; i < collected.Count && written < 5; i++)
            {
                string file = collected[i];
                string text;
                try
                {
                    text = File.ReadAllText(file);
                }
                catch (Exception)
                {
                    continue;
                }
                if (text.Length > 512 * 1024)
                {
                    text = text.Substring(text.Length - 512 * 1024);
                }
                string entryName = "logs/" + (written + 1).ToString(CultureInfo.InvariantCulture) + "-" + Path.GetFileName(file);
                AddEntry(archive, entryName, redactor.Redact(text));
                files.Add(entryName + "\t日志（已截断到最近 512KB）");
                written++;
            }
            return written;
        }

        private static void CollectLogFiles(List<string> collected, string directory)
        {
            if (string.IsNullOrEmpty(directory) || !Directory.Exists(directory)) { return; }
            try
            {
                string[] files = Directory.GetFiles(directory, "*.log");
                for (int i = 0; i < files.Length; i++)
                {
                    collected.Add(files[i]);
                }
            }
            catch (Exception) { }
        }

        private static void AddEntry(ZipArchive archive, string name, string content)
        {
            ZipArchiveEntry entry = archive.CreateEntry(name, CompressionLevel.Optimal);
            using (Stream stream = entry.Open())
            using (StreamWriter writer = new StreamWriter(stream, new UTF8Encoding(false)))
            {
                writer.Write(content);
            }
        }

        private static string AtomicText<T>(T value)
        {
            byte[] bytes = AtomicJsonFile.Serialize(value);
            return Encoding.UTF8.GetString(bytes);
        }

        private static string ReadmeText(bool noRedact)
        {
            StringBuilder sb = new StringBuilder();
            sb.AppendLine(CodexPaths.ToolName + " 诊断包");
            sb.AppendLine();
            sb.AppendLine("每个文件是什么：");
            sb.AppendLine("  state.json          --check 的只读快照（组件状态、journal、备份数、EFS 属性）");
            sb.AppendLine("  journal.json        只有当存在未收尾操作时才出现；这是崩溃后唯一的判定依据");
            sb.AppendLine("  backups.json        备份/安全快照的元数据（不含备份内容）");
            sb.AppendLine("  settings.json       当前配置（不含任何凭据）");
            sb.AppendLine("  native-host.txt     Chrome native-messaging 注册表键、默认值与 manifest 内容");
            sb.AppendLine("  environment.txt     OS build、CLR 版本、是否管理员、区域、环境变量白名单");
            sb.AppendLine("  logs/*.log          最近 5 个日志（各自截断到最近 512KB）");
            sb.AppendLine("  manifest.json       本包的文件清单，并标明是否已脱敏");
            sb.AppendLine();
            sb.AppendLine("这个包里没有：token、API key、cookie、插件文件内容、AppX 文件、任何对话数据。");
            sb.AppendLine("脱敏：" + (noRedact ? "未启用（导出时显式传了 --no-redact）" : "已启用（用户名/计算机名/用户目录/SID/临时目录已替换）"));
            sb.AppendLine();
            sb.AppendLine("提交 issue 时请附上：");
            sb.AppendLine("  1. 你执行了什么命令、期望什么结果、实际什么结果；");
            sb.AppendLine("  2. Codex Desktop 版本（state.json 里的 desktop.version）；");
            sb.AppendLine("  3. 如果是写入类问题，附上 journal.json 与对应时间段的日志。");
            return sb.ToString();
        }

        private static string JsonEscape(string value)
        {
            if (string.IsNullOrEmpty(value)) { return string.Empty; }
            StringBuilder sb = new StringBuilder(value.Length + 8);
            for (int i = 0; i < value.Length; i++)
            {
                char c = value[i];
                switch (c)
                {
                    case '"': sb.Append("\\\""); break;
                    case '\\': sb.Append("\\\\"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\t': sb.Append("\\t"); break;
                    default:
                        if (c < ' ') { sb.Append("\\u" + ((int)c).ToString("x4", CultureInfo.InvariantCulture)); }
                        else { sb.Append(c); }
                        break;
                }
            }
            return sb.ToString();
        }
    }
}
