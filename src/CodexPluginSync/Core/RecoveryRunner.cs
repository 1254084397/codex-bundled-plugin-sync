using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;

namespace CodexPluginSync.Core
{
    public sealed class RecoveryOutcome
    {
        public bool Recovered;
        public bool NeedsAttention;
        public string Kind;
        public string Message;
        public List<string> Actions = new List<string>();
        public List<string> Rows = new List<string>();
    }

    /// <summary>
    /// §6.6 崩溃恢复的执行侧。三条硬规则：
    ///   1. journal 只是提示，文件系统是唯一事实源：判定输入是 live/backup/stage(safety) 的存在性 + 树摘要；
    ///   2. 矩阵每行只允许一个动作；任何一行不匹配即 needs-attention，保留 journal 与快照，绝不猜；
    ///   3. 执行前先做终态一致性预检（组件终态不一致时一个动作都不执行）。
    /// </summary>
    public static class RecoveryRunner
    {
        public const string PhaseRecovered = "recovered";

        public static RecoveryOutcome Run(CodexPaths paths, TextWriter log)
        {
            RecoveryOutcome outcome = new RecoveryOutcome();
            if (paths == null)
            {
                outcome.Recovered = true;
                return outcome;
            }

            JournalDto journal = JournalStore.Read(paths);
            if (journal == null)
            {
                outcome.Recovered = true;
                outcome.Message = "没有未收尾的 journal";
                return outcome;
            }

            List<JournalTargetDto> targets = journal.Targets;
            if (targets == null || targets.Count == 0)
            {
                outcome.NeedsAttention = true;
                outcome.Message = "journal（phase=" + journal.Phase + "）没有任何目标，状态无法判定";
                outcome.Actions.Add("保留 journal：" + paths.JournalPath);
                return outcome;
            }

            bool rollbackKind = IsRollback(journal);
            outcome.Kind = rollbackKind ? "rollback" : "sync";

            // 1) 预检：先把每个组件的判定算出来，一个动作都还没做 --------------
            List<RecoveryMatrix.Decision> decisions = new List<RecoveryMatrix.Decision>();
            for (int i = 0; i < targets.Count; i++)
            {
                RecoveryMatrix.TargetState state = BuildState(targets[i], rollbackKind);
                RecoveryMatrix.Decision decision = rollbackKind
                    ? RecoveryMatrix.ResolveRollback(state)
                    : RecoveryMatrix.ResolveSync(state);
                decisions.Add(decision);
                outcome.Rows.Add("row=" + decision.Row.ToString(CultureInfo.InvariantCulture) +
                    " component=" + state.Component + " action=" + decision.Action +
                    " outcome=" + decision.Outcome + " reason=" + decision.Reason);
            }

            string inconsistency = RecoveryMatrix.FindInconsistentOutcome(decisions);
            if (inconsistency != null)
            {
                outcome.NeedsAttention = true;
                outcome.Message = inconsistency;
                MarkAttention(paths, journal, outcome);
                return outcome;
            }

            // 2) 执行：逐组件只做一个动作 ------------------------------------
            for (int i = 0; i < targets.Count; i++)
            {
                RecoveryMatrix.Decision decision = decisions[i];
                JournalTargetDto target = targets[i];
                try
                {
                    Execute(decision, target, rollbackKind, outcome);
                }
                catch (Exception ex)
                {
                    outcome.NeedsAttention = true;
                    outcome.Message = target.Component + "：恢复动作失败 —— " + ex.Message;
                    MarkAttention(paths, journal, outcome);
                    return outcome;
                }
                if (decision.Action == RecoveryMatrix.ActionAttention)
                {
                    outcome.NeedsAttention = true;
                    outcome.Message = target.Component + "：" + decision.Reason;
                    MarkAttention(paths, journal, outcome);
                    return outcome;
                }
            }

            // 3) 收尾：全部落地才删 journal ----------------------------------
            journal.Phase = PhaseRecovered;
            JournalStore.Write(paths, journal);
            JournalStore.Delete(paths);
            outcome.Recovered = true;
            outcome.Message = "上次操作（" + outcome.Kind + "）已按 §6.6 矩阵收尾";
            return outcome;
        }

        private static void MarkAttention(CodexPaths paths, JournalDto journal, RecoveryOutcome outcome)
        {
            journal.Phase = JournalPhases.NeedsAttention;
            try
            {
                JournalStore.Write(paths, journal);
            }
            catch (Exception) { }
            outcome.Actions.Add("保留 journal 与快照作为现场：" + paths.JournalPath);
        }

        private static bool IsRollback(JournalDto journal)
        {
            if (journal.Phase != null && journal.Phase.StartsWith("rollback-", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
            if (journal.Targets == null)
            {
                return false;
            }
            for (int i = 0; i < journal.Targets.Count; i++)
            {
                if (!string.IsNullOrEmpty(journal.Targets[i].SafetyPath))
                {
                    return true;
                }
            }
            return false;
        }

        /// <summary>把"存在性 + 摘要"折算成矩阵输入；摘要不匹配即视为内容不可识别。</summary>
        private static RecoveryMatrix.TargetState BuildState(JournalTargetDto target, bool rollbackKind)
        {
            RecoveryMatrix.TargetState state = new RecoveryMatrix.TargetState();
            state.Component = target.Component;
            state.ExistedBefore = target.ExistedBefore;

            state.LiveExists = Directory.Exists(target.LivePath);
            if (state.LiveExists)
            {
                ComponentDigestDto live = TreeDigest.Compute(target.LivePath);
                state.LiveIsOld = TreeDigest.Equal(live, target.OldDigest);
                state.LiveIsNew = TreeDigest.Equal(live, target.NewDigest);
            }

            state.BackupExists = !string.IsNullOrEmpty(target.BackupPath) && Directory.Exists(target.BackupPath);
            if (state.BackupExists)
            {
                ComponentDigestDto backup = TreeDigest.Compute(target.BackupPath);
                state.BackupIsOld = TreeDigest.Equal(backup, target.OldDigest);
            }

            state.StageExists = !string.IsNullOrEmpty(target.StagePath) && Directory.Exists(target.StagePath);
            if (state.StageExists)
            {
                ComponentDigestDto stage = TreeDigest.Compute(target.StagePath);
                state.StageIsNew = TreeDigest.Equal(stage, target.NewDigest);
            }

            if (rollbackKind)
            {
                state.SafetyExists = !string.IsNullOrEmpty(target.SafetyPath) && Directory.Exists(target.SafetyPath);
                if (state.SafetyExists)
                {
                    ComponentDigestDto safety = TreeDigest.Compute(target.SafetyPath);
                    state.SafetyIsOld = TreeDigest.Equal(safety, target.OldDigest);
                }
            }

            return state;
        }

        private static void Execute(RecoveryMatrix.Decision decision, JournalTargetDto target, bool rollbackKind,
            RecoveryOutcome outcome)
        {
            string action = decision.Action;
            if (action == RecoveryMatrix.ActionAttention)
            {
                return;
            }
            if (action == RecoveryMatrix.ActionNone)
            {
                outcome.Actions.Add(target.Component + "：无需动作（" + decision.Reason + "）");
                return;
            }

            if (action == RecoveryMatrix.ActionDeleteStage)
            {
                if (Directory.Exists(target.StagePath))
                {
                    DeleteTreeSafe(target.StagePath);
                    outcome.Actions.Add(target.Component + "：删除残留 stage");
                }
                return;
            }

            if (action == RecoveryMatrix.ActionCompleteSwap)
            {
                if (Directory.Exists(target.LivePath))
                {
                    throw new InvalidOperationException("前滚前 live 必须为空");
                }
                Directory.Move(target.StagePath, target.LivePath);
                ComponentDigestDto live = TreeDigest.Compute(target.LivePath);
                if (!TreeDigest.Equal(live, target.NewDigest))
                {
                    throw new InvalidOperationException("前滚后摘要与 newDigest 不一致");
                }
                outcome.Actions.Add(target.Component + "：前滚完成（stage → live）");
                return;
            }

            if (action == RecoveryMatrix.ActionRestoreBackup)
            {
                RestoreInto(target.LivePath, target.BackupPath, target.OldDigest, false);
                outcome.Actions.Add(target.Component + "：已退回备份内容");
                return;
            }

            if (action == RecoveryMatrix.ActionRestoreSafety)
            {
                RestoreInto(target.LivePath, target.SafetyPath, target.OldDigest, true);
                outcome.Actions.Add(target.Component + "：已退回安全快照内容");
                return;
            }

            if (action == RecoveryMatrix.ActionRevertNew)
            {
                RestoreInto(target.LivePath, target.BackupPath, target.OldDigest, false);
                outcome.Actions.Add(target.Component + "：已退回备份内容（撤回新建）");
                return;
            }

            if (action == RecoveryMatrix.ActionDeleteLive)
            {
                if (Directory.Exists(target.LivePath))
                {
                    DeleteTreeSafe(target.LivePath);
                }
                outcome.Actions.Add(target.Component + "：本次新建的组件已移除");
                return;
            }

            throw new InvalidOperationException("未知恢复动作：" + action + "（rollback=" + rollbackKind + "）");
        }

        /// <summary>live → 指定来源内容；copy=true 时保留来源（安全快照必须保留可回滚）。</summary>
        private static void RestoreInto(string livePath, string sourcePath, ComponentDigestDto expectedDigest, bool copy)
        {
            if (string.IsNullOrEmpty(sourcePath) || !Directory.Exists(sourcePath))
            {
                throw new InvalidOperationException("来源不存在：" + sourcePath);
            }
            if (Directory.Exists(livePath))
            {
                DeleteTreeSafe(livePath);
            }
            if (copy)
            {
                Installer.CopyTreePreservingLinks(sourcePath, livePath);
            }
            else
            {
                Directory.Move(sourcePath, livePath);
            }
            ComponentDigestDto restored = TreeDigest.Compute(livePath);
            if (!TreeDigest.Equal(restored, expectedDigest))
            {
                throw new InvalidOperationException("恢复后摘要与 oldDigest 不一致：" + livePath);
            }
        }

        private static void DeleteTreeSafe(string path)
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
                Directory.Delete(path, false);
                return;
            }
            Directory.Delete(path, true);
        }
    }
}
