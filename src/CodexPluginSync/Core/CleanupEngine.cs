using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;

namespace CodexPluginSync.Core
{
    public sealed class CleanupOptions
    {
        public string CodexHome;
        /// <summary>只打印计划、不删任何东西。</summary>
        public bool DryRun;
        /// <summary>selftest 专用：替换设置，避免读真实 settings.json。</summary>
        public Settings SettingsOverride;
        /// <summary>selftest 专用：替换工具日志目录（真实目录为 %LOCALAPPDATA%\CodexPluginSync\logs）。</summary>
        public string ToolLogDirectoryOverride;
        public TextWriter Out;
        public TextWriter Err;
    }

    /// <summary>
    /// §7.4 清理。三条硬规则：
    ///   1. 身份一律读 `backup.json` 的 kind/status，只有 legacy 目录才回退到目录名前缀；
    ///   2. 当前 journal 引用的任何目录（stage / backup / safety）永不自动删除；
    ///   3. 只删自己创建的命名模式，且删除前做路径边界与 reparse point 检查。
    /// </summary>
    public static class CleanupEngine
    {
        public const string StatusCleaned = "cleaned";
        public const string StatusDryRun = "dry-run";

        private sealed class Candidate
        {
            public string Category;
            public string Path;
            public DateTime Stamp;
            public bool Delete;
            public string Reason;
        }

        public static SyncRunResult Run(CleanupOptions options)
        {
            SyncRunResult result = new SyncRunResult();
            TextWriter output = options.Out ?? Console.Out;
            TextWriter error = options.Err ?? Console.Error;

            try
            {
                Settings settings = options.SettingsOverride ?? Settings.Load(null);
                string codexHome = CodexPaths.ResolveCodexHome(options.CodexHome ?? settings.CodexHome);
                CodexPaths paths = new CodexPaths(codexHome);
                string toolLogDirectory = options.ToolLogDirectoryOverride ?? CodexPaths.ToolLogDirectory;

                DateTime now = DateTime.UtcNow;
                List<string> referenced = ReferencedPaths(paths);
                List<Candidate> candidates = new List<Candidate>();

                // stage：只看命名模式 + 老于 staleStageHours + 不被 journal 引用
                CollectStages(candidates, paths.MarketParent, "openai-bundled.stage-*", settings.StaleStageHours, now, referenced);
                CollectStages(candidates, paths.CacheParent, "*.stage-*", settings.StaleStageHours, now, referenced);
                CollectStages(candidates, paths.MarketParent, "*.rollback-stage-*", settings.StaleStageHours, now, referenced);
                CollectStages(candidates, paths.CacheParent, "*.rollback-stage-*", settings.StaleStageHours, now, referenced);

                // 备份：按 kind/status 保留规则
                CollectBackups(candidates, paths, settings, now, referenced);

                // 日志：早于 keepLogDays 才删；最近 1 小时内写过的视为“正在写”，不删
                CollectLogs(candidates, paths.LogDirectory, "日志（CodexHome）", settings.KeepLogDays, now);
                CollectLogs(candidates, toolLogDirectory, "日志（工具）", settings.KeepLogDays, now);

                int deleted = 0;
                int kept = 0;
                for (int i = 0; i < candidates.Count; i++)
                {
                    Candidate candidate = candidates[i];
                    if (!candidate.Delete)
                    {
                        kept++;
                        result.Lines.Add("保留 " + candidate.Category + "：" + candidate.Path + "（" + candidate.Reason + "）");
                        continue;
                    }

                    if (options.DryRun)
                    {
                        result.Lines.Add("将删除 " + candidate.Category + "：" + candidate.Path);
                        continue;
                    }

                    string boundary = CheckBoundary(candidate.Path, paths, toolLogDirectory);
                    if (boundary != null)
                    {
                        kept++;
                        result.Lines.Add("跳过 " + candidate.Category + "：" + candidate.Path + "（" + boundary + "）");
                        continue;
                    }

                    try
                    {
                        DeleteTree(candidate.Path);
                        deleted++;
                        result.Lines.Add("已删除 " + candidate.Category + "：" + candidate.Path);
                    }
                    catch (Exception ex)
                    {
                        kept++;
                        result.Lines.Add("删除失败 " + candidate.Category + "：" + candidate.Path + " —— " + ex.Message);
                    }
                }

                result.Status = options.DryRun ? StatusDryRun : StatusCleaned;
                result.Message = options.DryRun
                    ? "清理计划：" + candidates.Count + " 项（未删除任何内容）"
                    : "清理完成：删除 " + deleted + " 项，保留 " + kept + " 项";
                result.ExitCode = ExitCodesLocator.Ok;

                for (int i = 0; i < result.Lines.Count; i++)
                {
                    output.WriteLine(result.Lines[i]);
                }
                output.WriteLine(result.Message);
                return result;
            }
            catch (Exception ex)
            {
                result.Status = SyncTransaction.StatusAborted;
                result.Message = ex.Message;
                result.ExitCode = ExitCodesLocator.Error;
                error.WriteLine("清理失败：" + ex.Message);
                return result;
            }
        }

        private static List<string> ReferencedPaths(CodexPaths paths)
        {
            List<string> referenced = new List<string>();
            JournalDto journal = JournalStore.Read(paths);
            if (journal == null)
            {
                return referenced;
            }
            if (!string.IsNullOrEmpty(journal.BackupId))
            {
                referenced.Add(Normalize(Path.Combine(paths.BackupsRoot, journal.BackupId)));
            }
            if (journal.Targets == null)
            {
                return referenced;
            }
            for (int i = 0; i < journal.Targets.Count; i++)
            {
                JournalTargetDto target = journal.Targets[i];
                if (target == null) { continue; }
                if (!string.IsNullOrEmpty(target.StagePath)) { referenced.Add(Normalize(target.StagePath)); }
                if (!string.IsNullOrEmpty(target.SafetyPath)) { referenced.Add(Normalize(target.SafetyPath)); }
            }
            return referenced;
        }

        private static bool IsReferenced(List<string> referenced, string path)
        {
            string normalized = Normalize(path);
            for (int i = 0; i < referenced.Count; i++)
            {
                if (string.Equals(referenced[i], normalized, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
            return false;
        }

        private static void CollectStages(List<Candidate> candidates, string parent, string pattern,
            int staleHours, DateTime now, List<string> referenced)
        {
            if (!Directory.Exists(parent)) { return; }
            string[] directories;
            try
            {
                directories = Directory.GetDirectories(parent, pattern);
            }
            catch (Exception)
            {
                return;
            }

            for (int i = 0; i < directories.Length; i++)
            {
                string directory = directories[i];
                Candidate candidate = new Candidate();
                candidate.Category = "stage";
                candidate.Path = directory;
                try
                {
                    candidate.Stamp = Directory.GetLastWriteTimeUtc(directory);
                }
                catch (Exception)
                {
                    candidate.Stamp = now;
                }
                if (IsReferenced(referenced, directory))
                {
                    candidate.Delete = false;
                    candidate.Reason = "被当前 journal 引用";
                }
                else if ((now - candidate.Stamp).TotalHours < staleHours)
                {
                    candidate.Delete = false;
                    candidate.Reason = "未超过 staleStageHours=" + staleHours.ToString(CultureInfo.InvariantCulture);
                }
                else
                {
                    candidate.Delete = true;
                    candidate.Reason = "超过 staleStageHours 且无引用";
                }
                candidates.Add(candidate);
            }
        }

        private static void CollectBackups(List<Candidate> candidates, CodexPaths paths, Settings settings,
            DateTime now, List<string> referenced)
        {
            if (!Directory.Exists(paths.BackupsRoot)) { return; }

            string[] directories;
            try
            {
                directories = Directory.GetDirectories(paths.BackupsRoot);
            }
            catch (Exception)
            {
                return;
            }

            List<Candidate> complete = new List<Candidate>();
            List<Candidate> safety = new List<Candidate>();

            for (int i = 0; i < directories.Length; i++)
            {
                string directory = directories[i];
                string name = Path.GetFileName(directory);
                string manifestPath = Path.Combine(directory, BackupManifestStore.FileName);

                Candidate candidate = new Candidate();
                candidate.Category = "备份";
                candidate.Path = directory;
                candidate.Stamp = SafeStamp(directory, now);

                if (IsReferenced(referenced, directory))
                {
                    candidate.Delete = false;
                    candidate.Reason = "被当前 journal 引用";
                    candidates.Add(candidate);
                    continue;
                }

                if (!File.Exists(manifestPath))
                {
                    if (IsEmptyDirectory(directory))
                    {
                        candidate.Category = "空备份目录";
                        candidate.Delete = true;
                        candidate.Reason = "空目录（失败运行残留）";
                        candidates.Add(candidate);
                        continue;
                    }
                    if (name.StartsWith(CodexPaths.BackupFolderPrefix, StringComparison.OrdinalIgnoreCase))
                    {
                        // legacy：无 backup.json，按前缀认定，纳入 complete 保留计数
                        candidate.Category = "legacy 备份";
                        candidate.Reason = "legacy 布局";
                        complete.Add(candidate);
                        continue;
                    }
                    candidate.Delete = false;
                    candidate.Reason = "不是本工具创建的目录";
                    candidates.Add(candidate);
                    continue;
                }

                BackupManifestDto manifest;
                try
                {
                    manifest = JsonRead.FromFile<BackupManifestDto>(manifestPath);
                }
                catch (Exception)
                {
                    candidate.Delete = false;
                    candidate.Reason = "backup.json 不可读，保守保留";
                    candidates.Add(candidate);
                    continue;
                }

                if (manifest != null && !string.IsNullOrEmpty(manifest.CreatedUtc))
                {
                    DateTime parsed;
                    if (DateTime.TryParse(manifest.CreatedUtc, CultureInfo.InvariantCulture,
                            DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out parsed))
                    {
                        candidate.Stamp = parsed;
                    }
                }

                string kind = manifest == null ? null : manifest.Kind;
                string status = manifest == null ? null : manifest.Status;

                if (string.Equals(kind, "rollback-safety", StringComparison.OrdinalIgnoreCase))
                {
                    candidate.Category = "回滚安全快照";
                    candidate.Reason = "rollback-safety";
                    safety.Add(candidate);
                }
                else if (status == BackupStates.Complete)
                {
                    candidate.Category = "备份（complete）";
                    candidate.Reason = "complete";
                    complete.Add(candidate);
                }
                else if (status == BackupStates.Partial)
                {
                    ApplyAge(candidate, now, settings.PartialBackupRetentionDays, "partialBackupRetentionDays");
                    candidates.Add(candidate);
                }
                else if (status == BackupStates.VerifyFailed)
                {
                    ApplyAge(candidate, now, settings.FailedBackupRetentionDays, "failedBackupRetentionDays");
                    candidates.Add(candidate);
                }
                else
                {
                    candidate.Delete = false;
                    candidate.Reason = "status=" + (status == null ? "(空)" : status) + "，保守保留";
                    candidates.Add(candidate);
                }
            }

            ApplyKeepNewest(candidates, complete, settings.KeepBackups, "keepBackups（只统计 complete）");
            ApplyKeepNewest(candidates, safety, 1, "kind=rollback-safety 只保留最新 1 个");
        }

        private static void ApplyKeepNewest(List<Candidate> candidates, List<Candidate> group, int keep, string rule)
        {
            if (group.Count == 0) { return; }
            group.Sort(delegate(Candidate left, Candidate right) { return right.Stamp.CompareTo(left.Stamp); });
            for (int i = 0; i < group.Count; i++)
            {
                Candidate candidate = group[i];
                if (i < keep)
                {
                    candidate.Delete = false;
                    candidate.Reason = "在 " + rule + " 的保留范围内（第 " + (i + 1).ToString(CultureInfo.InvariantCulture) + " 新）";
                }
                else
                {
                    candidate.Delete = true;
                    candidate.Reason = "超出 " + rule;
                }
                candidates.Add(candidate);
            }
        }

        private static void ApplyAge(Candidate candidate, DateTime now, int days, string settingName)
        {
            double ageDays = (now - candidate.Stamp).TotalDays;
            if (ageDays < days)
            {
                candidate.Delete = false;
                candidate.Reason = "未超过 " + settingName + "=" + days.ToString(CultureInfo.InvariantCulture);
            }
            else
            {
                candidate.Delete = true;
                candidate.Reason = "超过 " + settingName + "=" + days.ToString(CultureInfo.InvariantCulture);
            }
        }

        private static void CollectLogs(List<Candidate> candidates, string directory, string label, int keepLogDays, DateTime now)
        {
            if (string.IsNullOrEmpty(directory) || !Directory.Exists(directory)) { return; }
            string[] files;
            try
            {
                files = Directory.GetFiles(directory, "*.log");
            }
            catch (Exception)
            {
                return;
            }

            for (int i = 0; i < files.Length; i++)
            {
                string file = files[i];
                Candidate candidate = new Candidate();
                candidate.Category = label;
                candidate.Path = file;
                try
                {
                    candidate.Stamp = File.GetLastWriteTimeUtc(file);
                }
                catch (Exception)
                {
                    candidate.Stamp = now;
                }

                if ((now - candidate.Stamp).TotalHours < 1)
                {
                    candidate.Delete = false;
                    candidate.Reason = "最近 1 小时写过的日志视为正在使用";
                }
                else if ((now - candidate.Stamp).TotalDays < keepLogDays)
                {
                    candidate.Delete = false;
                    candidate.Reason = "未超过 keepLogDays=" + keepLogDays.ToString(CultureInfo.InvariantCulture);
                }
                else
                {
                    candidate.Delete = true;
                    candidate.Reason = "超过 keepLogDays=" + keepLogDays.ToString(CultureInfo.InvariantCulture);
                }
                candidates.Add(candidate);
            }
        }

        /// <summary>删除前统一做边界检查：必须落在自己管理的根目录内，且不允许顺着 reparse point 递归。</summary>
        private static string CheckBoundary(string path, CodexPaths paths, string toolLogDirectory)
        {
            List<string> roots = new List<string>();
            roots.Add(paths.BackupsRoot);
            roots.Add(paths.MarketParent);
            roots.Add(paths.CacheParent);
            roots.Add(paths.LogDirectory);
            roots.Add(toolLogDirectory);

            string full = Normalize(path);
            bool inside = false;
            for (int i = 0; i < roots.Count; i++)
            {
                if (string.IsNullOrEmpty(roots[i])) { continue; }
                string root = Normalize(roots[i]);
                if (full.Equals(root, StringComparison.OrdinalIgnoreCase))
                {
                    return "拒绝删除根目录本身";
                }
                if (full.StartsWith(root + "\\", StringComparison.OrdinalIgnoreCase))
                {
                    inside = true;
                }
            }
            if (!inside)
            {
                return "路径不在本工具管理的目录内";
            }
            if (full.IndexOf("..", StringComparison.Ordinal) >= 0)
            {
                return "路径包含 ..";
            }
            return null;
        }

        private static void DeleteTree(string path)
        {
            FileAttributes attributes = File.GetAttributes(path);
            bool isDirectory = (attributes & FileAttributes.Directory) == FileAttributes.Directory;
            bool isReparse = (attributes & FileAttributes.ReparsePoint) == FileAttributes.ReparsePoint;

            if (!isDirectory)
            {
                File.Delete(path);
                return;
            }
            if (isReparse)
            {
                // 只删链接本身，绝不递归进目标（避免越界删除）
                Directory.Delete(path, false);
                return;
            }
            Directory.Delete(path, true);
        }

        private static bool IsEmptyDirectory(string directory)
        {
            try
            {
                string[] entries = Directory.GetFileSystemEntries(directory);
                return entries.Length == 0;
            }
            catch (Exception)
            {
                return false;
            }
        }

        private static DateTime SafeStamp(string directory, DateTime fallback)
        {
            try
            {
                return Directory.GetLastWriteTimeUtc(directory);
            }
            catch (Exception)
            {
                return fallback;
            }
        }

        private static string Normalize(string path)
        {
            if (string.IsNullOrEmpty(path)) { return string.Empty; }
            try
            {
                string full = Path.GetFullPath(path);
                if (full.Length > 3)
                {
                    full = full.TrimEnd('\\');
                }
                return full;
            }
            catch (Exception)
            {
                return path;
            }
        }
    }
}
