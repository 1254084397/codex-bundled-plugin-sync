using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;

namespace CodexPluginSync.Core
{
    public sealed class RollbackOptions
    {
        public string CodexHome;
        /// <summary>backup.json 的 backupId 或目录名；缺省取最新 status=complete。</summary>
        public string BackupId;
        /// <summary>selftest 专用：不读注册表、不改真实清单。</summary>
        public bool SkipNativeHost;
        /// <summary>selftest 专用：跳过 EFS 预检/开启/恢复。</summary>
        public bool SkipEfs;
        public TextWriter Out;
        public TextWriter Err;
    }

    public static class RollbackPhases
    {
        public const string Preflight = "rollback-preflight";
        public const string SafetyCreated = "rollback-safety-created";
        public const string CommitStarted = "rollback-commit-started";
        public const string Committed = "rollback-committed";
        public const string Finalized = "rollback-finalized";
    }

    /// <summary>
    /// §7.3 回滚（自主状态机）。要点：
    ///   1. 选备份按 backup.json 的 createdUtc，不按目录名；
    ///   2. status!=complete 直接拒绝（partial / verify-failed 不可回滚）；
    ///   3. 校验备份内容（previousDigest）在动 live 之前完成；
    ///   4. 安全快照写完并校验通过才允许动 live；
    ///   5. 先写满 journal.targets 再进入 rollback-commit-started（崩溃后只靠 journal 能判定）；
    ///   6. 失败就退回安全快照；退不干净 → needs-attention，保留 journal 与两份快照。
    /// 每个组件的迁移一律整目录 rename/复制后切换，不逐文件写 live（§6.6 配套约束）。
    /// </summary>
    public static class RollbackEngine
    {
        public const string StatusRolledBack = "rolled-back";
        public const string SafetyFolderPrefix = "rollback-before-";
        private const string LegacyMarketplaceBackup = "marketplace.previous";

        /// <summary>失败注入点（仅 selftest）："rollback-commit-&lt;index&gt;"（index 从 1 开始）。</summary>
        public static Action<string> FailureInjectionHook;

        private sealed class PlanItem
        {
            public string Component;
            public string LivePath;
            public string BackupPath;
            public bool ExistedBefore;
            public ComponentDigestDto ExpectedDigest;
            public string SafetyPath;
            public ComponentDigestDto SafetyDigest;
            public string StagePath;
            public ComponentDigestDto NewDigest;
        }

        public static SyncRunResult Run(RollbackOptions options)
        {
            SyncRunResult result = new SyncRunResult();
            TextWriter output = options.Out ?? Console.Out;
            TextWriter error = options.Err ?? Console.Error;
            EfsState efsState = null;
            string safetyRoot = null;
            List<PlanItem> plan = new List<PlanItem>();
            bool commitStarted = false;
            JournalDto journal = null;
            CodexPaths paths = null;

            try
            {
                Settings settings = Settings.Load(null);
                string codexHome = CodexPaths.ResolveCodexHome(options.CodexHome ?? settings.CodexHome);
                paths = new CodexPaths(codexHome);

                // 0) 上次操作没写完就不猜：只报事实，不动任何文件 ------------------
                JournalDto pending = JournalStore.Read(paths);
                if (pending != null)
                {
                    result.Status = SyncTransaction.StatusNeedsAttention;
                    result.ExitCode = ExitCodesLocator.NeedsAttention;
                    result.Message = "存在未收尾的操作（phase=" + pending.Phase + "），本次未写入任何内容。";
                    output.WriteLine(result.Message);
                    output.WriteLine("待处理操作：" + pending.OperationId + "，目标 " +
                        (pending.Targets == null ? 0 : pending.Targets.Count) + " 个；journal：" + paths.JournalPath);
                    return result;
                }

                // 1) 选备份 -------------------------------------------------------
                bool legacy;
                string selectError;
                string backupDirectory = SelectBackup(paths, options.BackupId, out legacy, out selectError);
                if (backupDirectory == null)
                {
                    result.Status = "no-backup";
                    result.ExitCode = ExitCodesLocator.Error;
                    result.Message = selectError;
                    error.WriteLine(selectError);
                    return result;
                }
                result.Lines.Add("备份：" + backupDirectory + "（" + (legacy ? "legacy 布局" : "manifest") + "）");

                BackupManifestDto manifest = null;
                if (!legacy)
                {
                    manifest = JsonRead.FromFile<BackupManifestDto>(Path.Combine(backupDirectory, BackupManifestStore.FileName));
                    if (manifest == null)
                    {
                        result.Message = "backup.json 不可读：" + backupDirectory;
                        error.WriteLine(result.Message);
                        return result;
                    }
                    if (manifest.Status != BackupStates.Complete)
                    {
                        result.Status = SyncTransaction.StatusAborted;
                        result.ExitCode = ExitCodesLocator.Error;
                        result.Message = "备份状态不是 complete（" + (manifest.Status ?? "(空)") + "），按 §7.3 不可回滚。";
                        error.WriteLine(result.Message);
                        return result;
                    }
                }

                // 2) 恢复计划 -----------------------------------------------------
                plan = BuildPlan(paths, backupDirectory, manifest, legacy);

                // 3) 校验备份内容（动 live 之前）---------------------------------
                for (int i = 0; i < plan.Count; i++)
                {
                    PlanItem item = plan[i];
                    if (!item.ExistedBefore) { continue; }
                    if (string.IsNullOrEmpty(item.BackupPath) || !Directory.Exists(item.BackupPath))
                    {
                        return Fail(result, error, "备份不完整：缺少 " + item.BackupPath + "（组件 " + item.Component + "）");
                    }
                    if (item.ExpectedDigest == null)
                    {
                        if (!legacy)
                        {
                            return Fail(result, error, "备份没有记录内容摘要，无法校验完整性，拒绝回滚：" + item.Component);
                        }
                        result.Lines.Add(item.Component + "：legacy 布局无摘要，只做存在性校验");
                        continue;
                    }
                    ComponentDigestDto actual = TreeDigest.Compute(item.BackupPath);
                    if (!TreeDigest.Equal(actual, item.ExpectedDigest))
                    {
                        return Fail(result, error, "备份内容与记录不一致，拒绝回滚：" + item.Component + "（" + item.BackupPath + "）");
                    }
                    result.Lines.Add(item.Component + "：备份摘要校验通过");
                }

                // 4) EFS：与 sync 同一套“只撤销本次自己的开启” --------------------
                string runId = CodexPaths.Stamp() + "-" + Guid.NewGuid().ToString("N").Substring(0, 6);
                if (!options.SkipEfs)
                {
                    efsState = Efs.PrepareForWrite(paths.MarketParent);
                    result.Lines.Add("EFS：preEncrypted=" + efsState.PreEncrypted + "，enabledByThisRun=" + efsState.EnabledByThisRun);
                }

                // 5) 安全快照：写完 + 校验通过之前不动 live ----------------------
                journal = new JournalDto();
                journal.OperationId = runId;
                journal.BackupId = Path.GetFileName(backupDirectory);
                journal.Phase = RollbackPhases.Preflight;
                JournalStore.Write(paths, journal);

                safetyRoot = Path.Combine(paths.BackupsRoot, SafetyFolderPrefix + runId);
                if (Directory.Exists(safetyRoot))
                {
                    return Fail(result, error, "安全快照路径已存在：" + safetyRoot);
                }
                Directory.CreateDirectory(safetyRoot);

                for (int i = 0; i < plan.Count; i++)
                {
                    PlanItem item = plan[i];
                    item.SafetyPath = Path.Combine(safetyRoot, item.Component.Replace(':', '-'));
                    if (!Directory.Exists(item.LivePath))
                    {
                        item.SafetyPath = null;
                        continue;
                    }
                    ComponentDigestDto liveDigest;
                    Directory.CreateDirectory(item.SafetyPath);
                    Installer.CopyTreePreservingLinks(item.LivePath, item.SafetyPath);
                    liveDigest = TreeDigest.Compute(item.LivePath);
                    ComponentDigestDto copyDigest = TreeDigest.Compute(item.SafetyPath);
                    if (!TreeDigest.Equal(liveDigest, copyDigest))
                    {
                        return Fail(result, error, "安全快照校验失败，未移动任何 live 内容：" + item.Component);
                    }
                    item.SafetyDigest = copyDigest;
                    result.Lines.Add(item.Component + "：安全快照已校验");
                }

                journal.Phase = RollbackPhases.SafetyCreated;
                JournalStore.Write(paths, journal);

                // 6) 准备恢复内容（stage）+ 先写满 targets，再进入 commit-started --
                for (int i = 0; i < plan.Count; i++)
                {
                    PlanItem item = plan[i];
                    if (!item.ExistedBefore) { continue; }
                    string parent = Path.GetDirectoryName(item.LivePath);
                    item.StagePath = Path.Combine(parent, Path.GetFileName(item.LivePath) + ".rollback-stage-" + runId);
                    if (Directory.Exists(item.StagePath))
                    {
                        Directory.Delete(item.StagePath, true);
                    }
                    Installer.CopyTreePreservingLinks(item.BackupPath, item.StagePath);
                    item.NewDigest = TreeDigest.Compute(item.StagePath);
                    if (item.ExpectedDigest != null && !TreeDigest.Equal(item.NewDigest, item.ExpectedDigest))
                    {
                        return Fail(result, error, "恢复内容复制后校验失败：" + item.Component);
                    }
                }

                journal.Targets = new List<JournalTargetDto>();
                for (int i = 0; i < plan.Count; i++)
                {
                    PlanItem item = plan[i];
                    JournalTargetDto target = new JournalTargetDto();
                    target.Component = item.Component;
                    target.LivePath = item.LivePath;
                    target.StagePath = item.StagePath;
                    target.SafetyPath = item.SafetyPath;
                    target.OldDigest = item.SafetyDigest;
                    target.NewDigest = item.NewDigest;
                    target.SwappedOut = false;
                    target.SwappedIn = false;
                    journal.Targets.Add(target);
                }
                journal.Phase = RollbackPhases.CommitStarted;
                JournalStore.Write(paths, journal);
                commitStarted = true;

                // 7) 逐组件：live → 安全快照（已复制），备份内容 → live -----------
                for (int i = 0; i < plan.Count; i++)
                {
                    PlanItem item = plan[i];
                    if (Directory.Exists(item.LivePath))
                    {
                        Directory.Delete(item.LivePath, true);
                    }
                    journal.Targets[i].SwappedOut = true;
                    JournalStore.Write(paths, journal);

                    if (item.ExistedBefore)
                    {
                        Directory.Move(item.StagePath, item.LivePath);
                        journal.Targets[i].SwappedIn = true;
                        JournalStore.Write(paths, journal);
                        result.Lines.Add(item.Component + "：已回到同步前内容");
                    }
                    else
                    {
                        result.Lines.Add(item.Component + "：本次新建的组件已移除");
                    }

                    MaybeFail("rollback-commit-" + (i + 1).ToString(CultureInfo.InvariantCulture));
                }

                // 8) native host 恢复（§9.4 路径边界）----------------------------
                RestoreNativeHost(options, paths, backupDirectory, result);

                // 9) 逐组件校验：live 摘要必须等于要回填的内容 -------------------
                for (int i = 0; i < plan.Count; i++)
                {
                    PlanItem item = plan[i];
                    if (!item.ExistedBefore) { continue; }
                    ComponentDigestDto liveNow = TreeDigest.Compute(item.LivePath);
                    if (!TreeDigest.Equal(liveNow, item.NewDigest))
                    {
                        throw new InvalidOperationException("回滚校验失败：" + item.Component + " 的 live 摘要与备份内容不一致");
                    }
                    if (!HasPluginManifest(item.LivePath))
                    {
                        result.Lines.Add(item.Component + "：警告 —— live 内找不到 .codex-plugin\\plugin.json");
                    }
                }

                journal.Phase = RollbackPhases.Committed;
                JournalStore.Write(paths, journal);

                // 10) 安全快照写成可回滚的 backup.json --------------------------
                WriteSafetyManifest(safetyRoot, runId, backupDirectory, plan, result);

                journal.Phase = RollbackPhases.Finalized;
                JournalStore.Write(paths, journal);
                JournalStore.Delete(paths);
                journal = null;

                result.Status = StatusRolledBack;
                result.Message = "回滚完成并通过校验";
                result.ExitCode = ExitCodesLocator.Ok;
                for (int i = 0; i < result.Lines.Count; i++)
                {
                    output.WriteLine(result.Lines[i]);
                }
                output.WriteLine(result.Message + "；备份：" + backupDirectory);
                output.WriteLine("回滚前状态保留在：" + safetyRoot);
                return result;
            }
            catch (Exception ex)
            {
                result.Message = ex.Message;
                if (commitStarted && journal != null && paths != null)
                {
                    bool reverted = RevertFromSafety(plan, safetyRoot, result);
                    journal.Phase = reverted ? JournalPhases.Recovered : JournalPhases.NeedsAttention;
                    result.Status = reverted ? SyncTransaction.StatusRecovered : SyncTransaction.StatusNeedsAttention;
                    result.ExitCode = reverted ? ExitCodesLocator.Error : ExitCodesLocator.NeedsAttention;
                    try
                    {
                        if (reverted)
                        {
                            JournalStore.Delete(paths);
                        }
                        else
                        {
                            JournalStore.Write(paths, journal);
                        }
                    }
                    catch (Exception) { }
                }
                else
                {
                    result.Status = SyncTransaction.StatusAborted;
                    result.ExitCode = ExitCodesLocator.Error;
                    if (paths != null)
                    {
                        try { JournalStore.Delete(paths); }
                        catch (Exception) { }
                    }
                }
                error.WriteLine("回滚失败：" + ex.Message);
                if (!string.IsNullOrEmpty(safetyRoot) && Directory.Exists(safetyRoot))
                {
                    error.WriteLine("回滚前的当前状态保留在：" + safetyRoot);
                }
                return result;
            }
            finally
            {
                if (!options.SkipEfs)
                {
                    EfsRestoreResult efsRestore = Efs.Restore(efsState);
                    if (efsRestore.Restored)
                    {
                        if (efsState != null)
                        {
                            output.WriteLine("EFS：已回到操作前状态（now=" + efsRestore.NowEncrypted + "）");
                        }
                    }
                    else
                    {
                        output.WriteLine("EFS：needs-attention —— " + efsRestore.Message);
                    }
                }
                CleanupStages(plan);
            }
        }

        private static void MaybeFail(string point)
        {
            Action<string> hook = FailureInjectionHook;
            if (hook != null)
            {
                hook(point);
            }
        }

        private static SyncRunResult Fail(SyncRunResult result, TextWriter error, string message)
        {
            result.Message = message;
            result.Status = SyncTransaction.StatusAborted;
            result.ExitCode = ExitCodesLocator.Error;
            error.WriteLine(message);
            return result;
        }

        private static bool HasPluginManifest(string livePath)
        {
            if (!Directory.Exists(livePath)) { return false; }
            string direct = Path.Combine(livePath, ".codex-plugin\\plugin.json");
            if (File.Exists(direct)) { return true; }
            try
            {
                string[] found = Directory.GetFiles(livePath, "plugin.json", SearchOption.AllDirectories);
                for (int i = 0; i < found.Length; i++)
                {
                    if (found[i].IndexOf(@"\.codex-plugin\", StringComparison.OrdinalIgnoreCase) >= 0) { return true; }
                }
            }
            catch (Exception) { }
            return false;
        }

        /// <summary>组件名 → live 路径（§6.3 名称约定，与脚本层 Get-ComponentLivePath 一致）。</summary>
        private static string ComponentLivePath(CodexPaths paths, string component)
        {
            if (string.Equals(component, "marketplace", StringComparison.OrdinalIgnoreCase))
            {
                return paths.MarketBundle;
            }
            if (component != null && component.StartsWith("cache:", StringComparison.OrdinalIgnoreCase))
            {
                return Path.Combine(paths.CacheParent, component.Substring(6));
            }
            throw new InvalidOperationException("未知组件名：" + component);
        }

        /// <summary>legacy 布局里组件的备份落点。</summary>
        private static string LegacyBackupPath(string backupDirectory, string component)
        {
            if (string.Equals(component, "marketplace", StringComparison.OrdinalIgnoreCase))
            {
                return Path.Combine(backupDirectory, LegacyMarketplaceBackup);
            }
            return Path.Combine(backupDirectory, "cache-" + component.Substring(6) + ".previous");
        }

        private static List<PlanItem> BuildPlan(CodexPaths paths, string backupDirectory, BackupManifestDto manifest, bool legacy)
        {
            List<PlanItem> plan = new List<PlanItem>();
            if (legacy)
            {
                PlanItem marketplace = new PlanItem();
                marketplace.Component = "marketplace";
                marketplace.LivePath = paths.MarketBundle;
                marketplace.BackupPath = LegacyBackupPath(backupDirectory, "marketplace");
                marketplace.ExistedBefore = true;
                plan.Add(marketplace);

                string[] plugins = new string[] { "browser", "chrome", "computer-use" };
                for (int i = 0; i < plugins.Length; i++)
                {
                    string backupPath = Path.Combine(backupDirectory, "cache-" + plugins[i] + ".previous");
                    if (!Directory.Exists(backupPath)) { continue; }
                    PlanItem item = new PlanItem();
                    item.Component = "cache:" + plugins[i];
                    item.LivePath = ComponentLivePath(paths, item.Component);
                    item.BackupPath = backupPath;
                    item.ExistedBefore = true;
                    plan.Add(item);
                }
                return plan;
            }

            if (manifest.Components == null || manifest.Components.Count == 0)
            {
                throw new InvalidOperationException("备份没有记录任何组件改动，不能回滚");
            }

            for (int i = 0; i < manifest.Components.Count; i++)
            {
                BackupComponentDto component = manifest.Components[i];
                if (component == null || string.IsNullOrEmpty(component.Name)) { continue; }
                if (component.State == BackupStates.Planned) { continue; }

                PlanItem item = new PlanItem();
                item.Component = component.Name;
                item.LivePath = ComponentLivePath(paths, component.Name);
                item.ExistedBefore = component.ExistedBefore;
                item.ExpectedDigest = component.PreviousDigest;
                if (component.ExistedBefore)
                {
                    item.BackupPath = component.BackupPath;
                    if (string.IsNullOrEmpty(item.BackupPath))
                    {
                        item.BackupPath = LegacyBackupPath(backupDirectory, component.Name);
                    }
                }
                plan.Add(item);
            }

            if (plan.Count == 0)
            {
                throw new InvalidOperationException("备份里没有任何可回滚的组件");
            }
            return plan;
        }

        /// <summary>
        /// 选备份：--backup 指定 id/目录名，否则取 createdUtc 最新的 status=complete；
        /// 全都没有时退回 legacy 布局（无 backup.json，按 *-previous 子集，只读兼容）。
        /// </summary>
        private static string SelectBackup(CodexPaths paths, string backupId, out bool legacy, out string error)
        {
            legacy = false;
            error = null;
            if (!Directory.Exists(paths.BackupsRoot))
            {
                error = "没有任何备份目录：" + paths.BackupsRoot;
                return null;
            }

            string[] directories = Directory.GetDirectories(paths.BackupsRoot);
            List<string> withManifest = new List<string>();
            List<DateTime> withManifestStamp = new List<DateTime>();
            List<BackupManifestDto> manifests = new List<BackupManifestDto>();

            for (int i = 0; i < directories.Length; i++)
            {
                string directory = directories[i];
                string name = Path.GetFileName(directory);
                string manifestPath = Path.Combine(directory, BackupManifestStore.FileName);
                if (!File.Exists(manifestPath)) { continue; }

                BackupManifestDto dto;
                try
                {
                    dto = JsonRead.FromFile<BackupManifestDto>(manifestPath);
                }
                catch (Exception)
                {
                    continue;
                }
                if (dto == null) { continue; }
                if (!MatchesBackupId(backupId, name, dto.BackupId)) { continue; }
                if (dto.Status != BackupStates.Complete) { continue; }

                DateTime stamp;
                if (!DateTime.TryParse(dto.CreatedUtc, CultureInfo.InvariantCulture,
                        DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out stamp))
                {
                    stamp = Directory.GetLastWriteTimeUtc(directory);
                }
                withManifest.Add(directory);
                withManifestStamp.Add(stamp);
                manifests.Add(dto);
            }

            int best = -1;
            for (int i = 0; i < withManifest.Count; i++)
            {
                if (best < 0 || withManifestStamp[i] > withManifestStamp[best]) { best = i; }
            }
            if (best >= 0)
            {
                return withManifest[best];
            }

            // legacy：没有 backup.json 但带 marketplace.previous
            string legacyBest = null;
            DateTime legacyStamp = DateTime.MinValue;
            for (int i = 0; i < directories.Length; i++)
            {
                string directory = directories[i];
                if (File.Exists(Path.Combine(directory, BackupManifestStore.FileName))) { continue; }
                if (!MatchesBackupId(backupId, Path.GetFileName(directory), null)) { continue; }
                if (!Directory.Exists(Path.Combine(directory, LegacyMarketplaceBackup))) { continue; }
                DateTime stamp = Directory.GetLastWriteTimeUtc(directory);
                if (legacyBest == null || stamp > legacyStamp)
                {
                    legacyBest = directory;
                    legacyStamp = stamp;
                }
            }
            if (legacyBest != null)
            {
                legacy = true;
                return legacyBest;
            }

            error = string.IsNullOrEmpty(backupId)
                ? "没有 status=complete 的备份，也没有可用的 legacy 备份布局。"
                : "找不到可用备份：" + backupId + "（需要 status=complete 或 legacy 布局）";
            return null;
        }

        private static bool MatchesBackupId(string wanted, string directoryName, string manifestId)
        {
            if (string.IsNullOrEmpty(wanted)) { return true; }
            if (string.Equals(directoryName, wanted, StringComparison.OrdinalIgnoreCase)) { return true; }
            if (string.Equals(directoryName, CodexPaths.BackupFolderPrefix + wanted, StringComparison.OrdinalIgnoreCase)) { return true; }
            return !string.IsNullOrEmpty(manifestId) && string.Equals(manifestId, wanted, StringComparison.OrdinalIgnoreCase);
        }

        private static void WriteSafetyManifest(string safetyRoot, string runId, string sourceBackup,
            List<PlanItem> plan, SyncRunResult result)
        {
            BackupManifestDto safety = new BackupManifestDto();
            safety.BackupId = runId;
            safety.Kind = "rollback-safety";
            safety.Status = BackupStates.Complete;
            safety.CreatedUtc = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture);
            safety.Components = new List<BackupComponentDto>();
            safety.NativeHost = new List<BackupNativeHostDto>();

            for (int i = 0; i < plan.Count; i++)
            {
                PlanItem item = plan[i];
                string safetyPath = Path.Combine(safetyRoot, item.Component.Replace(':', '-'));
                bool exists = Directory.Exists(safetyPath);
                BackupComponentDto component = new BackupComponentDto();
                component.Name = item.Component;
                component.LivePath = item.LivePath;
                component.BackupPath = exists ? safetyPath : null;
                component.ExistedBefore = exists;
                component.State = exists ? BackupStates.Moved : BackupStates.Created;
                component.Digest = item.SafetyDigest;
                component.PreviousDigest = item.SafetyDigest;
                safety.Components.Add(component);
            }

            BackupManifestStore.Write(safetyRoot, safety);
            string validateError = BackupManifestStore.Validate(safety);
            if (validateError != null)
            {
                throw new InvalidOperationException("安全快照 backup.json 完成判据不通过：" + validateError);
            }
            result.Lines.Add("安全快照：" + safetyRoot + "（源自 " + Path.GetFileName(sourceBackup) + "）");
        }

        /// <summary>失败退回：live → 安全快照内容（逆序）；任一组件不干净就返回 false，journal 必须保留。</summary>
        private static bool RevertFromSafety(List<PlanItem> plan, string safetyRoot, SyncRunResult result)
        {
            bool allReverted = true;
            for (int i = plan.Count - 1; i >= 0; i--)
            {
                PlanItem item = plan[i];
                try
                {
                    if (Directory.Exists(item.LivePath))
                    {
                        Directory.Delete(item.LivePath, true);
                    }
                    string safetyPath = Path.Combine(safetyRoot, item.Component.Replace(':', '-'));
                    if (!Directory.Exists(safetyPath))
                    {
                        if (item.SafetyDigest != null)
                        {
                            allReverted = false;
                            result.Lines.Add(item.Component + "：安全快照缺失，无法退回（需人工处理）");
                        }
                        continue;
                    }
                    Installer.CopyTreePreservingLinks(safetyPath, item.LivePath);
                    ComponentDigestDto restored = TreeDigest.Compute(item.LivePath);
                    if (!TreeDigest.Equal(restored, item.SafetyDigest))
                    {
                        allReverted = false;
                        result.Lines.Add(item.Component + "：退回后摘要与安全快照不一致");
                    }
                    else
                    {
                        result.Lines.Add(item.Component + "：已退回回滚前内容");
                    }
                }
                catch (Exception ex)
                {
                    allReverted = false;
                    result.Lines.Add(item.Component + "：退回失败 —— " + ex.Message);
                }
            }
            return allReverted;
        }

        /// <summary>journal 仍在 = 现场必须保留（§6.6 配套约束），此时不清理 stage。</summary>
        private static void CleanupStages(List<PlanItem> plan)
        {
            for (int i = 0; i < plan.Count; i++)
            {
                string stage = plan[i].StagePath;
                if (string.IsNullOrEmpty(stage)) { continue; }
                try
                {
                    if (Directory.Exists(stage)) { Directory.Delete(stage, true); }
                }
                catch (Exception) { }
            }
        }

        private static void RestoreNativeHost(RollbackOptions options, CodexPaths paths, string backupDirectory, SyncRunResult result)
        {
            if (options.SkipNativeHost)
            {
                result.Lines.Add("native host：selftest 跳过（未读注册表）");
                return;
            }

            string manifestBackup = Path.Combine(backupDirectory, NativeHostWriter.ManifestFileName + ".previous");
            if (!File.Exists(manifestBackup))
            {
                result.Lines.Add("native host：备份中没有清单副本，跳过");
                return;
            }

            string current = NativeHostWriter.ReadRegistryDefault();
            if (string.IsNullOrEmpty(current))
            {
                throw new InvalidOperationException("native host：注册表项不存在，无法确定清单路径，拒绝猜测");
            }
            string boundaryError = NativeHostWriter.ValidateManifestPath(current, NativeHostWriter.AllowedRoots(paths.CodexHome));
            if (boundaryError != null)
            {
                throw new InvalidOperationException("native host：拒绝写入 —— " + boundaryError);
            }

            File.Copy(manifestBackup, current, true);
            string before;
            string after;
            using (System.Security.Cryptography.SHA256 sha = System.Security.Cryptography.SHA256.Create())
            {
                before = BitConverter.ToString(sha.ComputeHash(File.ReadAllBytes(manifestBackup)));
                after = BitConverter.ToString(sha.ComputeHash(File.ReadAllBytes(current)));
            }
            if (!string.Equals(before, after, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException("native host：清单恢复后字节校验失败：" + current);
            }
            result.Lines.Add("native host：清单已恢复（" + current + "）");
        }
    }
}
