using System;
using System.Collections.Generic;
using System.IO;

namespace CodexPluginSync.Core
{
    /// <summary>一个待交换的组件：stage 已由调用方准备好，本类负责备份、交换、校验与 journal。</summary>
    public sealed class SyncTarget
    {
        public string Component;
        public string LivePath;
        public string StagePath;
        public string BackupPath;
        public bool ExistedBefore;

        /// <summary>已计算结果（Execute 填充），供日志与 backup.json 使用。</summary>
        public ComponentDigestDto OldDigest;
        public ComponentDigestDto NewDigest;
    }

    public sealed class SyncTransactionResult
    {
        public string Status = SyncTransaction.StatusAborted;
        public string Message;
        public List<string> Actions = new List<string>();

        public void Add(string action)
        {
            Actions.Add(action);
        }
    }

    public static class JournalPhases
    {
        public const string Preflight = "preflight";
        public const string Planned = "planned";
        public const string BackupCreated = "backup-created";
        public const string Staged = "staged";
        public const string Verified = "verified";
        public const string CommitStarted = "commit-started";
        public const string Committed = "committed";
        public const string Finalized = "finalized";
        public const string Recovered = "recovered";
    }

    /// <summary>
    /// §7.2 同步事务（纯编排）：Planned → BackupCreated → Staged → Verified → CommitStarted → Committed → Finalized，
    /// CommitStarted 之后失败一律走 Revert（live 回到交换前），重启恢复由 RecoveryMatrix 负责。
    /// 本类只做编排 + 目录交换 + 摘要，不涉及 robocopy/EFS/native host（那些由调用方在 Staged 前完成）。
    /// </summary>
    public static class SyncTransaction
    {
        public const string StatusAborted = "aborted";
        public const string StatusCommitted = "committed";
        public const string StatusRecovered = "recovered";
        public const string StatusVerifyFailed = "verify-failed";

        /// <summary>
        /// 执行一次事务。backupDirectory 由调用方给出（已确保不存在或为空）；
        /// 任一阶段失败都不会留下半交换状态（CommitStarted 之后自动 Revert）。
        /// </summary>
        public static SyncTransactionResult Execute(CodexPaths paths, string backupDirectory, string backupId, IList<SyncTarget> targets)
        {
            if (paths == null) { throw new ArgumentNullException("paths"); }
            if (string.IsNullOrEmpty(backupDirectory)) { throw new ArgumentNullException("backupDirectory"); }
            if (targets == null || targets.Count == 0)
            {
                throw new ArgumentException("targets 不能为空", "targets");
            }

            SyncTransactionResult result = new SyncTransactionResult();
            JournalDto journal = new JournalDto();
            journal.OperationId = backupId;
            journal.BackupId = backupId;
            journal.Phase = JournalPhases.Planned;
            journal.Targets = new List<JournalTargetDto>();
            for (int i = 0; i < targets.Count; i++)
            {
                SyncTarget target = targets[i];
                JournalTargetDto entry = new JournalTargetDto();
                entry.Component = target.Component;
                entry.LivePath = target.LivePath;
                entry.StagePath = target.StagePath;
                journal.Targets.Add(entry);
            }

            BackupManifestDto manifest = new BackupManifestDto();
            manifest.BackupId = backupId;
            manifest.Kind = "sync";
            manifest.Status = BackupStates.Partial;
            manifest.CreatedUtc = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ", System.Globalization.CultureInfo.InvariantCulture);
            manifest.Components = new List<BackupComponentDto>();
            manifest.NativeHost = new List<BackupNativeHostDto>();

            bool commitStarted = false;
            try
            {
                // Planned ------------------------------------------------
                JournalStore.Write(paths, journal);

                // BackupCreated ------------------------------------------
                Directory.CreateDirectory(backupDirectory);
                for (int i = 0; i < targets.Count; i++)
                {
                    BackupComponentDto component = new BackupComponentDto();
                    component.Name = targets[i].Component;
                    component.LivePath = targets[i].LivePath;
                    component.BackupPath = targets[i].ExistedBefore ? targets[i].BackupPath : null;
                    component.ExistedBefore = targets[i].ExistedBefore;
                    component.State = BackupStates.Planned;
                    manifest.Components.Add(component);
                }
                BackupManifestStore.Write(backupDirectory, manifest);
                journal.Phase = JournalPhases.BackupCreated;
                JournalStore.Write(paths, journal);

                // Staged：调用方必须已经准备好 stage 树，这里只确认它存在 -------
                for (int i = 0; i < targets.Count; i++)
                {
                    if (string.IsNullOrEmpty(targets[i].StagePath) || !Directory.Exists(targets[i].StagePath))
                    {
                        result.Message = targets[i].Component + "：stage 不存在，事务在动 live 之前中止";
                        return Abort(paths, backupDirectory, manifest, result);
                    }
                }
                journal.Phase = JournalPhases.Staged;
                JournalStore.Write(paths, journal);

                // Verified：记录 old/new 摘要 ---------------------------
                for (int i = 0; i < targets.Count; i++)
                {
                    SyncTarget target = targets[i];
                    target.OldDigest = target.ExistedBefore ? TreeDigest.Compute(target.LivePath) : null;
                    target.NewDigest = TreeDigest.Compute(target.StagePath);
                    if (target.NewDigest == null)
                    {
                        result.Message = target.Component + "：stage 摘要计算失败";
                        return Abort(paths, backupDirectory, manifest, result);
                    }
                    if (target.ExistedBefore && target.OldDigest == null)
                    {
                        result.Message = target.Component + "：live 摘要计算失败";
                        return Abort(paths, backupDirectory, manifest, result);
                    }
                    manifest.Components[i].PreviousDigest = target.OldDigest;
                    manifest.Components[i].Digest = target.NewDigest;
                }
                BackupManifestStore.Write(backupDirectory, manifest);
                journal.Phase = JournalPhases.Verified;
                JournalStore.Write(paths, journal);

                // CommitStarted -----------------------------------------
                commitStarted = true;
                journal.Phase = JournalPhases.CommitStarted;
                JournalStore.Write(paths, journal);

                for (int i = 0; i < targets.Count; i++)
                {
                    SyncTarget target = targets[i];
                    if (target.ExistedBefore)
                    {
                        if (Directory.Exists(target.BackupPath)) { Directory.Delete(target.BackupPath, true); }
                        Directory.Move(target.LivePath, target.BackupPath);
                        journal.Targets[i].SwappedOut = true;
                        JournalStore.Write(paths, journal);
                        result.Add(target.Component + "：live → 备份");
                    }

                    Directory.Move(target.StagePath, target.LivePath);
                    journal.Targets[i].SwappedIn = true;
                    JournalStore.Write(paths, journal);
                    result.Add(target.Component + "：stage → live");
                    manifest.Components[i].State = target.ExistedBefore ? BackupStates.Moved : BackupStates.Created;
                }

                // Committed：复核 live 摘要 -----------------------------
                for (int i = 0; i < targets.Count; i++)
                {
                    ComponentDigestDto actual = TreeDigest.Compute(targets[i].LivePath);
                    if (!TreeDigest.Equal(actual, targets[i].NewDigest))
                    {
                        result.Message = targets[i].Component + "：交换后 live 摘要与 stage 不一致";
                        journal.Phase = JournalPhases.Committed;
                        Revert(paths, journal, targets, result);
                        manifest.Status = BackupStates.VerifyFailed;
                        BackupManifestStore.Write(backupDirectory, manifest);
                        result.Status = StatusVerifyFailed;
                        return result;
                    }
                }
                journal.Phase = JournalPhases.Committed;
                JournalStore.Write(paths, journal);

                // Finalized ---------------------------------------------
                manifest.Status = BackupStates.Complete;
                BackupManifestStore.Write(backupDirectory, manifest);
                string validateError = BackupManifestStore.Validate(manifest);
                if (validateError != null)
                {
                    throw new InvalidOperationException("backup.json 完成判据不通过：" + validateError);
                }
                journal.Phase = JournalPhases.Finalized;
                JournalStore.Write(paths, journal);
                JournalStore.Delete(paths);

                result.Status = StatusCommitted;
                result.Message = "事务完成";
                return result;
            }
            catch (Exception ex)
            {
                result.Message = ex.Message;
                if (commitStarted)
                {
                    Revert(paths, journal, targets, result);
                    manifest.Status = BackupStates.VerifyFailed;
                    try { BackupManifestStore.Write(backupDirectory, manifest); }
                    catch (Exception) { }
                    result.Status = StatusRecovered;
                }
                else
                {
                    try { JournalStore.Delete(paths); }
                    catch (Exception) { }
                    result.Status = StatusAborted;
                }
                return result;
            }
        }

        /// <summary>CommitStarted 之前的中止：live 未动，清理 journal 与备份骨架。</summary>
        private static SyncTransactionResult Abort(CodexPaths paths, string backupDirectory, BackupManifestDto manifest, SyncTransactionResult result)
        {
            try { JournalStore.Delete(paths); }
            catch (Exception) { }
            manifest.Status = BackupStates.Partial;
            try { BackupManifestStore.Write(backupDirectory, manifest); }
            catch (Exception) { }
            result.Status = StatusAborted;
            return result;
        }

        /// <summary>把已交换的组件退回交换前内容（逆序），逐组件校验摘要。</summary>
        private static void Revert(CodexPaths paths, JournalDto journal, IList<SyncTarget> targets, SyncTransactionResult result)
        {
            for (int i = targets.Count - 1; i >= 0; i--)
            {
                SyncTarget target = targets[i];
                try
                {
                    if (journal.Targets[i].SwappedIn && Directory.Exists(target.LivePath))
                    {
                        Directory.Delete(target.LivePath, true);
                    }
                    if (target.ExistedBefore && Directory.Exists(target.BackupPath) && !Directory.Exists(target.LivePath))
                    {
                        Directory.Move(target.BackupPath, target.LivePath);
                        ComponentDigestDto restored = TreeDigest.Compute(target.LivePath);
                        if (!TreeDigest.Equal(restored, target.OldDigest))
                        {
                            result.Add(target.Component + "：退回后摘要与 oldDigest 不一致");
                        }
                        else
                        {
                            result.Add(target.Component + "：已退回同步前内容");
                        }
                    }
                    journal.Targets[i].SwappedOut = false;
                    journal.Targets[i].SwappedIn = false;
                }
                catch (Exception ex)
                {
                    result.Add(target.Component + "：退回失败 —— " + ex.Message);
                }
            }
            journal.Phase = JournalPhases.Recovered;
            try { JournalStore.Write(paths, journal); }
            catch (Exception) { }
            try { JournalStore.Delete(paths); }
            catch (Exception) { }
        }
    }
}
